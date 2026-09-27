using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BossMod;

// layout objects on the canvas and in the sidebar: event objects (with what they control), pop points, exit links, trigger volumes, the
// replay's enemies and its event list
public sealed partial class ZoneArenaEditorWindow
{
    private enum ObjectKind : byte { None, EObj, Pop, Exit, Trigger, Enemy }

    private readonly record struct ObjectRef(ObjectKind Kind, int Index)
    {
        public static readonly ObjectRef None = new(ObjectKind.None, -1);
        public bool Valid => Kind != ObjectKind.None;
    }

    private ObjectRef _hoverObject = ObjectRef.None;
    private ObjectRef _selectedObject = ObjectRef.None;
    private ObjectRef _popupObject = ObjectRef.None;
    private string _objectFilter = "";
    private bool _objectsNearCentreOnly = true;
    private bool _showSealsOffInScene;
    private int _stateEdit;
    private bool _openObjectPopup; // set inside a PushId scope, consumed where the popup is drawn (same id scope as OpenPopup)
    private bool _eventsControllersOnly = true; // the events table: only state changes, director updates and teleports
    private bool _filterIsHex;  // the object filter parsed as a hex id once per frame, so rows compare numbers instead of formatting theirs
    private ulong _filterHex;
    private readonly Dictionary<int, ZoneMapArea> _areaByMarker = []; // map range marker -> area, per model
    private ZoneSceneModel? _areaByMarkerModel;
    private readonly List<(int area, List<int> enemies)> _enemyGroups = []; // hostile replay enemies by area in spawn order, per timeline
    private ZoneSceneTimelineFile? _enemyGroupsFor;
    private string[] _scenarioLabels = [];
    private object? _scenarioLabelsFor;
    private string _objectsSummary = "";
    private ZoneSceneModel? _objectsSummaryModel;
    private ZoneSceneTimelineFile? _objectsSummaryTimeline;

    private const string ObjectPopupId = "##objpopup";

    private Vector3 ObjectPosition(ObjectRef r)
    {
        var model = _session!.Model;
        var scene = _scene!;
        return r.Kind switch
        {
            ObjectKind.EObj => model.EventObjects[r.Index].ActorPosition,
            ObjectKind.Pop => model.PopPoints[r.Index].Position,
            ObjectKind.Exit => model.Exits[r.Index].Position,
            ObjectKind.Trigger => scene.Markers[model.Triggers[r.Index]].WorldBounds.Center,
            ObjectKind.Enemy when Enemy(r) is { } en => new(en.X, en.Y, en.Z),
            _ => default,
        };
    }

    private bool ObjectNearCentre(ObjectRef r)
    {
        if (!_objectsNearCentreOnly)
        {
            return true;
        }
        var p = ObjectPosition(r);
        var c = _session!.Centre;
        return (new Vector2(p.X, p.Z) - new Vector2(c.X, c.Z)).Length() <= _session.Settings.MaxRadius + 20f;
    }

    // the first object matching an OID / instance key (hex, with or without 0x) or a name fragment
    private void JumpToObject(string query)
    {
        var model = _session!.Model;
        var hex = query.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? query[2..] : query;
        var isHex = uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var id);
        var eo = model.EventObjects.FindIndex(e => isHex && (e.BaseId == id || e.InstanceKey == id) || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (eo >= 0)
        {
            GoToObject(new(ObjectKind.EObj, eo));
            return;
        }
        var pop = model.PopPoints.FindIndex(p => isHex && p.InstanceKey == id);
        if (pop >= 0)
        {
            GoToObject(new(ObjectKind.Pop, pop));
        }
    }

    private void GoToObject(ObjectRef r)
    {
        CentreCanvasOn(ObjectPosition(r));
        _selectedObject = r;
    }

    // centre on a point, close enough to read the objects there
    private void CentreCanvasOn(Vector3 p)
    {
        _canvas.Center = new(p.X, p.Z);
        if (_canvas.Zoom < 6f)
        {
            _canvas.Zoom = 8f;
        }
    }

    // the replay enemy an object ref points at, null without a timeline or past its end
    private ZoneEnemyDto? Enemy(ObjectRef r) => r.Kind == ObjectKind.Enemy && _timeline != null && r.Index >= 0 && r.Index < _timeline.Enemies.Count ? _timeline.Enemies[r.Index] : null;

    private string AreaName(int area, string fallback = "?")
    {
        var areas = _session!.Model.Areas;
        return area >= 0 && area < areas.Count ? areas[area].Name : fallback;
    }

    private Dictionary<int, ZoneMapArea> AreaByMarker()
    {
        var model = _session!.Model;
        if (_areaByMarkerModel != model)
        {
            _areaByMarker.Clear();
            foreach (var a in model.Areas)
            {
                _areaByMarker[a.MarkerIndex] = a;
            }
            _areaByMarkerModel = model;
        }
        return _areaByMarker;
    }

