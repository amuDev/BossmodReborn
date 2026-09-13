namespace BossMod;

// offline collision scene of a territory: layout instances placed in world space, with a flat triangle store for editing/auto-mapping
// written only by the load task; afterwards the UI thread owns it (layer toggles, lazy terrain tiles)
public sealed class ZoneLayer
{
    public int Index;
    public string SourceFile = "";
    public uint GroupId;
    public ushort Key;
    public string Name = "";
    public ushort FestivalId, FestivalPhase;
    public int InstanceCount;
    public bool Enabled;
    public string SharedGroupChain = ""; // "" for top-level layers, else the chain of shared-group instance names that own this layer
    public bool IsTerrain;
}

public sealed class ZoneMeshInstance
{
    public int Index;
    public string PcbPath = "";
    public string Name = "";
    public ulong LayoutObjectId;
    public int LayerIndex;
    public int SourceInstanceType;
    public uint InstanceKey;
    public ushort LayerKey;
    public Matrix4x4 World = Matrix4x4.Identity;
    public Matrix4x4 ParentWorld = Matrix4x4.Identity; // shared-group parent chain, identity for top-level instances
    public Vector3 Translation, RotationEuler, Scale = Vector3.One; // file-space values, kept for the validator
    public ulong ObjMatValue, ObjMatMask;
    public bool IsTerrainTile;
    public uint TerrainMeshId;
    public Bounds3 WorldBounds;
    public PcbMesh Mesh = null!;
    public int TriStart = -1, TriCount;
    public bool ActiveByDefault = true;
}

public sealed class ZoneBoxInstance
{
    public int Index;
    public string Name = "";
    public ulong LayoutObjectId;
    public int LayerIndex;
    public uint InstanceKey;
    public ushort LayerKey;
    public Matrix4x4 World = Matrix4x4.Identity;
    public Vector3 Center, HalfExtents;
    public Vector3 Translation, RotationEuler, Scale = Vector3.One;
    public ulong MatValue, MatMask;
    public LgbColliderKind Kind;
    public bool ActiveByDefault = true;
    public Vector3[] Corners = new Vector3[8]; // world corners of the local box, same order as the live tab's _boxCorners
    public Bounds3 WorldBounds;
    public Bounds3 LocalBounds = new(new(-1f), new(1f)); // unit cube for collision boxes; analytic bg-part boxes carry their own bounds
    public bool IsAnalytic;

    // (-1,-1,-1) (-1,-1,1) (-1,1,-1) (-1,1,1) (1,-1,-1) (1,-1,1) (1,1,-1) (1,1,1)
    public static readonly Vector3[] UnitCorners =
    [
        new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, -1), new(-1, 1, 1),
        new(1, -1, -1), new(1, -1, 1), new(1, 1, -1), new(1, 1, 1),
    ];
}

// layout instances that are not colliders but help locate arenas: exit ranges (dungeon exits), pop ranges (spawn points), event objects
public sealed class ZoneMarker
{
    public int Index;
    public int Type;
    public string Name = "";
    public ulong LayoutObjectId;
    public int LayerIndex;
    public Vector3 Position;

    public string TypeName => Type switch
    {
        (int)LgbInstanceType.ExitRange => "exit range",
        (int)LgbInstanceType.PopRange => "pop range",
        (int)LgbInstanceType.EventObject => "event object",
        _ => $"type {Type}",
    };
}

public sealed class ZoneAnalyticInstance
{
    public int Index;
    public string Name = "";
    public ulong LayoutObjectId;
    public int LayerIndex;
    public LgbColliderKind Kind;
    public Matrix4x4 World = Matrix4x4.Identity;
    public ulong MatValue, MatMask;
    public Bounds3 WorldBounds;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct WorldTriangle(Vector3 a, Vector3 b, Vector3 c, ulong material, ulong effective, float normalY, int meshIndex)
{
    public readonly Vector3 A = a, B = b, C = c;
    public readonly ulong Material = material;   // raw primitive material
    public readonly ulong Effective = effective; // (Material & ~objMask) | (objValue & objMask)
    public readonly float NormalY = normalY;     // |normalised cross(B-A, C-A)|.Y
    public readonly int MeshIndex = meshIndex;

    public Vector3 Centroid => (A + B + C) * (1f / 3f);

    public Bounds3 Bounds => new(Vector3.Min(A, Vector3.Min(B, C)), Vector3.Max(A, Vector3.Max(B, C)));

    public bool ContainsXZ(float x, float z)
    {
        var d1 = (x - B.X) * (A.Z - B.Z) - (A.X - B.X) * (z - B.Z);
        var d2 = (x - C.X) * (B.Z - C.Z) - (B.X - C.X) * (z - C.Z);
        var d3 = (x - A.X) * (C.Z - A.Z) - (C.X - A.X) * (z - A.Z);
        var hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
        var hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(hasNeg && hasPos);
    }

    // barycentric Y at (x, z); assumes ContainsXZ
    public float YAt(float x, float z)
    {
        var det = (B.Z - C.Z) * (A.X - C.X) + (C.X - B.X) * (A.Z - C.Z);
        if (MathF.Abs(det) < 1e-9f)
        {
            return Centroid.Y;
        }
        var l1 = ((B.Z - C.Z) * (x - C.X) + (C.X - B.X) * (z - C.Z)) / det;
        var l2 = ((C.Z - A.Z) * (x - C.X) + (A.X - C.X) * (z - C.Z)) / det;
        return l1 * A.Y + l2 * B.Y + (1f - l1 - l2) * C.Y;
    }
}

public sealed class ZoneTriangleStore
{
    private WorldTriangle[] _tris = new WorldTriangle[1 << 16];
    private int _count;

