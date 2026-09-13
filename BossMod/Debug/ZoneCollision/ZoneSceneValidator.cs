using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;
using Vector3 = System.Numerics.Vector3;

namespace BossMod;

// compares an offline-loaded zone scene with the live collision scene of the current zone; matches colliders by layout object id
// and reports which euler composition reproduces the live transforms, plus offset/material/path mismatches - this is how the file
// format assumptions get pinned without documentation
public sealed unsafe class ZoneSceneValidator
{
    public readonly struct LiveMesh(ulong layoutId, string path, Matrix4x4 world, Vector3 translation, Vector3 rotation, Vector3 scale, AABB bounds, int prims, ulong matValue, ulong matMask, ulong materialXor, ulong materialSum, int meshId, Vector3 firstVertex)
    {
        public readonly ulong LayoutId = layoutId;
        public readonly string Path = path;
        public readonly Matrix4x4 World = world;
        public readonly Vector3 Translation = translation, Rotation = rotation, Scale = scale;
        public readonly AABB Bounds = bounds;
        public readonly int Prims = prims;
        public readonly ulong MatValue = matValue, MatMask = matMask, MaterialXor = materialXor, MaterialSum = materialSum;
        public readonly int MeshId = meshId;
        public readonly Vector3 FirstVertex = firstVertex; // world position of node0 vertex0, for the compressed-vertex check
    }

    public readonly struct LiveBox(ulong layoutId, Matrix4x4 world, Vector3 translation, Vector3 rotation, Vector3 scale, ulong matValue, ulong matMask, ulong layerMask)
    {
        public readonly ulong LayoutId = layoutId;
        public readonly Matrix4x4 World = world;
        public readonly Vector3 Translation = translation, Rotation = rotation, Scale = scale;
        public readonly ulong MatValue = matValue, MatMask = matMask, LayerMask = layerMask;
    }

    public sealed class Report
    {
        public int LiveMeshes, LiveBoxes, LiveTiles, OfflineMeshes, OfflineBoxes, OfflineTiles;
        public int MatchedMeshes, MatchedBoxes, MatchedTiles, RotatedMatches;
        public readonly float[] OrderMaxError = new float[12]; // 6 euler orders x {scale first, rotation first}
        public readonly int[] OrderWins = new int[12];
        public float TranslationMaxError, EulerFieldMaxError, FirstVertexMaxError = float.NaN;
        public int PrimCountMismatches, MaterialMismatches, PathMismatches, BoxMaterialMismatches, TileMaterialMismatches;
        public readonly List<string> Worst = [];
        public readonly List<string> LiveOnly = [];
        public readonly List<string> OfflineOnly = [];
        public string ListPcbCheck = "";
        public string StreamedPath = "";
        public string Summary = "";

        public static string OrderName(int i) => $"{(EulerOrder)(i % 6)}{(i < 6 ? " SRT" : " RST")}";
    }

    public readonly List<LiveMesh> Meshes = [];
    public readonly List<LiveBox> Boxes = [];
    public readonly List<LiveMesh> Tiles = [];
    public string StreamedPath = "";
    public int StreamedListed;
    public string ListHeader = "";

    public void SnapshotLive()
    {
        Meshes.Clear();
        Boxes.Clear();
        Tiles.Clear();
        StreamedPath = "";
        StreamedListed = 0;
        ListHeader = "";
        var module = Framework.Instance()->BGCollisionModule;
        if (module == null || module->SceneManager == null)
        {
            return;
        }
        foreach (var s in module->SceneManager->Scenes)
        {
            foreach (var coll in s->Scene->Colliders)
            {
                switch (coll->GetColliderType())
                {
                    case ColliderType.Mesh:
                        {
                            var cm = (ColliderMesh*)coll;
                            if (!cm->MeshIsSimple && cm->Mesh != null)
                            {
                                Meshes.Add(Capture(cm, -1));
                            }
                            break;
                        }
                    case ColliderType.Box:
                        {
                            var box = (ColliderBox*)coll;
                            Boxes.Add(new(coll->LayoutObjectId, box->World.FullMatrix(), box->Translation, box->Rotation, box->Scale, coll->ObjectMaterialValue, coll->ObjectMaterialMask, coll->LayerMask));
                            break;
                        }
                    case ColliderType.Streamed:
                        {
                            var cs = (ColliderStreamed*)coll;
                            StreamedPath = cs->PathBaseString;
                            if (cs->Header != null && cs->Elements != null)
                            {
                                StreamedListed = cs->Header->NumMeshes;
                                ListHeader = $"sizeof(FileHeader)={sizeof(ColliderStreamed.FileHeader)} sizeof(FileEntry)={sizeof(ColliderStreamed.FileEntry)} NumMeshes={cs->Header->NumMeshes}";
                                if (cs->Entries != null && cs->Header->NumMeshes > 0)
                                {
                                    ListHeader += $" first=tr{cs->Entries[0].MeshId:d4} ({cs->Entries[0].Bounds.Min.X:f0},{cs->Entries[0].Bounds.Min.Z:f0})..({cs->Entries[0].Bounds.Max.X:f0},{cs->Entries[0].Bounds.Max.Z:f0})";
                                }
                                for (var i = 0; i < cs->Header->NumMeshes; ++i)
                                {
                                    var cm = cs->Elements[i].Mesh;
                                    if (cm != null && !cm->MeshIsSimple && cm->Mesh != null)
                                    {
                                        Tiles.Add(Capture(cm, cs->Entries != null ? cs->Entries[i].MeshId : i));
                                    }
                                }
                            }
                            break;
                        }
                }
            }
        }
    }

