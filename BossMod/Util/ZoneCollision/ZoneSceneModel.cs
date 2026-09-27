namespace BossMod;

// an EventObject placement: the EObj sheet row (= actor OID), the layout instance it controls and the colliders under it
public sealed class ZoneEventObject
{
    public int Index;
    public int NodeIndex;
    public int MarkerIndex;
    public uint BaseId;
    public uint InstanceKey;
    public ulong PathId;
    public string Name = "";
    public ZoneEObjRow Sheet;
    public int BoundNode = -1;
    public int LinkedNode = -1;
    public int[] ControlledMeshes = [];
    public int[] ControlledBoxes = [];
    public int[] SealBoxes = [];      // controlled boxes that pass the seal material + geometry test
    public bool IsDoor;               // bound shared group is a door (door range inside, initial door state, or a door sgb)
    public bool IsBarrierVfx;         // no bound instance, only a vfx sgb (the visual barrier next to a seal)
    public Vector3 Position;          // the EventObject instance
    public Vector3 ActorPosition;     // where the actor spawns in game: the bound instance when there is one

    public ZoneObjectRole Role;       // static classification from the sheet row / sgb; replays refine it (warps)

    public bool IsSealController => SealBoxes.Length > 0 && !IsDoor;
    public bool ControlsCollision => ControlledMeshes.Length > 0 || ControlledBoxes.Length > 0;
    public string Label => Name.Length > 0 ? $"0x{BaseId:X} {Name}" : $"0x{BaseId:X}";
}

// what an event object is for; the fixed EObj rows are shared by every dungeon (entrance barrier, shortcut, exit gate)
public enum ZoneObjectRole : byte { None, Entrance, Shortcut, Exit, Seal, Door, SpawnMarker, Warp, Vfx, Pickup, Platform }

public enum ZoneFloorPieceKind : byte { Platform, Section }

// EAnim = platform objects (OnActorEAnim), MapEffect = sections only (OnMapEffect)
public enum ZoneFloorFamily : byte { EAnim, MapEffect }

// a part of a boss arena that can be gone during the fight: a platform (an event object binding collision at the piece) or a section
// (a map range nested inside the boss area); pieces are grouped by the boss area they belong to
public sealed class ZoneFloorPiece
{
    public int Index;
    public int Group;
    public ZoneFloorPieceKind Kind;
    public int EObj = -1;           // Platform: the controlling event object
    public int MarkerIndex = -1;    // Section: the map range marker
    public int[] Boxes = [];
    public Bounds3 Bounds;
    public Vector3 Position;
    public ulong PathId;            // node path id of the event object (Platform) or the map range (Section)
    public string Label = "";
}

public sealed class ZoneFloorGroup
{
    public int Index;
    public int Area;                // Areas index of the boss area
    public string Name = "";
    public ZoneFloorFamily Family;
    public readonly List<int> Pieces = [];
}

// a warp -> destination guess or confirmation
public sealed class ZoneFlowLink
{
    public int EObj;             // EventObjects index of the warp / shortcut
    public int Pop = -1;         // PopPoints index of the landing, -1 when only a position is known
    public Vector3 Landing;
    public float Confidence;     // 1 = seen in a replay, < 0.5 = layout-order guess
    public string Source = "";   // "layout order" | replay file
}

public sealed class ZonePopPoint
{
    public int Index;
    public int NodeIndex;
    public int MarkerIndex;
    public ulong PathId;
    public uint InstanceKey;
    public LgbPopType PopType;
    public byte SlotIndex;
    public Vector3 Position;
    public Vector3[] RelativePositions = [];
    public int AreaIndex = -1;
}

public sealed class ZoneExitLink
{
    public int NodeIndex;
    public int MarkerIndex;
    public int DestPopNode = -1;
    public int ReturnPopNode = -1;
    public ushort TerritoryType;
    public ushort ZoneId;
    public int ExitIndex;
    public int ExitType;
    public Vector3 Position;
}

public sealed class ZoneMapArea
{
    public int NodeIndex;
    public int MarkerIndex;
    public uint Map, PlaceNameBlock, PlaceNameSpot;
    public string Name = "";
    public Bounds3 WorldBounds;
}

public sealed class ZoneDoor
{
    public int SgbNode;
    public int DoorRangeNode = -1;
    public int ControllerEObj = -1;
    public int[] Boxes = [];
}

