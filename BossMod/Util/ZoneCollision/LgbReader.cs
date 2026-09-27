namespace BossMod;

// byte-level reader for level layout files (.lgb) and shared-group files (.sgb), using the client's file layouts
// (Lumina's parser is stale past offset 0x44 of the bg-part / collision-box payloads and drops the high material bits)
public enum LgbInstanceType : int
{
    BgPart = 1,
    SharedGroup = 6,
    EventNpc = 8,
    BattleNpc = 9,
    Treasure = 16,
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
public enum LgbPopType : int { None = 0, Pc = 1, Npc = 2, Content = 3 }
public enum LgbDoorState : int { None = 0, Auto = 1, Open = 2, Closed = 3 }
public enum LgbTriggerShape : int { None = 0, Box = 1, Sphere = 2, Cylinder = 3, Board = 4, Mesh = 5, BoardBothSides = 6 }

public readonly record struct LgbAnalytic(ulong MatMask, ulong MatValue, LgbAnalyticKind Kind, Vector3 Translation, Vector3 RotationEuler, Vector3 Scale, Bounds3 Bounds);
// per-type payloads that the scene/scripting layer needs; every instance keeps the flat fields below for the collision path
public readonly record struct LgbTriggerBox(LgbTriggerShape Shape, short Priority, bool Enabled);
public readonly record struct LgbExitRange(int ExitType, ushort ZoneId, ushort TerritoryType, int Index, uint DestInstanceId, uint ReturnInstanceId, float PlayerRunningDirection);
public readonly record struct LgbMapRange(uint Map, uint PlaceNameBlock, uint PlaceNameSpot, uint Weather, uint Bgm, bool MapEnabled, bool PlaceNameEnabled);
public readonly record struct LgbPopRange(LgbPopType PopType, float InnerRadiusRatio, byte Index, Vector3[] RelativePositions);
public readonly record struct LgbSharedGroup(LgbDoorState InitialDoorState, int InitialRotationState, bool RandomTimelineAutoPlay, bool RandomTimelineLoop, bool IsCollisionControllableWithoutEObj, uint BoundClientPathInstanceId, bool NotCreateNavimeshDoor, int InitialTransformState, int InitialColorState);
public readonly record struct LgbNpc(uint BaseId, uint PopWeather, byte PopTimeStart, byte PopTimeEnd, uint MoveAi, byte WanderingRange, byte Route, ushort EventGroup, uint Behavior);
public readonly record struct LgbTreasure(uint BaseId, bool NonpopInitZone);
public readonly record struct LgbObSetRef(int AssetType, uint InstanceKey, bool Enable, bool EmissiveEnable);

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
    public uint BaseId;           // EventObject: EObj sheet row (= the actor OID in game); PopRange: pop type; npc/treasure: base row
    public uint BoundInstanceId;  // EventObject: layout instance bound to this object
    public uint LinkedInstanceId;
    // typed payloads (null when the type has none or the read failed)
    public LgbTriggerBox? Trigger;
    public LgbExitRange? Exit;
    public LgbMapRange? Map;
    public LgbPopRange? Pop;
    public LgbSharedGroup? Group;
    public LgbNpc? Npc;
    public LgbTreasure? Treasure;
}

