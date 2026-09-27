using Clipper2Lib;

namespace BossMod;

// offline collision scene of a territory: layout instances placed in world space, with a flat triangle store for editing/auto-mapping
// written only by the load task; afterwards the UI thread owns it (layer toggles, lazy terrain tiles)
public sealed class ZoneLayer
{
    public int Index;
    public string SourceFile = "";
    public uint GroupId;
    public uint LayerId;  // full 32-bit layer id; Key is the low word the layout object id uses
    public ushort Key;
    public string Name = "";
    public ushort FestivalId, FestivalPhase;
    public bool IsTemporary;
    public int InstanceCount;
    public bool Enabled;
    public string SharedGroupChain = ""; // "" for top-level layers, else the chain of shared-group instance names that own this layer
    public int ParentNode = -1;          // the shared-group node that instantiated this layer, -1 for top-level layers
    public ulong PathId;                 // stable across loads: hash of the owning node chain, source file and layer id
    public bool IsTerrain;

    public string StablePath => $"{SourceFile}/{LayerId}/{Name}<{SharedGroupChain}>";
}

// one instantiated layout instance (every type, including shared-group instances and trigger ranges), placed in world space; nodes are
// appended depth-first so a node's descendants are [Index + 1, SubtreeEnd)
public sealed class ZoneNode
{
    public int Index;
    public int Parent = -1;
    public int SubtreeEnd;
    public int LayerIndex;
    public int Depth;
    public int Type;
    public uint InstanceKey;
    public string Name = "";
    public Matrix4x4 World = Matrix4x4.Identity;
    public ulong LayoutObjectId; // the game's id form (not unique across shared-group instantiations); matches the live collision scene's object ids
    public ulong PathId;         // unique and stable: hash of the ancestor chain + layer id + instance key
    public ZoneCollisionScene Scene = null!;
    public LgbInstance Source = null!;
    public int MeshIndex = -1, BoxIndex = -1, AnalyticIndex = -1, MarkerIndex = -1;

    public Vector3 Position => World.Translation;
    public string Path => Scene.NodePath(Index); // e.g. "planmap.lgb/LVD_gimmick_01/4554EE>sgpl_w_lvd_b0118_wide.sgb/0/9", built on demand
}

public sealed class ZoneMeshInstance
{
    public int Index;
    public string PcbPath = "";
    public string Name = "";
    public ulong LayoutObjectId;
    public int LayerIndex;
    public int NodeIndex = -1;
    public uint InstanceKey;
    public ushort LayerKey;
    public Matrix4x4 World = Matrix4x4.Identity;
    public Vector3 Translation, RotationEuler, Scale = Vector3.One; // the instance's own transform components as the layout stores them (World includes the parents)
    public ulong ObjMatValue, ObjMatMask;
    public bool IsTerrainTile;
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
    public int NodeIndex = -1;
    public uint InstanceKey;
    public ushort LayerKey;
    public Matrix4x4 World = Matrix4x4.Identity;
    public Vector3 Center, HalfExtents;
    public Vector3 Translation, RotationEuler, Scale = Vector3.One;
    public ulong MatValue, MatMask;
    public LgbColliderKind Kind;
    public bool ActiveByDefault = true;
    public Vector3[] Corners = new Vector3[8]; // world corners of the local box, in UnitCorners order
    public Bounds3 WorldBounds;
    public Bounds3 LocalBounds = new(new(-1f), new(1f)); // unit cube for collision boxes; analytic bg-part boxes carry their own bounds
    public bool IsAnalytic;
    private Path64? _footprint;

    // XZ convex hull of the world corners (counter-clockwise, default clipper scale), computed once; inflate with BoxFootprintOps.Inflate
    public Path64 FootprintXZ => _footprint ??= BoxFootprintOps.BoxFootprint(Corners, 0f);

    // (-1,-1,-1) (-1,-1,1) (-1,1,-1) (-1,1,1) (1,-1,-1) (1,-1,1) (1,1,-1) (1,1,1)
    public static readonly Vector3[] UnitCorners =
    [
        new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, -1), new(-1, 1, 1),
        new(1, -1, -1), new(1, -1, 1), new(1, 1, -1), new(1, 1, 1),
    ];
}

