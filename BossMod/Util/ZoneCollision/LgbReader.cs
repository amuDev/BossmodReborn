namespace BossMod;

// byte-level reader for level layout files (.lgb) and shared-group files (.sgb), using the client's file layouts
// (Lumina's parser is stale past offset 0x44 of the bg-part / collision-box payloads and drops the high material bits)
public enum LgbInstanceType : int
{
    BgPart = 1,
    SharedGroup = 6,
    PopRange = 40,
    ExitRange = 41,
    MapRange = 43,
    EventObject = 45,
    EventRange = 49,
    CollisionBox = 57,
    DoorRange = 58,
}

public enum LgbBgCollisionType : int { None = 0, Mesh = 1, Analytic = 2 }
public enum LgbColliderKind : byte { None = 0, Box = 1, Sphere = 2, Cylinder = 3, Plane = 4, Mesh = 5, PlaneTwoSided = 6 }
public enum LgbAnalyticKind : byte { None = 0, Box = 1, Sphere = 2, Cylinder = 3, Plane = 4 }

public readonly record struct LgbAnalytic(ulong MatMask, ulong MatValue, LgbAnalyticKind Kind, Vector3 Translation, Vector3 RotationEuler, Vector3 Scale, Bounds3 Bounds);

public sealed class LgbInstance
{
    public int Type;
    public uint Key;
    public string Name = "";
    public int FileOffset;
    public Vector3 Translation, RotationEuler, Scale;
    // bg part
    public string? ModelPath, CollisionPath;
    public LgbBgCollisionType BgCollision;
    public ulong BgMatMask, BgMatValue;
    public LgbAnalytic? Analytic;
    // collision box
    public LgbColliderKind ColliderKind;
    public bool ActiveByDefault = true;
    public ulong BoxMatMask, BoxMatValue;
    public string? BoxMeshPath;
    // shared group
    public string? SharedGroupPath;
}

public sealed class LgbLayer
{
    public ushort Key;
    public string Name = "";
    public ushort FestivalId, FestivalPhase;
    public int FileOffset;
    public readonly List<LgbInstance> Instances = [];
}

public sealed class LgbLayerGroup
{
    public uint Id;
    public string Name = "";
    public int FileOffset;
    public readonly List<LgbLayer> Layers = [];
}

public sealed class LgbDocument
{
    public string Path = "";
    public string Magic = "";
    public readonly List<LgbLayerGroup> Groups = [];
    public readonly List<string> Warnings = [];
}

public static class LgbReader
{
    private const int FileHeaderSize = 0xc;
    private const int SectionHeaderSize = 0x8;
    private const int LayerGroupHeaderSize = 0x10;
    private const int InstanceHeaderSize = 0x30;

    public static LgbDocument Parse(string path, ReadOnlySpan<byte> data)
    {
        var doc = new LgbDocument { Path = path };
        if (data.Length < FileHeaderSize)
        {
            throw new ZoneFormatException($"{path}: file too small ({data.Length} bytes)");
        }
        doc.Magic = ZoneBinary.Magic(data, 0);
        if (doc.Magic is not ("LGB1" or "SGB1"))
        {
            throw new ZoneFormatException($"{path}: unexpected magic '{doc.Magic}'");
        }
        var totalSize = ZoneBinary.I32(data, 4);
        var numSections = ZoneBinary.I32(data, 8);
        if (totalSize > data.Length)
        {
            doc.Warnings.Add($"header size {totalSize} exceeds file size {data.Length}");
        }
        var sec = FileHeaderSize;
        for (var s = 0; s < numSections && sec + SectionHeaderSize <= data.Length; ++s)
        {
            var magic = ZoneBinary.Magic(data, sec);
            var size = ZoneBinary.I32(data, sec + 4);
            var body = sec + SectionHeaderSize;
            switch (magic)
            {
                case "LGP1":
                    doc.Groups.Add(ParseLayerGroup(data, body, doc));
                    break;
                case "SCN1":
                    ParseScene(data, body, doc);
                    break;
                default:
                    doc.Warnings.Add($"section '{magic}' at 0x{sec:X} skipped");
                    break;
            }
            if (size <= 0)
            {
                break;
            }
            sec = body + size;
        }
        return doc;
    }

    // scene section of a shared group: header {i32 offsetLayerGroup (rel. to body), i32 numLayerGroups, ...}; the layer group header (LGP1 layout)
    // sits directly at body + offset (verified: id/name/layers match Lumina's SgbFile); every shipped sgb has exactly one group
    private static void ParseScene(ReadOnlySpan<byte> data, int body, LgbDocument doc)
    {
        var offsetGroups = ZoneBinary.I32(data, body);
        var numGroups = ZoneBinary.I32(data, body + 4);
        if (numGroups <= 0 || numGroups > 4096)
        {
            doc.Warnings.Add($"scene: implausible layer group count {numGroups}");
            return;
        }
        doc.Groups.Add(ParseLayerGroup(data, body + offsetGroups, doc));
        if (numGroups > 1)
        {
            doc.Warnings.Add($"scene declares {numGroups} layer groups, only the first is read");
        }
    }

