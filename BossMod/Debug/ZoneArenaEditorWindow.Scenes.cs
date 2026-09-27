using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod;

// scenes: named layer / node / event object states the loaded layout is resolved under; the replay timeline drives them too
public sealed partial class ZoneArenaEditorWindow
{
    private string _newSceneName = "";
    private int _renameScene = -1;
    private string _renameBuf = "";
    private bool _renameFocus;
    private bool _showTriggers = true;
    private bool _ghostInactive = true;
    private bool _showLinks = true;
    private ZoneSceneTimelineFile? _timeline;
    private string _timelinePath = "";
    private string _timelineStatus = "";
    private float _timelineT;
    private bool _seekPending;      // the scrub slider moved _timelineT; the scene state follows once the slider rests or is released
    private long _seekPendingSince;
    private bool _timelineEstaOnly = true;
    private bool _liveSeals = true; // seeking the replay sets the seal 'use' toggles from the scene state; off = the author's choices stay
    private Task<ZoneSceneTimelineFile>? _timelineTask;
    private CancellationTokenSource? _timelineCts;
    private bool _timelineTaskAddScenes;

    private ZoneScene? ActiveScene => _session?.ActiveScene;
    private string _waypointStatus = "";

    private void EnsureDefaultScene()
    {
        var session = _session!;
        if (session.Scenes.Count == 0)
        {
            session.Scenes.Add(new ZoneScene { Name = "default", Source = ZoneSceneSource.Layout, State = session.State.Clone() });
        }
        if (session.ActiveSceneIndex < 0 || session.ActiveSceneIndex >= session.Scenes.Count)
        {
            session.ActiveSceneIndex = 0;
        }
    }

    // resolve the active scene and push it through seals, adjacency and the result
    private void ApplyActiveScene(bool reseal = true)
    {
        var session = _session!;
        session.Resolve();
        if (reseal)
        {
            ReapplySceneToSession();
        }
        ++_recomputeGen; // an in-flight recompute saw the previous activity
        MarkResultDirty();
    }

    // seals are re-detected under the new activity; the author's seal choices (which are cut, which pair) survive when the seal still exists
    private void ReapplySceneToSession()
    {
        var session = _session!;
        var scene = _scene!;
        HashSet<ulong> off = [];
        for (var i = 0; i < session.Seals.Count; ++i)
        {
            var b = session.Seals[i].BoxIndex;
            if (!session.ActiveSeals.Contains(b))
            {
                off.Add(BoxPathId(b));
            }
        }
        var pairA = 0ul;
        var pairB = 0ul;
        var pairMarker = -1;
        if (session.PairIndex >= 0 && session.PairIndex < session.Pairs.Count)
        {
            var pair = session.Pairs[session.PairIndex];
            pairA = BoxPathId(session.Seals[pair.SealA].BoxIndex);
            pairB = pair.SealB >= 0 ? BoxPathId(session.Seals[pair.SealB].BoxIndex) : 0ul;
            pairMarker = pair.MarkerIndex;
        }
        session.DetectSeals();
        for (var i = 0; i < session.Seals.Count; ++i)
        {
            var b = session.Seals[i].BoxIndex;
            if (off.Contains(BoxPathId(b)))
            {
                session.ActiveSeals.Remove(b);
            }
        }
        if (pairA != 0)
        {
            for (var i = 0; i < session.Pairs.Count; ++i)
            {
                var pair = session.Pairs[i];
                var a = BoxPathId(session.Seals[pair.SealA].BoxIndex);
                var b = pair.SealB >= 0 ? BoxPathId(session.Seals[pair.SealB].BoxIndex) : 0ul;
                if (a == pairA && b == pairB && pair.MarkerIndex == pairMarker)
                {
                    session.PairIndex = i;
                    break;
                }
            }
        }
        if (!session.CentreValid)
        {
            session.EstimateCentre(null);
        }
    }

    private ulong BoxPathId(int box)
    {
        var scene = _scene!;
        var node = scene.Boxes[box].NodeIndex;
        return node >= 0 ? scene.Nodes[node].PathId : scene.Boxes[box].LayoutObjectId;
    }

