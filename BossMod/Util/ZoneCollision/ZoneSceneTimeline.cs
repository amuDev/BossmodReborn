using System.Text.Json.Serialization;
using System.Threading;

namespace BossMod;

// what a timeline event records; Tag() is the short text the reporter, the events table and the harness print
public enum ZoneEventKind : byte { Create, EventState, ObjectState, EAnim, Targetable, Death, Destroy, DirectorUpdate, MapEffect, Teleport, Player }

// where a state comes from: Actor.EventState (OnActorEventStateChange), EObjSetState (OnActorEState), an object animation or a map effect
public enum ZoneStateChannel : byte { EventState, ObjectState, EAnim, MapEffect }

public enum ZoneRuleKind : byte { SealOnPull, SealRelease, SetClearSpawns, KeyOpensDoor, WaveSpawn, BossAdds, WarpOpens, WarpTeleport, ShortcutTeleport, InstanceStart, ExitGate, FloorChange }

public enum ZoneStepKind : byte { Start, Wave, Adds, Clear, WarpOpen, Warp, Shortcut, Seal, Boss, Door, Exit, Floor, Object }

// OnLoad = placed when the room streamed in, Wave = spawned with players already in the room (scripted), BossAdds = inside a boss fight
public enum ZoneEnemyClass : byte { OnLoad, Wave, BossAdds, Boss }

public static class ZoneTimelineTags
{
    public static string Tag(this ZoneEventKind kind) => kind switch
    {
        ZoneEventKind.Create => "CREATE",
        ZoneEventKind.EventState => "EVTS",
        ZoneEventKind.ObjectState => "ESTA",
        ZoneEventKind.EAnim => "EANM",
        ZoneEventKind.Targetable => "TGT",
        ZoneEventKind.Death => "DEATH",
        ZoneEventKind.Destroy => "DESTROY",
        ZoneEventKind.DirectorUpdate => "DIRU",
        ZoneEventKind.MapEffect => "MAPFX",
        ZoneEventKind.Teleport => "TELEPORT",
        ZoneEventKind.Player => "PLAYER",
        _ => kind.ToString(),
    };

    public static string Tag(this ZoneStateChannel channel) => channel switch
    {
        ZoneStateChannel.EventState => "EVTS",
        ZoneStateChannel.ObjectState => "ESTA",
        ZoneStateChannel.EAnim => "EANM",
        ZoneStateChannel.MapEffect => "MAPFX",
        _ => channel.ToString(),
    };
}

// one replay event that matters for scene state: object creation, EventState (EVTS), EObjSetState (ESTA), animation, targetable/death,
// director updates, map effects and player teleports; EventObj rows are joined to the layout (NodeId = the EventObject's PathId)
public sealed class ZoneTimelineEvent
{
    public double T;               // seconds from the first replay op
    public ZoneEventKind Kind;
    public ActorType Type;         // Create: what was created
    public uint Oid;
    public ulong Instance;
    public uint LayoutId;
    public ushort State;
    public float X, Y, Z;
    public uint NameId;
    public string Name = "";
    public string NodeId = "";     // "0x{PathId:X16}" of the matched EventObject node, "" when unmatched
    public string NodePath = "";
    public int Eobj = -1;          // ZoneSceneModel.EventObjects index at build time (not stable across loads; NodeId is)
    public int[] Boxes = [];
    public int[] Seals = [];
    public uint Dir, Upd, P1, P2, P3, P4;
    public uint MaxHp;             // CREATE/TGT of enemies
    public bool Ally;              // CREATE of enemies: friendly npc
    public bool Initial;           // EVTS/ESTA within 0.5 s of the object's creation: the object's starting state, not a transition
    public bool Commence;          // PLAYER: the position at duty commence, not the first recorded one
    public bool HasFrom;           // TELEPORT: where the player jumped from
    public float FromX, FromY, FromZ;
    [JsonIgnore] public int Actor = -1;      // build-time index of the participant: one per actor even when the game reuses an instance id
    [JsonIgnore] public ulong NodeIdValue;   // NodeId parsed (Prepare)
    [JsonIgnore] public Vector3 Pos => new(X, Y, Z);

    // what the reporter and the events table print next to the name
    [JsonIgnore]
    public string Note => Kind switch
    {
        ZoneEventKind.Create => Type.ToString(),
        ZoneEventKind.Teleport when HasFrom => $"from ({FromX:f1}, {FromY:f1}, {FromZ:f1})",
        ZoneEventKind.Player when Commence => "commence",
        _ => "",
    };
}

// one enemy of the run: where and when it appeared and died (the "set" a key or warp waits on is read off these)
public sealed class ZoneEnemyDto
{
    public uint Oid;
    public uint NameId;
    public string Name = "";
    public ulong Instance;
    public double Spawn;
    public double Death = -1;
    public float X, Y, Z;   // spawn position
    public float DX, DZ;    // death position
    public int Area = -1;
    public uint MaxHp;
    public bool Targetable;
    public bool Ally;
    public ZoneEnemyClass Class;
    public uint Marker;        // instance key of the spawn-marker event object it came out of, 0 = none within 3 y
    [JsonIgnore] public int Actor = -1;
    [JsonIgnore] public Vector3 Pos => new(X, Y, Z);
}

// a player's positions over the run, throttled (t, x, y, z quads)
public sealed class ZonePlayerTrack
{
    public ulong Instance;
    public string Name = "";
    public double End;         // when the actor left the run (the run's end when it never did)
    public float[] Samples = [];
    [JsonIgnore] public float MinX, MinZ, MaxX, MaxZ;

    public bool LiveAt(double t) => Samples.Length > 0 && t >= Samples[0] && t <= End + 0.5;
}

// a boss's positions while it was up, its hitbox and the pull / death times
public sealed class ZoneBossTrack
{
    public uint Oid;
    public uint NameId;
    public string Name = "";
    public ulong Instance;
    public double Pull;
    public double Death = -1;
    public float Hitbox;
    public float[] Samples = [];
    [JsonIgnore] public int Actor = -1;
    [JsonIgnore] public float MinX, MinZ, MaxX, MaxZ;
}

// a floor piece's presence changing at time t (platform animation states or map effects mapped to a piece)
public sealed class ZoneFloorStateDto
{
    public double T;
    public string PieceNodeId = "";
    public bool Present;
    public uint State;      // the animation / map effect state that caused it
    public ZoneStateChannel Source; // EAnim | MapEffect
}

// a warp landing seen in the replay (the model's links are rebuilt from these on import)
public sealed class ZoneFlowLinkDto
{
    public string EObjNodeId = "";
    public string PopNodeId = "";
    public float X, Y, Z;
}

// enemies of one kind in a kill set, printed "0x{oid} name x{count}"
public sealed class ZoneKillGroup
{
    public uint Oid;
    public string Name = "";
    public int Count = 1;

    public override string ToString() => $"0x{Oid:X} {Name}{(Count > 1 ? $" x{Count}" : "")}";
}

// an inferred cause -> effect pair, as data; the editor lists them and the code generator turns them into component snippets
public sealed class ZoneMechanicRule
{
    public ZoneRuleKind Kind;
    public ZoneStateChannel Channel; // which state channel the effect object uses (EAnim / MapEffect: floor changes)
    public double T;                // when the effect happened (seconds from the first replay op)
    public string Trigger = "";
    public string Effect = "";
    public string Area = "";        // map area of the effect, for rules whose effect has no layout node (waves, adds, spawns)
    public float DelaySeconds;
    public float Confidence = 1f;
    public List<string> Evidence = [];
    public string TriggerNodeId = "";
    public string EffectNodeId = "";
    public uint TriggerOid;
    public uint EffectOid;
    public int TriggerState = -1;
    public int EffectState = -1;
    public string ArenaPre = "";
    public string ArenaPost = "";
    public List<ZoneKillGroup> RequiredKills = [];    // enemies that had died when the effect fired
    public List<ZoneKillGroup> NotRequiredKills = []; // enemies of the same area still alive at that moment: not needed for the effect

    // what the trigger text ends with when part of the area was still alive
    public static string StillAliveNote(List<ZoneKillGroup> alive) => alive.Count > 0 ? $" [still alive, not required: {string.Join(", ", alive)}]" : "";
}

// one step of the run in order: entrance, sets cleared, waves, warps opened and taken, seals, boss kills, exit
public sealed class ZoneScenarioStep
{
    public double T;
    public ZoneStepKind Kind;
    public string Area = "";
    public string Text = "";
    public string NodeId = "";    // the object involved
    public string TargetNodeId = ""; // where a warp lands (pop point)
    public uint Oid;
    public float X, Z;
}

public sealed class ZoneSceneTimelineFile
{
    public uint Territory;
    public uint Cfc;
    public string Replay = "";
    public double Duration;
    public List<ZoneTimelineEvent> Events = [];
    public List<ZoneEnemyDto> Enemies = [];
    public List<ZonePlayerTrack> Players = [];
    public List<ZoneBossTrack> Bosses = [];
    public List<ZoneFlowLinkDto> Links = [];
    public List<ZoneFloorStateDto> FloorStates = [];
    public List<ZoneMechanicRule> Rules = [];
    public List<ZoneScenarioStep> Scenario = [];
    [JsonIgnore] public List<(string nodeId, ZoneObjectRole role)> RoleChanges = []; // what the replay taught about the model's objects; applied on import, never by the builder

