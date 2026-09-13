using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.IO;
using System.Text.Json;

namespace BossMod;

public sealed partial class ZoneArenaEditorWindow
{
    public sealed class SavedSeal
    {
        public string LayoutId = "";
        public bool Use = true;
    }

    public sealed class SavedMeshMode
    {
        public string Path = "";
        public string LayoutId = "";
        public string Mode = "Auto";
    }

    public sealed class SavedTriangleDelta
    {
        public string Path = "";
        public string LayoutId = "";
        public int TriangleCount;
        public int[] Tris = []; // mesh-local indices
    }

    public sealed class SavedManualPolygon
    {
        public bool Difference = true;
        public float[] XZ = [];
        public float[] Y = [];
    }

    public sealed class ZoneArenaProject
    {
        public float CenterX, CenterY, CenterZ;
        public bool CentreValid;
        public List<string> FloorMaterials = ["7000/F000"];
        public string SealMaterialValue = "2400", SealMaterialMask = "1FFFFFFFFF";
        public bool SealGeometryFilter = true;
        public string PairSealA = "", PairSealB = "", PairMarker = "";
        public List<string> FloorBoxes = [];
        public List<string> IgnoredBoxes = [];
        public List<string> ObstacleBoxMaterials = ["2000/F000"];
        public bool CutBoxes = true;
        public int FloorMatchMode;
        public bool SealRequireExactMask = true, SealIncludeInactive = true;
        public float SealPairMaxDistance = 80f, MaxRadius = 60f, MaxSlopeDeg = 45f, WeldEps = 1e-3f, SealFootprintInflate = 0.05f, ObstacleInflate = 0.05f, ObstacleHeightAbove = 2.5f, ObstacleHeightBelow = 0.5f, ObstacleMinHeight = 0.25f;
        public int Adjacency, SealBlock;
        public bool KeepPolygonContainingCentre = true, CutObstacles = true;
        public float RimExtension = 1f, RimMaxSlopeDeg = 80f;
        public int RimHops = 3;
        public bool ObstacleLocalHeight = true;
        public bool RimRequireWall = true;
        public float StepHeight = 0.5f;
        public float SeamClose = 0.05f;
        public float WallSnap = 0.5f;
        public List<SavedSeal> Seals = [];
        public List<SavedMeshMode> Meshes = [];
        public List<string> DisabledLayers = [];
        public bool AutoMapRan;
        public List<SavedTriangleDelta> Added = [];
        public List<SavedTriangleDelta> Removed = [];
        public float Epsilon = 0.02f, Offset, Closing, Opening;
        public int Decimals = 3;
        public bool TrimCollinear = true;
        public float DropAreaBelow;
        public string ArenaFieldName = "arena";
        public float FlatThreshold = 0.5f;
        public bool EmitProjectionHeightZero;
        public float LayerGap = 1f;
        public float TerrainRadius = 120f;
        public List<bool> ContourEnabled = [];
        public List<SavedManualPolygon> ManualPolygons = [];
        public List<float> DeletedVertices = []; // x,z pairs
        public bool AdjustForHitboxInwards = true;
    }

    public sealed class ZoneArenaProjectFile
    {
        public int Version = 1;
        public uint Territory;
        public string Bg = "";
        public Dictionary<string, ZoneArenaProject> Projects = [];
    }

    private string _projectName = "";
    private string _projectStatus = "";
    private ZoneArenaProjectFile? _projectFile;
    private uint _projectFileTerritory = uint.MaxValue;

    private string ProjectPath(uint territory) => Path.Combine(_dalamud.ConfigDirectory.FullName, "zone-arenas", $"{territory}.json");

