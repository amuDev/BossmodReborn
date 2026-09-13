using Clipper2Lib;
using Dalamud.Bindings.ImGui;
using System.Runtime.InteropServices;

namespace BossMod;

public sealed partial class ZoneArenaEditorWindow
{
    private const int TriangleBudget = 60000;

    private int _hoverTri = -1;
    private int _anchorTri = -1;          // last clicked triangle (used by sidebar actions and shortcuts)
    private Vector3 _anchorVertex;
    private bool _anchorVertexValid;
    private readonly List<int> _pickHits = [];
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

    private List<Path64> ManualUnionPaths()
    {
        List<Path64> result = [];
        foreach (var p in _manualPolygons)
        {
            if (!p.Difference && p.Vertices.Count >= 3)
            {
                result.Add(ToPath(p.Vertices));
            }
        }
        return result;
    }

    private List<Path64> ManualCutPaths()
    {
        List<Path64> result = [];
        foreach (var p in _manualPolygons)
        {
            if (p.Difference && p.Vertices.Count >= 3)
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
            DrawMeshes();
            DrawSelection();
            DrawBoxes();
            DrawSeals();
            DrawMarkers();
            DrawResult();
            DrawVertexTool();
            DrawManualPolygons();
            DrawCentre();
            DrawPlayer();
            HandleCanvasInput();
            DrawHover();
        }
        else
        {
            _canvas.Text(_canvas.Center, _loadTask != null ? "loading..." : "no zone loaded", Colors.TextColor1);
        }
        _canvas.End();
    }

