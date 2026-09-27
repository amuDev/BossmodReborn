using Clipper2Lib;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.IO;

namespace BossMod;

// the replay side of the editor: picking a replay for the loaded territory (open in the replay manager or on disk), syncing with the
// replay viewer, the player path / party / boss overlays and the position coverage check of the result polygon
public sealed partial class ZoneArenaEditorWindow
{
    private readonly record struct ReplayChoice(string Label, string Path, Replay? Loaded);

    private readonly ReplayManagementWindow? _replayWindow;
    private readonly List<ReplayChoice> _replayChoices = [];
    private int _replayChoice = -1;
    private bool _replayShowAll;
    private string _replayExtraFolder = "";
    private uint _replayChoicesTerritory;
    private int _replayChoicesLoadedCount = -1;
    private (string path, Replay replay, DateTime? time, uint territory)? _pendingReplaySync;
    private (Replay replay, DateTime time)? _openViewer; // the viewer showing the imported replay, looked up once per frame
    private int _lastJumpedTrack = -1;                    // the P button pressed last: what "follow" follows when it turns on
    private string _moduleArenaText = "";
    private BossModule? _moduleArenaFor;
    private ArenaBounds? _moduleArenaBounds;
    private string[] _playerLabels = [];
    private ZoneSceneTimelineFile? _playerLabelsFor;
    private string _coverageText = "";
    private bool _coverageTextRedDots;

    private bool _showPlayerPath = true;
    private bool _showBosses = true;
    private bool _showCoverage = true;
    private bool _showSpawns = true;
    private bool _showModuleArena = true;
    private int _followPlayer = -1; // track index the canvas keeps centred on while the scrub moves
    private float _followLastT = float.NaN;
    private readonly Dictionary<string, ZoneSceneTimelineFile> _importedTimelines = []; // every replay imported this session, by path: spawn locations cluster across them
    private readonly List<SpawnCluster> _spawnClusters = [];
    private int _spawnClustersFor = -1;

    private sealed class SpawnCluster
    {
        public Vector3 Centre;
        public int Count;
        public int Replays;
        public ZoneEnemyClass Class;
        public string Names = "";
        public uint Marker;
    }
    private bool _syncViewerTime = true;
    private DateTime _lastViewerTime;
    private bool _pullingViewerTime;
    private bool _pendingViewerSeek; // a viewer opened from here on a file still parsing: seek it to the scrub once it is up

    private const uint PathColor = 0xFFD00062; // purple (ABGR)
    private const double ViewerSyncStep = 0.1; // seconds the viewer has to move before the editor follows

    private int _coverageGen = -1;
    private ZoneSceneTimelineFile? _coverageTimeline;
    private int _coverageEnabledKey;
    private int _coverageInside, _coverageCandidates, _coverageTotal;
    private readonly List<Vector3> _coverageOutliers = [];

    // the replay open in a replay viewer drives the editor: load its territory and import it (called once when the editor is created from the
    // debug window, and from the "sync viewer" button); with no viewer open there is nothing to follow
    public void SyncWithReplayViewer()
    {
        if (_replayWindow is not { IsOpen: true } w)
        {
            return;
        }
        string? path = null;
        Replay? replay = null;
        DateTime? time = null;
        foreach (var r in w.Manager.LoadedReplays)
        {
            if (r.windowOpen)
            {
                (path, replay, _, time) = r;
                break;
            }
        }
        if (path == null || replay == null)
        {
            return;
        }
        var territory = ReplayTerritory(replay);
        if (territory == 0)
        {
            return;
        }
        if (_timeline != null && _timelinePath == path && _loadedTerritory == territory)
        {
            if (time is { } t)
            {
                _lastViewerTime = t;
                SeekTimeline((float)(t - replay.Ops[0].Timestamp).TotalSeconds);
            }
            return;
        }
        _pendingReplaySync = (path, replay, time, territory);
        if (_loadedTerritory != territory || _scene == null)
        {
            LoadTerritory(territory);
        }
        else
        {
            RunPendingReplaySync();
        }
    }

