namespace BossMod;

// uniform XZ grid over one mesh's triangles (CSR layout) for hover/click/rect/brush picking in the offline editor
public sealed class TriangleGrid
{
    public const float CellSize = 2f;
    public readonly float MinX, MinZ;
    public readonly int CellsX, CellsZ;
    private readonly int[] _cellStart;
    private readonly int[] _items;

    public TriangleGrid(ReadOnlySpan<WorldTriangle> tris, int first, int count, in Bounds3 bounds)
    {
        MinX = bounds.Min.X;
        MinZ = bounds.Min.Z;
        CellsX = Math.Max(1, (int)MathF.Ceiling((bounds.Max.X - MinX) / CellSize) + 1);
        CellsZ = Math.Max(1, (int)MathF.Ceiling((bounds.Max.Z - MinZ) / CellSize) + 1);
        var counts = new int[CellsX * CellsZ + 1];
        var end = first + count;
        for (var i = first; i < end; ++i)
        {
            ref readonly var t = ref tris[i];
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
        for (var i = first; i < end; ++i)
        {
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
}

public sealed class ZoneTrianglePicker(ZoneCollisionScene scene)
{
    private readonly Dictionary<int, TriangleGrid> _grids = [];
    private int[] _visitStamp = new int[scene.Triangles.Count];
    private int _stamp;

    public ZoneCollisionScene Scene => scene;

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
        }
        return grid;
    }

    private void NextStamp()
    {
        if (_visitStamp.Length < scene.Triangles.Count)
        {
            Array.Resize(ref _visitStamp, scene.Triangles.Count);
        }
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_visitStamp);
            _stamp = 1;
        }
    }

    // all triangles containing the point in XZ, sorted by Y descending; returns the topmost or -1
    public int PickTriangle(WPos p, List<int> hits, Predicate<int>? filter)
    {
        hits.Clear();
        var tris = scene.Triangles.Span;
        var meshes = scene.Meshes;
        NextStamp();
        for (var m = 0; m < meshes.Count; ++m)
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
                if (_visitStamp[i] == _stamp)
                {
                    continue;
                }
                _visitStamp[i] = _stamp;
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
            var ys = new float[hits.Count];
            var order = new int[hits.Count];
            for (var i = 0; i < hits.Count; ++i)
            {
                ys[i] = -tris[hits[i]].YAt(p.X, p.Z); // descending Y
                order[i] = hits[i];
            }
            Array.Sort(ys, order);
            for (var i = 0; i < hits.Count; ++i)
            {
                hits[i] = order[i];
            }
        }
        return hits.Count > 0 ? hits[0] : -1;
    }

    public bool NearestVertex(WPos p, float maxDist, Predicate<int>? filter, out Vector3 vertex, out int triangle)
    {
        vertex = default;
        triangle = -1;
        var best = maxDist * maxDist;
        var tris = scene.Triangles.Span;
        var meshes = scene.Meshes;
        NextStamp();
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (!scene.IsMeshEnabled(m) || !mesh.WorldBounds.IntersectsXZCircle(new(p.X, p.Z), maxDist))
            {
                continue;
            }
            var grid = Grid(m);
            if (grid == null)
            {
                continue;
            }
            grid.CellsInRect(p.X - maxDist, p.Z - maxDist, p.X + maxDist, p.Z + maxDist, out var cx0, out var cz0, out var cx1, out var cz1);
            for (var cz = cz0; cz <= cz1; ++cz)
            {
                for (var cx = cx0; cx <= cx1; ++cx)
                {
                    foreach (var i in grid.Cell(cx, cz))
                    {
                        if (_visitStamp[i] == _stamp)
                        {
                            continue;
                        }
                        _visitStamp[i] = _stamp;
                        if (filter != null && !filter(i))
                        {
                            continue;
                        }
                        ref readonly var t = ref tris[i];
                        Consider(t.A, p, ref best, ref vertex, ref triangle, i);
                        Consider(t.B, p, ref best, ref vertex, ref triangle, i);
                        Consider(t.C, p, ref best, ref vertex, ref triangle, i);
                    }
                }
            }
        }
        return triangle >= 0;

        static void Consider(in Vector3 v, WPos p, ref float best, ref Vector3 vertex, ref int triangle, int i)
        {
            var dx = v.X - p.X;
            var dz = v.Z - p.Z;
            var d = dx * dx + dz * dz;
            if (d < best)
            {
                best = d;
                vertex = v;
                triangle = i;
            }
        }
    }

    public void CentroidsInRect(WPos min, WPos max, Predicate<int>? filter, List<int> dst)
    {
        var tris = scene.Triangles.Span;
        var meshes = scene.Meshes;
        NextStamp();
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (!scene.IsMeshEnabled(m) || mesh.WorldBounds.Max.X < min.X || mesh.WorldBounds.Min.X > max.X || mesh.WorldBounds.Max.Z < min.Z || mesh.WorldBounds.Min.Z > max.Z)
            {
                continue;
            }
            var grid = Grid(m);
            if (grid == null)
            {
                continue;
            }
            grid.CellsInRect(min.X, min.Z, max.X, max.Z, out var cx0, out var cz0, out var cx1, out var cz1);
            for (var cz = cz0; cz <= cz1; ++cz)
            {
                for (var cx = cx0; cx <= cx1; ++cx)
                {
                    foreach (var i in grid.Cell(cx, cz))
                    {
                        if (_visitStamp[i] == _stamp)
                        {
                            continue;
                        }
                        _visitStamp[i] = _stamp;
                        if (filter != null && !filter(i))
                        {
                            continue;
                        }
                        var c = tris[i].Centroid;
                        if (c.X >= min.X && c.X <= max.X && c.Z >= min.Z && c.Z <= max.Z)
                        {
                            dst.Add(i);
                        }
                    }
                }
            }
        }
    }

    public void CentroidsInCircle(WPos c, float r, Predicate<int>? filter, List<int> dst)
    {
        var tris = scene.Triangles.Span;
        var meshes = scene.Meshes;
        var r2 = r * r;
        NextStamp();
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (!scene.IsMeshEnabled(m) || !mesh.WorldBounds.IntersectsXZCircle(new(c.X, c.Z), r))
            {
                continue;
            }
            var grid = Grid(m);
            if (grid == null)
            {
                continue;
            }
            grid.CellsInRect(c.X - r, c.Z - r, c.X + r, c.Z + r, out var cx0, out var cz0, out var cx1, out var cz1);
            for (var cz = cz0; cz <= cz1; ++cz)
            {
                for (var cx = cx0; cx <= cx1; ++cx)
                {
                    foreach (var i in grid.Cell(cx, cz))
                    {
                        if (_visitStamp[i] == _stamp)
                        {
                            continue;
                        }
                        _visitStamp[i] = _stamp;
                        if (filter != null && !filter(i))
                        {
                            continue;
                        }
                        var cc = tris[i].Centroid;
                        var dx = cc.X - c.X;
                        var dz = cc.Z - c.Z;
                        if (dx * dx + dz * dz <= r2)
                        {
                            dst.Add(i);
                        }
                    }
                }
            }
        }
    }
}
