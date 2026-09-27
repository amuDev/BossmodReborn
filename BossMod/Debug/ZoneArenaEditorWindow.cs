using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using System.Threading;

namespace BossMod;

// offline arena editor: loads a territory's collision from the game files, shows it top-down, auto-maps the arena from the seal boxes
// and the walkable floor, lets the author correct the triangle selection and produces the same module snippet as the live collision tab
public sealed partial class ZoneArenaEditorWindow : UIWindow
{
    private readonly record struct ZoneEntry(uint TerritoryId, string Bg, string Place, string Cfc, uint CfcId, uint IntendedUse, string Label); // Label = the zone list row, built once

    private enum Tool : byte { Pick, Rect, Brush, Centre, Polygon, Vertex }
    private enum MeshMode : byte { Auto, Include, Exclude }

    private readonly BossModuleManager _bmm;
    private readonly IDalamudPluginInterface _dalamud;
    private readonly UICanvas2D _canvas = new();
    private readonly ArenaPolygonPipeline _pipeline = new();
    private readonly LuminaZoneFileSource _fileSource = new(Service.LuminaGameData);

    private ZoneCollisionScene? _scene;
    private ZoneTrianglePicker? _picker;
    private ArenaMapSession? _session;
    private TriangleSelection _selection = new([]);
    private readonly List<MeshMode> _meshModes = [];
    private int _lastSelectionVersion = -1;

    private readonly List<ZoneEntry> _zones = [];
    private readonly List<int> _zoneMatches = [];
    private string _zoneSearch = "";
    private string _lastZoneSearch = "";
    private bool _zoneMatchesStale = true;
    private uint _loadedTerritory; // the territory of _scene; set when a load lands
    private uint _loadingTerritory; // the territory _loadTask is loading
    private Task<ZoneCollisionScene>? _loadTask;
    private bool _loadIsReload;
    private CancellationTokenSource? _loadCts;
    private readonly ZoneLoadProgress _progress = new();
    private string _loadStatus = "";
    private float _terrainRadius = 120f;
    private bool _fitOnLoad = true;

    private Tool _tool = Tool.Pick;
    private Tool _toolBeforeCentre = Tool.Pick;
    private float _brushRadius = 2f;
    private readonly bool[] _keyWasDown = new bool[256]; // indexed by virtual key
    private readonly bool[] _keyPressedThisFrame = new bool[256];
    private bool _keysActive;

    private bool _resultDirty;
    private long _resultDirtySince;
    private int _recomputeGen;
    private Task<(List<PolygonWithHoles> polys, string keep, long ms, int obstacles, int obstacleBoxes, int wallSnapEdges, HashSet<int> rim)>? _recomputeTask;
    private int _recomputeTaskGen;
    private CancellationTokenSource? _recomputeCts, _autoMapCts;
    private long _lastRecomputeMs;
    private int _lastObstacleTriangles;
    private int _lastObstacleBoxes;
    private int _lastWallSnapEdges;
    private int _lastRimTriangles;
    private bool _autoRecompute = true;
    private float _autoRecomputeDelay = 0.5f; // seconds of no edits before an automatic recompute starts
    private Task<AutoMapJob>? _autoMapTask;
    private ArenaPolygonPipeline.SimplifySettings _pendingSimplify;
    private long _pendingSimplifySince;

    public ZoneArenaEditorWindow(BossModuleManager bmm, IDalamudPluginInterface dalamud, ReplayManagementWindow? replayWindow = null)
        : base("Zone arena editor", false, new(1400 * ImGuiHelpers.GlobalScale, 900 * ImGuiHelpers.GlobalScale))
    {
        _bmm = bmm;
        _dalamud = dalamud;
        _replayWindow = replayWindow;
        BuildZoneList();
    }

    protected override void Dispose(bool disposing)
    {
        _loadCts?.Cancel();
        _mapCts?.Cancel();
        _timelineCts?.Cancel();
        _timelineCts?.Dispose();
        _recomputeCts?.Cancel();
        _recomputeCts?.Dispose();
        _autoMapCts?.Cancel();
        _autoMapCts?.Dispose();
        base.Dispose(disposing);
    }