    // after a load: the sync is for one territory; a load of another one (or a failed load followed by a different one) drops it
    private void RunPendingReplaySync()
    {
        if (_pendingReplaySync is not { } sync || _scene == null || _session == null)
        {
            return;
        }
        _pendingReplaySync = null;
        if (_scene.TerritoryId != sync.territory)
        {
            return;
        }
        RefreshReplayChoices();
        _replayChoice = _replayChoices.FindIndex(c => c.Path == sync.path);
        StartTimelineImport(sync.replay, sync.path, true);
        if (sync.time is { } t && sync.replay.Ops.Count > 0)
        {
            _timelineT = (float)(t - sync.replay.Ops[0].Timestamp).TotalSeconds;
        }
    }

    // the replay viewer showing the imported replay, if it is open; looked up at the start of the frame (and after an import), read from
    // the field by everything else in it
    private void RefreshOpenViewer()
    {
        _openViewer = null;
        if (_timeline == null || _timelinePath.Length == 0 || _replayWindow is not { IsOpen: true } w)
        {
            return;
        }
        foreach (var (path, replay, windowOpen, time) in w.Manager.LoadedReplays)
        {
            if (windowOpen && path == _timelinePath && time is { } t && replay.Ops.Count > 0)
            {
                _openViewer = (replay, t);
                return;
            }
        }
    }

    private bool AnyViewerOpen()
    {
        if (_replayWindow is not { IsOpen: true } w)
        {
            return false;
        }
        foreach (var r in w.Manager.LoadedReplays)
        {
            if (r.windowOpen)
            {
                return true;
            }
        }
        return false;
    }

    // what the replay view keeps between frames: follow, viewer sync and jump state; a new timeline starts over
    private void ResetReplayViewState()
    {
        _followPlayer = -1;
        _followLastT = float.NaN;
        _lastJumpedTrack = -1;
        _lastViewerTime = default;
        _pendingViewerSeek = false;
    }

    // one timeline for both tools: the viewer moved -> the editor scrubs to the same time (once per frame, throttled to 0.1 s)
    private void PullViewerTime()
    {
        RefreshOpenViewer();
        if (_pendingViewerSeek && _openViewer is { } opened)
        {
            // the explicit "open in viewer at this time": the viewer goes to the scrub whether or not the two are kept in step
            _pendingViewerSeek = false;
            var seek = opened.replay.Ops[0].Timestamp.AddSeconds(_timelineT);
            _lastViewerTime = seek;
            _replayWindow!.Manager.SetReplayTime(_timelinePath, seek);
            return;
        }
        if (!_syncViewerTime || _openViewer is not { } v || v.time == _lastViewerTime)
        {
            return;
        }
        _lastViewerTime = v.time;
        var t = (float)(v.time - v.replay.Ops[0].Timestamp).TotalSeconds;
        if (Math.Abs(t - _timelineT) < ViewerSyncStep)
        {
            return;
        }
        _pullingViewerTime = true;
        SeekTimeline(t);
        _pullingViewerTime = false;
    }

    // the editor scrubbed -> the viewer moves to the same time
    private void PushViewerTime()
    {
        if (!_syncViewerTime || _pullingViewerTime || _openViewer is not { } v)
        {
            return;
        }
        var t = v.replay.Ops[0].Timestamp.AddSeconds(_timelineT);
        if (t == v.time)
        {
            return;
        }
        _lastViewerTime = t;
        _replayWindow!.Manager.SetReplayTime(_timelinePath, t);
    }

    private void DrawViewerSyncToggle()
    {
        if (_openViewer == null)
        {
            return;
        }
        ImGui.SameLine();
        ImGui.Checkbox("sync viewer time", ref _syncViewerTime);
        Hint("Keep this scrub and the replay viewer's time in step both ways (the viewer's play/seek moves this scene, this slider moves the viewer)");
    }

    // the territory a replay was recorded in: the zone its players carry while in a duty, else the first encounter's zone
    private static uint ReplayTerritory(Replay replay)
    {
        var zones = replay.Participants.Where(p => p.Type == ActorType.Player && p.CFCID != 0 && p.ZoneID != 0).GroupBy(p => p.ZoneID).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (zones != null)
        {
            return zones.Key;
        }
        return replay.Encounters.Count > 0 ? replay.Encounters[0].Zone : 0u;
    }

