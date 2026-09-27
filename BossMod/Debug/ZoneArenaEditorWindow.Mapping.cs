using Dalamud.Bindings.ImGui;
using System.Threading;

namespace BossMod;

// batch mapping: "map at (centre, scene) -> project" as one step, run over a list of targets on a worker while the UI shows progress;
// used by "map every waypoint", the per-seal sealed/released pair and project loads
public sealed partial class ZoneArenaEditorWindow
{
    private readonly record struct MapTarget(string Name, Vector3 At, ZoneSceneState? State, bool ChoosePair = true, ZonePathSegment? Path = null);
    // what the author was looking at before a batch; seals, the pair and ignored boxes by node path id, since the batch re-detects seals under other states
    private readonly record struct MapSnapshot(Vector3 Centre, bool Valid, (ulong a, ulong b, int marker) Pair, HashSet<int> Selected, HashSet<int> Auto, HashSet<int> Boxes, HashSet<ulong> ActiveSeals, HashSet<ulong> Ignored, ZoneSceneState State);
    private float _pathCorridor = 12f;

    private Task<(int done, int empty)>? _mapTask;
    private CancellationTokenSource? _mapCts;
    private string _mapProgress = "";
    private int _mapIndex, _mapCount;
    private Action? _mapDone; // runs on the UI thread after the batch (naming, rule fill-in)
    private MapSnapshot? _mapSaved;
    private bool _terrainAlongFlow = true;

    private bool MappingBusy => _mapTask != null;

    // terrain tiles were appended to the scene: mesh modes for the new meshes, the picker's grids refreshed, the state re-resolved
    private void TerrainAppended()
    {
        var scene = _scene!;
        while (_meshModes.Count < scene.Meshes.Count)
        {
            _meshModes.Add(MeshMode.Auto);
        }
        RefreshPicker();
        _session!.Resolve();
    }

    // the current centre / selection state auto-mapped, recomputed and saved as a project; the polygon count, 0 when nothing was selected
    private int AutoMapToProject(string name)
    {
        var session = _session!;
        session.AutoMap();
        if (session.Selected.Count == 0)
        {
            return 0;
        }
        var polys = session.Recompute(ManualPaths(false), ManualPaths(true), ManualYSource());
        _pipeline.Raw = polys;
        _pipeline.KeepStatus = session.KeepStatus;
        _pipeline.AnchorXZ = new(session.Centre.X, session.Centre.Z);
        _pipeline.RebuildSimplified();
        SaveProject(name);
        return polys.Count;
    }

    // one target: terrain around it, centre on the floor there, optional scene state, pair, auto-map, recompute, save as a project
    private int MapAt(MapTarget t, CancellationToken ct)
    {
        var session = _session!;
        var scene = _scene!;
        if (t.Path != null)
        {
            return MapPath(t, ct);
        }
        if (_terrainRadius > 0f && ZoneCollisionLoader.EnsureTerrainLoaded(scene, _fileSource, new(t.At.X, t.At.Z), _terrainRadius) > 0)
        {
            TerrainAppended();
        }
        if (t.State != null)
        {
            session.State.CopyFrom(t.State);
            session.Resolve();
            ReapplySceneToSession();
        }
        ct.ThrowIfCancellationRequested();
        session.SetCentre(new(t.At.X, FloorYAt(t.At), t.At.Z));
        if (t.ChoosePair)
        {
            session.ChooseDefaultPair(session.Centre);
        }
        return AutoMapToProject(t.Name);
    }

    // path mode: the floor is what the party walked over in this run segment; terrain along the samples, no ring, no centre, no seal blocking
    private int MapPath(MapTarget t, CancellationToken ct)
    {
        var session = _session!;
        var scene = _scene!;
        var seg = t.Path!;
        var added = 0;
        Vector2? last = null;
        foreach (var s in seg.Samples)
        {
            var xz = new Vector2(s.X, s.Z);
            if (last is { } l && (l - xz).Length() < 40f)
            {
                continue;
            }
            last = xz;
            added += ZoneCollisionLoader.EnsureTerrainLoaded(scene, _fileSource, xz, MathF.Max(_terrainRadius, 60f));
        }
        if (added > 0)
        {
            TerrainAppended();
        }
        ct.ThrowIfCancellationRequested();
        // the walked triangles and steps are picked against the scene as loaded now (terrain may have arrived since the segments were built)
        var built = ZonePathMapping.BuildSegment(scene, _picker!, _timeline!, seg.Name, seg.T0, seg.T1);
        session.SetPath(built.Samples, _pathCorridor, built.ForcedTriangles, built.ForcedLinks);
        try
        {
            return AutoMapToProject(t.Name);
        }
        finally
        {
            session.ClearPath();
        }
    }