    private void BuildZoneList()
    {
        var sheet = Service.LuminaSheet<Lumina.Excel.Sheets.TerritoryType>();
        if (sheet == null)
        {
            return;
        }
        foreach (var row in sheet)
        {
            var bg = row.Bg.ToString();
            if (bg.Length == 0)
            {
                continue;
            }
            var place = row.PlaceName.ValueNullable?.Name.ToString() ?? "";
            var cfc = row.ContentFinderCondition.ValueNullable?.Name.ToString() ?? "";
            var label = cfc.Length > 0 ? $"{row.RowId}: {cfc} ({place})##zone{row.RowId}" : $"{row.RowId}: {place}##zone{row.RowId}";
            _zones.Add(new(row.RowId, bg, place, cfc, row.ContentFinderCondition.RowId, row.TerritoryIntendedUse.RowId, label));
        }
    }

    private void UpdateZoneMatches()
    {
        if (!_zoneMatchesStale && _zoneSearch == _lastZoneSearch)
        {
            return;
        }
        _zoneMatchesStale = false;
        _lastZoneSearch = _zoneSearch;
        _zoneMatches.Clear();
        var q = _zoneSearch.Trim();
        var isNumber = uint.TryParse(q, out var id);
        for (var i = 0; i < _zones.Count; ++i)
        {
            var z = _zones[i];
            if (q.Length == 0 || (isNumber && (z.TerritoryId == id || z.CfcId == id)) || z.Place.Contains(q, StringComparison.OrdinalIgnoreCase) || z.Cfc.Contains(q, StringComparison.OrdinalIgnoreCase) || z.Bg.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                _zoneMatches.Add(i);
                if (_zoneMatches.Count >= 200)
                {
                    break;
                }
            }
        }
    }

    public void LoadTerritory(uint territory)
    {
        _loadCts?.Cancel();
        _loadCts = new();
        var bg = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(territory)?.Bg.ToString() ?? "";
        if (bg.Length == 0)
        {
            _loadStatus = $"territory {territory}: no bg path";
            return;
        }
        var opt = new ZoneLoadOptions { TerrainPreloadRadius = _terrainRadius, TerrainPreloadCenterXZ = TerrainCentreForLoad(territory) };
        _loadStatus = $"loading territory {territory} ({bg})...";
        _loadIsReload = _loadedTerritory == territory && _scene != null;
        _loadingTerritory = territory;
        _loadTask = ZoneCollisionLoader.LoadAsync(_fileSource, territory, bg, opt, _progress, _loadCts.Token);
    }

    // streams the terrain tiles within the terrain radius of xz; the tile count, the scene caches refreshed and any running recompute superseded when > 0
    private int LoadTerrainAround(Vector2 xz)
    {
        if (_scene == null || _terrainRadius <= 0f)
        {
            return 0;
        }
        var added = ZoneCollisionLoader.EnsureTerrainLoaded(_scene, _fileSource, xz, _terrainRadius);
        if (added > 0)
        {
            TerrainAppended();
            ++_recomputeGen; // a recompute reading the old mesh list is discarded when it lands
            MarkResultDirty();
        }
        return added;
    }

    // terrain tiles are lazy; preload around the player when loading the current zone, else around the previous centre, else none
    private Vector2? TerrainCentreForLoad(uint territory)
    {
        if (territory == Service.ClientState.TerritoryType && Service.ObjectTable.LocalPlayer is { } player)
        {
            return new(player.Position.X, player.Position.Z);
        }
        if (_session is { CentreValid: true } s && _loadedTerritory == territory)
        {
            return new(s.Centre.X, s.Centre.Z);
        }
        return null;
    }