    private string LoadedCfcPrefix()
    {
        var z = _zones.Find(z => z.TerritoryId == _loadedTerritory);
        return z.Cfc.Length > 0 ? Utils.StringToIdentifier(z.Cfc) : "";
    }

    // replays for the loaded territory: parsed ones from the replay manager, then .log files whose name starts with the duty's name
    private void RefreshReplayChoices()
    {
        _replayChoices.Clear();
        _replayChoicesTerritory = _loadedTerritory;
        var prefix = LoadedCfcPrefix();
        var manager = _replayWindow?.Manager;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loaded = manager?.LoadedReplays.ToList() ?? [];
        _replayChoicesLoadedCount = loaded.Count;
        foreach (var (path, replay, windowOpen, _) in loaded)
        {
            var territory = ReplayTerritory(replay);
            if (!_replayShowAll && territory != _loadedTerritory)
            {
                continue;
            }
            seen.Add(path);
            _replayChoices.Add(new($"{(windowOpen ? "[viewer] " : "[loaded] ")}{Path.GetFileName(path)}{(territory != _loadedTerritory ? $" (territory {territory})" : "")}", path, replay));
        }
        List<string> dirs = [];
        if (manager != null && manager.LogDirectory.Length > 0)
        {
            dirs.Add(manager.LogDirectory);
        }
        if (_replayExtraFolder.Length > 0)
        {
            dirs.Add(_replayExtraFolder.Trim().Trim('"'));
        }
        foreach (var dir in dirs)
        {
            if (_replayChoices.Count >= 200 || !Directory.Exists(dir))
            {
                continue;
            }
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.log", SearchOption.AllDirectories).OrderByDescending(f => f);
            }
            catch (Exception ex)
            {
                _timelineStatus = $"{dir}: {ex.Message}";
                continue;
            }
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (!seen.Add(f) || !_replayShowAll && (prefix.Length == 0 || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                _replayChoices.Add(new(Path.GetRelativePath(dir, f), f, null));
                if (_replayChoices.Count >= 200)
                {
                    break; // the outer loop stops on the same count
                }
            }
        }
        if (_replayChoice >= _replayChoices.Count)
        {
            _replayChoice = -1;
        }
    }

    private void DrawReplayPicker()
    {
        var manager = _replayWindow?.Manager;
        if (_replayChoicesTerritory != _loadedTerritory || (manager?.LoadedReplayCount ?? 0) != _replayChoicesLoadedCount)
        {
            RefreshReplayChoices();
        }
        ImGui.SetNextItemWidth(-150f);
        var preview = _replayChoice >= 0 && _replayChoice < _replayChoices.Count ? _replayChoices[_replayChoice].Label : _replayChoices.Count > 0 ? $"{_replayChoices.Count} replay(s) for this duty" : "no replay found for this duty";
        using (var combo = ImRaii.Combo("##replaypick", preview))
        {
            if (combo)
            {
                for (var i = 0; i < _replayChoices.Count; ++i)
                {
                    if (ImGui.Selectable(_replayChoices[i].Label, i == _replayChoice))
                    {
                        _replayChoice = i;
                    }
                }
            }
        }
        Hint("Replays open in the replay manager and .log files in its folder (and the extra folder) whose name starts with the duty's name; a BMR log is parsed here (10-20 s), an open one is reused as is");
        ImGui.SameLine();
        using (ImRaii.Disabled(_timelineTask != null || _replayChoice < 0 || _replayChoice >= _replayChoices.Count))
        {
            if (ImGui.Button("Import"))
            {
                var c = _replayChoices[_replayChoice];
                if (c.Loaded != null)
                {
                    StartTimelineImport(c.Loaded, c.Path, true);
                }
                else
                {
                    StartTimelineImport(c.Path, true);
                }
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("refresh"))
        {
            RefreshReplayChoices();
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("all", ref _replayShowAll))
        {
            RefreshReplayChoices();
        }
        Hint("List every replay, not only the ones named after the loaded duty");
        ImGui.SetNextItemWidth(-150f);
        if (ImGui.InputTextWithHint("##replayextra", "extra replay folder (searched recursively) or a harness events .json", ref _replayExtraFolder, 512, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            RefreshReplayChoices();
        }
        ImGui.SameLine();
        var extra = _replayExtraFolder.Trim().Trim('"');
        using (ImRaii.Disabled(_timelineTask != null || !extra.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (ImGui.Button("Import json"))
            {
                StartTimelineImport(extra, true);
            }
        }
        if (AnyViewerOpen())
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("sync viewer"))
            {
                SyncWithReplayViewer();
            }
            Hint("Load the territory of the replay open in the replay viewer, import it and seek to the viewer's time");
        }
        if (_replayWindow != null && _timeline != null && _timelinePath.Length > 0 && _timelinePath.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && _openViewer == null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("open in viewer"))
            {
                OpenTimelineInViewer();
            }
            Hint("Open the replay manager and the imported replay's viewer at this scrub time (an already loaded replay opens at once, a file is parsed first)");
        }
    }

