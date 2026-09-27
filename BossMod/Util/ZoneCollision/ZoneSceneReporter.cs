namespace BossMod;

// one-line text for the scene model, timeline and rules, shared by the editor's lists / tooltips and the harness reports
public static class ZoneSceneReporter
{
    public static string RoleName(ZoneObjectRole role) => role switch
    {
        ZoneObjectRole.Entrance => "entrance",
        ZoneObjectRole.Shortcut => "shortcut",
        ZoneObjectRole.Exit => "exit",
        ZoneObjectRole.Seal => "seal",
        ZoneObjectRole.Door => "door",
        ZoneObjectRole.SpawnMarker => "spawn marker",
        ZoneObjectRole.Warp => "warp",
        ZoneObjectRole.Vfx => "vfx",
        ZoneObjectRole.Pickup => "pickup",
        ZoneObjectRole.Platform => "platform",
        _ => "",
    };

    public static string EventObject(ZoneCollisionScene scene, ZoneEventObject eo)
    {
        var bound = eo.BoundNode >= 0 ? scene.Nodes[eo.BoundNode].Path : "-";
        var role = RoleName(eo.Role);
        return $"[{eo.Index}] {eo.Label} key 0x{eo.InstanceKey:X} pathId 0x{eo.PathId:X16} at ({eo.Position.X:f1},{eo.Position.Z:f1}) actor ({eo.ActorPosition.X:f1},{eo.ActorPosition.Z:f1}) data 0x{eo.Sheet.Data:X} popType {eo.Sheet.PopType} eye={eo.Sheet.EyeCollision} sgb '{System.IO.Path.GetFileName(eo.Sheet.SgbPath)}' director={eo.Sheet.DirectorControl} target={eo.Sheet.Target} bound {bound} meshes {eo.ControlledMeshes.Length} boxes {eo.ControlledBoxes.Length} seals [{string.Join(",", eo.SealBoxes)}]{(eo.IsSealController ? " SEAL" : "")}{(eo.IsDoor ? " DOOR" : "")}{(eo.IsBarrierVfx ? " VFX" : "")}{(role.Length > 0 ? $" role={role}" : "")}";
    }

    public static string FloorPiece(ZoneSceneModel model, ZoneFloorPiece p) => $"[{p.Index}] {p.Kind} {p.Label} at ({p.Position.X:f1}, {p.Position.Z:f1}) bounds ({p.Bounds.Min.X:f0},{p.Bounds.Min.Z:f0})..({p.Bounds.Max.X:f0},{p.Bounds.Max.Z:f0}) group '{model.FloorGroups[p.Group].Name}'{(p.Boxes.Length > 0 ? $" boxes [{string.Join(",", p.Boxes)}]" : "")}";

    public static string FloorGroup(ZoneSceneModel model, ZoneFloorGroup g) => $"floor group '{g.Name}' ({g.Family}: {(g.Family == ZoneFloorFamily.EAnim ? "OnActorEAnim on the platform objects" : "OnMapEffect, index from a replay")}) {g.Pieces.Count} piece(s): {g.Pieces.Count(p => model.FloorPieces[p].Kind == ZoneFloorPieceKind.Platform)} platform(s), {g.Pieces.Count(p => model.FloorPieces[p].Kind == ZoneFloorPieceKind.Section)} section(s)";

    public static string Area(ZoneMapArea a) => $"area '{a.Name}' map {a.Map} bounds ({a.WorldBounds.Min.X:f0},{a.WorldBounds.Min.Z:f0})..({a.WorldBounds.Max.X:f0},{a.WorldBounds.Max.Z:f0})";

    public static string Door(ZoneCollisionScene scene, ZoneSceneModel model, ZoneDoor d) => $"door sgb {scene.Nodes[d.SgbNode].Path} boxes [{string.Join(",", d.Boxes)}] controller {(d.ControllerEObj >= 0 ? model.EventObjects[d.ControllerEObj].Label : "none")}";

    public static string Link(ZoneSceneModel model, ZoneFlowLink l)
    {
        var eo = model.EventObjects[l.EObj];
        return $"link: {eo.Label} key 0x{eo.InstanceKey:X} -> {(l.Pop >= 0 ? $"pop key 0x{model.PopPoints[l.Pop].InstanceKey:X}" : "?")} at ({l.Landing.X:f1}, {l.Landing.Z:f1}) conf {l.Confidence:f1} ({l.Source})";
    }

    public static string Rule(ZoneMechanicRule r) => $"{r.Kind,-15} conf {r.Confidence:f2} delay {r.DelaySeconds:f1}s: {r.Trigger} -> {r.Effect}";

    public static string Rule(SavedRule r) => $"{r.Kind,-15} {r.TriggerText} -> {r.EffectText}{(r.Confidence < 1f ? $" ({r.Confidence:P0})" : "")}{(r.DelaySeconds != 0f ? $" +{r.DelaySeconds:f1}s" : "")}";