    private double _piecesT = double.NaN;
    private int _piecesCount = -1;
    private Dictionary<string, bool>? _pieces;
    private Dictionary<ulong, (double T, ushort State)[]>? _eventStates;  // per node, in time order (initial states included)
    private Dictionary<ulong, (double T, ushort State)[]>? _objectStates;
    private ZoneTimelineEvent[] _stateChanges = [];                       // state changes of matched objects without the initial states, in time order
    private double[] _stateChangeTimes = [];
    private int _stateIndexCount = -1;

    // derived fields after a build or a json load: parsed node ids, track extents, the per-node state index
    public void Prepare()
    {
        foreach (var e in Events)
        {
            e.NodeIdValue = e.NodeId.Length > 0 ? ParseId(e.NodeId) : 0;
        }
        BuildStateIndex();
        foreach (var p in Players)
        {
            Extent(p.Samples, out p.MinX, out p.MinZ, out p.MaxX, out p.MaxZ);
        }
        foreach (var b in Bosses)
        {
            Extent(b.Samples, out b.MinX, out b.MinZ, out b.MaxX, out b.MaxZ);
        }
        _pieces = null;
    }

    private static void Extent(float[] samples, out float minX, out float minZ, out float maxX, out float maxZ)
    {
        minX = minZ = float.MaxValue;
        maxX = maxZ = float.MinValue;
        var n = samples.Length / 4;
        for (var i = 0; i < n; ++i)
        {
            var x = samples[4 * i + 1];
            var z = samples[4 * i + 3];
            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minZ = MathF.Min(minZ, z);
            maxZ = MathF.Max(maxZ, z);
        }
    }

    // events are in time order, so appending per node keeps every node's states sorted
    private void BuildStateIndex()
    {
        Dictionary<ulong, List<(double T, ushort State)>> eventStates = [];
        Dictionary<ulong, List<(double T, ushort State)>> objectStates = [];
        List<ZoneTimelineEvent> changes = [];
        foreach (var e in Events)
        {
            if (e.NodeId.Length == 0 || e.Kind is not (ZoneEventKind.EventState or ZoneEventKind.ObjectState))
            {
                continue;
            }
            var states = e.Kind == ZoneEventKind.EventState ? eventStates : objectStates;
            if (!states.TryGetValue(e.NodeIdValue, out var list))
            {
                states[e.NodeIdValue] = list = [];
            }
            list.Add((e.T, e.State));
            if (!e.Initial)
            {
                changes.Add(e);
            }
        }
        _eventStates = Freeze(eventStates);
        _objectStates = Freeze(objectStates);
        _stateChanges = [.. changes];
        _stateChangeTimes = [.. changes.Select(e => e.T)];
        _stateIndexCount = Events.Count;
    }

    private static Dictionary<ulong, (double T, ushort State)[]> Freeze(Dictionary<ulong, List<(double T, ushort State)>> src)
    {
        Dictionary<ulong, (double T, ushort State)[]> r = new(src.Count);
        foreach (var (node, list) in src)
        {
            r[node] = [.. list];
        }
        return r;
    }

    // a file put together by hand (no Prepare) or one that grew since is indexed on first use
    private void EnsureStateIndex()
    {
        if (_eventStates == null || _objectStates == null || _stateIndexCount != Events.Count)
        {
            BuildStateIndex();
        }
    }

    // cumulative event object states at time t (EVTS and ESTA of matched objects): per node the last state at or before t
    public ZoneSceneState StateAt(double t)
    {
        EnsureStateIndex();
        var s = new ZoneSceneState();
        Fill(_eventStates!, t, s.EObjStates);
        Fill(_objectStates!, t, s.EObjObjectStates);
        return s;
    }

    private static void Fill(Dictionary<ulong, (double T, ushort State)[]> index, double t, Dictionary<ulong, ushort> dst)
    {
        foreach (var (node, states) in index)
        {
            var lo = 0;
            var hi = states.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (states[mid].T <= t)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            if (lo > 0)
            {
                dst[node] = states[lo - 1].State;
            }
        }
    }

    // index of the first state change later than t (strict) or at / after t
    private int FirstStateChange(double t, bool strict)
    {
        var lo = 0;
        var hi = _stateChangeTimes.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (strict ? _stateChangeTimes[mid] <= t : _stateChangeTimes[mid] < t)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    // the first state change after t that passes the filter (an object's initial state is not a change)
    public ZoneTimelineEvent? NextStateChange(double t, Func<ZoneTimelineEvent, bool>? filter = null)
    {
        EnsureStateIndex();
        for (var i = FirstStateChange(t, true); i < _stateChanges.Length; ++i)
        {
            if (filter == null || filter(_stateChanges[i]))
            {
                return _stateChanges[i];
            }
        }
        return null;
    }

    // the last state change before t that passes the filter
    public ZoneTimelineEvent? PreviousStateChange(double t, Func<ZoneTimelineEvent, bool>? filter = null)
    {
        EnsureStateIndex();
        for (var i = FirstStateChange(t, false) - 1; i >= 0; --i)
        {
            if (filter == null || filter(_stateChanges[i]))
            {
                return _stateChanges[i];
            }
        }
        return null;
    }

    // which floor pieces are gone at time t (absent = present); the result is shared between callers of the same time, not to be changed
    public Dictionary<string, bool> PiecesPresentAt(double t)
    {
        if (_pieces != null && t == _piecesT && FloorStates.Count == _piecesCount)
        {
            return _pieces;
        }
        Dictionary<string, bool> r = [];
        foreach (var s in FloorStates)
        {
            if (s.T > t)
            {
                break;
            }
            r[s.PieceNodeId] = s.Present;
        }
        _pieces = r;
        _piecesT = t;
        _piecesCount = FloorStates.Count;
        return r;
    }

    public static bool PieceGone(Dictionary<string, bool>? present, ZoneFloorPiece p) => present != null && present.TryGetValue(FormatId(p.PathId), out var pr) && !pr;

    // every player's position at time t (interpolated between samples) while the member is in the run, for the scrub marker and the coverage check
    public IEnumerable<(ZonePlayerTrack track, Vector3 pos)> PlayersAt(double t)
    {
        foreach (var p in Players)
        {
            if (p.LiveAt(t) && SampleAt(p.Samples, t) is { } pos)
            {
                yield return (p, pos);
            }
        }
    }

    public static Vector3? SampleAt(float[] samples, double t)
    {
        var n = samples.Length / 4;
        if (n == 0 || t < samples[0])
        {
            return null;
        }
        var lo = 0;
        var hi = n - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (samples[4 * mid] <= t)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (lo + 1 >= n)
        {
            return new(samples[4 * lo + 1], samples[4 * lo + 2], samples[4 * lo + 3]);
        }
        var t0 = samples[4 * lo];
        var t1 = samples[4 * lo + 4];
        var f = t1 > t0 ? (float)Math.Clamp((t - t0) / (t1 - t0), 0.0, 1.0) : 0f;
        return Vector3.Lerp(new(samples[4 * lo + 1], samples[4 * lo + 2], samples[4 * lo + 3]), new(samples[4 * lo + 5], samples[4 * lo + 6], samples[4 * lo + 7]), f);
    }

    public static ulong ParseId(string hex) => hex.StartsWith("0x") ? Convert.ToUInt64(hex[2..], 16) : Convert.ToUInt64(hex, 16);
    public static string FormatId(ulong id) => $"0x{id:X16}";
}

// the same rule across several runs of a duty: agreement becomes confidence, the kill sets intersect (what died every time) minus what was
// ever seen alive when the effect fired (never required)
public static class ZoneRuleMerger
{
    // rules whose effect has no layout node are told apart by the area they happened in (the same mob waves in two rooms)
    public static string Key(ZoneMechanicRule r) => $"{r.Kind}|{(r.EffectNodeId.Length > 0 ? r.EffectNodeId : $"0x{r.EffectOid:X}|{r.Area}")}|{(r.Kind is ZoneRuleKind.WarpTeleport or ZoneRuleKind.ShortcutTeleport ? r.TriggerNodeId : "")}";

