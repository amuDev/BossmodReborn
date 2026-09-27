namespace BossMod;

// on-disk project of the zone arena editor: one file per territory, several named projects (one per room); shared with the harness so
// `zone --project FILE --scene NAME` resolves the same state the editor shows
public sealed class SavedSeal
{
    public string LayoutId = "";
    public string NodeId = "";
    public bool Use = true;
}

public sealed class SavedMeshMode
{
    public string Path = "";
    public string LayoutId = "";
    public string NodeId = "";
    public string Mode = "Auto";
}

public sealed class SavedTriangleDelta
{
    public string Path = "";
    public string LayoutId = "";
    public string NodeId = "";
    public int TriangleCount;
    public int[] Tris = []; // mesh-local indices
}

public sealed class SavedManualPolygon
{
    public bool Difference = true;
    public float[] XZ = [];
    public float[] Y = [];
}

// a scene: layer / node / event object overrides against the layout defaults, keyed by stable ids (layer StablePath, node PathId hex)
public sealed class SavedScene
{
    public string Name = "default";
    public ZoneSceneSource Source;
    public string Notes = "";
    public string ReplayFile = "";
    public double ReplayTime;
    public List<string> DisabledLayers = [];
    public Dictionary<string, bool> NodeOverrides = [];
    public Dictionary<string, ushort> EObjStates = [];
    public Dictionary<string, ushort> EObjObjectStates = [];

    public static SavedScene From(ZoneCollisionScene scene, ZoneScene s)
    {
        var r = new SavedScene { Name = s.Name, Source = s.Source, Notes = s.Notes, ReplayFile = s.ReplayFile, ReplayTime = s.ReplayTime };
        foreach (var layer in scene.Layers)
        {
            if (s.State.DisabledLayers.Contains(layer.PathId))
            {
                r.DisabledLayers.Add(layer.StablePath);
            }
        }
        Format(s.State.NodeOverrides, r.NodeOverrides);
        Format(s.State.EObjStates, r.EObjStates);
        Format(s.State.EObjObjectStates, r.EObjObjectStates);
        return r;
    }

    private static void Format<T>(Dictionary<ulong, T> src, Dictionary<string, T> dst)
    {
        foreach (var kv in src)
        {
            dst[ZoneSceneTimelineFile.FormatId(kv.Key)] = kv.Value;
        }
    }

    // entries whose node id is not in the layout are skipped; the skipped count
    private static int Resolve<T>(ZoneCollisionScene scene, Dictionary<string, T> src, Dictionary<ulong, T> dst)
    {
        var missing = 0;
        foreach (var kv in src)
        {
            var id = ZoneSceneTimelineFile.ParseId(kv.Key);
            if (scene.NodeByPathId.ContainsKey(id))
            {
                dst[id] = kv.Value;
            }
            else
            {
                ++missing;
            }
        }
        return missing;
    }

    // unknown layer paths / node ids are skipped and counted (a game update or a different sgb set)
    public ZoneScene ToScene(ZoneCollisionScene scene, out int missing)
    {
        missing = 0;
        var r = new ZoneScene { Name = Name, Source = Source, Notes = Notes, ReplayFile = ReplayFile, ReplayTime = ReplayTime };
        foreach (var path in DisabledLayers)
        {
            var found = false;
            foreach (var layer in scene.Layers)
            {
                if (layer.StablePath == path)
                {
                    r.State.DisabledLayers.Add(layer.PathId);
                    found = true;
                }
            }
            if (!found)
            {
                ++missing;
            }
        }
        missing += Resolve(scene, NodeOverrides, r.State.NodeOverrides);
        missing += Resolve(scene, EObjStates, r.State.EObjStates);
        missing += Resolve(scene, EObjObjectStates, r.State.EObjObjectStates);
        return r;
    }
}

public sealed class SavedPairOverride
{
    public string SealNode = "";
    public string PartnerNode = ""; // "" = keep the seal single
    public bool PartnerIsNode;
}

public sealed class SavedEObjRule
{
    public string NodeId = "";
    public bool ObjectStateChannel;
    public ushort State;
    public bool CollisionOn;
}

// an editable mechanic rule: what the code generator turns into a component snippet
public sealed class SavedRule
{
    public ZoneRuleKind Kind;
    public ZoneStateChannel Channel;   // EventState (Actor.EventState, OnActorEventStateChange) | ObjectState (EObjSetState, OnActorEState); EAnim / MapEffect for floor changes
    public string Name = "";
    public bool Enabled = true;
    public string TriggerOid = "";     // "0x1E8FB8" or "" (boss OID for seal rules, key OID for door rules, last enemy for set rules)
    public string TriggerNodeId = "";
    public int TriggerState = -1;
    public string TriggerText = "";
    public string EffectOid = "";
    public string EffectNodeId = "";
    public int EffectState = -1;
    public string EffectText = "";
    public string SetMembers = "";     // "0xAD7 Illuminati Soldier x4, 0xAD8 Eoraptor" for set rules
    public string ArenaPre = "";       // project names in the same file; "(current)" = the live session
    public string ArenaPost = "";
    public string HintText = "";
    public float DelaySeconds;
    public float Confidence = 1f;
    public List<string> Evidence = [];
    public List<ZoneKillGroup> RequiredKills = [];
    public List<ZoneKillGroup> NotRequiredKills = [];