    private static LiveMesh Capture(ColliderMesh* cm, int meshId)
    {
        var mesh = (MeshPCB*)cm->Mesh;
        ulong xor = 0, sum = 0;
        var prims = 0;
        var first = new Vector3(float.NaN);
        WalkMaterials(mesh->RootNode, ref cm->World, ref xor, ref sum, ref prims, ref first);
        return new(cm->Collider.LayoutObjectId, cm->Resource != null ? cm->Resource->PathString : "", cm->World.FullMatrix(), cm->Translation, cm->Rotation, cm->Scale,
            cm->WorldBoundingBox, prims, cm->Collider.ObjectMaterialValue, cm->Collider.ObjectMaterialMask, xor, sum, meshId, first);
    }

    private static void WalkMaterials(MeshPCB.FileNode* node, ref Matrix4x3 world, ref ulong xor, ref ulong sum, ref int prims, ref Vector3 first)
    {
        var stack = new Stack<nint>();
        stack.Push((nint)node);
        while (stack.Count > 0)
        {
            var n = (MeshPCB.FileNode*)stack.Pop();
            if (n == null)
            {
                continue;
            }
            var numPrims = n->NumPrims;
            for (var i = 0; i < numPrims; ++i)
            {
                var m = n->Primitives[i].Material;
                xor ^= m;
                unchecked
                {
                    sum += m;
                }
            }
            prims += numPrims;
            if (float.IsNaN(first.X) && n->NumVertsRaw + n->NumVertsCompressed > 0)
            {
                first = world.TransformCoordinate(n->Vertex(0));
            }
            stack.Push((nint)n->Child2);
            stack.Push((nint)n->Child1);
        }
    }

