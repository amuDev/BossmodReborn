using Clipper2Lib;

namespace BossMod;

// accumulates world-space triangles (snapped to an integer grid, forced counter-clockwise) and unions them into outer/hole polygons with recovered Y
// this is the seam shared by the live collision tab (walking game meshes) and the offline zone editor (stored triangles)
public sealed class TrianglePolygonBuilder(float snapEpsXZ = 1e-4f, long scale = 1024L * 1024L)
{
    public readonly long Scale = scale;
    public readonly long SnapInt = ComputeSnapInt(snapEpsXZ, scale);
    private readonly Paths64 _subjects = [];
    private readonly Dictionary<Point64, float> _yLUT = new(1 << 13);
    private readonly List<EdgeY> _edges = new(1 << 15);

    public int TriangleCount => _subjects.Count;

    public void Clear()
    {
        _subjects.Clear();
        _yLUT.Clear();
        _edges.Clear();
    }

    public void AddTriangle(in Vector3 a, in Vector3 b, in Vector3 c)
    {
        // quantize & snap to grid to fuse seams
        var pa = Snap(ToP64(a, Scale), SnapInt);
        var pb = Snap(ToP64(b, Scale), SnapInt);
        var pc = Snap(ToP64(c, Scale), SnapInt);

        // drop degenerate after snapping (any duplicates)
        if (pa == pb || pb == pc || pc == pa)
        {
            return;
        }

        // enforce CCW in XZ after snap (consistent winding -> no NonZero cancellations)
        EnsureCCW(ref pa, ref pb, ref pc);
        _subjects.Add([pa, pb, pc]);

        // Y lifting LUT + edges for interpolation
        AccumY(_yLUT, pa, a.Y);
        AccumY(_yLUT, pb, b.Y);
        AccumY(_yLUT, pc, c.Y);
        _edges.Add(new(pa, pb, a.Y, b.Y));
        _edges.Add(new(pb, pc, b.Y, c.Y));
        _edges.Add(new(pc, pa, c.Y, a.Y));
    }

    public void AddTriangles(ReadOnlySpan<WorldTriangle> tris, ReadOnlySpan<int> indices)
    {
        for (var i = 0; i < indices.Length; ++i)
        {
            ref readonly var t = ref tris[indices[i]];
            AddTriangle(t.A, t.B, t.C);
        }
    }

    public Paths64 LastUnion = []; // raw union output of the last Build (diagnostics)

    public List<CollisionOutlinesExtractor.PolygonWithHoles> Build(float minAreaMeters2 = 1e-6f)
    {
        if (_subjects.Count == 0)
        {
            return [];
        }
        var union = Clipper.Union(_subjects, FillRule.NonZero);
        LastUnion = union;
        return Paths64ToPolys(union, _yLUT, _edges, Scale, minAreaMeters2);
    }

    public static List<CollisionOutlinesExtractor.PolygonWithHoles> Paths64ToPolys(Paths64 paths, Dictionary<Point64, float> yLUT, List<EdgeY> edges, long scale, float minAreaMeters2)
    {
        // filter slivers (post-union) using area threshold
        var minAreaInt = Math.Max(1.0, minAreaMeters2 * (double)scale * scale);

        var outers = new List<(Path64 path, double area, AABB2 bb)>(paths.Count);
        var holes = new List<(Path64 path, double area, AABB2 bb)>();

        foreach (var p in paths)
        {
            if (p.Count < 3)
            {
                continue;
            }

            // small clean-up: remove consecutive duplicates after union
            var cleaned = RemoveConsecutiveDuplicates(p);
            if (cleaned.Count < 3)
            {
                continue;
            }

            var aSigned = AreaSigned(cleaned);
            var aInt = Math.Abs(aSigned);
            if (aInt < minAreaInt)
            {
                continue; // drop tiny fragments
            }

            var bb = BoundsXZ(cleaned);

            if (aSigned > 0d)
            {
                outers.Add((cleaned, aSigned, bb));
            }
            else
            {
                holes.Add((cleaned, aSigned, bb));
            }
        }

        outers.Sort(static (A, B) => B.area.CompareTo(A.area));
        holes.Sort(static (A, B) => Math.Abs(B.area).CompareTo(Math.Abs(A.area)));

        var countO = outers.Count;
        var countH = holes.Count;
        var res = new List<CollisionOutlinesExtractor.PolygonWithHoles>(countO + countH);

        for (var i = 0; i < countO; ++i)
        {
            var poly = new CollisionOutlinesExtractor.PolygonWithHoles();
            var outer = outers[i];
            PolyToVectorsCCW(outer.path, yLUT, edges, scale, poly.Outer);

            for (var h = 0; h < countH; ++h)
            {
                var hol = holes[h];
                if (hol.path == null)
                {
                    continue;
                }
                if (!outer.bb.Contains(hol.bb))
                {
                    continue;
                }

                // both in scaled integer space: a hole is matched to the outer that contains it
                var (cx, cz) = CentroidScaled(hol.path);
                if (PointInPolygonScaled(cx, cz, outer.path))
                {
                    var hole = new List<Vector3>();
                    PolyToVectorsCCW(hol.path, yLUT, edges, scale, hole);
                    poly.Holes.Add(hole);
                    holes[h] = (null!, 0, default);
                }
            }
            res.Add(poly);
        }

        for (var i = 0; i < countH; ++i)
        {
            var h = holes[i];
            if (h.path == null)
            {
                continue;
            }
            var poly = new CollisionOutlinesExtractor.PolygonWithHoles();
            PolyToVectorsCCW(h.path, yLUT, edges, scale, poly.Outer);
            res.Add(poly);
        }
        return res;
    }