    // the enemies that spawned with en (same area, within a second, hostile), the earliest death first
    private List<ZoneEnemyDto> SpawnGroup(ZoneEnemyDto en) => [.. _timeline!.Enemies.Where(x => !x.Ally && x.Area == en.Area && Math.Abs(x.Spawn - en.Spawn) <= 1.0).OrderBy(x => x.Death < 0 ? double.MaxValue : x.Death)];

    private static string SpawnGroupText(IEnumerable<ZoneEnemyDto> group) => string.Join(", ", group.GroupBy(x => (x.Oid, x.Name)).Select(g => $"0x{g.Key.Oid:X} {g.Key.Name}{(g.Count() > 1 ? $" x{g.Count()}" : "")}"));

    // one row of an object list: click centres the canvas on it, hovering highlights it on the canvas and shows its description
    private void ObjectRow(string label, ObjectRef r, ImGuiSelectableFlags flags = ImGuiSelectableFlags.None)
    {
        if (ImGui.Selectable(label, _selectedObject == r, flags))
        {
            GoToObject(r);
        }
        if (ImGui.IsItemHovered())
        {
            _hoverObject = r;
            ImGui.SetTooltip(ObjectDescription(r));
        }
    }

    private string ObjectLabel(ObjectRef r)
    {
        var model = _session!.Model;
        var scene = _scene!;
        switch (r.Kind)
        {
            case ObjectKind.EObj:
                return $"EObj {model.EventObjects[r.Index].Label}";
            case ObjectKind.Pop:
                {
                    var p = model.PopPoints[r.Index];
                    return $"pop {p.PopType} key 0x{p.InstanceKey:X}";
                }
            case ObjectKind.Exit:
                return $"exit {model.Exits[r.Index].ExitIndex}";
            case ObjectKind.Trigger:
                {
                    var m = scene.Markers[model.Triggers[r.Index]];
                    return $"{m.TypeName}{(m.Name.Length > 0 ? $" '{m.Name}'" : "")}";
                }
            case ObjectKind.Enemy:
                return Enemy(r) is { } en ? $"enemy 0x{en.Oid:X} '{en.Name}'" : "enemy";
            default:
                return "";
        }
    }