    private void DrawMeshes()
    {
        var scene = _scene!;
        var tris = scene.Triangles.Span;
        var settings = _session!.Settings;
        var minNormalY = settings.MinNormalY;
        var floorFill = UICanvas2D.WithAlpha(Colors.CollisionColor1, 0x28);
        var floorEdge = UICanvas2D.WithAlpha(Colors.CollisionColor2, 0x60);
        var otherEdge = UICanvas2D.WithAlpha(Colors.Shadows, 0x70);
        var wallEdge = UICanvas2D.WithAlpha(Colors.CollisionColor3, 0x50);
        var drawEdges = _canvas.Zoom >= 6f;
        var lod = _canvas.Zoom < 2f;
        for (var m = 0; m < scene.Meshes.Count; ++m)
        {
            var mesh = scene.Meshes[m];
            if (mesh.TriCount == 0 || !scene.IsMeshEnabled(m) || !_canvas.IsVisible(mesh.WorldBounds.Min, mesh.WorldBounds.Max))
            {
                continue;
            }
            var excluded = m < _meshModes.Count && _meshModes[m] == MeshMode.Exclude;
            var forcedFloor = _session.FloorMeshes.Contains(m);
            if (lod || !_showAllTriangles || _trianglesDrawn > TriangleBudget)
            {
                _canvas.Rect(mesh.WorldBounds.Min, mesh.WorldBounds.Max, excluded ? UICanvas2D.WithAlpha(Colors.Danger, 0x40) : UICanvas2D.WithAlpha(Colors.Shadows, 0x60));
                continue;
            }
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                ref readonly var t = ref tris[i];
                var minX = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X));
                var maxX = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
                var minZ = MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z));
                var maxZ = MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z));
                if (!_canvas.IsVisible(minX, minZ, maxX, maxZ))
                {
                    continue;
                }
                ++_trianglesDrawn;
                var floor = t.NormalY >= minNormalY && (forcedFloor || settings.FloorMatches(t));
                if (floor && !excluded)
                {
                    _canvas.TriangleFilled(t.A, t.B, t.C, floorFill);
                    if (drawEdges)
                    {
                        _canvas.Triangle(t.A, t.B, t.C, floorEdge);
                    }
                }
                else if (drawEdges)
                {
                    _canvas.Triangle(t.A, t.B, t.C, t.NormalY < minNormalY ? wallEdge : otherEdge);
                }
                if (_trianglesDrawn > TriangleBudget)
                {
                    break;
                }
            }
        }
    }

    private void DrawSelection()
    {
        if (!_showSelected)
        {
            return;
        }
        var tris = _scene!.Triangles.Span;
        var selected = UICanvas2D.WithAlpha(Colors.Safe, 0x70);
        var excluded = UICanvas2D.WithAlpha(Colors.Danger, 0x50);
        foreach (var i in _selection.Selected)
        {
            ref readonly var t = ref tris[i];
            var b = t.Bounds;
            if (_canvas.IsVisible(b.Min, b.Max))
            {
                _canvas.TriangleFilled(t.A, t.B, t.C, selected);
            }
        }
        foreach (var i in _session!.LastAutoResult)
        {
            if (_selection.Selected.Contains(i))
            {
                continue;
            }
            ref readonly var t = ref tris[i];
            var b = t.Bounds;
            if (_canvas.IsVisible(b.Min, b.Max))
            {
                _canvas.TriangleFilled(t.A, t.B, t.C, excluded);
            }
        }
        // rim slopes the last recompute extended the floor across (only the part inside the rim band ends up in the polygon)
        var rim = UICanvas2D.WithAlpha(Colors.Vulnerable, 0x50);
        foreach (var i in _session.RimTriangles)
        {
            ref readonly var t = ref tris[i];
            var b = t.Bounds;
            if (_canvas.IsVisible(b.Min, b.Max))
            {
                _canvas.TriangleFilled(t.A, t.B, t.C, rim);
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
            if (!scene.IsBoxEnabled(b) || _sealBoxes.Contains(b) || box.Kind != LgbColliderKind.Box || !_canvas.IsVisible(box.WorldBounds.Min, box.WorldBounds.Max))
            {
                continue;
            }
            var c = box.Corners;
            var hovered = b == _hoverBox;
            if (s.BoxIsFloor(box))
            {
                var selected = session.SelectedFloorBoxes.Contains(b);
                // top face: corners 2,3,7,6 of the unit cube (+Y)
                _canvas.TriangleFilled(c[2], c[3], c[7], selected ? selectedFill : floorFill);
                _canvas.TriangleFilled(c[2], c[7], c[6], selected ? selectedFill : floorFill);
                DrawBoxOutline(c, hovered ? Colors.PlayerInteresting : floorEdge, hovered ? 2f : 1f);
            }
            else if (s.BoxIsObstacle(box))
            {
                var ignored = session.IgnoredBoxes.Contains(b);
                if (!ignored)
                {
                    _canvas.TriangleFilled(c[2], c[3], c[7], obstacleFill);
                    _canvas.TriangleFilled(c[2], c[7], c[6], obstacleFill);
                }
                DrawBoxOutline(c, hovered ? Colors.PlayerInteresting : ignored ? ignoredEdge : obstacleEdge, hovered ? 2f : 1f);
            }
            else
            {
                DrawBoxOutline(c, hovered ? Colors.PlayerInteresting : otherEdge, hovered ? 2f : 1f);
            }
        }
    }

    private void DrawBoxOutline(Vector3[] c, uint color, float thickness)
    {
        _canvas.Line(c[2], c[3], color, thickness);
        _canvas.Line(c[3], c[7], color, thickness);
        _canvas.Line(c[7], c[6], color, thickness);
        _canvas.Line(c[6], c[2], color, thickness);
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
            var hull = BoxFootprintOps.BoxFootprint(box.Corners, 0f);
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
        return $"{role}: material 0x{box.MatValue:X}/0x{box.MatMask:X} {(box.IsAnalytic ? "model collider (BgPart analytic)" : "CollisionBox instance")}\nlayout 0x{box.LayoutObjectId:X16} layer '{scene.Layers[box.LayerIndex].Name}'\ncentre ({box.Center.X:f2}, {box.Center.Y:f2}, {box.Center.Z:f2}) half ({box.HalfExtents.X:f2}, {box.HalfExtents.Y:f2}, {box.HalfExtents.Z:f2}) y {box.WorldBounds.Min.Y:f1}..{box.WorldBounds.Max.Y:f1}\nB toggles cut/ignored (floor box: selected)";
    }

    private void DrawMarkers()
    {
        if (!_showMarkers)
        {
            return;
        }
        var scene = _scene!;
        var session = _session!;
        for (var i = 0; i < scene.Markers.Count; ++i)
        {
            var m = scene.Markers[i];
            if (!scene.Layers[m.LayerIndex].Enabled || !_canvas.IsVisible(m.Position.X - 1f, m.Position.Z - 1f, m.Position.X + 1f, m.Position.Z + 1f))
            {
                continue;
            }
            var exit = m.Type == (int)LgbInstanceType.ExitRange;
            if (!exit && _canvas.Zoom < 4f)
            {
                continue;
            }
            var chosen = session.LastEstimate.MarkerIndex == i;
            var color = chosen ? Colors.PlayerInteresting : exit ? Colors.Other3 : UICanvas2D.WithAlpha(Colors.Other4, 0xA0);
            var p = new WPos(m.Position.X, m.Position.Z);
            _canvas.ScreenCircle(_canvas.ToScreen(p), exit ? 6f : 4f, color, 2f);
            if (exit || _canvas.Zoom >= 8f)
            {
                _canvas.Text(p, exit ? $"exit {i}" : $"{m.TypeName} {i}{(m.Name.Length > 0 ? $" {m.Name}" : "")}", color, new(8f, -6f));
            }
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
            var chosen = i == session.LastEstimate.SealA || i == session.LastEstimate.SealB;
            var color = i == _hoveredSeal ? Colors.PlayerInteresting : active ? Colors.Danger : UICanvas2D.WithAlpha(Colors.Danger, 0x60);
            var fp = seal.FootprintXZ;
            for (var k = 0; k < fp.Count; ++k)
            {
                var a = fp[k];
                var b = fp[(k + 1) % fp.Count];
                _canvas.Line(new WPos(a.X / scale, a.Y / scale), new WPos(b.X / scale, b.Y / scale), color, chosen ? 4f : 2f);
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
        if (_hoverVertexPoly >= 0)
        {
            var c = _hoverVertexContour < 0 ? _pipeline.Preview[_hoverVertexPoly].Outer : _pipeline.Preview[_hoverVertexPoly].Holes[_hoverVertexContour];
            var pts = c.SimplifiedDraw;
            if (_hoverVertexIndex < pts.Length)
            {
                var p = pts[_hoverVertexIndex];
                _canvas.ScreenCircle(_canvas.ToScreen(p), 7f, Colors.PlayerInteresting, 2f);
                var prev = pts[(_hoverVertexIndex + pts.Length - 1) % pts.Length];
                var next = pts[(_hoverVertexIndex + 1) % pts.Length];
                _canvas.Line(prev, next, UICanvas2D.WithAlpha(Colors.PlayerInteresting, 0xA0), 1.5f); // where the edge goes after deleting
                _canvas.Text(new WPos(p.X, p.Z), $"({p.X:f3}, {p.Z:f3}) click deletes", Colors.PlayerInteresting, new(10f, -18f));
            }
        }
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
                    ToggleFloorAnchorMesh();
                }
                else
                {
                    ToggleExcludeAnchorMesh();
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
                var top = session.PickTriangle(new(p.X, p.Z), session.Centre.Y);
                var y = top >= 0 ? scene.Triangles[top].YAt(p.X, p.Z) : session.Centre.Y;
                session.SetCentre(new(p.X, y, p.Z));
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
                    if (!ctrl && _selectFloorOnly)
                    {
                        FilterFloorOnly(_scratch);
                    }
                    if (_selectLayerOnly && _layerRefValid)
                    {
                        FilterLayer(_scratch, _layerRefY);
                    }
                    var arr = _scratch.ToArray();
                    if (ctrl)
                    {
                        _selection.Apply([], arr, "rect remove");
                    }
                    else
                    {
                        _selection.Apply(arr, [], "rect add");
                    }
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
                        if (!ctrl && _selectFloorOnly)
                        {
                            FilterFloorOnly(_scratch);
                        }
                        if (_selectLayerOnly && _layerRefValid)
                        {
                            FilterLayer(_scratch, _layerRefY);
                        }
                        var arr = _scratch.ToArray();
                        if (ctrl)
                        {
                            _selection.Apply([], arr, "brush");
                        }
                        else
                        {
                            _selection.Apply(arr, [], "brush");
                        }
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
                    if (_hoverVertexPoly >= 0)
                    {
                        var c = _hoverVertexContour < 0 ? _pipeline.Preview[_hoverVertexPoly].Outer : _pipeline.Preview[_hoverVertexPoly].Holes[_hoverVertexContour];
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
