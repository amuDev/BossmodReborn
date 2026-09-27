using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.IO;
using System.Text.Json;

namespace BossMod;

public sealed partial class ZoneArenaEditorWindow
{
    private string _projectName = "";
    private string _projectStatus = "";
    private ZoneArenaProjectFile? _projectFile;
    private uint _projectFileTerritory = uint.MaxValue;
    private bool _projectFileBroken; // the file on disk did not parse: it is kept as .bak by the next write
    private string _projectFileSummary = "";
    private readonly List<(string name, string summary)> _projectRows = [];
    private string _deleteArmed = "";
    private long _deleteArmedAt;

    private string ProjectPath(uint territory) => Path.Combine(_dalamud.ConfigDirectory.FullName, "zone-arenas", $"{territory}.json");

    private ZoneArenaProjectFile LoadProjectFile(uint territory)
    {
        if (_projectFile != null && _projectFileTerritory == territory)
        {
            return _projectFile;
        }
        _projectFileTerritory = territory;
        _projectFileBroken = false;
        _projectFile = new ZoneArenaProjectFile { Territory = territory };
        var path = ProjectPath(territory);
        if (File.Exists(path))
        {
            try
            {
                _projectFile = JsonSerializer.Deserialize<ZoneArenaProjectFile>(File.ReadAllText(path), Serialization.BuildSerializationOptions()) ?? _projectFile;
            }
            catch (Exception ex)
            {
                _projectFileBroken = true;
                _projectStatus = $"load failed: {ex.Message}";
            }
        }
        RefreshProjectSummaries(_projectFile);
        return _projectFile;
    }