    // the inverse of sync viewer: the imported replay goes to the replay manager, its viewer opens at the editor's scrub time
    private void OpenTimelineInViewer()
    {
        if (_replayWindow == null || _timeline == null)
        {
            return;
        }
        _replayWindow.OpenAndBringToFront();
        var loaded = _replayWindow.Manager.LoadedReplays.FirstOrDefault(r => r.path == _timelinePath);
        var time = loaded.replay != null && loaded.replay.Ops.Count > 0 ? loaded.replay.Ops[0].Timestamp.AddSeconds(_timelineT) : (DateTime?)null;
        _replayWindow.Manager.ShowReplay(_timelinePath, time);
        _lastViewerTime = time ?? default;
        _pendingViewerSeek = time == null;
    }

    private void DrawReplayOverlayToggles()
    {
        ImGui.SameLine();
        ImGui.Checkbox("path", ref _showPlayerPath);
        Hint("Player paths of the imported replay in purple: past (before the scrub time) solid, future faded, the party at the scrub time as circles");
        ImGui.SameLine();
        ImGui.Checkbox("bosses", ref _showBosses);
        Hint("Where each boss stood while it was up (its track and extent) and its hitbox at the scrub time");
        ImGui.SameLine();
        ImGui.Checkbox("coverage", ref _showCoverage);
        Hint("Player samples near the result polygon that fall outside it (missed floor) as red dots; the share is in the Result section");
        ImGui.SameLine();
        ImGui.Checkbox("spawns", ref _showSpawns);
        Hint("Enemy spawn locations of every replay imported this session, clustered within 3 y: grey = placed on load (nobody near), orange = wave (spawned with the party there), red = boss adds; hollow diamonds = the layout's spawn-marker objects");
        ImGui.SameLine();
        ImGui.Checkbox("module arena", ref _showModuleArena);
        Hint("The arena of the boss module the replay viewer is running at its current time, drawn over the map: a module that swaps its bounds during the fight (breaking platforms, shrinking floor) shows every state as the viewer plays");
    }

