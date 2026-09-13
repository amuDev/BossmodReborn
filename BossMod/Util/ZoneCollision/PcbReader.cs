namespace BossMod;

// collision mesh file (.pcb): a tree of nodes, each with raw/compressed vertices and triangles carrying a 64-bit material
public sealed class PcbMesh
{
    public string Path = "";
    public Vector3[] Vertices = [];   // local space, flattened over nodes, compressed ones already decoded
    public int[] Indices = [];        // 3 per triangle, into Vertices
    public ulong[] Materials = [];    // per triangle, raw primitive material
    public Bounds3 LocalBounds;
    public int NodeCount, HeaderNodes, HeaderPolygons;
    public int TriangleCount => Materials.Length;
    public bool SelfCheckOk;
    public string SelfCheck = "";
}

public readonly record struct PcbListEntry(uint MeshId, Bounds3 Bounds);

public sealed class PcbList
{
    public Bounds3 Bounds;
    public PcbListEntry[] Entries = [];
}

public readonly record struct PcbNodeHeader(int Offset, ulong Header, int Child1Offset, int Child2Offset, Bounds3 Bounds, int VertsCompressed, int Prims, int VertsRaw);

public static class PcbReader
{
    public const int HeaderSize = 0x10;
    public const int NodeHeaderSize = 0x30;
    public const int PrimSize = 12;

    public static PcbNodeHeader ReadNodeHeader(ReadOnlySpan<byte> d, int n) => new(n, ZoneBinary.U64(d, n), ZoneBinary.I32(d, n + 8), ZoneBinary.I32(d, n + 0xc),
        new(ZoneBinary.Vec3(d, n + 0x10), ZoneBinary.Vec3(d, n + 0x1c)), ZoneBinary.U16(d, n + 0x28), ZoneBinary.U16(d, n + 0x2a), ZoneBinary.U16(d, n + 0x2c));

    public static PcbMesh ParseMesh(string path, ReadOnlySpan<byte> data)
    {
        var mesh = new PcbMesh { Path = path };
        if (data.Length < HeaderSize + NodeHeaderSize)
        {
            throw new ZoneFormatException($"{path}: file too small ({data.Length} bytes)");
        }
        mesh.HeaderNodes = (int)ZoneBinary.U32(data, 8);
        mesh.HeaderPolygons = (int)ZoneBinary.U32(data, 0xc);

        List<Vector3> verts = new(Math.Max(16, mesh.HeaderPolygons * 2));
        List<int> indices = new(Math.Max(48, mesh.HeaderPolygons * 3));
        List<ulong> materials = new(Math.Max(16, mesh.HeaderPolygons));
        var stack = new Stack<int>();
        stack.Push(HeaderSize);
        var visited = 0;
        var maxNodes = Math.Max(mesh.HeaderNodes * 2 + 16, 1 << 16);
        Bounds3? bounds = null;
        var childCheckFailures = 0;
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (++visited > maxNodes)
            {
                throw new ZoneFormatException($"{path}: node walk exceeded {maxNodes} nodes (corrupt child offsets?)");
            }
            var h = ReadNodeHeader(data, n);
            bounds = bounds == null ? h.Bounds : Bounds3.Union(bounds.Value, h.Bounds);
            var vertBase = verts.Count;
            var p = n + NodeHeaderSize;
            for (var i = 0; i < h.VertsRaw; ++i)
            {
                verts.Add(ZoneBinary.Vec3(data, p));
                p += 12;
            }
            var scale = h.Bounds.Size / 65535f;
            for (var i = 0; i < h.VertsCompressed; ++i)
            {
                verts.Add(new(h.Bounds.Min.X + ZoneBinary.U16(data, p) * scale.X, h.Bounds.Min.Y + ZoneBinary.U16(data, p + 2) * scale.Y, h.Bounds.Min.Z + ZoneBinary.U16(data, p + 4) * scale.Z));
                p += 6;
            }
            var vertCount = h.VertsRaw + h.VertsCompressed;
            for (var i = 0; i < h.Prims; ++i)
            {
                int v1 = data[p], v2 = data[p + 1], v3 = data[p + 2];
                if (v1 >= vertCount || v2 >= vertCount || v3 >= vertCount)
                {
                    throw new ZoneFormatException($"{path}: node at 0x{n:X} primitive {i} references vertex beyond {vertCount}");
                }
                indices.Add(vertBase + v1);
                indices.Add(vertBase + v2);
                indices.Add(vertBase + v3);
                materials.Add(ZoneBinary.U64(data, p + 4));
                p += PrimSize;
            }
            // a node's first child is expected to start right after its own payload
            if (h.Child1Offset != 0 && n + h.Child1Offset != p)
            {
                ++childCheckFailures;
            }
            if (h.Child2Offset != 0)
            {
                stack.Push(n + h.Child2Offset);
            }
            if (h.Child1Offset != 0)
            {
                stack.Push(n + h.Child1Offset);
            }
        }
        mesh.Vertices = [.. verts];
        mesh.Indices = [.. indices];
        mesh.Materials = [.. materials];
        mesh.NodeCount = visited;
        mesh.LocalBounds = bounds ?? default;
        // the header node count excludes the root for single-node files (0); accept either convention until the validator pins it
        mesh.SelfCheckOk = (visited == mesh.HeaderNodes || visited == mesh.HeaderNodes + 1) && materials.Count == mesh.HeaderPolygons && childCheckFailures == 0;
        mesh.SelfCheck = $"nodes {visited}/{mesh.HeaderNodes}, prims {materials.Count}/{mesh.HeaderPolygons}, child-offset mismatches {childCheckFailures}";
        return mesh;
    }

    // list.pcb of a streamed terrain collision directory: FileHeader { int NumMeshes; AABB Bounds } (0x20) then FileEntry { int MeshId; AABB Bounds } (0x20 each), as in ColliderStreamed
    public static PcbList ParseList(ReadOnlySpan<byte> data)
    {
        var list = new PcbList();
        var count = (int)ZoneBinary.U32(data, 0);
        if (count < 0 || count > 1 << 16)
        {
            throw new ZoneFormatException($"list.pcb: implausible entry count {count}");
        }
        list.Bounds = new(ZoneBinary.Vec3(data, 4), ZoneBinary.Vec3(data, 0x10));
        list.Entries = new PcbListEntry[count];
        var p = 0x20;
        for (var i = 0; i < count; ++i)
        {
            list.Entries[i] = new(ZoneBinary.U32(data, p), new(ZoneBinary.Vec3(data, p + 4), ZoneBinary.Vec3(data, p + 0x10)));
            p += 0x20;
        }
        return list;
    }
}
