using System.Threading;

namespace BossMod;

public sealed class ZoneLoadOptions
{
    public bool LoadTerrain = true;              // parse list.pcb; tiles are loaded lazily via EnsureTerrainLoaded
    public float TerrainPreloadRadius;           // 0 = none at load time
    public Vector2? TerrainPreloadCenterXZ;
    public int MaxSharedGroupDepth = 8;
    public bool MaterialiseTriangles = true;
    public string[] LgbNames = ["bg.lgb", "planmap.lgb", "planevent.lgb", "planlive.lgb"];
}

public sealed class ZoneLoadProgress
{
    public volatile string Stage = "";
    public volatile int Current, Total;
}

// territory -> layout files -> collision meshes placed in world space; all file reads happen on the calling thread
public static class ZoneCollisionLoader
{
    private const int MaxListedPcbWarnings = 5; // per kind (missing, self-check); the report counters carry the totals

    public static bool TryResolvePaths(IZoneFileSource src, string bg, out string levelDir, out string collisionDir, List<string> log) => TryResolvePaths(src, bg, out levelDir, out collisionDir, log, out _);

    // the probe reads bg.lgb itself so a following Load can use it without a second read
    public static bool TryResolvePaths(IZoneFileSource src, string bg, out string levelDir, out string collisionDir, List<string> log, out byte[]? bgLgb)
    {
        levelDir = "";
        collisionDir = "";
        bgLgb = null;
        if (bg.Length == 0)
        {
            log.Add("territory has no bg path");
            return false;
        }
        var slash = bg.LastIndexOf('/');
        var dir = slash >= 0 ? bg[..slash] : bg;
        levelDir = "bg/" + dir;
        collisionDir = levelDir.EndsWith("/level", StringComparison.Ordinal) ? levelDir[..^6] + "/collision" : levelDir + "/collision";
        var probe = levelDir + "/bg.lgb";
        bgLgb = src.Read(probe);
        if (bgLgb != null)
        {
            log.Add($"level dir {levelDir} (bg.lgb found)");
            return true;
        }
        log.Add($"{probe} not found");
        return false;
    }

    // the territory's bg path from the TerritoryType sheet; a missing row or an empty path ends up as a report warning like an unresolved directory
    public static Task<ZoneCollisionScene> LoadAsync(IZoneFileSource src, uint territoryId, ZoneLoadOptions opt, ZoneLoadProgress? progress, CancellationToken ct)
        => Task.Run(() => Load(src, territoryId, opt, progress, ct), ct);

    public static ZoneCollisionScene Load(IZoneFileSource src, uint territoryId, ZoneLoadOptions opt, ZoneLoadProgress? progress, CancellationToken ct)
    {
        var row = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(territoryId);
        if (row == null)
        {
            var scene = new ZoneCollisionScene { TerritoryId = territoryId };
            scene.Report.Warnings.Add($"territory {territoryId}: no TerritoryType row");
            return scene;
        }
        return Load(src, territoryId, row.Value.Bg.ToString(), opt, progress ?? new(), ct);
    }

    public static Task<ZoneCollisionScene> LoadAsync(IZoneFileSource src, uint territoryId, string bg, ZoneLoadOptions opt, ZoneLoadProgress progress, CancellationToken ct)
        => Task.Run(() => Load(src, territoryId, bg, opt, progress, ct), ct);

    public static ZoneCollisionScene Load(IZoneFileSource src, uint territoryId, string bg, ZoneLoadOptions opt, ZoneLoadProgress progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scene = new ZoneCollisionScene { TerritoryId = territoryId, Bg = bg };
        var report = scene.Report;
        if (!TryResolvePaths(src, bg, out scene.LevelDir, out scene.CollisionDir, report.ResolvedPaths, out var bgLgb))
        {
            report.Warnings.Add($"could not resolve level directory for '{bg}'");
            return scene;
        }

        var ctx = new LoadContext(src, scene, opt, progress, ct);
        foreach (var name in opt.LgbNames)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"{scene.LevelDir}/{name}";
            progress.Stage = name;
            var data = name == "bg.lgb" ? bgLgb : src.Read(path);
            if (data == null)
            {
                report.ResolvedPaths.Add($"{path}: missing");
                continue;
            }
            ++report.LgbFiles;
            LgbDocument doc;
            try
            {
                doc = LgbReader.Parse(path, data);
            }
            catch (ZoneFormatException ex)
            {
                report.Warnings.Add(ex.Message);
                continue;
            }
            foreach (var w in doc.Warnings)
            {
                report.Warnings.Add($"{name}: {w}");
            }
            ctx.VisitDocument(doc, name, Matrix4x4.Identity, 0, "", -1, ZoneBinary.Fnv1aOffset);
        }
        report.ParseMs = sw.ElapsedMilliseconds;