    public int Count => _count;
    public ReadOnlySpan<WorldTriangle> Span => _tris.AsSpan(0, _count);
    public ref readonly WorldTriangle this[int i] => ref _tris[i];

    // transforms the instance's mesh into world space and appends it; sets TriStart/TriCount/WorldBounds on the instance
    public int Append(ZoneMeshInstance inst)
    {
        var mesh = inst.Mesh;
        var n = mesh.TriangleCount;
        if (_count + n > _tris.Length)
        {
            Array.Resize(ref _tris, Math.Max(_tris.Length * 2, _count + n));
        }
        var start = _count;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var world = inst.World;
        var objMask = inst.ObjMatMask;
        var objValue = inst.ObjMatValue & objMask;
        var idx = mesh.Indices;
        var verts = mesh.Vertices;
        for (var i = 0; i < n; ++i)
        {
            var a = Vector3.Transform(verts[idx[3 * i]], world);
            var b = Vector3.Transform(verts[idx[3 * i + 1]], world);
            var c = Vector3.Transform(verts[idx[3 * i + 2]], world);
            var normal = Vector3.Cross(b - a, c - a);
            var len = normal.Length();
            var normalY = len > 1e-12f ? MathF.Abs(normal.Y / len) : 0f;
            var material = mesh.Materials[i];
            _tris[_count++] = new(a, b, c, material, (material & ~objMask) | objValue, normalY, inst.Index);
            min = Vector3.Min(min, Vector3.Min(a, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(a, Vector3.Max(b, c)));
        }
        inst.TriStart = start;
        inst.TriCount = n;
        inst.WorldBounds = n > 0 ? new(min, max) : Bounds3.Transform(mesh.LocalBounds, world);
        return start;
    }
}

public sealed class ZoneLoadReport
{
    public int LgbFiles, SgbFiles, Layers, Instances, BgParts, BgPartsWithMesh, BgPartsAnalytic, CollisionBoxes, SharedGroups, SharedGroupDepthMax, PcbFilesLoaded, PcbFilesMissing, TerrainTilesListed, PcbSelfCheckFailures;
    public readonly Dictionary<int, int> UnparsedTypes = [];
    public readonly List<string> Warnings = [];
    public long ParseMs, TransformMs;
    public readonly List<string> ResolvedPaths = [];
}

public sealed class ZoneCollisionScene
{
    public uint TerritoryId;
    public string Bg = "";
    public string LevelDir = "";
    public string CollisionDir = "";
    public readonly List<ZoneLayer> Layers = [];
    public readonly List<ZoneMeshInstance> Meshes = [];
    public readonly List<ZoneBoxInstance> Boxes = [];
    public readonly List<ZoneAnalyticInstance> Analytics = [];
    public readonly List<ZoneMarker> Markers = [];
    public PcbList? TerrainList;
    public readonly Dictionary<uint, int> TerrainTileMeshIndex = [];
    public readonly Dictionary<string, PcbMesh> PcbCache = [];
    public readonly ZoneTriangleStore Triangles = new();
    public readonly ZoneLoadReport Report = new();
    public int TerrainLayerIndex = -1;
    public Bounds3 Bounds;

    public static ulong MakeLayoutObjectId(int type, ushort layerKey, uint instanceKey) => ((ulong)instanceKey << 32) | ((ulong)layerKey << 16) | ((ulong)(byte)type << 8);

    public static (uint instanceKey, ushort layerKey, byte type) DecodeLayoutObjectId(ulong id) => ((uint)(id >> 32), (ushort)(id >> 16), (byte)(id >> 8));

    public bool IsMeshEnabled(int meshIndex) => Layers[Meshes[meshIndex].LayerIndex].Enabled;
    public bool IsBoxEnabled(int boxIndex) => Layers[Boxes[boxIndex].LayerIndex].Enabled;

    public void RecomputeBounds()
    {
        Bounds3? b = null;
        var n = Meshes.Count;
        for (var i = 0; i < n; ++i)
        {
            b = b == null ? Meshes[i].WorldBounds : Bounds3.Union(b.Value, Meshes[i].WorldBounds);
        }
        Bounds = b ?? default;
    }
}
