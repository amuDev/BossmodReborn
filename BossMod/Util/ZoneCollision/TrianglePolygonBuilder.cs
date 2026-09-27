using Clipper2Lib;

namespace BossMod;

// recovers floor heights for union output vertices: exact vertex hits first, then exact integer collinearity with a source triangle edge,
// then the nearest source vertex; the edge and vertex grids are built once on the first lookup instead of scanning every edge per vertex
public sealed class YLookup(long scale) : IHeightSource
{
    public readonly struct EdgeY
    {
        public readonly Point64 A, B; public readonly float YA, YB;
        public EdgeY(Point64 a, Point64 b, float ya, float yb)
        {
            // normalize key order to make on-seg checks stable
            if (a.X > b.X || a.X == b.X && a.Y > b.Y)
            {
                (a, b) = (b, a);
                (ya, yb) = (yb, ya);
            }
            A = a;
            B = b;
            YA = ya;
            YB = yb;
        }
    }

    private const float CellYalms = 2f;
    private readonly long _cell = (long)CellYalms * scale; // 2 yalms in integer units
    private readonly Dictionary<Point64, float> _lut = new(1 << 13);
    private readonly List<EdgeY> _edges = new(1 << 15);
    private XZHashGrid? _edgeCells;
    private XZHashGrid? _vertexCells;
    private Point64[] _vertices = [];

    public void AddVertex(Point64 p, float y)
    {
        _lut[p] = _lut.TryGetValue(p, out var cur) ? (cur + y) * 0.5f : y;
        _vertexCells = null;
    }

    public void AddEdge(Point64 a, Point64 b, float ya, float yb)
    {
        _edges.Add(new(a, b, ya, yb));
        _edgeCells = null;
    }

    private int CellOf(long v) => (int)Math.Floor(v / (double)_cell);

    private struct NearestVertex(Point64[] vertices, Dictionary<Point64, float> lut, Point64 p) : IRingVisitor
    {
        public double Best = double.MaxValue;
        public float Y;

        public readonly double BestDistSq => Best;

        public void Visit(List<int> ids)
        {
            for (var i = 0; i < ids.Count; ++i)
            {
                var v = vertices[ids[i]];
                double ddx = v.X - p.X, ddz = v.Y - p.Y;
                var d2 = ddx * ddx + ddz * ddz;
                if (d2 < Best)
                {
                    Best = d2;
                    Y = lut[v];
                }
            }
        }
    }

    public float Sample(Point64 p)
    {
        if (_lut.TryGetValue(p, out var y))
        {
            return y;
        }

        // lies on a recorded edge (exact integer collinearity)?
        _edgeCells ??= BuildEdgeCells();
        var cx = CellOf(p.X);
        var cz = CellOf(p.Y);
        if (_edgeCells.Cell(cx, cz) is { } candidates)
        {
            for (var k = 0; k < candidates.Count; ++k)
            {
                var e = _edges[candidates[k]];
                if (!OnSegment(p, e.A, e.B))
                {
                    continue;
                }
                // param t along the dominant axis
                long dx = e.B.X - e.A.X, dz = e.B.Y - e.A.Y;
                var t = Math.Abs(dx) >= Math.Abs(dz) ? dx == 0L ? 0d : (p.X - e.A.X) / (double)dx : dz == 0L ? 0d : (p.Y - e.A.Y) / (double)dz;
                return (float)(e.YA + t * (e.YB - e.YA));
            }
        }

        // fallback: nearest known vertex, ring search outward from the query cell
        _vertexCells ??= BuildVertexCells();
        if (_lut.Count == 0)
        {
            return 0f;
        }
        var nearest = new NearestVertex(_vertices, _lut, p);
        _vertexCells.NearestRings(cx, cz, ref nearest, _cell);
        return nearest.Y;
    }

    public float Sample(Point64 p, long scale) => Sample(p);

    // the grids are keyed by integer cell coordinates: the hash grid's float cell size is only a placeholder, cells are computed here
    private XZHashGrid BuildEdgeCells()
    {
        var cells = new XZHashGrid(CellYalms);
        for (var i = 0; i < _edges.Count; ++i)
        {
            var e = _edges[i];
            var cx0 = CellOf(Math.Min(e.A.X, e.B.X));
            var cx1 = CellOf(Math.Max(e.A.X, e.B.X));
            var cz0 = CellOf(Math.Min(e.A.Y, e.B.Y));
            var cz1 = CellOf(Math.Max(e.A.Y, e.B.Y));
            for (var cz = cz0; cz <= cz1; ++cz)
            {
                for (var cx = cx0; cx <= cx1; ++cx)
                {
                    cells.GetOrAddCell(cx, cz).Add(i);
                }
            }
        }
        return cells;
    }

