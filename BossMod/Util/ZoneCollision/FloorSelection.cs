namespace BossMod;

// the selected floor triangles of one recompute: membership, extents, an XZ grid for "is there floor at this height here" queries and the
// boundary edges (shared by exactly one selected triangle); built once per recompute and shared by the obstacle, rim and wall-snap passes
public sealed class FloorSelection
{
    public readonly int[] Triangles;
    public readonly Bounds3 Bounds;
    public readonly TriangleGrid Grid;
    public readonly float WeldEps;
    private readonly ZoneTriangleStore _store;
    private readonly bool[] _selected; // indexed by global triangle
    private List<(Vector3 a, Vector3 b)>? _boundary;
    private Dictionary<ulong, float>? _boundaryKeys;

    public FloorSelection(ZoneTriangleStore store, ReadOnlySpan<int> triangles, float weldEps)
    {
        _store = store;
        Triangles = triangles.ToArray();
        _selected = new bool[store.Count];
        for (var i = 0; i < Triangles.Length; ++i)
        {
            _selected[Triangles[i]] = true;
        }
        WeldEps = MathF.Max(weldEps, 1e-4f);
        var tris = store.Span;
        Bounds = TriangleGrid.BoundsOf(tris, Triangles);
        Grid = new(tris, Triangles, Bounds);
    }

    public int Count => Triangles.Length;
    public bool Contains(int triangle) => (uint)triangle < (uint)_selected.Length && _selected[triangle];

    // boundary edges of the selection within the weld tolerance
    public List<(Vector3 a, Vector3 b)> BoundaryEdges => _boundary ??= BuildBoundary();

    // edge key -> height of the floor edge (the lower vertex), for chains that start from the boundary
    public Dictionary<ulong, float> BoundaryKeys
    {
        get
        {
            _ = BoundaryEdges;
            return _boundaryKeys!;
        }
    }

    private struct OverlapQuery(ZoneTriangleStore store, Bounds3 b, float below, float above) : ITriangleVisitor
    {
        public bool Found;
        public float FloorTop = float.MinValue;

        public void Visit(int i)
        {
            var fb = store[i].Bounds;
            if (fb.Max.X < b.Min.X || fb.Min.X > b.Max.X || fb.Max.Z < b.Min.Z || fb.Min.Z > b.Max.Z)
            {
                return;
            }
            if (b.Max.Y >= fb.Min.Y - below && b.Min.Y <= fb.Max.Y + above)
            {
                Found = true;
                FloorTop = MathF.Max(FloorTop, fb.Max.Y);
            }
        }
    }

    // true when a floor triangle overlaps the bounds in XZ and the obstacle intersects [floorMin - below, floorMax + above];
    // floorTop = highest such floor triangle's top
    public bool Overlaps(in Bounds3 b, float below, float above, out float floorTop)
    {
        floorTop = float.MinValue;
        if (Triangles.Length == 0 || b.Max.X < Bounds.Min.X || b.Min.X > Bounds.Max.X || b.Max.Z < Bounds.Min.Z || b.Min.Z > Bounds.Max.Z)
        {
            return false;
        }
        var q = new OverlapQuery(_store, b, below, above);
        Grid.ForEachInRect(b.Min.X, b.Min.Z, b.Max.X, b.Max.Z, null, ref q);
        floorTop = q.FloorTop;
        return q.Found;
    }

    private List<(Vector3 a, Vector3 b)> BuildBoundary()
    {
        var tris = _store.Span;
        var eps = WeldEps;
        var edgeKeys = new ulong[3 * Triangles.Length];
        Dictionary<ulong, int> edgeCount = new(edgeKeys.Length);
        for (var i = 0; i < Triangles.Length; ++i)
        {
            ref readonly var t = ref tris[Triangles[i]];
            var ka = TriangleAdjacency.WeldKey(t.A, eps);
            var kb = TriangleAdjacency.WeldKey(t.B, eps);
            var kc = TriangleAdjacency.WeldKey(t.C, eps);
            edgeKeys[3 * i] = TriangleAdjacency.EdgeKey(ka, kb);
            edgeKeys[3 * i + 1] = TriangleAdjacency.EdgeKey(kb, kc);
            edgeKeys[3 * i + 2] = TriangleAdjacency.EdgeKey(kc, ka);
            for (var k = 0; k < 3; ++k)
            {
                ++CollectionsMarshal.GetValueRefOrAddDefault(edgeCount, edgeKeys[3 * i + k], out _);
            }
        }
        List<(Vector3 a, Vector3 b)> boundary = [];
        Dictionary<ulong, float> keys = [];
        for (var i = 0; i < Triangles.Length; ++i)
        {
            ref readonly var t = ref tris[Triangles[i]];
            if (edgeCount[edgeKeys[3 * i]] == 1) { boundary.Add((t.A, t.B)); keys[edgeKeys[3 * i]] = MathF.Min(t.A.Y, t.B.Y); }
            if (edgeCount[edgeKeys[3 * i + 1]] == 1) { boundary.Add((t.B, t.C)); keys[edgeKeys[3 * i + 1]] = MathF.Min(t.B.Y, t.C.Y); }
            if (edgeCount[edgeKeys[3 * i + 2]] == 1) { boundary.Add((t.C, t.A)); keys[edgeKeys[3 * i + 2]] = MathF.Min(t.C.Y, t.A.Y); }
        }
        _boundaryKeys = keys;
        return boundary;
    }
}
