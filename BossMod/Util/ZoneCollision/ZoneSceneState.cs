namespace BossMod;

// what a scene changes relative to the layout defaults: layers switched off, nodes forced on/off, event object states
public sealed class ZoneSceneState
{
    public readonly HashSet<ulong> DisabledLayers = [];               // layer PathIds
    public readonly Dictionary<ulong, bool> NodeOverrides = [];       // node PathId -> forced active/inactive (applies to the subtree)
    public readonly Dictionary<ulong, ushort> EObjStates = [];        // EventObject node PathId -> EventState (EVTS byte, the collision channel); absent = 0
    public readonly Dictionary<ulong, ushort> EObjObjectStates = [];  // ESTA channel (informational unless a rule maps it)

    public bool IsEmpty => DisabledLayers.Count == 0 && NodeOverrides.Count == 0 && EObjStates.Count == 0 && EObjObjectStates.Count == 0;

    public ZoneSceneState Clone()
    {
        var c = new ZoneSceneState();
        c.DisabledLayers.UnionWith(DisabledLayers);
        foreach (var kv in NodeOverrides)
        {
            c.NodeOverrides[kv.Key] = kv.Value;
        }
        foreach (var kv in EObjStates)
        {
            c.EObjStates[kv.Key] = kv.Value;
        }
        foreach (var kv in EObjObjectStates)
        {
            c.EObjObjectStates[kv.Key] = kv.Value;
        }
        return c;
    }

    public void CopyFrom(ZoneSceneState other)
    {
        DisabledLayers.Clear();
        NodeOverrides.Clear();
        EObjStates.Clear();
        EObjObjectStates.Clear();
        DisabledLayers.UnionWith(other.DisabledLayers);
        foreach (var kv in other.NodeOverrides)
        {
            NodeOverrides[kv.Key] = kv.Value;
        }
        foreach (var kv in other.EObjStates)
        {
            EObjStates[kv.Key] = kv.Value;
        }
        foreach (var kv in other.EObjObjectStates)
        {
            EObjObjectStates[kv.Key] = kv.Value;
        }
    }

    // order-independent hash of every entry
    public long Fingerprint()
    {
        var h = ZoneBinary.Fnv1aOffset;
        ulong acc = 0;
        foreach (var l in DisabledLayers)
        {
            acc ^= ZoneBinary.Fnv1a64(h, l);
        }
        foreach (var kv in NodeOverrides)
        {
            acc ^= ZoneBinary.Fnv1a64(ZoneBinary.Fnv1a64(h, kv.Key), kv.Value ? 1ul : 2ul);
        }
        foreach (var kv in EObjStates)
        {
            acc ^= ZoneBinary.Fnv1a64(ZoneBinary.Fnv1a64(h, kv.Key), 0x100ul | kv.Value);
        }
        foreach (var kv in EObjObjectStates)
        {
            acc ^= ZoneBinary.Fnv1a64(ZoneBinary.Fnv1a64(h, kv.Key), 0x10000ul | kv.Value);
        }
        return (long)acc;
    }
}

// user override of the state -> collision mapping for one event object (default: EventState 7 = collision off, anything else on)
public readonly record struct ZoneEObjRule(ulong EObjPathId, bool ObjectStateChannel, ushort State, bool CollisionOn);

public enum ZoneSceneSource : byte { Layout, Manual, Replay, Derived }

public sealed class ZoneScene
{
    public string Name = "";
    public ZoneSceneState State = new();
    public ZoneSceneSource Source;
    public string Notes = "";
    public string ReplayFile = "";
    public double ReplayTime;

    public ZoneScene Clone(string name) => new() { Name = name, State = State.Clone(), Source = Source, Notes = Notes, ReplayFile = ReplayFile, ReplayTime = ReplayTime };
}

