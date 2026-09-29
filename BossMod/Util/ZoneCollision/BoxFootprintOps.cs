using Clipper2Lib;

namespace BossMod;

// receives the polygons of a clipper tree walk: an outer, its direct holes, then EndOuter; islands inside holes come back as outers of their own,
// nested between their parent's Outer and EndOuter (so an island ends before its parent)
public interface ITreeVisitor
{
    // false skips the outer and everything nested in it; holeCount = the direct holes the walk will report
    bool Outer(Path64 path, int holeCount);
    void Hole(Path64 path, int index);
    void EndOuter();
}

// box colliders (entrance/exit seals) as XZ footprints: cut or unioned against floor polygons, optionally keeping only the polygon containing an anchor
public static class BoxFootprintOps
{
    public const long DefaultScale = TrianglePolygonBuilder.ClipperScale;

    // Andrew's monotone chain on the XZ projection
    public static Path64 ConvexHullXZ(ReadOnlySpan<Vector3> pts, long scale = DefaultScale)
    {
        var n = pts.Length;
        var p = new Point64[n];
        for (var i = 0; i < n; ++i)
        {
            p[i] = TrianglePolygonBuilder.ToP64(pts[i], scale);
        }
        Array.Sort(p, (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var hull = new Path64(2 * n);
        for (var i = 0; i < n; ++i)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p[i]) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(p[i]);
        }
        var lowerCount = hull.Count + 1;
        for (var i = n - 2; i >= 0; --i)
        {
            while (hull.Count >= lowerCount && Cross(hull[^2], hull[^1], p[i]) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(p[i]);
        }
        if (hull.Count > 1)
        {
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;

        static long Cross(Point64 o, Point64 a, Point64 b) => Math.Sign((a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X));
    }

    // convex hull of the box corners, counter-clockwise, optionally inflated
    public static Path64 BoxFootprint(ReadOnlySpan<Vector3> corners, float inflate = 0f, long scale = DefaultScale)
    {
        var hull = ConvexHullXZ(corners, scale);
        if (hull.Count >= 3 && !Clipper.IsPositive(hull))
        {
            hull = Clipper.ReversePath(hull);
        }
        return Inflate(hull, inflate, scale);
    }

    // the footprint grown by 'inflate' yalms with miter joins (unchanged when zero); the input is never modified
    public static Path64 Inflate(Path64 hull, float inflate, long scale = DefaultScale)
    {
        if (inflate == 0f || hull.Count < 3)
        {
            return hull;
        }
        var inflated = Clipper.InflatePaths([hull], inflate * scale, JoinType.Miter, EndType.Polygon);
        if (inflated.Count == 0)
        {
            return hull;
        }
        var result = inflated[0];
        return Clipper.IsPositive(result) ? result : Clipper.ReversePath(result);
    }

    // inside or on the edge of the polygon
    public static bool Contains(Path64 poly, Point64 p) => poly.Count >= 3 && Clipper.PointInPolygon(p, poly) != PointInPolygonResult.IsOutside;

    // morphological closing on clipper paths (inflate then deflate with miter joins): fills slits and notches narrower than 2r without moving the
    // rest of the outline
    public static Paths64 ClosePaths(Paths64 paths, float radius, long scale = DefaultScale)
        => Clipper.InflatePaths(Clipper.InflatePaths(paths, radius * scale, JoinType.Miter, EndType.Polygon), -radius * scale, JoinType.Miter, EndType.Polygon);

    // the polygons as clipper subjects with the winding normalised (outers positive, holes negative) and their vertices collected as height sources:
    // the NonZero fill rule cancels a positive union path against a negative outer, so mixed conventions would turn unioned boxes into holes
    private static Paths64 ToSubjects(List<PolygonWithHoles> polys, List<Vector3> ySource, long scale)
    {
        var paths = new Paths64();
        for (var i = 0; i < polys.Count; ++i)
        {
            var poly = polys[i];
            var outer = ToPath64(poly.Outer, false, scale);
            if (!Clipper.IsPositive(outer))
            {
                outer.Reverse();
            }
            paths.Add(outer);
            ySource.AddRange(poly.Outer);
            for (var h = 0; h < poly.Holes.Count; ++h)
            {
                var hole = ToPath64(poly.Holes[h], false, scale);
                if (Clipper.IsPositive(hole))
                {
                    hole.Reverse();
                }
                paths.Add(hole);
                ySource.AddRange(poly.Holes[h]);
            }
        }
        return paths;
    }

    // morphological closing of polygons; Y is recovered from the input vertices
    public static List<PolygonWithHoles> Close(List<PolygonWithHoles> polys, float radius, float minArea, long scale = DefaultScale)
    {
        if (polys.Count == 0 || radius <= 0f)
        {
            return polys;
        }
        List<Vector3> ySource = [];
        var shaped = ClosePaths(ToSubjects(polys, ySource, scale), radius, scale);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, shaped, null, tree, FillRule.NonZero);
        List<PolygonWithHoles> result = [];
        AddTreePolygons(tree, result, new YSampler(ySource), (double)minArea * scale * scale, scale);
        return result;
    }

    // cut and union paths applied to the polygons; with an anchor only the polygon containing it survives (the largest when it is in none)
    public static List<PolygonWithHoles> Apply(List<PolygonWithHoles> polys, List<Path64> cut, List<Path64> union, List<Vector3> boxYSource,
        Vector2? keepAnchorXZ, float minArea, out string keepStatus, long scale = DefaultScale)
    {
        keepStatus = "";
        List<Vector3> ySource = [.. boxYSource];
        var subject = ToSubjects(polys, ySource, scale);
        for (var i = 0; i < union.Count; ++i)
        {
            var u = union[i];
            if (u.Count >= 3 && !Clipper.IsPositive(u))
            {
                u = Clipper.ReversePath(u);
            }
            subject.Add(u);
        }
        var tree = new PolyTree64();
        if (cut.Count > 0)
        {
            var clip = new Paths64(cut.Count);
            clip.AddRange(cut);
            Clipper.BooleanOp(ClipType.Difference, subject, clip, tree, FillRule.NonZero);
        }
        else
        {
            Clipper.BooleanOp(ClipType.Union, subject, null, tree, FillRule.NonZero);
        }

        List<PolygonWithHoles> result = [];
        var minAreaScaled = (double)minArea * scale * scale;
        AddTreePolygons(tree, result, new YSampler(ySource), minAreaScaled, scale);

        if (keepAnchorXZ is { } anchorXZ && result.Count > 1)
        {
            var anchor = TrianglePolygonBuilder.ToP64(anchorXZ, scale);
            var keep = -1;
            var largest = -1;
            var largestArea = 0d;
            for (var i = 0; i < result.Count; ++i)
            {
                var outer = ToPath64(result[i].Outer, false, scale);
                var area = Math.Abs(Clipper.Area(outer));
                if (area > largestArea)
                {
                    largestArea = area;
                    largest = i;
                }
                if (keep < 0 && Contains(outer, anchor))
                {
                    keep = i;
                }
            }
            var chosen = keep >= 0 ? keep : largest;
            keepStatus = keep >= 0 ? $"kept polygon containing the anchor, dropped {result.Count - 1}" : $"anchor is in no polygon - kept the largest, dropped {result.Count - 1}";
            result = [result[chosen]];
        }
        return result;
    }

    // depth-first over a clipper tree, outers with their direct holes; islands inside holes are outers of their own
    public static void WalkTree<T>(PolyPath64 node, ref T visitor) where T : ITreeVisitor
    {
        var count = node.Count;
        for (var i = 0; i < count; ++i)
        {
            var outerNode = node[i];
            if (outerNode.Polygon == null || !visitor.Outer(outerNode.Polygon, outerNode.Count))
            {
                continue;
            }
            var holeCount = outerNode.Count;
            for (var h = 0; h < holeCount; ++h)
            {
                var holeNode = outerNode[h];
                if (holeNode.Polygon != null)
                {
                    visitor.Hole(holeNode.Polygon, h);
                }
                WalkTree(holeNode, ref visitor);
            }
            visitor.EndOuter();
        }
    }

    private readonly struct PolygonCollector(List<PolygonWithHoles> dst, IHeightSource ySource, double minAreaScaled, long scale) : ITreeVisitor
    {
        private readonly List<PolygonWithHoles> _open = []; // outers still taking holes, innermost last

        public bool Outer(Path64 path, int holeCount)
        {
            if (path.Count < 3 || Math.Abs(Clipper.Area(path)) < minAreaScaled)
            {
                return false;
            }
            _open.Add(new() { Outer = PathToVectorsCCW(path, ySource, scale), Holes = [] });
            return true;
        }

        public void Hole(Path64 path, int index)
        {
            if (path.Count >= 3 && Math.Abs(Clipper.Area(path)) >= minAreaScaled)
            {
                _open[^1].Holes.Add(PathToVectorsCCW(path, ySource, scale));
            }
        }

        public void EndOuter()
        {
            dst.Add(_open[^1]);
            _open.RemoveAt(_open.Count - 1);
        }
    }

    // the tree's polygons (contours below minAreaScaled dropped) with Y recovered from the height source
    public static void AddTreePolygons(PolyPath64 node, List<PolygonWithHoles> dst, IHeightSource ySource, double minAreaScaled, long scale = DefaultScale)
    {
        var collector = new PolygonCollector(dst, ySource, minAreaScaled, scale);
        WalkTree(node, ref collector);
    }

    public static List<Vector3> PathToVectorsCCW(Path64 path, IHeightSource ySource, long scale = DefaultScale)
    {
        if (!Clipper.IsPositive(path))
        {
            path = Clipper.ReversePath(path);
        }
        var n = path.Count;
        List<Vector3> pts = new(n);
        for (var i = 0; i < n; ++i)
        {
            var p = path[i];
            pts.Add(new((float)(p.X / (double)scale), ySource.Sample(p, scale), (float)(p.Y / (double)scale)));
        }
        return pts;
    }

    public static Path64 ToPath64(List<Vector3> pts, bool reverse, long scale = DefaultScale)
    {
        var n = pts.Count;
        var path = new Path64(n);
        if (reverse)
        {
            for (var i = n - 1; i >= 0; --i)
            {
                path.Add(TrianglePolygonBuilder.ToP64(pts[i], scale));
            }
        }
        else
        {
            for (var i = 0; i < n; ++i)
            {
                path.Add(TrianglePolygonBuilder.ToP64(pts[i], scale));
            }
        }
        return path;
    }
}