    private void PublishLoadedScene(ZoneCollisionScene scene)
    {
        _loadedTerritory = scene.TerritoryId;
        _scene = scene;
        _picker = new(scene);
        InvalidateSceneCaches();
        _session = new(scene, new LuminaZoneSheetSource());
        _selection = new(_session.Selected);
        _lastSelectionVersion = _selection.Version;
        _meshModes.Clear();
        for (var i = 0; i < scene.Meshes.Count; ++i)
        {
            _meshModes.Add(MeshMode.Auto);
        }
        _pipeline.Reset();
        _pendingProjectLoad = null;
        ResetEditHistory();
        _projectHealth.Clear();
        _hexSynced = false;
        _manualPolygons.Clear();
        _polygonDraft.Clear();
        _polygonDraftY.Clear();
        _rules.Clear();
        _selectedRule = -1;
        _ruleSnippetFor = -1;
        _timeline = null;
        _timelinePath = "";
        _timelineStatus = "";
        _importedTimelines.Clear();
        ResetReplayViewState();
        _spawnClustersFor = -1;
        _hoverObject = _selectedObject = _popupObject = ObjectRef.None;
        EnsureDefaultScene();
        _session.DetectSeals();
        var inZone = scene.TerritoryId == Service.ClientState.TerritoryType;
        var spawn = DungeonSpawn(_session.Model);
        if (!inZone && !_loadIsReload && spawn is { } sp)
        {
            // a zone loaded from outside: start where the party starts (the entrance barrier, else the first player pop point)
            LoadTerrainAround(new(sp.X, sp.Z));
            var top = _picker.PickTriangleNearY(new(sp.X, sp.Z), sp.Y, _scratchHits);
            _session.SetCentre(new(sp.X, top >= 0 ? scene.Triangles[top].YAt(sp.X, sp.Z) : sp.Y, sp.Z));
            _session.ChooseDefaultPair(_session.Centre);
        }
        else
        {
            var near = TerrainCentreForLoad(scene.TerritoryId);
            var nearPoint = near is { } c ? new Vector3(c.X, 0f, c.Y) : (Vector3?)null;
            _session.ChooseDefaultPair(nearPoint);
            _session.EstimateCentre(nearPoint);
            if (_session.LastEstimate.Reason == "no seals" && Service.ObjectTable.LocalPlayer is { } player && inZone)
            {
                _session.SetCentre(player.Position);
            }
        }
        _hoverTri = -1;
        ResetCanvasState();
        _resultDirty = false;
        var flowTiles = inZone ? 0 : PreloadTerrainAlongFlow(scene);
        if (!_loadIsReload)
        {
            ReopenLastProject(scene);
        }
        RunPendingReplaySync();
        var r = scene.Report;
        _loadStatus = $"territory {scene.TerritoryId}: {scene.Meshes.Count} meshes, {scene.Boxes.Count} boxes, {scene.Triangles.Count} triangles, {_session.Seals.Count} seal(s), parse {r.ParseMs} ms, transform {r.TransformMs} ms{(r.Warnings.Count > 0 ? $", {r.Warnings.Count} warning(s)" : "")}{(flowTiles > 0 ? $", {flowTiles} terrain tile(s) along the flow" : "")}";
        if (_fitOnLoad)
        {
            if (_session.CentreValid)
            {
                _canvas.Center = new(_session.Centre.X, _session.Centre.Z);
                _canvas.Zoom = 8f;
            }
            else
            {
                _canvas.FitBounds(scene.Bounds.Min, scene.Bounds.Max);
            }
        }
    }

    // where the party appears: the entrance barrier object, else the first player pop point of the layout
    private static Vector3? DungeonSpawn(ZoneSceneModel model)
    {
        var entrance = model.EventObjects.Find(e => e.Role == ZoneObjectRole.Entrance);
        if (entrance != null)
        {
            return entrance.ActorPosition;
        }
        var pop = model.PopPoints.Find(p => p.PopType == LgbPopType.Pc);
        return pop?.Position;
    }