public sealed class LgbLayer
{
    public uint LayerId;          // full 32-bit id (shared-group layers use the high word); Key is the low 16 bits
    public ushort Key;
    public string Name = "";
    public ushort FestivalId, FestivalPhase;
    public bool ToolModeVisible = true, IsTemporary, IsHousing;
    public ushort VersionMask;
    public int LayerSetReferencedType;
    public uint[] LayerSetIds = [];
    public LgbObSetRef[] ObSetEnable = [];
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
        var layer = new LgbLayer { FileOffset = l, LayerId = ZoneBinary.U32(data, l), Key = ZoneBinary.U16(data, l) };
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
        try
        {
            ParseLayerExtras(data, l, layer);
        }
        catch (ZoneFormatException ex)
        {
            doc.Warnings.Add($"layer '{layer.Name}' at 0x{l:X}: header extras skipped ({ex.Message})");
        }
        var table = l + offsetInstances;
        for (var i = 0; i < numInstances; ++i)
        {
            var inst = table + ZoneBinary.I32(data, table + 4 * i);
            try
            {
                layer.Instances.Add(ParseInstance(data, inst, doc));
            }
            catch (ZoneFormatException ex)
            {
                doc.Warnings.Add($"layer '{layer.Name}' instance {i} at 0x{inst:X}: {ex.Message}");
            }
        }
        return layer;
    }

    // LayerHeader: flags @0x10, LayerSetReferencedList offset @0x14 (list {type, offset rel. to list, count}), festival @0x18, IsTemporary @0x1c,
    // IsHousing @0x1d, VersionMask @0x1e, OBSetReferencedList @0x24/0x28, OBSetEnableReferencedList @0x2c/0x30 (12-byte entries)
    private static void ParseLayerExtras(ReadOnlySpan<byte> data, int l, LgbLayer layer)
    {
        layer.ToolModeVisible = ZoneBinary.U8(data, l + 0x10) != 0;
        layer.IsTemporary = ZoneBinary.U8(data, l + 0x1c) != 0;
        layer.IsHousing = ZoneBinary.U8(data, l + 0x1d) != 0;
        layer.VersionMask = ZoneBinary.U16(data, l + 0x1e);
        var setList = ZoneBinary.I32(data, l + 0x14);
        if (setList > 0)
        {
            var s = l + setList;
            layer.LayerSetReferencedType = ZoneBinary.I32(data, s);
            var off = ZoneBinary.I32(data, s + 4);
            var count = ZoneBinary.I32(data, s + 8);
            if (count > 0 && count <= 256)
            {
                layer.LayerSetIds = new uint[count];
                for (var i = 0; i < count; ++i)
                {
                    layer.LayerSetIds[i] = ZoneBinary.U32(data, s + off + 4 * i);
                }
            }
        }
        var obOff = ZoneBinary.I32(data, l + 0x2c);
        var obCount = ZoneBinary.I32(data, l + 0x30);
        if (obOff > 0 && obCount > 0 && obCount <= 4096)
        {
            layer.ObSetEnable = new LgbObSetRef[obCount];
            for (var i = 0; i < obCount; ++i)
            {
                var e = l + obOff + 12 * i;
                layer.ObSetEnable[i] = new(ZoneBinary.I32(data, e), ZoneBinary.U32(data, e + 4), ZoneBinary.U8(data, e + 8) != 0, ZoneBinary.U8(data, e + 9) != 0);
            }
        }
    }

    private static LgbInstance ParseInstance(ReadOnlySpan<byte> data, int i, LgbDocument doc)
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
                    inst.ColliderKind = (LgbColliderKind)ZoneBinary.U8(data, i + 0x30);
                    inst.ActiveByDefault = ZoneBinary.U8(data, i + 0x36) != 0;
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
                inst.ColliderKind = (LgbColliderKind)ZoneBinary.U8(data, i + 0x30);
                inst.ActiveByDefault = ZoneBinary.U8(data, i + 0x36) != 0;
                break;
            case LgbInstanceType.EventObject:
                // GameInstanceObject { BaseId @0x30 } (the EObj sheet row = the actor's OID in game), then BoundInstanceID @0x34, LinkedInstanceID @0x38
                inst.BaseId = ZoneBinary.U32(data, i + 0x30);
                inst.BoundInstanceId = ZoneBinary.U32(data, i + 0x34);
                inst.LinkedInstanceId = ZoneBinary.U32(data, i + 0x38);
                break;
            case LgbInstanceType.PopRange:
            case LgbInstanceType.EventNpc:
            case LgbInstanceType.BattleNpc:
            case LgbInstanceType.Treasure:
                inst.BaseId = ZoneBinary.U32(data, i + 0x30); // PopType / ENpcBase / BNpcBase / treasure base
                break;
        }
        try
        {
            ParsePayload(data, i, inst);
        }
        catch (ZoneFormatException ex)
        {
            doc.Warnings.Add($"instance '{inst.Name}' type {inst.Type} at 0x{i:X}: payload skipped ({ex.Message})");
        }
        return inst;
    }

    // typed payloads, offsets from the instance start (the common header is 0x30 bytes); the harness `zone --oracle` pins them against Lumina
    private static void ParsePayload(ReadOnlySpan<byte> data, int i, LgbInstance inst)
    {
        switch ((LgbInstanceType)inst.Type)
        {
            case LgbInstanceType.ExitRange:
                inst.Trigger = ReadTrigger(data, i);
                inst.Exit = new(ZoneBinary.I32(data, i + 0x3c), ZoneBinary.U16(data, i + 0x40), ZoneBinary.U16(data, i + 0x42), ZoneBinary.I32(data, i + 0x44), ZoneBinary.U32(data, i + 0x48), ZoneBinary.U32(data, i + 0x4c), ZoneBinary.F32(data, i + 0x50));
                break;
            case LgbInstanceType.MapRange:
                inst.Trigger = ReadTrigger(data, i);
                inst.Map = new(ZoneBinary.U32(data, i + 0x3c), ZoneBinary.U32(data, i + 0x40), ZoneBinary.U32(data, i + 0x44), ZoneBinary.U32(data, i + 0x48), ZoneBinary.U32(data, i + 0x4c), ZoneBinary.U8(data, i + 0x5d) != 0, ZoneBinary.U8(data, i + 0x5e) != 0);
                break;
            case LgbInstanceType.EventRange:
            case LgbInstanceType.DoorRange:
                inst.Trigger = ReadTrigger(data, i);
                break;
            case LgbInstanceType.PopRange:
                {
                    var listOff = ZoneBinary.I32(data, i + 0x34);
                    var count = ZoneBinary.I32(data, i + 0x38);
                    var rel = Array.Empty<Vector3>();
                    if (listOff > 0 && count > 0 && count <= 64)
                    {
                        rel = new Vector3[count];
                        for (var k = 0; k < count; ++k)
                        {
                            rel[k] = ZoneBinary.Vec3(data, i + 0x30 + listOff + 12 * k);
                        }
                    }
                    inst.Pop = new((LgbPopType)ZoneBinary.I32(data, i + 0x30), ZoneBinary.F32(data, i + 0x3c), ZoneBinary.U8(data, i + 0x40), rel);
                }
                break;
            case LgbInstanceType.SharedGroup:
                inst.Group = new((LgbDoorState)ZoneBinary.I32(data, i + 0x34), ZoneBinary.I32(data, i + 0x40), ZoneBinary.U8(data, i + 0x44) != 0, ZoneBinary.U8(data, i + 0x45) != 0, ZoneBinary.U8(data, i + 0x46) != 0, ZoneBinary.U32(data, i + 0x48), ZoneBinary.U8(data, i + 0x50) != 0, ZoneBinary.I32(data, i + 0x54), ZoneBinary.I32(data, i + 0x58));
                break;
            case LgbInstanceType.EventNpc:
            case LgbInstanceType.BattleNpc:
                inst.Npc = new(ZoneBinary.U32(data, i + 0x30), ZoneBinary.U32(data, i + 0x34), ZoneBinary.U8(data, i + 0x38), ZoneBinary.U8(data, i + 0x39), ZoneBinary.U32(data, i + 0x3c), ZoneBinary.U8(data, i + 0x40), ZoneBinary.U8(data, i + 0x41), ZoneBinary.U16(data, i + 0x42), ZoneBinary.U32(data, i + 0x4c));
                break;
            case LgbInstanceType.Treasure:
                inst.Treasure = new(ZoneBinary.U32(data, i + 0x30), ZoneBinary.U8(data, i + 0x34) != 0);
                break;
        }
    }

    // TriggerBoxInstanceObject { shape i32 @0x30, priority i16 @0x34, enabled u8 @0x36 }
    private static LgbTriggerBox ReadTrigger(ReadOnlySpan<byte> data, int i) => new((LgbTriggerShape)ZoneBinary.I32(data, i + 0x30), (short)ZoneBinary.U16(data, i + 0x34), ZoneBinary.U8(data, i + 0x36) != 0);
}