// layout instances that are not colliders but carry the scripting: exit/pop ranges, event objects, trigger volumes, npc/treasure placements
public sealed class ZoneMarker
{
    public int Index;
    public int Type;
    public string Name = "";
    public ulong LayoutObjectId;
    public int LayerIndex;
    public int NodeIndex = -1;
    public uint InstanceKey;
    public Vector3 Position;
    public uint BaseId;           // EventObject: EObj row id (the OID of the EventObj actor in game / in replays); PopRange: pop type; npc/treasure: base row
    public uint BoundInstanceId;  // EventObject: the layout instance it controls
    public uint LinkedInstanceId;
    public bool ActiveByDefault = true;
    public LgbInstance Source = null!;  // typed payload (Pop, Exit, Map, Trigger, Npc, Treasure)
    public Vector3[]? Corners;          // trigger volumes: world corners of the unit box
    public Bounds3 WorldBounds;

    public bool IsTrigger => Type is (int)LgbInstanceType.ExitRange or (int)LgbInstanceType.MapRange or (int)LgbInstanceType.EventRange or (int)LgbInstanceType.DoorRange;

    public string TypeName => Type switch
    {
        (int)LgbInstanceType.ExitRange => "exit range",
        (int)LgbInstanceType.PopRange => "pop range",
        (int)LgbInstanceType.EventObject => "event object",
        (int)LgbInstanceType.MapRange => "map range",
        (int)LgbInstanceType.EventRange => "event range",
        (int)LgbInstanceType.DoorRange => "door range",
        (int)LgbInstanceType.Treasure => "treasure",
        (int)LgbInstanceType.EventNpc => "event npc",
        (int)LgbInstanceType.BattleNpc => "battle npc",
        _ => $"type {Type}",
    };
}

public sealed class ZoneAnalyticInstance
{
    public int Index;
    public string Name = "";
    public ulong LayoutObjectId;
    public int LayerIndex;
    public int NodeIndex = -1;
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
    private WorldTriangle[] _tris = [];
    private int _count;

    public int Count => _count;
    public ReadOnlySpan<WorldTriangle> Span => _tris.AsSpan(0, _count);
    public ref readonly WorldTriangle this[int i] => ref _tris[i];

    // room for total triangles in all, so a load with a known count appends without regrowing
    public void Reserve(int total)
    {
        if (total > _tris.Length)
        {
            Array.Resize(ref _tris, total);
        }
    }

    // transforms the instance's mesh into world space and appends it; sets TriStart/TriCount/WorldBounds on the instance
    public int Append(ZoneMeshInstance inst)
    {
        var mesh = inst.Mesh;
        var n = mesh.TriangleCount;
        if (_count + n > _tris.Length)
        {
            Array.Resize(ref _tris, Math.Max(Math.Max(_tris.Length * 2, 1 << 16), _count + n));
        }
        var start = _count;
        var bounds = Bounds3.Empty;
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
            var tri = new WorldTriangle(a, b, c, material, (material & ~objMask) | objValue, normalY, inst.Index);
            _tris[_count++] = tri;
            bounds = Bounds3.Union(bounds, tri.Bounds);
        }
        inst.TriStart = start;
        inst.TriCount = n;
        inst.WorldBounds = n > 0 ? bounds : Bounds3.Transform(mesh.LocalBounds, world);
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
    public readonly List<ZoneNode> Nodes = [];
    public readonly Dictionary<uint, int> TopLevelNodeByKey = []; // instance keys of the lgb files are unique per territory; bound/linked/dest ids refer to them
    public readonly Dictionary<ulong, int> NodeByPathId = [];
    public readonly List<ZoneMeshInstance> Meshes = [];
    public readonly List<ZoneBoxInstance> Boxes = [];
    public readonly List<ZoneAnalyticInstance> Analytics = [];
    public readonly List<ZoneMarker> Markers = [];
    public PcbList? TerrainList;
    public readonly Dictionary<uint, int> TerrainTileMeshIndex = [];
    public readonly Dictionary<string, PcbMesh?> PcbCache = []; // null: the file is missing or malformed (reported once)
    public readonly ZoneTriangleStore Triangles = new();
    public readonly ZoneLoadReport Report = new();
    public int TerrainLayerIndex = -1;
    public Bounds3 Bounds;
    private ZoneActivity _activity = new();
    public int ActivityVersion; // bumped on every Activity publish, so a reader can tell a resolve happened