    private void ActivateScene(int index)
    {
        FlushPendingSeek(); // a no-op from SeekTimeline itself
        var session = _session!;
        if (index < 0 || index >= session.Scenes.Count)
        {
            return;
        }
        session.ActivateScene(index, false);
        ApplyActiveScene();
    }

    private int AddScene(ZoneScene scene, bool activate)
    {
        var session = _session!;
        var baseName = scene.Name.Length > 0 ? scene.Name : "scene";
        var name = baseName;
        var n = 2;
        while (session.Scenes.Exists(s => s.Name == name))
        {
            name = $"{baseName} ({n++})";
        }
        scene.Name = name;
        session.Scenes.Add(scene);
        if (activate)
        {
            ActivateScene(session.Scenes.Count - 1);
        }
        return session.Scenes.Count - 1;
    }

    private void DeleteScene(int index)
    {
        FlushPendingSeek();
        var session = _session!;
        if (session.Scenes.Count <= 1 || index < 0 || index >= session.Scenes.Count)
        {
            return;
        }
        session.Scenes.RemoveAt(index);
        if (_renameScene == index)
        {
            _renameScene = -1;
        }
        else if (_renameScene > index)
        {
            --_renameScene;
        }
        if (session.ActiveSceneIndex >= session.Scenes.Count)
        {
            session.ActiveSceneIndex = session.Scenes.Count - 1;
        }
        else if (session.ActiveSceneIndex > index)
        {
            --session.ActiveSceneIndex;
        }
        ActivateScene(session.ActiveSceneIndex);
    }

    private void SetEObjStateInScene(int eobj, ushort? state)
    {
        _session!.SetEObjState(eobj, state);
        ApplyActiveScene();
    }

    private void SetNodeInScene(int node, bool? active)
    {
        _session!.SetNodeOverride(node, active);
        ApplyActiveScene();
    }

    // a derived scene: the current state with one object forced to a state (0 = collision on / sealed / closed, 7 = removed / open)
    private void ShowEObjDerived(int eobj, ushort state)
    {
        var session = _session!;
        var eo = session.Model.EventObjects[eobj];
        var what = state == 7 ? (eo.IsDoor ? "open" : eo.IsSealController ? "released" : "state 7") : eo.IsDoor ? "closed" : eo.IsSealController ? "sealed" : $"state {state}";
        AddScene(session.DeriveScene(eobj, state, $"{eo.Label} {what}"), true);
    }

    private static string SceneBadge(ZoneScene s) => s.Source switch
    {
        ZoneSceneSource.Layout => "layout",
        ZoneSceneSource.Replay => s.ReplayTime > 0 ? $"replay t={s.ReplayTime:f1}s" : "replay",
        ZoneSceneSource.Derived => "derived",
        _ => "manual",
    };

    private static string StateSummary(ZoneSceneState st) => $"{st.DisabledLayers.Count} layer(s) off, {st.NodeOverrides.Count} node override(s), {st.EObjStates.Count} EventState(s)";

