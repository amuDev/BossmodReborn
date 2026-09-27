namespace BossMod;

// receives the items a grid rect query finds (struct implementations keep the query loops allocation-free)
public interface ITriangleVisitor
{
    void Visit(int triangle);
}

// uniform XZ grid (CSR layout) over world triangles: one mesh's contiguous range, or an arbitrary index list (a selection, the candidate set, the
// walls near a room); the same grid serves picking in the editor and the proximity queries of the auto-mapper
public sealed class TriangleGrid
{
    public const float CellSize = 2f;
    public readonly float MinX, MinZ;
    public readonly int CellsX, CellsZ;
    private readonly int[] _cellStart;
    private readonly int[] _items;

    public TriangleGrid(ReadOnlySpan<WorldTriangle> tris, int first, int count, in Bounds3 bounds) : this(tris, default, first, count, bounds) { }

    public TriangleGrid(ReadOnlySpan<WorldTriangle> tris, ReadOnlySpan<int> indices) : this(tris, indices, BoundsOf(tris, indices)) { }

    public TriangleGrid(ReadOnlySpan<WorldTriangle> tris, ReadOnlySpan<int> indices, in Bounds3 bounds) : this(tris, indices, -1, indices.Length, bounds) { }

    // first < 0: the items are indices[k]; otherwise the items are first + k
    private TriangleGrid(ReadOnlySpan<WorldTriangle> tris, ReadOnlySpan<int> indices, int first, int count, in Bounds3 bounds)
    {
        MinX = bounds.Min.X;
        MinZ = bounds.Min.Z;
        CellsX = Math.Max(1, (int)MathF.Ceiling((bounds.Max.X - MinX) / CellSize) + 1);
        CellsZ = Math.Max(1, (int)MathF.Ceiling((bounds.Max.Z - MinZ) / CellSize) + 1);
        var counts = new int[CellsX * CellsZ + 1];
        for (var k = 0; k < count; ++k)
        {
            ref readonly var t = ref tris[first < 0 ? indices[k] : first + k];
            CellRange(t, out var x0, out var z0, out var x1, out var z1);
            for (var z = z0; z <= z1; ++z)
            {
                for (var x = x0; x <= x1; ++x)
                {
                    ++counts[z * CellsX + x + 1];
                }
            }
        }
        for (var c = 1; c < counts.Length; ++c)
        {
            counts[c] += counts[c - 1];
        }
        _cellStart = counts;
        _items = new int[_cellStart[^1]];
        var fill = new int[CellsX * CellsZ];
        for (var k = 0; k < count; ++k)
        {
            var i = first < 0 ? indices[k] : first + k;
            ref readonly var t = ref tris[i];
            CellRange(t, out var x0, out var z0, out var x1, out var z1);
            for (var z = z0; z <= z1; ++z)
            {
                for (var x = x0; x <= x1; ++x)
                {
                    var cell = z * CellsX + x;
                    _items[_cellStart[cell] + fill[cell]++] = i;
                }
            }
        }
    }

    public static Bounds3 BoundsOf(ReadOnlySpan<WorldTriangle> tris, ReadOnlySpan<int> indices)
    {
        if (indices.Length == 0)
        {
            return default;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var k = 0; k < indices.Length; ++k)
        {
            var b = tris[indices[k]].Bounds;
            min = Vector3.Min(min, b.Min);
            max = Vector3.Max(max, b.Max);
        }
        return new(min, max);
    }

