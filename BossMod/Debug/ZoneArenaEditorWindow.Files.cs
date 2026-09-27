using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.IO;
using System.Text.RegularExpressions;

namespace BossMod;

// the snippets written straight into a module file instead of the clipboard: the arena block is replaced in place, a rule component is
// replaced or inserted before the module class and activated in the trivial phase; plus autosave, reopen and the recent-zone list
public sealed partial class ZoneArenaEditorWindow
{
    private string _moduleFilePath = "";
    private string _fileStatus = "";
    private bool _autosave = true;
    private bool _reopenLast = true;
    private long _lastAutosave;
    private bool _autosaveDue;
    private const string AutosaveName = "_autosave";
    private readonly List<(uint territory, string name, DateTime saved)> _recentZones = [];
    private long _recentZonesAt;
    private bool _recentZonesStale = true;
    private (string key, Func<bool> get, Action<bool> set)[]? _viewToggles;

    private string ModuleFilePath => _moduleFilePath.Trim().Trim('"');

    private void DrawModuleFileControls()
    {
        ImGui.SetNextItemWidth(-190f);
        ImGui.InputTextWithHint("##modulefile", "module .cs under BossMod/Modules (arena block and rule components are written in place)", ref _moduleFilePath, 512);
        Hint("Full path of the room's boss module file; saved with the project. The arena block (vertex fields + ArenaBoundsCustom) is replaced where it is, else inserted at the end of the module class");
        ImGui.SameLine();
        using (ImRaii.Disabled(!CanWriteArenaToModuleFile))
        {
            if (ImGui.Button("Write arena to file"))
            {
                WriteArenaToModuleFile();
            }
        }
        if (_fileStatus.Length > 0)
        {
            ImGui.TextWrapped(_fileStatus);
        }
    }

    private bool CanWriteArenaToModuleFile => ModuleFilePath.Length > 0 && _pipeline.EnabledContourCounts(out _) > 0;

    private void WriteArenaToModuleFile()
    {
        if (!CanWriteArenaToModuleFile)
        {
            return;
        }
        _fileStatus = WriteArenaToFile(ModuleFilePath, _pipeline.BuildModuleSnippet(ProvenanceHeader(), false), _pipeline.ArenaFieldName.Length > 0 ? _pipeline.ArenaFieldName : "arena");
    }

    // the module file as lines, its own line ending kept for the write back; null when it does not exist
    private static List<string>? ReadModuleLines(string path, out string nl)
    {
        nl = "\n";
        if (!File.Exists(path))
        {
            return null;
        }
        var text = File.ReadAllText(path);
        nl = text.Contains("\r\n") ? "\r\n" : "\n";
        return [.. text.Split(nl)];
    }

    private static void WriteModuleText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    private static List<string> SnippetLines(string snippet) => [.. snippet.Replace("\r\n", "\n").TrimEnd().Split('\n')];

    // file errors (locked, read-only, missing directory) become the status text instead of an exception
    private static string WriteArenaToFile(string path, string snippet, string field)
    {
        try
        {
            return ReplaceArenaBlock(path, snippet, field);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"file error: {ex.Message}";
        }
    }

    private static string WriteComponentToFile(string path, string snippet)
    {
        try
        {
            return ReplaceComponent(path, snippet);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"file error: {ex.Message}";
        }
    }

