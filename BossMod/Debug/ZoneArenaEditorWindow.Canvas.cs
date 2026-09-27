using Clipper2Lib;
using Dalamud.Bindings.ImGui;

namespace BossMod;

public sealed partial class ZoneArenaEditorWindow
{
    private const int TriangleBudget = 60000;

    private int _hoverTri = -1;
    private int _anchorTri = -1;          // last clicked triangle (used by sidebar actions and shortcuts)
    private Vector3 _anchorVertex;
    private bool _anchorVertexValid;
    private readonly List<int> _pickHits = [];
    private readonly List<int> _scratchHits = []; // one-shot picks that must not disturb the hover stack
    private int _pickCycle;
    private Vector3 _snapVertex;
    private bool _snapValid;
    private int _hoveredSeal = -1;
    private int _hoverBox = -1;           // box collider under the cursor (smallest footprint wins)
    private readonly HashSet<int> _sealBoxes = [];
    private bool _draggingCentre;
    private bool _brushStroke;
    private readonly List<int> _scratch = [];
    private readonly List<ManualPolygon> _manualPolygons = [];
    private readonly List<WPos> _polygonDraft = [];
    private readonly List<float> _polygonDraftY = [];
    private bool _polygonDraftDifference = true;
    private bool _showAllTriangles = true;
    private bool _showSelected = true;
    private bool _showResult = true;
    private bool _showSeals = true;
    private bool _showMarkers = true;
    private bool _showPlayer = true;       // player position + facing on the canvas while standing in the loaded zone
    private bool _showWorldPreview = true; // result contours drawn in the 3D world while standing in the loaded zone
    private int _worldPreviewMode;         // 0 = the simplified contours, 1 = the ArenaBoundsCustom the snippet builds (framework post-processing applied)
    private float _worldPreviewLift = 0.2f;
    private bool _selectFloorOnly = true; // rect/brush add only floor-material (or floor-mesh) triangles
    private bool _selectLayerOnly = true; // rect/brush act only on the Tab layer under the cursor at the start of the drag/stroke
    private float _layerRefY;
    private bool _layerRefValid;
    private readonly List<int> _layerHits = [];
    private int _hoverVertexPoly = -1;    // vertex tool: hovered preview vertex (polygon index, contour index, vertex index)
    private int _hoverVertexContour = -1;
    private int _hoverVertexIndex = -1;
    private int _hoverDeletedVertex = -1; // vertex tool: hovered deleted vertex (index into pipeline.DeletedVertices)
    private bool _showRawOutline;
    private int _trianglesDrawn;
    private bool _showFloorPieces = true;
    private readonly Dictionary<int, MeshDraw> _meshDrawCache = []; // per-mesh triangle classes (floor / wall / other) under the floor settings, computed once per settings change instead of per frame, and the visible triangles in screen space under the last view
    private UICanvas2D.ViewState _meshView;
    private int _meshViewGen;       // bumped when the view changes; a mesh's screen-space record is reused while it carries the current value
    private int _meshDrawFrame;
    private int _meshRecordEntries; // record capacity held over all meshes, so records of meshes that left the view are released in a sweep
    private readonly Dictionary<int, (int pieces, short[]? piece)> _meshPieceCache = []; // floor piece index per triangle (-1 = none) per mesh, null when no piece overlaps the mesh
    private string _floorSettingsKey = "";
    private uint[] _pieceFills = []; // fill per floor piece at the scrub time (gone pieces faded), rebuilt when the model or the scrub time changes
    private ZoneSceneModel? _pieceFillsModel;
    private ZoneSceneTimelineFile? _pieceFillsTimeline;
    private float _pieceFillsT = float.NaN;
    private readonly Dictionary<int, int> _popByMarker = []; // pop range marker -> pop point, per model
    private ZoneSceneModel? _popByMarkerModel;
    private int[] _meshSelectedCounts = [];
    private int _meshSelectedCountsVersion = -1;
    private readonly Dictionary<string, Vector3?> _nodePositionCache = [];
    private readonly List<WPos> _relContour = []; // scratch for the bounds preview contours

    // triangle classes of one mesh under (floor settings, forced floor, excluded), the same triangles bucketed by class, and the record of the
    // triangles visible under one view with their screen-space vertices
    private sealed class MeshDraw
    {
        public string Key = "";
        public bool Forced, Excluded;
        public int TriStart;
        public byte[] Cls = [];       // 0 other, 1 floor, 2 wall per local triangle
        public int[] Order = [];      // local triangle indices bucketed floor | other | wall, ascending inside a bucket
        public readonly int[] BucketStart = new int[4];
        public readonly float[] BucketMinX = new float[3], BucketMinZ = new float[3], BucketMaxX = new float[3], BucketMaxZ = new float[3];
        public bool Contained;        // every triangle lies inside the mesh bounds (no NaN vertex): a mesh or bucket inside the view needs no per-triangle cull

        public int ViewGen = -1;
        public bool Edges;            // the record holds every visible triangle (edges are drawn), else the floor ones only
        public bool Ordered;          // entries carry their ordinal among the visible triangles, so a triangle budget cut can be replayed
        public bool Complete;         // the whole mesh was scanned; else the scan stopped at the budget after Processed visible triangles
        public int Processed;
        public int Count;
        public int[] Local = [];
        public int[] Ordinal = [];
        public Vector2[] Verts = [];  // three per entry
        public int LastFrame;
    }

    public sealed class ManualPolygon
    {
        public bool Difference = true;
        public List<WPos> Vertices = [];
        public List<float> Y = [];
    }

    private bool TriangleAllowed(int tri)
    {
        if (_scene == null)
        {
            return false;
        }
        var meshIndex = _scene.Triangles[tri].MeshIndex;
        return _scene.IsMeshEnabled(meshIndex) && (meshIndex >= _meshModes.Count || _meshModes[meshIndex] != MeshMode.Exclude);
    }

    // the closed manual polygons of one kind (cut out of / unioned into the result) as clipper paths
    private List<Path64> ManualPaths(bool cut)
    {
        List<Path64> result = [];
        foreach (var p in _manualPolygons)
        {
            if (p.Difference == cut && p.Vertices.Count >= 3)
            {
                result.Add(ToPath(p.Vertices));
            }
        }
        return result;
    }

    private List<Vector3> ManualYSource()
    {
        List<Vector3> result = [];
        foreach (var p in _manualPolygons)
        {
            for (var i = 0; i < p.Vertices.Count; ++i)
            {
                result.Add(new(p.Vertices[i].X, i < p.Y.Count ? p.Y[i] : 0f, p.Vertices[i].Z));
            }
        }
        return result;
    }

    private static Path64 ToPath(List<WPos> pts)
    {
        var path = new Path64(pts.Count);
        for (var i = 0; i < pts.Count; ++i)
        {
            path.Add(new Point64((long)Math.Round(pts[i].X * BoxFootprintOps.DefaultScale), (long)Math.Round(pts[i].Z * BoxFootprintOps.DefaultScale)));
        }
        if (path.Count >= 3 && !Clipper.IsPositive(path))
        {
            path.Reverse();
        }
        return path;
    }

    private void DrawCanvas(Vector2 size)
    {
        if (!_canvas.Begin("##zonecanvas", size))
        {
            return;
        }
        _trianglesDrawn = 0;
        if (_scene != null && _session != null)
        {
            DrawAreaOverview();
            DrawMeshes();
            DrawFloorPieces();
            DrawSelection();
            DrawBoxes();
            DrawSeals();
            DrawTriggers();
            DrawExitLinks();
            DrawMarkers();
            DrawObjectHighlight();
            DrawResult();
            DrawCoverageOutliers();
            DrawSpawnOverlay();
            DrawPlayerPaths();
            DrawBossOverlay();
            DrawModuleArena();
            DrawBoundsPreview();
            DrawVertexTool();
            DrawManualPolygons();
            DrawCentre();
            DrawPlayer();
            HandleCanvasInput();
            DrawObjectPopup();
            DrawHover();
        }
        else
        {
            _canvas.Text(_canvas.Center, _loadTask != null ? "loading..." : "no zone loaded", Colors.TextColor1);
        }
        _canvas.End();
    }

