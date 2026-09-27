namespace BossMod;

// a rocky terrain skin can contain steep little faces with no separate walkable mesh underneath: bounded roughness attached to the floor is
// treated as a step, measuring the whole edge-connected steep patch instead of each facet alone, so a subdivided cliff never becomes a staircase
// (ported from the live collision arena's ClassifySupport)
public static class ReliefPromotion
{
    // floors: triangles that already pass the slope test; steep: material-eligible, non-vertical triangles that fail it; returns how many were promoted
    public static int Promote(ReadOnlySpan<WorldTriangle> tris, List<int> floors, List<int> steep, float minNormalY, float step, float weldEps, List<int> promoted)
    {
        var n = steep.Count;
        if (n == 0 || floors.Count == 0)
        {
            return 0;
        }
        var sets = new UnionFind(n);
        var touchesFloor = new bool[n];
        Dictionary<(ulong a, ulong b), int> edges = new(n * 3);
        for (var k = 0; k < n; ++k)
        {
            ref readonly var t = ref tris[steep[k]];
            var ka = TriangleAdjacency.WeldKey(t.A, weldEps);
            var kb = TriangleAdjacency.WeldKey(t.B, weldEps);
            var kc = TriangleAdjacency.WeldKey(t.C, weldEps);
            AddEdge(ka, kb, k);
            AddEdge(kb, kc, k);
            AddEdge(kc, ka, k);
        }
        // index only the steep faces; find the footholds after joining every steep edge owner, so a nonmanifold edge behaves the same
        // regardless of triangle order
        for (var i = 0; i < floors.Count; ++i)
        {
            ref readonly var t = ref tris[floors[i]];
            var ka = TriangleAdjacency.WeldKey(t.A, weldEps);
            var kb = TriangleAdjacency.WeldKey(t.B, weldEps);
            var kc = TriangleAdjacency.WeldKey(t.C, weldEps);
            TouchFloor(ka, kb);
            TouchFloor(kb, kc);
            TouchFloor(kc, ka);
        }
        Dictionary<int, List<int>> patches = [];
        for (var k = 0; k < n; ++k)
        {
            var root = sets.Find(k);
            if (!patches.TryGetValue(root, out var members))
            {
                patches[root] = members = [];
            }
            members.Add(k);
        }
        var count = 0;
        Dictionary<ulong, Vector3> unique = [];
        foreach (var members in patches.Values)
        {
            var attached = false;
            var low = float.MaxValue;
            var high = float.MinValue;
            for (var m = 0; m < members.Count; ++m)
            {
                var k = members[m];
                attached |= touchesFloor[k];
                var b = tris[steep[k]].Bounds;
                low = MathF.Min(low, b.Min.Y);
                high = MathF.Max(high, b.Max.Y);
            }
            if (!attached)
            {
                continue;
            }
            if (high - low > step)
            {
                // follow a walkable overall incline while limiting the relief around it: a long shallow hillside can rise by more than one
                // step, a continuous steep wall still fails the fitted slope; unique vertices keep duplicated faces from biasing the fit
                unique.Clear();
                for (var m = 0; m < members.Count; ++m)
                {
                    ref readonly var t = ref tris[steep[members[m]]];
                    unique[TriangleAdjacency.WeldKey(t.A, weldEps)] = t.A;
                    unique[TriangleAdjacency.WeldKey(t.B, weldEps)] = t.B;
                    unique[TriangleAdjacency.WeldKey(t.C, weldEps)] = t.C;
                }
                if (!StepSizedRelief(unique.Values, minNormalY, step))
                {
                    continue;
                }
            }
            for (var m = 0; m < members.Count; ++m)
            {
                promoted.Add(steep[members[m]]);
            }
            count += members.Count;
        }
        return count;

        void AddEdge(ulong a, ulong b, int k)
        {
            if (a == b)
            {
                return;
            }
            var key = a < b ? (a, b) : (b, a);
            if (!edges.TryGetValue(key, out var owner))
            {
                edges[key] = k;
                return;
            }
            sets.Union(owner, k);
        }
        void TouchFloor(ulong a, ulong b)
        {
            if (edges.TryGetValue(a < b ? (a, b) : (b, a), out var owner))
            {
                touchesFloor[owner] = true;
            }
        }
    }

    // least-squares plane through the points: walkable overall grade, and every point within one step of the plane
    private static bool StepSizedRelief(Dictionary<ulong, Vector3>.ValueCollection points, float minNormalY, float step)
    {
        if (points.Count < 3)
        {
            return false;
        }
        double mx = 0d, my = 0d, mz = 0d;
        foreach (var p in points)
        {
            mx += p.X;
            my += p.Y;
            mz += p.Z;
        }
        mx /= points.Count;
        my /= points.Count;
        mz /= points.Count;
        double xx = 0d, xz = 0d, zz = 0d, xy = 0d, zy = 0d;
        foreach (var p in points)
        {
            var px = p.X - mx;
            var py = p.Y - my;
            var pz = p.Z - mz;
            xx += px * px;
            xz += px * pz;
            zz += pz * pz;
            xy += px * py;
            zy += pz * py;
        }
        var determinant = xx * zz - xz * xz;
        if (xx <= 0d || zz <= 0d || determinant <= 1e-12d * xx * zz)
        {
            return false;
        }
        var gradeX = (xy * zz - zy * xz) / determinant;
        var gradeZ = (zy * xx - xy * xz) / determinant;
        if (1d < minNormalY * Math.Sqrt(1d + gradeX * gradeX + gradeZ * gradeZ))
        {
            return false;
        }
        var low = double.PositiveInfinity;
        var high = double.NegativeInfinity;
        foreach (var p in points)
        {
            var residual = p.Y - my - gradeX * (p.X - mx) - gradeZ * (p.Z - mz);
            low = Math.Min(low, residual);
            high = Math.Max(high, residual);
            if (high - low > step)
            {
                return false;
            }
        }
        return true;
    }
}
