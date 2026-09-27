using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BossMod;

// mechanic rules: inferred from replays or added by hand, turned into component snippets for the room's boss module
public sealed partial class ZoneArenaEditorWindow
{
    private readonly List<SavedRule> _rules = [];
    private int _selectedRule = -1;
    private string _ruleSnippet = "";
    private int _ruleSnippetFor = -1;

    private static readonly ZoneRuleKind[] RuleKinds = Enum.GetValues<ZoneRuleKind>();

    // the start and the exit gate come from the replay detectors only: never offered for a rule made by hand
    private static bool DetectorOnly(ZoneRuleKind kind) => kind is ZoneRuleKind.InstanceStart or ZoneRuleKind.ExitGate;

    private void AddRuleForEObj(ZoneRuleKind kind, int eobj)
    {
        var eo = _session!.Model.EventObjects[eobj];
        var r = new SavedRule
        {
            Kind = kind,
            Name = $"{kind} {eo.Label}",
            EffectOid = $"0x{eo.BaseId:X}",
            EffectNodeId = ZoneSceneTimelineFile.FormatId(eo.PathId),
            EffectState = kind == ZoneRuleKind.SealOnPull ? 0 : 7,
            ArenaPre = "(current)",
        };
        r.EffectText = kind switch
        {
            ZoneRuleKind.SealOnPull => $"{eo.Label} EventState 0: seal collision on",
            ZoneRuleKind.SealRelease => $"{eo.Label} EventState 7: seal collision removed",
            ZoneRuleKind.KeyOpensDoor => $"door {eo.Label} opens (EventState 7)",
            ZoneRuleKind.WarpOpens => $"warp {eo.Label} appears (EventState 0)",
            _ => $"{eo.Label} spawns",
        };
        r.TriggerText = kind switch
        {
            ZoneRuleKind.SealOnPull => "boss pull",
            ZoneRuleKind.SealRelease => "boss death",
            ZoneRuleKind.KeyOpensDoor => "key used (EventState 7)",
            ZoneRuleKind.WarpOpens => "room cleared",
            _ => "last enemy of the set dies",
        };
        if (kind == ZoneRuleKind.WarpOpens)
        {
            r.EffectState = 0;
            r.ArenaPre = "";
        }
        if (kind is ZoneRuleKind.SealOnPull or ZoneRuleKind.SealRelease)
        {
            r.TriggerOid = _bmm.ActiveModule?.PrimaryActor.OID is { } oid && oid != 0 ? $"0x{oid:X}" : "";
        }
        AddRule(r);
    }

    private void AddRule(SavedRule r)
    {
        _rules.Add(r);
        _selectedRule = _rules.Count - 1;
    }