    private string ObjectDescription(ObjectRef r)
    {
        var session = _session!;
        var model = session.Model;
        var scene = _scene!;
        var sb = new StringBuilder();
        switch (r.Kind)
        {
            case ObjectKind.EObj:
                {
                    var eo = model.EventObjects[r.Index];
                    sb.Append($"event object {eo.Label} (EObj row = actor OID){(eo.Role != ZoneObjectRole.None ? $" - {RoleName(eo.Role)}" : "")}").Append('\n');
                    sb.Append($"instance key 0x{eo.InstanceKey:X} (= actor LayoutID), node {ZoneSceneTimelineFile.FormatId(eo.PathId)}").Append('\n');
                    sb.Append($"layer '{scene.Layers[scene.Nodes[eo.NodeIndex].LayerIndex].Name}', pop type {eo.Sheet.PopType}{(eo.Sheet.SgbPath.Length > 0 ? $", sgb {System.IO.Path.GetFileName(eo.Sheet.SgbPath)}" : "")}{(eo.Sheet.DirectorControl ? ", director controlled" : "")}").Append('\n');
                    if (eo.BoundNode >= 0)
                    {
                        var bn = scene.Nodes[eo.BoundNode];
                        sb.Append($"bound -> {bn.Path}{(bn.Source.SharedGroupPath is { Length: > 0 } sp ? $" ({System.IO.Path.GetFileName(sp)})" : "")}").Append('\n');
                        sb.Append($"controls {eo.ControlledMeshes.Length} mesh(es), {eo.ControlledBoxes.Length} box(es){(eo.SealBoxes.Length > 0 ? $", seal box(es) [{string.Join(", ", eo.SealBoxes)}]" : "")}{(eo.IsDoor ? " (door)" : "")}{(eo.IsSealController ? " (seal)" : "")}").Append('\n');
                        var idx = session.Seals.FindIndex(s => Array.IndexOf(eo.SealBoxes, s.BoxIndex) >= 0);
                        if (idx >= 0)
                        {
                            sb.Append($"seal {idx} in the seals table").Append('\n');
                        }
                    }
                    else
                    {
                        sb.Append(eo.IsBarrierVfx ? "no bound instance (vfx only)" : "no bound instance").Append('\n');
                    }
                    if (eo.LinkedNode >= 0)
                    {
                        sb.Append($"linked -> {scene.Nodes[eo.LinkedNode].Path}").Append('\n');
                    }
                    var evts = session.EObjState(r.Index);
                    var esta = session.EObjState(r.Index, true);
                    sb.Append($"scene state: EventState {(evts is { } v ? v.ToString() : "default (0)")}{(esta is { } o ? $", object state {o}" : "")}; collision {(eo.BoundNode >= 0 ? scene.IsNodeActive(eo.BoundNode) ? "on" : "off" : "n/a")}").Append('\n');
                    if (_timeline != null)
                    {
                        var id = ZoneSceneTimelineFile.FormatId(eo.PathId);
                        var seen = _timeline.Events.Where(e => e.NodeId == id && e.Kind is ZoneEventKind.EventState or ZoneEventKind.ObjectState).Select(e => $"{e.Kind.Tag()} {e.State} @{e.T:f1}s").ToList();
                        if (seen.Count > 0)
                        {
                            sb.Append($"replay: {string.Join(", ", seen.Take(8))}{(seen.Count > 8 ? " ..." : "")}").Append('\n');
                        }
                    }
                    sb.Append($"at ({eo.Position.X:f1}, {eo.Position.Y:f1}, {eo.Position.Z:f1}){(eo.BoundNode >= 0 ? $", actor at ({eo.ActorPosition.X:f1}, {eo.ActorPosition.Z:f1})" : "")}").Append('\n');
                    sb.Append("left click selects, right click for pre/post scenes and rules");
                }
                break;
            case ObjectKind.Pop:
                {
                    var p = model.PopPoints[r.Index];
                    sb.Append($"pop range key 0x{p.InstanceKey:X}: type {p.PopType}, slot {p.SlotIndex}, {p.RelativePositions.Length} relative position(s)").Append('\n');
                    sb.Append($"layer '{scene.Layers[scene.Nodes[p.NodeIndex].LayerIndex].Name}'{(p.AreaIndex >= 0 ? $", area '{model.Areas[p.AreaIndex].Name}'" : "")}").Append('\n');
                    sb.Append($"at ({p.Position.X:f1}, {p.Position.Y:f1}, {p.Position.Z:f1})").Append('\n');
                    sb.Append(p.PopType == LgbPopType.Pc ? "player spawn / return point (dungeon teleports land here)" : "npc spawn anchor");
                }
                break;
            case ObjectKind.Exit:
                {
                    var e = model.Exits[r.Index];
                    sb.Append($"exit range {e.ExitIndex} type {e.ExitType} -> territory {e.TerritoryType} zone {e.ZoneId}").Append('\n');
                    sb.Append($"dest {(e.DestPopNode >= 0 ? scene.Nodes[e.DestPopNode].Path : "other zone")}, return {(e.ReturnPopNode >= 0 ? scene.Nodes[e.ReturnPopNode].Path : "-")}");
                }
                break;
            case ObjectKind.Trigger:
                {
                    var m = scene.Markers[model.Triggers[r.Index]];
                    sb.Append($"{m.TypeName}{(m.Name.Length > 0 ? $" '{m.Name}'" : "")} key 0x{m.InstanceKey:X}, layer '{scene.Layers[m.LayerIndex].Name}'").Append('\n');
                    if (m.Source.Trigger is { } t)
                    {
                        sb.Append($"shape {t.Shape}, priority {t.Priority}, enabled {t.Enabled}").Append('\n');
                    }
                    if (m.Source.Map is { } map)
                    {
                        var area = AreaByMarker().GetValueOrDefault(m.Index);
                        sb.Append($"map {map.Map}, place '{area?.Name}' ({map.PlaceNameBlock}/{map.PlaceNameSpot}), bgm {map.Bgm}").Append('\n');
                    }
                    sb.Append($"bounds ({m.WorldBounds.Min.X:f1}, {m.WorldBounds.Min.Z:f1})..({m.WorldBounds.Max.X:f1}, {m.WorldBounds.Max.Z:f1}) y {m.WorldBounds.Min.Y:f1}..{m.WorldBounds.Max.Y:f1}");
                }
                break;
            case ObjectKind.Enemy:
                if (Enemy(r) is { } en)
                {
                    sb.Append($"0x{en.Oid:X} '{en.Name}' (name id {en.NameId}) in '{AreaName(en.Area)}'{(en.Ally ? ", friendly" : "")}{(en.Targetable ? "" : ", never targetable")}, max hp {en.MaxHp}").Append('\n');
                    sb.Append($"spawned {en.Spawn:f1}s at ({en.X:f1}, {en.Z:f1}){(en.Death >= 0 ? $", died {en.Death:f1}s at ({en.DX:f1}, {en.DZ:f1})" : ", not killed")}").Append('\n');
                    sb.Append(en.Class switch
                    {
                        ZoneEnemyClass.OnLoad => "placed on load: nobody was near when it appeared (streamed in with the room)",
                        ZoneEnemyClass.Wave => "wave: spawned while the party was in the room (a script placed it)",
                        ZoneEnemyClass.BossAdds => "boss adds: spawned inside a boss fight",
                        ZoneEnemyClass.Boss => "boss",
                        _ => "",
                    }).Append(en.Marker != 0 ? $", spawn marker key 0x{en.Marker:X}" : "").Append('\n');
                    var together = SpawnGroup(en).Where(x => x != en).ToList();
                    if (together.Count > 0)
                    {
                        sb.Append($"spawned together with: {SpawnGroupText(together)}");
                    }
                }
                break;
        }
        return sb.ToString();
    }