    public static List<ZoneMechanicRule> Merge(IReadOnlyList<ZoneSceneTimelineFile> files)
    {
        List<ZoneMechanicRule> result = [];
        if (files.Count == 0)
        {
            return result;
        }
        var groups = files.SelectMany(f => f.Rules.Select(r => (f, r))).GroupBy(x => Key(x.r));
        foreach (var g in groups)
        {
            var first = g.First().r;
            var runs = g.Select(x => x.f.Replay).Distinct().Count();
            var m = new ZoneMechanicRule
            {
                Kind = first.Kind,
                Channel = first.Channel,
                T = g.Min(x => x.r.T),
                Trigger = first.Trigger,
                Effect = first.Effect,
                Area = first.Area,
                DelaySeconds = g.Average(x => x.r.DelaySeconds),
                Confidence = files.Count == 1 ? first.Confidence : g.Average(x => x.r.Confidence) * runs / files.Count,
                TriggerNodeId = first.TriggerNodeId,
                EffectNodeId = first.EffectNodeId,
                TriggerOid = first.TriggerOid,
                EffectOid = first.EffectOid,
                TriggerState = first.TriggerState,
                EffectState = first.EffectState,
                ArenaPre = first.ArenaPre,
                ArenaPost = first.ArenaPost,
                Evidence = [.. g.SelectMany(x => x.r.Evidence)],
            };
            if (g.Any(x => x.r.RequiredKills.Count > 0))
            {
                // counts differ between runs: a kind of enemy is compared by oid and name
                var required = g.Select(x => x.r.RequiredKills.Select(k => (k.Oid, k.Name)).ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; });
                var notRequired = g.SelectMany(x => x.r.NotRequiredKills.Select(k => (k.Oid, k.Name))).Distinct().ToList();
                required.ExceptWith(notRequired);
                m.RequiredKills = [.. required.Select(k => new ZoneKillGroup { Oid = k.Oid, Name = k.Name })];
                m.NotRequiredKills = [.. notRequired.Select(k => new ZoneKillGroup { Oid = k.Oid, Name = k.Name })];
                if (runs > 1)
                {
                    var alive = ZoneMechanicRule.StillAliveNote(first.NotRequiredKills);
                    var trigger = alive.Length > 0 && first.Trigger.EndsWith(alive, StringComparison.Ordinal) ? first.Trigger[..^alive.Length] : first.Trigger;
                    m.Trigger = $"{trigger} [{runs} runs: required {(m.RequiredKills.Count > 0 ? string.Join(", ", m.RequiredKills) : "none in common")}{(m.NotRequiredKills.Count > 0 ? $"; not required {string.Join(", ", m.NotRequiredKills)}" : "")}]";
                }
            }
            result.Add(m);
        }
        result.Sort((a, b) => a.T.CompareTo(b.T));
        return result;
    }
}

// builds the timeline from the digested replay (participants, histories, director updates); only the EObjSetState packets need the raw ops
public static class ZoneSceneTimelineBuilder
{
    private const double InitialStateWindow = 0.5;
    private const float TeleportJump = 30f;
    private const float TrackMinDistance = 0.5f;
    private const double TrackMinInterval = 1.0;

    // one enemy / event object of the run: what every event of it carries
    private sealed class ActorInfo
    {
        public Replay.Participant P = null!;
        public int Index;
        public int Eobj = -1;
        public string Name = "";
        public uint NameId;
        public uint MaxHp;
        public bool Ally;
        public string NodeId = "";
        public ulong NodeIdValue;
        public string NodePath = "";
        public int[] Boxes = [];
        public int[] Seals = [];
    }

    // what the detectors share: deaths and targetability per actor, the duty commence
    private sealed class RunContext
    {
        public readonly Dictionary<int, double> Deaths = [];      // actor -> first death
        public readonly HashSet<int> Targetable = [];            // actors that were targetable at some point
        public readonly List<ZoneTimelineEvent> DeathEvents = []; // in time order
        public readonly List<ZoneFlowLink> Confirmed = [];       // warp landings this replay showed (applied to the model on import)
        public ZoneTimelineEvent? Commence;                      // DIRU 0x40000001
    }

