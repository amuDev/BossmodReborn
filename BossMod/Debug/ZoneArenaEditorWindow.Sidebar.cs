using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;

namespace BossMod;

public sealed partial class ZoneArenaEditorWindow
{
    private string _sealValueHex = "2400";
    private string _sealMaskHex = "1FFFFFFFFF";
    private readonly List<(string value, string mask)> _floorHex = [];
    private readonly List<(string value, string mask)> _obstacleBoxHex = [];
    private string _newObstacleBoxValueHex = "3000";
    private string _newObstacleBoxMaskHex = "F000";
    private string _newFloorValueHex = "700E";
    private string _newFloorMaskHex = "FFFF";
    private bool _hexSynced;
    private string _meshFilter = "";
    private string _validateStatus = "";

    private void DrawSidebar()
    {
        if (ImGui.CollapsingHeader("Zone", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawZoneSection();
        }
        if (_scene == null || _session == null)
        {
            return;
        }
        if (ImGui.CollapsingHeader("Auto-map", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawAutoMapSection();
        }
        if (ImGui.CollapsingHeader("Selection", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawSelectionSection();
        }
        if (ImGui.CollapsingHeader("Result", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawResultSection();
        }
        if (ImGui.CollapsingHeader("Project"))
        {
            DrawProjectSection();
        }
    }

    private void DrawZoneSection()
    {
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##zonesearch", "search place / duty name / territory id", ref _zoneSearch, 64);
        Hint("Every territory with layout data; click one to load its collision files from the game data (no need to be in the zone)");
        UpdateZoneMatches();
        using (var list = ImRaii.Child("##zonelist", new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * 6f), true))
        {
            if (list)
            {
                for (var i = 0; i < _zoneMatches.Count; ++i)
                {
                    var z = _zones[_zoneMatches[i]];
                    var label = z.Cfc.Length > 0 ? $"{z.TerritoryId}: {z.Cfc} ({z.Place})" : $"{z.TerritoryId}: {z.Place}";
                    if (ImGui.Selectable($"{label}##zone{z.TerritoryId}", z.TerritoryId == _loadedTerritory))
                    {
                        LoadTerritory(z.TerritoryId);
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(z.Bg);
                    }
                }
            }
        }
        var loading = _loadTask is { IsCompleted: false };
        using (ImRaii.Disabled(loading))
        {
            if (ImGui.Button("Current zone"))
            {
                LoadTerritory(Service.ClientState.TerritoryType);
            }
            Hint("Load the territory you are standing in");
            ImGui.SameLine();
            if (ImGui.Button("Reload") && _loadedTerritory != 0)
            {
                LoadTerritory(_loadedTerritory);
            }
            Hint("Reload the loaded territory from the game files (selection, centre and project state are reset)");
        }
        if (loading)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _loadCts?.Cancel();
            }
            ImGui.TextUnformatted($"loading: {_progress.Stage} {_progress.Current}/{_progress.Total}");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        ImGui.InputFloat("Terrain radius", ref _terrainRadius, 10f, 50f, "%.0f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Streamed terrain tiles are loaded around the player (current zone) or the last centre; 0 = none. Use 'Load terrain around centre' after moving the centre");
        }
        ImGui.SameLine();
        ImGui.Checkbox("Fit on load", ref _fitOnLoad);
        Hint("Fit the canvas to the loaded geometry when a zone finishes loading");
        ImGui.TextWrapped(_loadStatus);
        if (_scene == null)
        {
            return;
        }
        using (ImRaii.Disabled(_scene.TerritoryId != Service.ClientState.TerritoryType || loading))
        {
            if (ImGui.Button("Validate against live scene"))
            {
                var live = new ZoneSceneValidator();
                live.SnapshotLive();
                var report = ZoneSceneValidator.Compare(_scene, live);
                var best = 0;
                for (var i = 1; i < 12; ++i)
                {
                    if (report.OrderWins[i] > report.OrderWins[best])
                    {
                        best = i;
                    }
                }
                var configured = (int)ZoneTransform.RotationOrder + (ZoneTransform.ScaleFirst ? 0 : 6);
                _validateStatus = (report.RotatedMatches == 0 ? "no rotated instances matched, order unverified. " : best == configured ? "rotation order OK. " : $"ROTATION ORDER MISMATCH: live prefers {ZoneSceneValidator.Report.OrderName(best)}. ") + report.Summary;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Only while standing in the loaded territory: compares offline transforms/materials with the live collision scene");
        }
        if (_validateStatus.Length > 0)
        {
            ImGui.TextWrapped(_validateStatus);
        }
        var loadTerrain = ImGui.Button("Load terrain around centre");
        Hint("Load the streamed terrain tiles (list.pcb) within 'Terrain radius' of the centre - outdoor floors and many dungeon floors are terrain, not bg meshes");
        if (loadTerrain && _session != null && !loading)
        {
            var added = ZoneCollisionLoader.EnsureTerrainLoaded(_scene, new LuminaZoneFileSource(Service.LuminaGameData), new(_session.Centre.X, _session.Centre.Z), _terrainRadius);
            while (_meshModes.Count < _scene.Meshes.Count)
            {
                _meshModes.Add(MeshMode.Auto);
            }
            _picker = new(_scene);
            _session.Adjacency = null;
            _loadStatus = $"loaded {added} terrain tile(s); {_scene.Meshes.Count} meshes, {_scene.Triangles.Count} triangles";
            MarkResultDirty();
        }
        using var layers = ImRaii.TreeNode($"Layers ({_scene.Layers.Count})###layers");
        if (layers)
        {
            foreach (var layer in _scene.Layers)
            {
                var enabled = layer.Enabled;
                var label = layer.IsTerrain ? "terrain tiles" : $"{layer.SourceFile} '{layer.Name}'{(layer.SharedGroupChain.Length > 0 ? $" <{layer.SharedGroupChain}>" : "")}{(layer.FestivalId != 0 ? $" festival {layer.FestivalId}" : "")} ({layer.InstanceCount})";
                if (ImGui.Checkbox($"{label}###layer{layer.Index}", ref enabled))
                {
                    layer.Enabled = enabled;
                    if (_session != null)
                    {
                        _session.Adjacency = null;
                    }
                    MarkResultDirty();
                }
                Hint("Layout layer: untick to hide its meshes and boxes from picking, the flood fill and the cuts (festival layers and mid-fight layers are runtime state the loader cannot know)");
            }
        }
    }

    private static void Hint(string text)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text);
        }
    }

    private static bool HexField(string label, ref string text, ref ulong value, float width = 110f)
    {
        ImGui.SetNextItemWidth(width);
        if (!ImGui.InputText(label, ref text, 20, ImGuiInputTextFlags.CharsHexadecimal))
        {
            return false;
        }
        if (ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private void SyncHexFromSettings()
    {
        var s = _session!.Settings;
        _sealValueHex = s.SealMaterialValue.ToString("X");
        _sealMaskHex = s.SealMaterialMask.ToString("X");
        _floorHex.Clear();
        foreach (var f in s.FloorMaterials)
        {
            _floorHex.Add((f.Value.ToString("X"), f.Mask.ToString("X")));
        }
        _obstacleBoxHex.Clear();
        foreach (var f in s.ObstacleBoxMaterials)
        {
            _obstacleBoxHex.Add((f.Value.ToString("X"), f.Mask.ToString("X")));
        }
        _hexSynced = true;
    }

    private void DrawAutoMapSection()
    {
        var session = _session!;
        var scene = _scene!;
        var s = session.Settings;
        if (!_hexSynced)
        {
            SyncHexFromSettings();
        }
        var changed = false;
        ImGui.TextDisabled("seal boxes");
        changed |= HexField("value##seal", ref _sealValueHex, ref s.SealMaterialValue);
        Hint("Material value of the entrance/exit seal box colliders (0x2400 in every zone seen so far)");
        ImGui.SameLine();
        changed |= HexField("mask##seal", ref _sealMaskHex, ref s.SealMaterialMask);
        Hint("Material mask of the seal boxes (0x1FFFFFFFFF = designer-placed CollisionBox instances)");
        ImGui.SameLine();
        changed |= ImGui.Checkbox("exact mask", ref s.SealRequireExactMask);
        Hint("Require the box's mask to equal the value above, not just its material bits - filters out model colliders that happen to share the material");
        ImGui.SetNextItemWidth(80f);
        changed |= ImGui.InputFloat("pair max distance", ref s.SealPairMaxDistance, 5f, 20f, "%.0f");
        Hint("Two seals closer than this (yalms) are offered as an entrance/exit pair for the centre estimate");
        ImGui.SameLine();
        changed |= ImGui.Checkbox("include inactive", ref s.SealIncludeInactive);
        Hint("Also list seal boxes whose layout instance is inactive by default (seals that only appear once a fight starts)");
        changed |= ImGui.Checkbox("geometry filter (thin, wide doors only)", ref s.SealGeometryFilter);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"thickness <= {s.SealMaxThickness:0.#}, width >= {s.SealMinWidth:0.#}, width >= {s.SealMinWidthToHeight:0.##} x height; cubes and tall barriers with the seal material are skipped");
        }
        if (changed)
        {
            session.DetectSeals();
            session.ChooseDefaultPair(session.CentreValid ? session.Centre : null);
            MarkResultDirty();
        }

        ImGui.TextDisabled($"detected seals: {session.Seals.Count}");
        _hoveredSeal = -1;
        if (session.Seals.Count > 0)
        {
            using var table = ImRaii.Table("##seals", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY, new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * Math.Min(6, session.Seals.Count + 1)));
            if (table)
            {
                ImGui.TableSetupColumn("use");
                ImGui.TableSetupColumn("#");
                ImGui.TableSetupColumn("centre");
                ImGui.TableSetupColumn("size");
                ImGui.TableSetupColumn("zoom");
                ImGui.TableHeadersRow();
                for (var i = 0; i < session.Seals.Count; ++i)
                {
                    var seal = session.Seals[i];
                    var box = scene.Boxes[seal.BoxIndex];
                    using var id = ImRaii.PushId(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var use = session.ActiveSeals.Contains(seal.BoxIndex);
                    if (ImGui.Checkbox("##use", ref use))
                    {
                        if (use)
                        {
                            session.ActiveSeals.Add(seal.BoxIndex);
                        }
                        else
                        {
                            session.ActiveSeals.Remove(seal.BoxIndex);
                        }
                        MarkResultDirty();
                    }
                    ImGui.TableNextColumn();
                    var pair = i == session.LastEstimate.SealA || i == session.LastEstimate.SealB;
                    ImGui.Selectable($"{i}{(pair ? " *" : "")}", false, ImGuiSelectableFlags.SpanAllColumns);
                    if (ImGui.IsItemHovered())
                    {
                        _hoveredSeal = i;
                        ImGui.SetTooltip($"layout 0x{box.LayoutObjectId:X16} layer '{scene.Layers[box.LayerIndex].Name}' mat {box.MatValue:X}/{box.MatMask:X} active-by-default {box.ActiveByDefault}\n* = pair used for the centre estimate");
                    }
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"({seal.Center.X:f1}, {seal.Center.Z:f1}) y {seal.Center.Y:f1}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{box.HalfExtents.X * 2f:f1} x {box.HalfExtents.Z * 2f:f1}, h {box.HalfExtents.Y * 2f:f1}");
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton("go"))
                    {
                        _canvas.Center = new(seal.Center.X, seal.Center.Z);
                    }
                }
            }
        }

        ImGui.Separator();
        ImGui.TextDisabled("pairing");
        var pairLabel = session.PairIndex >= 0 && session.PairIndex < session.Pairs.Count ? PairLabel(session.Pairs[session.PairIndex]) : "(none)";
        ImGui.SetNextItemWidth(-1f);
        using (var combo = ImRaii.Combo("##pair", pairLabel))
        {
            if (combo)
            {
                for (var i = 0; i < session.Pairs.Count; ++i)
                {
                    if (ImGui.Selectable(PairLabel(session.Pairs[i]), i == session.PairIndex))
                    {
                        session.PairIndex = i;
                        session.EstimateCentre(session.CentreValid ? session.Centre : null);
                        MarkResultDirty();
                    }
                }
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Seal pairs in layout order: the first boss room is usually the lowest pair, the last boss has a single seal (pair it with an exit range marker when one exists)");
        }

        ImGui.Separator();
        ImGui.TextDisabled("centre");
        var centre = new Vector2(session.Centre.X, session.Centre.Z);
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputFloat2("X / Z##centre", ref centre))
        {
            var top = session.PickTriangle(centre, session.Centre.Y);
            session.SetCentre(new(centre.X, top >= 0 ? scene.Triangles[top].YAt(centre.X, centre.Y) : session.Centre.Y, centre.Y));
            MarkResultDirty();
        }
        Hint("Where the flood fill starts and which polygon is kept; the Y follows the floor nearest the current height under the new XZ");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        var centreY = session.Centre.Y;
        if (ImGui.InputFloat("Y##centre", ref centreY, 0.5f, 5f, "%.2f"))
        {
            session.SetCentre(new(session.Centre.X, centreY, session.Centre.Z));
            MarkResultDirty();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Height the fill seeds from: the floor triangle under the centre nearest this Y is the seed. Use the level list to pick a stacked surface (floor vs ceiling slab)");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        using (var combo = ImRaii.Combo("level", $"y {session.Centre.Y:f1}"))
        {
            if (combo)
            {
                foreach (var (y, label) in LevelsAtCentre(session))
                {
                    if (ImGui.Selectable(label, MathF.Abs(y - session.Centre.Y) < 0.05f))
                    {
                        session.SetCentre(new(session.Centre.X, y, session.Centre.Z));
                        MarkResultDirty();
                    }
                }
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Walkable surfaces stacked under the centre, top first");
        }
        if (ImGui.Button("From seals"))
        {
            session.EstimateCentre(session.CentreValid ? session.Centre : null);
            MarkResultDirty();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{session.LastEstimate.Reason}; seals {session.LastEstimate.SealA}/{session.LastEstimate.SealB}, ray gap {session.LastEstimate.RayGap:f2}");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(scene.TerritoryId != Service.ClientState.TerritoryType || Service.ObjectTable.LocalPlayer == null))
        {
            if (ImGui.Button("From player"))
            {
                session.SetCentre(Service.ObjectTable.LocalPlayer!.Position);
                MarkResultDirty();
            }
            Hint("Use your current position (only while standing in the loaded zone)");
        }
        ImGui.SameLine();
        var centreTool = _tool == Tool.Centre;
        if (ImGui.Checkbox("Drag on canvas", ref centreTool))
        {
            SetCentreTool(centreTool);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Next click/drag on the canvas moves the centre, then the previous tool returns (key 4 / Esc). Dragging the crosshair itself works in every tool");
        }
        ImGui.SameLine();
        if (ImGui.Button("Go"))
        {
            _canvas.Center = new(session.Centre.X, session.Centre.Z);
        }
        ImGui.TextDisabled($"estimate: {session.LastEstimate.Reason}");

        ImGui.Separator();
        ImGui.TextDisabled("floor materials (triangles and boxes matching any entry are walkable)");
        var floorChanged = false;
        for (var i = 0; i < _floorHex.Count; ++i)
        {
            using var id = ImRaii.PushId(i);
            var (value, mask) = _floorHex[i];
            ulong v = 0, m = 0;
            var edited = false;
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputText("##v", ref value, 20, ImGuiInputTextFlags.CharsHexadecimal))
            {
                edited = true;
            }
            Hint("Floor material value (hex); 7000 with mask F000 = the whole 0x7xxx floor family");
            ImGui.SameLine();
            ImGui.TextUnformatted("/");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputText("##m", ref mask, 20, ImGuiInputTextFlags.CharsHexadecimal))
            {
                edited = true;
            }
            Hint("Mask applied to the triangle/box material before comparing; 0 = exact match");
            ImGui.SameLine();
            if (ImGui.SmallButton("x") && _floorHex.Count > 1)
            {
                _floorHex.RemoveAt(i);
                s.FloorMaterials.RemoveAt(i);
                floorChanged = true;
                break;
            }
            if (edited)
            {
                _floorHex[i] = (value, mask);
                if (ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) && ulong.TryParse(mask.Length == 0 ? "0" : mask, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out m))
                {
                    s.FloorMaterials[i] = new(v, m);
                    floorChanged = true;
                }
            }
        }
        ImGui.SetNextItemWidth(90f);
        ImGui.InputText("##nv", ref _newFloorValueHex, 20, ImGuiInputTextFlags.CharsHexadecimal);
        ImGui.SameLine();
        ImGui.TextUnformatted("/");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputText("##nm", ref _newFloorMaskHex, 20, ImGuiInputTextFlags.CharsHexadecimal);
        ImGui.SameLine();
        if (ImGui.SmallButton("add floor material") && ulong.TryParse(_newFloorValueHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nv) && ulong.TryParse(_newFloorMaskHex.Length == 0 ? "0" : _newFloorMaskHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var nm))
        {
            s.FloorMaterials.Add(new(nv, nm));
            _floorHex.Add((_newFloorValueHex, _newFloorMaskHex));
            floorChanged = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("e.g. 700E / FFFF for transparent floor boxes; hover a triangle or box on the canvas to read its material");
        }
        var mode = (int)s.FloorMatchMode;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.Combo("match##floor", ref mode, "EffectiveMasked\0EffectiveExact\0PrimMasked\0PrimExact\0"))
        {
            s.FloorMatchMode = (CollisionOutlinesExtractor.MaterialMatchMode)mode;
            floorChanged = true;
        }
        Hint("Effective = the triangle material after the mesh instance's material override is applied (what the game uses); Prim = the raw triangle material. Masked applies the entry's mask, Exact compares the whole value");
        ImGui.SetNextItemWidth(90f);
        floorChanged |= ImGui.SliderFloat("max slope", ref s.MaxSlopeDeg, 5f, 89f, "%.0f deg");
        Hint("Steepest triangle the flood fill still treats as walkable floor; steeper ones are obstacles (or rim/wall-snap candidates)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        floorChanged |= ImGui.SliderFloat("radius", ref s.MaxRadius, 5f, 300f, "%.0f");
        Hint("Only triangles within this distance of the centre take part in the fill - shrink it when the fill leaks through a corridor");
        var adjacency = (int)s.Adjacency;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.Combo("adjacency", ref adjacency, "Shared edge\0Shared vertex\0"))
        {
            s.Adjacency = (AdjacencyMode)adjacency;
            floorChanged = true;
        }
        Hint("How the fill walks between triangles: shared edge is strict (fails across T-junctions), shared vertex also crosses corner contacts and un-welded seams");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        floorChanged |= ImGui.InputFloat("step", ref s.StepHeight, 0.1f, 0.5f, "%.2f");
        Hint("Link floor triangles across a vertical step up to this height (raised plates, kerbs, checkerboard tiles); vertices are welded in XZ and the Y difference is checked per link, so a ceiling slab above the floor is never linked. 0 = exact 3D weld");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        ImGui.InputFloat("seam", ref s.SeamClose, 0.01f, 0.05f, "%.2f");
        Hint("Closing radius applied to the floor union: fuses hairline slits between plates and tiles whose outlines do not coincide exactly (they show up as 1px lines into the arena and become real notches once the bounds are offset inwards)");
        ImGui.SameLine();
        var sealBlock = (int)s.SealBlock;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.Combo("seal block", ref sealBlock, "Behind plane\0Centroid\0Any vertex\0"))
        {
            s.SealBlock = (SealBlockMode)sealBlock;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Behind plane: only triangles entirely on the far side of a seal are blocked; triangles straddling the seal stay floor and the seal cuts them as a wall");
        }
        ImGui.Checkbox("cut obstacles", ref s.CutObstacles);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Walls and props are separate meshes standing on the floor; their footprints are cut out of the arena polygon (thin strips for vertical faces)");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        ImGui.InputFloat("inflate", ref s.ObstacleInflate, 0.01f, 0.05f, "%.2f");
        Hint("Half-width of the strip cut around each obstacle triangle outline (yalms); vertical walls have no footprint of their own, so this is what carves them. Wall-snapped vertices are moved back onto the wall line afterwards");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        ImGui.InputFloat("above", ref s.ObstacleHeightAbove, 0.5f, 1f, "%.1f");
        Hint("Obstacles starting more than this high above the floor under them are ignored (ceilings, bridges, hanging props)");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        ImGui.InputFloat("below", ref s.ObstacleHeightBelow, 0.5f, 1f, "%.1f");
        Hint("Obstacles whose top is more than this below the floor under them are ignored (geometry under the floor)");
        ImGui.SameLine();
        ImGui.Checkbox("local", ref s.ObstacleLocalHeight);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Measure above/below against the selected floor directly under each obstacle instead of the whole selection's height range - keeps bridges and upper galleries from being projected onto the arena in multi-level rooms");
        }
        ImGui.Checkbox("cut boxes", ref s.CutBoxes);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Props without a collision mesh are box colliders: a designer-placed CollisionBox (mask 1FFFFFFFFF, material 0x2000 = the wall material) usually paired with the model's own analytic box (mask FFFFFFFF, material 0x3005). Boxes matching the list below that stand on the selected floor are cut out of the arena. Hover a box on the canvas to read its material and id; B (or a Pick click) switches one box between cut and ignored");
        }
        ImGui.SameLine();
        ImGui.TextDisabled("obstacle box materials");
        for (var i = 0; i < _obstacleBoxHex.Count; ++i)
        {
            using var id = ImRaii.PushId(1000 + i);
            var (value, mask) = _obstacleBoxHex[i];
            var edited = false;
            ImGui.SetNextItemWidth(90f);
            edited |= ImGui.InputText("##ov", ref value, 20, ImGuiInputTextFlags.CharsHexadecimal);
            ImGui.SameLine();
            ImGui.TextUnformatted("/");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f);
            edited |= ImGui.InputText("##om", ref mask, 20, ImGuiInputTextFlags.CharsHexadecimal);
            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
            {
                _obstacleBoxHex.RemoveAt(i);
                s.ObstacleBoxMaterials.RemoveAt(i);
                MarkResultDirty();
                break;
            }
            if (edited)
            {
                _obstacleBoxHex[i] = (value, mask);
                if (ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && ulong.TryParse(mask.Length == 0 ? "0" : mask, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var m))
                {
                    s.ObstacleBoxMaterials[i] = new(v, m);
                    MarkResultDirty();
                }
            }
        }
        ImGui.SetNextItemWidth(90f);
        ImGui.InputText("##onv", ref _newObstacleBoxValueHex, 20, ImGuiInputTextFlags.CharsHexadecimal);
        ImGui.SameLine();
        ImGui.TextUnformatted("/");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.InputText("##onm", ref _newObstacleBoxMaskHex, 20, ImGuiInputTextFlags.CharsHexadecimal);
        ImGui.SameLine();
        if (ImGui.SmallButton("add box material") && ulong.TryParse(_newObstacleBoxValueHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var onv) && ulong.TryParse(_newObstacleBoxMaskHex.Length == 0 ? "0" : _newObstacleBoxMaskHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var onm))
        {
            s.ObstacleBoxMaterials.Add(new(onv, onm));
            _obstacleBoxHex.Add((_newObstacleBoxValueHex, _newObstacleBoxMaskHex));
            MarkResultDirty();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Default 2000/F000 = the wall material family (designer-placed collision). Add 3000/F000 to also cut the model colliders when a prop has no wall box");
        }
        ImGui.SetNextItemWidth(70f);
        ImGui.InputFloat("rim extension", ref s.RimExtension, 0.25f, 0.5f, "%.2f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The player's wall collision stops the character before the floor's steep rim: extend the floor across non-wall slopes adjoining the selection edge, up to this distance (the player collision radius), so the wall cut defines the edge. 0 = off");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        ImGui.InputFloat("rim max slope", ref s.RimMaxSlopeDeg, 5f, 10f, "%.0f");
        Hint("Triangles steeper than this are walls (never rim, and what the rim/wall snap reach for); between 'max slope' and this they are rim slopes");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60f);
        ImGui.InputInt("rim hops", ref s.RimHops, 1, 1);
        Hint("How many triangles outward the rim may walk from the selection edge (each hop must stay inside the rim band)");
        ImGui.SameLine();
        ImGui.Checkbox("reach wall", ref s.RimRequireWall);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Only extend across slopes whose chain touches a wall steeper than 'rim max slope' - stops rocky cave floors from growing through open slopes");
        }
        ImGui.SetNextItemWidth(70f);
        ImGui.InputFloat("wall snap", ref s.WallSnap, 0.1f, 0.5f, "%.2f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Orphan floor edges (no walkable neighbour) with a wall within this distance are extended to the wall foot line and its corner vertices: the player's collision stops at the wall, so the unwalkable sliver in between (a steep lip, a hole in the mesh) counts as floor. 0.5 = the player hitbox radius. 0 = off");
        }
        ImGui.SameLine();
        ImGui.Checkbox("keep polygon containing centre", ref s.KeepPolygonContainingCentre);
        Hint("After the cuts split the fill into pieces, keep only the piece the centre is in (drops corridors beyond seals and leaked areas)");
        if (floorChanged)
        {
            session.Adjacency = null;
        }

        if (ImGui.Button("Run auto-map"))
        {
            RunAutoMap();
        }
        Hint("Flood-fill the walkable floor from the centre with the settings above (replaces the selection; ctrl+Z restores the previous one)");
        ImGui.SameLine();
        if (ImGui.Button("Recompute result"))
        {
            MarkResultDirty();
            _resultDirtySince = 0;
        }
        Hint("Rebuild the polygon from the current selection now (union, rim, wall snap, cuts, simplify)");
        ImGui.SameLine();
        ImGui.Checkbox("auto", ref _autoRecompute);
        Hint("Recompute automatically a quarter second after the selection or a setting changes");
        if (session.Last.Status.Length > 0)
        {
            ImGui.TextWrapped($"{session.Last.Status}; adjacency {session.Adjacency?.Candidates.Length ?? 0} candidates ({session.Adjacency?.BuildMs ?? 0} ms), fill {session.Last.FillMs} ms");
        }
    }

    // distinct walkable surfaces under the centre XZ (top first): floor, ceiling slabs, bridges
    private static List<(float y, string label)> LevelsAtCentre(ArenaMapSession session)
    {
        var scene = session.Scene;
        var tris = scene.Triangles.Span;
        var minNormalY = session.Settings.MinNormalY;
        var cx = session.Centre.X;
        var cz = session.Centre.Z;
        List<(float y, string label)> levels = [];
        for (var m = 0; m < scene.Meshes.Count; ++m)
        {
            var mesh = scene.Meshes[m];
            if (mesh.TriCount == 0 || !scene.IsMeshEnabled(m) || !mesh.WorldBounds.ContainsXZ(cx, cz))
            {
                continue;
            }
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                ref readonly var t = ref tris[i];
                if (t.NormalY < minNormalY || !t.ContainsXZ(cx, cz))
                {
                    continue;
                }
                var y = t.YAt(cx, cz);
                var dup = false;
                foreach (var l in levels)
                {
                    if (MathF.Abs(l.y - y) < 0.2f)
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                {
                    levels.Add((y, $"y {y:f1}  {t.Effective:X} {mesh.Name}{System.IO.Path.GetFileName(mesh.PcbPath)}"));
                }
            }
        }
        levels.Sort((a, b) => b.y.CompareTo(a.y));
        return levels;
    }

    private static readonly (string key, string what)[] KeybindRows =
    [
        ("1 - 6", "tools: Pick, Rect, Brush, Centre, Polygon, Vertex"),
        ("Tab", "cycle the stacked triangle under the cursor (the layer sticks until Tab again; rect/brush use it as their level)"),
        ("Delete", "remove the hovered triangle from the selection"),
        ("G / C", "grow the selection 1 hop / to the whole connected floor from the last click"),
        ("V", "copy the last clicked vertex as new(x, z)"),
        ("X / I", "exclude the last clicked mesh / mark it as floor"),
        ("B", "toggle the hovered box: obstacle box cut/ignored, floor box selected/unselected"),
        ("F", "fit the canvas to the selection (or the scene)"),
        ("ctrl+Z / ctrl+Y", "undo / redo selection changes"),
        ("Esc", "cancel the polygon draft or the one-shot centre tool"),
        ("Backspace / Enter", "polygon tool: undo the last vertex / close the polygon"),
        ("shift / ctrl + click", "Pick: add / remove instead of toggle"),
        ("alt / alt+shift + click", "Pick: exclude the mesh / mark the mesh as floor"),
        ("ctrl + drag", "Rect/Brush: remove instead of add; Centre: snap to 0.5"),
        ("wheel / shift+wheel", "zoom about the cursor / brush radius"),
        ("middle, right or space + drag", "pan"),
    ];

    private string PairLabel(in SealPair p)
    {
        var session = _session!;
        var scene = _scene!;
        if (p.SealB >= 0)
        {
            return $"seals {p.SealA} + {p.SealB} ({p.Distance:0} yalms apart)";
        }
        if (p.MarkerIndex >= 0 && p.MarkerIndex < scene.Markers.Count)
        {
            return $"seal {p.SealA} + {scene.Markers[p.MarkerIndex].TypeName} {p.MarkerIndex} ({p.Distance:0} yalms)";
        }
        var seal = session.Seals[p.SealA];
        return $"seal {p.SealA} alone at ({seal.Center.X:0}, {seal.Center.Z:0})";
    }

    private void RunAutoMap()
    {
        if (_session == null)
        {
            return;
        }
        HashSet<int> previous = [.. _selection.Selected];
        _session.AutoMap();
        _selection.RecordExternal(previous, "auto-map");
        MarkResultDirty();
        _resultDirtySince = 0;
    }

    private void DrawSelectionSection()
    {
        var session = _session!;
        var scene = _scene!;
        ImGui.TextUnformatted($"selected {_selection.Selected.Count} triangle(s) + {session.SelectedFloorBoxes.Count} floor box(es), auto-map result {session.LastAutoResult.Count}");
        if (ImGui.RadioButton("Pick (1)", _tool == Tool.Pick))
        {
            _tool = Tool.Pick;
        }
        Hint("Click toggles a triangle, shift adds, ctrl removes, alt+click excludes its mesh, alt+shift+click marks its mesh as floor; Tab cycles stacked triangles (the layer sticks until Tab again); Delete removes the hovered one; a click on a box with no triangle under the cursor toggles the box");
        ImGui.SameLine();
        if (ImGui.RadioButton("Rect (2)", _tool == Tool.Rect))
        {
            _tool = Tool.Rect;
        }
        Hint("Drag adds triangles, ctrl+drag removes; the Tab layer under the cursor when the drag starts is the level it works on");
        ImGui.SameLine();
        if (ImGui.RadioButton("Brush (3)", _tool == Tool.Brush))
        {
            _tool = Tool.Brush;
        }
        Hint("Paint adds triangles, ctrl paints removal, shift+wheel changes the radius; the Tab layer under the cursor when the stroke starts is the level it works on");
        ImGui.SameLine();
        if (ImGui.RadioButton("Centre (4)", _tool == Tool.Centre))
        {
            SetCentreTool(true);
        }
        Hint("The next click/drag moves the centre, then the previous tool returns; ctrl snaps to 0.5. The crosshair itself can be dragged in any tool");
        ImGui.SameLine();
        if (ImGui.RadioButton("Polygon (5)", _tool == Tool.Polygon))
        {
            _tool = Tool.Polygon;
        }
        Hint("Click places vertices (snapped to mesh vertices), click the first vertex or Enter closes, Backspace undoes the last vertex, Esc cancels; the polygon cuts or unions per the checkbox below");
        ImGui.SameLine();
        if (ImGui.RadioButton("Vertex (6)", _tool == Tool.Vertex))
        {
            _tool = Tool.Vertex;
        }
        Hint("Click a result vertex to delete it (its neighbours connect directly), click a red deleted vertex to restore it; deletions survive recomputes and are saved with the project");
        using (var keys = ImRaii.TreeNode("Keybinds###keybinds"))
        {
            if (keys)
            {
                ImGui.TextDisabled("keys are polled raw, so the game may react to them as well; 'last click' = the triangle/vertex of the last left click on the canvas");
                using var keyTable = ImRaii.Table("##keybinds", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
                if (keyTable)
                {
                    foreach (var (key, what) in KeybindRows)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(key);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(what);
                    }
                }
            }
        }
        if (_tool is Tool.Rect or Tool.Brush)
        {
            ImGui.Checkbox("floor materials only", ref _selectFloorOnly);
            Hint("Off: rect/brush add any triangle with a walkable slope, whatever its material (the result is computed from the selection, selected triangles are never cut as obstacles)");
            ImGui.SameLine();
            ImGui.Checkbox("current layer only", ref _selectLayerOnly);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Only the stacked surface nearest in height to the Tab layer under the cursor at the start of the drag/stroke is affected; off = every level under the rect/brush");
            }
        }
        if (_tool == Tool.Vertex)
        {
            ImGui.TextUnformatted($"{_pipeline.DeletedVertices.Count} deleted vertex(es)");
            ImGui.SameLine();
            using (ImRaii.Disabled(_pipeline.DeletedVertices.Count == 0))
            {
                if (ImGui.Button("Restore all"))
                {
                    _pipeline.DeletedVertices.Clear();
                    _pipeline.RebuildSimplified();
                }
                Hint("Put every deleted vertex back");
            }
        }
        if (_tool == Tool.Brush)
        {
            ImGui.SetNextItemWidth(120f);
            ImGui.SliderFloat("brush radius", ref _brushRadius, 0.25f, 50f, "%.1f");
            Hint("Radius of the brush in yalms (shift+wheel over the canvas changes it too)");
        }
        if (_tool == Tool.Polygon)
        {
            ImGui.Checkbox("polygon cuts (difference)", ref _polygonDraftDifference);
            Hint("New polygons are cut out of the arena; untick to union them in (extra floor the meshes do not carry)");
            ImGui.SameLine();
            ImGui.TextDisabled($"{_manualPolygons.Count} manual polygon(s)");
            for (var i = 0; i < _manualPolygons.Count; ++i)
            {
                using var id = ImRaii.PushId(i);
                if (ImGui.SmallButton("x"))
                {
                    _manualPolygons.RemoveAt(i);
                    MarkResultDirty();
                    break;
                }
                ImGui.SameLine();
                var diff = _manualPolygons[i].Difference;
                if (ImGui.Checkbox("cut", ref diff))
                {
                    _manualPolygons[i].Difference = diff;
                    MarkResultDirty();
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"polygon {i}: {_manualPolygons[i].Vertices.Count} vertices");
            }
        }

        var anchorMesh = _anchorTri >= 0 ? scene.Meshes[scene.Triangles[_anchorTri].MeshIndex] : null;
        ImGui.TextDisabled(_anchorTri >= 0 ? $"last click: triangle {_anchorTri} of {(anchorMesh!.Name.Length > 0 ? anchorMesh.Name : System.IO.Path.GetFileNameWithoutExtension(anchorMesh.PcbPath))}" : "last click: none (click a triangle on the canvas)");
        using (ImRaii.Disabled(_anchorTri < 0))
        {
            if (ImGui.Button("Grow 1 hop (G)"))
            {
                GrowFromAnchor(1);
            }
            Hint("Add the walkable triangles adjacent to the last clicked one");
            ImGui.SameLine();
            if (ImGui.Button("Connected floor (C)"))
            {
                GrowFromAnchor(int.MaxValue);
            }
            Hint("Add every walkable triangle connected to the last clicked one (a flood fill from there, seals and slope still apply)");
            ImGui.SameLine();
            var excluded = anchorMesh != null && _meshModes[anchorMesh.Index] == MeshMode.Exclude;
            if (ImGui.Button(excluded ? "Un-exclude mesh (X)" : "Exclude mesh (X)"))
            {
                ToggleExcludeAnchorMesh();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Excluded meshes are removed from the selection and no longer cut the arena as obstacles - use it for decoration the player never touches");
            }
            ImGui.SameLine();
            var forced = anchorMesh != null && _meshModes[anchorMesh.Index] == MeshMode.Include;
            if (ImGui.Button(forced ? "Unmark floor mesh (I)" : "Mark mesh as floor (I)"))
            {
                ToggleFloorAnchorMesh();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The mesh counts as floor whatever its material: its walkable triangles are selected, the flood fill can cross it and it is never cut as an obstacle - use it where auto-map missed part of the arena");
            }
        }
        using (ImRaii.Disabled(_selection.Selected.Count == 0))
        {
            if (ImGui.Button("Clear"))
            {
                _selection.Clear();
            }
            Hint("Deselect every triangle (undoable)");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!_selection.CanUndo))
        {
            if (ImGui.Button($"Undo (ctrl+Z) {_selection.UndoCount}"))
            {
                _selection.Undo();
            }
            Hint("Undo the last selection change (auto-map, rect, brush, grow, clear...)");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!_selection.CanRedo))
        {
            if (ImGui.Button("Redo (ctrl+Y)"))
            {
                _selection.Redo();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Fit (F)"))
        {
            FitToSelectionOrScene();
        }
        Hint("Fit the canvas to the selection, or to the whole scene when nothing is selected");
        if (_anchorVertexValid)
        {
            ImGui.TextUnformatted($"last clicked vertex ({_anchorVertex.X:f3}, {_anchorVertex.Y:f3}, {_anchorVertex.Z:f3})");
            ImGui.SameLine();
            if (ImGui.SmallButton("copy (V)"))
            {
                CopyAnchorVertex(false);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("copy Vector3"))
            {
                CopyAnchorVertex(true);
            }
            Hint("Copy the last clicked vertex as new(x, y, z)");
        }
        if (_snapValid)
        {
            ImGui.TextDisabled($"hovered vertex ({_snapVertex.X:f3}, {_snapVertex.Y:f3}, {_snapVertex.Z:f3})");
        }
        ImGui.Checkbox("triangles", ref _showAllTriangles);
        Hint("Draw every mesh triangle near the view (floor-material triangles tinted); below the budget only mesh boxes are drawn");
        ImGui.SameLine();
        ImGui.Checkbox("selection", ref _showSelected);
        Hint("Draw the selected triangles (green), the auto-map result you removed (red) and the rim slopes (orange)");
        ImGui.SameLine();
        ImGui.Checkbox("seals", ref _showSeals);
        Hint("Draw the seal box footprints and the inward arrows of the chosen pair");
        ImGui.SameLine();
        ImGui.Checkbox("result", ref _showResult);
        Hint("Draw the simplified result contours (what the snippet emits)");
        ImGui.SameLine();
        ImGui.Checkbox("raw", ref _showRawOutline);
        Hint("Also draw the raw union outline before simplification");
        ImGui.SameLine();
        ImGui.Checkbox("markers", ref _showMarkers);
        Hint("Draw exit ranges, pop ranges and event objects from the layout");
        var inZone = PlayerInLoadedZone() != null;
        using (ImRaii.Disabled(!inZone))
        {
            ImGui.Checkbox("player", ref _showPlayer);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Your position and facing on the canvas (only while standing in the loaded zone)");
            }
            ImGui.SameLine();
            ImGui.Checkbox("preview in world", ref _showWorldPreview);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Draw the result contours in the 3D world (green outers, red holes) while standing in the loaded zone; they follow every recompute and the vertex edits");
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70f);
            ImGui.InputFloat("lift", ref _worldPreviewLift, 0.1f, 0.5f, "%.1f");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Height above the recovered floor the world preview is drawn at");
            }
        }
        if (!inZone)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(enter the loaded zone for the player marker and the world preview)");
        }
        ImGui.TextDisabled($"drawn {_trianglesDrawn} triangles (budget {TriangleBudget}), zoom {_canvas.Zoom:f1} px/yalm");

        using var meshes = ImRaii.TreeNode("Meshes near the centre###meshes");
        if (!meshes)
        {
            return;
        }
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##meshfilter", "filter by name/path", ref _meshFilter, 64);
        using var table = ImRaii.Table("##meshtable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY, new Vector2(-1f, ImGui.GetTextLineHeightWithSpacing() * 9f));
        if (!table)
        {
            return;
        }
        ImGui.TableSetupColumn("mesh");
        ImGui.TableSetupColumn("tris");
        ImGui.TableSetupColumn("sel");
        ImGui.TableSetupColumn("mode");
        ImGui.TableSetupColumn("go");
        ImGui.TableHeadersRow();
        var cxz = new Vector2(session.Centre.X, session.Centre.Z);
        var shown = 0;
        for (var m = 0; m < scene.Meshes.Count && shown < 200; ++m)
        {
            var mesh = scene.Meshes[m];
            if (mesh.TriCount == 0 || !mesh.WorldBounds.IntersectsXZCircle(cxz, session.Settings.MaxRadius))
            {
                continue;
            }
            var name = mesh.Name.Length > 0 ? mesh.Name : System.IO.Path.GetFileNameWithoutExtension(mesh.PcbPath);
            if (_meshFilter.Length > 0 && !name.Contains(_meshFilter, StringComparison.OrdinalIgnoreCase) && !mesh.PcbPath.Contains(_meshFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            ++shown;
            using var id = ImRaii.PushId(m);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{mesh.PcbPath}\nlayout 0x{mesh.LayoutObjectId:X16} layer '{scene.Layers[mesh.LayerIndex].Name}' objmat {mesh.ObjMatValue:X}/{mesh.ObjMatMask:X}");
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mesh.TriCount.ToString());
            ImGui.TableNextColumn();
            var selectedCount = 0;
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                if (_selection.Selected.Contains(i))
                {
                    ++selectedCount;
                }
            }
            ImGui.TextUnformatted(selectedCount.ToString());
            ImGui.TableNextColumn();
            var mode = (int)_meshModes[m];
            ImGui.SetNextItemWidth(80f);
            if (ImGui.Combo("##mode", ref mode, "Auto\0Floor\0Exclude\0"))
            {
                SetMeshMode(m, (MeshMode)mode);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Floor: counts as floor whatever its material (selected, flood-fill crosses it, never cut). Exclude: deselected and never cut. Auto: material decides");
            }
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("go"))
            {
                _canvas.FitBounds(mesh.WorldBounds.Min, mesh.WorldBounds.Max);
            }
        }
    }

    private void DrawResultSection()
    {
        var session = _session!;
        var outers = _pipeline.EnabledContourCounts(out var holes);
        var busy = _recomputeTask != null;
        ImGui.TextWrapped($"raw {_pipeline.Raw.Count} polygon(s), {_pipeline.RawVertexCount} verts | enabled {outers} outer(s), {holes} hole(s), {_pipeline.SimpVertexCount} verts | union {_lastRecomputeMs} ms ({_lastObstacleTriangles} obstacle tris + {_lastObstacleBoxes} boxes cut, {_lastRimTriangles} rim tris extended, {_lastWallSnapEdges} edges snapped to walls), simplify {_pipeline.SimplifyMs} ms{(busy ? " | computing..." : _resultDirty ? " | pending" : "")}");
        if (_pipeline.Status.Length > 0)
        {
            ImGui.TextDisabled(_pipeline.Status);
        }
        if (_pipeline.KeepStatus.Length > 0)
        {
            ImGui.TextDisabled(_pipeline.KeepStatus);
        }
        _pipeline.DrawSimplifyControls();
        _pipeline.DrawPreviewTable();
        _pipeline.DrawCodegenControls();
        _pipeline.DrawCodegenButtons(ProvenanceHeader);
        _pipeline.DrawApplyButtons(_bmm);
        _pipeline.SnippetPreviewBox(ProvenanceHeader);
        if (session.Polygons.Count == 0 && _selection.Selected.Count > 0 && !busy && !_resultDirty)
        {
            ImGui.TextDisabled("no polygon: the selection may be entirely inside cut footprints; check 'cut obstacles' and the seal list");
        }
    }

    private string ProvenanceHeader()
    {
        var scene = _scene!;
        var session = _session!;
        var s = session.Settings;
        var simplify = _pipeline.LastSimplify;
        var zone = _zones.Find(z => z.TerritoryId == scene.TerritoryId);
        var sb = new StringBuilder();
        sb.Append($"// arena from zone arena editor: territory {scene.TerritoryId} ({zone.Place}{(zone.Cfc.Length > 0 ? $" / {zone.Cfc}" : "")}), bg {scene.Bg}, centre ({CollisionArenaCodeGen.F(session.Centre.X, 2)}, {CollisionArenaCodeGen.F(session.Centre.Z, 2)})");
        sb.AppendLine();
        var meshCount = 0;
        HashSet<int> meshes = [];
        foreach (var t in _selection.Selected)
        {
            meshes.Add(scene.Triangles[t].MeshIndex);
        }
        meshCount = meshes.Count;
        var floors = new StringBuilder();
        foreach (var f in s.FloorMaterials)
        {
            floors.Append(floors.Length > 0 ? ", " : "").Append("0x").Append(f.ToString());
        }
        sb.Append($"// {_selection.Selected.Count} triangles from {meshCount} meshes + {session.SelectedFloorBoxes.Count} floor box(es), floor {floors} ({s.FloorMatchMode}), slope {s.MaxSlopeDeg:0}, epsilon {simplify.Epsilon.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (simplify.Offset != 0f)
        {
            sb.Append($", offset {simplify.Offset.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        sb.Append($", {session.ActiveSeals.Count} seal box(es) cut, obstacles {(s.CutObstacles ? "cut" : "kept")}, {_manualPolygons.Count} manual polygon(s)");
        sb.AppendLine();
        return sb.ToString();
    }
}