    // every run segment of the imported replay (pulls to the first boss, the boss, pulls to the next, ...) mapped from the party's path
    private void MapReplayPath()
    {
        if (_timeline == null || _scene == null || _picker == null)
        {
            _waypointStatus = "import a replay first";
            return;
        }
        var segments = ZonePathMapping.Build(_scene, _picker, _timeline);
        if (segments.Count == 0)
        {
            _waypointStatus = "no player samples in the replay";
            return;
        }
        List<MapTarget> targets = [];
        for (var i = 0; i < segments.Count; ++i)
        {
            targets.Add(new($"path {i} {segments[i].Name}", segments[i].Samples[0], null, false, segments[i]));
        }
        StartMapping(targets);
    }

    // the batch runs on a worker; the sidebar and canvas are replaced by a progress line meanwhile (the session is being mutated)
    private void StartMapping(List<MapTarget> targets, Action? done = null)
    {
        FlushPendingSeek(); // the batch maps the scene state, not a scrub still settling
        if (_session == null || _scene == null || targets.Count == 0 || _mapTask != null)
        {
            return;
        }
        if (_recomputeTask != null || _autoMapTask != null)
        {
            // the batch appends terrain and re-detects seals under the workers' feet
            _waypointStatus = "a recompute / auto-map is still running; start the mapping again when it is done";
            return;
        }
        var session = _session;
        _mapSaved = new(session.Centre, session.CentreValid, PairKey(session.PairIndex), [.. session.Selected], [.. session.LastAutoResult], [.. session.SelectedFloorBoxes], [.. session.ActiveSeals.Select(BoxPathId)], [.. session.IgnoredBoxes.Select(BoxPathId)], session.State.Clone());
        _mapDone = done;
        _mapCount = targets.Count;
        _mapIndex = 0;
        _mapCts?.Cancel();
        _mapCts?.Dispose();
        _mapCts = new();
        var ct = _mapCts.Token;
        _mapTask = Task.Run(() =>
        {
            var doneCount = 0;
            var empty = 0;
            for (var i = 0; i < targets.Count; ++i)
            {
                ct.ThrowIfCancellationRequested();
                _mapIndex = i;
                _mapProgress = targets[i].Name;
                if (MapAt(targets[i], ct) > 0)
                {
                    ++doneCount;
                }
                else
                {
                    ++empty;
                }
            }
            return (doneCount, empty);
        }, ct);
    }

    private void PublishMapping()
    {
        if (_mapTask == null || !_mapTask.IsCompleted)
        {
            return;
        }
        var task = _mapTask;
        _mapTask = null;
        var session = _session;
        if (session != null && _mapSaved is { } saved)
        {
            // back to what the author was looking at
            session.ClearPath();
            session.State.CopyFrom(saved.State);
            session.Resolve();
            ReapplySceneToSession();
            session.SetCentre(saved.Centre);
            session.CentreValid = saved.Valid;
            session.PairIndex = FindPair(saved.Pair);
            session.ActiveSeals.Clear();
            foreach (var seal in session.Seals)
            {
                if (saved.ActiveSeals.Contains(BoxPathId(seal.BoxIndex)))
                {
                    session.ActiveSeals.Add(seal.BoxIndex);
                }
            }
            session.IgnoredBoxes.Clear();
            for (var b = 0; saved.Ignored.Count > 0 && b < _scene!.Boxes.Count; ++b)
            {
                if (saved.Ignored.Contains(BoxPathId(b)))
                {
                    session.IgnoredBoxes.Add(b);
                }
            }
            session.Selected.Clear();
            session.Selected.UnionWith(saved.Selected);
            session.LastAutoResult.Clear();
            session.LastAutoResult.UnionWith(saved.Auto);
            session.SelectedFloorBoxes.Clear();
            session.SelectedFloorBoxes.UnionWith(saved.Boxes);
            _selection.ClearHistory();
            ++_recomputeGen;
            MarkResultDirty();
            _resultDirtySince = 0;
        }
        _mapSaved = null;
        if (task.IsCompletedSuccessfully)
        {
            var (done, empty) = task.Result;
            _waypointStatus = $"mapped {done} of {_mapCount} target(s) into projects{(empty > 0 ? $", {empty} with no floor at the point" : "")}; load one from the Project section";
            _mapDone?.Invoke();
        }
        else
        {
            _waypointStatus = $"mapping stopped: {task.Exception?.InnerException?.Message ?? task.Exception?.Message ?? "cancelled"}";
        }
        _mapDone = null;
    }