    public static ZoneSceneTimelineFile Build(Replay replay, ZoneCollisionScene scene, ZoneSceneModel model, AutoMapSettings sealSettings, uint cfc = 0, CancellationToken ct = default)
    {
        var file = new ZoneSceneTimelineFile { Territory = scene.TerritoryId, Cfc = cfc, Replay = System.IO.Path.GetFileName(replay.Path) };
        if (replay.Ops.Count == 0)
        {
            return file;
        }
        var t0 = replay.Ops[0].Timestamp;
        var tEnd = replay.Ops[^1].Timestamp;
        file.Duration = (tEnd - t0).TotalSeconds;
        var seals = ArenaAutoMapper.FindSeals(scene, sealSettings);
        var events = file.Events;
        double T(DateTime t) => (t - t0).TotalSeconds;

        // participants of the territory only, from the moment the recording entered it (a recording started in town carries the town's
        // actors under another zone id, and the party's own positions before the zone change are the town's)
        var zoneEntry = replay.Ops.Find(op => op is WorldState.OpZoneChange z && z.Zone == scene.TerritoryId)?.Timestamp ?? t0;
        var inZone = replay.Participants.Where(p => p.ZoneID == scene.TerritoryId).ToList();
        // the party: players and duty support / trust members (Buddy actors owned by a player); pets and bystanders with one sample are not tracks
        var players = inZone.Where(p => (p.Type == ActorType.Player || p.Type == ActorType.Buddy && p.OwnerID != 0) && p.PosRotHistory.Count > 1 && p.PosRotHistory.Keys[^1] > zoneEntry).OrderBy(p => p.Type == ActorType.Player ? 0 : 1).ToList();
        List<ActorInfo> actors = [];
        Dictionary<ulong, List<ActorInfo>> byInstance = [];
        foreach (var p in inZone)
        {
            if (p.Type is not (ActorType.Enemy or ActorType.EventObj) || p.OID == 0)
            {
                continue;
            }
            var start = p.WorldExistence.Count > 0 ? p.WorldExistence[0].Start : p.EffectiveExistence.Start;
            var pos0 = p.PosRotAt(start);
            var (name0, nameId) = p.NameAt(start);
            var a = new ActorInfo
            {
                P = p,
                Index = actors.Count,
                Name = name0 ?? "",
                NameId = nameId,
                Eobj = p.Type == ActorType.EventObj ? model.FindEventObject(p.OID, p.LayoutID, new(pos0.X, pos0.Y, pos0.Z)) : -1,
                MaxHp = p.HPMPHistory.Count > 0 ? p.HPMPHistory.Values[0].MaxHP : 0u,
                Ally = p.AllyHistory.Count > 0 && p.AllyHistory.Values[0],
            };
            if (a.Eobj >= 0)
            {
                var eo = model.EventObjects[a.Eobj];
                if (a.Name.Length == 0)
                {
                    a.Name = eo.Name;
                }
                a.NodeIdValue = eo.PathId;
                a.NodeId = ZoneSceneTimelineFile.FormatId(eo.PathId);
                a.NodePath = scene.Nodes[eo.NodeIndex].Path;
                a.Boxes = eo.ControlledBoxes;
                a.Seals = [.. eo.SealBoxes.Select(b => seals.FindIndex(s => s.BoxIndex == b)).Where(i => i >= 0)];
            }
            actors.Add(a);
            if (!byInstance.TryGetValue(p.InstanceID, out var list))
            {
                byInstance[p.InstanceID] = list = [];
            }
            list.Add(a);
        }
        ZoneTimelineEvent At(ActorInfo a, DateTime t, ZoneEventKind kind)
        {
            var pr = a.P.PosRotAt(t);
            return new()
            {
                T = T(t),
                Kind = kind,
                Oid = a.P.OID,
                Instance = a.P.InstanceID,
                LayoutId = a.P.LayoutID,
                NameId = a.NameId,
                Name = a.Name,
                X = pr.X,
                Y = pr.Y,
                Z = pr.Z,
                NodeId = a.NodeId,
                NodeIdValue = a.NodeIdValue,
                NodePath = a.NodePath,
                Eobj = a.Eobj,
                Actor = a.Index,
                Boxes = a.Boxes,
                Seals = a.Seals,
                MaxHp = a.MaxHp,
                Ally = a.Ally,
            };
        }
        bool JustCreated(ActorInfo a, DateTime t) => a.P.WorldExistence.Any(r => (t - r.Start).TotalSeconds is >= 0 and < InitialStateWindow);

        // players: start positions (at duty commence when the replay has it), tracks and teleports
        var commence = replay.DirectorUpdates.Find(d => d.UpdateID == 0x40000001);
        List<(DateTime t, Vector4 pos, Vector4 prev)> jumps = [];
        foreach (var p in players)
        {
            ct.ThrowIfCancellationRequested();
            var name = p.NameHistory.Count > 0 ? p.NameHistory.Values[0].name : "";
            var first = p.PosRotHistory.Values[0];
            events.Add(new() { T = T(p.PosRotHistory.Keys[0]), Kind = ZoneEventKind.Player, Instance = p.InstanceID, Name = name, X = first.X, Y = first.Y, Z = first.Z });
            if (commence != null && p.ExistsInWorldAt(commence.Timestamp))
            {
                var at = p.PosRotAt(commence.Timestamp);
                events.Add(new() { T = T(commence.Timestamp), Kind = ZoneEventKind.Player, Instance = p.InstanceID, Name = name, X = at.X, Y = at.Y, Z = at.Z, Commence = true });
            }
            jumps.Clear();
            var last = p.WorldExistence.Count > 0 ? p.WorldExistence[^1].End : default;
            file.Players.Add(new() { Instance = p.InstanceID, Name = name, End = last != default && last < tEnd ? T(last) : file.Duration, Samples = Throttle(p.PosRotHistory, t0, zoneEntry, jumps) });
            foreach (var (t, pos, pv) in jumps)
            {
                events.Add(new() { T = T(t), Kind = ZoneEventKind.Teleport, Instance = p.InstanceID, Name = name, X = pos.X, Y = pos.Y, Z = pos.Z, HasFrom = true, FromX = pv.X, FromY = pv.Y, FromZ = pv.Z });
            }
        }

        // enemies and event objects: creation / destruction per existence range, state and death histories
        foreach (var a in actors)
        {
            ct.ThrowIfCancellationRequested();
            var p = a.P;
            if (p.WorldExistence.Count == 0)
            {
                continue;
            }
            foreach (var r in p.WorldExistence)
            {
                var c = At(a, r.Start, ZoneEventKind.Create);
                c.State = (ushort)(p.TargetableAt(r.Start) ? 1 : 0);
                c.Type = p.Type;
                events.Add(c);
                if (r.End != default && r.End < tEnd)
                {
                    events.Add(At(a, r.End, ZoneEventKind.Destroy));
                }
            }
            if (p.Type == ActorType.EventObj)
            {
                foreach (var (t, v) in p.EventState)
                {
                    var e = At(a, t, ZoneEventKind.EventState);
                    e.State = v;
                    e.Initial = JustCreated(a, t);
                    events.Add(e);
                }
                foreach (var (t, v) in p.EventObjectAnimation)
                {
                    var e = At(a, t, ZoneEventKind.EAnim);
                    e.P1 = v >> 16;
                    e.P2 = v & 0xFFFF;
                    events.Add(e);
                }
            }
            foreach (var (t, v) in p.TargetableHistory)
            {
                if (p.WorldExistence.Any(r => r.Start == t))
                {
                    continue; // the creation value is on the CREATE event
                }
                var e = At(a, t, ZoneEventKind.Targetable);
                e.State = (ushort)(v ? 1 : 0);
                events.Add(e);
            }
            var wasDead = false;
            DateTime? death = null;
            foreach (var (t, v) in p.DeadHistory)
            {
                if (v && !wasDead)
                {
                    events.Add(At(a, t, ZoneEventKind.Death));
                    death ??= t;
                }
                wasDead = v;
            }
            if (p.Type == ActorType.Enemy && a.Name.Length > 0)
            {
                var start = p.WorldExistence[0].Start;
                var pos0 = p.PosRotAt(start);
                var dpos = death is { } dt ? p.PosRotAt(dt) : pos0;
                file.Enemies.Add(new()
                {
                    Oid = p.OID,
                    NameId = a.NameId,
                    Name = a.Name,
                    Instance = p.InstanceID,
                    Actor = a.Index,
                    Spawn = T(start),
                    Death = death is { } d2 ? T(d2) : -1,
                    X = pos0.X,
                    Y = pos0.Y,
                    Z = pos0.Z,
                    DX = dpos.X,
                    DZ = dpos.Z,
                    Area = model.AreaAt(new(pos0.X, pos0.Y, pos0.Z)),
                    MaxHp = a.MaxHp,
                    Targetable = p.TargetableHistory.Any(x => x.Value),
                    Ally = a.Ally,
                });
            }
        }
        // EObjSetState packets are not digested by ReplayBuilder: the one thing still read from the ops
        var opsSeen = 0;
        foreach (var op in replay.Ops)
        {
            if ((++opsSeen & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (op is not ActorState.OpEventObjectStateChange es || !byInstance.TryGetValue(es.InstanceID, out var cands))
            {
                continue;
            }
            var a = cands.Find(x => x.P.EffectiveExistence.Contains(op.Timestamp)) ?? cands[0];
            var e = At(a, op.Timestamp, ZoneEventKind.ObjectState);
            e.State = es.State;
            e.Initial = JustCreated(a, op.Timestamp);
            events.Add(e);
        }
        foreach (var du in replay.DirectorUpdates)
        {
            events.Add(new() { T = T(du.Timestamp), Kind = ZoneEventKind.DirectorUpdate, Dir = du.DirectorID, Upd = du.UpdateID, P1 = du.Param1, P2 = du.Param2, P3 = du.Param3, P4 = du.Param4 });
        }
        foreach (var me in replay.MapEffects)
        {
            events.Add(new() { T = T(me.Timestamp), Kind = ZoneEventKind.MapEffect, State = me.Index, P1 = me.State });
        }
        SortStable(events);
        ct.ThrowIfCancellationRequested();
        var ctx = new RunContext();
        foreach (var e in events)
        {
            if (e.Kind == ZoneEventKind.Death)
            {
                ctx.DeathEvents.Add(e);
                ctx.Deaths.TryAdd(e.Actor, e.T);
            }
            else if ((e.Kind == ZoneEventKind.Create || e.Kind == ZoneEventKind.Targetable) && e.State == 1)
            {
                ctx.Targetable.Add(e.Actor);
            }
            else if (e.Kind == ZoneEventKind.DirectorUpdate && e.Upd == 0x40000001)
            {
                ctx.Commence ??= e;
            }
        }
        InferRules(file, model, events, ctx);
        ct.ThrowIfCancellationRequested();
        InferFlow(file, model, events, ctx);
        ct.ThrowIfCancellationRequested();
        BuildBossTracks(file, replay, actors, t0);
        InferFloorChanges(file, model, events);
        ClassifyEnemies(file, model);
        // landings: this run's, then what earlier runs confirmed on the model (the model itself changes only on import, on the ui thread)
        foreach (var l in ctx.Confirmed)
        {
            file.Links.Add(LinkDto(model, l));
        }
        foreach (var l in model.Links)
        {
            if (l.Confidence >= 0.5f && !ctx.Confirmed.Exists(c => c.EObj == l.EObj))
            {
                file.Links.Add(LinkDto(model, l));
            }
        }
        BuildScenario(file, model, events);
        file.Prepare();
        return file;
    }

    private static ZoneFlowLinkDto LinkDto(ZoneSceneModel model, ZoneFlowLink l) => new() { EObjNodeId = ZoneSceneTimelineFile.FormatId(model.EventObjects[l.EObj].PathId), PopNodeId = l.Pop >= 0 ? ZoneSceneTimelineFile.FormatId(model.PopPoints[l.Pop].PathId) : "", X = l.Landing.X, Y = l.Landing.Y, Z = l.Landing.Z };

    // the replay's landing for a warp, one per object (the last seen wins, as the model's ConfirmLink does)
    private static void Confirm(RunContext ctx, int eobj, int pop, Vector3 landing, string source)
    {
        ctx.Confirmed.RemoveAll(l => l.EObj == eobj);
        ctx.Confirmed.Add(new() { EObj = eobj, Pop = pop, Landing = landing, Confidence = 1f, Source = source });
    }

    // by time, insertion order between equal times
    private static void SortStable(List<ZoneTimelineEvent> events)
    {
        var n = events.Count;
        var order = new int[n];
        for (var i = 0; i < n; ++i)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) =>
        {
            var c = events[a].T.CompareTo(events[b].T);
            return c != 0 ? c : a.CompareTo(b);
        });
        var sorted = new ZoneTimelineEvent[n];
        for (var i = 0; i < n; ++i)
        {
            sorted[i] = events[order[i]];
        }
        events.Clear();
        events.AddRange(sorted);
    }

    private static string AreaName(ZoneSceneModel model, in Vector3 p)
    {
        var a = model.AreaAt(p);
        return a >= 0 ? model.Areas[a].Name : "";
    }

    // on load / wave / boss adds per enemy: what was around when it appeared decides what spawned it
    private static void ClassifyEnemies(ZoneSceneTimelineFile file, ZoneSceneModel model)
    {
        foreach (var e in file.Enemies)
        {
            var marker = model.SpawnMarkerNear(e.Pos);
            e.Marker = marker >= 0 ? model.EventObjects[marker].InstanceKey : 0;
            if (file.Bosses.Exists(b => b.Actor >= 0 ? b.Actor == e.Actor : b.Instance == e.Instance))
            {
                e.Class = ZoneEnemyClass.Boss;
                continue;
            }
            var boss = false;
            foreach (var b in file.Bosses)
            {
                if (e.Spawn < b.Pull - 5.0 || b.Death >= 0 && e.Spawn > b.Death)
                {
                    continue;
                }
                if (ZoneSceneTimelineFile.SampleAt(b.Samples, e.Spawn) is { } bp && ZoneSceneModel.DistXZ(bp, e.Pos) <= 60f)
                {
                    boss = true;
                    break;
                }
            }
            if (boss)
            {
                e.Class = ZoneEnemyClass.BossAdds;
                continue;
            }
            // a wave needs the party in the room already; a room that streams in as the party lands places its enemies on load
            var playerNear = false;
            foreach (var p in file.Players)
            {
                if (ZoneSceneTimelineFile.SampleAt(p.Samples, e.Spawn) is { } pp && ZoneSceneModel.DistXZ(pp, e.Pos) <= 50f
                    && ZoneSceneTimelineFile.SampleAt(p.Samples, e.Spawn - 5.0) is { } pb && ZoneSceneModel.DistXZ(pb, e.Pos) <= 50f)
                {
                    playerNear = true;
                    break;
                }
            }
            e.Class = playerNear && e.Spawn > 5.0 ? ZoneEnemyClass.Wave : ZoneEnemyClass.OnLoad;
        }
    }