    public static Report Compare(ZoneCollisionScene scene, ZoneSceneValidator live)
    {
        var r = new Report { LiveMeshes = live.Meshes.Count, LiveBoxes = live.Boxes.Count, LiveTiles = live.Tiles.Count, OfflineBoxes = scene.Boxes.Count, StreamedPath = live.StreamedPath };
        Dictionary<ulong, ZoneMeshInstance> offMeshes = [];
        Dictionary<uint, ZoneMeshInstance> offTiles = [];
        foreach (var m in scene.Meshes)
        {
            if (m.IsTerrainTile)
            {
                offTiles[m.TerrainMeshId] = m;
            }
            else
            {
                offMeshes[m.LayoutObjectId] = m;
            }
        }
        r.OfflineMeshes = offMeshes.Count;
        r.OfflineTiles = offTiles.Count;
        Dictionary<ulong, ZoneBoxInstance> offBoxes = [];
        foreach (var b in scene.Boxes)
        {
            offBoxes[b.LayoutObjectId] = b;
        }
        r.ListPcbCheck = $"{live.ListHeader} | offline entries={scene.TerrainList?.Entries.Length ?? 0}{(scene.TerrainList is { Entries.Length: > 0 } tl ? $" first=tr{tl.Entries[0].MeshId:d4} ({tl.Entries[0].Bounds.Min.X:f0},{tl.Entries[0].Bounds.Min.Z:f0})..({tl.Entries[0].Bounds.Max.X:f0},{tl.Entries[0].Bounds.Max.Z:f0})" : "")} | live streamed dir '{live.StreamedPath}' offline '{scene.CollisionDir}'";
        for (var i = 0; i < 12; ++i)
        {
            r.OrderMaxError[i] = 0f;
        }

        HashSet<ulong> seen = [];
        var worst = new List<(float err, string what)>();
        foreach (var lm in live.Meshes)
        {
            if (!offMeshes.TryGetValue(lm.LayoutId, out var om))
            {
                var (ik, lk, type) = ZoneCollisionScene.DecodeLayoutObjectId(lm.LayoutId);
                if (r.LiveOnly.Count < 40)
                {
                    r.LiveOnly.Add($"0x{lm.LayoutId:X16} type={type} layer={lk} inst={ik} {lm.Path}");
                }
                continue;
            }
            seen.Add(lm.LayoutId);
            ++r.MatchedMeshes;
            CompareTransform(r, lm.World, lm.Translation, lm.Rotation, om.Translation, om.RotationEuler, om.Scale, om.ParentWorld, worst, $"mesh 0x{lm.LayoutId:X16} {om.PcbPath}");
            if (lm.Prims != om.Mesh.TriangleCount)
            {
                ++r.PrimCountMismatches;
                worst.Add((1e6f, $"prims live {lm.Prims} offline {om.Mesh.TriangleCount} {om.PcbPath}"));
            }
            else
            {
                ulong xor = 0, sum = 0;
                foreach (var m in om.Mesh.Materials)
                {
                    xor ^= m;
                    unchecked
                    {
                        sum += m;
                    }
                }
                if (xor != lm.MaterialXor || sum != lm.MaterialSum)
                {
                    ++r.MaterialMismatches;
                }
            }
            if (!string.Equals(lm.Path, om.PcbPath, StringComparison.OrdinalIgnoreCase))
            {
                ++r.PathMismatches;
                if (r.PathMismatches <= 5)
                {
                    worst.Add((1e5f, $"path live '{lm.Path}' offline '{om.PcbPath}'"));
                }
            }
            if (!float.IsNaN(lm.FirstVertex.X) && om.Mesh.Vertices.Length > 0)
            {
                var v = Vector3.Transform(om.Mesh.Vertices[0], om.World);
                var d = (v - lm.FirstVertex).Length();
                r.FirstVertexMaxError = float.IsNaN(r.FirstVertexMaxError) ? d : MathF.Max(r.FirstVertexMaxError, d);
            }
        }
        foreach (var (id, om) in offMeshes)
        {
            if (!seen.Contains(id) && r.OfflineOnly.Count < 40)
            {
                r.OfflineOnly.Add($"0x{id:X16} layer '{scene.Layers[om.LayerIndex].Name}' {om.PcbPath}{(om.ActiveByDefault ? "" : " (inactive by default)")}");
            }
        }

        foreach (var lb in live.Boxes)
        {
            if (!offBoxes.TryGetValue(lb.LayoutId, out var ob))
            {
                var (ik, lk, type) = ZoneCollisionScene.DecodeLayoutObjectId(lb.LayoutId);
                if (r.LiveOnly.Count < 60)
                {
                    r.LiveOnly.Add($"box 0x{lb.LayoutId:X16} type={type} layer={lk} inst={ik} mat={lb.MatValue:X}/{lb.MatMask:X}");
                }
                continue;
            }
            ++r.MatchedBoxes;
            CompareTransform(r, lb.World, lb.Translation, lb.Rotation, ob.Translation, ob.RotationEuler, ob.Scale, Matrix4x4.Identity, worst, $"box 0x{lb.LayoutId:X16}");
            if (lb.MatValue != ob.MatValue || lb.MatMask != ob.MatMask)
            {
                ++r.BoxMaterialMismatches;
                if (r.BoxMaterialMismatches <= 5)
                {
                    worst.Add((1e5f, $"box material live {lb.MatValue:X}/{lb.MatMask:X} offline {ob.MatValue:X}/{ob.MatMask:X} 0x{lb.LayoutId:X16}"));
                }
            }
        }

        foreach (var lt in live.Tiles)
        {
            if (!offTiles.TryGetValue((uint)lt.MeshId, out var ot))
            {
                continue;
            }
            ++r.MatchedTiles;
            if (lt.Prims != ot.Mesh.TriangleCount)
            {
                ++r.TileMaterialMismatches;
            }
            else
            {
                ulong xor = 0;
                foreach (var m in ot.Mesh.Materials)
                {
                    xor ^= m;
                }
                if (xor != lt.MaterialXor)
                {
                    ++r.TileMaterialMismatches;
                }
            }
        }

        worst.Sort((a, b) => b.err.CompareTo(a.err));
        for (var i = 0; i < Math.Min(20, worst.Count); ++i)
        {
            r.Worst.Add($"{worst[i].err:g3}: {worst[i].what}");
        }
        var best = 0;
        for (var i = 1; i < 12; ++i)
        {
            if (r.OrderWins[i] > r.OrderWins[best])
            {
                best = i;
            }
        }
        var configured = (int)ZoneTransform.RotationOrder + (ZoneTransform.ScaleFirst ? 0 : 6);
        r.Summary = $"matched meshes {r.MatchedMeshes}/{r.LiveMeshes} live ({r.OfflineMeshes} offline), boxes {r.MatchedBoxes}/{r.LiveBoxes} ({r.OfflineBoxes} offline), tiles {r.MatchedTiles}/{r.LiveTiles}; rotated matches {r.RotatedMatches}; best order {Report.OrderName(best)} (wins {r.OrderWins[best]}, max err {r.OrderMaxError[best]:g3}); configured {Report.OrderName(configured)} (max err {r.OrderMaxError[configured]:g3}); translation max err {r.TranslationMaxError:g3}; euler field max err {r.EulerFieldMaxError:g3}; first-vertex max err {r.FirstVertexMaxError:g3}; prim mismatches {r.PrimCountMismatches}, material {r.MaterialMismatches}, path {r.PathMismatches}, box material {r.BoxMaterialMismatches}, tile {r.TileMaterialMismatches}";
        return r;
    }