    private void DrawRulesSection()
    {
        var session = _session!;
        ImGui.TextDisabled("rules become component snippets pasted into the room's boss module (state hooks, key pickup, door); import a replay timeline to infer them");
        if (ImGui.SmallButton("+ seal on pull"))
        {
            AddRule(new SavedRule { Kind = ZoneRuleKind.SealOnPull, Name = "SealOnPull", EffectState = 0, ArenaPre = "(current)", TriggerText = "boss pull", EffectText = "seal collision on" });
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ seal release"))
        {
            AddRule(new SavedRule { Kind = ZoneRuleKind.SealRelease, Name = "SealRelease", EffectState = 7, ArenaPre = "(current)", TriggerText = "boss death", EffectText = "seal collision removed" });
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ key drop"))
        {
            AddRule(new SavedRule { Kind = ZoneRuleKind.SetClearSpawns, Name = "SetClearSpawns", TriggerText = "last enemy of the set dies", EffectText = "key spawns" });
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ door"))
        {
            AddRule(new SavedRule { Kind = ZoneRuleKind.KeyOpensDoor, Name = "KeyOpensDoor", TriggerState = 7, EffectState = 7, TriggerText = "key used", EffectText = "door opens" });
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ warp"))
        {
            AddRule(new SavedRule { Kind = ZoneRuleKind.WarpOpens, Name = "WarpOpens", EffectState = 0, TriggerText = "room cleared", EffectText = "warp appears (EventState 0)" });
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("+ teleport"))
        {
            AddRule(new SavedRule { Kind = ZoneRuleKind.WarpTeleport, Name = "WarpTeleport", Channel = ZoneStateChannel.ObjectState, TriggerText = "boss death / director update", EffectText = "players teleport to a pop point" });
        }
        Hint("Right-click an event object on the canvas (or its '...' button in Objects) to add a rule pre-filled with that object");
        if (ImGui.SmallButton("map sealed / released rooms"))
        {
            MapSealStates();
        }
        Hint("Auto-map every seal controller's room twice (seal up, seal removed), save both as projects and point the seal rules' 'arena before / after' at them; the arena-swap snippet is then complete without picking");
        for (var i = 0; i < _rules.Count; ++i)
        {
            var r = _rules[i];
            using var id = ImRaii.PushId(i);
            var enabled = r.Enabled;
            if (ImGui.Checkbox("##en", ref enabled))
            {
                r.Enabled = enabled;
            }
            ImGui.SameLine();
            if (ImGui.Selectable($"{ZoneSceneReporter.Rule(r)}##rule", _selectedRule == i))
            {
                _selectedRule = i;
            }
            if (ImGui.IsItemHovered() && r.Evidence.Count > 0)
            {
                ImGui.SetTooltip(string.Join("\n", r.Evidence));
            }
        }
        if (_selectedRule < 0 || _selectedRule >= _rules.Count)
        {
            return;
        }
        ImGui.Separator();
        var rule = _rules[_selectedRule];
        ImGui.SetNextItemWidth(130f);
        using (var kinds = ImRaii.Combo("kind", rule.Kind.ToString()))
        {
            if (kinds)
            {
                foreach (var kind in RuleKinds)
                {
                    if (DetectorOnly(kind) && kind != rule.Kind)
                    {
                        continue;
                    }
                    if (ImGui.Selectable(kind.ToString(), kind == rule.Kind))
                    {
                        rule.Kind = kind;
                    }
                }
            }
        }
        ImGui.SameLine();
        var esta = rule.Channel == ZoneStateChannel.ObjectState;
        if (ImGui.Checkbox("ESTA channel", ref esta))
        {
            rule.Channel = esta ? ZoneStateChannel.ObjectState : ZoneStateChannel.EventState;
        }
        Hint("Off: the object's EventState byte (OnActorEventStateChange; doors and seals in dungeons). On: the EObjSetState packet (OnActorEState; gimmick arenas)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160f);
        ImGui.InputText("name", ref rule.Name, 64);
        ImGui.SetNextItemWidth(90f);
        ImGui.InputText("trigger OID", ref rule.TriggerOid, 12);
        Hint("Seal rules: the boss OID (hex); door rules: the key object OID; set rules: the last enemy's OID (informational)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60f);
        ImGui.InputInt("t.state", ref rule.TriggerState, 0, 0);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputText("effect OID", ref rule.EffectOid, 12);
        Hint("The event object whose state changes (seal controller, door, spawned key); pick it via right-click on the canvas");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60f);
        ImGui.InputInt("e.state", ref rule.EffectState, 0, 0);
        DrawArenaCombo("arena before", ref rule.ArenaPre);
        ImGui.SameLine();
        DrawArenaCombo("arena after", ref rule.ArenaPost);
        Hint("Saved projects of this territory (their result polygons are stored with them); '(current)' = the result shown now; empty = no arena swap in the snippet");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##hint", "hint text shown to the player (optional)", ref rule.HintText, 128);
        if (rule.SetMembers.Length > 0)
        {
            ImGui.TextWrapped($"set: {rule.SetMembers}");
        }
        if (rule.Evidence.Count > 0)
        {
            ImGui.TextDisabled($"evidence: {string.Join("; ", rule.Evidence)}");
        }
        if (ImGui.Button("Copy snippet"))
        {
            ImGui.SetClipboardText(EmitRule(rule));
        }
        ImGui.SameLine();
        if (ImGui.Button("Preview"))
        {
            _ruleSnippet = EmitRule(rule);
            _ruleSnippetFor = _selectedRule;
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(ModuleFilePath.Length == 0))
        {
            if (ImGui.Button("Write to file"))
            {
                _fileStatus = WriteComponentToFile(ModuleFilePath, EmitRule(rule));
            }
        }
        Hint("Write the component into the module file named in the Result section: an existing class of the same name is replaced, else it is inserted before the states class and activated in TrivialPhase()");
        ImGui.SameLine();
        if (ImGui.Button("Delete rule"))
        {
            _rules.RemoveAt(_selectedRule);
            _selectedRule = -1;
            _ruleSnippetFor = -1;
            return;
        }
        if (_ruleSnippetFor == _selectedRule && _ruleSnippet.Length > 0)
        {
            ImGui.InputTextMultiline("##rulesnippet", ref _ruleSnippet, 65536, new Vector2(-1f, ImGui.GetTextLineHeight() * 12f), ImGuiInputTextFlags.ReadOnly);
        }
    }

    private void DrawArenaCombo(string label, ref string value)
    {
        var scene = _scene!;
        var file = LoadProjectFile(scene.TerritoryId);
        ImGui.SetNextItemWidth(140f);
        using var combo = ImRaii.Combo(label, value.Length > 0 ? value : "(none)");
        if (!combo)
        {
            return;
        }
        if (ImGui.Selectable("(none)", value.Length == 0))
        {
            value = "";
        }
        if (ImGui.Selectable("(current)", value == "(current)"))
        {
            value = "(current)";
        }
        foreach (var name in file.Projects.Keys)
        {
            if (ImGui.Selectable(name, value == name))
            {
                value = name;
            }
        }
    }

    // "(current)" = the pipeline's enabled contours, else the polygons saved with a project of this territory
    private SceneCodeGen.ArenaSource? ResolveArena(string name, string field)
    {
        if (name.Length == 0)
        {
            return null;
        }
        if (name == "(current)")
        {
            var polys = _pipeline.ExportEnabledPolygons(out var flat, out var meanY);
            return polys.Count == 0 ? null : SceneCodeGen.FromPolygons(field, polys, flat, meanY, _pipeline.AdjustForHitboxInwards, _pipeline.AdjustForHitboxOutwards, _pipeline.EmitProjectionHeightZero);
        }
        var file = LoadProjectFile(_scene!.TerritoryId);
        if (!file.Projects.TryGetValue(name, out var p) || p.ResultPolygons.Count == 0)
        {
            return null;
        }
        return SceneCodeGen.FromPolygons(field, p.ResultPolygons, p.ResultFlat, p.ResultMeanY, p.AdjustForHitboxInwards, p.AdjustForHitboxOutwards, p.EmitProjectionHeightZero);
    }

    private string EmitRule(SavedRule r)
    {
        var header = ProvenanceHeader();
        var decimals = _pipeline.Decimals;
        var pre = ResolveArena(r.ArenaPre, "room");
        var post = ResolveArena(r.ArenaPost, "open");
        return SceneCodeGen.Emit(r, header, pre, post, decimals, _session!.Model);
    }
}