    private void DrawScenesSection()
    {
        var session = _session!;
        EnsureDefaultScene();
        for (var i = 0; i < session.Scenes.Count; ++i)
        {
            var s = session.Scenes[i];
            using var id = ImRaii.PushId(i);
            var active = i == session.ActiveSceneIndex;
            if (_renameScene == i)
            {
                if (_renameFocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    _renameFocus = false;
                }
                ImGui.SetNextItemWidth(180f);
                var commit = ImGui.InputText("##rename", ref _renameBuf, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine();
                commit |= ImGui.SmallButton("ok");
                if (commit)
                {
                    s.Name = _renameBuf.Length > 0 ? _renameBuf : s.Name;
                    _renameScene = -1;
                }
            }
            else
            {
                if (ImGui.RadioButton($"{s.Name}##scene", active) && !active)
                {
                    ActivateScene(i);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{SceneBadge(s)}: {StateSummary(s.State)}, {s.State.EObjObjectStates.Count} object state(s){(s.Notes.Length > 0 ? $"\n{s.Notes}" : "")}{(s.ReplayFile.Length > 0 ? $"\n{s.ReplayFile}" : "")}");
                }
                ImGui.SameLine();
                ImGui.TextDisabled($"[{SceneBadge(s)}]");
                ImGui.SameLine();
                if (ImGui.SmallButton("ren"))
                {
                    _renameScene = i;
                    _renameBuf = s.Name;
                    _renameFocus = true;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("dup"))
                {
                    AddScene(s.Clone(s.Name), false);
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(session.Scenes.Count <= 1))
                {
                    if (ImGui.SmallButton("del"))
                    {
                        DeleteScene(i);
                        break;
                    }
                }
            }
        }
        ImGui.SetNextItemWidth(160f);
        ImGui.InputTextWithHint("##newscene", "new scene name", ref _newSceneName, 64);
        ImGui.SameLine();
        if (ImGui.SmallButton("+ from current"))
        {
            AddScene(new ZoneScene { Name = _newSceneName.Length > 0 ? _newSceneName : "scene", Source = ZoneSceneSource.Manual, State = session.State.Clone() }, true);
            _newSceneName = "";
        }
        Hint("Copy the active state (layers, node overrides, object states) into a new scene");
        ImGui.SameLine();
        if (ImGui.SmallButton("+ layout defaults"))
        {
            AddScene(new ZoneScene { Name = _newSceneName.Length > 0 ? _newSceneName : "layout", Source = ZoneSceneSource.Layout }, true);
            _newSceneName = "";
        }
        Hint("A scene with every layer on and no object state: what the loader shows before any toggle");
        var st = session.State;
        ImGui.TextDisabled($"active: {StateSummary(st)}; inactive {_scene!.Activity.MeshActive.Count(a => !a)} mesh(es) / {_scene.Activity.BoxActive.Count(a => !a)} box(es)");
        if (st.EObjStates.Count > 0 || st.NodeOverrides.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("clear states"))
            {
                st.EObjStates.Clear();
                st.EObjObjectStates.Clear();
                st.NodeOverrides.Clear();
                ActiveScene?.State.CopyFrom(st);
                ApplyActiveScene();
            }
            Hint("Drop every object state and node override of the active scene (layer toggles stay)");
        }
        DrawTimelineControls();
    }