    // the game-wide meaning of the animation / map effect states the arena-changing modules key on
    public static bool? PresentAfter(uint state) => state switch
    {
        0x00020001u or 0x00010002u or 0x00200010u => true,
        0x00080004u or 0x00040008u or 0x00800040u => false,
        0x00100020u => true, // about to break: still there
        _ => null,
    };

    public static string StateWord(uint state) => state switch
    {
        0x00100020u => "about to break",
        0x00040008u => "broken",
        0x00080004u => "removed",
        0x00020001u or 0x00010002u => "appears",
        0x00200010u => "restored",
        0x00800040u => "gone",
        _ => $"state 0x{state:X8}",
    };

    // floor changes: an animation state on a platform object maps straight to its piece; map effects during a boss fight in a group of sections
    // are the change's trigger but which section they hit needs the module (or a second replay), so they become half-confidence rules
    private static void InferFloorChanges(ZoneSceneTimelineFile file, ZoneSceneModel model, List<ZoneTimelineEvent> events)
    {
        var rules = file.Rules;
        HashSet<string> seen = [];
        foreach (var e in events.Where(e => e.Kind == ZoneEventKind.EAnim && e.Eobj >= 0))
        {
            var piece = model.FloorPieces.Find(p => p.Kind == ZoneFloorPieceKind.Platform && p.EObj == e.Eobj);
            if (piece == null)
            {
                continue;
            }
            var state = e.P1 << 16 | e.P2;
            var present = PresentAfter(state);
            var id = ZoneSceneTimelineFile.FormatId(piece.PathId);
            if (present is { } pr)
            {
                file.FloorStates.Add(new() { T = e.T, PieceNodeId = id, Present = pr, State = state, Source = ZoneStateChannel.EAnim });
            }
            if (!seen.Add($"{id}|{state:X}"))
            {
                continue;
            }
            var eo = model.EventObjects[e.Eobj];
            rules.Add(new()
            {
                Kind = ZoneRuleKind.FloorChange,
                Channel = ZoneStateChannel.EAnim,
                T = e.T,
                Trigger = $"animation 0x{state:X8} on {eo.Label} key 0x{eo.InstanceKey:X}",
                Effect = $"{piece.Label} {StateWord(state)} ({model.FloorGroups[piece.Group].Name})",
                Area = model.FloorGroups[piece.Group].Name,
                TriggerOid = eo.BaseId,
                TriggerNodeId = ZoneSceneTimelineFile.FormatId(eo.PathId),
                TriggerState = (int)state,
                EffectNodeId = id,
                EffectState = present is { } p2 ? (p2 ? 1 : 0) : -1,
                Confidence = present != null ? 1f : 0.6f,
                Evidence = [$"{file.Replay}: t={e.T:f1}s EANM {e.P1:X4}/{e.P2:X4} at ({e.X:f1}, {e.Z:f1})"],
            });
        }
        // map effects inside a boss fight whose room has sections but no platforms
        var mapGroups = model.FloorGroups.Where(g => g.Family == ZoneFloorFamily.MapEffect).ToList();
        if (mapGroups.Count == 0)
        {
            return;
        }
        foreach (var b in file.Bosses)
        {
            var group = mapGroups.Find(g => ZoneSceneTimelineFile.SampleAt(b.Samples, b.Pull) is { } at && model.Areas[g.Area].WorldBounds.ContainsXZ(at.X, at.Z));
            if (group == null)
            {
                continue;
            }
            var end = b.Death >= 0 ? b.Death : file.Duration;
            foreach (var e in events.Where(e => e.Kind == ZoneEventKind.MapEffect && e.T >= b.Pull && e.T <= end))
            {
                var key = $"mapfx|{group.Index}|{e.State}|{e.P1:X}";
                if (!seen.Add(key))
                {
                    continue;
                }
                var word = StateWord(e.P1);
                rules.Add(new()
                {
                    Kind = ZoneRuleKind.FloorChange,
                    Channel = ZoneStateChannel.MapEffect,
                    T = e.T,
                    Trigger = $"map effect index 0x{e.State:X2} state 0x{e.P1:X8} during {b.Name}",
                    Effect = $"a section of '{group.Name}' {word}: which one needs the module or a second replay ({group.Pieces.Count} sections)",
                    Area = group.Name,
                    TriggerOid = b.Oid,
                    TriggerState = e.State,
                    EffectState = (int)e.P1,
                    Confidence = PresentAfter(e.P1) != null ? 0.5f : 0.3f,
                    Evidence = [$"{file.Replay}: t={e.T:f1}s MAPFX index {e.State:X2} state {e.P1:X8}"],
                });
            }
        }
    }

    // positions thinned to what the canvas needs: a sample when the actor moved half a yalm or a second passed; jumps over 30 y between
    // consecutive raw positions (teleports) are reported on the side
    private static float[] Throttle(SortedList<DateTime, Vector4> history, DateTime t0, DateTime from = default, List<(DateTime t, Vector4 pos, Vector4 prev)>? jumps = null)
    {
        List<float> r = [];
        Vector4? last = null;
        Vector4? prev = null;
        var lastT = double.MinValue;
        foreach (var (t, pos) in history)
        {
            if (t < from)
            {
                continue;
            }
            if (jumps != null && prev is { } pv && (new Vector3(pos.X, pos.Y, pos.Z) - new Vector3(pv.X, pv.Y, pv.Z)).Length() > TeleportJump)
            {
                jumps.Add((t, pos, pv));
            }
            prev = pos;
            var ts = (t - t0).TotalSeconds;
            if (last is { } l && (new Vector3(pos.X, pos.Y, pos.Z) - new Vector3(l.X, l.Y, l.Z)).Length() < TrackMinDistance && ts - lastT < TrackMinInterval)
            {
                continue;
            }
            r.Add((float)ts);
            r.Add(pos.X);
            r.Add(pos.Y);
            r.Add(pos.Z);
            last = pos;
            lastT = ts;
        }
        return [.. r];
    }

    // bosses = the enemies the seal rules pulled on, plus the replay's own encounters
    private static void BuildBossTracks(ZoneSceneTimelineFile file, Replay replay, List<ActorInfo> actors, DateTime t0)
    {
        var oids = file.Rules.Where(r => r.Kind == ZoneRuleKind.SealOnPull && r.TriggerOid != 0).Select(r => r.TriggerOid).Concat(replay.Encounters.Select(e => e.OID)).ToHashSet();
        foreach (var a in actors)
        {
            var p = a.P;
            if (p.Type != ActorType.Enemy || !oids.Contains(p.OID) || p.PosRotHistory.Count < 2)
            {
                continue;
            }
            var pull = p.TargetableHistory.FirstOrDefault(x => x.Value).Key;
            if (pull == default)
            {
                pull = p.WorldExistence.Count > 0 ? p.WorldExistence[0].Start : p.PosRotHistory.Keys[0];
            }
            var death = p.DeadHistory.FirstOrDefault(x => x.Value).Key;
            file.Bosses.Add(new()
            {
                Oid = p.OID,
                NameId = p.NameHistory.Count > 0 ? p.NameHistory.Values[0].id : 0,
                Name = p.NameHistory.Count > 0 ? p.NameHistory.Values[0].name : "",
                Instance = p.InstanceID,
                Actor = a.Index,
                Pull = (pull - t0).TotalSeconds,
                Death = death != default ? (death - t0).TotalSeconds : -1,
                Hitbox = p.MaxRadius > 0f ? p.MaxRadius : 0f,
                Samples = Throttle(p.PosRotHistory, t0),
            });
        }
    }

    // a set of enemies by kind, in first-seen order
    public static List<ZoneKillGroup> Groups(IEnumerable<(uint Oid, string Name)> xs) => [.. xs.GroupBy(x => x).Select(g => new ZoneKillGroup { Oid = g.Key.Oid, Name = g.Key.Name, Count = g.Count() })];

    // "0x{oid} name x{count}, ..."
    public static string Group(IEnumerable<(uint Oid, string Name)> xs) => string.Join(", ", Groups(xs));