        if (opt.LoadTerrain)
        {
            var listPath = $"{scene.CollisionDir}/list.pcb";
            var data = src.Read(listPath);
            if (data != null)
            {
                try
                {
                    scene.TerrainList = PcbReader.ParseList(data);
                    report.TerrainTilesListed = scene.TerrainList.Entries.Length;
                    report.ResolvedPaths.Add($"{listPath}: {report.TerrainTilesListed} tiles");
                }
                catch (ZoneFormatException ex)
                {
                    report.Warnings.Add(ex.Message);
                }
            }
            else
            {
                report.ResolvedPaths.Add($"{listPath}: missing");
            }
            var terrainLayer = new ZoneLayer { Index = scene.Layers.Count, SourceFile = "list.pcb", Name = "terrain", Enabled = true, IsTerrain = true, PathId = ZoneBinary.Fnv1a64(ZoneBinary.Fnv1aOffset, "list.pcb") };
            scene.Layers.Add(terrainLayer);
            scene.TerrainLayerIndex = terrainLayer.Index;
            if (opt.TerrainPreloadRadius > 0f && opt.TerrainPreloadCenterXZ is { } c)
            {
                EnsureTerrainLoaded(scene, src, c, opt.TerrainPreloadRadius, materialise: false);
            }
        }
        if (report.PcbFilesMissing > MaxListedPcbWarnings)
        {
            report.Warnings.Add($"... and {report.PcbFilesMissing - MaxListedPcbWarnings} more missing pcb files");
        }
        if (report.PcbSelfCheckFailures > MaxListedPcbWarnings)
        {
            report.Warnings.Add($"... and {report.PcbSelfCheckFailures - MaxListedPcbWarnings} more pcb self-check failures");
        }