    // written to a temp file and moved over the old one, so a crash mid-write cannot leave a truncated project file
    private void WriteProjectFile(ZoneArenaProjectFile file)
    {
        var path = ProjectPath(file.Territory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (_projectFileBroken && File.Exists(path))
            {
                File.Move(path, path + ".bak", true);
                _projectFileBroken = false;
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, Serialization.BuildSerializationOptions()));
            File.Move(tmp, path, true);
            _projectStatus = $"saved {path}";
        }
        catch (Exception ex)
        {
            _projectStatus = $"save failed: {ex.Message}";
        }
        _recentZonesStale = true;
        RefreshProjectSummaries(file);
    }

    // the project list rows are built once per file load / write, not per frame
    private void RefreshProjectSummaries(ZoneArenaProjectFile file)
    {
        _projectFileSummary = $"{file.Projects.Count} project(s) for territory {file.Territory} in {ProjectPath(file.Territory)}";
        _projectRows.Clear();
        foreach (var (name, p) in file.Projects)
        {
            var added = 0;
            var removed = 0;
            foreach (var d in p.Added)
            {
                added += d.Tris.Length;
            }
            foreach (var d in p.Removed)
            {
                removed += d.Tris.Length;
            }
            _projectRows.Add((name, $"{name}{(name == file.LastProject ? " (last)" : "")}: centre ({p.CenterX:f1}, {p.CenterZ:f1}), {added} added / {removed} removed, {p.ManualPolygons.Count} manual polygon(s), {p.Scenes.Count} scene(s), {p.Rules.Count} rule(s){(p.SavedAt != default ? $", {p.SavedAt:g}" : "")}"));
        }
    }

    private static string Hex(ulong v) => v.ToString("X");

    private void SaveProject(string name, bool remember = true)
    {
        FlushPendingSeek();
        var scene = _scene!;
        var session = _session!;
        var s = session.Settings;
        var file = LoadProjectFile(scene.TerritoryId);
        file.Bg = scene.Bg;
        if (remember)
        {
            file.LastProject = name;
        }
        var p = new ZoneArenaProject
        {
            CenterX = session.Centre.X,
            CenterY = session.Centre.Y,
            CenterZ = session.Centre.Z,
            CentreValid = session.CentreValid,
            SealMaterialValue = Hex(s.SealMaterialValue),
            SealGeometryFilter = s.SealGeometryFilter,
            SealMaterialMask = Hex(s.SealMaterialMask),
            FloorMatchMode = (int)s.FloorMatchMode,
            SealRequireExactMask = s.SealRequireExactMask,
            SealIncludeInactive = s.SealIncludeInactive,
            SealPairMaxDistance = s.SealPairMaxDistance,
            MaxRadius = s.MaxRadius,
            MaxSlopeDeg = s.MaxSlopeDeg,
            WeldEps = s.WeldEps,
            SealFootprintInflate = s.SealFootprintInflate,
            ObstacleInflate = s.ObstacleInflate,
            ObstacleHeightAbove = s.ObstacleHeightAbove,
            ObstacleHeightBelow = s.ObstacleHeightBelow,
            ObstacleMinHeight = s.ObstacleMinHeight,
            Adjacency = (int)s.Adjacency,
            SealBlock = (int)s.SealBlock,
            KeepPolygonContainingCentre = s.KeepPolygonContainingCentre,
            CutObstacles = s.CutObstacles,
            RimExtension = s.RimExtension,
            RimMaxSlopeDeg = s.RimMaxSlopeDeg,
            RimHops = s.RimHops,
            ObstacleLocalHeight = s.ObstacleLocalHeight,
            ObstacleUnderFloor = s.ObstacleUnderFloor,
            RimRequireWall = s.RimRequireWall,
            StepHeight = s.StepHeight,
            SeamClose = s.SeamClose,
            WallSnap = s.WallSnap,
            EdgeSnap = s.EdgeSnap,
            GapBridge = s.GapBridge,
            GapBridgeRise = s.GapBridgeRise,
            ExcludeUnwalkableMaterials = s.ExcludeUnwalkableMaterials,
            ReliefPromotion = s.ReliefPromotion,
            ReliefStep = s.ReliefStep,
            SealMaxThickness = s.SealMaxThickness,
            SealMinWidth = s.SealMinWidth,
            SealMinWidthToHeight = s.SealMinWidthToHeight,
            SealBehindDepth = s.SealBehindDepth,
            BoxFloorTouchEps = s.BoxFloorTouchEps,
            BoxFloorTouchHeight = s.BoxFloorTouchHeight,
            SeedSearchRadius = s.SeedSearchRadius,
            SnapEpsXZ = s.SnapEpsXZ,
            MinArea = s.MinArea,
            ObstacleMaxTriangles = s.ObstacleMaxTriangles,
            WallSnapVertices = s.WallSnapVertices,
            AutoMapRan = session.LastAutoResult.Count > 0,
            Epsilon = _pipeline.Epsilon,
            Offset = _pipeline.Offset,
            Closing = _pipeline.Closing,
            Opening = _pipeline.Opening,
            AdjustForHitboxInwards = _pipeline.AdjustForHitboxInwards,
            AdjustForHitboxOutwards = _pipeline.AdjustForHitboxOutwards,
            Decimals = _pipeline.Decimals,
            TrimCollinear = _pipeline.TrimCollinear,
            DropAreaBelow = _pipeline.DropAreaBelow,
            ArenaFieldName = _pipeline.ArenaFieldName,
            FlatThreshold = _pipeline.FlatThreshold,
            EmitProjectionHeightZero = _pipeline.EmitProjectionHeightZero,
            LayerGap = _pipeline.LayerGap,
            TerrainRadius = _terrainRadius,
        };
        p.FloorMaterials.Clear();
        foreach (var f in s.FloorMaterials)
        {
            p.FloorMaterials.Add(f.ToString());
        }
        for (var i = 0; i < session.Seals.Count; ++i)
        {
            var box = scene.Boxes[session.Seals[i].BoxIndex];
            p.Seals.Add(new SavedSeal { LayoutId = ZoneSceneTimelineFile.FormatId(box.LayoutObjectId), NodeId = NodeHex(scene, box.NodeIndex), Use = session.ActiveSeals.Contains(box.Index) });
        }
        if (session.PairIndex >= 0 && session.PairIndex < session.Pairs.Count)
        {
            var pair = session.Pairs[session.PairIndex];
            var boxA = scene.Boxes[session.Seals[pair.SealA].BoxIndex];
            p.PairSealA = ZoneSceneTimelineFile.FormatId(boxA.LayoutObjectId);
            p.PairSealANode = NodeHex(scene, boxA.NodeIndex);
            p.PairSealB = pair.SealB >= 0 ? ZoneSceneTimelineFile.FormatId(scene.Boxes[session.Seals[pair.SealB].BoxIndex].LayoutObjectId) : "";
            p.PairSealBNode = pair.SealB >= 0 ? NodeHex(scene, scene.Boxes[session.Seals[pair.SealB].BoxIndex].NodeIndex) : "";
            p.PairMarker = pair.MarkerIndex >= 0 ? ZoneSceneTimelineFile.FormatId(scene.Markers[pair.MarkerIndex].LayoutObjectId) : "";
            p.PairMarkerNode = pair.MarkerIndex >= 0 ? NodeHex(scene, scene.Markers[pair.MarkerIndex].NodeIndex) : "";
        }
        foreach (var o in session.PairOverrides)
        {
            p.PairOverrides.Add(new SavedPairOverride { SealNode = ZoneSceneTimelineFile.FormatId(o.SealPathId), PartnerNode = o.PartnerPathId != 0 ? ZoneSceneTimelineFile.FormatId(o.PartnerPathId) : "", PartnerIsNode = o.PartnerIsNode });
        }
        foreach (var b in session.IgnoredBoxes)
        {
            p.IgnoredBoxes.Add(ZoneSceneTimelineFile.FormatId(scene.Boxes[b].LayoutObjectId));
            p.IgnoredBoxNodes.Add(NodeHex(scene, scene.Boxes[b].NodeIndex));
        }
        p.CutBoxes = s.CutBoxes;
        p.ObstacleBoxMaterials.Clear();
        foreach (var f in s.ObstacleBoxMaterials)
        {
            p.ObstacleBoxMaterials.Add(f.ToString());
        }
        foreach (var b in session.SelectedFloorBoxes)
        {
            p.FloorBoxes.Add(ZoneSceneTimelineFile.FormatId(scene.Boxes[b].LayoutObjectId));
            p.FloorBoxNodes.Add(NodeHex(scene, scene.Boxes[b].NodeIndex));
        }
        for (var m = 0; m < scene.Meshes.Count && m < _meshModes.Count; ++m)
        {
            if (_meshModes[m] != MeshMode.Auto)
            {
                p.Meshes.Add(new SavedMeshMode { Path = scene.Meshes[m].PcbPath, LayoutId = ZoneSceneTimelineFile.FormatId(scene.Meshes[m].LayoutObjectId), NodeId = NodeHex(scene, scene.Meshes[m].NodeIndex), Mode = _meshModes[m].ToString() });
            }
        }
        EnsureDefaultScene();
        ActiveScene?.State.CopyFrom(session.State);
        foreach (var sc in session.Scenes)
        {
            p.Scenes.Add(SavedScene.From(scene, sc));
        }
        p.ActiveScene = session.ActiveSceneIndex;
        foreach (var er in session.EObjRules)
        {
            p.EObjRules.Add(new SavedEObjRule { NodeId = ZoneSceneTimelineFile.FormatId(er.EObjPathId), ObjectStateChannel = er.ObjectStateChannel, State = er.State, CollisionOn = er.CollisionOn });
        }
        foreach (var r in _rules)
        {
            p.Rules.Add(r.Clone());
        }
        p.ReplayTimelinePath = _timelinePath;
        p.View = CaptureView();
        p.ModuleFilePath = _moduleFilePath;
        p.SavedAt = DateTime.Now;
        p.ResultPolygons = _pipeline.ExportEnabledPolygons(out var resultFlat, out var resultMeanY);
        p.ResultFlat = resultFlat;
        p.ResultMeanY = resultMeanY;
        // deltas against the auto-map result, grouped by mesh
        Dictionary<int, List<int>> added = [];
        Dictionary<int, List<int>> removed = [];
        foreach (var t in session.Selected)
        {
            if (!session.LastAutoResult.Contains(t))
            {
                AddDelta(added, scene, t);
            }
        }
        foreach (var t in session.LastAutoResult)
        {
            if (!session.Selected.Contains(t))
            {
                AddDelta(removed, scene, t);
            }
        }
        foreach (var (mesh, tris) in added)
        {
            p.Added.Add(MakeDelta(scene, mesh, tris));
        }
        foreach (var (mesh, tris) in removed)
        {
            p.Removed.Add(MakeDelta(scene, mesh, tris));
        }
        foreach (var poly in _pipeline.Preview)
        {
            p.ContourEnabled.Add(poly.Outer.Enabled);
            foreach (var h in poly.Holes)
            {
                p.ContourEnabled.Add(h.Enabled);
            }
        }
        foreach (var mp in _manualPolygons)
        {
            var xz = new float[mp.Vertices.Count * 2];
            for (var i = 0; i < mp.Vertices.Count; ++i)
            {
                xz[2 * i] = mp.Vertices[i].X;
                xz[2 * i + 1] = mp.Vertices[i].Z;
            }
            p.ManualPolygons.Add(new SavedManualPolygon { Difference = mp.Difference, XZ = xz, Y = [.. mp.Y] });
        }
        foreach (var v in _pipeline.DeletedVertices)
        {
            p.DeletedVertices.Add(v.X);
            p.DeletedVertices.Add(v.Z);
        }
        file.Projects[name] = p;
        WriteProjectFile(file);
    }

    private static void AddDelta(Dictionary<int, List<int>> dst, ZoneCollisionScene scene, int tri)
    {
        var mesh = scene.Triangles[tri].MeshIndex;
        if (!dst.TryGetValue(mesh, out var list))
        {
            dst[mesh] = list = [];
        }
        list.Add(tri - scene.Meshes[mesh].TriStart);
    }

    private static SavedTriangleDelta MakeDelta(ZoneCollisionScene scene, int meshIndex, List<int> localTris)
    {
        var mesh = scene.Meshes[meshIndex];
        localTris.Sort();
        return new SavedTriangleDelta { Path = mesh.PcbPath, LayoutId = ZoneSceneTimelineFile.FormatId(mesh.LayoutObjectId), NodeId = NodeHex(scene, mesh.NodeIndex), TriangleCount = mesh.TriCount, Tris = [.. localTris] };
    }

    private static string NodeHex(ZoneCollisionScene scene, int nodeIndex) => nodeIndex >= 0 ? ZoneSceneTimelineFile.FormatId(scene.Nodes[nodeIndex].PathId) : "";

    // saved references resolve by node path id first (unique across shared-group instantiations), else by layout id (+ pcb path for meshes); the layout-id lookups are dictionaries built per scene size
    private readonly Dictionary<string, int> _boxByLayoutId = [];
    private readonly Dictionary<(string path, string layoutId), int> _meshByPathAndLayout = [];
    private int _lookupBoxes = -1, _lookupMeshes = -1;

    private void EnsureLookups()
    {
        var scene = _scene!;
        if (_lookupBoxes != scene.Boxes.Count)
        {
            _boxByLayoutId.Clear();
            for (var b = 0; b < scene.Boxes.Count; ++b)
            {
                _boxByLayoutId.TryAdd(ZoneSceneTimelineFile.FormatId(scene.Boxes[b].LayoutObjectId), b);
            }
            _lookupBoxes = scene.Boxes.Count;
        }
        if (_lookupMeshes != scene.Meshes.Count)
        {
            _meshByPathAndLayout.Clear();
            for (var m = 0; m < scene.Meshes.Count; ++m)
            {
                _meshByPathAndLayout.TryAdd((scene.Meshes[m].PcbPath, ZoneSceneTimelineFile.FormatId(scene.Meshes[m].LayoutObjectId)), m);
            }
            _lookupMeshes = scene.Meshes.Count;
        }
    }

    private int FindMesh(string path, string layoutId, string nodeId = "")
    {
        var scene = _scene!;
        if (nodeId.Length > 0)
        {
            var node = scene.FindNode(ZoneSceneTimelineFile.ParseId(nodeId));
            if (node >= 0 && scene.Nodes[node].MeshIndex >= 0)
            {
                return scene.Nodes[node].MeshIndex;
            }
        }
        EnsureLookups();
        return _meshByPathAndLayout.TryGetValue((path, layoutId), out var m) ? m : -1;
    }

    private int FindBox(string layoutId, string nodeId)
    {
        var scene = _scene!;
        if (nodeId.Length > 0)
        {
            var node = scene.FindNode(ZoneSceneTimelineFile.ParseId(nodeId));
            if (node >= 0 && scene.Nodes[node].BoxIndex >= 0)
            {
                return scene.Nodes[node].BoxIndex;
            }
        }
        EnsureLookups();
        return _boxByLayoutId.TryGetValue(layoutId, out var b) ? b : -1;
    }

    private void LoadProject(string name)
    {
        var scene = _scene!;
        var session = _session!;
        var file = LoadProjectFile(scene.TerritoryId);
        if (!file.Projects.TryGetValue(name, out var p))
        {
            return;
        }
        if (p.FloorBoxes.Count != p.FloorBoxNodes.Count || p.IgnoredBoxes.Count != p.IgnoredBoxNodes.Count)
        {
            _projectStatus = $"'{name}' is malformed: its box and node id lists differ in length";
            return;
        }
        var s = session.Settings;
        s.FloorMaterials.Clear();
        foreach (var f in p.FloorMaterials)
        {
            if (TryParseMaterial(f, out var mat))
            {
                s.FloorMaterials.Add(mat);
            }
        }
        s.CutBoxes = p.CutBoxes;
        s.ObstacleBoxMaterials.Clear();
        foreach (var f in p.ObstacleBoxMaterials)
        {
            if (TryParseMaterial(f, out var mat))
            {
                s.ObstacleBoxMaterials.Add(mat);
            }
        }
        session.IgnoredBoxes.Clear();
        for (var i = 0; i < p.IgnoredBoxes.Count; ++i)
        {
            var b = FindBox(p.IgnoredBoxes[i], p.IgnoredBoxNodes[i]);
            if (b >= 0)
            {
                session.IgnoredBoxes.Add(b);
            }
        }
        if (s.FloorMaterials.Count == 0)
        {
            s.FloorMaterials.Add(new(0x7000, 0xF000));
        }
        s.SealGeometryFilter = p.SealGeometryFilter;
        s.SealMaterialValue = Convert.ToUInt64(p.SealMaterialValue, 16);
        s.SealMaterialMask = Convert.ToUInt64(p.SealMaterialMask, 16);
        s.FloorMatchMode = (MaterialMatchMode)p.FloorMatchMode;
        s.SealRequireExactMask = p.SealRequireExactMask;
        s.SealIncludeInactive = p.SealIncludeInactive;
        s.SealPairMaxDistance = p.SealPairMaxDistance;
        s.MaxRadius = p.MaxRadius;
        s.MaxSlopeDeg = p.MaxSlopeDeg;
        s.WeldEps = p.WeldEps;
        s.SealFootprintInflate = p.SealFootprintInflate;
        s.ObstacleInflate = p.ObstacleInflate;
        s.ObstacleHeightAbove = p.ObstacleHeightAbove;
        s.ObstacleHeightBelow = p.ObstacleHeightBelow;
        s.ObstacleMinHeight = p.ObstacleMinHeight;
        s.Adjacency = (AdjacencyMode)p.Adjacency;
        s.SealBlock = (SealBlockMode)p.SealBlock;
        s.KeepPolygonContainingCentre = p.KeepPolygonContainingCentre;
        s.CutObstacles = p.CutObstacles;
        s.RimExtension = p.RimExtension;
        s.RimMaxSlopeDeg = p.RimMaxSlopeDeg;
        s.RimHops = p.RimHops;
        s.ObstacleLocalHeight = p.ObstacleLocalHeight;
        s.ObstacleUnderFloor = p.ObstacleUnderFloor;
        s.RimRequireWall = p.RimRequireWall;
        s.StepHeight = p.StepHeight;
        s.SeamClose = p.SeamClose;
        s.WallSnap = p.WallSnap;
        s.EdgeSnap = p.EdgeSnap;
        s.GapBridge = p.GapBridge;
        s.GapBridgeRise = p.GapBridgeRise;
        s.ExcludeUnwalkableMaterials = p.ExcludeUnwalkableMaterials;
        s.ReliefPromotion = p.ReliefPromotion;
        s.ReliefStep = p.ReliefStep;
        s.SealMaxThickness = p.SealMaxThickness;
        s.SealMinWidth = p.SealMinWidth;
        s.SealMinWidthToHeight = p.SealMinWidthToHeight;
        s.SealBehindDepth = p.SealBehindDepth;
        s.BoxFloorTouchEps = p.BoxFloorTouchEps;
        s.BoxFloorTouchHeight = p.BoxFloorTouchHeight;
        s.SeedSearchRadius = p.SeedSearchRadius;
        s.SnapEpsXZ = p.SnapEpsXZ;
        s.MinArea = p.MinArea;
        s.ObstacleMaxTriangles = p.ObstacleMaxTriangles;
        s.WallSnapVertices = p.WallSnapVertices;
        _pipeline.Epsilon = p.Epsilon;
        _pipeline.Offset = p.Offset;
        _pipeline.Closing = p.Closing;
        _pipeline.Opening = p.Opening;
        _pipeline.AdjustForHitboxInwards = p.AdjustForHitboxInwards;
        _pipeline.AdjustForHitboxOutwards = p.AdjustForHitboxOutwards && !p.AdjustForHitboxInwards;
        _pipeline.DeletedVertices.Clear();
        for (var i = 0; i + 1 < p.DeletedVertices.Count; i += 2)
        {
            _pipeline.DeletedVertices.Add(new(p.DeletedVertices[i], p.DeletedVertices[i + 1]));
        }
        _pipeline.Decimals = p.Decimals;
        _pipeline.TrimCollinear = p.TrimCollinear;
        _pipeline.DropAreaBelow = p.DropAreaBelow;
        _pipeline.ArenaFieldName = p.ArenaFieldName;
        _pipeline.FlatThreshold = p.FlatThreshold;
        _pipeline.EmitProjectionHeightZero = p.EmitProjectionHeightZero;
        _pipeline.LayerGap = p.LayerGap;
        _terrainRadius = p.TerrainRadius;
        _moduleFilePath = p.ModuleFilePath;
        if (p.View != null)
        {
            ApplyView(p.View);
        }
        SyncHexFromSettings();

        // scenes
        session.Scenes.Clear();
        var missingSceneRefs = 0;
        foreach (var ss in p.Scenes)
        {
            session.Scenes.Add(ss.ToScene(scene, out var miss));
            missingSceneRefs += miss;
        }
        EnsureDefaultScene();
        session.EObjRules.Clear();
        foreach (var er in p.EObjRules)
        {
            session.EObjRules.Add(new(ZoneSceneTimelineFile.ParseId(er.NodeId), er.ObjectStateChannel, er.State, er.CollisionOn));
        }
        session.ActivateScene(Math.Clamp(p.ActiveScene, 0, session.Scenes.Count - 1), false);
        session.PairOverrides.Clear();
        foreach (var o in p.PairOverrides)
        {
            session.PairOverrides.Add(new(ZoneSceneTimelineFile.ParseId(o.SealNode), o.PartnerNode.Length > 0 ? ZoneSceneTimelineFile.ParseId(o.PartnerNode) : 0ul, o.PartnerIsNode));
        }
        session.DetectSeals();
        foreach (var saved in p.Seals)
        {
            var b = FindBox(saved.LayoutId, saved.NodeId);
            if (b >= 0 && session.Seals.Exists(sl => sl.BoxIndex == b))
            {
                if (saved.Use)
                {
                    session.ActiveSeals.Add(b);
                }
                else
                {
                    session.ActiveSeals.Remove(b);
                }
            }
        }
        session.PairIndex = -1;
        var wantA = FindBox(p.PairSealA, p.PairSealANode);
        var wantB = p.PairSealB.Length > 0 ? FindBox(p.PairSealB, p.PairSealBNode) : -1;
        for (var i = 0; i < session.Pairs.Count; ++i)
        {
            var pair = session.Pairs[i];
            var a = session.Seals[pair.SealA].BoxIndex;
            var b = pair.SealB >= 0 ? session.Seals[pair.SealB].BoxIndex : -1;
            var mk = pair.MarkerIndex >= 0 ? ZoneSceneTimelineFile.FormatId(scene.Markers[pair.MarkerIndex].LayoutObjectId) : "";
            if (a == wantA && b == wantB && mk == p.PairMarker)
            {
                session.PairIndex = i;
                break;
            }
        }
        session.Centre = new(p.CenterX, p.CenterY, p.CenterZ);
        session.CentreValid = p.CentreValid;
        LoadTerrainAround(new(p.CenterX, p.CenterZ));
        for (var m = 0; m < _meshModes.Count; ++m)
        {
            _meshModes[m] = MeshMode.Auto;
        }
        session.FloorMeshes.Clear();
        var missing = 0;
        foreach (var mm in p.Meshes)
        {
            var m = FindMesh(mm.Path, mm.LayoutId, mm.NodeId);
            if (m < 0)
            {
                ++missing;
                continue;
            }
            _meshModes[m] = Enum.TryParse<MeshMode>(mm.Mode, out var mode) ? mode : MeshMode.Auto;
            if (_meshModes[m] == MeshMode.Include)
            {
                session.FloorMeshes.Add(m);
            }
        }

        _selection.Reset();
        if (p.AutoMapRan)
        {
            // the flood fill runs on a task like the button does; the deltas and boxes are applied once it lands
            _pendingProjectLoad = (p, name, missing, missingSceneRefs);
            _projectStatus = $"loading '{name}': auto-map running...";
            StartAutoMap();
            return;
        }
        session.LastAutoResult.Clear();
        FinishProjectLoad(p, name, missing, missingSceneRefs);
    }

    private (ZoneArenaProject p, string name, int missing, int missingSceneRefs)? _pendingProjectLoad;

    // "7000/F000" -> value / mask; false for anything that is not two hex numbers
    private static bool TryParseMaterial(string text, out MaterialFilter filter)
    {
        filter = default;
        var slash = text.IndexOf('/');
        if (slash <= 0 || !ulong.TryParse(text.AsSpan(0, slash), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value) || !TryParseHexMask(text[(slash + 1)..], out var mask))
        {
            return false;
        }
        filter = new(value, mask);
        return true;
    }

    private void FinishProjectLoad(ZoneArenaProject p, string name, int missing, int missingSceneRefs)
    {
        var session = _session!;
        foreach (var m in Enumerable.Range(0, _meshModes.Count))
        {
            if (_meshModes[m] == MeshMode.Exclude)
            {
                session.SelectMesh(m, false, false);
            }
            else if (_meshModes[m] == MeshMode.Include)
            {
                session.SelectMesh(m, true, true);
            }
        }
        var skipped = 0;
        ApplyDeltas(p.Added, true, ref missing, ref skipped);
        ApplyDeltas(p.Removed, false, ref missing, ref skipped);
        session.SelectedFloorBoxes.Clear();
        for (var i = 0; i < p.FloorBoxes.Count; ++i)
        {
            var b = FindBox(p.FloorBoxes[i], p.FloorBoxNodes[i]);
            if (b >= 0)
            {
                session.SelectedFloorBoxes.Add(b);
            }
        }
        _selection.ClearHistory(); // the loaded state is the new baseline
        _manualPolygons.Clear();
        foreach (var mp in p.ManualPolygons)
        {
            var poly = new ManualPolygon { Difference = mp.Difference };
            for (var i = 0; i + 1 < mp.XZ.Length; i += 2)
            {
                poly.Vertices.Add(new(mp.XZ[i], mp.XZ[i + 1]));
                poly.Y.Add(i / 2 < mp.Y.Length ? mp.Y[i / 2] : session.Centre.Y);
            }
            _manualPolygons.Add(poly);
        }
        _pendingContourFlags = p.ContourEnabled;
        _rules.Clear();
        foreach (var r in p.Rules)
        {
            _rules.Add(r.Clone());
        }
        _selectedRule = -1;
        _ruleSnippetFor = -1;
        _timeline = null;
        _timelinePath = p.ReplayTimelinePath;
        if (_timelinePath.Length > 0 && File.Exists(_timelinePath))
        {
            var open = _replayWindow?.Manager.LoadedReplays.FirstOrDefault(r => r.path == _timelinePath);
            if (open?.replay != null)
            {
                StartTimelineImport(open.Value.replay, _timelinePath, false);
            }
            else
            {
                StartTimelineImport(_timelinePath, false);
            }
        }
        ++_recomputeGen;
        MarkResultDirty();
        _resultDirtySince = 0;
        _projectStatus = $"loaded '{name}': {session.Selected.Count} triangles, {session.Scenes.Count} scene(s), {_rules.Count} rule(s){(missing > 0 ? $", {missing} mesh reference(s) not found" : "")}{(skipped > 0 ? $", {skipped} mesh delta(s) skipped (triangle count changed)" : "")}{(missingSceneRefs > 0 ? $", {missingSceneRefs} scene reference(s) not found" : "")}";
        ReportProjectHealth(p, missing, skipped);
        ResetEditHistory();
        if (p.View == null || p.View.Zoom <= 0f)
        {
            _canvas.Center = new(session.Centre.X, session.Centre.Z);
        }
    }

    private List<bool>? _pendingContourFlags;

    private void ApplyDeltas(List<SavedTriangleDelta> deltas, bool add, ref int missing, ref int skipped)
    {
        var scene = _scene!;
        var session = _session!;
        foreach (var d in deltas)
        {
            var m = FindMesh(d.Path, d.LayoutId, d.NodeId);
            if (m < 0)
            {
                ++missing;
                continue;
            }
            var mesh = scene.Meshes[m];
            if (mesh.TriCount != d.TriangleCount)
            {
                ++skipped;
                continue;
            }
            foreach (var local in d.Tris)
            {
                if (local < 0 || local >= mesh.TriCount)
                {
                    continue;
                }
                if (add)
                {
                    session.Selected.Add(mesh.TriStart + local);
                }
                else
                {
                    session.Selected.Remove(mesh.TriStart + local);
                }
            }
        }
    }

    // contour enable flags saved with a project are applied once the first recompute after loading produced the same layout
    private void ApplyPendingContourFlags()
    {
        if (_pendingContourFlags == null)
        {
            return;
        }
        var count = 0;
        foreach (var poly in _pipeline.Preview)
        {
            count += 1 + poly.Holes.Count;
        }
        if (count == _pendingContourFlags.Count)
        {
            var k = 0;
            foreach (var poly in _pipeline.Preview)
            {
                poly.Outer.Enabled = _pendingContourFlags[k++];
                foreach (var h in poly.Holes)
                {
                    h.Enabled = _pendingContourFlags[k++];
                }
            }
            _pipeline.SnippetDirty = true;
        }
        _pendingContourFlags = null;
    }

    private void DrawProjectSection()
    {
        var scene = _scene!;
        var file = LoadProjectFile(scene.TerritoryId);
        ImGui.SetNextItemWidth(160f);
        ImGui.InputText("name##project", ref _projectName, 64);
        ImGui.SameLine();
        var saveName = _projectName.Trim();
        var loadInFlight = _pendingProjectLoad != null || _autoMapTask != null; // a load in flight has emptied the selection
        using (ImRaii.Disabled(saveName.Length == 0 || loadInFlight))
        {
            if (ImGui.Button("Save"))
            {
                SaveProject(saveName);
            }
        }
        Hint(loadInFlight ? "Wait for the running load / auto-map to finish" : "Save the working state under this name (a name in use is overwritten)");
        ImGui.TextDisabled(_projectFileSummary);
        string? toDelete = null;
        var now = Environment.TickCount64;
        var armed = _deleteArmed.Length > 0 && now - _deleteArmedAt < 3000 ? _deleteArmed : "";
        foreach (var (name, summary) in _projectRows)
        {
            using var id = ImRaii.PushId(name);
            using (ImRaii.Disabled(loadInFlight))
            {
                if (ImGui.SmallButton("load"))
                {
                    _projectName = name;
                    LoadProject(name);
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(armed == name ? "confirm delete" : "delete"))
            {
                if (armed == name)
                {
                    toDelete = name;
                    _deleteArmed = "";
                }
                else
                {
                    _deleteArmed = name;
                    _deleteArmedAt = now;
                }
            }
            Hint(armed == name ? "Click again to delete this project (no undo)" : "Click twice within 3 s to delete this project");
            ImGui.SameLine();
            ImGui.TextUnformatted(summary);
        }
        if (toDelete != null)
        {
            file.Projects.Remove(toDelete);
            WriteProjectFile(file);
        }
        if (_projectStatus.Length > 0)
        {
            ImGui.TextWrapped(_projectStatus);
        }
        if (_projectHealth.Count > 0)
        {
            ImGui.TextDisabled("health:");
            foreach (var line in _projectHealth)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextWrapped(line);
            }
        }
    }
}