    private static void InferRules(ZoneSceneTimelineFile file, ZoneSceneModel model, List<ZoneTimelineEvent> events, RunContext ctx)
    {
        var rules = file.Rules;
        var bosses = events.Where(e => e.Kind == ZoneEventKind.Create && e.Type == ActorType.Enemy).ToList();
        // (a) seal controllers: EVTS 0 near a boss becoming targetable = sealed for the pull, EVTS 7 near the boss death = released
        foreach (var e in events.Where(e => e.Kind == ZoneEventKind.EventState && !e.Initial && e.Eobj >= 0 && model.EventObjects[e.Eobj].IsSealController))
        {
            var eo = model.EventObjects[e.Eobj];
            if (e.State == 0)
            {
                // the boss: the biggest enemy alive near the seal when it goes up, the one created closest to the seal for equal hp
                ZoneTimelineEvent? pull = null;
                foreach (var x in bosses)
                {
                    if (x.T > e.T + 10.0 || x.Name.Length == 0 || x.Ally || !ctx.Targetable.Contains(x.Actor) || ctx.Deaths.TryGetValue(x.Actor, out var dt) && dt <= e.T || ZoneSceneModel.DistXZ(x.Pos, e.Pos) > 100f)
                    {
                        continue;
                    }
                    if (pull == null || x.MaxHp > pull.MaxHp || x.MaxHp == pull.MaxHp && Math.Abs(x.T - e.T) < Math.Abs(pull.T - e.T))
                    {
                        pull = x;
                    }
                }
                rules.Add(new()
                {
                    Kind = ZoneRuleKind.SealOnPull,
                    T = e.T,
                    Trigger = pull != null ? $"pull of 0x{pull.Oid:X} '{pull.Name}'" : "pull (no enemy alive near the seal)",
                    Effect = $"{eo.Label} EventState 0: seal collision on",
                    Area = AreaName(model, eo.ActorPosition),
                    TriggerOid = pull?.Oid ?? 0,
                    EffectOid = eo.BaseId,
                    EffectNodeId = e.NodeId,
                    EffectState = 0,
                    DelaySeconds = pull != null ? (float)(e.T - pull.T) : 0f,
                    Confidence = pull != null ? 1f : 0.5f,
                    Evidence = [$"{file.Replay}: t={e.T:f1}s EVTS 0 on {eo.Label} key 0x{eo.InstanceKey:X}{(pull != null ? $", boss spawned t={pull.T:f1}s" : "")}"],
                });
            }
            else if (e.State == 7 && e.T > 1.0)
            {
                var kill = ctx.DeathEvents.Where(x => x.T <= e.T && x.T >= e.T - 8.0).MaxBy(x => x.T);
                var diru = events.Find(x => x.Kind == ZoneEventKind.DirectorUpdate && Math.Abs(x.T - e.T) <= 1.0);
                rules.Add(new()
                {
                    Kind = ZoneRuleKind.SealRelease,
                    T = e.T,
                    Trigger = kill != null ? $"death of 0x{kill.Oid:X} '{kill.Name}'" : "release (no death within 8 s)",
                    Effect = $"{eo.Label} EventState 7: seal collision removed",
                    Area = AreaName(model, eo.ActorPosition),
                    TriggerOid = kill?.Oid ?? 0,
                    EffectOid = eo.BaseId,
                    EffectNodeId = e.NodeId,
                    EffectState = 7,
                    DelaySeconds = kill != null ? (float)(e.T - kill.T) : 0f,
                    Confidence = kill != null ? 1f : 0.5f,
                    Evidence = [$"{file.Replay}: t={e.T:f1}s EVTS 7 on {eo.Label} key 0x{eo.InstanceKey:X}{(kill != null ? $", death t={kill.T:f1}s" : "")}{(diru != null ? $", DIRU {diru.Upd:X8} p1={diru.P1:X} p2={diru.P2:X}" : "")}"],
                });
            }
        }
        // (b) an event object appearing right after enemies died around it: the trigger set is every enemy that died nearby in the minute before
        // the spawn (the scripted set is not in the client files); objects that only toggle collision or carry a vfx are streamed in, not spawned
        foreach (var e in events.Where(e => e.Kind == ZoneEventKind.Create && e.Type == ActorType.EventObj && (e.LayoutId == 0 || e.Eobj >= 0 && !model.EventObjects[e.Eobj].ControlsCollision && !model.EventObjects[e.Eobj].IsBarrierVfx)))
        {
            var dead = file.Enemies.Where(x => x.Death >= 0 && x.Death <= e.T + 0.5 && x.Death >= e.T - 60.0 && ZoneSceneModel.DistXZ(new(x.DX, 0f, x.DZ), e.Pos) <= 40f).OrderBy(x => x.Death).ToList();
            if (dead.Count == 0 || e.T - dead[^1].Death > 10.0)
            {
                continue;
            }
            var last = dead[^1];
            var setRule = new ZoneMechanicRule
            {
                Kind = ZoneRuleKind.SetClearSpawns,
                T = e.T,
                Trigger = $"last of {dead.Count} enemies dies ({Group(dead.Select(x => (x.Oid, x.Name)))})",
                Effect = $"0x{e.Oid:X} '{e.Name}' spawns at ({e.X:f1}, {e.Z:f1})",
                Area = AreaName(model, e.Pos),
                TriggerOid = last.Oid,
                EffectOid = e.Oid,
                EffectNodeId = e.NodeId,
                DelaySeconds = (float)(e.T - last.Death),
                Evidence = [$"{file.Replay}: deaths t={dead[0].Death:f1}s..{last.Death:f1}s, spawn t={e.T:f1}s"],
            };
            KillSets(setRule, model, file.Enemies, e.T, e.Pos, 40f);
            rules.Add(setRule);
        }
        // (c) a door opening (EVTS 7 on a door controller) after a pickup object was consumed (EVTS 7): the nearest preceding pickup within 30 s
        foreach (var d in events.Where(d => d.Kind == ZoneEventKind.EventState && d.State == 7 && !d.Initial && d.Eobj >= 0 && model.EventObjects[d.Eobj].IsDoor))
        {
            var k = events.Where(e => e.Kind == ZoneEventKind.EventState && e.State == 7 && !e.Initial && e.T < d.T && e.T >= d.T - 30.0 && (e.Eobj < 0 || !model.EventObjects[e.Eobj].ControlsCollision && !model.EventObjects[e.Eobj].IsBarrierVfx)).MaxBy(e => e.T);
            if (k != null)
            {
                var door = model.EventObjects[d.Eobj];
                rules.Add(new()
                {
                    Kind = ZoneRuleKind.KeyOpensDoor,
                    T = d.T,
                    Trigger = $"0x{k.Oid:X} '{k.Name}' used (EventState 7)",
                    Effect = $"door {door.Label} opens (EventState 7, {door.ControlledBoxes.Length} box(es) removed)",
                    Area = AreaName(model, door.ActorPosition),
                    TriggerOid = k.Oid,
                    TriggerNodeId = k.NodeId,
                    TriggerState = 7,
                    EffectOid = door.BaseId,
                    EffectNodeId = d.NodeId,
                    EffectState = 7,
                    DelaySeconds = (float)(d.T - k.T),
                    Evidence = [$"{file.Replay}: key EVTS 7 t={k.T:f1}s, door EVTS 7 t={d.T:f1}s"],
                });
            }
        }
        // collapse duplicates of the same kind/trigger/effect keys
        var seen = new HashSet<string>();
        rules.RemoveAll(r => !seen.Add($"{r.Kind}|{r.TriggerOid:X}|{r.TriggerNodeId}|{r.EffectOid:X}|{r.EffectNodeId}"));
    }