    // P1..Pn: centre the canvas on a party member's position at the scrub time; follow keeps it centred as the time moves
    private void DrawPlayerJumpButtons()
    {
        if (_timeline == null || _timeline.Players.Count == 0)
        {
            return;
        }
        ImGui.TextDisabled("jump to:");
        for (var i = 0; i < _timeline.Players.Count; ++i)
        {
            ImGui.SameLine();
            var track = _timeline.Players[i];
            using var color = ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), i == _followPlayer);
            if (ImGui.SmallButton($"P{i + 1}##jump{i}"))
            {
                CentreOnPlayer(i);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{track.Name}: centre the canvas on this member's position at the scrub time{(i == _followPlayer ? " (following)" : "")}");
            }
        }
        ImGui.SameLine();
        var follow = _followPlayer >= 0;
        if (ImGui.Checkbox("follow", ref follow))
        {
            _followPlayer = follow ? Math.Clamp(_lastJumpedTrack < 0 ? 0 : _lastJumpedTrack, 0, _timeline.Players.Count - 1) : -1;
            _followLastT = float.NaN;
        }
        Hint("Keep the canvas centred on the last jumped-to member while the scrub or the replay viewer moves (click another P button to switch)");
        if (follow && ImGui.IsItemHovered() && ImGui.GetIO().MouseClicked[1])
        {
            _followPlayer = -1;
        }
    }

    private void CentreOnPlayer(int track)
    {
        if (_timeline == null || track < 0 || track >= _timeline.Players.Count)
        {
            return;
        }
        if (ZoneSceneTimelineFile.SampleAt(_timeline.Players[track].Samples, _timelineT) is { } pos)
        {
            _canvas.Center = new(pos.X, pos.Z);
            if (_canvas.Zoom < 4f)
            {
                _canvas.Zoom = 6f;
            }
        }
        _lastJumpedTrack = track;
        if (_followPlayer >= 0)
        {
            _followPlayer = track;
        }
        _followLastT = _timelineT;
    }

    private void FollowPlayerTick()
    {
        if (_followPlayer < 0 || _timeline == null || _followPlayer >= _timeline.Players.Count || _timelineT == _followLastT)
        {
            return;
        }
        _followLastT = _timelineT;
        if (ZoneSceneTimelineFile.SampleAt(_timeline.Players[_followPlayer].Samples, _timelineT) is { } pos)
        {
            _canvas.Center = new(pos.X, pos.Z);
        }
    }

    // the replay viewer's boss module arena at the viewer's time, over the map: the reference for arenas whose floor changes during the fight
    private void DrawModuleArena()
    {
        if (!_showModuleArena || _replayWindow == null || _timelinePath.Length == 0)
        {
            return;
        }
        var module = _replayWindow.Manager.ActiveModule(_timelinePath);
        if (module == null)
        {
            return;
        }
        var bounds = module.Arena.Bounds;
        var center = module.Arena.Center;
        var color = UICanvas2D.WithAlpha(Colors.Vulnerable, 0xE0);
        var parts = bounds.Shape.Parts;
        for (var i = 0; i < parts.Count; ++i)
        {
            var part = parts[i];
            DrawRelContour(part.Exterior, center, color, 2.5f);
            var holeCount = part.HoleStarts.Count;
            for (var h = 0; h < holeCount; ++h)
            {
                DrawRelContour(part.Interior(h), center, color, 1.5f);
            }
        }
        _canvas.ScreenCircle(_canvas.ToScreen(center), 5f, color, 1.5f);
        if (_canvas.Zoom >= 2f)
        {
            if (!ReferenceEquals(module, _moduleArenaFor) || !ReferenceEquals(bounds, _moduleArenaBounds))
            {
                _moduleArenaFor = module;
                _moduleArenaBounds = bounds;
                _moduleArenaText = $"{module.GetType().Name}: {bounds}";
            }
            _canvas.Text(center, _moduleArenaText, color, new(8f, 8f));
        }
    }

    // spawn locations across every imported replay, grouped within 3 y; the class of a cluster is its majority class
    private void UpdateSpawnClusters()
    {
        var key = _importedTimelines.Count * 100003 + _importedTimelines.Sum(t => t.Value.Enemies.Count);
        if (key == _spawnClustersFor)
        {
            return;
        }
        _spawnClustersFor = key;
        _spawnClusters.Clear();
        List<(Vector3 pos, ZoneEnemyClass cls, uint oid, string name, uint marker, string replay)> spawns = [];
        foreach (var (path, tl) in _importedTimelines)
        {
            foreach (var e in tl.Enemies)
            {
                if (!e.Ally && e.Targetable)
                {
                    spawns.Add((e.Pos, e.Class, e.Oid, e.Name, e.Marker, path));
                }
            }
        }
        List<List<(Vector3 pos, ZoneEnemyClass cls, uint oid, string name, uint marker, string replay)>> groups = [];
        foreach (var s in spawns)
        {
            var g = groups.Find(g => ZoneSceneModel.DistXZ(g[0].pos, s.pos) <= 3f);
            if (g == null)
            {
                groups.Add([s]);
            }
            else
            {
                g.Add(s);
            }
        }
        foreach (var g in groups)
        {
            var c = Vector3.Zero;
            foreach (var s in g)
            {
                c += s.pos;
            }
            _spawnClusters.Add(new()
            {
                Centre = c / g.Count,
                Count = g.Count,
                Replays = g.Select(s => s.replay).Distinct().Count(),
                Class = g.GroupBy(s => s.cls).OrderByDescending(x => x.Count()).First().Key,
                Names = ZoneSceneTimelineBuilder.Group(g.Select(s => (s.oid, s.name))),
                Marker = g.Select(s => s.marker).FirstOrDefault(m => m != 0),
            });
        }
    }

    private void DrawSpawnOverlay()
    {
        if (!_showSpawns || _session == null)
        {
            return;
        }
        var model = _session.Model;
        var markerCol = UICanvas2D.WithAlpha(Colors.Other1, 0xA0);
        var dl = ImGui.GetWindowDrawList();
        foreach (var eo in model.EventObjects)
        {
            if (eo.Role != ZoneObjectRole.SpawnMarker || !_canvas.IsVisible(eo.ActorPosition.X - 1f, eo.ActorPosition.Z - 1f, eo.ActorPosition.X + 1f, eo.ActorPosition.Z + 1f))
            {
                continue;
            }
            var sp = _canvas.ToScreen(eo.ActorPosition);
            dl.AddQuad(sp + new Vector2(0f, -5f), sp + new Vector2(5f, 0f), sp + new Vector2(0f, 5f), sp + new Vector2(-5f, 0f), markerCol, 1.5f);
        }
        if (_importedTimelines.Count == 0)
        {
            return;
        }
        UpdateSpawnClusters();
        foreach (var c in _spawnClusters)
        {
            if (!_canvas.IsVisible(c.Centre.X - 1f, c.Centre.Z - 1f, c.Centre.X + 1f, c.Centre.Z + 1f))
            {
                continue;
            }
            var col = c.Class switch
            {
                ZoneEnemyClass.BossAdds or ZoneEnemyClass.Boss => Colors.Enemy,
                ZoneEnemyClass.Wave => Colors.Other2,
                _ => Colors.Shadows,
            };
            var p = new WPos(c.Centre.X, c.Centre.Z);
            _canvas.CircleFilled(p, 0.8f, UICanvas2D.WithAlpha(col, 0x70));
            _canvas.Circle(p, 0.8f, UICanvas2D.WithAlpha(col, 0xE0), 1.5f);
            if (_canvas.Zoom >= 6f)
            {
                _canvas.Text(p, $"{c.Count}x {c.Names}{(c.Replays > 1 ? $" ({c.Replays} runs)" : "")}{(c.Marker != 0 ? $" @0x{c.Marker:X}" : "")}", UICanvas2D.WithAlpha(col, 0xE0), new(8f, -6f));
            }
        }
    }

    // past / future split at the scrub time, party dots at the scrub time
    private void DrawPlayerPaths()
    {
        FollowPlayerTick();
        if (!_showPlayerPath || _timeline == null)
        {
            return;
        }
        var past = UICanvas2D.WithAlpha(PathColor, 0xE0);
        var future = UICanvas2D.WithAlpha(PathColor, 0x50);
        var t = _timelineT;
        if (!ReferenceEquals(_playerLabelsFor, _timeline))
        {
            _playerLabelsFor = _timeline;
            _playerLabels = new string[_timeline.Players.Count];
            for (var i = 0; i < _playerLabels.Length; ++i)
            {
                _playerLabels[i] = $"P{i + 1} {_timeline.Players[i].Name}";
            }
        }
        foreach (var track in _timeline.Players)
        {
            if (!_canvas.IsVisible(track.MinX, track.MinZ, track.MaxX, track.MaxZ))
            {
                continue; // the whole track is off-screen
            }
            var s = track.Samples;
            var n = s.Length / 4;
            for (var i = 1; i < n; ++i)
            {
                var ax = s[4 * i - 3];
                var az = s[4 * i - 1];
                var bx = s[4 * i + 1];
                var bz = s[4 * i + 3];
                if (!_canvas.IsVisible(MathF.Min(ax, bx), MathF.Min(az, bz), MathF.Max(ax, bx), MathF.Max(az, bz)))
                {
                    continue;
                }
                var t1 = s[4 * i];
                if ((new Vector2(ax, az) - new Vector2(bx, bz)).Length() > 30f)
                {
                    continue; // a teleport, not a walk
                }
                _canvas.Line(new WPos(ax, az), new WPos(bx, bz), t1 <= t ? past : future, t1 <= t ? 1.5f : 1f);
            }
        }
        foreach (var (track, pos) in _timeline.PlayersAt(t))
        {
            var sp = _canvas.ToScreen(pos);
            _canvas.ScreenCircle(sp, 4f, PathColor, 2f);
            if (_canvas.Zoom >= 4f)
            {
                var index = _timeline.Players.IndexOf(track);
                _canvas.Text(new WPos(pos.X, pos.Z), index >= 0 && index < _playerLabels.Length ? _playerLabels[index] : track.Name, UICanvas2D.WithAlpha(PathColor, 0xE0), new(6f, -6f));
            }
        }
    }

    // every boss's track while it was up, its extent, and its hitbox where it stood at the scrub time
    private void DrawBossOverlay()
    {
        if (!_showBosses || _timeline == null)
        {
            return;
        }
        var t = _timelineT;
        foreach (var boss in _timeline.Bosses)
        {
            var s = boss.Samples;
            var n = s.Length / 4;
            if (n == 0)
            {
                continue;
            }
            var min = new Vector2(boss.MinX, boss.MinZ);
            var max = new Vector2(boss.MaxX, boss.MaxZ);
            if (!_canvas.IsVisible(min.X - boss.Hitbox, min.Y - boss.Hitbox, max.X + boss.Hitbox, max.Y + boss.Hitbox))
            {
                continue;
            }
            var col = UICanvas2D.WithAlpha(Colors.Enemy, 0x80);
            for (var i = 1; i < n; ++i)
            {
                var p = new Vector2(s[4 * i + 1], s[4 * i + 3]);
                if ((p - new Vector2(s[4 * i - 3], s[4 * i - 1])).Length() <= 30f)
                {
                    _canvas.Line(new WPos(s[4 * i - 3], s[4 * i - 1]), new WPos(p.X, p.Y), col, 1f);
                }
            }
            _canvas.Rect(new WPos(min.X - boss.Hitbox, min.Y - boss.Hitbox), new WPos(max.X + boss.Hitbox, max.Y + boss.Hitbox), UICanvas2D.WithAlpha(Colors.Enemy, 0x50), 1f);
            var live = boss.Death < 0 || t <= boss.Death;
            if (ZoneSceneTimelineFile.SampleAt(s, t) is { } at && t >= boss.Pull - 5.0 && live)
            {
                _canvas.Circle(new WPos(at.X, at.Z), MathF.Max(boss.Hitbox, 0.5f), Colors.Enemy, 2f);
                _canvas.ScreenCircle(_canvas.ToScreen(at), 3f, Colors.Enemy, 2f);
            }
            if (_canvas.Zoom >= 2f)
            {
                _canvas.Text(new WPos(min.X, min.Y), $"{boss.Name} 0x{boss.Oid:X} r{boss.Hitbox:f1} extent {max.X - min.X:f0}x{max.Y - min.Y:f0}", UICanvas2D.WithAlpha(Colors.Enemy, 0xC0), new(0f, -14f));
            }
        }
    }

    // share of the player samples near the enabled result polygons that fall inside them; the ones outside are the missed floor
    private void UpdateCoverage()
    {
        if (_timeline == null || _pipeline.Preview.Count == 0)
        {
            _coverageTimeline = null;
            _coverageCandidates = _coverageInside = _coverageTotal = 0;
            _coverageOutliers.Clear();
            return;
        }
        var enabledKey = 0;
        foreach (var poly in _pipeline.Preview)
        {
            enabledKey = enabledKey * 31 + (poly.Outer.Enabled ? 1 : 0) + poly.Outer.Simplified.Length * 7;
            foreach (var h in poly.Holes)
            {
                enabledKey = enabledKey * 31 + (h.Enabled ? 1 : 0);
            }
        }
        if (_coverageTimeline == _timeline && _coverageGen == _recomputeGen && _coverageEnabledKey == enabledKey)
        {
            return;
        }
        _coverageTimeline = _timeline;
        _coverageGen = _recomputeGen;
        _coverageEnabledKey = enabledKey;
        _coverageOutliers.Clear();
        _coverageInside = _coverageCandidates = _coverageTotal = 0;
        const float margin = 3f;
        List<(Path64 outer, List<Path64> holes, WPos min, WPos max, float minY, float maxY)> polys = [];
        foreach (var poly in _pipeline.Preview)
        {
            if (!poly.Outer.Enabled || poly.Outer.Simplified.Length < 3)
            {
                continue;
            }
            var holes = poly.Holes.Where(h => h.Enabled && h.Simplified.Length >= 3).Select(h => ToPath(h.Simplified)).ToList();
            polys.Add((ToPath(poly.Outer.Simplified), holes, poly.Outer.BBMin, poly.Outer.BBMax, poly.Outer.MinY, poly.Outer.MaxY));
        }
        if (polys.Count == 0)
        {
            return;
        }
        foreach (var track in _timeline.Players)
        {
            var s = track.Samples;
            var n = s.Length / 4;
            _coverageTotal += n;
            for (var i = 0; i < n; ++i)
            {
                var x = s[4 * i + 1];
                var y = s[4 * i + 2];
                var z = s[4 * i + 3];
                var near = false;
                var inside = false;
                foreach (var (outer, holes, min, max, minY, maxY) in polys)
                {
                    if (x < min.X - margin || x > max.X + margin || z < min.Z - margin || z > max.Z + margin || y < minY - 5f || y > maxY + 5f)
                    {
                        continue;
                    }
                    near = true;
                    var pt = new Point64((long)Math.Round(x * ArenaPolygonPipeline.ClipperScale), (long)Math.Round(z * ArenaPolygonPipeline.ClipperScale));
                    if (Clipper.PointInPolygon(pt, outer) != PointInPolygonResult.IsOutside && !holes.Exists(h => Clipper.PointInPolygon(pt, h) == PointInPolygonResult.IsInside))
                    {
                        inside = true;
                        break;
                    }
                }
                if (!near)
                {
                    continue;
                }
                ++_coverageCandidates;
                if (inside)
                {
                    ++_coverageInside;
                }
                else if (_coverageOutliers.Count < 5000)
                {
                    _coverageOutliers.Add(new(x, y, z));
                }
            }
        }

        static Path64 ToPath(WPos[] pts)
        {
            var path = new Path64(pts.Length);
            foreach (var p in pts)
            {
                path.Add(new Point64((long)Math.Round(p.X * ArenaPolygonPipeline.ClipperScale), (long)Math.Round(p.Z * ArenaPolygonPipeline.ClipperScale)));
            }
            return path;
        }
    }

    private void DrawCoverageOutliers()
    {
        if (!_showCoverage || !_showResult || _timeline == null)
        {
            return;
        }
        UpdateCoverage();
        var col = UICanvas2D.WithAlpha(Colors.Danger, 0xB0);
        foreach (var p in _coverageOutliers)
        {
            if (_canvas.IsVisible(p.X, p.Z, p.X, p.Z))
            {
                _canvas.ScreenCircle(_canvas.ToScreen(p), 2f, col, 1.5f);
            }
        }
    }

    // rebuilt when the coverage was recomputed or the red-dot toggle changed, not every frame
    private string CoverageText()
    {
        if (_timeline == null)
        {
            return "";
        }
        var before = _coverageTimeline;
        var gen = _coverageGen;
        var key = _coverageEnabledKey;
        UpdateCoverage();
        var redDots = _coverageOutliers.Count > 0 && _showCoverage;
        if (_coverageText.Length > 0 && ReferenceEquals(before, _coverageTimeline) && gen == _coverageGen && key == _coverageEnabledKey && redDots == _coverageTextRedDots)
        {
            return _coverageText;
        }
        _coverageTextRedDots = redDots;
        if (_coverageCandidates == 0)
        {
            _coverageText = $"coverage: no player sample of {Path.GetFileName(_timeline.Replay)} near the result";
            return _coverageText;
        }
        var share = 100.0 * _coverageInside / _coverageCandidates;
        _coverageText = $"coverage: {share:f1}% of {_coverageCandidates} player samples near the result are inside it ({_coverageCandidates - _coverageInside} outside{(redDots ? ", red dots" : "")}; {_coverageTotal} samples in the run)";
        return _coverageText;
    }
}