    // per-instance activity under the current scene state (ZoneSceneResolver.Resolve); empty until resolved. Published as one reference after the
    // arrays are complete: a reader takes the reference once and sees either the previous or the new consistent set
    public ZoneActivity Activity
    {
        get => _activity;
        set
        {
            _activity = value;
            ++ActivityVersion;
        }
    }

    public static ulong MakeLayoutObjectId(int type, ushort layerKey, uint instanceKey) => ((ulong)instanceKey << 32) | ((ulong)layerKey << 16) | ((ulong)(byte)type << 8);

    // collision present under the current scene state; meshes appended after the last resolve (terrain tiles) fall back to their layer flag
    public bool IsMeshEnabled(int meshIndex)
    {
        var a = _activity;
        return meshIndex < a.MeshActive.Length ? a.MeshActive[meshIndex] : Layers[Meshes[meshIndex].LayerIndex].Enabled;
    }

    public bool IsBoxEnabled(int boxIndex)
    {
        var a = _activity;
        return boxIndex < a.BoxActive.Length ? a.BoxActive[boxIndex] : Layers[Boxes[boxIndex].LayerIndex].Enabled;
    }

    public bool IsLayerEnabled(int layerIndex) => Layers[layerIndex].Enabled;

    // the instance exists in the scene (layer on, not overridden off) even if an event object currently has its collision removed
    public bool IsNodePlaced(int nodeIndex)
    {
        if (nodeIndex < 0)
        {
            return true;
        }
        var a = _activity;
        return nodeIndex < a.NodePlaced.Length ? a.NodePlaced[nodeIndex] : Layers[Nodes[nodeIndex].LayerIndex].Enabled;
    }

    public bool IsNodeActive(int nodeIndex)
    {
        if (nodeIndex < 0)
        {
            return true;
        }
        var a = _activity;
        return nodeIndex < a.NodeActive.Length ? a.NodeActive[nodeIndex] : Layers[Nodes[nodeIndex].LayerIndex].Enabled;
    }

    public bool IsBoxPlaced(int boxIndex) => Boxes[boxIndex].NodeIndex >= 0 ? IsNodePlaced(Boxes[boxIndex].NodeIndex) : Layers[Boxes[boxIndex].LayerIndex].Enabled;
    public bool IsMeshPlaced(int meshIndex) => Meshes[meshIndex].NodeIndex >= 0 ? IsNodePlaced(Meshes[meshIndex].NodeIndex) : Layers[Meshes[meshIndex].LayerIndex].Enabled;

    public int FindTopLevelNode(uint instanceKey) => instanceKey != 0 && TopLevelNodeByKey.TryGetValue(instanceKey, out var n) ? n : -1;
    public int FindNode(ulong pathId) => NodeByPathId.TryGetValue(pathId, out var n) ? n : -1;

    // "<source file>/<layer>/<key hex>" per node along the ancestor chain, joined by '>'
    public string NodePath(int nodeIndex)
    {
        var node = Nodes[nodeIndex];
        var layer = Layers[node.LayerIndex];
        var own = $"{layer.SourceFile}/{layer.Name}/{node.InstanceKey:X}";
        return node.Parent >= 0 ? $"{NodePath(node.Parent)}>{own}" : own;
    }

    // colliders of the node itself and everything instantiated under it (shared-group children)
    public void CollectSubtreeColliders(int node, List<int> meshes, List<int> boxes)
    {
        if (node < 0 || node >= Nodes.Count)
        {
            return;
        }
        var end = Nodes[node].SubtreeEnd;
        for (var i = node; i < end; ++i)
        {
            var n = Nodes[i];
            if (n.MeshIndex >= 0)
            {
                meshes.Add(n.MeshIndex);
            }
            if (n.BoxIndex >= 0)
            {
                boxes.Add(n.BoxIndex);
            }
        }
    }

    public void RecomputeBounds()
    {
        var b = Bounds3.Empty;
        var n = Meshes.Count;
        for (var i = 0; i < n; ++i)
        {
            b = Bounds3.Union(b, Meshes[i].WorldBounds);
        }
        Bounds = n > 0 ? b : default;
    }
}