    private ZoneArenaProjectFile LoadProjectFile(uint territory)
    {
        if (_projectFile != null && _projectFileTerritory == territory)
        {
            return _projectFile;
        }
        _projectFileTerritory = territory;
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
                _projectStatus = $"load failed: {ex.Message}";
            }
        }
        return _projectFile;
    }

    private void WriteProjectFile(ZoneArenaProjectFile file)
    {
        var path = ProjectPath(file.Territory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(file, Serialization.BuildSerializationOptions()));
            _projectStatus = $"saved {path}";
        }
        catch (Exception ex)
        {
            _projectStatus = $"save failed: {ex.Message}";
        }
    }

    private static string Hex(ulong v) => v.ToString("X");

    private void SaveProject(string name)
    {
        var scene = _scene!;
        var session = _session!;
        var s = session.Settings;
        var file = LoadProjectFile(scene.TerritoryId);
        file.Bg = scene.Bg;
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
            RimRequireWall = s.RimRequireWall,
            StepHeight = s.StepHeight,
            SeamClose = s.SeamClose,
            WallSnap = s.WallSnap,
            AutoMapRan = session.LastAutoResult.Count > 0,
            Epsilon = _pipeline.Epsilon,
            Offset = _pipeline.Offset,
            Closing = _pipeline.Closing,
            Opening = _pipeline.Opening,
            AdjustForHitboxInwards = _pipeline.AdjustForHitboxInwards,
            Decimals = _pipeline.Decimals,
            TrimCollinear = _pipeline.TrimCollinear,
            DropAreaBelow = _pipeline.DropAreaBelow,
            ArenaFieldName = _pipeline.ArenaFieldName,
            FlatThreshold = _pipeline.FlatThreshold,
            EmitProjectionHeightZero = _pipeline.EmitProjectionHeightZero,
            LayerGap = _pipeline.LayerGap,
            TerrainRadius = _terrainRadius,
        };
        foreach (var f in s.FloorMaterials)
        {
            p.FloorMaterials.Add(f.ToString());
        }
        for (var i = 0; i < session.Seals.Count; ++i)
        {
            var box = scene.Boxes[session.Seals[i].BoxIndex];
            p.Seals.Add(new SavedSeal { LayoutId = $"0x{box.LayoutObjectId:X16}", Use = session.ActiveSeals.Contains(box.Index) });
        }
        if (session.PairIndex >= 0 && session.PairIndex < session.Pairs.Count)
        {
            var pair = session.Pairs[session.PairIndex];
            p.PairSealA = $"0x{scene.Boxes[session.Seals[pair.SealA].BoxIndex].LayoutObjectId:X16}";
            p.PairSealB = pair.SealB >= 0 ? $"0x{scene.Boxes[session.Seals[pair.SealB].BoxIndex].LayoutObjectId:X16}" : "";
            p.PairMarker = pair.MarkerIndex >= 0 ? $"0x{scene.Markers[pair.MarkerIndex].LayoutObjectId:X16}" : "";
        }
        foreach (var b in session.IgnoredBoxes)
        {
            p.IgnoredBoxes.Add($"0x{scene.Boxes[b].LayoutObjectId:X16}");
        }
        p.CutBoxes = s.CutBoxes;
        p.ObstacleBoxMaterials.Clear();
        foreach (var f in s.ObstacleBoxMaterials)
        {
            p.ObstacleBoxMaterials.Add(f.ToString());
        }
        foreach (var b in session.SelectedFloorBoxes)
        {
            p.FloorBoxes.Add($"0x{scene.Boxes[b].LayoutObjectId:X16}");
        }
        for (var m = 0; m < scene.Meshes.Count && m < _meshModes.Count; ++m)
        {
            if (_meshModes[m] != MeshMode.Auto)
            {
                p.Meshes.Add(new SavedMeshMode { Path = scene.Meshes[m].PcbPath, LayoutId = $"0x{scene.Meshes[m].LayoutObjectId:X16}", Mode = _meshModes[m].ToString() });
            }
        }
        foreach (var layer in scene.Layers)
        {
            if (!layer.Enabled)
            {
                p.DisabledLayers.Add($"{layer.SourceFile}/{layer.Key}/{layer.Name}");
            }
        }
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
        return new SavedTriangleDelta { Path = mesh.PcbPath, LayoutId = $"0x{mesh.LayoutObjectId:X16}", TriangleCount = mesh.TriCount, Tris = [.. localTris] };
    }

    private int FindMesh(string path, string layoutId)
    {
        var scene = _scene!;
        for (var m = 0; m < scene.Meshes.Count; ++m)
        {
            var mesh = scene.Meshes[m];
            if (mesh.PcbPath == path && $"0x{mesh.LayoutObjectId:X16}" == layoutId)
            {
                return m;
            }
        }
        return -1;
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
        var s = session.Settings;
        s.FloorMaterials.Clear();
        foreach (var f in p.FloorMaterials)
        {
            var slash = f.IndexOf('/');
            if (slash > 0)
            {
                s.FloorMaterials.Add(new(Convert.ToUInt64(f[..slash], 16), Convert.ToUInt64(f[(slash + 1)..], 16)));
            }
        }
        s.CutBoxes = p.CutBoxes;
        s.ObstacleBoxMaterials.Clear();
        foreach (var f in p.ObstacleBoxMaterials)
        {
            var slash = f.IndexOf('/');
            if (slash > 0)
            {
                s.ObstacleBoxMaterials.Add(new(Convert.ToUInt64(f[..slash], 16), Convert.ToUInt64(f[(slash + 1)..], 16)));
            }
        }
        session.IgnoredBoxes.Clear();
        foreach (var id in p.IgnoredBoxes)
        {
            for (var b = 0; b < scene.Boxes.Count; ++b)
            {
                if ($"0x{scene.Boxes[b].LayoutObjectId:X16}" == id)
                {
                    session.IgnoredBoxes.Add(b);
                    break;
                }
            }
        }
        if (s.FloorMaterials.Count == 0)
        {
            s.FloorMaterials.Add(new(0x7000, 0xF000));
        }
        s.SealGeometryFilter = p.SealGeometryFilter;
        s.SealMaterialValue = Convert.ToUInt64(p.SealMaterialValue, 16);
        s.SealMaterialMask = Convert.ToUInt64(p.SealMaterialMask, 16);
        s.FloorMatchMode = (CollisionOutlinesExtractor.MaterialMatchMode)p.FloorMatchMode;
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
        s.RimRequireWall = p.RimRequireWall;
        s.StepHeight = p.StepHeight;
        s.SeamClose = p.SeamClose;
        s.WallSnap = p.WallSnap;
        _pipeline.Epsilon = p.Epsilon;
        _pipeline.Offset = p.Offset;
        _pipeline.Closing = p.Closing;
        _pipeline.Opening = p.Opening;
        _pipeline.AdjustForHitboxInwards = p.AdjustForHitboxInwards;
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
        SyncHexFromSettings();

        foreach (var layer in scene.Layers)
        {
            layer.Enabled = !p.DisabledLayers.Contains($"{layer.SourceFile}/{layer.Key}/{layer.Name}");
        }
        session.Adjacency = null;
        session.DetectSeals();
        foreach (var saved in p.Seals)
        {
            for (var i = 0; i < session.Seals.Count; ++i)
            {
                var box = scene.Boxes[session.Seals[i].BoxIndex];
                if ($"0x{box.LayoutObjectId:X16}" == saved.LayoutId)
                {
                    if (saved.Use)
                    {
                        session.ActiveSeals.Add(box.Index);
                    }
                    else
                    {
                        session.ActiveSeals.Remove(box.Index);
                    }
                }
            }
        }
        session.PairIndex = -1;
        for (var i = 0; i < session.Pairs.Count; ++i)
        {
            var pair = session.Pairs[i];
            var a = $"0x{scene.Boxes[session.Seals[pair.SealA].BoxIndex].LayoutObjectId:X16}";
            var b = pair.SealB >= 0 ? $"0x{scene.Boxes[session.Seals[pair.SealB].BoxIndex].LayoutObjectId:X16}" : "";
            var mk = pair.MarkerIndex >= 0 ? $"0x{scene.Markers[pair.MarkerIndex].LayoutObjectId:X16}" : "";
            if (a == p.PairSealA && b == p.PairSealB && mk == p.PairMarker)
            {
                session.PairIndex = i;
                break;
            }
        }
        session.Centre = new(p.CenterX, p.CenterY, p.CenterZ);
        session.CentreValid = p.CentreValid;
        if (_terrainRadius > 0f)
        {
            ZoneCollisionLoader.EnsureTerrainLoaded(scene, new LuminaZoneFileSource(Service.LuminaGameData), new(p.CenterX, p.CenterZ), _terrainRadius);
            while (_meshModes.Count < scene.Meshes.Count)
            {
                _meshModes.Add(MeshMode.Auto);
            }
            _picker = new(scene);
        }
        for (var m = 0; m < _meshModes.Count; ++m)
        {
            _meshModes[m] = MeshMode.Auto;
        }
        session.FloorMeshes.Clear();
        var missing = 0;
        foreach (var mm in p.Meshes)
        {
            var m = FindMesh(mm.Path, mm.LayoutId);
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
            session.AutoMap();
        }
        else
        {
            session.LastAutoResult.Clear();
        }
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
        foreach (var id in p.FloorBoxes)
        {
            for (var b = 0; b < scene.Boxes.Count; ++b)
            {
                if ($"0x{scene.Boxes[b].LayoutObjectId:X16}" == id)
                {
                    session.SelectedFloorBoxes.Add(b);
                    break;
                }
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
        MarkResultDirty();
        _resultDirtySince = 0;
        _projectStatus = $"loaded '{name}': {session.Selected.Count} triangles{(missing > 0 ? $", {missing} mesh reference(s) not found" : "")}{(skipped > 0 ? $", {skipped} mesh delta(s) skipped (triangle count changed)" : "")}";
        _canvas.Center = new(session.Centre.X, session.Centre.Z);
    }

    private List<bool>? _pendingContourFlags;

    private void ApplyDeltas(List<SavedTriangleDelta> deltas, bool add, ref int missing, ref int skipped)
    {
        var scene = _scene!;
        var session = _session!;
        foreach (var d in deltas)
        {
            var m = FindMesh(d.Path, d.LayoutId);
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
        using (ImRaii.Disabled(_projectName.Length == 0))
        {
            if (ImGui.Button("Save"))
            {
                SaveProject(_projectName);
            }
        }
        ImGui.TextDisabled($"{file.Projects.Count} project(s) for territory {scene.TerritoryId} in {ProjectPath(scene.TerritoryId)}");
        string? toDelete = null;
        foreach (var (name, p) in file.Projects)
        {
            using var id = ImRaii.PushId(name);
            if (ImGui.SmallButton("load"))
            {
                _projectName = name;
                LoadProject(name);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("delete"))
            {
                toDelete = name;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted($"{name}: centre ({p.CenterX:f1}, {p.CenterZ:f1}), {p.Added.Sum(d => d.Tris.Length)} added / {p.Removed.Sum(d => d.Tris.Length)} removed, {p.ManualPolygons.Count} manual polygon(s)");
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
    }
}
