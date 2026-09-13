using Clipper2Lib;

namespace BossMod;

// box colliders (entrance/exit seals) as XZ footprints: cut or unioned against floor polygons, optionally keeping only the polygon containing an anchor
public static class BoxFootprintOps
{
    public const long DefaultScale = 1024L * 1024L;

    // Andrew's monotone chain on the XZ projection
    public static Path64 ConvexHullXZ(ReadOnlySpan<Vector3> pts, long scale = DefaultScale)
    {
        var n = pts.Length;
        var p = new Point64[n];
        for (var i = 0; i < n; ++i)
        {
            p[i] = new Point64((long)Math.Round(pts[i].X * scale), (long)Math.Round(pts[i].Z * scale));
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
        if (inflate != 0f && hull.Count >= 3)
        {
            var inflated = Clipper.InflatePaths([hull], inflate * scale, JoinType.Miter, EndType.Polygon);
            if (inflated.Count > 0)
            {
                hull = inflated[0];
                if (!Clipper.IsPositive(hull))
                {
                    hull = Clipper.ReversePath(hull);
                }
            }
        }
        return hull;
    }

    // morphological closing (inflate then deflate with miter joins): fills slits and notches narrower than 2r without moving the
    // rest of the outline; Y is recovered from the input vertices
    public static List<CollisionOutlinesExtractor.PolygonWithHoles> Close(List<CollisionOutlinesExtractor.PolygonWithHoles> polys, float radius, float minArea, long scale = DefaultScale)
    {
        if (polys.Count == 0 || radius <= 0f)
        {
            return polys;
        }
        List<Vector3> ySource = [];
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
        var shaped = Clipper.InflatePaths(Clipper.InflatePaths(paths, radius * scale, JoinType.Miter, EndType.Polygon), -radius * scale, JoinType.Miter, EndType.Polygon);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, shaped, null, tree, FillRule.NonZero);
        List<CollisionOutlinesExtractor.PolygonWithHoles> result = [];
        AddTreePolygons(tree, result, ySource, (double)minArea * scale * scale, scale);
        return result;
    }

    public static List<CollisionOutlinesExtractor.PolygonWithHoles> Apply(List<CollisionOutlinesExtractor.PolygonWithHoles> polys, List<Path64> cut, List<Path64> union, List<Vector3> boxYSource,
        Vector2? keepAnchorXZ, float minArea, out string keepStatus, long scale = DefaultScale)
    {
        keepStatus = "";
        List<Vector3> ySource = [.. boxYSource];
        var subject = new Paths64();
        var count = polys.Count;
        // normalise winding explicitly (outers positive, holes negative, unions positive): the NonZero fill rule cancels a
        // positive union path against a negative outer, so mixed conventions turn unioned boxes into holes
        for (var i = 0; i < count; ++i)
        {
            var poly = polys[i];
            var outer = ToPath64(poly.Outer, false, scale);
            if (!Clipper.IsPositive(outer))
            {
                outer.Reverse();
            }
            subject.Add(outer);
            ySource.AddRange(poly.Outer);
            for (var h = 0; h < poly.Holes.Count; ++h)
            {
                var hole = ToPath64(poly.Holes[h], false, scale);
                if (Clipper.IsPositive(hole))
                {
                    hole.Reverse();
                }
                subject.Add(hole);
                ySource.AddRange(poly.Holes[h]);
            }
        }
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

        List<CollisionOutlinesExtractor.PolygonWithHoles> result = [];
        var minAreaScaled = (double)minArea * scale * scale;
        AddTreePolygons(tree, result, ySource, minAreaScaled, scale);

        if (keepAnchorXZ is { } anchorXZ && cut.Count > 0 && result.Count > 1)
        {
            var anchor = new Point64((long)Math.Round(anchorXZ.X * scale), (long)Math.Round(anchorXZ.Y * scale));
            var keep = -1;
            var largest = -1;
            var largestArea = 0.0;
            for (var i = 0; i < result.Count; ++i)
            {
                var outer = ToPath64(result[i].Outer, false, scale);
                var area = Math.Abs(Clipper.Area(outer));
                if (area > largestArea)
                {
                    largestArea = area;
                    largest = i;
                }
                if (keep < 0 && Clipper.PointInPolygon(anchor, outer) != PointInPolygonResult.IsOutside)
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

    public static void AddTreePolygons(PolyPath64 node, List<CollisionOutlinesExtractor.PolygonWithHoles> dst, List<Vector3> ySource, double minAreaScaled, long scale = DefaultScale)
    {
        var count = node.Count;
        for (var i = 0; i < count; ++i)
        {
            var outerNode = node[i];
            if (outerNode.Polygon == null || outerNode.Polygon.Count < 3 || Math.Abs(Clipper.Area(outerNode.Polygon)) < minAreaScaled)
            {
                continue;
            }
            var poly = new CollisionOutlinesExtractor.PolygonWithHoles { Outer = PathToVectorsCCW(outerNode.Polygon, ySource, scale), Holes = [] };
            var holeCount = outerNode.Count;
            for (var h = 0; h < holeCount; ++h)
            {
                var holeNode = outerNode[h];
                if (holeNode.Polygon != null && holeNode.Polygon.Count >= 3 && Math.Abs(Clipper.Area(holeNode.Polygon)) >= minAreaScaled)
                {
                    poly.Holes.Add(PathToVectorsCCW(holeNode.Polygon, ySource, scale));
                }
                AddTreePolygons(holeNode, dst, ySource, minAreaScaled, scale);
            }
            dst.Add(poly);
        }
    }

    public static List<Vector3> PathToVectorsCCW(Path64 path, List<Vector3> ySource, long scale = DefaultScale)
    {
        if (!Clipper.IsPositive(path))
        {
            path = Clipper.ReversePath(path);
        }
        var n = path.Count;
        List<Vector3> pts = new(n);
        for (var i = 0; i < n; ++i)
        {
            var x = (float)(path[i].X / (double)scale);
            var z = (float)(path[i].Y / (double)scale);
            pts.Add(new(x, NearestY(ySource, x, z), z));
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
                path.Add(new Point64((long)Math.Round(pts[i].X * scale), (long)Math.Round(pts[i].Z * scale)));
            }
        }
        else
        {
            for (var i = 0; i < n; ++i)
            {
                path.Add(new Point64((long)Math.Round(pts[i].X * scale), (long)Math.Round(pts[i].Z * scale)));
            }
        }
        return path;
    }

    public static float NearestY(List<Vector3> source, float x, float z)
    {
        var best = float.MaxValue;
        var y = 0f;
        var n = source.Count;
        for (var i = 0; i < n; ++i)
        {
            var v = source[i];
            var dx = v.X - x;
            var dz = v.Z - z;
            var d = dx * dx + dz * dz;
            if (d < best)
            {
                best = d;
                y = v.Y;
            }
        }
        return y;
    }
}
