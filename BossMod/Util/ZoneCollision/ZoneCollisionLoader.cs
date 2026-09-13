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
    public static bool TryResolvePaths(IZoneFileSource src, string bg, out string levelDir, out string collisionDir, List<string> log)
    {
        levelDir = "";
        collisionDir = "";
        if (bg.Length == 0)
        {
            log.Add("territory has no bg path");
            return false;
        }
        var slash = bg.LastIndexOf('/');
        var dir = slash >= 0 ? bg[..slash] : bg;
        levelDir = "bg/" + dir;
        collisionDir = levelDir.EndsWith("/level") ? levelDir[..^6] + "/collision" : levelDir + "/collision";
        var probe = levelDir + "/bg.lgb";
        if (src.Exists(probe))
        {
            log.Add($"level dir {levelDir} (bg.lgb found)");
            return true;
        }
        log.Add($"{probe} not found");
        return false;
    }

    public static Task<ZoneCollisionScene> LoadAsync(IZoneFileSource src, uint territoryId, string bg, ZoneLoadOptions opt, ZoneLoadProgress progress, CancellationToken ct)
        => Task.Run(() => Load(src, territoryId, bg, opt, progress, ct), ct);

    public static ZoneCollisionScene Load(IZoneFileSource src, uint territoryId, string bg, ZoneLoadOptions opt, ZoneLoadProgress progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scene = new ZoneCollisionScene { TerritoryId = territoryId, Bg = bg };
        var report = scene.Report;
        if (!TryResolvePaths(src, bg, out scene.LevelDir, out scene.CollisionDir, report.ResolvedPaths))
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
            var data = src.Read(path);
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
            ctx.VisitDocument(doc, name, Matrix4x4.Identity, 0, "");
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
            var terrainLayer = new ZoneLayer { Index = scene.Layers.Count, SourceFile = "list.pcb", Name = "terrain", Enabled = true, IsTerrain = true };
            scene.Layers.Add(terrainLayer);
            scene.TerrainLayerIndex = terrainLayer.Index;
            if (opt.TerrainPreloadRadius > 0f && opt.TerrainPreloadCenterXZ is { } c)
            {
                EnsureTerrainLoaded(scene, src, c, opt.TerrainPreloadRadius, materialise: false);
            }
        }

        if (opt.MaterialiseTriangles)
        {
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            progress.Stage = "triangles";
            progress.Total = scene.Meshes.Count;
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

    // loads listed terrain tiles whose bounds intersect the circle; UI thread only, never while a load task is running
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
                TerrainMeshId = e.MeshId,
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
        }
        return added;
    }

    internal static PcbMesh? GetOrLoadPcb(ZoneCollisionScene scene, IZoneFileSource src, string path)
    {
        if (scene.PcbCache.TryGetValue(path, out var cached))
        {
            return cached;
        }
        var data = src.Read(path);
        if (data == null)
        {
            ++scene.Report.PcbFilesMissing;
            scene.Report.Warnings.Add($"missing pcb {path}");
            scene.PcbCache[path] = null!;
            return null;
        }
        try
        {
            var mesh = PcbReader.ParseMesh(path, data);
            ++scene.Report.PcbFilesLoaded;
            if (!mesh.SelfCheckOk)
            {
                ++scene.Report.PcbSelfCheckFailures;
                if (scene.Report.PcbSelfCheckFailures <= 5)
                {
                    scene.Report.Warnings.Add($"{path}: self-check failed ({mesh.SelfCheck})");
                }
            }
            scene.PcbCache[path] = mesh;
            return mesh;
        }
        catch (ZoneFormatException ex)
        {
            scene.Report.Warnings.Add(ex.Message);
            scene.PcbCache[path] = null!;
            return null;
        }
    }

    private sealed class LoadContext(IZoneFileSource src, ZoneCollisionScene scene, ZoneLoadOptions opt, ZoneLoadProgress progress, CancellationToken ct)
    {
        private readonly Dictionary<string, LgbDocument?> _sgbCache = [];

        public void VisitDocument(LgbDocument doc, string sourceFile, in Matrix4x4 parentWorld, int depth, string chain)
        {
            var report = scene.Report;
            foreach (var group in doc.Groups)
            {
                foreach (var layer in group.Layers)
                {
                    var zl = new ZoneLayer
                    {
                        Index = scene.Layers.Count,
                        SourceFile = sourceFile,
                        GroupId = group.Id,
                        Key = layer.Key,
                        Name = layer.Name,
                        FestivalId = layer.FestivalId,
                        FestivalPhase = layer.FestivalPhase,
                        InstanceCount = layer.Instances.Count,
                        Enabled = layer.FestivalId == 0,
                        SharedGroupChain = chain,
                    };
                    scene.Layers.Add(zl);
                    ++report.Layers;
                    progress.Stage = $"{sourceFile}/{layer.Name}";
                    foreach (var inst in layer.Instances)
                    {
                        ct.ThrowIfCancellationRequested();
                        ++report.Instances;
                        VisitInstance(inst, zl, parentWorld, depth, chain);
                    }
                }
            }
        }

        private void VisitInstance(LgbInstance inst, ZoneLayer layer, in Matrix4x4 parentWorld, int depth, string chain)
        {
            var report = scene.Report;
            var local = ZoneTransform.Compose(inst.Translation, inst.RotationEuler, inst.Scale);
            var world = local * parentWorld;
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
                            AddMesh(inst, layer, mesh, inst.CollisionPath, world, parentWorld, inst.BgMatValue, inst.BgMatMask, true);
                        }
                    }
                    if (inst.Analytic is { } a)
                    {
                        ++report.BgPartsAnalytic;
                        var aWorld = ZoneTransform.Compose(a.Translation, a.RotationEuler, a.Scale) * world;
                        AddAnalytic(inst, layer, (LgbColliderKind)(byte)a.Kind, aWorld, a.MatValue, a.MatMask, true, parentWorld, a.Bounds);
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
                                AddMesh(inst, layer, mesh, inst.BoxMeshPath, world, parentWorld, inst.BoxMatValue, inst.BoxMatMask, inst.ActiveByDefault);
                            }
                        }
                    }
                    else if (inst.ColliderKind != LgbColliderKind.None)
                    {
                        AddAnalytic(inst, layer, inst.ColliderKind, world, inst.BoxMatValue, inst.BoxMatMask, inst.ActiveByDefault, parentWorld, null);
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
                            VisitDocument(doc, System.IO.Path.GetFileName(inst.SharedGroupPath), world, depth + 1, childChain);
                        }
                    }
                    break;
                case LgbInstanceType.ExitRange:
                case LgbInstanceType.PopRange:
                case LgbInstanceType.EventObject:
                    scene.Markers.Add(new ZoneMarker
                    {
                        Index = scene.Markers.Count,
                        Type = inst.Type,
                        Name = inst.Name,
                        LayoutObjectId = ZoneCollisionScene.MakeLayoutObjectId(inst.Type, layer.Key, inst.Key),
                        LayerIndex = layer.Index,
                        Position = world.Translation,
                    });
                    break;
                default:
                    report.UnparsedTypes[inst.Type] = report.UnparsedTypes.GetValueOrDefault(inst.Type) + 1;
                    break;
            }
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

        private void AddMesh(LgbInstance inst, ZoneLayer layer, PcbMesh mesh, string path, in Matrix4x4 world, in Matrix4x4 parentWorld, ulong matValue, ulong matMask, bool active)
        {
            var m = new ZoneMeshInstance
            {
                Index = scene.Meshes.Count,
                PcbPath = path,
                Name = inst.Name,
                LayoutObjectId = ZoneCollisionScene.MakeLayoutObjectId(inst.Type, layer.Key, inst.Key),
                LayerIndex = layer.Index,
                SourceInstanceType = inst.Type,
                InstanceKey = inst.Key,
                LayerKey = layer.Key,
                World = world,
                ParentWorld = parentWorld,
                Translation = inst.Translation,
                RotationEuler = inst.RotationEuler,
                Scale = inst.Scale,
                ObjMatValue = matValue,
                ObjMatMask = matMask,
                Mesh = mesh,
                WorldBounds = Bounds3.Transform(mesh.LocalBounds, world),
                ActiveByDefault = active,
            };
            scene.Meshes.Add(m);
        }

        private void AddAnalytic(LgbInstance inst, ZoneLayer layer, LgbColliderKind kind, in Matrix4x4 world, ulong matValue, ulong matMask, bool active, in Matrix4x4 parentWorld, Bounds3? analyticBounds)
        {
            var id = ZoneCollisionScene.MakeLayoutObjectId(inst.Type, layer.Key, inst.Key);
            // analytic bg-part boxes: the block's bounds give the local extents (the transform is usually unscaled); degenerate bounds fall back to the unit cube
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
                for (var i = 0; i < 8; ++i)
                {
                    var u = ZoneBoxInstance.UnitCorners[i];
                    box.Corners[i] = Vector3.Transform(new Vector3(lc.X + u.X * lh.X, lc.Y + u.Y * lh.Y, lc.Z + u.Z * lh.Z), world);
                }
                box.Center = Vector3.Transform(lc, world);
                box.HalfExtents = new(lh.X * new Vector3(world.M11, world.M12, world.M13).Length(), lh.Y * new Vector3(world.M21, world.M22, world.M23).Length(), lh.Z * new Vector3(world.M31, world.M32, world.M33).Length());
                box.WorldBounds = Bounds3.FromPoints(box.Corners);
                scene.Boxes.Add(box);
            }
            else
            {
                scene.Analytics.Add(new ZoneAnalyticInstance
                {
                    Index = scene.Analytics.Count,
                    Name = inst.Name,
                    LayoutObjectId = id,
                    LayerIndex = layer.Index,
                    Kind = kind,
                    World = world,
                    MatValue = matValue,
                    MatMask = matMask,
                    WorldBounds = Bounds3.Transform(new(new(-1f), new(1f)), world),
                });
            }
        }
    }
}