    private XZHashGrid BuildVertexCells()
    {
        var cells = new XZHashGrid(CellYalms);
        _vertices = [.. _lut.Keys];
        for (var i = 0; i < _vertices.Length; ++i)
        {
            var p = _vertices[i];
            cells.GetOrAddCell(CellOf(p.X), CellOf(p.Y)).Add(i);
        }
        return cells;
    }

    private static bool OnSegment(in Point64 p, in Point64 a, in Point64 b)
    {
        var aX = a.X;
        var aY = a.Y;
        var bX = b.X;
        var bY = b.Y;
        var pX = p.X;
        var pY = p.Y;
        var cross = (bX - aX) * (pY - aY) - (bY - aY) * (pX - aX);
        if (cross != 0L)
        {
            return false;
        }
        long minX = Math.Min(aX, bX), maxX = Math.Max(aX, bX);
        long minY = Math.Min(aY, bY), maxY = Math.Max(aY, bY);
        return pX >= minX && pX <= maxX && pY >= minY && pY <= maxY;
    }
}

// accumulates world-space triangles (snapped to an integer grid, forced counter-clockwise) and unions them into outer/hole polygons with recovered Y
// this is the seam shared by the live collision tab (walking game meshes) and the offline zone editor (stored triangles)
public sealed class TrianglePolygonBuilder(float snapEpsXZ = 1e-4f, long scale = TrianglePolygonBuilder.ClipperScale)
{
    // integer units per yalm for every clipper operation of the arena pipeline
    public const long ClipperScale = 1024L * 1024L;

    public readonly long Scale = scale;
    private readonly long _snapInt = ComputeSnapInt(snapEpsXZ, scale);
    private readonly Paths64 _subjects = [];
    private readonly YLookup _y = new(scale);

    public void AddTriangle(in Vector3 a, in Vector3 b, in Vector3 c)
    {
        // quantize & snap to grid to fuse seams
        var pa = Snap(ToP64(a, Scale), _snapInt);
        var pb = Snap(ToP64(b, Scale), _snapInt);
        var pc = Snap(ToP64(c, Scale), _snapInt);

        // drop degenerate after snapping (any duplicates)
        if (pa == pb || pb == pc || pc == pa)
        {
            return;
        }

        // enforce CCW in XZ after snap (consistent winding -> no NonZero cancellations)
        EnsureCCW(ref pa, ref pb, ref pc);
        _subjects.Add([pa, pb, pc]);

        // Y lifting LUT + edges for interpolation
        _y.AddVertex(pa, a.Y);
        _y.AddVertex(pb, b.Y);
        _y.AddVertex(pc, c.Y);
        _y.AddEdge(pa, pb, a.Y, b.Y);
        _y.AddEdge(pb, pc, b.Y, c.Y);
        _y.AddEdge(pc, pa, c.Y, a.Y);
    }

    public void AddTriangles(ReadOnlySpan<WorldTriangle> tris, ReadOnlySpan<int> indices)
    {
        for (var i = 0; i < indices.Length; ++i)
        {
            ref readonly var t = ref tris[indices[i]];
            AddTriangle(t.A, t.B, t.C);
        }
    }

    // union of the triangles as outer/hole polygons; fragments and holes below minAreaMeters2 are dropped
    public List<PolygonWithHoles> Build(float minAreaMeters2 = 1e-6f)
    {
        List<PolygonWithHoles> res = [];
        if (_subjects.Count == 0)
        {
            return res;
        }
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, _subjects, null, tree, FillRule.NonZero);
        BoxFootprintOps.AddTreePolygons(tree, res, _y, Math.Max(1d, minAreaMeters2 * (double)Scale * Scale), Scale);
        return res;
    }

    public static Point64 ToP64(in Vector3 v, long s) => new((long)Math.Round(v.X * s), (long)Math.Round(v.Z * s));

    public static Point64 ToP64(in Vector2 xz, long s) => new((long)Math.Round(xz.X * s), (long)Math.Round(xz.Y * s));

    public static long ComputeSnapInt(float snapEpsXZ, long scale)
    {
        if (!(snapEpsXZ > 0f))
        {
            return 1L;
        }
        var k = (long)Math.Round(snapEpsXZ * scale);
        return Math.Max(1, k);
    }

    public static Point64 Snap(Point64 p, long snapInt)
    {
        if (snapInt <= 1L)
        {
            return p;
        }
        static long RoundToMultiple(long v, long m)
        {
            // nearest multiple of m
            var half = m >> 1;
            return v >= 0L ? (v + half) / m * m : (v - half) / m * m;
        }
        return new Point64(RoundToMultiple(p.X, snapInt), RoundToMultiple(p.Y, snapInt));
    }

    public static void EnsureCCW(ref Point64 a, ref Point64 b, ref Point64 c)
    {
        var aX = a.X;
        var aY = a.Y;
        var cross = (b.X - aX) * (c.Y - aY) - (b.Y - aY) * (c.X - aX);
        if (cross < 0L)
        {
            (b, c) = (c, b);
        }
    }
}