    private void CellRange(in WorldTriangle t, out int x0, out int z0, out int x1, out int z1)
    {
        var minX = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X));
        var maxX = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
        var minZ = MathF.Min(t.A.Z, MathF.Min(t.B.Z, t.C.Z));
        var maxZ = MathF.Max(t.A.Z, MathF.Max(t.B.Z, t.C.Z));
        x0 = Math.Clamp((int)((minX - MinX) / CellSize), 0, CellsX - 1);
        x1 = Math.Clamp((int)((maxX - MinX) / CellSize), 0, CellsX - 1);
        z0 = Math.Clamp((int)((minZ - MinZ) / CellSize), 0, CellsZ - 1);
        z1 = Math.Clamp((int)((maxZ - MinZ) / CellSize), 0, CellsZ - 1);
    }

    public void CellsInRect(float minX, float minZ, float maxX, float maxZ, out int cx0, out int cz0, out int cx1, out int cz1)
    {
        cx0 = Math.Clamp((int)((minX - MinX) / CellSize), 0, CellsX - 1);
        cx1 = Math.Clamp((int)((maxX - MinX) / CellSize), 0, CellsX - 1);
        cz0 = Math.Clamp((int)((minZ - MinZ) / CellSize), 0, CellsZ - 1);
        cz1 = Math.Clamp((int)((maxZ - MinZ) / CellSize), 0, CellsZ - 1);
    }

    public ReadOnlySpan<int> Cell(int cx, int cz)
    {
        var cell = cz * CellsX + cx;
        return _items.AsSpan(_cellStart[cell], _cellStart[cell + 1] - _cellStart[cell]);
    }

    // every item whose cells overlap the rect, each once when a stamp is given (Next() is called here), else once per cell it spans
    public void ForEachInRect<T>(float minX, float minZ, float maxX, float maxZ, VisitStamp? visited, ref T visitor) where T : struct, ITriangleVisitor
    {
        visited?.Next();
        CellsInRect(minX, minZ, maxX, maxZ, out var cx0, out var cz0, out var cx1, out var cz1);
        for (var cz = cz0; cz <= cz1; ++cz)
        {
            for (var cx = cx0; cx <= cx1; ++cx)
            {
                foreach (var i in Cell(cx, cz))
                {
                    if (visited == null || visited.Visit(i))
                    {
                        visitor.Visit(i);
                    }
                }
            }
        }
    }
}

// dedupes the items a grid query returns from several cells without allocating a set per query
public sealed class VisitStamp(int capacity)
{
    private int[] _stamp = new int[capacity];
    private int _current = 1;

    public void Next()
    {
        if (++_current == int.MaxValue)
        {
            Array.Clear(_stamp);
            _current = 1;
        }
    }

    public void EnsureCapacity(int count)
    {
        if (_stamp.Length < count)
        {
            Array.Resize(ref _stamp, count);
        }
    }

    // true the first time an item is seen since the last Next()
    public bool Visit(int item)
    {
        if (_stamp[item] == _current)
        {
            return false;
        }
        _stamp[item] = _current;
        return true;
    }
}

// receives the cells of a ring search in order of distance; BestDistSq stops the search once no unvisited ring can hold a closer item
public interface IRingVisitor
{
    double BestDistSq { get; }
    void Visit(List<int> ids);
}

// sparse XZ hash grid of integer ids (edges, segments, samples, vertices) over square cells; the id lists are appended in insertion order
public sealed class XZHashGrid(float cellSize)
{
    public readonly float CellSize = cellSize;
    private readonly Dictionary<long, List<int>> _cells = [];
    private int _minCellX = int.MaxValue, _minCellZ = int.MaxValue, _maxCellX = int.MinValue, _maxCellZ = int.MinValue;

    public int CellOf(float v) => (int)MathF.Floor(v / CellSize);
    public static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    public int Count => _cells.Count;

    public void Add(int id, float minX, float minZ, float maxX, float maxZ)
    {
        var cx0 = CellOf(minX);
        var cx1 = CellOf(maxX);
        var cz0 = CellOf(minZ);
        var cz1 = CellOf(maxZ);
        for (var cz = cz0; cz <= cz1; ++cz)
        {
            for (var cx = cx0; cx <= cx1; ++cx)
            {
                GetOrAddCell(cx, cz).Add(id);
            }
        }
    }