    // a pair by the path ids of its seals and its marker, so it can be found again after the seals were re-detected
    private (ulong a, ulong b, int marker) PairKey(int index)
    {
        var session = _session!;
        if (index < 0 || index >= session.Pairs.Count)
        {
            return (0ul, 0ul, -1);
        }
        var pair = session.Pairs[index];
        return (BoxPathId(session.Seals[pair.SealA].BoxIndex), pair.SealB >= 0 ? BoxPathId(session.Seals[pair.SealB].BoxIndex) : 0ul, pair.MarkerIndex);
    }

    private int FindPair((ulong a, ulong b, int marker) key)
    {
        if (key.a == 0)
        {
            return -1;
        }
        var count = _session!.Pairs.Count;
        for (var i = 0; i < count; ++i)
        {
            if (PairKey(i) == key)
            {
                return i;
            }
        }
        return -1;
    }

    // the floor height under p: the stacked surface nearest p.Y, p.Y itself when no triangle is there
    private float FloorYAt(Vector3 p)
    {
        var top = _picker!.PickTriangleNearY(new(p.X, p.Z), p.Y, _scratchHits);
        return top >= 0 ? _scene!.Triangles[top].YAt(p.X, p.Z) : p.Y;
    }

    private static bool WithinXZ(in Vector3 a, in Vector3 b, float dist) => (new Vector2(a.X, a.Z) - new Vector2(b.X, b.Z)).Length() < dist;

    private void DrawMappingProgress()
    {
        ImGui.TextUnformatted($"mapping {_mapIndex + 1}/{_mapCount}: {_mapProgress}");
        ImGui.SameLine();
        if (ImGui.SmallButton("cancel"))
        {
            _mapCts?.Cancel();
        }
    }

    // the run's waypoints: bosses of the imported replay (named after them), the start, every warp landing, every player pop point, every sealed room
    private List<MapTarget> WaypointTargets()
    {
        var session = _session!;
        var scene = _scene!;
        var model = session.Model;
        List<MapTarget> targets = [];
        void Add(string name, Vector3 at, float dedupe = 5f)
        {
            if (!targets.Exists(w => WithinXZ(w.At, at, dedupe)))
            {
                targets.Add(new(name, at, null));
            }
        }
        string AreaNameAt(Vector3 p) => AreaName(model.AreaAt(p), "area");
        // a boss room is mapped from where the boss stood at the pull: the seal pair around that point is the room's
        if (_timeline != null)
        {
            foreach (var b in _timeline.Bosses)
            {
                if (ZoneSceneTimelineFile.SampleAt(b.Samples, b.Pull) is { } at)
                {
                    Add($"boss {b.Name}", at);
                }
            }
        }
        var entrance = model.EventObjects.Find(e => e.Role == ZoneObjectRole.Entrance);
        if (entrance != null)
        {
            Add($"start {AreaNameAt(entrance.ActorPosition)}", entrance.ActorPosition);
        }
        foreach (var l in model.Links.OrderBy(l => l.Confidence < 0.5f ? 1 : 0))
        {
            Add($"landing {AreaNameAt(l.Landing)}", l.Landing);
        }
        foreach (var p in model.PopPoints.Where(p => p.PopType == LgbPopType.Pc))
        {
            Add($"pop {AreaNameAt(p.Position)}", p.Position);
        }
        for (var i = 0; i < session.Pairs.Count; ++i)
        {
            var pair = session.Pairs[i];
            if (pair.IsSingle)
            {
                continue;
            }
            var est = ArenaAutoMapper.EstimateCentre(pair, session.Seals, scene.Markers, session.Settings, null);
            if (!est.UsedFallback)
            {
                Add($"room {AreaNameAt(est.Centre)}", est.Centre, 30f);
            }
        }
        for (var i = 0; i < targets.Count; ++i)
        {
            targets[i] = targets[i] with { Name = $"wp {i} {targets[i].Name}" };
        }
        return targets;
    }

    private void MapWaypoints()
    {
        var targets = WaypointTargets();
        if (targets.Count == 0)
        {
            _waypointStatus = "no waypoints: no entrance, pop points, bosses or seal pairs in this layout";
            return;
        }
        StartMapping(targets);
    }

