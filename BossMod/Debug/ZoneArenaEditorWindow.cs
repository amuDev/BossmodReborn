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
    private readonly record struct ZoneEntry(uint TerritoryId, string Bg, string Place, string Cfc, uint CfcId, uint IntendedUse);

    private enum Tool : byte { Pick, Rect, Brush, Centre, Polygon, Vertex }
    private enum MeshMode : byte { Auto, Include, Exclude }

    private readonly BossModuleManager _bmm;
    private readonly IDalamudPluginInterface _dalamud;
    private readonly UICanvas2D _canvas = new();
    private readonly ArenaPolygonPipeline _pipeline = new();

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
    private uint _loadedTerritory;
    private Task<ZoneCollisionScene>? _loadTask;
    private CancellationTokenSource? _loadCts;
    private readonly ZoneLoadProgress _progress = new();
    private string _loadStatus = "";
    private float _terrainRadius = 120f;
    private bool _fitOnLoad = true;

    private Tool _tool = Tool.Pick;
    private Tool _toolBeforeCentre = Tool.Pick;
    private float _brushRadius = 2f;
    private readonly Dictionary<int, bool> _keyWasDown = [];
    private readonly HashSet<int> _keyPressedThisFrame = [];
    private bool _keysActive;

    private bool _resultDirty;
    private long _resultDirtySince;
    private int _recomputeGen;
    private Task<(List<CollisionOutlinesExtractor.PolygonWithHoles> polys, string keep, long ms, int obstacles, int obstacleBoxes, int wallSnapEdges, HashSet<int> rim)>? _recomputeTask;
    private int _recomputeTaskGen;
    private long _lastRecomputeMs;
    private int _lastObstacleTriangles;
    private int _lastObstacleBoxes;
    private int _lastWallSnapEdges;
    private int _lastRimTriangles;
    private bool _autoRecompute = true;

    public ZoneArenaEditorWindow(BossModuleManager bmm, IDalamudPluginInterface dalamud)
        : base("Zone arena editor", false, new(1400 * ImGuiHelpers.GlobalScale, 900 * ImGuiHelpers.GlobalScale))
    {
        _bmm = bmm;
        _dalamud = dalamud;
        BuildZoneList();
    }

    protected override void Dispose(bool disposing)
    {
        _loadCts?.Cancel();
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
            _zones.Add(new(row.RowId, bg, place, cfc, row.ContentFinderCondition.RowId, row.TerritoryIntendedUse.RowId));
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
        _loadedTerritory = territory;
        _loadTask = ZoneCollisionLoader.LoadAsync(new LuminaZoneFileSource(Service.LuminaGameData), territory, bg, opt, _progress, _loadCts.Token);
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
        _scene = scene;
        _picker = new(scene);
        _session = new(scene);
        _selection = new(_session.Selected);
        _lastSelectionVersion = _selection.Version;
        _meshModes.Clear();
        for (var i = 0; i < scene.Meshes.Count; ++i)
        {
            _meshModes.Add(MeshMode.Auto);
        }
        _pipeline.Reset();
        _hexSynced = false;
        _manualPolygons.Clear();
        _polygonDraft.Clear();
        _polygonDraftY.Clear();
        _session.DetectSeals();
        var near = TerrainCentreForLoad(scene.TerritoryId);
        var nearPoint = near is { } c ? new Vector3(c.X, 0f, c.Y) : (Vector3?)null;
        _session.ChooseDefaultPair(nearPoint);
        _session.EstimateCentre(nearPoint);
        if (_session.LastEstimate.Reason == "no seals" && Service.ObjectTable.LocalPlayer is { } player && scene.TerritoryId == Service.ClientState.TerritoryType)
        {
            _session.SetCentre(player.Position);
        }
        _hoverTri = -1;
        _resultDirty = false;
        var r = scene.Report;
        _loadStatus = $"territory {scene.TerritoryId}: {scene.Meshes.Count} meshes, {scene.Boxes.Count} boxes, {scene.Triangles.Count} triangles, {_session.Seals.Count} seal(s), parse {r.ParseMs} ms, transform {r.TransformMs} ms{(r.Warnings.Count > 0 ? $", {r.Warnings.Count} warning(s)" : "")}";
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

    public override void Draw()
    {
        if (_loadTask != null && _loadTask.IsCompleted)
        {
            if (_loadTask.IsCompletedSuccessfully)
            {
                PublishLoadedScene(_loadTask.Result);
            }
            else
            {
                _loadStatus = $"load failed: {_loadTask.Exception?.InnerException?.Message ?? _loadTask.Exception?.Message ?? "cancelled"}";
            }
            _loadTask = null;
        }
        PublishRecompute();
        UpdateResultDirty();
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
        DrawCanvas(ImGui.GetContentRegionAvail());
    }

    // the game consumes keyboard input before ImGui sees it, so shortcuts poll the raw key state (same approach as the live picker)
    private const int VkBackspace = 0x08, VkTab = 0x09, VkEnter = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12, VkEscape = 0x1B, VkSpace = 0x20, VkDelete = 0x2E;
    private const int Vk1 = 0x31, VkB = 0x42, VkC = 0x43, VkF = 0x46, VkG = 0x47, VkI = 0x49, VkV = 0x56, VkX = 0x58, VkY = 0x59, VkZ = 0x5A;

    private static bool RawKeyDown(int vk) => (PInvoke.User32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private void PollKeys()
    {
        _keyPressedThisFrame.Clear();
        // edge-detect every frame so a stale edge cannot fire when the window regains focus
        foreach (var vk in (ReadOnlySpan<int>)[VkBackspace, VkTab, VkEnter, VkEscape, VkDelete, Vk1, Vk1 + 1, Vk1 + 2, Vk1 + 3, Vk1 + 4, Vk1 + 5, VkB, VkC, VkF, VkG, VkI, VkV, VkX, VkY, VkZ])
        {
            var down = RawKeyDown(vk);
            if (down && !_keyWasDown.GetValueOrDefault(vk))
            {
                _keyPressedThisFrame.Add(vk);
            }
            _keyWasDown[vk] = down;
        }
        _canvas.SpaceHeld = RawKeyDown(VkSpace);
        // shortcuts act only while this window (or its children) is focused and no text field is being edited
        _keysActive = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && !ImGui.GetIO().WantTextInput;
        if (!_keysActive)
        {
            _keyPressedThisFrame.Clear();
        }
    }

    private bool KeyPressed(int vk) => _keysActive && _keyPressedThisFrame.Contains(vk);
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
        if (CtrlHeld && KeyPressed(VkZ))
        {
            _selection.Undo();
        }
        else if (CtrlHeld && KeyPressed(VkY))
        {
            _selection.Redo();
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
            ToggleExcludeAnchorMesh();
        }
        else if (KeyPressed(VkI))
        {
            ToggleFloorAnchorMesh();
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

    private void ToggleExcludeAnchorMesh()
    {
        if (_scene == null || _anchorTri < 0)
        {
            return;
        }
        var m = _scene.Triangles[_anchorTri].MeshIndex;
        SetMeshMode(m, _meshModes[m] == MeshMode.Exclude ? MeshMode.Auto : MeshMode.Exclude);
    }

    private void ToggleFloorAnchorMesh()
    {
        if (_scene == null || _anchorTri < 0)
        {
            return;
        }
        var m = _scene.Triangles[_anchorTri].MeshIndex;
        SetMeshMode(m, _meshModes[m] == MeshMode.Include ? MeshMode.Auto : MeshMode.Include);
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

    private void MarkResultDirty()
    {
        if (!_resultDirty)
        {
            _resultDirty = true;
            _resultDirtySince = Environment.TickCount64;
        }
    }

    private void UpdateResultDirty()
    {
        if (_selection.Version != _lastSelectionVersion)
        {
            _lastSelectionVersion = _selection.Version;
            MarkResultDirty();
        }
        if (_resultDirty && _autoRecompute && _recomputeTask == null && Environment.TickCount64 - _resultDirtySince >= 250)
        {
            StartRecompute();
        }
        if (_pipeline.CurrentSimplify != _pipeline.LastSimplify && _pipeline.Raw.Count > 0)
        {
            _pipeline.RebuildSimplified();
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
        var extraUnion = ManualUnionPaths();
        var extraCut = ManualCutPaths();
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
        _recomputeTask = Task.Run(() =>
        {
            HashSet<int> rim = [];
            var polys = ArenaAutoMapper.BuildPolygons(scene, snapshot, boxes, seals, active, extraUnion, extraCut, extraY, keep, settings, out var keepStatus, out var ms, out var obstacles, out var obstacleBoxes, out var wallSnapEdges, rim, excluded, floorMeshes, ignoredBoxes);
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
    }
}