    public static SavedRule From(ZoneMechanicRule r) => new()
    {
        Kind = r.Kind,
        Channel = r.Channel,
        Name = $"{r.Kind} {(r.EffectOid != 0 ? $"0x{r.EffectOid:X}" : "")}".Trim(),
        TriggerOid = r.TriggerOid != 0 ? $"0x{r.TriggerOid:X}" : "",
        TriggerNodeId = r.TriggerNodeId,
        TriggerState = r.TriggerState,
        TriggerText = r.Trigger,
        EffectOid = r.EffectOid != 0 ? $"0x{r.EffectOid:X}" : "",
        EffectNodeId = r.EffectNodeId,
        EffectState = r.EffectState,
        EffectText = r.Effect,
        SetMembers = r.Kind == ZoneRuleKind.SetClearSpawns ? r.Trigger : "",
        ArenaPre = r.ArenaPre,
        ArenaPost = r.ArenaPost,
        DelaySeconds = r.DelaySeconds,
        Confidence = r.Confidence,
        Evidence = [.. r.Evidence],
        RequiredKills = [.. r.RequiredKills],
        NotRequiredKills = [.. r.NotRequiredKills],
    };

    public SavedRule Clone()
    {
        var c = (SavedRule)MemberwiseClone();
        c.Evidence = [.. Evidence];
        c.RequiredKills = [.. RequiredKills];
        c.NotRequiredKills = [.. NotRequiredKills];
        return c;
    }
}

// an enabled result polygon saved with the project so another project's rules can emit it without reloading
public sealed class SavedPolygon
{
    public float[] Outer = []; // x,z pairs
    public List<float[]> Holes = [];
    public float MeanY, MinY, MaxY;
}

// what the author was looking at: canvas position and zoom, overlay toggles; restored with the project
public sealed class SavedView
{
    public float CenterX, CenterZ, Zoom = 4f;
    public Dictionary<string, bool> Toggles = [];
}

public sealed class ZoneArenaProject
{
    public float CenterX, CenterY, CenterZ;
    public bool CentreValid;
    public List<string> FloorMaterials = ["7000/F000"];
    public string SealMaterialValue = "2400", SealMaterialMask = "1FFFFFFFFF";
    public bool SealGeometryFilter = true;
    public string PairSealA = "", PairSealB = "", PairMarker = "";
    public string PairSealANode = "", PairSealBNode = "", PairMarkerNode = "";
    public List<string> FloorBoxes = [];
    public List<string> FloorBoxNodes = [];
    public List<string> IgnoredBoxes = [];
    public List<string> IgnoredBoxNodes = [];
    public List<string> ObstacleBoxMaterials = ["2000/F000"];
    public bool CutBoxes = true;
    public int FloorMatchMode;
    public bool SealRequireExactMask = true, SealIncludeInactive = true;
    public float SealPairMaxDistance = 80f, MaxRadius = 60f, MaxSlopeDeg = 55f, WeldEps = 1e-3f, SealFootprintInflate = 0.05f, ObstacleInflate = 0.05f, ObstacleHeightAbove = 2.5f, ObstacleHeightBelow = 0.5f, ObstacleMinHeight = 0.25f;
    public int Adjacency, SealBlock;
    public bool KeepPolygonContainingCentre = true, CutObstacles = true;
    public float RimExtension = 1f, RimMaxSlopeDeg = 80f;
    public int RimHops = 3;
    public bool ObstacleLocalHeight = true;
    public bool ObstacleUnderFloor = true;
    public bool RimRequireWall = true;
    public float StepHeight = 0.5f;
    public float SeamClose = 0.05f;
    public float WallSnap = 0.5f;
    public float EdgeSnap = 0.02f;
    public float GapBridge = 1f;
    public float GapBridgeRise = 0.7f;
    public bool ExcludeUnwalkableMaterials = true;
    public bool ReliefPromotion = true;
    public float ReliefStep = 0.5f;
    public float SealMaxThickness = 3f, SealMinWidth = 4f, SealMinWidthToHeight = 0.5f, SealBehindDepth = 12f;
    public float BoxFloorTouchEps = 0.15f, BoxFloorTouchHeight = 2.5f, SeedSearchRadius = 2f, SnapEpsXZ = 1e-5f, MinArea = 0.05f;
    public int ObstacleMaxTriangles = 20000;
    public bool WallSnapVertices = true;
    public List<SavedSeal> Seals = [];
    public List<SavedPairOverride> PairOverrides = [];
    public List<SavedMeshMode> Meshes = [];
    public bool AutoMapRan;
    public List<SavedTriangleDelta> Added = [];
    public List<SavedTriangleDelta> Removed = [];
    public float Epsilon = 0.02f, Offset, Closing, Opening;
    public int Decimals = 3;
    public bool TrimCollinear = true;
    public float DropAreaBelow;
    public string ArenaFieldName = "arena";
    public float FlatThreshold = 0.5f;
    public bool EmitProjectionHeightZero;
    public float LayerGap = 1f;
    public float TerrainRadius = 120f;
    public List<bool> ContourEnabled = [];
    public List<SavedManualPolygon> ManualPolygons = [];
    public List<float> DeletedVertices = []; // x,z pairs
    public bool AdjustForHitboxInwards = true;
    public bool AdjustForHitboxOutwards;
    public List<SavedScene> Scenes = [];
    public int ActiveScene;
    public List<SavedEObjRule> EObjRules = [];
    public List<SavedRule> Rules = [];
    public string ReplayTimelinePath = "";
    public List<SavedPolygon> ResultPolygons = [];
    public bool ResultFlat;
    public float ResultMeanY;
    public SavedView? View;
    public string ModuleFilePath = ""; // the .cs the arena / rule snippets are written into
    public DateTime SavedAt;
}

public sealed class ZoneArenaProjectFile
{
    public uint Territory;
    public string Bg = "";
    public string LastProject = ""; // reopened when the territory is loaded again
    public Dictionary<string, ZoneArenaProject> Projects = [];
}