    private int FindEObjControllingBox(int box) => _session?.Model.ControllingEObjOfBox(box) ?? -1;

    private static string RoleName(ZoneObjectRole role) => ZoneSceneReporter.RoleName(role);

    private int NodeToEObj(string nodeId) => _session?.Model.FindEventObject(nodeId) ?? -1;

    private int NodeToPop(string nodeId)
    {
        if (nodeId.Length == 0 || _session == null)
        {
            return -1;
        }
        var id = ZoneSceneTimelineFile.ParseId(nodeId);
        return _session.Model.PopPoints.FindIndex(p => p.PathId == id);
    }

    private Vector3? NodePosition(string nodeId)
    {
        var scene = _scene;
        if (scene == null || nodeId.Length == 0)
        {
            return null;
        }
        if (_nodePositionCache.TryGetValue(nodeId, out var cached))
        {
            return cached;
        }
        var n = scene.FindNode(ZoneSceneTimelineFile.ParseId(nodeId));
        Vector3? r = null;
        if (n >= 0)
        {
            var eo = NodeToEObj(nodeId);
            r = eo >= 0 ? _session!.Model.EventObjects[eo].ActorPosition : scene.Nodes[n].Position;
        }
        _nodePositionCache[nodeId] = r;
        return r;
    }

    // the run in order: from the imported replay when there is one, else the layout order (scripted objects by handler id, warp landings guessed)
    private void DrawScenarioTree()
    {
        var session = _session!;
        var fromReplay = _timeline != null && _timeline.Scenario.Count > 0;
        var steps = fromReplay ? _timeline!.Scenario : session.Model.StaticFlow;
        if (steps.Count == 0)
        {
            return;
        }
        using var tree = ImRaii.TreeNode($"Scenario ({steps.Count} steps, {(fromReplay ? "replay" : "layout order")})###scenario", ImGuiTreeNodeFlags.DefaultOpen);
        if (!tree)
        {
            return;
        }
        if (!fromReplay)
        {
            ImGui.TextDisabled("no replay imported: order = scripted object handler ids; warp landings are guesses (dashed arrows) until a replay confirms them");
            ImGui.SameLine();
        }
        if (ImGui.SmallButton("map every waypoint"))
        {
            MapWaypoints();
        }
        Hint("Run the auto-map at the start, at every warp landing and at every sealed room and save each as a project ('wp N <area>'); the current centre and selection are restored afterwards");
        if (_scenarioLabelsFor != steps)
        {
            _scenarioLabels = new string[steps.Count];
            for (var i = 0; i < steps.Count; ++i)
            {
                _scenarioLabels[i] = ZoneSceneReporter.Scenario(steps[i], fromReplay, i);
            }
            _scenarioLabelsFor = steps;
        }
        for (var i = 0; i < steps.Count; ++i)
        {
            var st = steps[i];
            using var id = ImRaii.PushId(i);
            if (ImGui.SmallButton("go"))
            {
                var p = NodePosition(st.NodeId);
                if (p is { } pp)
                {
                    _canvas.Center = new(pp.X, pp.Z);
                }
                else if (st.X != 0f || st.Z != 0f)
                {
                    _canvas.Center = new(st.X, st.Z);
                }
                var eo = NodeToEObj(st.NodeId);
                if (eo >= 0)
                {
                    _selectedObject = new(ObjectKind.EObj, eo);
                }
                if (fromReplay)
                {
                    SeekTimeline((float)Math.Min(st.T, _timeline!.Duration) + 0.001f);
                }
            }
            Hint(fromReplay ? "Centre the canvas on this step's object and scrub the timeline to it" : "Centre the canvas on this step's object");
            if (st.TargetNodeId.Length > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("dest"))
                {
                    var p = NodePosition(st.TargetNodeId);
                    if (p is { } pp)
                    {
                        _canvas.Center = new(pp.X, pp.Z);
                    }
                    var pop = NodeToPop(st.TargetNodeId);
                    if (pop >= 0)
                    {
                        _selectedObject = new(ObjectKind.Pop, pop);
                    }
                }
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(_scenarioLabels[i]);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(st.Text);
            }
            ImGui.SameLine();
            ImGui.TextDisabled(st.Text.Length > 90 ? st.Text[..90] + "..." : st.Text);
        }
    }

    // the context menu of an object; the same body is drawn from the canvas popup and from the tables' "..." buttons
    private void DrawObjectPopupBody()
    {
        var session = _session!;
        var model = session.Model;
        var scene = _scene!;
        var r = _popupObject;
        if (!r.Valid)
        {
            return;
        }
        ImGui.TextDisabled(ObjectLabel(r));
        ImGui.Separator();
        switch (r.Kind)
        {
            case ObjectKind.EObj:
                {
                    var eo = model.EventObjects[r.Index];
                    if (eo.ControlsCollision)
                    {
                        if (ImGui.MenuItem(eo.IsDoor ? "Show closed (EventState 0)" : eo.IsSealController ? "Show sealed (EventState 0)" : "Show state 0 (collision on)"))
                        {
                            ShowEObjDerived(r.Index, 0);
                        }
                        if (ImGui.MenuItem(eo.IsDoor ? "Show open (EventState 7)" : eo.IsSealController ? "Show released (EventState 7)" : "Show state 7 (collision off)"))
                        {
                            ShowEObjDerived(r.Index, 7);
                        }
                        ImGui.Separator();
                    }
                    ImGui.SetNextItemWidth(60f);
                    ImGui.InputInt("##stateedit", ref _stateEdit, 0, 0);
                    ImGui.SameLine();
                    if (ImGui.MenuItem("Set EventState in active scene"))
                    {
                        SetEObjStateInScene(r.Index, (ushort)Math.Clamp(_stateEdit, 0, ushort.MaxValue));
                    }
                    if (session.EObjState(r.Index) != null && ImGui.MenuItem("Clear state in active scene"))
                    {
                        SetEObjStateInScene(r.Index, null);
                    }
                    if (eo.BoundNode >= 0)
                    {
                        if (ImGui.MenuItem("Go to bound instance"))
                        {
                            var b = scene.Nodes[eo.BoundNode].World.Translation;
                            _canvas.Center = new(b.X, b.Z);
                        }
                        var placed = scene.IsNodePlaced(eo.BoundNode);
                        if (ImGui.MenuItem(placed ? "Remove bound instance from scene" : "Restore bound instance in scene"))
                        {
                            SetNodeInScene(eo.BoundNode, placed ? false : null);
                        }
                    }
                    ImGui.Separator();
                    if (eo.IsSealController)
                    {
                        if (ImGui.MenuItem("Add rule: seal on pull / release"))
                        {
                            AddRuleForEObj(ZoneRuleKind.SealOnPull, r.Index);
                            AddRuleForEObj(ZoneRuleKind.SealRelease, r.Index);
                        }
                    }
                    else if (eo.IsDoor)
                    {
                        if (ImGui.MenuItem("Add rule: key opens this door"))
                        {
                            AddRuleForEObj(ZoneRuleKind.KeyOpensDoor, r.Index);
                        }
                    }
                    else if (eo.Role == ZoneObjectRole.Warp)
                    {
                        if (ImGui.MenuItem("Add rule: warp appears when the room is cleared"))
                        {
                            AddRuleForEObj(ZoneRuleKind.WarpOpens, r.Index);
                        }
                    }
                    else if (ImGui.MenuItem("Add rule: spawns when a set is cleared"))
                    {
                        AddRuleForEObj(ZoneRuleKind.SetClearSpawns, r.Index);
                    }
                    if (ImGui.MenuItem("Copy OID enum line"))
                    {
                        ImGui.SetClipboardText(SceneCodeGen.OidLine(eo.BaseId, eo.Name));
                    }
                }
                break;
            case ObjectKind.Pop:
                if (ImGui.MenuItem("Add rule: players teleport here"))
                {
                    var p = model.PopPoints[r.Index];
                    AddRule(new SavedRule { Kind = ZoneRuleKind.WarpTeleport, Name = $"WarpTeleport 0x{p.InstanceKey:X}", Channel = ZoneStateChannel.ObjectState, EffectNodeId = ZoneSceneTimelineFile.FormatId(p.PathId), EffectText = $"players teleport to pop point key 0x{p.InstanceKey:X} at ({p.Position.X:f1}, {p.Position.Z:f1})", TriggerText = "boss death / director update" });
                }
                break;
            case ObjectKind.Exit:
                {
                    var e = model.Exits[r.Index];
                    if (e.DestPopNode >= 0 && ImGui.MenuItem("Go to destination pop"))
                    {
                        var p = scene.Nodes[e.DestPopNode].Position;
                        _canvas.Center = new(p.X, p.Z);
                    }
                    if (e.ReturnPopNode >= 0 && ImGui.MenuItem("Go to return pop"))
                    {
                        var p = scene.Nodes[e.ReturnPopNode].Position;
                        _canvas.Center = new(p.X, p.Z);
                    }
                }
                break;
            case ObjectKind.Enemy:
                if (Enemy(r) is { } en && ImGui.MenuItem("Add rule: something spawns when this group dies"))
                {
                    var group = SpawnGroup(en);
                    var area = AreaName(en.Area, "area");
                    AddRule(new SavedRule { Kind = ZoneRuleKind.SetClearSpawns, Name = $"SetClearSpawns {area}", TriggerText = $"last of {group.Count} enemies in '{area}' dies", SetMembers = SpawnGroupText(group), TriggerOid = $"0x{group[^1].Oid:X}" });
                }
                break;
        }
        if (ImGui.MenuItem("Go to"))
        {
            GoToObject(r);
        }
    }

    private void DrawObjectsSection()
    {
        var session = _session!;
        var model = session.Model;
        var scene = _scene!;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputTextWithHint("##objfilter", "filter by name / OID / key (Enter = jump)", ref _objectFilter, 64, ImGuiInputTextFlags.EnterReturnsTrue) && _objectFilter.Length > 0)
        {
            JumpToObject(_objectFilter.Trim());
        }
        Hint("Enter jumps to the first event object (then pop point) whose OID, instance key or name matches");
        _filterIsHex = ZoneBinary.TryParseHex(_objectFilter, out _filterHex);
        ImGui.SameLine();
        ImGui.Checkbox("near centre only", ref _objectsNearCentreOnly);
        Hint("Only objects within radius + 20 yalms of the centre");
        if (_objectsSummaryModel != model || _objectsSummaryTimeline != _timeline)
        {
            _objectsSummary = $"{model.EventObjects.Count} event objects, {model.PopPoints.Count} pop points, {model.Exits.Count} exit links, {model.Triggers.Count} trigger volumes, {model.Areas.Count} map areas{(model.FloorPieces.Count > 0 ? $", {model.FloorPieces.Count} floor pieces" : "")}{(_timeline != null ? $", {_timeline.Enemies.Count} replay enemies" : "")}";
            _objectsSummaryModel = model;
            _objectsSummaryTimeline = _timeline;
        }
        ImGui.TextDisabled(_objectsSummary);
        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        DrawScenarioTree();
        using (var tree = ImRaii.TreeNode($"Event objects ({model.EventObjects.Count})###eobjs", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (tree)
            {
                using var table = ImRaii.Table("##eobjtable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY, new Vector2(-1f, rowHeight * 9f));
                if (table)
                {
                    ImGui.TableSetupColumn("OID");
                    ImGui.TableSetupColumn("name");
                    ImGui.TableSetupColumn("controls");
                    ImGui.TableSetupColumn("state");
                    ImGui.TableSetupColumn("collision");
                    ImGui.TableSetupColumn("");
                    ImGui.TableHeadersRow();
                    for (var i = 0; i < model.EventObjects.Count; ++i)
                    {
                        var eo = model.EventObjects[i];
                        var r = new ObjectRef(ObjectKind.EObj, i);
                        if (!Matches(eo.Label, eo.InstanceKey) || !ObjectNearCentre(r))
                        {
                            continue;
                        }
                        using var id = ImRaii.PushId(i);
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ObjectRow($"0x{eo.BaseId:X}", r, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(eo.Name.Length > 0 ? eo.Name : eo.IsBarrierVfx ? "(vfx)" : "");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(eo.BoundNode >= 0 ? $"{eo.ControlledMeshes.Length}m {eo.ControlledBoxes.Length}b{(eo.IsSealController ? " seal" : eo.IsDoor ? " door" : "")}" : "-");
                        ImGui.TableNextColumn();
                        if (eo.ControlsCollision)
                        {
                            var cur = session.EObjState(i);
                            var sel = cur is null ? 0 : cur == 0 ? 1 : cur == 7 ? 2 : 3;
                            ImGui.SetNextItemWidth(70f);
                            if (ImGui.Combo("##st", ref sel, "default\0 0 (on)\0 7 (off)\0other\0"))
                            {
                                if (sel == 0)
                                {
                                    SetEObjStateInScene(i, null);
                                }
                                else if (sel == 1)
                                {
                                    SetEObjStateInScene(i, 0);
                                }
                                else if (sel == 2)
                                {
                                    SetEObjStateInScene(i, 7);
                                }
                            }
                        }
                        else
                        {
                            var cur = session.EObjState(i);
                            ImGui.TextDisabled(cur is { } c ? c.ToString() : "-");
                        }
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(eo.BoundNode >= 0 ? scene.IsNodeActive(eo.BoundNode) ? "on" : scene.IsNodePlaced(eo.BoundNode) ? "off" : "removed" : "");
                        ImGui.TableNextColumn();
                        if (ImGui.SmallButton("..."))
                        {
                            _popupObject = r;
                            _openObjectPopup = true;
                        }
                    }
                }
                if (_openObjectPopup)
                {
                    _openObjectPopup = false;
                    ImGui.OpenPopup(ObjectPopupId);
                }
                DrawObjectPopup();
            }
        }
        using (var tree = ImRaii.TreeNode($"Pop points ({model.PopPoints.Count})###pops"))
        {
            if (tree)
            {
                for (var i = 0; i < model.PopPoints.Count; ++i)
                {
                    var p = model.PopPoints[i];
                    var r = new ObjectRef(ObjectKind.Pop, i);
                    if (!ObjectNearCentre(r) || !Matches(p.PopType.ToString(), p.InstanceKey))
                    {
                        continue;
                    }
                    using var id = ImRaii.PushId(i);
                    ObjectRow($"key 0x{p.InstanceKey:X} {p.PopType} slot {p.SlotIndex} at ({p.Position.X:f1}, {p.Position.Z:f1}){(p.AreaIndex >= 0 ? $" '{model.Areas[p.AreaIndex].Name}'" : "")}", r);
                }
            }
        }
        if (model.Exits.Count > 0)
        {
            using var tree = ImRaii.TreeNode($"Exit links ({model.Exits.Count})###exits");
            if (tree)
            {
                for (var i = 0; i < model.Exits.Count; ++i)
                {
                    var e = model.Exits[i];
                    var r = new ObjectRef(ObjectKind.Exit, i);
                    if (!Matches($"exit {e.ExitIndex}", e.TerritoryType))
                    {
                        continue;
                    }
                    using var id = ImRaii.PushId(i);
                    ObjectRow($"exit {e.ExitIndex} at ({e.Position.X:f1}, {e.Position.Z:f1}) -> {(e.DestPopNode >= 0 ? $"pop key 0x{scene.Nodes[e.DestPopNode].InstanceKey:X}" : $"territory {e.TerritoryType}")}", r);
                }
            }
        }
        using (var tree = ImRaii.TreeNode($"Trigger volumes ({model.Triggers.Count})###triggers"))
        {
            if (tree)
            {
                for (var i = 0; i < model.Triggers.Count; ++i)
                {
                    var m = scene.Markers[model.Triggers[i]];
                    var r = new ObjectRef(ObjectKind.Trigger, i);
                    if (!ObjectNearCentre(r) || !Matches(m.Name, m.InstanceKey))
                    {
                        continue;
                    }
                    using var id = ImRaii.PushId(i);
                    var area = m.Type == (int)LgbInstanceType.MapRange ? AreaByMarker().GetValueOrDefault(m.Index)?.Name : null;
                    ObjectRow($"{m.TypeName}{(area != null ? $" '{area}'" : m.Name.Length > 0 ? $" '{m.Name}'" : "")} {m.WorldBounds.Max.X - m.WorldBounds.Min.X:f0}x{m.WorldBounds.Max.Z - m.WorldBounds.Min.Z:f0} at ({m.WorldBounds.Center.X:f1}, {m.WorldBounds.Center.Z:f1})", r);
                }
            }
        }
        if (model.FloorGroups.Count > 0)
        {
            using var floor = ImRaii.TreeNode($"Floor pieces ({model.FloorPieces.Count} in {model.FloorGroups.Count} group(s))###floor", ImGuiTreeNodeFlags.DefaultOpen);
            if (floor)
            {
                var present = _timeline?.PiecesPresentAt(_timelineT);
                foreach (var g in model.FloorGroups)
                {
                    using var gid = ImRaii.PushId(g.Index);
                    using var gtree = ImRaii.TreeNode($"{g.Name}: {g.Pieces.Count} piece(s), {g.Family}###group", ImGuiTreeNodeFlags.DefaultOpen);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(ZoneSceneReporter.FloorGroup(model, g));
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("copy floor snippet"))
                    {
                        ImGui.SetClipboardText(SceneCodeGen.FloorGroupSnippet(model, g, _pipeline.Decimals));
                    }
                    Hint(g.Family == ZoneFloorFamily.EAnim ? "The shape list of every piece, the union as the default arena, and an OnActorEAnim component skeleton that removes a platform when its object plays the break animation" : "The shape list of every section, the union as the default arena, and an OnMapEffect component skeleton; the map effect index comes from a replay ('events' MAPFX lines)");
                    if (!gtree)
                    {
                        continue;
                    }
                    foreach (var pi in g.Pieces)
                    {
                        var p = model.FloorPieces[pi];
                        using var pid = ImRaii.PushId(pi);
                        var gone = ZoneSceneTimelineFile.PieceGone(present, p);
                        var r = p.EObj >= 0 ? new ObjectRef(ObjectKind.EObj, p.EObj) : ObjectRef.None;
                        if (ImGui.Selectable($"{(p.Kind == ZoneFloorPieceKind.Platform ? "platform" : "section")} {g.Pieces.IndexOf(pi)}: {p.Bounds.Max.X - p.Bounds.Min.X:f0}x{p.Bounds.Max.Z - p.Bounds.Min.Z:f0} at ({p.Position.X:f1}, {p.Position.Z:f1}){(gone ? " (gone now)" : "")}{(p.EObj >= 0 ? $" {model.EventObjects[p.EObj].Label}" : "")}", r.Valid && _selectedObject == r))
                        {
                            CentreCanvasOn(p.Position);
                            if (r.Valid)
                            {
                                _selectedObject = r;
                            }
                        }
                        if (ImGui.IsItemHovered())
                        {
                            if (r.Valid)
                            {
                                _hoverObject = r;
                            }
                            ImGui.SetTooltip(ZoneSceneReporter.FloorPiece(model, p));
                        }
                    }
                }
            }
        }
        if (_timeline != null)
        {
            using (var tree = ImRaii.TreeNode($"Replay enemies ({_timeline.Enemies.Count})###enemies"))
            {
                if (tree)
                {
                    // by area, in spawn order; the same area can hold several spawn groups, the gap between them shows the room's waves
                    foreach (var (areaIndex, enemies) in EnemyGroups(_timeline))
                    {
                        using var areaTree = ImRaii.TreeNode($"{AreaName(areaIndex, "no area")} ({enemies.Count})###area{areaIndex}");
                        if (!areaTree)
                        {
                            continue;
                        }
                        foreach (var i in enemies)
                        {
                            var en = _timeline.Enemies[i];
                            if (!Matches(en.Name, en.Oid))
                            {
                                continue;
                            }
                            using var id = ImRaii.PushId(i);
                            ObjectRow($"{en.Spawn,6:f1}s {en.Class switch { ZoneEnemyClass.OnLoad => "[load]", ZoneEnemyClass.Wave => "[wave]", ZoneEnemyClass.BossAdds => "[adds]", ZoneEnemyClass.Boss => "[boss]", _ => "" },-6} 0x{en.Oid:X} {en.Name}{(en.Death >= 0 ? $" +{en.Death - en.Spawn:f0}s" : " (alive)")}", new(ObjectKind.Enemy, i));
                        }
                    }
                }
            }
            using var events = ImRaii.TreeNode($"Replay events ({_timeline.Events.Count})###events");
            if (events)
            {
                ImGui.Checkbox("controllers only", ref _eventsControllersOnly);
                Hint("Only event object state changes (EVTS / ESTA), director updates and teleports; off: every recorded event but targets and destroys");
                using var table = ImRaii.Table("##eventtable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY, new Vector2(-1f, rowHeight * 10f));
                if (table)
                {
                    ImGui.TableSetupColumn("t");
                    ImGui.TableSetupColumn("kind");
                    ImGui.TableSetupColumn("object");
                    ImGui.TableSetupColumn("state");
                    ImGui.TableSetupColumn("where");
                    ImGui.TableHeadersRow();
                    var shown = 0;
                    for (var i = 0; i < _timeline.Events.Count && shown < 400; ++i)
                    {
                        var e = _timeline.Events[i];
                        if (e.Kind is ZoneEventKind.Targetable or ZoneEventKind.Destroy || (_eventsControllersOnly && e.Kind is not (ZoneEventKind.EventState or ZoneEventKind.ObjectState or ZoneEventKind.DirectorUpdate or ZoneEventKind.Teleport)))
                        {
                            continue;
                        }
                        if (!Matches(e.Name, e.Oid))
                        {
                            continue;
                        }
                        ++shown;
                        using var id = ImRaii.PushId(i);
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        if (ImGui.Selectable($"{e.T:f1}", MathF.Abs((float)e.T - _timelineT) < 0.01f, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                        {
                            SeekTimeline((float)e.T + 0.001f);
                            if (e.Eobj >= 0 && e.Eobj < model.EventObjects.Count)
                            {
                                _selectedObject = new(ObjectKind.EObj, e.Eobj);
                            }
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(ZoneSceneReporter.TimelineEvent(e));
                        }
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(e.Kind.Tag());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(e.Kind is ZoneEventKind.DirectorUpdate ? $"{e.Upd:X8} p1={e.P1:X}" : e.Kind == ZoneEventKind.MapEffect ? $"index {e.State} state {e.P1:X8}" : e.Oid != 0 ? $"0x{e.Oid:X} {e.Name}" : e.Note);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(e.Kind is ZoneEventKind.EventState or ZoneEventKind.ObjectState ? $"{e.State}{(e.Initial ? " (initial)" : "")}" : e.Kind == ZoneEventKind.Teleport ? e.Note : "");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(e.NodePath.Length > 0 ? e.NodePath : e.Kind is ZoneEventKind.DirectorUpdate or ZoneEventKind.MapEffect ? "" : $"({e.X:f1}, {e.Z:f1})");
                    }
                }
            }
        }

        bool Matches(string label, uint key) => _objectFilter.Length == 0 || label.Contains(_objectFilter, StringComparison.OrdinalIgnoreCase) || _filterIsHex && key == _filterHex;
    }

    private List<(int area, List<int> enemies)> EnemyGroups(ZoneSceneTimelineFile tl)
    {
        if (_enemyGroupsFor != tl)
        {
            _enemyGroups.Clear();
            foreach (var g in tl.Enemies.Select((e, i) => (e, i)).Where(x => !x.e.Ally).GroupBy(x => x.e.Area).OrderBy(g => g.Min(x => x.e.Spawn)))
            {
                _enemyGroups.Add((g.Key, [.. g.OrderBy(x => x.e.Spawn).Select(x => x.i)]));
            }
            _enemyGroupsFor = tl;
        }
        return _enemyGroups;
    }
}