    private static void CompareTransform(Report r, in Matrix4x4 liveWorld, in Vector3 liveTranslation, in Vector3 liveRotation, in Vector3 t, in Vector3 e, in Vector3 s, in Matrix4x4 parent, List<(float, string)> worst, string what)
    {
        r.EulerFieldMaxError = MathF.Max(r.EulerFieldMaxError, (liveRotation - e).Length());
        var rotated = e.LengthSquared() > 1e-6f;
        var bestErr = float.MaxValue;
        var bestIdx = -1;
        for (var i = 0; i < 12; ++i)
        {
            var m = ZoneTransform.Compose(t, e, s, (EulerOrder)(i % 6), i < 6) * parent;
            var err = MatrixError(m, liveWorld);
            if (rotated)
            {
                r.OrderMaxError[i] = MathF.Max(r.OrderMaxError[i], err);
            }
            if (err < bestErr)
            {
                bestErr = err;
                bestIdx = i;
            }
        }
        if (rotated)
        {
            ++r.RotatedMatches;
            ++r.OrderWins[bestIdx];
        }
        var tErr = (liveWorld.Translation - (ZoneTransform.Compose(t, e, s) * parent).Translation).Length();
        r.TranslationMaxError = MathF.Max(r.TranslationMaxError, tErr);
        if (bestErr > 1e-3f)
        {
            worst.Add((bestErr, $"transform {what} best {Report.OrderName(bestIdx)} err {bestErr:g3} t-err {tErr:g3}"));
        }
    }

    private static float MatrixError(in Matrix4x4 a, in Matrix4x4 b)
    {
        var e = 0f;
        e = MathF.Max(e, MathF.Abs(a.M11 - b.M11));
        e = MathF.Max(e, MathF.Abs(a.M12 - b.M12));
        e = MathF.Max(e, MathF.Abs(a.M13 - b.M13));
        e = MathF.Max(e, MathF.Abs(a.M21 - b.M21));
        e = MathF.Max(e, MathF.Abs(a.M22 - b.M22));
        e = MathF.Max(e, MathF.Abs(a.M23 - b.M23));
        e = MathF.Max(e, MathF.Abs(a.M31 - b.M31));
        e = MathF.Max(e, MathF.Abs(a.M32 - b.M32));
        e = MathF.Max(e, MathF.Abs(a.M33 - b.M33));
        e = MathF.Max(e, MathF.Abs(a.M41 - b.M41));
        e = MathF.Max(e, MathF.Abs(a.M42 - b.M42));
        e = MathF.Max(e, MathF.Abs(a.M43 - b.M43));
        return e;
    }
}
