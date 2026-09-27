using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace BossMod;

// the strip above the sidebar (Load -> Scenes -> Map -> Correct -> Export) with the current step highlighted and its next action; undo for
// scene / rule / object-state edits (snapshots taken automatically when the edited state changes); the project health report
public sealed partial class ZoneArenaEditorWindow
{
    private string _openHeader = "";
    private static readonly string[] WorkflowSteps = ["Load", "Scenes", "Map", "Correct", "Export"];
    private static readonly string[] WorkflowHeaders = ["Zone", "Scenes", "Auto-map", "Selection", "Result"];

    private int CurrentWorkflowStep()
    {
        if (_scene == null || _session == null)
        {
            return 0;
        }
        if (_session.Selected.Count == 0 && _session.SelectedFloorBoxes.Count == 0)
        {
            // nothing mapped yet: the scene step is current until a replay is imported or a scene beyond the layout default exists
            return _timeline == null && _importedTimelines.Count == 0 && _session.Scenes.Count <= 1 ? 1 : 2;
        }
        return _pipeline.EnabledContourCounts(out _) == 0 || _resultDirty ? 3 : 4;
    }

    private void DrawWorkflowStrip()
    {
        var current = CurrentWorkflowStep();
        for (var i = 0; i < WorkflowSteps.Length; ++i)
        {
            if (i > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(">");
                ImGui.SameLine();
            }
            using var color = ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), i == current);
            if (ImGui.SmallButton(WorkflowSteps[i]))
            {
                _openHeader = WorkflowHeaders[i];
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(i switch
                {
                    0 => "Pick a territory (or the current zone); a recent one reopens its last project",
                    1 => "Choose the scene (seal / door states) the map is made under; import a replay to drive it",
                    2 => "Set the centre and run the auto-map (or map every waypoint / boss room in one go)",
                    3 => "Fix the selection: grow, exclude meshes, floor boxes, manual polygons, seals; recompute",
                    _ => "Copy or write the arena snippet and the rule components into the module file",
                });
            }
        }
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        switch (current)
        {
            case 0:
                if (ImGui.SmallButton("Current zone##wf"))
                {
                    LoadTerritory(Service.ClientState.TerritoryType);
                }
                break;
            case 1:
            case 2:
                using (ImRaii.Disabled(_autoMapTask != null || _session == null || !_session.CentreValid))
                {
                    if (ImGui.SmallButton("Auto-map##wf"))
                    {
                        StartAutoMap();
                    }
                }
                Hint(_session is { CentreValid: false } ? "Set the centre first (Centre tool, 'From seals' or a waypoint)" : current == 1 ? "Flood-fill the floor from the centre under the layout defaults; import a replay or add a scene under Scenes first to map a sealed / opened state" : "Flood-fill the floor from the centre");
                break;
            case 3:
                using (ImRaii.Disabled(_recomputeTask != null))
                {
                    if (ImGui.SmallButton("Recompute##wf"))
                    {
                        StartRecompute();
                    }
                }
                break;
            default:
                if (ImGui.SmallButton("Copy module snippet##wf"))
                {
                    ImGui.SetClipboardText(_pipeline.BuildModuleSnippet(ProvenanceHeader(), false));
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!CanWriteArenaToModuleFile))
                {
                    if (ImGui.SmallButton("Write arena to file##wf"))
                    {
                        WriteArenaToModuleFile();
                    }
                }
                break;
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(_editUndo.Count == 0))
        {
            if (ImGui.SmallButton($"undo edit ({_editUndo.Count})"))
            {
                UndoEdit();
            }
        }
        Hint("Undo the last scene / rule / object-state edit (ctrl+shift+Z); triangle selection has its own undo (ctrl+Z)");
    }

    // a collapsing header that the workflow strip can open on demand
    private bool Header(string name, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
    {
        if (_openHeader == name)
        {
            ImGui.SetNextItemOpen(true);
            _openHeader = "";
        }
        return ImGui.CollapsingHeader(name, flags);
    }

    // --- edit undo: scenes, active scene, object rules and mechanic rules are snapshotted when their fingerprint changes ---

    private sealed class EditSnapshot
    {
        public List<ZoneScene> Scenes = [];
        public int Active;
        public List<ZoneEObjRule> EObjRules = [];
        public List<SavedRule> Rules = [];
    }

    private readonly List<EditSnapshot> _editUndo = [];
    private int _editFingerprint;
    private bool _editFingerprinted;
    private bool _editRestoring;
    private EditSnapshot? _editPrevious;
    private long _editLastPush;
    private const int EditUndoDepth = 32;

    // a hash of the undoable state (scene names and states, object rules, mechanic rule fields), compared once per frame
    private int EditFingerprint()
    {
        var session = _session!;
        var h = new HashCode();
        h.Add(session.ActiveSceneIndex);
        foreach (var s in session.Scenes)
        {
            h.Add(s.Name);
            h.Add(s.State.Fingerprint());
        }
        h.Add(session.EObjRules.Count);
        foreach (var r in session.EObjRules)
        {
            h.Add(r);
        }
        h.Add(_rules.Count);
        foreach (var r in _rules)
        {
            h.Add(r.Kind);
            h.Add(r.Name);
            h.Add(r.Enabled);
            h.Add(r.TriggerOid);
            h.Add(r.TriggerNodeId);
            h.Add(r.TriggerState);
            h.Add(r.EffectOid);
            h.Add(r.EffectNodeId);
            h.Add(r.EffectState);
            h.Add(r.ArenaPre);
            h.Add(r.ArenaPost);
            h.Add(r.HintText);
            h.Add(r.Channel);
        }
        return h.ToHashCode();
    }

    private EditSnapshot CaptureEdit()
    {
        var session = _session!;
        return new()
        {
            Scenes = [.. session.Scenes.Select(s => s.Clone(s.Name))],
            Active = session.ActiveSceneIndex,
            EObjRules = [.. session.EObjRules],
            Rules = [.. _rules.Select(r => r.Clone())],
        };
    }

    // called once per frame: a changed fingerprint pushes the previous snapshot; edits within a second of the last push coalesce (typing)
    private void TrackEdits()
    {
        if (_session == null)
        {
            return;
        }
        var fp = EditFingerprint();
        if (_editFingerprinted && fp == _editFingerprint)
        {
            _editRestoring = false; // an undo that restored the current state still ends the restore
            return;
        }
        if (_editFingerprinted && _editPrevious != null && !_editRestoring)
        {
            var now = Environment.TickCount64;
            if (now - _editLastPush > 1000 || _editUndo.Count == 0)
            {
                _editUndo.Add(_editPrevious);
                if (_editUndo.Count > EditUndoDepth)
                {
                    _editUndo.RemoveAt(0);
                }
            }
            _editLastPush = now;
        }
        _editRestoring = false;
        _editFingerprint = fp;
        _editFingerprinted = true;
        _editPrevious = CaptureEdit();
    }

    private void UndoEdit()
    {
        if (_session == null || _editUndo.Count == 0)
        {
            return;
        }
        var snap = _editUndo[^1];
        _editUndo.RemoveAt(_editUndo.Count - 1);
        var session = _session;
        session.Scenes.Clear();
        session.Scenes.AddRange(snap.Scenes.Select(s => s.Clone(s.Name)));
        session.ActiveSceneIndex = Math.Clamp(snap.Active, 0, Math.Max(0, session.Scenes.Count - 1));
        session.EObjRules.Clear();
        session.EObjRules.AddRange(snap.EObjRules);
        _rules.Clear();
        _rules.AddRange(snap.Rules.Select(r => r.Clone()));
        _selectedRule = Math.Min(_selectedRule, _rules.Count - 1);
        _ruleSnippetFor = -1;
        _editRestoring = true;
        if (session.Scenes.Count > 0)
        {
            session.ActivateScene(session.ActiveSceneIndex, false);
        }
        ApplyActiveScene();
    }

    private void ResetEditHistory()
    {
        _editUndo.Clear();
        _editFingerprinted = false;
        _editRestoring = false;
        _editPrevious = null;
    }

    // --- project health: what a saved project referenced that the loaded layout no longer has ---

    private readonly List<string> _projectHealth = [];

    private void ReportProjectHealth(ZoneArenaProject p, int missingMeshRefs, int skippedDeltas)
    {
        _projectHealth.Clear();
        var scene = _scene!;
        var session = _session!;
        if (p.Seals.Count != session.Seals.Count)
        {
            _projectHealth.Add($"seal count changed: {p.Seals.Count} saved, {session.Seals.Count} detected now (check the pair and the cuts)");
        }
        List<string> badNodes = [];
        foreach (var s in p.Scenes)
        {
            foreach (var id in s.NodeOverrides.Keys.Concat(s.EObjStates.Keys).Concat(s.EObjObjectStates.Keys))
            {
                if (!scene.NodeByPathId.ContainsKey(ZoneSceneTimelineFile.ParseId(id)) && badNodes.Count < 6)
                {
                    badNodes.Add($"{id[^6..]} (scene '{s.Name}')");
                }
            }
            foreach (var layer in s.DisabledLayers)
            {
                if (!scene.Layers.Exists(l => l.StablePath == layer) && badNodes.Count < 6)
                {
                    badNodes.Add($"layer '{layer}' (scene '{s.Name}')");
                }
            }
        }
        if (badNodes.Count > 0)
        {
            _projectHealth.Add($"scene references not in this layout: {string.Join(", ", badNodes)}");
        }
        var badRules = p.Rules.Where(r => r.EffectNodeId.Length > 0 && !scene.NodeByPathId.ContainsKey(ZoneSceneTimelineFile.ParseId(r.EffectNodeId))).Select(r => r.Name).Take(4).ToList();
        if (badRules.Count > 0)
        {
            _projectHealth.Add($"rules whose effect object is gone: {string.Join(", ", badRules)}");
        }
        if (missingMeshRefs > 0)
        {
            _projectHealth.Add($"{missingMeshRefs} mesh reference(s) not found (mesh modes / deltas dropped)");
        }
        if (skippedDeltas > 0)
        {
            _projectHealth.Add($"{skippedDeltas} mesh delta(s) skipped: the mesh's triangle count changed since the save");
        }
        if (p.ReplayTimelinePath.Length > 0 && !System.IO.File.Exists(p.ReplayTimelinePath))
        {
            _projectHealth.Add($"replay file missing: {p.ReplayTimelinePath}");
        }
        if (p.ModuleFilePath.Length > 0 && !System.IO.File.Exists(p.ModuleFilePath))
        {
            _projectHealth.Add($"module file missing: {p.ModuleFilePath}");
        }
    }
}