    // the line of the closing brace of the class declared at `classLine` (brace depth from its opening brace); -1 when unbalanced
    private static int ClassCloseLine(List<string> lines, int classLine)
    {
        var depth = 0;
        var opened = false;
        for (var i = classLine; i < lines.Count; ++i)
        {
            foreach (var c in lines[i])
            {
                if (c == '{')
                {
                    ++depth;
                    opened = true;
                }
                else if (c == '}' && --depth == 0 && opened)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    // replaces the existing `private static readonly ArenaBoundsCustom <field>` block (with the WPos[] fields and comments right above it), else
    // inserts the block before the closing brace of the module class (the first `class ... : BossModule`)
    private static string ReplaceArenaBlock(string path, string snippet, string field)
    {
        if (ReadModuleLines(path, out var nl) is not { } lines)
        {
            return $"not found: {path}";
        }
        var block = SnippetLines(snippet).Where(l => !l.StartsWith("// ctor:")).Select(l => l.Length > 0 ? "    " + l : l).ToList();
        var decl = lines.FindIndex(l => l.TrimStart().StartsWith($"private static readonly ArenaBoundsCustom {field} "));
        if (decl >= 0)
        {
            var start = decl;
            while (start > 0)
            {
                var prev = lines[start - 1].TrimStart();
                if (prev.Length == 0)
                {
                    break; // a blank line ends the block: a comment above it belongs to the previous member
                }
                if (prev.StartsWith("private static readonly WPos[]") || prev.StartsWith("new(") || prev.StartsWith("//"))
                {
                    --start;
                }
                else
                {
                    break;
                }
            }
            lines.RemoveRange(start, decl - start + 1);
            lines.InsertRange(start, block);
            WriteModuleText(path, string.Join(nl, lines));
            return $"replaced the '{field}' arena block in {Path.GetFileName(path)} ({decl - start + 1} line(s) -> {block.Count})";
        }
        // no block yet: a one-line module (`... : BossModule(...);`) becomes a class with a body, else the block goes before the module class's closing brace
        var module = lines.FindIndex(l => l.Contains(": BossModule(") && l.TrimEnd().EndsWith(';'));
        if (module >= 0)
        {
            var line = lines[module].TrimEnd();
            lines[module] = line[..^1];
            lines.InsertRange(module + 1, ["{", .. block, "}"]);
            WriteModuleText(path, string.Join(nl, lines));
            return $"added a class body with the '{field}' arena block to {Path.GetFileName(path)}; point the ctor at {field}.Center, {field}";
        }
        var moduleClass = lines.FindIndex(l => Regex.IsMatch(l, @"\bclass\s+\w+.*:\s*BossModule\b"));
        var close = moduleClass >= 0 ? ClassCloseLine(lines, moduleClass) : -1;
        if (close < 0)
        {
            return "no module class body found in the file";
        }
        lines.InsertRange(close, block);
        WriteModuleText(path, string.Join(nl, lines));
        return $"inserted the '{field}' arena block at the end of the module class in {Path.GetFileName(path)}";
    }

    // a rule component: replaced when a class of that name exists, else inserted before the states class / [ModuleInfo]; activated in TrivialPhase()
    private static string ReplaceComponent(string path, string snippet)
    {
        var cls = Regex.Match(snippet, @"sealed class (\w+)\(BossModule module\)");
        if (!cls.Success)
        {
            return "this rule emits notes only, nothing to write";
        }
        if (ReadModuleLines(path, out var nl) is not { } lines)
        {
            return $"not found: {path}";
        }
        var name = cls.Groups[1].Value;
        var block = SnippetLines(snippet);
        var existing = lines.FindIndex(l => l.StartsWith($"sealed class {name}("));
        string what;
        if (existing >= 0)
        {
            var start = existing;
            while (start > 0 && lines[start - 1].StartsWith("//"))
            {
                --start;
            }
            var end = lines.FindIndex(existing, l => l == "}");
            if (end < 0)
            {
                return $"class {name} found but its closing brace is not at column 0";
            }
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, block);
            what = $"replaced component {name}";
        }
        else
        {
            var at = lines.FindIndex(l => l.StartsWith("sealed class ") && l.Contains("States : StateMachineBuilder"));
            if (at < 0)
            {
                at = lines.FindIndex(l => l.StartsWith("[ModuleInfo("));
            }
            if (at < 0)
            {
                at = lines.Count;
            }
            lines.InsertRange(at, [.. block, ""]);
            what = $"inserted component {name}";
        }
        var joined = string.Join(nl, lines);
        if (!joined.Contains($"ActivateOnEnter<{name}>"))
        {
            var phase = Regex.Match(joined, @"TrivialPhase\(\)");
            if (phase.Success)
            {
                joined = joined[..phase.Index] + $"TrivialPhase(){nl}            .ActivateOnEnter<{name}>()" + joined[(phase.Index + phase.Length)..];
                what += ", activated in TrivialPhase()";
            }
            else
            {
                what += "; add .ActivateOnEnter<" + name + ">() to the state machine";
            }
        }
        WriteModuleText(path, joined);
        return $"{what} in {Path.GetFileName(path)}";
    }

    // the working state saved as '_autosave' a few seconds after every result change; false while it still has to wait (the caller retries next frame)
    private bool AutosaveTick()
    {
        if (!_autosave || _scene == null || _session == null || _pipeline.Raw.Count == 0)
        {
            return true;
        }
        if (MappingBusy || _pendingProjectLoad != null || _autoMapTask != null || Environment.TickCount64 - _lastAutosave < 5000)
        {
            return false;
        }
        _lastAutosave = Environment.TickCount64;
        try
        {
            SaveProject(AutosaveName, false);
        }
        catch (Exception ex)
        {
            _projectStatus = $"autosave failed: {ex.Message}";
        }
        return true;
    }

    // the last saved project of a freshly loaded territory (else its autosave) comes back
    private void ReopenLastProject(ZoneCollisionScene scene)
    {
        if (!_reopenLast)
        {
            return;
        }
        var file = LoadProjectFile(scene.TerritoryId);
        var name = file.LastProject.Length > 0 && file.Projects.ContainsKey(file.LastProject) ? file.LastProject : file.Projects.ContainsKey(AutosaveName) ? AutosaveName : "";
        if (name.Length == 0)
        {
            return;
        }
        _projectName = name;
        LoadProject(name);
    }

    // territories with a project file, newest first; the directory is scanned at most every 5 s and after a project write
    private List<(uint territory, string name, DateTime saved)> RecentZones()
    {
        var now = Environment.TickCount64;
        if (!_recentZonesStale && now - _recentZonesAt < 5000)
        {
            return _recentZones;
        }
        _recentZonesAt = now;
        _recentZonesStale = false;
        _recentZones.Clear();
        var dir = Path.Combine(_dalamud.ConfigDirectory.FullName, "zone-arenas");
        if (!Directory.Exists(dir))
        {
            return _recentZones;
        }
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            if (!uint.TryParse(Path.GetFileNameWithoutExtension(f), out var territory))
            {
                continue;
            }
            var z = _zones.Find(z => z.TerritoryId == territory);
            _recentZones.Add((territory, z.Cfc.Length > 0 ? z.Cfc : z.Place.Length > 0 ? z.Place : territory.ToString(), File.GetLastWriteTime(f)));
        }
        _recentZones.Sort((a, b) => b.saved.CompareTo(a.saved));
        return _recentZones;
    }

    private void DrawRecentZones(bool loading)
    {
        var recent = RecentZones();
        if (recent.Count == 0)
        {
            return;
        }
        ImGui.TextDisabled("recent:");
        var shown = 0;
        using (ImRaii.Disabled(loading))
        {
            foreach (var (territory, name, saved) in recent)
            {
                if (shown++ >= 6)
                {
                    break;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"{name}##recent{territory}"))
                {
                    LoadTerritory(territory);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"territory {territory}, projects saved {saved:g}");
                }
            }
        }
        ImGui.SameLine();
        ImGui.Checkbox("reopen last", ref _reopenLast);
        Hint("When a territory loads, reload its last saved project (else its autosave)");
        ImGui.SameLine();
        ImGui.Checkbox("autosave", ref _autosave);
        Hint("Save the working state as project '_autosave' a few seconds after every result change");
    }

    // the overlay toggles saved with a view: one table for the save and the load
    private (string key, Func<bool> get, Action<bool> set)[] ViewToggles => _viewToggles ??=
    [
        ("allTriangles", () => _showAllTriangles, v => _showAllTriangles = v),
        ("selected", () => _showSelected, v => _showSelected = v),
        ("result", () => _showResult, v => _showResult = v),
        ("seals", () => _showSeals, v => _showSeals = v),
        ("markers", () => _showMarkers, v => _showMarkers = v),
        ("player", () => _showPlayer, v => _showPlayer = v),
        ("worldPreview", () => _showWorldPreview, v => _showWorldPreview = v),
        ("rawOutline", () => _showRawOutline, v => _showRawOutline = v),
        ("triggers", () => _showTriggers, v => _showTriggers = v),
        ("links", () => _showLinks, v => _showLinks = v),
        ("ghost", () => _ghostInactive, v => _ghostInactive = v),
        ("path", () => _showPlayerPath, v => _showPlayerPath = v),
        ("bosses", () => _showBosses, v => _showBosses = v),
        ("coverage", () => _showCoverage, v => _showCoverage = v),
        ("spawns", () => _showSpawns, v => _showSpawns = v),
        ("areas", () => _showAreas, v => _showAreas = v),
        ("moduleArena", () => _showModuleArena, v => _showModuleArena = v),
        ("floorPieces", () => _showFloorPieces, v => _showFloorPieces = v),
    ];

    private SavedView CaptureView()
    {
        var v = new SavedView { CenterX = _canvas.Center.X, CenterZ = _canvas.Center.Z, Zoom = _canvas.Zoom };
        foreach (var (key, get, _) in ViewToggles)
        {
            v.Toggles[key] = get();
        }
        return v;
    }

    private void ApplyView(SavedView v)
    {
        if (v.Zoom > 0f)
        {
            _canvas.Center = new(v.CenterX, v.CenterZ);
            _canvas.Zoom = v.Zoom;
        }
        foreach (var (key, _, set) in ViewToggles)
        {
            if (v.Toggles.TryGetValue(key, out var b))
            {
                set(b);
            }
        }
    }
}