// derived view of the layout's scripting anchors; rebuilt after every load (positions/colliders never change afterwards)
public sealed class ZoneSceneModel
{
    public readonly List<ZoneEventObject> EventObjects = [];
    public readonly List<ZonePopPoint> PopPoints = [];
    public readonly List<ZoneExitLink> Exits = [];
    public readonly List<ZoneMapArea> Areas = [];
    public readonly List<ZoneDoor> Doors = [];
    public readonly List<int> Triggers = [];  // marker indices of trigger volumes (exit/map/event/door ranges)
    public readonly List<ZoneFlowLink> Links = []; // warp destinations (guessed from the layout, replaced by replay evidence)
    public readonly List<ZoneScenarioStep> StaticFlow = []; // the run in layout order, without a replay
    public readonly List<ZoneFloorPiece> FloorPieces = [];  // parts of boss arenas that can be gone mid-fight
    public readonly List<ZoneFloorGroup> FloorGroups = [];  // one per boss area with pieces
    public int[] ControllerOfNode = [];       // node -> EventObjects index or -1
    public readonly Dictionary<uint, int> EventObjectByKey = [];
    public readonly Dictionary<ulong, int> EventObjectByPathId = [];
    public readonly Dictionary<uint, List<int>> EventObjectsByBaseId = [];
    private readonly Dictionary<int, int> _eobjByBox = [];
    private ZoneCollisionScene? _scene;