    public static string KillSets(ZoneMechanicRule r) => $"{(r.RequiredKills.Count > 0 ? $"killed before: {string.Join(", ", r.RequiredKills)}" : "")}{(r.RequiredKills.Count > 0 && r.NotRequiredKills.Count > 0 ? "; " : "")}{(r.NotRequiredKills.Count > 0 ? $"not required (alive): {string.Join(", ", r.NotRequiredKills)}" : "")}";

    // replay steps carry a time, layout-order steps an index
    public static string Scenario(ZoneScenarioStep st, bool fromReplay, int index) => fromReplay
        ? $"{(st.T == double.MaxValue ? "end" : $"{st.T:f0}s")} {st.Kind}{(st.Area.Length > 0 ? $" [{st.Area}]" : "")}"
        : $"{index}. {st.Kind}{(st.Area.Length > 0 ? $" [{st.Area}]" : "")}";

    public static string TimelineEvent(ZoneTimelineEvent e)
    {
        var where = e.NodeId.Length > 0 ? $"  [{(e.Seals.Length > 0 ? $"seal {string.Join(",", e.Seals)} " : "")}{(e.Boxes.Length > 0 ? $"boxes {string.Join(",", e.Boxes)} " : "")}{e.NodePath}]" : "";
        var line = e.Kind switch
        {
            ZoneEventKind.DirectorUpdate => $"DIRU    dir {e.Dir:X8} upd {e.Upd:X8} p=({e.P1:X},{e.P2:X},{e.P3:X},{e.P4:X})",
            ZoneEventKind.MapEffect => $"MAPFX   index {e.State:X2} state {e.P1:X8}",
            ZoneEventKind.Teleport => $"TELEPORT player to ({e.X:f1}, {e.Y:f1}, {e.Z:f1}) {e.Note}",
            ZoneEventKind.Player => $"PLAYER  '{e.Name}' at ({e.X:f1}, {e.Z:f1}){(e.Note.Length > 0 ? $" ({e.Note})" : "")}",
            ZoneEventKind.EAnim => $"EANM    {e.Oid:X} '{e.Name}' p1={e.P1:X4} p2={e.P2:X4}{where}",
            ZoneEventKind.EventState or ZoneEventKind.ObjectState => $"{e.Kind.Tag(),-7} {e.Oid:X} '{e.Name}' state={e.State}{(e.Initial ? " (initial)" : "")}{where}",
            _ => $"{e.Kind.Tag(),-7} {e.Oid:X} '{e.Name}' {e.Note} at ({e.X:f1}, {e.Z:f1}){(e.Kind == ZoneEventKind.Create ? $" layout 0x{e.LayoutId:X} targetable={e.State}" : "")}{where}",
        };
        return $"{e.T,8:f1}s {line}";
    }

    public static string Boss(ZoneBossTrack b) => $"boss 0x{b.Oid:X} '{b.Name}' (name id {b.NameId}) pull {b.Pull:f1}s death {(b.Death >= 0 ? $"{b.Death:f1}s" : "-")} hitbox {b.Hitbox:f1} samples {b.Samples.Length / 4}";

    // enemies by area, in spawn groups (spawns within 1 s); the class of a group is its majority class
    public static IEnumerable<string> EnemyGroups(ZoneSceneTimelineFile file, ZoneSceneModel model)
    {
        foreach (var area in file.Enemies.Where(e => !e.Ally).GroupBy(e => e.Area).OrderBy(g => g.Min(e => e.Spawn)))
        {
            var areaName = area.Key >= 0 && area.Key < model.Areas.Count ? model.Areas[area.Key].Name : "no area";
            var ordered = area.OrderBy(e => e.Spawn).ToList();
            var gi = 0;
            while (gi < ordered.Count)
            {
                var gj = gi;
                while (gj + 1 < ordered.Count && ordered[gj + 1].Spawn - ordered[gi].Spawn <= 1.0)
                {
                    ++gj;
                }
                var g = ordered.GetRange(gi, gj - gi + 1);
                var deaths = g.Where(e => e.Death >= 0).Select(e => e.Death).ToList();
                var cls = g.GroupBy(e => e.Class).OrderByDescending(x => x.Count()).First().Key;
                yield return $"[{areaName}] {cls,-8} spawn {g[0].Spawn:f1}s x{g.Count}{(deaths.Count > 0 ? $" deaths {deaths.Min():f1}..{deaths.Max():f1}s" : "")}{(deaths.Count < g.Count ? $" ({g.Count - deaths.Count} not killed)" : "")}: {ZoneSceneTimelineBuilder.Group(g.Select(e => (e.Oid, e.Name)))}";
                gi = gj + 1;
            }
        }
    }
}