    public void AddPoint(int id, float x, float z) => GetOrAddCell(CellOf(x), CellOf(z)).Add(id);

    public List<int>? Cell(int cx, int cz) => _cells.TryGetValue(Key(cx, cz), out var list) ? list : null;

    // the cell's list, created empty when the cell was not used yet (isNew tells the caller so it can skip scanning an empty cell)
    public List<int> GetOrAddCell(int cx, int cz, out bool isNew)
    {
        var key = Key(cx, cz);
        isNew = !_cells.TryGetValue(key, out var list);
        if (isNew)
        {
            _cells[key] = list = [];
            _minCellX = Math.Min(_minCellX, cx);
            _minCellZ = Math.Min(_minCellZ, cz);
            _maxCellX = Math.Max(_maxCellX, cx);
            _maxCellZ = Math.Max(_maxCellZ, cz);
        }
        return list!;
    }

    public List<int> GetOrAddCell(int cx, int cz) => GetOrAddCell(cx, cz, out _);

    // the cells overlapping the rect, in row order; the same id can come back from several cells
    public void ForEachInRect<T>(float minX, float minZ, float maxX, float maxZ, ref T visitor) where T : struct, IRingVisitor
    {
        var cx0 = CellOf(minX);
        var cx1 = CellOf(maxX);
        var cz0 = CellOf(minZ);
        var cz1 = CellOf(maxZ);
        for (var cz = cz0; cz <= cz1; ++cz)
        {
            for (var cx = cx0; cx <= cx1; ++cx)
            {
                if (_cells.TryGetValue(Key(cx, cz), out var list))
                {
                    visitor.Visit(list);
                }
            }
        }
    }

    // rings of cells outward from (cx, cz) until every unvisited cell lies at least sqrt(BestDistSq) away: every item outside the rings scanned
    // so far lies at least (ring - 1) * CellSize away (the query can sit at the edge of its cell); reachSq is compared in the caller's units
    public void NearestRings<T>(int cx, int cz, ref T visitor, double cellSizeInUnits) where T : struct, IRingVisitor
    {
        if (_cells.Count == 0)
        {
            return;
        }
        var maxRing = Math.Max(Math.Max(cx - _minCellX, _maxCellX - cx), Math.Max(cz - _minCellZ, _maxCellZ - cz));
        for (var ring = 0; ring <= maxRing; ++ring)
        {
            var reach = (ring - 1) * cellSizeInUnits;
            if (ring > 0 && visitor.BestDistSq <= reach * reach)
            {
                break;
            }
            for (var dz = -ring; dz <= ring; ++dz)
            {
                var edge = Math.Abs(dz) == ring;
                for (var dx = -ring; dx <= ring; dx += edge ? 1 : 2 * ring)
                {
                    if (_cells.TryGetValue(Key(cx + dx, cz + dz), out var list))
                    {
                        visitor.Visit(list);
                    }
                    if (ring == 0)
                    {
                        break;
                    }
                }
            }
        }
    }
}

// disjoint sets over 0..n-1: path halving on Find, union by rank
public sealed class UnionFind
{
    private readonly int[] _parent;
    private readonly byte[] _rank;

    public UnionFind(int n)
    {
        _parent = new int[n];
        _rank = new byte[n];
        for (var i = 0; i < n; ++i)
        {
            _parent[i] = i;
        }
    }

    public int Find(int x)
    {
        while (_parent[x] != x)
        {
            _parent[x] = _parent[_parent[x]];
            x = _parent[x];
        }
        return x;
    }

    // false when the two were already joined
    public bool Union(int a, int b)
    {
        var x = Find(a);
        var y = Find(b);
        if (x == y)
        {
            return false;
        }
        if (_rank[x] < _rank[y])
        {
            _parent[x] = y;
        }
        else
        {
            _parent[y] = x;
            if (_rank[x] == _rank[y])
            {
                ++_rank[x];
            }
        }
        return true;
    }
}