    public static Path64 RemoveConsecutiveDuplicates(Path64 p)
    {
        if (p.Count <= 2)
        {
            return p;
        }
        var count = p.Count;
        var outp = new Path64(count);
        Point64 prev = new(long.MinValue, long.MinValue);
        for (var i = 0; i < count; ++i)
        {
            var pi = p[i];
            if (i == 0 || pi.X != prev.X || pi.Y != prev.Y)
            {
                outp.Add(pi);
            }
            prev = pi;
        }
        // also check last==first
        var countO = outp.Count;
        if (countO >= 2 && outp[0] == outp[^1])
        {
            outp.RemoveAt(countO - 1);
        }
        return outp;
    }

    public static void PolyToVectorsCCW(Path64 path, Dictionary<Point64, float> yLUT, List<EdgeY> edges, long scale, List<Vector3> dst)
    {
        if (AreaSigned(path) < 0d)
        {
            path.Reverse();
        }
        var count = path.Count;
        dst.Capacity = Math.Max(dst.Capacity, count);
        for (var i = 0; i < count; ++i)
        {
            var p = path[i];
            var y = SampleY(p, yLUT, edges);
            dst.Add(new Vector3(p.X / (float)scale, y, p.Y / (float)scale));
        }
    }

    public static Point64 ToP64(in Vector3 v, long s) => new((long)Math.Round(v.X * s), (long)Math.Round(v.Z * s));

    private static void AccumY(Dictionary<Point64, float> lut, Point64 p, float y)
    {
        lut[p] = lut.TryGetValue(p, out var cur) ? (cur + y) * 0.5f : y;
    }

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

    public static float SampleY(Point64 p, Dictionary<Point64, float> lut, List<EdgeY> edges)
    {
        if (lut.TryGetValue(p, out var y))
        {
            return y;
        }

        // check if lies on any recorded edge (exact integer colinearity)
        var count = edges.Count;
        for (int i = 0, n = count; i < n; ++i)
        {
            var e = edges[i];
            if (!OnSegment(p, e.A, e.B))
            {
                continue;
            }

            // param t along the dominant axis
            long dx = e.B.X - e.A.X, dz = e.B.Y - e.A.Y;
            var t = Math.Abs(dx) >= Math.Abs(dz) ? dx == 0L ? 0d : (p.X - e.A.X) / (double)dx : dz == 0L ? 0d : (p.Y - e.A.Y) / (double)dz;
            return (float)(e.YA + t * (e.YB - e.YA));
        }

        // fallback: nearest known vertex
        var bestY = 0f;
        var bestD2 = double.MaxValue;
        foreach (var kv in lut)
        {
            double ddx = kv.Key.X - p.X, ddz = kv.Key.Y - p.Y;
            var d2 = ddx * ddx + ddz * ddz;
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestY = kv.Value;
            }
        }
        return bestY;
    }

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

    public static double AreaSigned(Path64 p)
    {
        long a = 0;
        var n = p.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            a += p[j].X * p[i].Y - p[i].X * p[j].Y;
        }
        return 0.5d * a;
    }

    private static AABB2 BoundsXZ(Path64 p)
    {
        long minX = long.MaxValue, minZ = long.MaxValue, maxX = long.MinValue, maxZ = long.MinValue;
        var count = p.Count;
        for (var i = 0; i < count; ++i)
        {
            var pt = p[i];
            var pX = pt.X;
            var pY = pt.Y;
            if (pX < minX)
            {
                minX = pX;
            }
            if (pX > maxX)
            {
                maxX = pX;
            }
            if (pY < minZ)
            {
                minZ = pY;
            }
            if (pY > maxZ)
            {
                maxZ = pY;
            }
        }
        return new AABB2(minX, minZ, maxX, maxZ);
    }

    private static (double cx, double cz) CentroidScaled(Path64 p)
    {
        double cx = 0, cz = 0;
        var n = p.Count;
        for (var i = 0; i < n; ++i)
        {
            cx += p[i].X;
            cz += p[i].Y;
        }
        return (cx / n, cz / n);
    }

    private static bool PointInPolygonScaled(double px, double pz, Path64 path)
    {
        var inside = false;
        var n = path.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var vi = path[i];
            var vj = path[j];
            double xi = vi.X, zi = vi.Y, xj = vj.X, zj = vj.Y;
            var inter = (zi > pz) != (zj > pz) && px < (xj - xi) * (pz - zi) / ((zj - zi) == 0d ? double.Epsilon : (zj - zi)) + xi;
            if (inter)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private readonly struct AABB2(float minX, float minZ, float maxX, float maxZ)
    {
        public readonly float MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ;

        public bool Contains(AABB2 o) => o.MinX >= MinX && o.MaxX <= MaxX && o.MinZ >= MinZ && o.MaxZ <= MaxZ;
    }
}