    // every seal controller's room mapped twice, sealed (EventState 0) and released (7), saved as 'sealed <obj>' / 'released <obj>'; the seal
    // rules of that object then point at both, so the arena-swap snippet needs no project picking
    private void MapSealStates()
    {
        var session = _session!;
        var scene = _scene!;
        var model = session.Model;
        List<MapTarget> targets = [];
        List<(ulong pathId, string sealed_, string released)> named = [];
        List<string> skipped = []; // controllers whose seal box the detector did not pass (there is no room position to map from)
        foreach (var eo in model.EventObjects.Where(e => e.IsSealController))
        {
            var tag = eo.Name.Length > 0 ? eo.Name : $"0x{eo.BaseId:X} key {eo.InstanceKey:X}";
            var pair = session.Pairs.FindIndex(p => !p.IsSingle && (Array.IndexOf(eo.SealBoxes, session.Seals[p.SealA].BoxIndex) >= 0 || p.SealB >= 0 && Array.IndexOf(eo.SealBoxes, session.Seals[p.SealB].BoxIndex) >= 0));
            Vector3 at;
            if (pair >= 0)
            {
                var est = ArenaAutoMapper.EstimateCentre(session.Pairs[pair], session.Seals, scene.Markers, session.Settings, null);
                at = est.Centre;
            }
            else
            {
                // one seal only: the room is on the side of the seal away from the entrance, a few yalms in
                var sealIndex = session.Seals.FindIndex(s => Array.IndexOf(eo.SealBoxes, s.BoxIndex) >= 0);
                if (sealIndex < 0)
                {
                    skipped.Add(tag);
                    continue;
                }
                var seal = session.Seals[sealIndex];
                var entrance = model.EventObjects.Find(e => e.Role == ZoneObjectRole.Entrance);
                var away = entrance != null ? Vector3.Normalize(seal.Center - entrance.ActorPosition) : seal.ThinAxisWorld;
                at = seal.Center + new Vector3(away.X, 0f, away.Z) * 8f;
            }
            var sealedState = session.State.Clone();
            sealedState.EObjStates[eo.PathId] = 0;
            var releasedState = session.State.Clone();
            releasedState.EObjStates[eo.PathId] = 7;
            targets.Add(new($"sealed {tag}", at, sealedState));
            targets.Add(new($"released {tag}", at, releasedState));
            named.Add((eo.PathId, $"sealed {tag}", $"released {tag}"));
        }
        var skippedNote = skipped.Count > 0 ? $"; skipped {skipped.Count} controller(s) without a detected seal box: {string.Join(", ", skipped)}" : "";
        if (targets.Count == 0)
        {
            _waypointStatus = $"no seal controllers with a detected seal box in this layout{skippedNote}";
            return;
        }
        StartMapping(targets, () =>
        {
            var filled = 0;
            foreach (var r in _rules)
            {
                if (r.Kind is not (ZoneRuleKind.SealOnPull or ZoneRuleKind.SealRelease) || r.EffectNodeId.Length == 0)
                {
                    continue;
                }
                var id = ZoneSceneTimelineFile.ParseId(r.EffectNodeId);
                var n = named.Find(x => x.pathId == id);
                if (n.sealed_ == null)
                {
                    continue;
                }
                // before the pull the seal is gone (7), the pull puts it up (0); the kill takes it down again
                r.ArenaPre = r.Kind == ZoneRuleKind.SealOnPull ? n.released : n.sealed_;
                r.ArenaPost = r.Kind == ZoneRuleKind.SealOnPull ? n.sealed_ : n.released;
                ++filled;
            }
            _waypointStatus += $"; {filled} seal rule(s) now point at their sealed / released projects{skippedNote}";
        });
    }

    // terrain tiles around every waypoint of the flow, so the whole run is walkable in the canvas without moving the centre first
    private int PreloadTerrainAlongFlow(ZoneCollisionScene scene)
    {
        if (!_terrainAlongFlow || _terrainRadius <= 0f || _session == null)
        {
            return 0;
        }
        var model = _session.Model;
        List<Vector3> points = [];
        void Add(Vector3 p)
        {
            if (points.Count < 16 && !points.Exists(q => WithinXZ(q, p, _terrainRadius * 0.5f)))
            {
                points.Add(p);
            }
        }
        foreach (var eo in model.EventObjects.Where(e => e.Role is ZoneObjectRole.Entrance or ZoneObjectRole.Exit or ZoneObjectRole.Warp))
        {
            Add(eo.ActorPosition);
        }
        foreach (var l in model.Links)
        {
            Add(l.Landing);
        }
        foreach (var p in model.PopPoints.Where(p => p.PopType == LgbPopType.Pc))
        {
            Add(p.Position);
        }
        var added = 0;
        foreach (var p in points)
        {
            added += ZoneCollisionLoader.EnsureTerrainLoaded(scene, _fileSource, new(p.X, p.Z), _terrainRadius);
        }
        if (added > 0)
        {
            TerrainAppended();
        }
        return added;
    }
}