    private static int NearestPop(ZoneSceneModel model, Vector3 p, float maxDist)
    {
        var best = -1;
        var bestD = maxDist;
        for (var i = 0; i < model.PopPoints.Count; ++i)
        {
            var d = (model.PopPoints[i].Position - p).Length();
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    // the run's flow: where players start, waves that spawn as a set dies, warps that open and where they lead, the exit
    private static void InferFlow(ZoneSceneTimelineFile file, ZoneSceneModel model, List<ZoneTimelineEvent> events, RunContext ctx)
    {
        var rules = file.Rules;
        // start: the party's position at duty commence (else the first recorded position), the entrance object next to it
        var first = events.Find(e => e.Kind == ZoneEventKind.Player && e.Commence) ?? events.Find(e => e.Kind == ZoneEventKind.Player);
        if (first != null)
        {
            var entrance = model.EventObjects.Where(eo => eo.Role == ZoneObjectRole.Entrance).MinBy(eo => ZoneSceneModel.DistXZ(eo.ActorPosition, first.Pos));
            var commence = ctx.Commence;
            var pop = NearestPop(model, first.Pos, 8f);
            rules.Add(new()
            {
                Kind = ZoneRuleKind.InstanceStart,
                T = first.T,
                Trigger = commence != null ? $"duty commenced (DIRU {commence.Upd:X8}) at {commence.T:f1}s" : "instance start",
                Effect = $"players spawn at ({first.X:f1}, {first.Z:f1}){(entrance != null ? $"; entrance barrier {entrance.Label} key 0x{entrance.InstanceKey:X} at ({entrance.ActorPosition.X:f1}, {entrance.ActorPosition.Z:f1})" : "")}{(pop >= 0 ? $"; pop point key 0x{model.PopPoints[pop].InstanceKey:X}" : "")}",
                Area = AreaName(model, first.Pos),
                EffectOid = entrance?.BaseId ?? 0,
                EffectNodeId = entrance != null ? ZoneSceneTimelineFile.FormatId(entrance.PathId) : pop >= 0 ? ZoneSceneTimelineFile.FormatId(model.PopPoints[pop].PathId) : "",
                Evidence = [$"{file.Replay}: first player at t={first.T:f1}s"],
            });
        }
        // exit gate: identified from the layout, opened by the last sealed boss; the object itself streams in whenever a player comes close
        var lastRelease = rules.Where(r => r.Kind == ZoneRuleKind.SealRelease && r.TriggerOid != 0).MaxBy(r => r.T);
        var bossDeath = lastRelease != null ? ctx.DeathEvents.Where(e => e.Oid == lastRelease.TriggerOid).MaxBy(e => e.T) : null;
        foreach (var exit in model.EventObjects.Where(eo => eo.Role == ZoneObjectRole.Exit))
        {
            var id = ZoneSceneTimelineFile.FormatId(exit.PathId);
            var seen = events.Find(e => e.Kind == ZoneEventKind.Create && e.NodeId == id);
            rules.Add(new()
            {
                Kind = ZoneRuleKind.ExitGate,
                T = bossDeath?.T ?? seen?.T ?? 0.0,
                Trigger = bossDeath != null ? $"death of the last boss 0x{bossDeath.Oid:X} '{bossDeath.Name}'" : "last boss death",
                Effect = $"exit {exit.Label} at ({exit.Position.X:f1}, {exit.Position.Z:f1}){(seen != null ? $" (streamed in at {seen.T:f1}s)" : " (not seen in this replay)")}",
                Area = AreaName(model, exit.ActorPosition),
                TriggerOid = bossDeath?.Oid ?? 0,
                EffectOid = exit.BaseId,
                EffectNodeId = id,
                Confidence = bossDeath != null ? 1f : 0.5f,
                Evidence = [$"{file.Replay}: {(seen != null ? $"exit created t={seen.T:f1}s, " : "")}{(bossDeath != null ? $"last boss death t={bossDeath.T:f1}s" : "no sealed boss")}"],
            });
        }
        // waves: enemies created together (within 1 s) that become targetable, within 10 s after deaths in the same area
        var creates = events.Where(e => e.Kind == ZoneEventKind.Create && e.Type == ActorType.Enemy && !e.Ally && e.Name.Length > 0).ToList();
        var deaths = ctx.DeathEvents;
        var i = 0;
        while (i < creates.Count)
        {
            var j = i;
            while (j + 1 < creates.Count && creates[j + 1].T - creates[i].T <= 1.0)
            {
                ++j;
            }
            var wave = creates.GetRange(i, j - i + 1).Where(c => ctx.Targetable.Contains(c.Actor)).ToList();
            i = j + 1;
            if (wave.Count == 0 || wave[0].T < 2.0)
            {
                continue;
            }
            var t = wave[0].T;
            var at = wave[0].Pos;
            var area = model.AreaAt(at);
            var before = deaths.Where(d => d.T <= t && d.T >= t - 10.0 && (area < 0 || model.AreaAt(d.Pos) == area) && ZoneSceneModel.DistXZ(d.Pos, at) <= 60f).MaxBy(d => d.T);
            if (before == null)
            {
                continue;
            }
            // how much of what was alive in the area had to die: deaths in the area so far vs the enemies that had spawned there before this wave
            var spawnedBefore = creates.Where(c => c.T < t && ctx.Targetable.Contains(c.Actor) && (area < 0 || model.AreaAt(c.Pos) == area) && ZoneSceneModel.DistXZ(c.Pos, at) <= 60f).Select(c => c.Actor).ToHashSet();
            var deadBefore = deaths.Count(d => d.T <= t && spawnedBefore.Contains(d.Actor));
            var markers = wave.Select(w => model.SpawnMarkerNear(w.Pos)).Where(m => m >= 0).Select(m => model.EventObjects[m].InstanceKey).Distinct().ToList();
            var areaName = area >= 0 ? model.Areas[area].Name : "?";
            // between a seal going up and coming down near the wave = adds of the boss fight, not a room wave
            var duringBoss = rules.Exists(r => r.Kind == ZoneRuleKind.SealOnPull && r.T <= t && !rules.Exists(x => x.Kind == ZoneRuleKind.SealRelease && x.EffectNodeId == r.EffectNodeId && x.T < t)
                && model.FindEventObject(r.EffectNodeId) is var seal && seal >= 0 && ZoneSceneModel.DistXZ(model.EventObjects[seal].ActorPosition, at) <= 60f);
            var waveRule = new ZoneMechanicRule
            {
                Kind = duringBoss ? ZoneRuleKind.BossAdds : ZoneRuleKind.WaveSpawn,
                T = t,
                Trigger = $"{deadBefore} of {spawnedBefore.Count} enemies in '{areaName}' dead (last: 0x{before.Oid:X} '{before.Name}')",
                Effect = $"wave of {wave.Count}: {Group(wave.Select(w => (w.Oid, w.Name)))} at {string.Join(" ", wave.Select(w => $"({w.X:f0}, {w.Z:f0})"))}{(markers.Count > 0 ? $" on spawn marker(s) key {string.Join(",", markers.Select(k => $"0x{k:X}"))}" : "")}",
                Area = area >= 0 ? areaName : "",
                TriggerOid = before.Oid,
                EffectOid = wave[0].Oid,
                DelaySeconds = (float)(t - before.T),
                Evidence = [$"{file.Replay}: deaths ..{before.T:f1}s, wave t={t:f1}s"],
            };
            if (!duringBoss)
            {
                KillSets(waveRule, model, file.Enemies, t - 0.01, at, 60f);
            }
            rules.Add(waveRule);
        }
        // warps: an object that goes EventState 7 -> 0 after deaths (opens), is interacted with (ESTA) and is followed by a teleport
        foreach (var open in events.Where(e => e.Kind == ZoneEventKind.EventState && e.State == 0 && !e.Initial && e.Eobj >= 0 && !model.EventObjects[e.Eobj].ControlsCollision && model.EventObjects[e.Eobj].Role is ZoneObjectRole.None or ZoneObjectRole.Warp))
        {
            var eo = model.EventObjects[open.Eobj];
            var wasHidden = events.Exists(e => e.Kind == ZoneEventKind.EventState && e.State == 7 && e.Actor == open.Actor && e.T < open.T);
            var tp = events.Find(e => e.Kind == ZoneEventKind.Teleport && e.T > open.T && e.T <= open.T + 120.0 && e.HasFrom && (new Vector2(e.FromX, e.FromZ) - new Vector2(eo.ActorPosition.X, eo.ActorPosition.Z)).Length() <= 40f);
            if (!wasHidden && tp == null)
            {
                continue;
            }
            file.RoleChanges.Add((open.NodeId, ZoneObjectRole.Warp));
            var lastDeath = deaths.Where(d => d.T <= open.T + 0.5 && d.T >= open.T - 10.0).MaxBy(d => d.T);
            var boss = lastDeath != null && rules.Exists(r => r.Kind == ZoneRuleKind.SealRelease && r.TriggerOid == lastDeath.Oid && Math.Abs(r.DelaySeconds) < 5f);
            var deadNearby = deaths.Count(d => d.T <= open.T + 0.5 && d.T >= open.T - 120.0 && ZoneSceneModel.DistXZ(d.Pos, eo.ActorPosition) <= 60f);
            var warpRule = new ZoneMechanicRule
            {
                Kind = ZoneRuleKind.WarpOpens,
                T = open.T,
                Trigger = lastDeath != null ? boss ? $"death of boss 0x{lastDeath.Oid:X} '{lastDeath.Name}'" : $"last of {deadNearby} enemies nearby dies (0x{lastDeath.Oid:X} '{lastDeath.Name}')" : "unknown",
                Effect = $"warp {eo.Label} key 0x{eo.InstanceKey:X} at ({eo.ActorPosition.X:f1}, {eo.ActorPosition.Z:f1}) appears (EventState 0)",
                Area = AreaName(model, eo.ActorPosition),
                TriggerOid = lastDeath?.Oid ?? 0,
                EffectOid = eo.BaseId,
                EffectNodeId = open.NodeId,
                EffectState = 0,
                DelaySeconds = lastDeath != null ? (float)(open.T - lastDeath.T) : 0f,
                Confidence = lastDeath != null ? 1f : 0.5f,
                Evidence = [$"{file.Replay}: EVTS 0 t={open.T:f1}s{(lastDeath != null ? $", last death t={lastDeath.T:f1}s" : "")}"],
            };
            if (!boss)
            {
                KillSets(warpRule, model, file.Enemies, open.T, eo.ActorPosition, 60f);
            }
            rules.Add(warpRule);
            if (tp != null)
            {
                var (landText, landNode, pop) = Landing(model, tp);
                Confirm(ctx, open.Eobj, pop, tp.Pos, file.Replay);
                var use = events.Find(e => e.Kind == ZoneEventKind.ObjectState && e.Actor == open.Actor && e.T > open.T && e.T <= tp.T);
                rules.Add(new()
                {
                    Kind = ZoneRuleKind.WarpTeleport,
                    T = tp.T,
                    Trigger = $"warp {eo.Label} used{(use != null ? $" (ESTA {use.State})" : "")}",
                    Effect = $"players land {landText}",
                    Area = AreaName(model, tp.Pos),
                    TriggerOid = eo.BaseId,
                    TriggerNodeId = open.NodeId,
                    TriggerState = use?.State ?? -1,
                    Channel = ZoneStateChannel.ObjectState,
                    EffectNodeId = landNode,
                    DelaySeconds = (float)(tp.T - (use?.T ?? open.T)),
                    Evidence = [$"{file.Replay}: {(use != null ? $"ESTA t={use.T:f1}s, " : "")}teleport t={tp.T:f1}s to ({tp.X:f1}, {tp.Z:f1})"],
                });
            }
        }
        // teleports not taken through a warp: the object standing at the origin is the source (the shortcut sends the party to the last
        // checkpoint); with no object there the landing is still recorded as a teleport with no source
        var explained = rules.Where(r => r.Kind == ZoneRuleKind.WarpTeleport).Select(r => r.T).ToList();
        var commenceT = ctx.Commence?.T;
        foreach (var tp in events.Where(e => e.Kind == ZoneEventKind.Teleport))
        {
            // before the duty commenced the party is being placed, not warped
            if (commenceT is { } c && tp.T < c || explained.Exists(t => Math.Abs(t - tp.T) < 0.01) || !tp.HasFrom)
            {
                continue;
            }
            // barriers, seals and doors stand where players wait, they are never the thing that moved them
            var origin = new Vector3(tp.FromX, 0f, tp.FromZ);
            var src = model.EventObjects.Where(eo => !eo.ControlsCollision && eo.Role is not (ZoneObjectRole.Vfx or ZoneObjectRole.Seal or ZoneObjectRole.Door or ZoneObjectRole.Entrance or ZoneObjectRole.Exit or ZoneObjectRole.SpawnMarker)).MinBy(eo => ZoneSceneModel.DistXZ(eo.ActorPosition, origin));
            if (src != null && ZoneSceneModel.DistXZ(src.ActorPosition, origin) > 4f)
            {
                src = null;
            }
            var (landText, landNode, pop) = Landing(model, tp);
            if (landNode.Length == 0)
            {
                continue;
            }
            var kind = src?.Role == ZoneObjectRole.Shortcut ? ZoneRuleKind.ShortcutTeleport : ZoneRuleKind.WarpTeleport;
            if (src != null && kind == ZoneRuleKind.WarpTeleport)
            {
                file.RoleChanges.Add((ZoneSceneTimelineFile.FormatId(src.PathId), ZoneObjectRole.Warp));
                Confirm(ctx, src.Index, pop, tp.Pos, file.Replay);
            }
            var before = src == null ? events.FindLast(x => x.Kind is ZoneEventKind.Death or ZoneEventKind.DirectorUpdate && x.T <= tp.T && x.T >= tp.T - 10.0) : null;
            rules.Add(new()
            {
                Kind = kind,
                T = tp.T,
                Trigger = src != null ? $"{(src.Role == ZoneObjectRole.Shortcut ? "shortcut" : "warp")} {src.Label} used" : before != null ? (before.Kind == ZoneEventKind.Death ? $"death of 0x{before.Oid:X} '{before.Name}'" : $"DIRU {before.Upd:X8}") : "unknown (no object at the origin)",
                Effect = $"players land {landText}",
                Area = AreaName(model, tp.Pos),
                TriggerOid = src?.BaseId ?? before?.Oid ?? 0,
                TriggerNodeId = src != null ? ZoneSceneTimelineFile.FormatId(src.PathId) : "",
                Channel = ZoneStateChannel.ObjectState,
                EffectNodeId = landNode,
                Confidence = src != null ? 0.8f : 0.4f,
                Evidence = [$"{file.Replay}: teleport t={tp.T:f1}s from ({tp.FromX:f1}, {tp.FromZ:f1}) to ({tp.X:f1}, {tp.Z:f1})"],
            });
            explained.Add(tp.T);
        }
        // each (kind, source, destination) once, earliest kept
        var seenLinks = new HashSet<string>();
        rules.RemoveAll(r => r.Kind is ZoneRuleKind.WarpTeleport or ZoneRuleKind.ShortcutTeleport or ZoneRuleKind.WarpOpens && !seenLinks.Add($"{r.Kind}|{r.TriggerNodeId}|{r.EffectNodeId}"));
    }

    // where a teleport landed: a pop point, else the entrance object (the return warp lands there), else the raw position
    private static (string text, string nodeId, int pop) Landing(ZoneSceneModel model, ZoneTimelineEvent tp)
    {
        var pop = NearestPop(model, tp.Pos, 5f);
        if (pop >= 0)
        {
            var p = model.PopPoints[pop];
            return ($"on pop point key 0x{p.InstanceKey:X} at ({p.Position.X:f1}, {p.Position.Z:f1}){(p.AreaIndex >= 0 ? $" '{model.Areas[p.AreaIndex].Name}'" : "")}", ZoneSceneTimelineFile.FormatId(p.PathId), pop);
        }
        var entrance = model.EventObjects.Find(eo => eo.Role == ZoneObjectRole.Entrance && ZoneSceneModel.DistXZ(eo.ActorPosition, tp.Pos) <= 5f);
        if (entrance != null)
        {
            return ($"at the entrance {entrance.Label} ({entrance.ActorPosition.X:f1}, {entrance.ActorPosition.Z:f1})", ZoneSceneTimelineFile.FormatId(entrance.PathId), -1);
        }
        return ($"at ({tp.X:f1}, {tp.Z:f1})", "", -1);
    }

    // which enemies around a point had died by time t (required for whatever fired) and which were still alive (not required)
    private static void KillSets(ZoneMechanicRule rule, ZoneSceneModel model, List<ZoneEnemyDto> enemies, double t, Vector3 at, float radius)
    {
        var area = model.AreaAt(at);
        var nearby = enemies.Where(e => !e.Ally && e.Spawn <= t && ZoneSceneModel.DistXZ(e.Pos, at) <= radius && (area < 0 || e.Area == area)).ToList();
        var required = nearby.Where(e => e.Death >= 0 && e.Death <= t + 0.5 && e.Spawn >= t - 300.0).ToList();
        var alive = nearby.Where(e => e.Targetable && (e.Death < 0 || e.Death > t + 0.5)).ToList();
        rule.RequiredKills = Groups(required.Select(x => (x.Oid, x.Name)));
        rule.NotRequiredKills = Groups(alive.Select(x => (x.Oid, x.Name)));
        rule.Trigger += ZoneMechanicRule.StillAliveNote(rule.NotRequiredKills);
    }

    // the run in order, one line per step, from the inferred rules
    private static void BuildScenario(ZoneSceneTimelineFile file, ZoneSceneModel model, List<ZoneTimelineEvent> events)
    {
        var steps = file.Scenario;
        foreach (var r in file.Rules)
        {
            var t = r.T;
            switch (r.Kind)
            {
                case ZoneRuleKind.InstanceStart:
                    {
                        var p = events.Find(e => e.Kind == ZoneEventKind.Player && e.Commence) ?? events.Find(e => e.Kind == ZoneEventKind.Player);
                        steps.Add(new() { T = t, Kind = ZoneStepKind.Start, Text = r.Effect, NodeId = r.EffectNodeId, Oid = r.EffectOid, X = p?.X ?? 0f, Z = p?.Z ?? 0f, Area = p != null ? AreaName(model, p.Pos) : "" });
                    }
                    break;
                case ZoneRuleKind.WaveSpawn:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Wave, Text = $"{r.Trigger} -> {r.Effect}", Oid = r.EffectOid });
                    break;
                case ZoneRuleKind.BossAdds:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Adds, Text = $"{r.Trigger} -> {r.Effect}", Oid = r.EffectOid });
                    break;
                case ZoneRuleKind.ShortcutTeleport:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Shortcut, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.TriggerNodeId, TargetNodeId = r.EffectNodeId, Oid = r.TriggerOid });
                    break;
                case ZoneRuleKind.SetClearSpawns:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Clear, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.EffectNodeId, Oid = r.EffectOid });
                    break;
                case ZoneRuleKind.WarpOpens:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.WarpOpen, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.EffectNodeId, Oid = r.EffectOid });
                    break;
                case ZoneRuleKind.WarpTeleport:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Warp, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.TriggerNodeId, TargetNodeId = r.EffectNodeId, Oid = r.TriggerOid });
                    break;
                case ZoneRuleKind.SealOnPull:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Seal, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.EffectNodeId, Oid = r.TriggerOid });
                    break;
                case ZoneRuleKind.SealRelease:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Boss, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.EffectNodeId, Oid = r.TriggerOid });
                    break;
                case ZoneRuleKind.KeyOpensDoor:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Door, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.EffectNodeId, Oid = r.EffectOid });
                    break;
                case ZoneRuleKind.ExitGate:
                    steps.Add(new() { T = r.TriggerOid != 0 ? t : double.MaxValue, Kind = ZoneStepKind.Exit, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.EffectNodeId, Oid = r.EffectOid });
                    break;
                case ZoneRuleKind.FloorChange when r.Channel == ZoneStateChannel.EAnim:
                    steps.Add(new() { T = t, Kind = ZoneStepKind.Floor, Text = $"{r.Trigger} -> {r.Effect}", NodeId = r.EffectNodeId, Oid = r.TriggerOid });
                    break;
            }
        }
        steps.Sort((a, b) => a.T.CompareTo(b.T));
        foreach (var st in steps)
        {
            if (st.Area.Length == 0 && st.NodeId.Length > 0)
            {
                var i = model.FindEventObject(st.NodeId);
                if (i >= 0)
                {
                    var eo = model.EventObjects[i];
                    st.X = eo.ActorPosition.X;
                    st.Z = eo.ActorPosition.Z;
                    st.Area = AreaName(model, eo.ActorPosition);
                }
            }
        }
    }
}