    // the floor piece under each triangle of a mesh, once per model; null when no piece overlaps the mesh
    private short[]? MeshPieces(int m)
    {
        var model = _session!.Model;
        if (_meshPieceCache.TryGetValue(m, out var e) && e.pieces == model.FloorPieces.Count)
        {
            return e.piece;
        }
        var scene = _scene!;
        var mesh = scene.Meshes[m];
        var b = mesh.WorldBounds;
        short[]? r = null;
        if (model.FloorPieces.Exists(p => p.Bounds.Max.X >= b.Min.X && p.Bounds.Min.X <= b.Max.X && p.Bounds.Max.Z >= b.Min.Z && p.Bounds.Min.Z <= b.Max.Z))
        {
            var tris = scene.Triangles.Span;
            r = new short[mesh.TriCount];
            for (var i = 0; i < mesh.TriCount; ++i)
            {
                r[i] = (short)model.FloorPieceAt(tris[mesh.TriStart + i].Centroid);
            }
        }
        _meshPieceCache[m] = (model.FloorPieces.Count, r);
        return r;
    }

    // one hue per floor group, pieces of a group a step apart; gone pieces (at the scrub time) fade
    private uint[] PieceFills()
    {
        var model = _session!.Model;
        if (_pieceFillsModel == model && _pieceFillsTimeline == _timeline && _pieceFillsT == _timelineT && _pieceFills.Length == model.FloorPieces.Count)
        {
            return _pieceFills;
        }
        var present = _timeline?.PiecesPresentAt(_timelineT);
        var fills = new uint[model.FloorPieces.Count];
        for (var i = 0; i < fills.Length; ++i)
        {
            var p = model.FloorPieces[i];
            var indexInGroup = model.FloorGroups[p.Group].Pieces.IndexOf(i);
            fills[i] = IndexColor(p.Group, 0.85f, 0.75f + 0.25f * ((indexInGroup % 3) / 2f), ZoneSceneTimelineFile.PieceGone(present, p) ? 0.10f : 0.42f, 0.15f);
        }
        _pieceFills = fills;
        _pieceFillsModel = model;
        _pieceFillsTimeline = _timeline;
        _pieceFillsT = _timelineT;
        return fills;
    }

    // a distinct hue per index (golden ratio steps) as a packed colour
    private static uint IndexColor(int index, float sat, float val, float alpha, float hueOffset = 0f)
    {
        var rgb = HsvToRgb((index * 0.618034f + hueOffset) % 1f, sat, val);
        return ImGui.ColorConvertFloat4ToU32(new Vector4(rgb, alpha));
    }

    private Dictionary<int, int> PopByMarker()
    {
        var model = _session!.Model;
        if (_popByMarkerModel != model)
        {
            _popByMarker.Clear();
            for (var i = 0; i < model.PopPoints.Count; ++i)
            {
                _popByMarker[model.PopPoints[i].MarkerIndex] = i;
            }
            _popByMarkerModel = model;
        }
        return _popByMarker;
    }

    // everything the canvas remembers about the loaded scene: hover, anchors, strokes and selections; a new zone starts clean
    private void ResetCanvasState()
    {
        _hoverTri = _anchorTri = -1;
        _anchorVertexValid = false;
        _pickHits.Clear();
        _scratchHits.Clear();
        _pickCycle = 0;
        _snapValid = false;
        _hoveredSeal = _hoverBox = -1;
        _sealBoxes.Clear();
        _draggingCentre = _brushStroke = false;
        _layerRefValid = false;
        _hoverVertexPoly = _hoverVertexContour = _hoverVertexIndex = _hoverDeletedVertex = -1;
        _hoverObject = _selectedObject = _popupObject = ObjectRef.None;
        _openObjectPopup = false;
        _stateEdit = 0;
    }

    private void InvalidateSceneCaches()
    {
        _meshDrawCache.Clear();
        _meshRecordEntries = 0;
        ++_meshViewGen;
        _meshPieceCache.Clear();
        _meshSelectedCountsVersion = -1;
        _nodePositionCache.Clear();
    }

    private void RefreshPicker()
    {
        if (_picker == null || _picker.Scene != _scene)
        {
            _picker = new(_scene!);
        }
        else
        {
            _picker.Refresh();
        }
    }

    // the floor settings that decide a triangle's class, once per frame; the previous string is kept while it reads the same so the per-mesh compares are reference hits
    private string FloorSettingsKey()
    {
        var s = _session!.Settings;
        var key = $"{string.Join(",", s.FloorMaterials)}|{s.FloorMatchMode}|{s.MaxSlopeDeg}|{s.ExcludeUnwalkableMaterials}";
        if (key != _floorSettingsKey)
        {
            _floorSettingsKey = key;
        }
        return _floorSettingsKey;
    }