    private void DrawTimelineControls()
    {
        ImGui.Separator();
        ImGui.TextDisabled("replay timeline");
        DrawReplayPicker();
        if (_timelineStatus.Length > 0)
        {
            ImGui.TextWrapped(_timelineStatus);
        }
        if (_timeline == null)
        {
            return;
        }
        var tl = _timeline;
        ImGui.TextDisabled($"{Path.GetFileName(tl.Replay)}: {tl.Events.Count} events over {tl.Duration:f0}s, {tl.Enemies.Count} enemies, {tl.Players.Count} players, {tl.Bosses.Count} bosses, {tl.Rules.Count} rules");
        ImGui.SetNextItemWidth(-160f);
        // what only reads the scrub time (tracks, party dots, floor pieces) follows the drag live; the scene state (resolve, seals, result) once the
        // slider rests or is released; the viewer (which replays from the start for a backwards move) only when the slider is released
        if (ImGui.SliderFloat("##scrub", ref _timelineT, 0f, (float)tl.Duration, "%.1f s"))
        {
            _seekPending = true;
            _seekPendingSince = Environment.TickCount64;
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            FlushPendingSeek();
            PushViewerTime();
        }
        else if (_seekPending && Environment.TickCount64 - _seekPendingSince >= 100)
        {
            FlushPendingSeek();
        }
        Hint("Scrub: the scene named 'replay scrub' takes the cumulative object states at this time (layer toggles of the active scene are kept)");
        ImGui.SameLine();
        if (ImGui.SmallButton("<"))
        {
            StepTimeline(-1);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(">"))
        {
            StepTimeline(1);
        }
        Hint("Previous / next object state change (ctrl+Left / ctrl+Right)");
        ImGui.SameLine();
        ImGui.Checkbox("controllers only", ref _timelineEstaOnly);
        Hint("Step only through changes of objects that toggle collision (seals, doors)");
        ImGui.SameLine();
        if (ImGui.Checkbox("live seals", ref _liveSeals) && _liveSeals)
        {
            SyncSealsToScene();
        }
        Hint("On: every seek (slider, steps, the replay viewer) sets the seals' 'use' toggles from the replay state at that time (collision on = cut, removed = not cut). Off: the toggles keep whatever you set and seeking leaves them alone");
        ImGui.SameLine();
        var scrub = _session!.Scenes.FindIndex(s => s.Name == "replay scrub");
        using (ImRaii.Disabled(scrub < 0))
        {
            if (ImGui.SmallButton("pin"))
            {
                FlushPendingSeek();
                var s = _session.Scenes[scrub];
                s.Name = $"t={_timelineT:f0}s";
                s.ReplayTime = _timelineT;
            }
        }
        Hint("Keep the scrubbed state as its own scene (the next scrub creates a new 'replay scrub')");
        DrawViewerSyncToggle();
        DrawPlayerJumpButtons();
        if (ImGui.SmallButton("map replay path"))
        {
            MapReplayPath();
        }
        Hint("Map every segment of the run (pulls to the first boss, the boss, pulls to the next, ...) from where the party walked: no ring, no centre; the triangles under the players are floor by evidence and a step between two triangles the geometry does not join (a bridge) is joined by force. One project per segment ('path N ...')");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60f);
        ImGui.InputFloat("corridor", ref _pathCorridor, 1f, 5f, "%.0f");
        Hint("How far from the walked path the floor may extend (yalms); walls and cuts still apply inside it");
        if (_waypointStatus.Length > 0)
        {
            ImGui.TextWrapped(_waypointStatus);
        }
    }

    // .json = harness output, .log = replay parsed on a worker; the join needs the loaded scene, so both finish on the UI thread
    private void StartTimelineImport(string path, bool addScenes)
    {
        if (_scene == null || _session == null)
        {
            return;
        }
        if (!File.Exists(path))
        {
            _timelineStatus = $"not found: {path}";
            return;
        }
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var file = JsonSerializer.Deserialize<ZoneSceneTimelineFile>(File.ReadAllText(path), Serialization.BuildSerializationOptions());
                if (file == null)
                {
                    _timelineStatus = "empty timeline file";
                    return;
                }
                if (file.Territory != _scene.TerritoryId)
                {
                    _timelineStatus = $"timeline is for territory {file.Territory}, loaded {_scene.TerritoryId}";
                    return;
                }
                file.Prepare();
                ApplyTimeline(file, path, addScenes);
            }
            catch (Exception ex)
            {
                _timelineStatus = $"import failed: {ex.Message}";
            }
            return;
        }
        BeginTimelineJob(path, addScenes, $"parsing {Path.GetFileName(path)}...", (scene, model, settings, cfc, ct) =>
        {
            var progress = 0f;
            var replay = ReplayParserLog.Parse(path, ref progress, ct);
            return ZoneSceneTimelineBuilder.Build(replay, scene, model, settings, cfc, ct);
        });
    }

    // a replay the replay manager already parsed: no second parse, only the join
    private void StartTimelineImport(Replay replay, string path, bool addScenes)
    {
        BeginTimelineJob(path, addScenes, $"joining {Path.GetFileName(path)}...", (scene, model, settings, cfc, ct) => ZoneSceneTimelineBuilder.Build(replay, scene, model, settings, cfc, ct));
    }

    // the worker job behind both imports: a previous one is cancelled, the loaded scene / model and the session's settings go with it
    private void BeginTimelineJob(string path, bool addScenes, string status, Func<ZoneCollisionScene, ZoneSceneModel, AutoMapSettings, uint, CancellationToken, ZoneSceneTimelineFile> job)
    {
        if (_scene == null || _session == null)
        {
            return;
        }
        _timelineTaskAddScenes = addScenes;
        _timelineCts?.Cancel();
        _timelineCts?.Dispose();
        _timelineCts = new();
        var ct = _timelineCts.Token;
        var scene = _scene;
        var model = _session.Model;
        var settings = _session.Settings.Clone();
        var cfc = _zones.Find(z => z.TerritoryId == scene.TerritoryId).CfcId;
        _timelineStatus = status;
        _timelinePath = path;
        _timelineTask = Task.Run(() => job(scene, model, settings, cfc, ct), ct);
    }

    private void PublishTimeline()
    {
        if (_timelineTask == null || !_timelineTask.IsCompleted)
        {
            return;
        }
        var task = _timelineTask;
        _timelineTask = null;
        if (!task.IsCompletedSuccessfully)
        {
            _timelineStatus = $"import failed: {task.Exception?.InnerException?.Message ?? task.Exception?.Message ?? "cancelled"}";
            return;
        }
        if (task.Result.Events.Count == 0)
        {
            _timelineStatus = "no events: not a BMR replay? run the harness `events --json` on it instead";
            return;
        }
        ApplyTimeline(task.Result, _timelinePath, _timelineTaskAddScenes);
    }

    private void ApplyTimeline(ZoneSceneTimelineFile file, string path, bool addScenes)
    {
        var session = _session!;
        var model = session.Model;
        _timeline = file;
        _timelinePath = path;
        _importedTimelines[path] = file;
        _timelineT = MathF.Min(_timelineT, (float)file.Duration);
        ResetReplayViewState();
        RefreshOpenViewer();
        // what the replay taught about the model's objects: the builder never touches the model (it runs on a worker while the canvas reads it),
        // so roles and warp landings land here; a landing an earlier replay confirmed is kept
        foreach (var (nodeId, role) in file.RoleChanges)
        {
            var i = model.FindEventObject(nodeId);
            if (i >= 0)
            {
                model.EventObjects[i].Role = role;
            }
        }
        var linksAdded = 0;
        foreach (var l in file.Links)
        {
            var i = model.FindEventObject(l.EObjNodeId);
            if (i < 0 || model.Links.Exists(x => x.EObj == i && x.Confidence >= 1f))
            {
                continue;
            }
            var eo = model.EventObjects[i];
            var pop = -1;
            if (l.PopNodeId.Length > 0)
            {
                var popId = ZoneSceneTimelineFile.ParseId(l.PopNodeId);
                pop = model.PopPoints.FindIndex(p => p.PathId == popId);
            }
            model.ConfirmLink(i, pop, new(l.X, l.Y, l.Z), file.Replay);
            if (eo.Role is ZoneObjectRole.None or ZoneObjectRole.Warp)
            {
                eo.Role = ZoneObjectRole.Warp;
            }
            ++linksAdded;
        }
        model.BuildStaticFlow();
        var rulesAdded = 0;
        foreach (var r in file.Rules)
        {
            var saved = SavedRule.From(r);
            if (!_rules.Exists(x => x.Kind == saved.Kind && x.TriggerOid == saved.TriggerOid && x.EffectOid == saved.EffectOid && x.EffectNodeId == saved.EffectNodeId))
            {
                _rules.Add(saved);
                ++rulesAdded;
            }
        }
        _timelineStatus = $"imported {Path.GetFileName(path)}: {file.Events.Count} events, {file.Enemies.Count} enemies, {linksAdded} warp landing(s) confirmed, {rulesAdded} rule(s) added";
        if (addScenes)
        {
            SeekTimeline(_timelineT);
        }
    }

    // the scene state catches up with a scrub that so far only moved the time; called before anything that reads the scene state for output
    private void FlushPendingSeek()
    {
        if (_seekPending)
        {
            SeekTimeline(_timelineT, false);
        }
    }

    // pushViewer = false for the slider: the viewer follows when it is released
    private void SeekTimeline(float t, bool pushViewer = true)
    {
        _seekPending = false;
        if (_timeline == null || _session == null)
        {
            return;
        }
        var session = _session;
        _timelineT = t;
        var state = _timeline.StateAt(t);
        var scrub = session.Scenes.FindIndex(s => s.Name == "replay scrub");
        if (scrub < 0)
        {
            session.Scenes.Add(new ZoneScene { Name = "replay scrub", Source = ZoneSceneSource.Replay, ReplayFile = _timeline.Replay });
            scrub = session.Scenes.Count - 1;
        }
        var s = session.Scenes[scrub];
        s.ReplayTime = t;
        // keep the layer toggles of what the author was looking at, take every object state from the replay
        var layers = ActiveScene?.State.DisabledLayers ?? session.State.DisabledLayers;
        state.DisabledLayers.UnionWith(layers);
        s.State.CopyFrom(state);
        ActivateScene(scrub);
        if (_liveSeals)
        {
            SyncSealsToScene();
        }
        if (pushViewer)
        {
            PushViewerTime();
        }
    }

    // the seal 'use' toggles follow the resolved scene: a seal whose collision is on is cut, one whose collision is removed is not
    private void SyncSealsToScene()
    {
        if (_session == null || _scene == null)
        {
            return;
        }
        var changed = false;
        foreach (var seal in _session.Seals)
        {
            var on = _scene.IsBoxEnabled(seal.BoxIndex);
            changed |= on ? _session.ActiveSeals.Add(seal.BoxIndex) : _session.ActiveSeals.Remove(seal.BoxIndex);
        }
        if (changed)
        {
            MarkResultDirty();
        }
    }

    private void StepTimeline(int dir)
    {
        if (_timeline == null || _session == null)
        {
            return;
        }
        var model = _session.Model;
        bool Wanted(ZoneTimelineEvent e) => !_timelineEstaOnly || e.Eobj >= 0 && e.Eobj < model.EventObjects.Count && model.EventObjects[e.Eobj].ControlsCollision;
        var target = dir > 0 ? _timeline.NextStateChange(_timelineT + 0.01, Wanted) : dir < 0 ? _timeline.PreviousStateChange(_timelineT - 0.01, Wanted) : null;
        if (target != null)
        {
            SeekTimeline((float)target.T + 0.001f);
        }
    }

    // one line above the canvas: the scene combo, replay stepping and the object-drawing toggles
    private void DrawCanvasToolbar()
    {
        var session = _session;
        if (session == null)
        {
            return;
        }
        EnsureDefaultScene();
        ImGui.SetNextItemWidth(220f);
        var active = session.ActiveScene;
        using (var combo = ImRaii.Combo("##scenecombo", active != null ? $"{active.Name} [{SceneBadge(active)}]" : "(no scene)"))
        {
            if (combo)
            {
                for (var i = 0; i < session.Scenes.Count; ++i)
                {
                    var s = session.Scenes[i];
                    if (ImGui.Selectable($"{s.Name} [{SceneBadge(s)}]##sc{i}", i == session.ActiveSceneIndex))
                    {
                        ActivateScene(i);
                    }
                }
            }
        }
        Hint("Active scene: which layers, nodes and object states the canvas and the auto-map see");
        ImGui.SameLine();
        using (ImRaii.Disabled(_timeline == null))
        {
            if (ImGui.SmallButton("<##tl"))
            {
                StepTimeline(-1);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(">##tl"))
            {
                StepTimeline(1);
            }
        }
        Hint("Previous / next replay state change (needs an imported timeline; ctrl+Left / ctrl+Right)");
        ImGui.SameLine();
        ImGui.Checkbox("ghost inactive", ref _ghostInactive);
        Hint("Draw colliders whose instance exists but has its collision removed in the active scene as dashed outlines instead of hiding them");
        ImGui.SameLine();
        ImGui.Checkbox("triggers", ref _showTriggers);
        Hint("Draw door / event / map range volumes");
        ImGui.SameLine();
        ImGui.Checkbox("links", ref _showLinks);
        Hint("Draw event object -> bound instance lines and exit -> pop point arrows");
        ImGui.SameLine();
        ImGui.Checkbox("areas", ref _showAreas);
        Hint("At low zoom, fill the map ranges with a colour per area and label them with their place names");
        ImGui.SameLine();
        ImGui.Checkbox("floor pieces", ref _showFloorPieces);
        Hint("Floor that can be gone during a boss fight: the triangles under each platform (an event object binding collision in the boss area) or section (a map range nested in the boss area) are filled in the group's colour; with a replay, pieces gone at the scrub time fade and draw dashed");
        DrawReplayOverlayToggles();
        ImGui.SameLine();
        ImGui.TextDisabled($"tool: {_tool}");
    }
}