        if (opt.MaterialiseTriangles)
        {
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            progress.Stage = "triangles";
            progress.Total = scene.Meshes.Count;
            var total = 0;
            for (var i = 0; i < scene.Meshes.Count; ++i)
            {
                total += scene.Meshes[i].Mesh.TriangleCount;
            }
            scene.Triangles.Reserve(total);
            for (var i = 0; i < scene.Meshes.Count; ++i)
            {
                ct.ThrowIfCancellationRequested();
                progress.Current = i;
                scene.Triangles.Append(scene.Meshes[i]);
            }
            report.TransformMs = sw2.ElapsedMilliseconds;
        }
        scene.RecomputeBounds();
        progress.Stage = "done";
        return scene;
    }

    // loads listed terrain tiles whose bounds intersect the circle; mutates the scene, so no scene reader may run concurrently (the editor
    // guarantees this by gating Draw while a mapping batch runs) and never while a load task is running
    public static int EnsureTerrainLoaded(ZoneCollisionScene scene, IZoneFileSource src, Vector2 centerXZ, float radius, bool materialise = true)
    {
        if (scene.TerrainList == null || scene.TerrainLayerIndex < 0)
        {
            return 0;
        }
        var added = 0;
        var entries = scene.TerrainList.Entries;
        for (var i = 0; i < entries.Length; ++i)
        {
            var e = entries[i];
            if (scene.TerrainTileMeshIndex.ContainsKey(e.MeshId))
            {
                continue;
            }
            // y bounds of list entries are not meaningful; test XZ only
            var b = new Bounds3(new(e.Bounds.Min.X, -1e6f, e.Bounds.Min.Z), new(e.Bounds.Max.X, 1e6f, e.Bounds.Max.Z));
            if (!b.IntersectsXZCircle(centerXZ, radius))
            {
                continue;
            }
            var path = $"{scene.CollisionDir}/tr{e.MeshId:d4}.pcb";
            var mesh = GetOrLoadPcb(scene, src, path);
            if (mesh == null)
            {
                continue;
            }
            var inst = new ZoneMeshInstance
            {
                Index = scene.Meshes.Count,
                PcbPath = path,
                Name = $"tr{e.MeshId:d4}",
                LayerIndex = scene.TerrainLayerIndex,
                IsTerrainTile = true,
                Mesh = mesh,
                WorldBounds = mesh.LocalBounds,
            };
            scene.Meshes.Add(inst);
            scene.TerrainTileMeshIndex[e.MeshId] = inst.Index;
            if (materialise)
            {
                scene.Triangles.Append(inst);
            }
            ++added;
        }
        if (added > 0)
        {
            scene.RecomputeBounds();
            scene.Activity.Dirty = true;
        }
        return added;
    }

    // a missing or malformed file is cached as null and counted; only the first few of each are listed in the warnings
    internal static PcbMesh? GetOrLoadPcb(ZoneCollisionScene scene, IZoneFileSource src, string path)
    {
        if (scene.PcbCache.TryGetValue(path, out var cached))
        {
            return cached;
        }
        var data = src.Read(path);
        if (data == null)
        {
            if (++scene.Report.PcbFilesMissing <= MaxListedPcbWarnings)
            {
                scene.Report.Warnings.Add($"missing pcb {path}");
            }
            scene.PcbCache[path] = null;
            return null;
        }
        try
        {
            var mesh = PcbReader.ParseMesh(path, data);
            ++scene.Report.PcbFilesLoaded;
            if (!mesh.SelfCheckOk && ++scene.Report.PcbSelfCheckFailures <= MaxListedPcbWarnings)
            {
                scene.Report.Warnings.Add($"{path}: self-check failed ({mesh.SelfCheck})");
            }
            scene.PcbCache[path] = mesh;
            return mesh;
        }
        catch (ZoneFormatException ex)
        {
            scene.Report.Warnings.Add(ex.Message);
            scene.PcbCache[path] = null;
            return null;
        }
    }

    private sealed class LoadContext(IZoneFileSource src, ZoneCollisionScene scene, ZoneLoadOptions opt, ZoneLoadProgress progress, CancellationToken ct)
    {
        private readonly Dictionary<string, LgbDocument?> _sgbCache = [];

        public void VisitDocument(LgbDocument doc, string sourceFile, in Matrix4x4 parentWorld, int depth, string chain, int parentNode, ulong parentPathId)
        {
            var report = scene.Report;
            var filePathId = ZoneBinary.Fnv1a64(parentPathId, sourceFile);
            foreach (var group in doc.Groups)
            {
                foreach (var layer in group.Layers)
                {
                    var zl = new ZoneLayer
                    {
                        Index = scene.Layers.Count,
                        SourceFile = sourceFile,
                        GroupId = group.Id,
                        LayerId = layer.LayerId,
                        Key = layer.Key,
                        Name = layer.Name,
                        FestivalId = layer.FestivalId,
                        FestivalPhase = layer.FestivalPhase,
                        IsTemporary = layer.IsTemporary,
                        InstanceCount = layer.Instances.Count,
                        Enabled = layer.FestivalId == 0,
                        SharedGroupChain = chain,
                        ParentNode = parentNode,
                        PathId = ZoneBinary.Fnv1a64(filePathId, layer.LayerId),
                    };
                    scene.Layers.Add(zl);
                    ++report.Layers;
                    progress.Stage = $"{sourceFile}/{layer.Name}";
                    foreach (var inst in layer.Instances)
                    {
                        ct.ThrowIfCancellationRequested();
                        ++report.Instances;
                        VisitInstance(inst, zl, parentWorld, depth, chain, parentNode);
                    }
                }
            }
        }

        private void VisitInstance(LgbInstance inst, ZoneLayer layer, in Matrix4x4 parentWorld, int depth, string chain, int parentNode)
        {
            var report = scene.Report;
            var local = ZoneTransform.Compose(inst.Translation, inst.RotationEuler, inst.Scale);
            var world = local * parentWorld;
            var node = new ZoneNode
            {
                Index = scene.Nodes.Count,
                Parent = parentNode,
                LayerIndex = layer.Index,
                Depth = depth,
                Type = inst.Type,
                InstanceKey = inst.Key,
                Name = inst.Name,
                World = world,
                LayoutObjectId = ZoneCollisionScene.MakeLayoutObjectId(inst.Type, layer.Key, inst.Key),
                PathId = ZoneBinary.Fnv1a64(layer.PathId, inst.Key),
                Scene = scene,
                Source = inst,
            };
            scene.Nodes.Add(node);
            if (parentNode < 0 && !scene.TopLevelNodeByKey.TryAdd(inst.Key, node.Index))
            {
                report.Warnings.Add($"duplicate top-level instance key 0x{inst.Key:X} ('{inst.Name}' in {layer.SourceFile}/{layer.Name})");
            }
            if (!scene.NodeByPathId.TryAdd(node.PathId, node.Index))
            {
                report.Warnings.Add($"path id collision for {node.Path}");
            }
            switch ((LgbInstanceType)inst.Type)
            {
                case LgbInstanceType.BgPart:
                    ++report.BgParts;
                    if (inst.BgCollision == LgbBgCollisionType.Mesh && !string.IsNullOrEmpty(inst.CollisionPath))
                    {
                        var mesh = GetOrLoadPcb(scene, src, inst.CollisionPath);
                        if (mesh != null)
                        {
                            ++report.BgPartsWithMesh;
                            AddMesh(inst, layer, node, mesh, inst.CollisionPath, world, inst.BgMatValue, inst.BgMatMask, true);
                        }
                    }
                    if (inst.Analytic is { } a)
                    {
                        ++report.BgPartsAnalytic;
                        var aWorld = ZoneTransform.Compose(a.Translation, a.RotationEuler, a.Scale) * world;
                        AddAnalytic(inst, layer, node, (LgbColliderKind)(byte)a.Kind, aWorld, a.MatValue, a.MatMask, true, a.Bounds);
                    }
                    break;
                case LgbInstanceType.CollisionBox:
                    ++report.CollisionBoxes;
                    if (inst.ColliderKind == LgbColliderKind.Mesh)
                    {
                        if (!string.IsNullOrEmpty(inst.BoxMeshPath))
                        {
                            var mesh = GetOrLoadPcb(scene, src, inst.BoxMeshPath);
                            if (mesh != null)
                            {
                                AddMesh(inst, layer, node, mesh, inst.BoxMeshPath, world, inst.BoxMatValue, inst.BoxMatMask, inst.ActiveByDefault);
                            }
                        }
                    }
                    else if (inst.ColliderKind != LgbColliderKind.None)
                    {
                        AddAnalytic(inst, layer, node, inst.ColliderKind, world, inst.BoxMatValue, inst.BoxMatMask, inst.ActiveByDefault, null);
                    }
                    break;
                case LgbInstanceType.SharedGroup:
                    ++report.SharedGroups;
                    report.SharedGroupDepthMax = Math.Max(report.SharedGroupDepthMax, depth + 1);
                    if (depth + 1 > opt.MaxSharedGroupDepth)
                    {
                        report.Warnings.Add($"shared group '{inst.Name}' exceeds nesting depth {opt.MaxSharedGroupDepth}, skipped");
                        break;
                    }
                    if (!string.IsNullOrEmpty(inst.SharedGroupPath))
                    {
                        var doc = GetOrParseSgb(inst.SharedGroupPath);
                        if (doc != null)
                        {
                            var childChain = chain.Length == 0 ? inst.Name : $"{chain}>{inst.Name}";
                            if (childChain.Length == 0)
                            {
                                childChain = inst.SharedGroupPath;
                            }
                            VisitDocument(doc, System.IO.Path.GetFileName(inst.SharedGroupPath), world, depth + 1, childChain, node.Index, node.PathId);
                        }
                    }
                    break;
                case LgbInstanceType.ExitRange:
                case LgbInstanceType.PopRange:
                case LgbInstanceType.EventObject:
                case LgbInstanceType.MapRange:
                case LgbInstanceType.EventRange:
                case LgbInstanceType.DoorRange:
                case LgbInstanceType.Treasure:
                case LgbInstanceType.EventNpc:
                case LgbInstanceType.BattleNpc:
                    {
                        var m = new ZoneMarker
                        {
                            Index = scene.Markers.Count,
                            Type = inst.Type,
                            Name = inst.Name,
                            LayoutObjectId = node.LayoutObjectId,
                            LayerIndex = layer.Index,
                            NodeIndex = node.Index,
                            InstanceKey = inst.Key,
                            Position = world.Translation,
                            BaseId = inst.BaseId,
                            BoundInstanceId = inst.BoundInstanceId,
                            LinkedInstanceId = inst.LinkedInstanceId,
                            ActiveByDefault = inst.ActiveByDefault,
                            Source = inst,
                            WorldBounds = new(world.Translation, world.Translation),
                        };
                        if (m.IsTrigger)
                        {
                            m.Corners = new Vector3[8];
                            Bounds3.TransformCorners(new(new(-1f), new(1f)), world, m.Corners);
                            m.WorldBounds = Bounds3.FromPoints(m.Corners);
                        }
                        node.MarkerIndex = m.Index;
                        scene.Markers.Add(m);
                    }
                    break;
                default:
                    report.UnparsedTypes[inst.Type] = report.UnparsedTypes.GetValueOrDefault(inst.Type) + 1;
                    break;
            }
            node.SubtreeEnd = scene.Nodes.Count;
        }

        private LgbDocument? GetOrParseSgb(string path)
        {
            if (_sgbCache.TryGetValue(path, out var cached))
            {
                return cached;
            }
            var data = src.Read(path);
            LgbDocument? doc = null;
            if (data == null)
            {
                scene.Report.Warnings.Add($"missing sgb {path}");
            }
            else
            {
                try
                {
                    doc = LgbReader.Parse(path, data);
                    ++scene.Report.SgbFiles;
                    foreach (var w in doc.Warnings)
                    {
                        scene.Report.Warnings.Add($"{path}: {w}");
                    }
                }
                catch (ZoneFormatException ex)
                {
                    scene.Report.Warnings.Add(ex.Message);
                }
            }
            _sgbCache[path] = doc;
            return doc;
        }

        private void AddMesh(LgbInstance inst, ZoneLayer layer, ZoneNode node, PcbMesh mesh, string path, in Matrix4x4 world, ulong matValue, ulong matMask, bool active)
        {
            var m = new ZoneMeshInstance
            {
                Index = scene.Meshes.Count,
                PcbPath = path,
                Name = inst.Name,
                LayoutObjectId = node.LayoutObjectId,
                LayerIndex = layer.Index,
                NodeIndex = node.Index,
                InstanceKey = inst.Key,
                LayerKey = layer.Key,
                World = world,
                Translation = inst.Translation,
                RotationEuler = inst.RotationEuler,
                Scale = inst.Scale,
                ObjMatValue = matValue,
                ObjMatMask = matMask,
                Mesh = mesh,
                WorldBounds = opt.MaterialiseTriangles ? default : Bounds3.Transform(mesh.LocalBounds, world), // ZoneTriangleStore.Append sets it from the placed triangles
                ActiveByDefault = active,
            };
            node.MeshIndex = m.Index;
            scene.Meshes.Add(m);
        }

        private void AddAnalytic(LgbInstance inst, ZoneLayer layer, ZoneNode node, LgbColliderKind kind, in Matrix4x4 world, ulong matValue, ulong matMask, bool active, Bounds3? analyticBounds)
        {
            var id = node.LayoutObjectId;
            // analytic bg-part shapes: the block's bounds give the local extents (the transform is usually unscaled); degenerate bounds fall back to the unit cube
            var local = new Bounds3(new(-1f), new(1f));
            if (analyticBounds is { } ab && ab.Max.X > ab.Min.X && ab.Max.Y > ab.Min.Y && ab.Max.Z > ab.Min.Z)
            {
                local = ab;
            }
            if (kind == LgbColliderKind.Box)
            {
                var box = new ZoneBoxInstance
                {
                    Index = scene.Boxes.Count,
                    Name = inst.Name,
                    LayoutObjectId = id,
                    LayerIndex = layer.Index,
                    NodeIndex = node.Index,
                    InstanceKey = inst.Key,
                    LayerKey = layer.Key,
                    World = world,
                    Translation = inst.Translation,
                    RotationEuler = inst.RotationEuler,
                    Scale = inst.Scale,
                    MatValue = matValue,
                    MatMask = matMask,
                    Kind = kind,
                    ActiveByDefault = active,
                };
                box.LocalBounds = local;
                box.IsAnalytic = analyticBounds != null;
                var lc = local.Center;
                var lh = local.Size * 0.5f;
                Bounds3.TransformCorners(local, world, box.Corners);
                box.Center = Vector3.Transform(lc, world);
                box.HalfExtents = new(lh.X * new Vector3(world.M11, world.M12, world.M13).Length(), lh.Y * new Vector3(world.M21, world.M22, world.M23).Length(), lh.Z * new Vector3(world.M31, world.M32, world.M33).Length());
                box.WorldBounds = Bounds3.FromPoints(box.Corners);
                node.BoxIndex = box.Index;
                scene.Boxes.Add(box);
            }
            else
            {
                node.AnalyticIndex = scene.Analytics.Count;
                scene.Analytics.Add(new ZoneAnalyticInstance
                {
                    Index = scene.Analytics.Count,
                    Name = inst.Name,
                    LayoutObjectId = id,
                    LayerIndex = layer.Index,
                    NodeIndex = node.Index,
                    Kind = kind,
                    World = world,
                    MatValue = matValue,
                    MatMask = matMask,
                    WorldBounds = Bounds3.Transform(local, world),
                });
            }
        }
    }
}