// the resolved activity of the loaded scene under the current state; arrays are replaced wholesale so a running job keeps a consistent view
public sealed class ZoneActivity
{
    public bool[] NodeActive = [];  // collision present
    public bool[] NodePlaced = [];  // layer on and not overridden off (ignores event object state): the instance exists, its collision may be toggled
    public bool[] MeshActive = [];
    public bool[] BoxActive = [];
    public long Fingerprint;
    public bool Dirty = true;
}

public static class ZoneSceneResolver
{
    public static bool DefaultCollisionOn(ushort eventState) => eventState != 7;

    public static void Resolve(ZoneCollisionScene scene, ZoneSceneState state, ZoneSceneModel model, IReadOnlyList<ZoneEObjRule> rules)
    {
        var layers = scene.Layers;
        for (var i = 0; i < layers.Count; ++i)
        {
            layers[i].Enabled = !state.DisabledLayers.Contains(layers[i].PathId);
        }
        var nodes = scene.Nodes;
        var n = nodes.Count;
        var active = new bool[n];
        var placed = new bool[n];
        for (var i = 0; i < n; ++i)
        {
            var node = nodes[i];
            var layerOn = layers[node.LayerIndex].Enabled;
            var parentActive = node.Parent < 0 || active[node.Parent];
            var parentPlaced = node.Parent < 0 || placed[node.Parent];
            var hasOverride = state.NodeOverrides.TryGetValue(node.PathId, out var ov);
            placed[i] = layerOn && parentPlaced && (!hasOverride || ov);
            bool own;
            if (hasOverride)
            {
                own = ov;
            }
            else
            {
                var controller = i < model.ControllerOfNode.Length ? model.ControllerOfNode[i] : -1;
                own = controller >= 0 && !(node.Source.Group?.IsCollisionControllableWithoutEObj ?? false) ? CollisionOn(model.EventObjects[controller], state, rules) : DefaultOwn(node);
            }
            active[i] = layerOn && parentActive && own;
        }
        var meshes = scene.Meshes;
        var meshActive = new bool[meshes.Count];
        for (var m = 0; m < meshes.Count; ++m)
        {
            var ni = meshes[m].NodeIndex;
            meshActive[m] = ni >= 0 ? active[ni] : layers[meshes[m].LayerIndex].Enabled;
        }
        var boxes = scene.Boxes;
        var boxActive = new bool[boxes.Count];
        for (var b = 0; b < boxes.Count; ++b)
        {
            var ni = boxes[b].NodeIndex;
            boxActive[b] = ni >= 0 ? active[ni] : layers[boxes[b].LayerIndex].Enabled;
        }
        var fp = state.Fingerprint();
        var lh = 17;
        for (var i = 0; i < layers.Count; ++i)
        {
            lh = lh * 31 + (layers[i].Enabled ? 1 : 0);
        }
        scene.Activity = new ZoneActivity { NodeActive = active, NodePlaced = placed, MeshActive = meshActive, BoxActive = boxActive, Fingerprint = fp ^ ((long)lh << 32) ^ meshes.Count, Dirty = false };
    }

    private static bool DefaultOwn(ZoneNode node) => (LgbInstanceType)node.Type switch
    {
        LgbInstanceType.CollisionBox => node.Source.ActiveByDefault,
        LgbInstanceType.SharedGroup => node.Source.Group is not { InitialDoorState: LgbDoorState.Open },
        _ => true,
    };

    public static bool CollisionOn(ZoneEventObject eobj, ZoneSceneState state, IReadOnlyList<ZoneEObjRule> rules)
    {
        var hasEvts = state.EObjStates.TryGetValue(eobj.PathId, out var evts);
        var hasEsta = state.EObjObjectStates.TryGetValue(eobj.PathId, out var esta);
        for (var i = 0; i < rules.Count; ++i)
        {
            var r = rules[i];
            if (r.EObjPathId != eobj.PathId)
            {
                continue;
            }
            if (r.ObjectStateChannel ? hasEsta && r.State == esta : r.State == (hasEvts ? evts : 0))
            {
                return r.CollisionOn;
            }
        }
        return DefaultCollisionOn(hasEvts ? evts : (ushort)0);
    }
}