    private static LgbLayerGroup ParseLayerGroup(ReadOnlySpan<byte> data, int g, LgbDocument doc)
    {
        var group = new LgbLayerGroup { FileOffset = g, Id = ZoneBinary.U32(data, g) };
        var offsetName = ZoneBinary.I32(data, g + 4);
        var offsetLayers = ZoneBinary.I32(data, g + 8);
        var numLayers = ZoneBinary.I32(data, g + 0xc);
        group.Name = ZoneBinary.CStr(data, g + offsetName);
        if (numLayers < 0 || numLayers > 65536)
        {
            throw new ZoneFormatException($"layer group at 0x{g:X}: implausible layer count {numLayers}");
        }
        var table = g + offsetLayers;
        for (var i = 0; i < numLayers; ++i)
        {
            var l = table + ZoneBinary.I32(data, table + 4 * i);
            group.Layers.Add(ParseLayer(data, l, doc));
        }
        return group;
    }

    private static LgbLayer ParseLayer(ReadOnlySpan<byte> data, int l, LgbDocument doc)
    {
        var layer = new LgbLayer { FileOffset = l, Key = ZoneBinary.U16(data, l) };
        var offsetName = ZoneBinary.I32(data, l + 4);
        var offsetInstances = ZoneBinary.I32(data, l + 8);
        var numInstances = ZoneBinary.I32(data, l + 0xc);
        layer.Name = ZoneBinary.CStr(data, l + offsetName);
        layer.FestivalId = ZoneBinary.U16(data, l + 0x18);
        layer.FestivalPhase = ZoneBinary.U16(data, l + 0x1a);
        if (numInstances < 0 || numInstances > 1 << 20)
        {
            throw new ZoneFormatException($"layer '{layer.Name}' at 0x{l:X}: implausible instance count {numInstances}");
        }
        var table = l + offsetInstances;
        for (var i = 0; i < numInstances; ++i)
        {
            var inst = table + ZoneBinary.I32(data, table + 4 * i);
            try
            {
                layer.Instances.Add(ParseInstance(data, inst));
            }
            catch (ZoneFormatException ex)
            {
                doc.Warnings.Add($"layer '{layer.Name}' instance {i} at 0x{inst:X}: {ex.Message}");
            }
        }
        return layer;
    }

    private static LgbInstance ParseInstance(ReadOnlySpan<byte> data, int i)
    {
        var inst = new LgbInstance
        {
            FileOffset = i,
            Type = ZoneBinary.I32(data, i),
            Key = ZoneBinary.U32(data, i + 4),
            Name = ZoneBinary.CStr(data, i + ZoneBinary.I32(data, i + 8)),
            Translation = ZoneBinary.Vec3(data, i + 0xc),
            RotationEuler = ZoneBinary.Vec3(data, i + 0x18),
            Scale = ZoneBinary.Vec3(data, i + 0x24),
        };
        switch ((LgbInstanceType)inst.Type)
        {
            case LgbInstanceType.BgPart:
                {
                    var mdl = ZoneBinary.I32(data, i + 0x30);
                    var pcb = ZoneBinary.I32(data, i + 0x34);
                    inst.ModelPath = mdl != 0 ? ZoneBinary.CStr(data, i + mdl) : "";
                    inst.CollisionPath = pcb != 0 ? ZoneBinary.CStr(data, i + pcb) : "";
                    inst.BgCollision = (LgbBgCollisionType)ZoneBinary.I32(data, i + 0x38);
                    inst.BgMatMask = ZoneBinary.U32(data, i + 0x3c) | ((ulong)ZoneBinary.U32(data, i + 0x44) << 32);
                    inst.BgMatValue = ZoneBinary.U32(data, i + 0x40) | ((ulong)ZoneBinary.U32(data, i + 0x48) << 32);
                    var analytic = ZoneBinary.I32(data, i + 0x4c);
                    if (analytic != 0)
                    {
                        var a = i + analytic;
                        inst.Analytic = new(ZoneBinary.U32(data, a), ZoneBinary.U32(data, a + 4), (LgbAnalyticKind)ZoneBinary.I32(data, a + 0x10),
                            ZoneBinary.Vec3(data, a + 0x14), ZoneBinary.Vec3(data, a + 0x20), ZoneBinary.Vec3(data, a + 0x2c),
                            new(ZoneBinary.Vec3(data, a + 0x38), ZoneBinary.Vec3(data, a + 0x44)));
                    }
                }
                break;
            case LgbInstanceType.CollisionBox:
                {
                    inst.ColliderKind = (LgbColliderKind)data[i + 0x30];
                    inst.ActiveByDefault = data[i + 0x36] != 0;
                    inst.BoxMatMask = ZoneBinary.U32(data, i + 0x3c) | ((ulong)ZoneBinary.U32(data, i + 0x44) << 32);
                    inst.BoxMatValue = ZoneBinary.U32(data, i + 0x40) | ((ulong)ZoneBinary.U32(data, i + 0x48) << 32);
                    var path = ZoneBinary.I32(data, i + 0x50);
                    inst.BoxMeshPath = inst.ColliderKind == LgbColliderKind.Mesh && path != 0 ? ZoneBinary.CStr(data, i + path) : null;
                }
                break;
            case LgbInstanceType.SharedGroup:
                {
                    var path = ZoneBinary.I32(data, i + 0x30);
                    inst.SharedGroupPath = path != 0 ? ZoneBinary.CStr(data, i + path) : "";
                }
                break;
            case LgbInstanceType.ExitRange:
            case LgbInstanceType.EventRange:
            case LgbInstanceType.MapRange:
            case LgbInstanceType.DoorRange:
                inst.ColliderKind = (LgbColliderKind)data[i + 0x30];
                inst.ActiveByDefault = data[i + 0x36] != 0;
                break;
        }
        return inst;
    }
}