    public static float DistXZ(in Vector3 a, in Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static ZoneSceneModel Build(ZoneCollisionScene scene, IZoneSheetSource sheets, AutoMapSettings sealSettings)
    {
        var model = new ZoneSceneModel { ControllerOfNode = new int[scene.Nodes.Count], _scene = scene };
        Array.Fill(model.ControllerOfNode, -1);
        List<int> meshes = [];
        List<int> boxes = [];
        Dictionary<int, int> doorRangeBySgb = [];
        var markers = scene.Markers;
        for (var i = 0; i < markers.Count; ++i)
        {
            var m = markers[i];
            switch ((LgbInstanceType)m.Type)
            {
                case LgbInstanceType.MapRange:
                    {
                        var map = m.Source.Map ?? default;
                        var name = sheets.PlaceName(map.PlaceNameSpot);
                        if (name.Length == 0)
                        {
                            name = sheets.PlaceName(map.PlaceNameBlock);
                        }
                        model.Areas.Add(new() { NodeIndex = m.NodeIndex, MarkerIndex = i, Map = map.Map, PlaceNameBlock = map.PlaceNameBlock, PlaceNameSpot = map.PlaceNameSpot, Name = name, WorldBounds = m.WorldBounds });
                        model.Triggers.Add(i);
                    }
                    break;
                case LgbInstanceType.DoorRange:
                    {
                        // door ranges live inside door shared groups: remember the owning sgb node
                        var sgb = OwningSharedGroup(scene, m.NodeIndex);
                        if (sgb >= 0)
                        {
                            doorRangeBySgb.TryAdd(sgb, m.NodeIndex);
                        }
                        model.Triggers.Add(i);
                    }
                    break;
                case LgbInstanceType.EventRange:
                case LgbInstanceType.ExitRange:
                    model.Triggers.Add(i);
                    break;
            }
        }
        for (var i = 0; i < markers.Count; ++i)
        {
            var m = markers[i];
            switch ((LgbInstanceType)m.Type)
            {
                case LgbInstanceType.PopRange:
                    {
                        var pop = m.Source.Pop ?? default;
                        var p = new ZonePopPoint
                        {
                            Index = model.PopPoints.Count,
                            NodeIndex = m.NodeIndex,
                            MarkerIndex = i,
                            PathId = scene.Nodes[m.NodeIndex].PathId,
                            InstanceKey = m.InstanceKey,
                            PopType = pop.PopType,
                            SlotIndex = pop.Index,
                            Position = m.Position,
                            RelativePositions = pop.RelativePositions ?? [],
                        };
                        p.AreaIndex = model.AreaAt(m.Position);
                        model.PopPoints.Add(p);
                    }
                    break;
                case LgbInstanceType.ExitRange:
                    {
                        var e = m.Source.Exit ?? default;
                        model.Exits.Add(new()
                        {
                            NodeIndex = m.NodeIndex,
                            MarkerIndex = i,
                            DestPopNode = scene.FindTopLevelNode(e.DestInstanceId),
                            ReturnPopNode = scene.FindTopLevelNode(e.ReturnInstanceId),
                            TerritoryType = e.TerritoryType,
                            ZoneId = e.ZoneId,
                            ExitIndex = e.Index,
                            ExitType = e.ExitType,
                            Position = m.Position,
                        });
                    }
                    break;
                case LgbInstanceType.EventObject:
                    {
                        var node = scene.Nodes[m.NodeIndex];
                        var eo = new ZoneEventObject
                        {
                            Index = model.EventObjects.Count,
                            NodeIndex = m.NodeIndex,
                            MarkerIndex = i,
                            BaseId = m.BaseId,
                            InstanceKey = m.InstanceKey,
                            PathId = node.PathId,
                            Name = sheets.EObjName(m.BaseId),
                            Sheet = sheets.EObj(m.BaseId) ?? default,
                            BoundNode = scene.FindTopLevelNode(m.BoundInstanceId),
                            LinkedNode = scene.FindTopLevelNode(m.LinkedInstanceId),
                            Position = m.Position,
                            ActorPosition = m.Position,
                        };
                        if (eo.BoundNode >= 0)
                        {
                            var bn = scene.Nodes[eo.BoundNode];
                            eo.ActorPosition = bn.Position;
                            meshes.Clear();
                            boxes.Clear();
                            scene.CollectSubtreeColliders(eo.BoundNode, meshes, boxes);
                            eo.ControlledMeshes = [.. meshes];
                            eo.ControlledBoxes = [.. boxes];
                            List<int> seals = [];
                            foreach (var b in boxes)
                            {
                                if (ArenaAutoMapper.IsSealBox(scene.Boxes[b], sealSettings, out _))
                                {
                                    seals.Add(b);
                                }
                            }
                            eo.SealBoxes = [.. seals];
                            var sgbPath = bn.Source.SharedGroupPath ?? "";
                            eo.IsDoor = doorRangeBySgb.ContainsKey(eo.BoundNode) || bn.Source.Group is { InitialDoorState: LgbDoorState.Open or LgbDoorState.Closed } || sgbPath.Contains("door", StringComparison.OrdinalIgnoreCase) || sgbPath.Contains("_dor", StringComparison.OrdinalIgnoreCase);
                            if (model.ControllerOfNode[eo.BoundNode] < 0)
                            {
                                model.ControllerOfNode[eo.BoundNode] = eo.Index;
                            }
                            foreach (var b in boxes)
                            {
                                model._eobjByBox.TryAdd(b, eo.Index);
                            }
                        }
                        else
                        {
                            // vfx-only objects (seal barriers, tank explosions) come from the for_vfx shared groups; keys and switches are bg sgbs
                            eo.IsBarrierVfx = System.IO.Path.GetFileName(eo.Sheet.SgbPath).StartsWith("sgvf", StringComparison.OrdinalIgnoreCase);
                        }
                        eo.Role = ClassifyRole(scene, eo);
                        model.EventObjects.Add(eo);
                        model.EventObjectByKey.TryAdd(m.InstanceKey, eo.Index);
                        model.EventObjectByPathId.TryAdd(eo.PathId, eo.Index);
                        if (!model.EventObjectsByBaseId.TryGetValue(m.BaseId, out var list))
                        {
                            model.EventObjectsByBaseId[m.BaseId] = list = [];
                        }
                        list.Add(eo.Index);
                    }
                    break;
            }
        }
        foreach (var (sgb, range) in doorRangeBySgb)
        {
            meshes.Clear();
            boxes.Clear();
            scene.CollectSubtreeColliders(sgb, meshes, boxes);
            model.Doors.Add(new() { SgbNode = sgb, DoorRangeNode = range, ControllerEObj = model.ControllerOfNode[sgb], Boxes = [.. boxes] });
        }
        model.BuildFloorPieces(scene);
        model.BuildStaticFlow();
        return model;
    }

    // floor pieces from the layout alone. Boss arenas are the map-0 map ranges. Several of them overlapping or touching (a platform grid, the
    // centre and four platforms of a trial) form one group whose members are its sections; a lone room inside a wider range is not a group.
    // Event objects binding collision (not seals, doors or flow objects) are platforms: clustered by distance into groups of their own (or
    // attached to a section group they stand in); tiny map-0 ranges over a platform are its markers, not rooms. Groups with platforms change
    // through OnActorEAnim on the object, sections through OnMapEffect
    private void BuildFloorPieces(ZoneCollisionScene scene)
    {
        static float Size(in Bounds3 b) => (b.Max.X - b.Min.X) * (b.Max.Z - b.Min.Z);
        static bool Touch(in Bounds3 a, in Bounds3 b) => a.Max.X >= b.Min.X - 1f && a.Min.X <= b.Max.X + 1f && a.Max.Z >= b.Min.Z - 1f && a.Min.Z <= b.Max.Z + 1f && a.Max.Y >= b.Min.Y - 10f && a.Min.Y <= b.Max.Y + 10f;
        // connected components of 0..n-1 under a symmetric relation: a component id per item and the component count
        static (int[] comp, int count) Components(int n, Func<int, int, bool> touches)
        {
            var comp = new int[n];
            Array.Fill(comp, -1);
            var count = 0;
            Stack<int> stack = new();
            for (var i = 0; i < n; ++i)
            {
                if (comp[i] >= 0)
                {
                    continue;
                }
                comp[i] = count;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    for (var j = 0; j < n; ++j)
                    {
                        if (comp[j] < 0 && touches(cur, j))
                        {
                            comp[j] = count;
                            stack.Push(j);
                        }
                    }
                }
                ++count;
            }
            return (comp, count);
        }
        // pieces are between 8 and 80 y across: smaller ranges mark a platform, larger ones are rooms and zones
        var boss = Enumerable.Range(0, Areas.Count).Where(i => Areas[i].Map == 0 && MathF.Max(Areas[i].WorldBounds.Max.X - Areas[i].WorldBounds.Min.X, Areas[i].WorldBounds.Max.Z - Areas[i].WorldBounds.Min.Z) is > 8f and <= 80f).ToList();
        // components of touching boss areas
        var (comp, comps) = Components(boss.Count, (i, j) => Touch(Areas[boss[i]].WorldBounds, Areas[boss[j]].WorldBounds));
        for (var c = 0; c < comps; ++c)
        {
            var members = Enumerable.Range(0, boss.Count).Where(i => comp[i] == c).Select(i => boss[i]).OrderByDescending(a => Size(Areas[a].WorldBounds)).ToList();
            if (members.Count < 3)
            {
                continue; // a room, or a room inside a wider range: nothing that comes and goes
            }
            var union = Areas[members[0]].WorldBounds;
            foreach (var a in members)
            {
                union = Bounds3.Union(union, Areas[a].WorldBounds);
            }
            var name = members.Select(a => Areas[a].Name).FirstOrDefault(n => n.Length > 0) ?? "";
            var g = new ZoneFloorGroup { Index = FloorGroups.Count, Area = members[0], Name = name.Length > 0 ? name : $"arena at ({union.Center.X:f0}, {union.Center.Z:f0})" };
            FloorGroups.Add(g);
            foreach (var a in members)
            {
                var b = Areas[a].WorldBounds;
                var piece = new ZoneFloorPiece { Index = FloorPieces.Count, Group = g.Index, Kind = ZoneFloorPieceKind.Section, MarkerIndex = Areas[a].MarkerIndex, Bounds = b, Position = b.Center, PathId = scene.Nodes[Areas[a].NodeIndex].PathId, Label = $"section {(Areas[a].Name.Length > 0 ? Areas[a].Name : $"range {Areas[a].MarkerIndex}")} {b.Max.X - b.Min.X:f0}x{b.Max.Z - b.Min.Z:f0}" };
                FloorPieces.Add(piece);
                g.Pieces.Add(piece.Index);
            }
        }
        // platforms
        List<ZoneFloorPiece> platforms = [];
        foreach (var eo in EventObjects)
        {
            if (!eo.ControlsCollision || eo.IsSealController || eo.IsDoor || eo.Role is ZoneObjectRole.Entrance or ZoneObjectRole.Exit or ZoneObjectRole.Warp or ZoneObjectRole.Shortcut or ZoneObjectRole.SpawnMarker or ZoneObjectRole.Vfx)
            {
                continue;
            }
            var bounds = new Bounds3(new(float.MaxValue), new(float.MinValue));
            foreach (var b in eo.ControlledBoxes)
            {
                bounds = Bounds3.Union(bounds, scene.Boxes[b].WorldBounds);
            }
            foreach (var m in eo.ControlledMeshes)
            {
                bounds = Bounds3.Union(bounds, scene.Meshes[m].WorldBounds);
            }
            if (bounds.Min.X > bounds.Max.X || MathF.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Z - bounds.Min.Z) > 60f)
            {
                continue; // nothing, or a whole-room collider (a shutter over the room), not a piece
            }
            platforms.Add(new() { Kind = ZoneFloorPieceKind.Platform, EObj = eo.Index, Boxes = eo.ControlledBoxes, Bounds = bounds, Position = eo.ActorPosition, PathId = eo.PathId, Label = $"platform {eo.Label} key 0x{eo.InstanceKey:X}" });
        }
        // clusters of platforms within 30 y of each other
        var (clusterOf, clusters) = Components(platforms.Count, (i, j) => (platforms[i].Position - platforms[j].Position).Length() <= 30f);
        for (var c = 0; c < clusters; ++c)
        {
            var members = Enumerable.Range(0, platforms.Count).Where(i => clusterOf[i] == c).ToList();
            var centre = Vector3.Zero;
            foreach (var i in members)
            {
                centre += platforms[i].Position;
            }
            centre /= members.Count;
            // a section group the cluster touches takes the platforms; else the cluster is a group named after the map area around it
            var clusterBounds = new Bounds3(new(float.MaxValue), new(float.MinValue));
            foreach (var i in members)
            {
                clusterBounds = Bounds3.Union(clusterBounds, platforms[i].Bounds);
            }
            var group = FloorGroups.Find(g => g.Pieces.Exists(p => FloorPieces[p].Kind == ZoneFloorPieceKind.Section && Touch(FloorPieces[p].Bounds, clusterBounds)));
            if (group == null)
            {
                var area = AreaAt(centre);
                var name = area >= 0 && Areas[area].Name.Length > 0 ? Areas[area].Name : $"platforms near ({centre.X:f0}, {centre.Z:f0})";
                group = new ZoneFloorGroup { Index = FloorGroups.Count, Area = area, Name = name };
                FloorGroups.Add(group);
            }
            foreach (var i in members)
            {
                var piece = platforms[i];
                piece.Index = FloorPieces.Count;
                piece.Group = group.Index;
                FloorPieces.Add(piece);
                group.Pieces.Add(piece.Index);
                EventObjects[piece.EObj].Role = ZoneObjectRole.Platform;
            }
            // a section drawn around a platform is its marker, not a piece of its own
            foreach (var pi in group.Pieces.ToList())
            {
                var p = FloorPieces[pi];
                if (p.Kind == ZoneFloorPieceKind.Section && members.Exists(i => p.Bounds.ContainsXZ(platforms[i].Position.X, platforms[i].Position.Z)) && Size(p.Bounds) < 400f)
                {
                    group.Pieces.Remove(pi);
                }
            }
        }
        // a lone collider (a shutter, a bridge, a switch) is not a floor piece and a lone section is a room: a group needs two platforms or sections
        FloorGroups.RemoveAll(g => g.Pieces.Count < 2);
        var keep = FloorGroups.SelectMany(g => g.Pieces).ToHashSet();
        var remap = new int[FloorPieces.Count];
        Array.Fill(remap, -1);
        var kept = new List<ZoneFloorPiece>();
        for (var i = 0; i < FloorPieces.Count; ++i)
        {
            if (keep.Contains(i))
            {
                remap[i] = kept.Count;
                FloorPieces[i].Index = kept.Count;
                kept.Add(FloorPieces[i]);
            }
            else if (FloorPieces[i].EObj >= 0)
            {
                EventObjects[FloorPieces[i].EObj].Role = ZoneObjectRole.None;
            }
        }
        FloorPieces.Clear();
        FloorPieces.AddRange(kept);
        for (var i = 0; i < FloorGroups.Count; ++i)
        {
            var g = FloorGroups[i];
            g.Index = i;
            for (var k = 0; k < g.Pieces.Count; ++k)
            {
                g.Pieces[k] = remap[g.Pieces[k]];
            }
            g.Family = g.Pieces.Exists(p => FloorPieces[p].Kind == ZoneFloorPieceKind.Platform) ? ZoneFloorFamily.EAnim : ZoneFloorFamily.MapEffect;
            foreach (var p in g.Pieces)
            {
                FloorPieces[p].Group = i;
            }
        }
    }

    public int FloorPieceAt(in Vector3 p)
    {
        for (var i = 0; i < FloorPieces.Count; ++i)
        {
            var b = FloorPieces[i].Bounds;
            if (p.X >= b.Min.X - 0.2f && p.X <= b.Max.X + 0.2f && p.Z >= b.Min.Z - 0.2f && p.Z <= b.Max.Z + 0.2f && p.Y >= b.Min.Y - 3f && p.Y <= b.Max.Y + 3f)
            {
                return i;
            }
        }
        return -1;
    }

    // scripted interactables carry an event handler id in EObj.Data (high word 0xF); exit and shortcut are fixed handlers, the rest are
    // numbered in progression order within the dungeon
    public const uint ExitHandler = 0xF0005;
    public const uint ShortcutHandler = 0xF004A;
    public static bool IsScripted(in ZoneEObjRow row) => (row.Data >> 16) == 0xF;

    // the run in layout order: entrance, then the scripted objects by handler id (warps with a guessed landing), the seals they pass, the exit;
    // warp destinations are server-side, so the landing is the next unused player pop in key order, flagged as a guess
    public void BuildStaticFlow()
    {
        var scene = _scene!;
        StaticFlow.Clear();
        Links.RemoveAll(l => l.Confidence < 0.5f);
        var entrance = EventObjects.Find(e => e.Role == ZoneObjectRole.Entrance);
        var start = entrance?.ActorPosition ?? (PopPoints.Count > 0 ? PopPoints[0].Position : Vector3.Zero);
        string AreaName(Vector3 p)
        {
            var a = AreaAt(p);
            return a >= 0 ? Areas[a].Name : "";
        }
        StaticFlow.Add(new() { T = 0, Kind = ZoneStepKind.Start, Area = AreaName(start), Text = entrance != null ? $"entrance {entrance.Label} key 0x{entrance.InstanceKey:X}" : "first pop point", NodeId = entrance != null ? ZoneSceneTimelineFile.FormatId(entrance.PathId) : "", Oid = entrance?.BaseId ?? 0, X = start.X, Z = start.Z });
        var scripted = EventObjects.Where(e => IsScripted(e.Sheet) && e.Role is not (ZoneObjectRole.Exit or ZoneObjectRole.Shortcut)).OrderBy(e => e.Sheet.Data).ToList();
        var usedPops = new HashSet<int>();
        // landing candidates: player pops in the warps' layer, away from the start, in key order; the landings were authored as one batch of
        // consecutive instance keys, so of the key batches (gaps > 0x800) the one whose size matches the warp count is the landing set
        // (verified on Cutter's Cry: six warps, the six 0x40CC7x pops; the interleaved 0x406Exx pops are the boss-room checkpoints)
        var warps = EventObjects.Where(e => e.Role == ZoneObjectRole.Warp).ToList();
        var warpLayers = warps.Select(e => scene.Nodes[e.NodeIndex].LayerIndex).ToHashSet();
        var candidatePops = PopPoints.Where(p => p.PopType == LgbPopType.Pc && (warpLayers.Count == 0 || warpLayers.Contains(scene.Nodes[p.NodeIndex].LayerIndex)) && DistXZ(p.Position, start) > 10f).OrderBy(p => p.InstanceKey).ToList();
        if (warps.Count > 0 && candidatePops.Count > warps.Count)
        {
            List<List<ZonePopPoint>> batches = [];
            foreach (var p in candidatePops)
            {
                if (batches.Count == 0 || p.InstanceKey - batches[^1][^1].InstanceKey > 0x800)
                {
                    batches.Add([]);
                }
                batches[^1].Add(p);
            }
            var best = batches.OrderBy(b => Math.Abs(b.Count - warps.Count)).ThenByDescending(b => b.Count).First();
            if (best.Count >= warps.Count - 1)
            {
                candidatePops = best;
            }
        }
        var sealsLeft = EventObjects.Where(e => e.IsSealController).ToList();
        var order = 1.0;
        foreach (var eo in scripted)
        {
            var pos = eo.ActorPosition;
            // seals within reach of this object come before it in the run
            foreach (var seal in sealsLeft.Where(sl => DistXZ(sl.ActorPosition, pos) <= 45f).ToList())
            {
                StaticFlow.Add(new() { T = order++, Kind = ZoneStepKind.Seal, Area = AreaName(seal.ActorPosition), Text = $"sealed room: {seal.Label} key 0x{seal.InstanceKey:X}", NodeId = ZoneSceneTimelineFile.FormatId(seal.PathId), Oid = seal.BaseId, X = seal.ActorPosition.X, Z = seal.ActorPosition.Z });
                sealsLeft.Remove(seal);
            }
            var id = ZoneSceneTimelineFile.FormatId(eo.PathId);
            switch (eo.Role)
            {
                case ZoneObjectRole.Warp:
                    {
                        var link = Links.Find(l => l.EObj == eo.Index);
                        if (link == null)
                        {
                            var area = AreaAt(pos);
                            var pop = candidatePops.Find(p => !usedPops.Contains(p.Index) && (area < 0 || AreaAt(p.Position) != area));
                            if (pop != null)
                            {
                                usedPops.Add(pop.Index);
                                link = new() { EObj = eo.Index, Pop = pop.Index, Landing = pop.Position, Confidence = 0.3f, Source = "layout order" };
                                Links.Add(link);
                            }
                        }
                        else if (link.Pop >= 0)
                        {
                            usedPops.Add(link.Pop);
                        }
                        StaticFlow.Add(new() { T = order++, Kind = ZoneStepKind.Warp, Area = AreaName(pos), Text = $"warp {eo.Label} key 0x{eo.InstanceKey:X} (handler 0x{eo.Sheet.Data:X}) -> {(link != null ? $"{(link.Pop >= 0 ? $"pop key 0x{PopPoints[link.Pop].InstanceKey:X} '{AreaName(link.Landing)}'" : $"({link.Landing.X:f0}, {link.Landing.Z:f0})")} ({(link.Confidence >= 0.5f ? link.Source : "guess: " + link.Source)})" : "unknown")}", NodeId = id, TargetNodeId = link is { Pop: >= 0 } ? ZoneSceneTimelineFile.FormatId(PopPoints[link.Pop].PathId) : "", Oid = eo.BaseId, X = pos.X, Z = pos.Z });
                    }
                    break;
                case ZoneObjectRole.Door:
                    StaticFlow.Add(new() { T = order++, Kind = ZoneStepKind.Door, Area = AreaName(pos), Text = $"door {eo.Label} key 0x{eo.InstanceKey:X} (handler 0x{eo.Sheet.Data:X})", NodeId = id, Oid = eo.BaseId, X = pos.X, Z = pos.Z });
                    break;
                case ZoneObjectRole.Pickup:
                    StaticFlow.Add(new() { T = order++, Kind = ZoneStepKind.Clear, Area = AreaName(pos), Text = $"pickup {eo.Label} key 0x{eo.InstanceKey:X} (handler 0x{eo.Sheet.Data:X})", NodeId = id, Oid = eo.BaseId, X = pos.X, Z = pos.Z });
                    break;
                default:
                    StaticFlow.Add(new() { T = order++, Kind = ZoneStepKind.Object, Area = AreaName(pos), Text = $"scripted object {eo.Label} key 0x{eo.InstanceKey:X} (handler 0x{eo.Sheet.Data:X})", NodeId = id, Oid = eo.BaseId, X = pos.X, Z = pos.Z });
                    break;
            }
        }
        foreach (var seal in sealsLeft)
        {
            StaticFlow.Add(new() { T = order++, Kind = ZoneStepKind.Seal, Area = AreaName(seal.ActorPosition), Text = $"sealed room: {seal.Label} key 0x{seal.InstanceKey:X}", NodeId = ZoneSceneTimelineFile.FormatId(seal.PathId), Oid = seal.BaseId, X = seal.ActorPosition.X, Z = seal.ActorPosition.Z });
        }
        var exit = EventObjects.Find(e => e.Role == ZoneObjectRole.Exit);
        if (exit != null)
        {
            StaticFlow.Add(new() { T = order, Kind = ZoneStepKind.Exit, Area = AreaName(exit.ActorPosition), Text = $"exit {exit.Label} key 0x{exit.InstanceKey:X}", NodeId = ZoneSceneTimelineFile.FormatId(exit.PathId), Oid = exit.BaseId, X = exit.ActorPosition.X, Z = exit.ActorPosition.Z });
        }
    }

    // replace a guessed link with what a replay showed
    public void ConfirmLink(int eobj, int pop, Vector3 landing, string source)
    {
        Links.RemoveAll(l => l.EObj == eobj);
        Links.Add(new() { EObj = eobj, Pop = pop, Landing = landing, Confidence = 1f, Source = source });
    }

    public const uint EntranceBaseId = 0x1E8536; // barrier at the duty entrance, EventState 7 when the duty commences
    public const uint ShortcutBaseId = 0x1E873C;
    public const uint ExitBaseId = 0x1E850B;     // the exit gate after the last boss

    private static ZoneObjectRole ClassifyRole(ZoneCollisionScene scene, ZoneEventObject eo)
    {
        if (eo.BaseId == EntranceBaseId)
        {
            return ZoneObjectRole.Entrance;
        }
        if (eo.BaseId == ShortcutBaseId)
        {
            return ZoneObjectRole.Shortcut;
        }
        if (eo.BaseId == ExitBaseId || eo.Sheet.Data == ExitHandler || eo.Name.Equals("exit", StringComparison.OrdinalIgnoreCase))
        {
            return ZoneObjectRole.Exit;
        }
        if (eo.Sheet.Data == ShortcutHandler)
        {
            return ZoneObjectRole.Shortcut;
        }
        if (eo.IsDoor)
        {
            return ZoneObjectRole.Door;
        }
        if (eo.IsSealController)
        {
            return ZoneObjectRole.Seal;
        }
        if (IsScripted(eo.Sheet))
        {
            // a scripted interactable: on a vfx-only shared group it is a warp, with its own model (key, lever) it is a pickup / switch
            if (eo.BoundNode >= 0 && !eo.ControlsCollision && (scene.Nodes[eo.BoundNode].Source.SharedGroupPath ?? "").Contains("for_vfx", StringComparison.OrdinalIgnoreCase))
            {
                return ZoneObjectRole.Warp;
            }
            if (eo.BoundNode < 0 && eo.Sheet.SgbPath.Length > 0 && !eo.Sheet.SgbPath.Contains("for_vfx", StringComparison.OrdinalIgnoreCase))
            {
                return ZoneObjectRole.Pickup;
            }
        }
        if (eo.BoundNode >= 0 && (scene.Nodes[eo.BoundNode].Source.SharedGroupPath ?? "").Contains("/btl/", StringComparison.OrdinalIgnoreCase))
        {
            return ZoneObjectRole.SpawnMarker; // battle vfx at the spot a wave comes out of
        }
        if (eo.IsBarrierVfx)
        {
            return ZoneObjectRole.Vfx;
        }
        return ZoneObjectRole.None;
    }

    // the top-level shared-group instance whose sgb contains this node (the node's ancestor at depth 0)
    private static int OwningSharedGroup(ZoneCollisionScene scene, int node)
    {
        while (node >= 0 && scene.Nodes[node].Parent >= 0)
        {
            node = scene.Nodes[node].Parent;
        }
        return node >= 0 && scene.Nodes[node].Type == (int)LgbInstanceType.SharedGroup ? node : -1;
    }

    // node id as the timeline and project files store it (0x + 16 hex digits of the node path id)
    public int FindEventObject(string nodeId)
    {
        if (nodeId.Length == 0)
        {
            return -1;
        }
        try
        {
            return EventObjectByPathId.TryGetValue(ZoneSceneTimelineFile.ParseId(nodeId), out var i) ? i : -1;
        }
        catch (FormatException)
        {
            return -1;
        }
    }

    // actor -> layout: the actor's LayoutID is the EventObject instance key for placed objects; server-spawned objects (LayoutID 0) fall
    // back to the nearest instance with the same EObj row
    public int FindEventObject(uint baseId, uint layoutKey, Vector3 actorPos, float maxDist = 5f)
    {
        if (layoutKey != 0 && EventObjectByKey.TryGetValue(layoutKey, out var byKey) && EventObjects[byKey].BaseId == baseId)
        {
            return byKey;
        }
        if (!EventObjectsByBaseId.TryGetValue(baseId, out var list))
        {
            return -1;
        }
        var best = -1;
        var bestD = maxDist;
        foreach (var i in list)
        {
            // server-spawned objects stand at the bound instance in some zones and at the event object's own placement in others
            var d = MathF.Min((EventObjects[i].ActorPosition - actorPos).Length(), (EventObjects[i].Position - actorPos).Length());
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best >= 0 ? best : list.Count == 1 ? list[0] : -1;
    }

    public int AreaAt(Vector3 p)
    {
        var best = -1;
        var bestArea = float.MaxValue;
        for (var i = 0; i < Areas.Count; ++i)
        {
            var b = Areas[i].WorldBounds;
            if (b.ContainsXZ(p.X, p.Z) && p.Y >= b.Min.Y - 5f && p.Y <= b.Max.Y + 5f)
            {
                var area = (b.Max.X - b.Min.X) * (b.Max.Z - b.Min.Z);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = i;
                }
            }
        }
        return best;
    }

    // the nearest spawn-marker object within maxDist of a point (a wave comes out of it), -1 = none
    public int SpawnMarkerNear(in Vector3 p, float maxDist = 3f)
    {
        var best = -1;
        var bestD = maxDist;
        for (var i = 0; i < EventObjects.Count; ++i)
        {
            var eo = EventObjects[i];
            if (eo.Role != ZoneObjectRole.SpawnMarker)
            {
                continue;
            }
            var d = DistXZ(eo.ActorPosition, p);
            if (d <= bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    public int ControllingEObjOfBox(int box) => _eobjByBox.TryGetValue(box, out var e) ? e : -1;
}