    public override void Draw()
    {
        if (_loadTask != null && _loadTask.IsCompleted)
        {
            var task = _loadTask;
            _loadTask = null; // cleared first: a throw while publishing must not re-run every frame
            if (task.IsCompletedSuccessfully)
            {
                try
                {
                    PublishLoadedScene(task.Result);
                }
                catch (Exception ex)
                {
                    _loadStatus = $"territory {_loadingTerritory}: publish failed: {ex.Message}";
                }
            }
            else
            {
                _loadStatus = $"territory {_loadingTerritory}: load {(task.IsCanceled ? "cancelled" : $"failed: {task.Exception?.InnerException?.Message ?? task.Exception?.Message}")}";
            }
        }
        PublishMapping();
        if (MappingBusy)
        {
            DrawMappingProgress();
            return;
        }
        PublishAutoMap();
        PublishRecompute();
        PublishTimeline();
        if (_seekPending && Environment.TickCount64 - _seekPendingSince >= 100)
        {
            FlushPendingSeek(); // the slider's own timer only runs while the Scenes section is drawn
        }
        PullViewerTime();
        _session?.EnsureResolved();
        UpdateResultDirty();
        TrackEdits();
        if (_autosaveDue && !_resultDirty && _recomputeTask == null && AutosaveTick())
        {
            _autosaveDue = false;
        }
        HandleKeyboard();
        DrawWorldPreview();

        using var table = ImRaii.Table("##layout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV);
        if (!table)
        {
            return;
        }
        ImGui.TableSetupColumn("sidebar", ImGuiTableColumnFlags.WidthFixed, 400f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("canvas", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        using (var sidebar = ImRaii.Child("##sidebar", new Vector2(0f, 0f), false))
        {
            if (sidebar)
            {
                DrawSidebar();
            }
        }
        ImGui.TableNextColumn();
        DrawCanvasToolbar();
        DrawCanvas(ImGui.GetContentRegionAvail());
    }

    // the game consumes keyboard input before ImGui sees it, so shortcuts poll the raw key state (same approach as the live picker)
    private const int VkBackspace = 0x08, VkTab = 0x09, VkEnter = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkEscape = 0x1B, VkSpace = 0x20, VkDelete = 0x2E;
    private const int Vk1 = 0x31, VkB = 0x42, VkC = 0x43, VkE = 0x45, VkF = 0x46, VkG = 0x47, VkI = 0x49, VkV = 0x56, VkX = 0x58, VkY = 0x59, VkZ = 0x5A;
    private const int VkLeft = 0x25, VkRight = 0x27;

    private static partial class Native
    {
        [LibraryImport("user32.dll")]
        internal static partial short GetAsyncKeyState(int vKey);
    }

    private static bool RawKeyDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static readonly int[] PolledKeys = [VkBackspace, VkTab, VkEnter, VkEscape, VkDelete, Vk1, Vk1 + 1, Vk1 + 2, Vk1 + 3, Vk1 + 4, Vk1 + 5, VkB, VkC, VkE, VkF, VkG, VkI, VkV, VkX, VkY, VkZ, VkLeft, VkRight];

    private void PollKeys()
    {
        Array.Clear(_keyPressedThisFrame);
        // edge-detect every frame so a stale edge cannot fire when the window regains focus
        foreach (var vk in PolledKeys)
        {
            var down = RawKeyDown(vk);
            _keyPressedThisFrame[vk] = down && !_keyWasDown[vk];
            _keyWasDown[vk] = down;
        }
        _canvas.SpaceHeld = RawKeyDown(VkSpace);
        _canvas.ShiftHeld = RawKeyDown(VkShift);
        // shortcuts act only while this window (or its children) is focused and no text field is being edited
        _keysActive = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput;
    }

    private bool KeyPressed(int vk) => _keysActive && _keyPressedThisFrame[vk];
    private static bool CtrlHeld => RawKeyDown(VkControl) || ImGui.GetIO().KeyCtrl;
    private static bool ShiftHeld => RawKeyDown(VkShift) || ImGui.GetIO().KeyShift;
    private static bool AltHeld => RawKeyDown(VkMenu) || ImGui.GetIO().KeyAlt;

    private void HandleKeyboard()
    {
        PollKeys();
        if (!_keysActive)
        {
            return;
        }
        if (_canvas.Hovered)
        {
            ImGui.SetNextFrameWantCaptureKeyboard(true);
        }
        var ctrl = CtrlHeld;
        if (ctrl && ShiftHeld && KeyPressed(VkZ))
        {
            UndoEdit();
        }
        else if (ctrl && KeyPressed(VkZ))
        {
            _selection.Undo();
        }
        else if (ctrl && KeyPressed(VkY))
        {
            _selection.Redo();
        }
        else if (ctrl && KeyPressed(VkLeft))
        {
            StepTimeline(-1);
        }
        else if (ctrl && KeyPressed(VkRight))
        {
            StepTimeline(1);
        }
        else if (ctrl || AltHeld)
        {
            return; // the plain-letter shortcuts below do not fire while a modifier is held (ctrl+C in a text field, alt+click on the canvas)
        }
        else if (KeyPressed(VkE) && _selectedObject.Kind == ObjectKind.EObj && _session != null)
        {
            var eo = _session.Model.EventObjects[_selectedObject.Index];
            if (eo.ControlsCollision)
            {
                ShowEObjDerived(_selectedObject.Index, _session.EObjState(_selectedObject.Index) == 7 ? (ushort)0 : (ushort)7);
            }
        }
        else if (KeyPressed(VkF) && _scene != null)
        {
            FitToSelectionOrScene();
        }
        else if (KeyPressed(VkDelete) && _hoverTri >= 0)
        {
            _selection.Remove(_hoverTri);
        }
        else if (KeyPressed(Vk1))
        {
            _tool = Tool.Pick;
        }
        else if (KeyPressed(Vk1 + 1))
        {
            _tool = Tool.Rect;
        }
        else if (KeyPressed(Vk1 + 2))
        {
            _tool = Tool.Brush;
        }
        else if (KeyPressed(Vk1 + 3))
        {
            SetCentreTool(true);
        }
        else if (KeyPressed(Vk1 + 4))
        {
            _tool = Tool.Polygon;
        }
        else if (KeyPressed(Vk1 + 5))
        {
            _tool = Tool.Vertex;
        }
        else if (KeyPressed(VkG))
        {
            GrowFromAnchor(1);
        }
        else if (KeyPressed(VkC))
        {
            GrowFromAnchor(int.MaxValue);
        }
        else if (KeyPressed(VkV))
        {
            CopyAnchorVertex(false);
        }
        else if (KeyPressed(VkX))
        {
            ToggleAnchorMeshMode(MeshMode.Exclude);
        }
        else if (KeyPressed(VkI))
        {
            ToggleAnchorMeshMode(MeshMode.Include);
        }
        else if (KeyPressed(VkB))
        {
            ToggleHoveredBox();
        }
        else if (KeyPressed(VkEscape))
        {
            _polygonDraft.Clear();
            _polygonDraftY.Clear();
            if (_tool == Tool.Centre)
            {
                SetCentreTool(false);
            }
        }
    }

    private void SetCentreTool(bool on)
    {
        if (on && _tool != Tool.Centre)
        {
            _toolBeforeCentre = _tool;
            _tool = Tool.Centre;
        }
        else if (!on && _tool == Tool.Centre)
        {
            _tool = _toolBeforeCentre == Tool.Centre ? Tool.Pick : _toolBeforeCentre;
        }
    }

    // sidebar actions work from the last clicked triangle/vertex, since the mouse cannot hover the canvas and press a button at the same time
    private void GrowFromAnchor(int depth)
    {
        if (_session == null || _anchorTri < 0)
        {
            return;
        }
        HashSet<int> grown = [];
        _session.GrowFrom(_anchorTri, depth, grown);
        _selection.Apply([.. grown], [], depth == 1 ? "grow" : "connected floor");
    }

    private void CopyAnchorVertex(bool vector3)
    {
        if (!_anchorVertexValid)
        {
            return;
        }
        ImGui.SetClipboardText(vector3
            ? $"new({CollisionArenaCodeGen.F(_anchorVertex.X, 3)}, {CollisionArenaCodeGen.F(_anchorVertex.Y, 3)}, {CollisionArenaCodeGen.F(_anchorVertex.Z, 3)})"
            : $"new({CollisionArenaCodeGen.F(_anchorVertex.X, 3)}, {CollisionArenaCodeGen.F(_anchorVertex.Z, 3)})");
    }

    // the last clicked triangle's mesh switched into `mode`, or back to Auto when it is in that mode already
    private void ToggleAnchorMeshMode(MeshMode mode)
    {
        if (_scene == null || _anchorTri < 0)
        {
            return;
        }
        var m = _scene.Triangles[_anchorTri].MeshIndex;
        SetMeshMode(m, _meshModes[m] == mode ? MeshMode.Auto : mode);
    }

    // Include = the mesh counts as floor whatever its material: its walkable-slope triangles are selected, become flood-fill candidates and never cut
    // as obstacles. Exclude = removed from the selection and never cut. Auto = material decides.
    private void SetMeshMode(int m, MeshMode mode)
    {
        if (_scene == null || _session == null || m < 0 || m >= _meshModes.Count)
        {
            return;
        }
        _meshModes[m] = mode;
        var mesh = _scene.Meshes[m];
        if (mode == MeshMode.Include)
        {
            _session.FloorMeshes.Add(m);
            HashSet<int> add = [];
            var tris = _scene.Triangles.Span;
            var minNormalY = _session.Settings.MinNormalY;
            for (var i = mesh.TriStart; i < mesh.TriStart + mesh.TriCount; ++i)
            {
                if (tris[i].NormalY >= minNormalY)
                {
                    add.Add(i);
                }
            }
            _selection.Apply([.. add], [], "include mesh as floor");
        }
        else
        {
            _session.FloorMeshes.Remove(m);
            if (mode == MeshMode.Exclude)
            {
                List<int> remove = [];
                for (var i = mesh.TriStart; i < mesh.TriStart + mesh.TriCount; ++i)
                {
                    remove.Add(i);
                }
                _selection.Apply([], [.. remove], "exclude mesh");
            }
        }
        _session.Adjacency = null;
        MarkResultDirty();
    }

    private void FitToSelectionOrScene()
    {
        if (_scene == null)
        {
            return;
        }
        if (_selection.Selected.Count > 0)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var tris = _scene.Triangles.Span;
            foreach (var t in _selection.Selected)
            {
                var b = tris[t].Bounds;
                min = Vector3.Min(min, b.Min);
                max = Vector3.Max(max, b.Max);
            }
            _canvas.FitBounds(min, max);
        }
        else
        {
            _canvas.FitBounds(_scene.Bounds.Min, _scene.Bounds.Max);
        }
    }

    // --- result recompute: selection -> polygons on a task, then the pipeline on the main thread ---

    // every edit restarts the delay, so a burst of edits triggers one recompute after the last one
    private void MarkResultDirty()
    {
        _resultDirty = true;
        _resultDirtySince = Environment.TickCount64;
        if (_recomputeTask != null)
        {
            _recomputeCts?.Cancel(); // the run in flight is stale: stop it so the next one starts sooner
        }
    }

    private void UpdateResultDirty()
    {
        if (_selection.Version != _lastSelectionVersion)
        {
            _lastSelectionVersion = _selection.Version;
            MarkResultDirty();
        }
        if (_resultDirty && _autoRecompute && _recomputeTask == null && Environment.TickCount64 - _resultDirtySince >= (long)(_autoRecomputeDelay * 1000f))
        {
            StartRecompute();
        }
        // simplify sliders rebuild once the value has rested for a moment, not on every frame of a drag
        var simplify = _pipeline.CurrentSimplify;
        if (simplify != _pipeline.LastSimplify && _pipeline.Raw.Count > 0)
        {
            if (simplify != _pendingSimplify)
            {
                _pendingSimplify = simplify;
                _pendingSimplifySince = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - _pendingSimplifySince >= 150)
            {
                _pipeline.RebuildSimplified();
            }
        }
    }

    // --- auto-map: adjacency + flood fill on a task, published on the main thread ---

    private void StartAutoMap()
    {
        if (_session == null || _autoMapTask != null)
        {
            return;
        }
        var session = _session;
        var job = session.PrepareAutoMap();
        _autoMapCts?.Dispose();
        _autoMapCts = new();
        var ct = _autoMapCts.Token;
        _autoMapTask = Task.Run(() =>
        {
            session.RunAutoMap(job, ct);
            return job;
        });
    }

    private void PublishAutoMap()
    {
        if (_autoMapTask == null || !_autoMapTask.IsCompleted)
        {
            return;
        }
        var task = _autoMapTask;
        _autoMapTask = null;
        if (!task.IsCompletedSuccessfully || _session == null)
        {
            _loadStatus = $"auto-map {(task.IsCanceled || task.Exception?.InnerException is OperationCanceledException ? "cancelled" : $"failed: {task.Exception?.InnerException?.Message ?? task.Exception?.Message}")}";
            if (_pendingProjectLoad is { } dropped)
            {
                _pendingProjectLoad = null; // the project load waiting on this result is abandoned, not finished by a later manual auto-map
                _projectStatus = $"loading '{dropped.name}' failed: {_loadStatus}";
            }
            return;
        }
        HashSet<int> previous = [.. _selection.Selected]; // before the apply: it rewrites the selection in place
        if (!_session.ApplyAutoMap(task.Result))
        {
            StartAutoMap(); // the inputs changed while it ran: once more with the current ones
            return;
        }
        _selection.RecordExternal(previous, "auto-map");
        MarkResultDirty();
        _resultDirtySince = 0;
        if (_pendingProjectLoad is { } pending)
        {
            _pendingProjectLoad = null;
            FinishProjectLoad(pending.p, pending.name, pending.missing, pending.missingSceneRefs);
        }
    }

    private void StartRecompute()
    {
        if (_session == null || _scene == null)
        {
            return;
        }
        _resultDirty = false;
        var session = _session;
        var scene = _scene;
        var snapshot = new int[session.Selected.Count];
        session.Selected.CopyTo(snapshot);
        var boxes = new int[session.SelectedFloorBoxes.Count];
        session.SelectedFloorBoxes.CopyTo(boxes);
        var seals = session.Seals;
        HashSet<int> active = [.. session.ActiveSeals];
        var settings = session.Settings.Clone();
        var centre = session.Centre;
        var keep = settings.KeepPolygonContainingCentre ? new Vector2(centre.X, centre.Z) : (Vector2?)null;
        var extraUnion = ManualPaths(false);
        var extraCut = ManualPaths(true);
        var extraY = ManualYSource();
        HashSet<int> excluded = [];
        for (var m = 0; m < _meshModes.Count; ++m)
        {
            if (_meshModes[m] == MeshMode.Exclude)
            {
                excluded.Add(m);
            }
        }
        HashSet<int> floorMeshes = [.. session.FloorMeshes];
        HashSet<int> ignoredBoxes = [.. session.IgnoredBoxes];
        var gen = ++_recomputeGen;
        _recomputeTaskGen = gen;
        _recomputeCts?.Dispose();
        _recomputeCts = new();
        var ct = _recomputeCts.Token;
        _recomputeTask = Task.Run(() =>
        {
            HashSet<int> rim = [];
            var polys = ArenaAutoMapper.BuildPolygons(scene, snapshot, boxes, seals, active, extraUnion, extraCut, extraY, keep, settings, out var keepStatus, out var ms, out var obstacles, out var obstacleBoxes, out var wallSnapEdges, rim, excluded, floorMeshes, ignoredBoxes, ct: ct);
            return (polys, keepStatus, ms, obstacles, obstacleBoxes, wallSnapEdges, rim);
        });
    }

    private void PublishRecompute()
    {
        if (_recomputeTask == null || !_recomputeTask.IsCompleted)
        {
            return;
        }
        var task = _recomputeTask;
        _recomputeTask = null;
        if (_recomputeTaskGen != _recomputeGen)
        {
            MarkResultDirty(); // superseded, run again with the latest state
            return;
        }
        if (task.IsCanceled || task.Exception?.InnerException is OperationCanceledException)
        {
            return; // stopped by a newer dirty mark, which already queued the next run
        }
        if (!task.IsCompletedSuccessfully)
        {
            _pipeline.Status = $"recompute failed: {task.Exception?.InnerException?.Message ?? task.Exception?.Message}";
            return;
        }
        var (polys, keep, ms, obstacles, obstacleBoxes, wallSnapEdges, rim) = task.Result;
        _pipeline.Raw = polys;
        _pipeline.KeepStatus = keep;
        _pipeline.AnchorXZ = _session != null ? new(_session.Centre.X, _session.Centre.Z) : default;
        _lastRecomputeMs = ms;
        _lastObstacleTriangles = obstacles;
        _lastObstacleBoxes = obstacleBoxes;
        _lastWallSnapEdges = wallSnapEdges;
        _lastRimTriangles = rim.Count;
        if (_session != null)
        {
            _session.RimTriangles.Clear();
            _session.RimTriangles.UnionWith(rim);
        }
        _pipeline.RebuildSimplified();
        ApplyPendingContourFlags();
        if (_session != null)
        {
            _session.Polygons = polys;
        }
        _lastAutosave = Environment.TickCount64 - 2000; // AutosaveTick waits 5 s since the last save: the autosave lands three seconds after the result settles
        _autosaveDue = true;
    }
}
