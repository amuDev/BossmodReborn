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
        // the header count excludes the root for single-node files (0), so the walk may see one node more; every node needs its own header bytes
        if (mesh.HeaderNodes < 0 || mesh.HeaderNodes > (data.Length - HeaderSize) / NodeHeaderSize)
        {
            throw new ZoneFormatException($"{path}: implausible node count {mesh.HeaderNodes} for {data.Length} bytes");
        }
        var maxNodes = mesh.HeaderNodes + 1;

        // first pass: the node tree (depth-first, child 1 before child 2) and the totals to size the arrays
        var nodes = new List<PcbNodeHeader>(maxNodes);
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(HeaderSize);
        var vertTotal = 0;
        var primTotal = 0;
        var childCheckFailures = 0;
        var bounds = Bounds3.Empty;
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!seen.Add(n))
            {
                throw new ZoneFormatException($"{path}: node at 0x{n:X} reached twice (child offset cycle)");
            }
            if (nodes.Count >= maxNodes)
            {
                throw new ZoneFormatException($"{path}: node walk exceeded the header count {mesh.HeaderNodes} (corrupt child offsets?)");
            }
            var h = ReadNodeHeader(data, n);
            nodes.Add(h);
            bounds = Bounds3.Union(bounds, h.Bounds);
            vertTotal += h.VertsRaw + h.VertsCompressed;
            primTotal += h.Prims;
            var end = n + NodeHeaderSize + h.VertsRaw * 12 + h.VertsCompressed * 6 + h.Prims * PrimSize;
            if (end > data.Length)
            {
                throw new ZoneFormatException($"{path}: node at 0x{n:X} payload ends at 0x{end:X} outside file of {data.Length} bytes");
            }
            // a node's first child is expected to start right after its own payload
            if (h.Child1Offset != 0 && n + h.Child1Offset != end)
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

        // second pass: decode straight into the arrays
        var verts = new Vector3[vertTotal];
        var indices = new int[primTotal * 3];
        var materials = new ulong[primTotal];
        var vi = 0;
        var pi = 0;
        for (var k = 0; k < nodes.Count; ++k)
        {
            var h = nodes[k];
            var n = h.Offset;
            var vertBase = vi;
            var p = n + NodeHeaderSize;
            for (var i = 0; i < h.VertsRaw; ++i)
            {
                verts[vi++] = ZoneBinary.Vec3(data, p);
                p += 12;
            }
            var scale = h.Bounds.Size / 65535f;
            for (var i = 0; i < h.VertsCompressed; ++i)
            {
                verts[vi++] = new(h.Bounds.Min.X + ZoneBinary.U16(data, p) * scale.X, h.Bounds.Min.Y + ZoneBinary.U16(data, p + 2) * scale.Y, h.Bounds.Min.Z + ZoneBinary.U16(data, p + 4) * scale.Z);
                p += 6;
            }
            var vertCount = h.VertsRaw + h.VertsCompressed;
            for (var i = 0; i < h.Prims; ++i)
            {
                int v1 = ZoneBinary.U8(data, p), v2 = ZoneBinary.U8(data, p + 1), v3 = ZoneBinary.U8(data, p + 2);
                if (v1 >= vertCount || v2 >= vertCount || v3 >= vertCount)
                {
                    throw new ZoneFormatException($"{path}: node at 0x{n:X} primitive {i} references vertex beyond {vertCount}");
                }
                indices[3 * pi] = vertBase + v1;
                indices[3 * pi + 1] = vertBase + v2;
                indices[3 * pi + 2] = vertBase + v3;
                materials[pi++] = ZoneBinary.U64(data, p + 4);
                p += PrimSize;
            }
        }
        mesh.Vertices = verts;
        mesh.Indices = indices;
        mesh.Materials = materials;
        mesh.NodeCount = nodes.Count;
        mesh.LocalBounds = nodes.Count > 0 ? bounds : default;
        // the header node count excludes the root for single-node files (0); both conventions are accepted
        mesh.SelfCheckOk = (nodes.Count == mesh.HeaderNodes || nodes.Count == mesh.HeaderNodes + 1) && primTotal == mesh.HeaderPolygons && childCheckFailures == 0;
        mesh.SelfCheck = $"nodes {nodes.Count}/{mesh.HeaderNodes}, prims {primTotal}/{mesh.HeaderPolygons}, child-offset mismatches {childCheckFailures}";
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
