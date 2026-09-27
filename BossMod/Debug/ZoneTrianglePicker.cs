namespace BossMod;

// hover/click/rect/brush picking over the loaded scene: one TriangleGrid per mesh, built on first use, and a coarse grid over the mesh
// bounds so a point pick only visits the meshes standing there instead of every mesh of the scene
public sealed class ZoneTrianglePicker(ZoneCollisionScene scene)
{
    private const float MeshCell = 16f;
    private readonly Dictionary<int, TriangleGrid> _grids = [];
    private readonly Dictionary<int, (int start, int count)> _gridKeys = [];
    private readonly VisitStamp _visited = new(scene.Triangles.Count);
    private XZHashGrid? _meshGrid;
    private int _meshGridCount = -1;
    private float[] _sortKeys = [];
    private int[] _sortOrder = [];

    public ZoneCollisionScene Scene => scene;

    // after meshes were appended (terrain tiles): grids of meshes whose triangle range is unchanged stay, the rest rebuild on first use
    public void Refresh()
    {
        List<int> stale = [];
        foreach (var (m, key) in _gridKeys)
        {
            if (m >= scene.Meshes.Count || scene.Meshes[m].TriStart != key.start || scene.Meshes[m].TriCount != key.count)
            {
                stale.Add(m);
            }
        }
        foreach (var m in stale)
        {
            _grids.Remove(m);
            _gridKeys.Remove(m);
        }
        _meshGrid = null;
        _visited.EnsureCapacity(scene.Triangles.Count);
    }

    private TriangleGrid? Grid(int meshIndex)
    {
        var mesh = scene.Meshes[meshIndex];
        if (mesh.TriCount == 0)
        {
            return null;
        }
        if (!_grids.TryGetValue(meshIndex, out var grid))
        {
            grid = new TriangleGrid(scene.Triangles.Span, mesh.TriStart, mesh.TriCount, mesh.WorldBounds);
            _grids[meshIndex] = grid;
            _gridKeys[meshIndex] = (mesh.TriStart, mesh.TriCount);
        }
        return grid;
    }

    // the non-empty meshes by their XZ bounds; rebuilt when meshes were appended without a Refresh
    private XZHashGrid MeshGrid()
    {
        if (_meshGrid == null || _meshGridCount != scene.Meshes.Count)
        {
            _meshGrid = new(MeshCell);
            var meshes = scene.Meshes;
            for (var m = 0; m < meshes.Count; ++m)
            {
                var b = meshes[m].WorldBounds;
                if (meshes[m].TriCount > 0)
                {
                    _meshGrid.Add(m, b.Min.X, b.Min.Z, b.Max.X, b.Max.Z);
                }
            }
            _meshGridCount = meshes.Count;
        }
        return _meshGrid;
    }

    private void NextStamp()
    {
        _visited.EnsureCapacity(scene.Triangles.Count);
        _visited.Next();
    }

    // all triangles containing the point in XZ, sorted by Y descending; returns the topmost or -1
    public int PickTriangle(WPos p, List<int> hits, Predicate<int>? filter)
    {
        hits.Clear();
        var tris = scene.Triangles.Span;
        var meshes = scene.Meshes;
        NextStamp();
        var meshGrid = MeshGrid();
        if (meshGrid.Cell(meshGrid.CellOf(p.X), meshGrid.CellOf(p.Z)) is not { } candidates)
        {
            return -1;
        }
        foreach (var m in candidates)
        {
            var mesh = meshes[m];
            if (!scene.IsMeshEnabled(m) || !mesh.WorldBounds.ContainsXZ(p.X, p.Z))
            {
                continue;
            }
            var grid = Grid(m);
            if (grid == null)
            {
                continue;
            }
            grid.CellsInRect(p.X, p.Z, p.X, p.Z, out var cx, out var cz, out _, out _);
            foreach (var i in grid.Cell(cx, cz))
            {
                if (!_visited.Visit(i))
                {
                    continue;
                }
                if (filter != null && !filter(i))
                {
                    continue;
                }
                if (tris[i].ContainsXZ(p.X, p.Z))
                {
                    hits.Add(i);
                }
            }
        }
        if (hits.Count > 1)
        {
            if (_sortKeys.Length < hits.Count)
            {
                _sortKeys = new float[Math.Max(hits.Count, 2 * _sortKeys.Length)];
                _sortOrder = new int[_sortKeys.Length];
            }
            for (var i = 0; i < hits.Count; ++i)
            {
                _sortKeys[i] = -tris[hits[i]].YAt(p.X, p.Z); // descending Y
                _sortOrder[i] = hits[i];
            }
            Array.Sort(_sortKeys, _sortOrder, 0, hits.Count);
            for (var i = 0; i < hits.Count; ++i)
            {
                hits[i] = _sortOrder[i];
            }
        }
        return hits.Count > 0 ? hits[0] : -1;
    }