    private MeshDraw MeshClasses(int m, string key, bool forcedFloor, bool excluded)
    {
        var scene = _scene!;
        var mesh = scene.Meshes[m];
        if (_meshDrawCache.TryGetValue(m, out var d))
        {
            if (d.Forced == forcedFloor && d.Excluded == excluded && ReferenceEquals(d.Key, key) && d.TriStart == mesh.TriStart && d.Cls.Length == mesh.TriCount)
            {
                return d;
            }
        }
        else
        {
            d = _meshDrawCache[m] = new();
        }
        var settings = _session!.Settings;
        var tris = scene.Triangles.Span;
        var minNormalY = settings.MinNormalY;
        var n = mesh.TriCount;
        var cls = new byte[n];
        Span<int> counts = [0, 0, 0];
        for (var i = 0; i < n; ++i)
        {
            ref readonly var t = ref tris[mesh.TriStart + i];
            var c = t.NormalY < minNormalY ? (byte)2 : (forcedFloor || settings.FloorMatches(t)) && !excluded ? (byte)1 : (byte)0;
            cls[i] = c;
            ++counts[c];
        }
        // buckets in the order floor (1), other (0), wall (2)
        Span<int> bucketOf = [1, 0, 2];
        Span<int> fill = [0, counts[1], counts[1] + counts[0]];
        for (var b = 0; b < 3; ++b)
        {
            d.BucketStart[b] = fill[b];
            d.BucketMinX[b] = d.BucketMinZ[b] = float.MaxValue;
            d.BucketMaxX[b] = d.BucketMaxZ[b] = float.MinValue;
        }
        d.BucketStart[3] = n;
        var order = new int[n];
        var mb = mesh.WorldBounds;
        var contained = true;
        for (var i = 0; i < n; ++i)
        {
            ref readonly var t = ref tris[mesh.TriStart + i];
            var b = bucketOf[cls[i]];
            order[fill[b]++] = i;
            var minX = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X));
            var maxX = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
            var minZ = MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z));
            var maxZ = MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z));
            contained &= minX >= mb.Min.X && maxX <= mb.Max.X && minZ >= mb.Min.Z && maxZ <= mb.Max.Z;
            if (minX < d.BucketMinX[b])
            {
                d.BucketMinX[b] = minX;
            }
            if (maxX > d.BucketMaxX[b])
            {
                d.BucketMaxX[b] = maxX;
            }
            if (minZ < d.BucketMinZ[b])
            {
                d.BucketMinZ[b] = minZ;
            }
            if (maxZ > d.BucketMaxZ[b])
            {
                d.BucketMaxZ[b] = maxZ;
            }
        }
        d.Key = key;
        d.Forced = forcedFloor;
        d.Excluded = excluded;
        d.TriStart = mesh.TriStart;
        d.Cls = cls;
        d.Order = order;
        d.Contained = contained;
        d.ViewGen = -1;
        return d;
    }

    // exactly the per-triangle view test: the XZ extent of the three vertices against the view
    private bool TriangleVisible(in WorldTriangle t)
    {
        var minX = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X));
        var maxX = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
        var minZ = MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z));
        var maxZ = MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z));
        return _canvas.IsVisible(minX, minZ, maxX, maxZ);
    }

    private void AddRecordEntry(MeshDraw d, int local, int ordinal, in WorldTriangle t)
    {
        var e = d.Count;
        if (e == d.Local.Length)
        {
            var cap = Math.Max(64, e * 2);
            _meshRecordEntries += cap - e;
            Array.Resize(ref d.Local, cap);
            Array.Resize(ref d.Ordinal, cap);
            Array.Resize(ref d.Verts, cap * 3);
        }
        d.Local[e] = local;
        d.Ordinal[e] = ordinal;
        d.Verts[3 * e] = _canvas.ToScreen(t.A);
        d.Verts[3 * e + 1] = _canvas.ToScreen(t.B);
        d.Verts[3 * e + 2] = _canvas.ToScreen(t.C);
        d.Count = e + 1;
    }

    // the visible triangles of a mesh under the current view, in mesh order, as far as the triangle budget lets the mesh go (allowance = visible
    // triangles it may still process). Without edges only floor triangles are drawn: when the budget cannot cut inside the mesh the floor bucket is
    // walked alone and the other buckets are only counted (by their bounds where those decide it)
    private void BuildMeshRecord(MeshDraw d, ZoneMeshInstance mesh, bool edges, int allowance)
    {
        var tris = _scene!.Triangles.Span;
        var mb = mesh.WorldBounds;
        var inside = d.Contained && _canvas.IsInsideView(mb.Min.X, mb.Min.Z, mb.Max.X, mb.Max.Z);
        var n = mesh.TriCount;
        d.ViewGen = _meshViewGen;
        d.Edges = edges;
        d.Count = 0;
        if (!edges && n <= allowance)
        {
            d.Ordered = false;
            var visible = 0;
            for (var b = 0; b < 3; ++b)
            {
                var start = d.BucketStart[b];
                var end = d.BucketStart[b + 1];
                if (start == end || !_canvas.IsVisible(d.BucketMinX[b], d.BucketMinZ[b], d.BucketMaxX[b], d.BucketMaxZ[b]))
                {
                    continue; // every triangle of the bucket fails the same test
                }
                var all = inside || d.Contained && _canvas.IsInsideView(d.BucketMinX[b], d.BucketMinZ[b], d.BucketMaxX[b], d.BucketMaxZ[b]);
                if (all && b != 0)
                {
                    visible += end - start;
                    continue;
                }
                for (var k = start; k < end; ++k)
                {
                    var local = d.Order[k];
                    ref readonly var t = ref tris[mesh.TriStart + local];
                    if (all || TriangleVisible(t))
                    {
                        ++visible;
                        if (b == 0)
                        {
                            AddRecordEntry(d, local, 0, t);
                        }
                    }
                }
            }
            d.Processed = visible;
            d.Complete = true;
            return;
        }
        d.Ordered = true;
        d.Complete = true;
        var count = 0;
        for (var local = 0; local < n; ++local)
        {
            ref readonly var t = ref tris[mesh.TriStart + local];
            if (!inside && !TriangleVisible(t))
            {
                continue;
            }
            if (edges || d.Cls[local] == 1)
            {
                AddRecordEntry(d, local, count, t);
            }
            if (++count >= allowance)
            {
                d.Complete = local == n - 1;
                break;
            }
        }
        d.Processed = count;
    }

    // records of meshes that were not drawn this frame are dropped once the held capacity outgrows a few budgets
    private void SweepMeshRecords()
    {
        if (_meshRecordEntries <= 4 * TriangleBudget)
        {
            return;
        }
        var total = 0;
        foreach (var d in _meshDrawCache.Values)
        {
            if (d.LastFrame != _meshDrawFrame)
            {
                d.Local = [];
                d.Ordinal = [];
                d.Verts = [];
                d.Count = 0;
                d.ViewGen = -1;
            }
            total += d.Local.Length;
        }
        _meshRecordEntries = total;
    }

    // selected triangles per mesh, recounted only when the selection changes
    private int MeshSelectedCount(int m)
    {
        var scene = _scene!;
        if (_meshSelectedCountsVersion != _selection.Version || _meshSelectedCounts.Length != scene.Meshes.Count)
        {
            _meshSelectedCounts = new int[scene.Meshes.Count];
            var tris = scene.Triangles.Span;
            foreach (var t in _selection.Selected)
            {
                if (t >= 0 && t < tris.Length)
                {
                    ++_meshSelectedCounts[tris[t].MeshIndex];
                }
            }
            _meshSelectedCountsVersion = _selection.Version;
        }
        return _meshSelectedCounts[m];
    }

    private void DrawMeshes()
    {
        var scene = _scene!;
        var session = _session!;
        var floorFill = UICanvas2D.WithAlpha(Colors.CollisionColor1, 0x28);
        var floorEdge = UICanvas2D.WithAlpha(Colors.CollisionColor2, 0x60);
        var otherEdge = UICanvas2D.WithAlpha(Colors.Shadows, 0x70);
        var wallEdge = UICanvas2D.WithAlpha(Colors.CollisionColor3, 0x50);
        var drawEdges = _canvas.Zoom >= 6f;
        var lod = _canvas.Zoom < 2f;
        var settingsKey = FloorSettingsKey();
        var pieceFills = _showFloorPieces && session.Model.FloorPieces.Count > 0 ? PieceFills() : null;
        var view = _canvas.View;
        var viewChanged = view != _meshView;
        if (viewChanged)
        {
            _meshView = view;
            ++_meshViewGen;
        }
        ++_meshDrawFrame;
        for (var m = 0; m < scene.Meshes.Count; ++m)
        {
            var mesh = scene.Meshes[m];
            if (mesh.TriCount == 0 || !_canvas.IsVisible(mesh.WorldBounds.Min, mesh.WorldBounds.Max))
            {
                continue;
            }
            if (!scene.IsMeshEnabled(m))
            {
                // the instance exists but its collision is removed in the active scene (a door / seal controlled by an event object)
                if (_ghostInactive && scene.IsMeshPlaced(m))
                {
                    DashedRect(mesh.WorldBounds, UICanvas2D.WithAlpha(Colors.Shadows, 0x70));
                }
                continue;
            }
            var excluded = m < _meshModes.Count && _meshModes[m] == MeshMode.Exclude;
            var forcedFloor = session.FloorMeshes.Contains(m);
            if (lod || !_showAllTriangles || _trianglesDrawn > TriangleBudget)
            {
                _canvas.Rect(mesh.WorldBounds.Min, mesh.WorldBounds.Max, excluded ? UICanvas2D.WithAlpha(Colors.Danger, 0x40) : UICanvas2D.WithAlpha(Colors.Shadows, 0x60));
                continue;
            }
            var d = MeshClasses(m, settingsKey, forcedFloor, excluded);
            var pieces = pieceFills != null ? MeshPieces(m) : null;
            // the mesh processes visible triangles until the running count passes the budget: at most this many
            var allowance = TriangleBudget + 1 - _trianglesDrawn;
            var reusable = d.ViewGen == _meshViewGen && d.Edges == drawEdges && (d.Complete ? d.Ordered || d.Processed <= allowance : d.Processed >= allowance);
            if (!reusable)
            {
                BuildMeshRecord(d, mesh, drawEdges, allowance);
            }
            d.LastFrame = _meshDrawFrame;
            var processed = Math.Min(d.Processed, allowance);
            _trianglesDrawn += processed;
            var entries = d.Count;
            if (d.Ordered && processed < d.Processed)
            {
                // the budget cuts earlier than the record: the entries among the first 'processed' visible triangles
                var lo = 0;
                while (lo < entries)
                {
                    var mid = (lo + entries) >> 1;
                    if (d.Ordinal[mid] < processed)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        entries = mid;
                    }
                }
            }
            ReadOnlySpan<Vector2> verts = d.Verts;
            if (drawEdges)
            {
                // fills and edges interleave in mesh order: translucent edges and fills of neighbouring triangles overlap, so the order is part of the picture
                for (var e = 0; e < entries; ++e)
                {
                    var local = d.Local[e];
                    var c = d.Cls[local];
                    var va = verts[3 * e];
                    var vb = verts[3 * e + 1];
                    var vc = verts[3 * e + 2];
                    if (c == 1)
                    {
                        var piece = pieces != null ? pieces[local] : -1;
                        _canvas.ScreenTriangleFilled(va, vb, vc, piece >= 0 ? pieceFills![piece] : floorFill);
                        _canvas.ScreenTriangle(va, vb, vc, floorEdge);
                    }
                    else
                    {
                        _canvas.ScreenTriangle(va, vb, vc, c == 2 ? wallEdge : otherEdge);
                    }
                }
            }
            else if (pieces == null)
            {
                _canvas.ScreenTrianglesFilled(verts[..(3 * entries)], floorFill);
            }
            else
            {
                // runs of one fill, in mesh order
                var runStart = 0;
                var runColor = 0u;
                for (var e = 0; e < entries; ++e)
                {
                    var piece = pieces[d.Local[e]];
                    var color = piece >= 0 ? pieceFills![piece] : floorFill;
                    if (e == 0)
                    {
                        runColor = color;
                    }
                    else if (color != runColor)
                    {
                        _canvas.ScreenTrianglesFilled(verts[(3 * runStart)..(3 * e)], runColor);
                        runStart = e;
                        runColor = color;
                    }
                }
                _canvas.ScreenTrianglesFilled(verts[(3 * runStart)..(3 * entries)], runColor);
            }
        }
        if (viewChanged)
        {
            SweepMeshRecords();
        }
    }

    private void DrawSelection()
    {
        if (!_showSelected)
        {
            return;
        }
        FillTriangles(_selection.Selected, UICanvas2D.WithAlpha(Colors.Safe, 0x70));
        FillTriangles(_session!.LastAutoResult, UICanvas2D.WithAlpha(Colors.Danger, 0x50), _selection.Selected);
        // rim slopes the last recompute extended the floor across (only the part inside the rim band ends up in the polygon)
        FillTriangles(_session.RimTriangles, UICanvas2D.WithAlpha(Colors.Vulnerable, 0x50));
    }

    private void FillTriangles(HashSet<int> tris, uint color, HashSet<int>? skip = null)
    {
        var span = _scene!.Triangles.Span;
        foreach (var i in tris)
        {
            if (skip != null && skip.Contains(i))
            {
                continue;
            }
            ref readonly var t = ref span[i];
            var b = t.Bounds;
            if (_canvas.IsVisible(b.Min, b.Max))
            {
                _canvas.TriangleFilled(t.A, t.B, t.C, color);
            }
        }
    }

    // every box collider: floor boxes (green when selected, unioned into the arena), obstacle boxes (props without a mesh, cut out of the
    // arena unless ignored) and the rest as a thin outline so the author can see what is there and read its material
    private void DrawBoxes()
    {
        var scene = _scene!;
        var session = _session!;
        var s = session.Settings;
        _sealBoxes.Clear();
        for (var i = 0; i < session.Seals.Count; ++i)
        {
            _sealBoxes.Add(session.Seals[i].BoxIndex);
        }
        var floorFill = UICanvas2D.WithAlpha(Colors.CollisionColor1, 0x30);
        var selectedFill = UICanvas2D.WithAlpha(Colors.Safe, 0x70);
        var floorEdge = UICanvas2D.WithAlpha(Colors.CollisionColor2, 0xA0);
        var obstacleFill = UICanvas2D.WithAlpha(Colors.Vulnerable, 0x40);
        var obstacleEdge = UICanvas2D.WithAlpha(Colors.Vulnerable, 0xC0);
        var ignoredEdge = UICanvas2D.WithAlpha(Colors.Shadows, 0xA0);
        var otherEdge = UICanvas2D.WithAlpha(Colors.Shadows, 0x60);
        for (var b = 0; b < scene.Boxes.Count; ++b)
        {
            var box = scene.Boxes[b];
            if (_sealBoxes.Contains(b) || box.Kind != LgbColliderKind.Box || !_canvas.IsVisible(box.WorldBounds.Min, box.WorldBounds.Max))
            {
                continue;
            }
            if (!scene.IsBoxEnabled(b))
            {
                if (_ghostInactive && scene.IsBoxPlaced(b))
                {
                    BoxTop(box.Corners, UICanvas2D.WithAlpha(Colors.Shadows, 0x90), 1f, true);
                }
                continue;
            }
            var c = box.Corners;
            var hovered = b == _hoverBox;
            if (s.BoxIsFloor(box))
            {
                BoxTopFill(c, session.SelectedFloorBoxes.Contains(b) ? selectedFill : floorFill);
                BoxTop(c, hovered ? Colors.PlayerInteresting : floorEdge, hovered ? 2f : 1f);
            }
            else if (s.BoxIsObstacle(box))
            {
                var ignored = session.IgnoredBoxes.Contains(b);
                if (!ignored)
                {
                    BoxTopFill(c, obstacleFill);
                }
                BoxTop(c, hovered ? Colors.PlayerInteresting : ignored ? ignoredEdge : obstacleEdge, hovered ? 2f : 1f);
            }
            else
            {
                BoxTop(c, hovered ? Colors.PlayerInteresting : otherEdge, hovered ? 2f : 1f);
            }
        }
    }

    // the top face of a box collider: corners 2,3,7,6 of the unit cube (+Y)
    private void BoxTop(Vector3[] c, uint color, float thickness, bool dashed = false)
    {
        if (dashed)
        {
            DashedLine(c[2], c[3], color, thickness);
            DashedLine(c[3], c[7], color, thickness);
            DashedLine(c[7], c[6], color, thickness);
            DashedLine(c[6], c[2], color, thickness);
        }
        else
        {
            _canvas.Line(c[2], c[3], color, thickness);
            _canvas.Line(c[3], c[7], color, thickness);
            _canvas.Line(c[7], c[6], color, thickness);
            _canvas.Line(c[6], c[2], color, thickness);
        }
    }

    private void BoxTopFill(Vector3[] c, uint color)
    {
        _canvas.TriangleFilled(c[2], c[3], c[7], color);
        _canvas.TriangleFilled(c[2], c[7], c[6], color);
    }

    // the box under the cursor: enabled, not a seal, XZ hull contains the point; the smallest footprint wins so the model box inside a wall box is reachable
    private void UpdateHoverBox()
    {
        _hoverBox = -1;
        if (_scene == null || _session == null || !_canvas.Hovered || _canvas.Panning)
        {
            return;
        }
        var mw = _canvas.MouseWorld;
        var best = float.MaxValue;
        for (var b = 0; b < _scene.Boxes.Count; ++b)
        {
            var box = _scene.Boxes[b];
            if (!_scene.IsBoxEnabled(b) || box.Kind != LgbColliderKind.Box || !box.WorldBounds.ContainsXZ(mw.X, mw.Z) || _sealBoxes.Contains(b))
            {
                continue;
            }
            var hull = box.FootprintXZ;
            if (hull.Count < 3 || Clipper.PointInPolygon(new Point64((long)Math.Round(mw.X * BoxFootprintOps.DefaultScale), (long)Math.Round(mw.Z * BoxFootprintOps.DefaultScale)), hull) == PointInPolygonResult.IsOutside)
            {
                continue;
            }
            var area = (box.WorldBounds.Max.X - box.WorldBounds.Min.X) * (box.WorldBounds.Max.Z - box.WorldBounds.Min.Z);
            if (area < best)
            {
                best = area;
                _hoverBox = b;
            }
        }
    }

    // B key / Pick click on an obstacle box: switch it between cut and ignored
    private void ToggleHoveredBox()
    {
        if (_hoverBox < 0 || _session == null || _scene == null)
        {
            return;
        }
        var box = _scene.Boxes[_hoverBox];
        if (_session.Settings.BoxIsFloor(box))
        {
            if (!_session.SelectedFloorBoxes.Remove(_hoverBox))
            {
                _session.SelectedFloorBoxes.Add(_hoverBox);
            }
        }
        else if (!_session.IgnoredBoxes.Remove(_hoverBox))
        {
            _session.IgnoredBoxes.Add(_hoverBox);
        }
        MarkResultDirty();
    }

    private string BoxDescription(int b)
    {
        var scene = _scene!;
        var s = _session!.Settings;
        var box = scene.Boxes[b];
        var role = s.BoxIsFloor(box) ? (_session.SelectedFloorBoxes.Contains(b) ? "floor box (selected)" : "floor box") : s.BoxIsObstacle(box) ? (_session.IgnoredBoxes.Contains(b) ? "obstacle box (ignored)" : "obstacle box (cut)") : "box (not floor, not in the obstacle material list)";
        return $"{role}: material 0x{box.MatValue:X}/0x{box.MatMask:X} {(box.IsAnalytic ? "model collider (BgPart analytic)" : "CollisionBox instance")}\nlayout 0x{box.LayoutObjectId:X16} layer '{scene.Layers[box.LayerIndex].Name}'\ncentre ({box.Center.X:f2}, {box.Center.Y:f2}, {box.Center.Z:f2}) half ({box.HalfExtents.X:f2}, {box.HalfExtents.Y:f2}, {box.HalfExtents.Z:f2}) y {box.WorldBounds.Min.Y:f1}..{box.WorldBounds.Max.Y:f1}\nB toggles cut/ignored (floor box: selected); a Pick click does the same when no triangle is under the cursor";
    }

    private void DrawMarkers()
    {
        if (!_showMarkers)
        {
            return;
        }
        var scene = _scene!;
        var session = _session!;
        var model = session.Model;
        var popByMarker = PopByMarker();
        for (var i = 0; i < scene.Markers.Count; ++i)
        {
            var m = scene.Markers[i];
            if (m.IsTrigger || !scene.Layers[m.LayerIndex].Enabled || !_canvas.IsVisible(m.Position.X - 1f, m.Position.Z - 1f, m.Position.X + 1f, m.Position.Z + 1f))
            {
                continue;
            }
            var eobj = m.Type == (int)LgbInstanceType.EventObject ? model.EventObjectByKey.GetValueOrDefault(m.InstanceKey, -1) : -1;
            var pop = m.Type == (int)LgbInstanceType.PopRange ? popByMarker.GetValueOrDefault(i, -1) : -1;
            var r = eobj >= 0 ? new ObjectRef(ObjectKind.EObj, eobj) : pop >= 0 ? new ObjectRef(ObjectKind.Pop, pop) : ObjectRef.None;
            var hot = r.Valid && (r == _hoverObject || r == _selectedObject);
            if (_canvas.Zoom < 4f && !hot)
            {
                continue;
            }
            var chosen = session.LastEstimate.MarkerIndex == i;
            var color = hot || chosen ? Colors.PlayerInteresting : m.Type switch
            {
                (int)LgbInstanceType.EventObject => UICanvas2D.WithAlpha(Colors.Other4, 0xC0),
                (int)LgbInstanceType.PopRange => UICanvas2D.WithAlpha(Colors.Other3, 0xA0),
                _ => UICanvas2D.WithAlpha(Colors.Other4, 0x80),
            };
            var p = new WPos(m.Position.X, m.Position.Z);
            var sp = _canvas.ToScreen(p);
            if (eobj >= 0)
            {
                var eo = model.EventObjects[eobj];
                if (eo.Role is ZoneObjectRole.Entrance or ZoneObjectRole.Exit or ZoneObjectRole.Warp or ZoneObjectRole.Shortcut)
                {
                    // the run's waypoints are always labelled and drawn bigger
                    hot = true;
                    color = hot && (r == _hoverObject || r == _selectedObject) ? Colors.PlayerInteresting : eo.Role switch
                    {
                        ZoneObjectRole.Entrance => Colors.Safe,
                        ZoneObjectRole.Exit => Colors.Danger,
                        ZoneObjectRole.Warp => Colors.Other2,
                        _ => Colors.Other3,
                    };
                    _canvas.ScreenCircle(sp, 7f, color, 2.5f);
                }
                if (eo.ControlsCollision)
                {
                    // controllers draw as squares; a hollow square when the collision they control is off in the scene
                    var on = eo.BoundNode >= 0 && scene.IsNodeActive(eo.BoundNode);
                    ImGui.GetWindowDrawList().AddRect(sp - new Vector2(5f, 5f), sp + new Vector2(5f, 5f), color, 0f, ImDrawFlags.None, on ? 2.5f : 1f);
                }
                else
                {
                    _canvas.ScreenCircle(sp, 4f, color, 2f);
                }
            }
            else if (pop >= 0)
            {
                _canvas.ScreenCircle(sp, 4f, color, 1.5f);
                _canvas.Crosshair(p, 4f, color, 1f);
            }
            else
            {
                _canvas.ScreenCircle(sp, 4f, color, 2f);
            }
            if (hot || _canvas.Zoom >= 8f)
            {
                string label;
                if (eobj >= 0)
                {
                    var eo = model.EventObjects[eobj];
                    var st = session.EObjState(eobj);
                    label = $"EObj {eo.Label}{(st is { } v ? $" [s={v}]" : "")}{(eo.Role != ZoneObjectRole.None ? $" {RoleName(eo.Role)}" : "")}";
                }
                else if (pop >= 0)
                {
                    label = $"pop {model.PopPoints[pop].PopType}";
                }
                else
                {
                    label = $"{m.TypeName} {i}{(m.Name.Length > 0 ? $" {m.Name}" : "")}";
                }
                _canvas.Text(p, label, color, new(8f, -6f));
            }
        }
        // exits are drawn at every zoom, they anchor the last room
        for (var i = 0; i < model.Exits.Count; ++i)
        {
            var e = model.Exits[i];
            var r = new ObjectRef(ObjectKind.Exit, i);
            var color = r == _hoverObject || r == _selectedObject ? Colors.PlayerInteresting : Colors.Other3;
            var p = new WPos(e.Position.X, e.Position.Z);
            _canvas.ScreenCircle(_canvas.ToScreen(p), 6f, color, 2f);
            _canvas.Text(p, $"exit {e.ExitIndex}", color, new(8f, -6f));
        }
    }

    private void DrawSeals()
    {
        if (!_showSeals)
        {
            return;
        }
        var session = _session!;
        var scene = _scene!;
        var centre = new WPos(session.Centre.X, session.Centre.Z);
        var scale = (float)BoxFootprintOps.DefaultScale;
        for (var i = 0; i < session.Seals.Count; ++i)
        {
            var seal = session.Seals[i];
            var box = scene.Boxes[seal.BoxIndex];
            if (!_canvas.IsVisible(box.WorldBounds.Min, box.WorldBounds.Max))
            {
                continue;
            }
            var active = session.ActiveSeals.Contains(seal.BoxIndex);
            var inScene = scene.IsBoxEnabled(seal.BoxIndex);
            var chosen = i == session.LastEstimate.SealA || i == session.LastEstimate.SealB;
            var color = i == _hoveredSeal ? Colors.PlayerInteresting : !inScene ? UICanvas2D.WithAlpha(Colors.Shadows, 0xA0) : active ? Colors.Danger : UICanvas2D.WithAlpha(Colors.Danger, 0x60);
            var fp = seal.FootprintXZ;
            if (!inScene && !_ghostInactive && i != _hoveredSeal)
            {
                continue;
            }
            for (var k = 0; k < fp.Count; ++k)
            {
                var a = fp[k];
                var b = fp[(k + 1) % fp.Count];
                if (inScene)
                {
                    _canvas.Line(new WPos(a.X / scale, a.Y / scale), new WPos(b.X / scale, b.Y / scale), color, chosen ? 4f : 2f);
                }
                else
                {
                    DashedLine(new Vector3(a.X / scale, seal.Center.Y, a.Y / scale), new Vector3(b.X / scale, seal.Center.Y, b.Y / scale), color, 2f);
                }
            }
            if (active)
            {
                var from = new WPos(seal.Center.X, seal.Center.Z);
                var dir = new Vector2(seal.ThinAxisWorld.X, seal.ThinAxisWorld.Z);
                if (dir.LengthSquared() > 1e-6f)
                {
                    dir = Vector2.Normalize(dir);
                    if (Vector2.Dot(dir, new Vector2(centre.X - from.X, centre.Z - from.Z)) < 0f)
                    {
                        dir = -dir;
                    }
                    _canvas.Arrow(from, new WPos(from.X + dir.X * 5f, from.Z + dir.Y * 5f), color, 2f);
                }
            }
            _canvas.Text(new WPos(seal.Center.X, seal.Center.Z), $"seal {i}", color, new(6f, -6f));
        }
        // the pairs: seal to seal or seal to node, the chosen one solid and thick, the rest thin; a single seal's inward side as a short tick
        for (var i = 0; i < session.Pairs.Count; ++i)
        {
            var p = session.Pairs[i];
            var a = session.Seals[p.SealA].Center;
            Vector3 b;
            if (p.SealB >= 0)
            {
                b = session.Seals[p.SealB].Center;
            }
            else if (p.MarkerIndex >= 0)
            {
                b = p.HasTarget ? p.Target : scene.Markers[p.MarkerIndex].Position;
            }
            else
            {
                continue;
            }
            if (!_canvas.IsVisible(MathF.Min(a.X, b.X), MathF.Min(a.Z, b.Z), MathF.Max(a.X, b.X), MathF.Max(a.Z, b.Z)))
            {
                continue;
            }
            var chosen = i == session.PairIndex;
            var manual = p.Kind.StartsWith("manual");
            var col = UICanvas2D.WithAlpha(manual ? Colors.PlayerInteresting : Colors.Danger, chosen ? (byte)0xC0 : (byte)0x50);
            if (chosen)
            {
                _canvas.Line(a, b, col, 2f);
            }
            else
            {
                DashedLine(a, b, col, 1f, 8f);
            }
            if (p.MarkerIndex >= 0)
            {
                _canvas.ScreenCircle(_canvas.ToScreen(b), 5f, col, 1.5f);
            }
        }
    }

    private void DrawResult()
    {
        if (!_showResult)
        {
            return;
        }
        var preview = _pipeline.Preview;
        for (var i = 0; i < preview.Count; ++i)
        {
            DrawContour(preview[i].Outer, Colors.Border, 3f, i);
            var holes = preview[i].Holes;
            for (var h = 0; h < holes.Count; ++h)
            {
                DrawContour(holes[h], Colors.Border, 2f, i);
            }
        }
    }

    private void DrawContour(ArenaPolygonPipeline.PreviewContour c, uint color, float thickness, int index)
    {
        var hovered = ReferenceEquals(c, _pipeline.HoverContour);
        if (!c.Enabled && !hovered)
        {
            color = UICanvas2D.WithAlpha(Colors.Shadows, 0x80);
        }
        if (hovered)
        {
            color = Colors.PlayerInteresting;
            thickness += 2f;
        }
        _canvas.Poly(c.SimplifiedDraw, color, thickness);
        if (_canvas.Zoom >= 8f || _tool == Tool.Vertex)
        {
            var pts = c.SimplifiedDraw;
            for (var i = 0; i < pts.Length; ++i)
            {
                _canvas.ScreenCircleFilled(_canvas.ToScreen(pts[i]), _tool == Tool.Vertex ? 3.5f : 2.5f, color);
            }
        }
        if (_showRawOutline)
        {
            _canvas.Poly(c.Raw, UICanvas2D.WithAlpha(Colors.Object, 0xA0), 1f);
        }
        if (hovered)
        {
            _canvas.Text(c.Centroid, $"#{index}", color);
        }
    }

    // vertex tool: deleted vertices as red crosses, the hovered vertex ringed
    private void DrawVertexTool()
    {
        if (_tool != Tool.Vertex)
        {
            return;
        }
        var deleted = _pipeline.DeletedVertices;
        for (var i = 0; i < deleted.Count; ++i)
        {
            var s = _canvas.ToScreen(deleted[i]);
            var color = i == _hoverDeletedVertex ? Colors.PlayerInteresting : Colors.Danger;
            _canvas.ScreenCircle(s, i == _hoverDeletedVertex ? 7f : 4f, color, 1.5f);
        }
        if (HoveredContour() is { } c && _hoverVertexIndex < c.SimplifiedDraw.Length)
        {
            var pts = c.SimplifiedDraw;
            var p = pts[_hoverVertexIndex];
            _canvas.ScreenCircle(_canvas.ToScreen(p), 7f, Colors.PlayerInteresting, 2f);
            var prev = pts[(_hoverVertexIndex + pts.Length - 1) % pts.Length];
            var next = pts[(_hoverVertexIndex + 1) % pts.Length];
            _canvas.Line(prev, next, UICanvas2D.WithAlpha(Colors.PlayerInteresting, 0xA0), 1.5f); // where the edge goes after deleting
            _canvas.Text(new WPos(p.X, p.Z), $"({p.X:f3}, {p.Z:f3}) click deletes", Colors.PlayerInteresting, new(10f, -18f));
        }
        else
        {
            _hoverVertexPoly = _hoverVertexContour = _hoverVertexIndex = -1; // the preview shrank under last frame's hover
        }
    }

    // the preview contour the vertex hover points at; null when there is none or a recompute shrank the preview since the hover was taken
    private ArenaPolygonPipeline.PreviewContour? HoveredContour()
    {
        var preview = _pipeline.Preview;
        if (_hoverVertexPoly < 0 || _hoverVertexPoly >= preview.Count)
        {
            return null;
        }
        var poly = preview[_hoverVertexPoly];
        if (_hoverVertexContour < 0)
        {
            return poly.Outer;
        }
        return _hoverVertexContour < poly.Holes.Count ? poly.Holes[_hoverVertexContour] : null;
    }

    // nearest preview vertex / deleted vertex to the cursor within a screen radius
    private void UpdateVertexHover()
    {
        _hoverVertexPoly = _hoverVertexContour = _hoverVertexIndex = -1;
        _hoverDeletedVertex = -1;
        if (_tool != Tool.Vertex || !_canvas.Hovered)
        {
            return;
        }
        var mouse = _canvas.ToScreen(_canvas.MouseWorld);
        var best = 10f * 10f;
        var deleted = _pipeline.DeletedVertices;
        for (var i = 0; i < deleted.Count; ++i)
        {
            var d = (_canvas.ToScreen(deleted[i]) - mouse).LengthSquared();
            if (d < best)
            {
                best = d;
                _hoverDeletedVertex = i;
            }
        }
        var preview = _pipeline.Preview;
        for (var p = 0; p < preview.Count; ++p)
        {
            ScanContourVertices(preview[p].Outer, p, -1, mouse, ref best);
            var holes = preview[p].Holes;
            for (var h = 0; h < holes.Count; ++h)
            {
                ScanContourVertices(holes[h], p, h, mouse, ref best);
            }
        }
        if (_hoverVertexPoly >= 0)
        {
            _hoverDeletedVertex = -1;
        }
    }

    private void ScanContourVertices(ArenaPolygonPipeline.PreviewContour c, int poly, int hole, Vector2 mouse, ref float best)
    {
        var pts = c.SimplifiedDraw;
        for (var i = 0; i < pts.Length; ++i)
        {
            var d = (_canvas.ToScreen(pts[i]) - mouse).LengthSquared();
            if (d < best)
            {
                best = d;
                _hoverVertexPoly = poly;
                _hoverVertexContour = hole;
                _hoverVertexIndex = i;
            }
        }
    }

    private void DrawManualPolygons()
    {
        foreach (var p in _manualPolygons)
        {
            _canvas.Poly(CollectionsMarshal.AsSpan(p.Vertices), p.Difference ? Colors.Other2 : Colors.Other3, 2f);
        }
        if (_polygonDraft.Count > 0)
        {
            _canvas.Poly(CollectionsMarshal.AsSpan(_polygonDraft), Colors.Vulnerable, 2f, false);
            _canvas.Line(_polygonDraft[^1], _snapValid ? new WPos(_snapVertex.X, _snapVertex.Z) : _canvas.MouseWorld, UICanvas2D.WithAlpha(Colors.Vulnerable, 0x80), 1f);
            if (_polygonDraft.Count >= 3)
            {
                _canvas.ScreenCircle(_canvas.ToScreen(_polygonDraft[0]), 8f, Colors.Vulnerable, 1.5f); // a click inside closes the draft
            }
        }
    }

    // the local player while standing in the loaded territory: a triangle pointing along the facing, so the canvas can be matched to the world
    private Actor? PlayerInLoadedZone()
    {
        if (_scene == null || _scene.TerritoryId != Service.ClientState.TerritoryType)
        {
            return null;
        }
        return _bmm.WorldState.Party.Player();
    }

    private void DrawPlayer()
    {
        if (!_showPlayer || PlayerInLoadedZone() is not { } player)
        {
            return;
        }
        var pos = player.Position;
        var dir = player.Rotation.ToDirection();
        var side = dir.OrthoL();
        var tip = pos + dir * 1.2f;
        var left = pos - dir * 0.7f + side * 0.6f;
        var right = pos - dir * 0.7f - side * 0.6f;
        var color = Colors.PlayerInteresting;
        var y = player.PosRot.Y;
        _canvas.TriangleFilled(new Vector3(tip.X, y, tip.Z), new Vector3(left.X, y, left.Z), new Vector3(right.X, y, right.Z), UICanvas2D.WithAlpha(color, 0x90));
        _canvas.Line(tip, left, color, 2f);
        _canvas.Line(left, right, color, 2f);
        _canvas.Line(right, tip, color, 2f);
        _canvas.ScreenCircle(_canvas.ToScreen(pos), 5f, color, 1.5f);
        _canvas.Text(pos, $"you ({pos.X:f1}, {pos.Z:f1}) y {player.PosRot.Y:f1} facing {player.Rotation.Deg:f0}", color, new(10f, 10f));
    }

    // the result contours (the same polygon the snippet emits) drawn in the 3D world at the recovered floor height; it follows every recompute
    private void DrawWorldPreview()
    {
        if (!_showWorldPreview || PlayerInLoadedZone() == null || Camera.Instance is not Camera camera)
        {
            return;
        }
        var lift = new Vector3(0f, _worldPreviewLift, 0f);
        var preview = _pipeline.Preview;
        if (_worldPreviewMode == 1)
        {
            // the bounds the snippet builds, through the framework's own constructor (hitbox offsets and its simplification applied)
            if (_pipeline.PreviewBounds() is { } bounds)
            {
                camera.DrawWorldPoly(new Vector3(bounds.Center.X, PreviewFloorY() + _worldPreviewLift, bounds.Center.Z), bounds.Shape, Colors.Border, 2f);
            }
            return;
        }
        for (var i = 0; i < preview.Count; ++i)
        {
            DrawWorldContour(camera, preview[i].Outer, Colors.Safe, lift, i);
            var holes = preview[i].Holes;
            for (var h = 0; h < holes.Count; ++h)
            {
                DrawWorldContour(camera, holes[h], Colors.Danger, lift, i);
            }
        }
    }

    // mean floor height of the enabled result contours (the bounds themselves carry no height unless the floor is flat)
    private float PreviewFloorY()
    {
        var sum = 0f;
        var n = 0;
        var preview = _pipeline.Preview;
        for (var i = 0; i < preview.Count; ++i)
        {
            if (preview[i].Outer.Enabled)
            {
                sum += preview[i].Outer.MeanY;
                ++n;
            }
        }
        return n > 0 ? sum / n : _session?.Centre.Y ?? 0f;
    }

    // the ArenaBoundsCustom preview on the canvas too, so the hitbox offset can be compared with the contours
    private void DrawBoundsPreview()
    {
        if (!_showResult || _worldPreviewMode != 1 || _pipeline.PreviewBounds() is not { } bounds)
        {
            return;
        }
        var parts = bounds.Shape.Parts;
        var color = Colors.Border;
        for (var i = 0; i < parts.Count; ++i)
        {
            var part = parts[i];
            DrawRelContour(part.Exterior, bounds.Center, color, 2f);
            var holeCount = part.HoleStarts.Count;
            for (var h = 0; h < holeCount; ++h)
            {
                DrawRelContour(part.Interior(h), bounds.Center, color, 1.5f);
            }
        }
    }

    private void DrawRelContour(ReadOnlySpan<WDir> contour, WPos center, uint color, float thickness)
    {
        var n = contour.Length;
        if (n == 0)
        {
            return;
        }
        _relContour.Clear();
        for (var i = 0; i < n; ++i)
        {
            _relContour.Add(center + contour[i]);
        }
        _canvas.Poly(CollectionsMarshal.AsSpan(_relContour), color, thickness);
    }

    private void DrawWorldContour(Camera camera, ArenaPolygonPipeline.PreviewContour c, uint color, Vector3 lift, int index)
    {
        var hovered = ReferenceEquals(c, _pipeline.HoverContour);
        if (!c.Enabled && !hovered)
        {
            return;
        }
        if (hovered)
        {
            color = Colors.PlayerInteresting;
            camera.DrawWorldTextBillboard(new Vector3(c.Centroid.X, c.MeanY + 1f, c.Centroid.Z), $"#{index}", color);
        }
        var pts = c.SimplifiedDraw;
        var n = pts.Length;
        for (var i = 0; i < n; ++i)
        {
            camera.DrawWorldLine(pts[i] + lift, pts[(i + 1) % n] + lift, color, hovered ? 4f : 2f);
        }
    }

    private void DrawCentre()
    {
        var session = _session!;
        var c = new WPos(session.Centre.X, session.Centre.Z);
        var color = session.CentreValid ? Colors.PlayerInteresting : UICanvas2D.WithAlpha(Colors.PlayerInteresting, 0x80);
        _canvas.Crosshair(c, 12f, color, 2f);
        _canvas.ScreenCircle(_canvas.ToScreen(c), 6f, color, 2f);
        _canvas.Text(c, $"({c.X:f1}, {c.Z:f1}) y {session.Centre.Y:f1}", color, new(10f, 8f));
        if (session.Settings.MaxRadius > 0f)
        {
            _canvas.Circle(c, session.Settings.MaxRadius, UICanvas2D.WithAlpha(Colors.PlayerInteresting, 0x40), 1f);
        }
    }

    private void HandleCanvasInput()
    {
        var session = _session!;
        var scene = _scene!;
        var picker = _picker!;
        var io = ImGui.GetIO();
        var spaceHeld = _canvas.SpaceHeld;
        var ctrl = CtrlHeld;
        var shift = ShiftHeld;

        // brush radius with shift+wheel
        if (_canvas.Hovered && shift && io.MouseWheel != 0f)
        {
            _brushRadius = Math.Clamp(_brushRadius * MathF.Pow(1.2f, io.MouseWheel), 0.25f, 50f);
        }

        // hover triangle + snap vertex
        _hoverTri = -1;
        _snapValid = false;
        if (_canvas.Hovered && !_canvas.Panning)
        {
            picker.PickTriangle(_canvas.MouseWorld, _pickHits, TriangleAllowed);
            if (_pickHits.Count > 0)
            {
                if (KeyPressed(VkTab))
                {
                    ++_pickCycle;
                }
                // the Tab layer is persistent: it only changes when Tab is pressed, never because the cursor crossed a gap or a click changed the stack
                _hoverTri = _pickHits[_pickCycle % _pickHits.Count];
            }
            _snapValid = picker.NearestVertex(_canvas.MouseWorld, MathF.Max(0.5f, 8f / _canvas.Zoom), TriangleAllowed, out _snapVertex, out _);
        }
        UpdateHoverBox();
        UpdateHoverObject();
        HandleObjectClicks(spaceHeld);

        // any left click on the canvas latches the anchor triangle/vertex for the sidebar actions
        if (_canvas.Hovered && !spaceHeld && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (_hoverTri >= 0)
            {
                _anchorTri = _hoverTri;
            }
            if (_snapValid)
            {
                _anchorVertex = _snapVertex;
                _anchorVertexValid = true;
            }
            // alt+click: exclude / re-include the clicked triangle's mesh; alt+shift+click: mark / unmark it as floor - both repeatable back to back
            if (AltHeld && _hoverTri >= 0)
            {
                if (ShiftHeld)
                {
                    ToggleAnchorMeshMode(MeshMode.Include);
                }
                else
                {
                    ToggleAnchorMeshMode(MeshMode.Exclude);
                }
                return;
            }
        }

        // centre drag: press near the marker, or the centre tool (one shot: the previous tool returns when the drag ends)
        var centreScreen = _canvas.ToScreen(new WPos(session.Centre.X, session.Centre.Z));
        var nearCentre = (_canvas.MouseScreen - centreScreen).Length() < 10f;
        if (!_draggingCentre && _canvas.Hovered && !spaceHeld && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && (_tool == Tool.Centre || nearCentre))
        {
            _draggingCentre = true;
        }
        if (_draggingCentre)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var p = _canvas.MouseWorld;
                if (ctrl)
                {
                    p = new(MathF.Round(p.X * 2f) / 2f, MathF.Round(p.Z * 2f) / 2f);
                }
                session.SetCentre(new(p.X, FloorYAt(new(p.X, session.Centre.Y, p.Z)), p.Z));
            }
            else
            {
                _draggingCentre = false;
                SetCentreTool(false);
                MarkResultDirty();
            }
            return;
        }

        switch (_tool)
        {
            case Tool.Pick:
                if (_hoverObject.Valid && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    break; // the click selected the object
                }
                if (_canvas.Hovered && !spaceHeld && _hoverTri < 0 && _hoverBox >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    ToggleHoveredBox(); // click on a box with no triangle under the cursor toggles it (floor: selected, obstacle: cut/ignored)
                }
                if (_canvas.Hovered && !spaceHeld && _hoverTri >= 0)
                {
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    {
                        if (shift)
                        {
                            _selection.Add(_hoverTri);
                        }
                        else if (ctrl)
                        {
                            _selection.Remove(_hoverTri);
                        }
                        else
                        {
                            _selection.Toggle(_hoverTri);
                        }
                    }
                }
                break;
            case Tool.Rect:
                if (!spaceHeld)
                {
                    _canvas.EnableDragRect(ImGuiMouseButton.Left);
                }
                if (_canvas.Hovered && !spaceHeld && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    LatchLayerReference();
                }
                if (_canvas.DragJustEnded)
                {
                    var min = new WPos(MathF.Min(_canvas.DragStart.X, _canvas.DragEnd.X), MathF.Min(_canvas.DragStart.Z, _canvas.DragEnd.Z));
                    var max = new WPos(MathF.Max(_canvas.DragStart.X, _canvas.DragEnd.X), MathF.Max(_canvas.DragStart.Z, _canvas.DragEnd.Z));
                    _scratch.Clear();
                    picker.CentroidsInRect(min, max, TriangleAllowed, _scratch);
                    ApplySelectionPick(ctrl, ctrl ? "rect remove" : "rect add");
                }
                break;
            case Tool.Brush:
                if (_canvas.Hovered)
                {
                    _canvas.Circle(_canvas.MouseWorld, _brushRadius, UICanvas2D.WithAlpha(Colors.Vulnerable, 0xA0), 1f);
                }
                if (!_brushStroke && _canvas.Hovered && !spaceHeld && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _brushStroke = true;
                    LatchLayerReference();
                    _selection.BeginStroke(ctrl ? "brush remove" : "brush add");
                }
                if (_brushStroke)
                {
                    if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    {
                        _scratch.Clear();
                        picker.CentroidsInCircle(_canvas.MouseWorld, _brushRadius, TriangleAllowed, _scratch);
                        ApplySelectionPick(ctrl, "brush");
                    }
                    else
                    {
                        _brushStroke = false;
                        _selection.EndStroke();
                    }
                }
                break;
            case Tool.Vertex:
                UpdateVertexHover();
                if (_canvas.Hovered && !spaceHeld && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (HoveredContour() is { } c)
                    {
                        if (_hoverVertexIndex < c.Simplified.Length)
                        {
                            _pipeline.DeletedVertices.Add(c.Simplified[_hoverVertexIndex]);
                            _pipeline.RebuildSimplified();
                        }
                    }
                    else if (_hoverDeletedVertex >= 0)
                    {
                        _pipeline.DeletedVertices.RemoveAt(_hoverDeletedVertex);
                        _pipeline.RebuildSimplified();
                    }
                }
                break;
            case Tool.Polygon:
                if (_canvas.Hovered && !spaceHeld && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    var p = _snapValid ? new WPos(_snapVertex.X, _snapVertex.Z) : _canvas.MouseWorld;
                    if (_polygonDraft.Count >= 3 && (_canvas.ToScreen(p) - _canvas.ToScreen(_polygonDraft[0])).Length() < 8f)
                    {
                        FinishPolygonDraft();
                    }
                    else
                    {
                        _polygonDraft.Add(p);
                        _polygonDraftY.Add(_snapValid ? _snapVertex.Y : session.Centre.Y);
                    }
                }
                if (KeyPressed(VkBackspace) && _polygonDraft.Count > 0)
                {
                    _polygonDraft.RemoveAt(_polygonDraft.Count - 1);
                    _polygonDraftY.RemoveAt(_polygonDraftY.Count - 1);
                }
                if (KeyPressed(VkEnter) && _polygonDraft.Count >= 3)
                {
                    FinishPolygonDraft();
                }
                break;
        }
    }

    // the triangles a rect / brush picked into _scratch, filtered by the floor and layer options, added to or removed from the selection
    private void ApplySelectionPick(bool ctrl, string label)
    {
        if (!ctrl && _selectFloorOnly)
        {
            FilterFloorOnly(_scratch);
        }
        if (_selectLayerOnly && _layerRefValid)
        {
            FilterLayer(_scratch, _layerRefY);
        }
        var picked = CollectionsMarshal.AsSpan(_scratch);
        if (ctrl)
        {
            _selection.Apply([], picked, label);
        }
        else
        {
            _selection.Apply(picked, [], label);
        }
    }

    private void FinishPolygonDraft()
    {
        _manualPolygons.Add(new ManualPolygon { Difference = _polygonDraftDifference, Vertices = [.. _polygonDraft], Y = [.. _polygonDraftY] });
        _polygonDraft.Clear();
        _polygonDraftY.Clear();
        MarkResultDirty();
    }

    // the layer a rect/brush works on is the Tab-cycled triangle under the cursor when the drag/stroke starts
    private void LatchLayerReference()
    {
        _layerRefValid = false;
        if (_hoverTri >= 0 && _scene != null)
        {
            var m = _canvas.MouseWorld;
            _layerRefY = _scene.Triangles[_hoverTri].YAt(m.X, m.Z);
            _layerRefValid = true;
        }
    }

    // keep only triangles that are the stacked surface nearest to the reference height at their own centroid, so a rect over a
    // room with a ceiling slab (or a bridge) selects one level instead of every level
    private void FilterLayer(List<int> tris, float refY)
    {
        var picker = _picker!;
        var span = _scene!.Triangles.Span;
        var w = 0;
        for (var i = 0; i < tris.Count; ++i)
        {
            var tri = tris[i];
            var c = span[tri].Centroid;
            picker.PickTriangle(new(c.X, c.Z), _layerHits, TriangleAllowed);
            var best = tri;
            var bestDy = float.MaxValue;
            for (var h = 0; h < _layerHits.Count; ++h)
            {
                var dy = MathF.Abs(span[_layerHits[h]].YAt(c.X, c.Z) - refY);
                if (dy < bestDy)
                {
                    bestDy = dy;
                    best = _layerHits[h];
                }
            }
            if (best == tri || MathF.Abs(c.Y - refY) <= bestDy + 0.01f)
            {
                tris[w++] = tri;
            }
        }
        tris.RemoveRange(w, tris.Count - w);
    }

    // rect/brush add only floor-material (or floor-mesh) triangles with a walkable slope unless the user removes or turns the filter off
    private void FilterFloorOnly(List<int> tris)
    {
        var scene = _scene!;
        var session = _session!;
        var s = session.Settings;
        var minNormalY = s.MinNormalY;
        var span = scene.Triangles.Span;
        var w = 0;
        for (var i = 0; i < tris.Count; ++i)
        {
            ref readonly var t = ref span[tris[i]];
            if (t.NormalY >= minNormalY && (s.FloorMatches(t) || session.FloorMeshes.Contains(t.MeshIndex)))
            {
                tris[w++] = tris[i];
            }
        }
        tris.RemoveRange(w, tris.Count - w);
    }

    private void DrawHover()
    {
        if (_hoverObject.Valid && _scene != null && _session != null && !ImGui.IsPopupOpen(ObjectPopupId))
        {
            var desc = ObjectDescription(_hoverObject);
            if (_hoverTri >= 0)
            {
                ref readonly var ht = ref _scene.Triangles[_hoverTri];
                _canvas.Triangle(ht.A, ht.B, ht.C, UICanvas2D.WithAlpha(Colors.PlayerInteresting, 0x80), 1f);
                desc += $"\n\ntriangle {_hoverTri} under the cursor (material 0x{ht.Effective:X})";
            }
            ImGui.SetTooltip(desc);
            return;
        }
        if (_hoverTri < 0 || _scene == null)
        {
            if (_snapValid)
            {
                _canvas.ScreenCircleFilled(_canvas.ToScreen(_snapVertex), 4f, Colors.Vulnerable);
            }
            if (_hoverBox >= 0 && _scene != null)
            {
                ImGui.SetTooltip(BoxDescription(_hoverBox));
            }
            return;
        }
        ref readonly var t = ref _scene.Triangles[_hoverTri];
        _canvas.Triangle(t.A, t.B, t.C, Colors.PlayerInteresting, 2f);
        if (_snapValid)
        {
            _canvas.ScreenCircleFilled(_canvas.ToScreen(_snapVertex), 4f, Colors.Vulnerable);
        }
        var mesh = _scene.Meshes[t.MeshIndex];
        if (_anchorTri == _hoverTri)
        {
            _canvas.Triangle(t.A, t.B, t.C, Colors.Vulnerable, 3f);
        }
        var sb = new StringBuilder();
        sb.Append($"{mesh.Name} {mesh.PcbPath}").Append('\n');
        sb.Append($"layout 0x{mesh.LayoutObjectId:X16} layer '{_scene.Layers[mesh.LayerIndex].Name}'").Append('\n');
        sb.Append($"material 0x{t.Material:X} (effective 0x{t.Effective:X}) normalY {t.NormalY:f2}").Append('\n');
        sb.Append($"triangle {_hoverTri} (mesh-local {_hoverTri - mesh.TriStart}), {_pickHits.Count} stacked (Tab cycles)").Append('\n');
        sb.Append($"A ({t.A.X:f2}, {t.A.Y:f2}, {t.A.Z:f2})  B ({t.B.X:f2}, {t.B.Y:f2}, {t.B.Z:f2})  C ({t.C.X:f2}, {t.C.Y:f2}, {t.C.Z:f2})");
        if (_snapValid)
        {
            sb.Append('\n').Append($"snap vertex ({_snapVertex.X:f3}, {_snapVertex.Y:f3}, {_snapVertex.Z:f3})");
        }
        if (_selection.Selected.Contains(_hoverTri))
        {
            sb.Append('\n').Append("selected");
        }
        if (_hoverBox >= 0)
        {
            sb.Append('\n').Append(BoxDescription(_hoverBox));
        }
        ImGui.SetTooltip(sb.ToString());
    }
}