    // the triangle under the point whose surface is nearest to preferY (the topmost when preferY is null), or -1
    public int PickTriangleNearY(WPos p, float? preferY, List<int> scratch)
    {
        PickTriangle(p, scratch, null);
        if (scratch.Count == 0)
        {
            return -1;
        }
        if (preferY is not { } py)
        {
            return scratch[0];
        }
        var tris = scene.Triangles.Span;
        var best = -1;
        var bestScore = float.MaxValue;
        for (var i = 0; i < scratch.Count; ++i)
        {
            var score = MathF.Abs(tris[scratch[i]].YAt(p.X, p.Z) - py);
            if (score < bestScore)
            {
                bestScore = score;
                best = scratch[i];
            }
        }
        return best;
    }

    private struct VertexQuery(ZoneTriangleStore store, VisitStamp visited, Predicate<int>? filter, WPos p, float best) : ITriangleVisitor
    {
        public float Best = best;
        public Vector3 Vertex;
        public int Triangle = -1;

        public void Visit(int i)
        {
            if (!visited.Visit(i) || (filter != null && !filter(i)))
            {
                return;
            }
            ref readonly var t = ref store[i];
            Consider(t.A, i);
            Consider(t.B, i);
            Consider(t.C, i);
        }

        private void Consider(in Vector3 v, int i)
        {
            var dx = v.X - p.X;
            var dz = v.Z - p.Z;
            var d = dx * dx + dz * dz;
            if (d < Best)
            {
                Best = d;
                Vertex = v;
                Triangle = i;
            }
        }
    }

    public bool NearestVertex(WPos p, float maxDist, Predicate<int>? filter, out Vector3 vertex, out int triangle)
    {
        var meshes = scene.Meshes;
        NextStamp();
        var q = new VertexQuery(scene.Triangles, _visited, filter, p, maxDist * maxDist);
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (!scene.IsMeshEnabled(m) || !mesh.WorldBounds.IntersectsXZCircle(new(p.X, p.Z), maxDist))
            {
                continue;
            }
            var grid = Grid(m);
            grid?.ForEachInRect(p.X - maxDist, p.Z - maxDist, p.X + maxDist, p.Z + maxDist, null, ref q);
        }
        vertex = q.Vertex;
        triangle = q.Triangle;
        return triangle >= 0;
    }

    // centroids inside the rect, or inside the circle when r > 0
    private struct CentroidQuery(ZoneTriangleStore store, VisitStamp visited, Predicate<int>? filter, WPos min, WPos max, WPos centre, float r2, List<int> dst) : ITriangleVisitor
    {
        public void Visit(int i)
        {
            if (!visited.Visit(i) || (filter != null && !filter(i)))
            {
                return;
            }
            var c = store[i].Centroid;
            if (r2 > 0f)
            {
                var dx = c.X - centre.X;
                var dz = c.Z - centre.Z;
                if (dx * dx + dz * dz <= r2)
                {
                    dst.Add(i);
                }
            }
            else if (c.X >= min.X && c.X <= max.X && c.Z >= min.Z && c.Z <= max.Z)
            {
                dst.Add(i);
            }
        }
    }

    public void CentroidsInRect(WPos min, WPos max, Predicate<int>? filter, List<int> dst)
    {
        var meshes = scene.Meshes;
        NextStamp();
        var q = new CentroidQuery(scene.Triangles, _visited, filter, min, max, default, 0f, dst);
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (!scene.IsMeshEnabled(m) || mesh.WorldBounds.Max.X < min.X || mesh.WorldBounds.Min.X > max.X || mesh.WorldBounds.Max.Z < min.Z || mesh.WorldBounds.Min.Z > max.Z)
            {
                continue;
            }
            var grid = Grid(m);
            grid?.ForEachInRect(min.X, min.Z, max.X, max.Z, null, ref q);
        }
    }

    public void CentroidsInCircle(WPos c, float r, Predicate<int>? filter, List<int> dst)
    {
        var meshes = scene.Meshes;
        NextStamp();
        var q = new CentroidQuery(scene.Triangles, _visited, filter, default, default, c, r * r, dst);
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (!scene.IsMeshEnabled(m) || !mesh.WorldBounds.IntersectsXZCircle(new(c.X, c.Z), r))
            {
                continue;
            }
            var grid = Grid(m);
            grid?.ForEachInRect(c.X - r, c.Z - r, c.X + r, c.Z + r, null, ref q);
        }
    }
}
