using Clipper2Lib;

namespace BossMod;

public enum AdjacencyMode : byte { SharedEdge, SharedVertex }
public enum SealBlockMode : byte { BehindPlane, Centroid, AnyVertex }

// masked material match: (material & Mask) == (Value & Mask); Mask 0 = exact
public readonly record struct MaterialFilter(ulong Value, ulong Mask)
{
    public bool Matches(ulong material) => Mask == 0 ? material == Value : (material & Mask) == (Value & Mask);
    public override string ToString() => $"{Value:X}/{Mask:X}";
}

public sealed class AutoMapSettings
{
    // walkable floor materials: the 0x7xxx family covers terrain and most floors (0x7004 stone, 0x700A, 0x7005 ...); box colliders whose
    // object material matches (e.g. 0x700E transparent floor tiles) are treated as floor too
    public List<MaterialFilter> FloorMaterials = [new(0x7000, 0xF000)];
    public CollisionOutlinesExtractor.MaterialMatchMode FloorMatchMode = CollisionOutlinesExtractor.MaterialMatchMode.EffectiveMasked;
    public ulong SealMaterialValue = 0x2400;
    public ulong SealMaterialMask = 0x1FFFFFFFFF;
    public bool SealRequireExactMask = true;
    public bool SealIncludeInactive = true;
    // seal doors are thin, wide and not taller than they are wide; cubes and tall barriers with the same material are not seals
    public bool SealGeometryFilter = true;
    public float SealMaxThickness = 3f;
    public float SealMinWidth = 4f;
    public float SealMinWidthToHeight = 0.5f;
    public float SealPairMaxDistance = 80f;
    public float MaxRadius = 60f;
    public float MaxSlopeDeg = 45f;
    public float WeldEps = 1e-3f;
    public float StepHeight = 0.5f; // neighbours are linked across a vertical step up to this height (raised plates, kerbs); 0 = exact 3D weld only
    public float SeamClose = 0.05f; // morphological closing radius applied to the floor union: fuses hairline slits between plates/tiles whose outlines do not coincide exactly (they would become notches once the bounds are offset inwards)
    public AdjacencyMode Adjacency = AdjacencyMode.SharedEdge;
    public SealBlockMode SealBlock = SealBlockMode.BehindPlane;
    public float SealBehindDepth = 12f;       // BehindPlane: how far behind the seal plane triangles are blocked
    public float SealFootprintInflate = 0.05f;
    public float BoxFloorTouchEps = 0.15f;    // floor boxes connect to floor triangles within this XZ distance of their footprint
    public float BoxFloorTouchHeight = 2.5f;  // ... and within this vertical distance (a step or a plank above the ground)
    public float SeedSearchRadius = 2f;
    public float SnapEpsXZ = 1e-5f;
    public float MinArea = 0.05f; // drop union fragments and holes below this area (yalm2): hairline gaps between floor plates, snapping slivers
    public bool KeepPolygonContainingCentre = true;
    // walls and props are separate meshes standing on the floor; their XZ footprints (inflated so vertical faces become thin strips) are cut out of the floor polygon
    public bool CutObstacles = true;
    public float ObstacleInflate = 0.05f;
    public float ObstacleHeightBelow = 0.5f; // obstacles whose top is more than this below the floor are ignored
    public float ObstacleHeightAbove = 2.5f; // obstacles entirely above floor + this are ignored (ceilings, bridges)
    public float ObstacleMinHeight = 0.25f;  // ignore flat decals / tiny steps
    public bool ObstacleLocalHeight = true;  // measure the height band against the selected floor under each obstacle (multi-level rooms) instead of the whole selection's Y range
    // props without a collision mesh are box colliders: a designer-placed CollisionBox (mask 1FFFFFFFFF, material 0x2000 = the wall material)
    // usually paired with the model's own analytic box (mask FFFFFFFF, material 0x3005); boxes matching this list that stand on the floor are cut out
    public bool CutBoxes = true;
    public List<MaterialFilter> ObstacleBoxMaterials = [new(0x2000, 0xF000)];
    public int ObstacleMaxTriangles = 20000; // skip the cut (with a warning) when the selection reaches this many obstacle triangles; shrink the radius or the leak first
    // the player's wall collision stops the character before the floor's steep rim is reached: extend the floor across non-wall slopes adjoining
    // the selection boundary, but only within this distance of the boundary, so the real wall cut defines the edge instead of the slope start
    public float RimExtension = 1f;
    public float RimMaxSlopeDeg = 80f;   // steeper faces are walls, never rim
    public int RimHops = 3;
    public bool RimRequireWall = true; // only keep rim slopes whose chain reaches a wall (steeper than RimMaxSlopeDeg); stops rocky cave floors from growing through open slopes
    // orphan boundary edges (no walkable neighbour) with a wall within this distance are extended to the wall foot: the player's collision
    // stops at the wall, so the unwalkable sliver in between (a steep lip, a gap in the mesh) counts as floor and the arena edge lands on the wall vertices
    public float WallSnap = 0.5f;
    public bool WallSnapVertices = true; // move final vertices within the obstacle inflate of a snapped wall foot onto the foot line / corner

    public bool FloorMatches(in WorldTriangle t)
    {
        var usePrim = FloorMatchMode is CollisionOutlinesExtractor.MaterialMatchMode.PrimExact or CollisionOutlinesExtractor.MaterialMatchMode.PrimMasked;
        var material = usePrim ? t.Material : t.Effective;
        var exact = FloorMatchMode is CollisionOutlinesExtractor.MaterialMatchMode.PrimExact or CollisionOutlinesExtractor.MaterialMatchMode.EffectiveExact;
        for (var i = 0; i < FloorMaterials.Count; ++i)
        {
            var f = FloorMaterials[i];
            if (exact ? material == f.Value : f.Matches(material))
            {
                return true;
            }
        }
        return false;
    }

    public bool BoxIsFloor(ZoneBoxInstance box)
    {
        if (box.Kind != LgbColliderKind.Box)
        {
            return false;
        }
        for (var i = 0; i < FloorMaterials.Count; ++i)
        {
            if (FloorMaterials[i].Matches(box.MatValue))
            {
                return true;
            }
        }
        return false;
    }

    public float MinNormalY => MathF.Cos(MaxSlopeDeg * MathF.PI / 180f);

    public bool BoxIsObstacle(ZoneBoxInstance box)
    {
        if (box.Kind != LgbColliderKind.Box || BoxIsFloor(box))
        {
            return false;
        }
        for (var i = 0; i < ObstacleBoxMaterials.Count; ++i)
        {
            if (ObstacleBoxMaterials[i].Matches(box.MatValue))
            {
                return true;
            }
        }
        return false;
    }

    public AutoMapSettings Clone()
    {
        var c = (AutoMapSettings)MemberwiseClone();
        c.ObstacleBoxMaterials = [.. ObstacleBoxMaterials];
        c.FloorMaterials = [.. FloorMaterials];
        return c;
    }
}

public readonly record struct SealCandidate(int BoxIndex, Vector3 Center, Vector3 ThinAxisWorld, Vector3 LongAxisWorld, float Thickness, float Width, float Height, bool PassesGeometry, Path64 FootprintXZ, Path64 InflatedFootprintXZ);

// a candidate pairing for the centre estimate: two seals, a seal and a layout marker (exit range / event object), or a single seal
public readonly record struct SealPair(int SealA, int SealB, int MarkerIndex, float Distance, string Kind)
{
    public bool IsSingle => SealB < 0 && MarkerIndex < 0;
}

public readonly record struct CentreEstimate(Vector3 Centre, int SealA, int SealB, int MarkerIndex, float RayGap, bool UsedFallback, string Reason);

// candidate floor triangles (material/slope/radius) plus floor boxes as extra nodes, with CSR neighbour lists built by welding vertices on a quantized grid
public sealed class TriangleAdjacency
{
    public int[] Candidates = [];      // global triangle indices, local node i < Candidates.Length
    public int[] FloorBoxes = [];      // box indices, local node Candidates.Length + j
    public int[] NeighbourStart = [];
    public int[] Neighbours = [];
    public readonly Dictionary<int, int> GlobalToLocal = [];
    public long BuildMs;
    public int WeldedVertices, Edges, NonManifoldEdges, BoxLinks;
    public float WeldEpsUsed;

    public int NodeCount => Candidates.Length + FloorBoxes.Length;
    public bool IsBoxNode(int local) => local >= Candidates.Length;

    private const long CellOffset = 1L << 20;
    private const ulong CellMask = (1UL << 21) - 1;

    private static ulong PackCell(long x, long y, long z) => (((ulong)(x + CellOffset) & CellMask) << 42) | (((ulong)(y + CellOffset) & CellMask) << 21) | ((ulong)(z + CellOffset) & CellMask);

    private static ulong CellKey(in Vector3 v, float eps) => PackCell((long)MathF.Floor(v.X / eps), (long)MathF.Floor(v.Y / eps), (long)MathF.Floor(v.Z / eps));

    // floorMeshes: meshes the author marked as floor regardless of material (transparent platforms, odd surface ids)
    public static TriangleAdjacency Build(ZoneCollisionScene scene, in Vector3 centre, AutoMapSettings s, IReadOnlySet<int>? floorMeshes = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var adj = new TriangleAdjacency();
        var tris = scene.Triangles.Span;
        var minNormalY = s.MinNormalY;
        var r2 = s.MaxRadius * s.MaxRadius;
        var cxz = new Vector2(centre.X, centre.Z);
        List<int> candidates = [];
        var meshes = scene.Meshes;
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (mesh.TriCount == 0 || !scene.IsMeshEnabled(m) || !mesh.WorldBounds.IntersectsXZCircle(cxz, s.MaxRadius))
            {
                continue;
            }
            var forcedFloor = floorMeshes != null && floorMeshes.Contains(m);
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                ref readonly var t = ref tris[i];
                if (t.NormalY < minNormalY || (!forcedFloor && !s.FloorMatches(t)))
                {
                    continue;
                }
                var c = t.Centroid;
                var dx = c.X - centre.X;
                var dz = c.Z - centre.Z;
                if (dx * dx + dz * dz > r2)
                {
                    continue;
                }
                candidates.Add(i);
            }
        }
        adj.Candidates = [.. candidates];
        var n = adj.Candidates.Length;
        for (var i = 0; i < n; ++i)
        {
            adj.GlobalToLocal[adj.Candidates[i]] = i;
        }

        // floor boxes inside the radius become extra nodes
        List<int> floorBoxes = [];
        for (var b = 0; b < scene.Boxes.Count; ++b)
        {
            var box = scene.Boxes[b];
            if (scene.IsBoxEnabled(b) && s.BoxIsFloor(box) && box.WorldBounds.IntersectsXZCircle(cxz, s.MaxRadius))
            {
                floorBoxes.Add(b);
            }
        }
        adj.FloorBoxes = [.. floorBoxes];

        // weld vertices; 21 bits per axis at eps covers +-(2^20 * eps) yalms, fall back to a coarser grid for huge coordinates
        var eps = s.WeldEps;
        var maxCoord = MathF.Max(MathF.Abs(scene.Bounds.Min.X), MathF.Max(MathF.Abs(scene.Bounds.Max.X), MathF.Max(MathF.Abs(scene.Bounds.Min.Z), MathF.Abs(scene.Bounds.Max.Z))));
        while (maxCoord / eps >= CellOffset - 1 && eps < 1f)
        {
            eps *= 2f;
        }
        adj.WeldEpsUsed = eps;
        // with a step tolerance vertices weld in XZ only and each link is checked for its Y difference afterwards, so a raised
        // plate (0.1 y step) still links to its neighbour while the ceiling slab above the floor does not
        var stepWeld = s.StepHeight > 0f;
        Dictionary<ulong, int> weld = new(n * 2);
        var triVerts = new int[3 * n];
        var triY = new float[3 * n];
        for (var i = 0; i < n; ++i)
        {
            ref readonly var t = ref tris[adj.Candidates[i]];
            triVerts[3 * i] = stepWeld ? WeldIdXZ(weld, t.A, eps) : WeldId(weld, t.A, eps);
            triVerts[3 * i + 1] = stepWeld ? WeldIdXZ(weld, t.B, eps) : WeldId(weld, t.B, eps);
            triVerts[3 * i + 2] = stepWeld ? WeldIdXZ(weld, t.C, eps) : WeldId(weld, t.C, eps);
            triY[3 * i] = t.A.Y;
            triY[3 * i + 1] = t.B.Y;
            triY[3 * i + 2] = t.C.Y;
        }
        adj.WeldedVertices = weld.Count;
        // true when every XZ-welded vertex the two triangles share sits within the step height in Y
        bool StepOk(int a, int b)
        {
            for (var ka = 0; ka < 3; ++ka)
            {
                for (var kb = 0; kb < 3; ++kb)
                {
                    if (triVerts[3 * a + ka] == triVerts[3 * b + kb] && MathF.Abs(triY[3 * a + ka] - triY[3 * b + kb]) > s.StepHeight)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        var nodeCount = adj.NodeCount;
        var counts = new int[nodeCount + 1];
        List<(int a, int b)> pairs = [];
        if (s.Adjacency == AdjacencyMode.SharedEdge)
        {
            Dictionary<ulong, (int first, int second)> edgeMap = new(3 * n);
            for (var i = 0; i < n; ++i)
            {
                for (var k = 0; k < 3; ++k)
                {
                    var v0 = triVerts[3 * i + k];
                    var v1 = triVerts[3 * i + (k + 1) % 3];
                    if (v0 == v1)
                    {
                        continue;
                    }
                    var key = ((ulong)(uint)Math.Min(v0, v1) << 32) | (uint)Math.Max(v0, v1);
                    if (!edgeMap.TryGetValue(key, out var e))
                    {
                        edgeMap[key] = (i, -1);
                        ++adj.Edges;
                    }
                    else if (e.second < 0)
                    {
                        edgeMap[key] = (e.first, i);
                        if (!stepWeld || StepOk(e.first, i))
                        {
                            pairs.Add((e.first, i));
                        }
                    }
                    else
                    {
                        // non-manifold: connect to every triangle already on the edge
                        if (!stepWeld || StepOk(e.first, i))
                        {
                            pairs.Add((e.first, i));
                        }
                        if (!stepWeld || StepOk(e.second, i))
                        {
                            pairs.Add((e.second, i));
                        }
                        ++adj.NonManifoldEdges;
                    }
                }
            }
        }
        else
        {
            Dictionary<int, List<int>> vertexTris = new(weld.Count);
            for (var i = 0; i < n; ++i)
            {
                for (var k = 0; k < 3; ++k)
                {
                    var v = triVerts[3 * i + k];
                    if (!vertexTris.TryGetValue(v, out var list))
                    {
                        vertexTris[v] = list = [];
                    }
                    if (list.Count == 0 || list[^1] != i)
                    {
                        list.Add(i);
                    }
                }
            }
            foreach (var list in vertexTris.Values)
            {
                for (var a = 0; a < list.Count; ++a)
                {
                    for (var b = a + 1; b < list.Count; ++b)
                    {
                        if (!stepWeld || StepOk(list[a], list[b]))
                        {
                            pairs.Add((list[a], list[b]));
                        }
                    }
                }
            }
        }

        // floor boxes link to every candidate triangle touching their (inflated) footprint, and to each other when their footprints touch
        var scale = BoxFootprintOps.DefaultScale;
        var boxPaths = new Path64[adj.FloorBoxes.Length];
        for (var j = 0; j < adj.FloorBoxes.Length; ++j)
        {
            var box = scene.Boxes[adj.FloorBoxes[j]];
            boxPaths[j] = BoxFootprintOps.BoxFootprint(box.Corners, s.BoxFloorTouchEps, scale);
            var wb = box.WorldBounds;
            var e2 = s.BoxFloorTouchEps;
            var ey = s.BoxFloorTouchHeight;
            for (var i = 0; i < n; ++i)
            {
                ref readonly var t = ref tris[adj.Candidates[i]];
                var tb = t.Bounds;
                if (tb.Max.X < wb.Min.X - e2 || tb.Min.X > wb.Max.X + e2 || tb.Max.Z < wb.Min.Z - e2 || tb.Min.Z > wb.Max.Z + e2 || tb.Max.Y < wb.Min.Y - ey || tb.Min.Y > wb.Max.Y + ey)
                {
                    continue;
                }
                if (PointIn(boxPaths[j], t.A, scale) || PointIn(boxPaths[j], t.B, scale) || PointIn(boxPaths[j], t.C, scale) || PointIn(boxPaths[j], t.Centroid, scale))
                {
                    pairs.Add((i, n + j));
                    ++adj.BoxLinks;
                }
            }
            for (var k = 0; k < j; ++k)
            {
                var other = scene.Boxes[adj.FloorBoxes[k]].WorldBounds;
                if (wb.Max.X >= other.Min.X - e2 && wb.Min.X <= other.Max.X + e2 && wb.Max.Z >= other.Min.Z - e2 && wb.Min.Z <= other.Max.Z + e2 && wb.Max.Y >= other.Min.Y - ey && wb.Min.Y <= other.Max.Y + ey)
                {
                    pairs.Add((n + k, n + j));
                    ++adj.BoxLinks;
                }
            }
        }

        foreach (var (a, b) in pairs)
        {
            ++counts[a];
            ++counts[b];
        }
        adj.NeighbourStart = new int[nodeCount + 1];
        for (var i = 0; i < nodeCount; ++i)
        {
            adj.NeighbourStart[i + 1] = adj.NeighbourStart[i] + counts[i];
        }
        adj.Neighbours = new int[adj.NeighbourStart[nodeCount]];
        var fill = new int[nodeCount];
        foreach (var (a, b) in pairs)
        {
            adj.Neighbours[adj.NeighbourStart[a] + fill[a]++] = b;
            adj.Neighbours[adj.NeighbourStart[b] + fill[b]++] = a;
        }
        adj.BuildMs = sw.ElapsedMilliseconds;
        return adj;
    }

    private static bool PointIn(Path64 poly, in Vector3 p, long scale)
        => poly.Count >= 3 && Clipper.PointInPolygon(new Point64((long)Math.Round(p.X * scale), (long)Math.Round(p.Z * scale)), poly) != PointInPolygonResult.IsOutside;

    private static int WeldIdXZ(Dictionary<ulong, int> weld, in Vector3 v, float eps)
    {
        var key = PackCell((long)MathF.Floor(v.X / eps), 0, (long)MathF.Floor(v.Z / eps));
        if (!weld.TryGetValue(key, out var id))
        {
            id = weld.Count;
            weld[key] = id;
        }
        return id;
    }

    private static int WeldId(Dictionary<ulong, int> weld, in Vector3 v, float eps)
    {
        var key = CellKey(v, eps);
        if (!weld.TryGetValue(key, out var id))
        {
            id = weld.Count;
            weld[key] = id;
        }
        return id;
    }

    public ReadOnlySpan<int> NeighboursOf(int local) => Neighbours.AsSpan(NeighbourStart[local], NeighbourStart[local + 1] - NeighbourStart[local]);
}

public sealed class AutoMapResult
{
    public Vector3 Centre;
    public int SeedTriangle = -1;
    public int[] Selected = [];
    public int[] SelectedBoxes = [];
    public int[] BlockedBySeal = [];
    public string Status = "";
    public long AdjacencyMs, FillMs;
}

public static class ArenaAutoMapper
{
    public static List<SealCandidate> FindSeals(ZoneCollisionScene scene, AutoMapSettings s)
    {
        List<SealCandidate> result = [];
        var boxes = scene.Boxes;
        for (var i = 0; i < boxes.Count; ++i)
        {
            var b = boxes[i];
            if (b.Kind != LgbColliderKind.Box || !scene.IsBoxEnabled(i))
            {
                continue;
            }
            if ((b.MatValue & s.SealMaterialMask) != (s.SealMaterialValue & s.SealMaterialMask))
            {
                continue;
            }
            if (s.SealRequireExactMask && b.MatMask != s.SealMaterialMask)
            {
                continue;
            }
            if (!s.SealIncludeInactive && !b.ActiveByDefault)
            {
                continue;
            }
            var seal = MakeSeal(b, s);
            if (!s.SealGeometryFilter || seal.PassesGeometry)
            {
                result.Add(seal);
            }
        }
        return result;
    }

    public static SealCandidate MakeSeal(ZoneBoxInstance b, AutoMapSettings s)
    {
        // basis rows of the world matrix scaled by the local half extents are the box half-axes: thin = shortest, long = longest horizontal-ish, height = the most vertical
        var lh = b.LocalBounds.Size * 0.5f;
        var rows = new[] { new Vector3(b.World.M11, b.World.M12, b.World.M13) * lh.X, new Vector3(b.World.M21, b.World.M22, b.World.M23) * lh.Y, new Vector3(b.World.M31, b.World.M32, b.World.M33) * lh.Z };
        var thin = 0;
        var vertical = 0;
        for (var k = 1; k < 3; ++k)
        {
            if (rows[k].LengthSquared() < rows[thin].LengthSquared())
            {
                thin = k;
            }
            if (MathF.Abs(rows[k].Y) / MathF.Max(rows[k].Length(), 1e-6f) > MathF.Abs(rows[vertical].Y) / MathF.Max(rows[vertical].Length(), 1e-6f))
            {
                vertical = k;
            }
        }
        var longAxis = 3 - thin - vertical;
        if (longAxis == thin || longAxis == vertical || longAxis < 0 || longAxis > 2)
        {
            longAxis = thin == 0 ? 1 : 0;
        }
        var thinLen = rows[thin].Length();
        var axis = thinLen > 1e-6f ? rows[thin] / thinLen : Vector3.UnitX;
        var longLen = rows[longAxis].Length();
        var longDir = longLen > 1e-6f ? rows[longAxis] / longLen : Vector3.UnitZ;
        var thickness = 2f * thinLen;
        var width = 2f * longLen;
        var height = 2f * rows[vertical].Length();
        var passes = thickness <= s.SealMaxThickness && width >= s.SealMinWidth && width >= height * s.SealMinWidthToHeight && vertical != thin;
        var footprint = BoxFootprintOps.BoxFootprint(b.Corners, 0f);
        var inflated = BoxFootprintOps.BoxFootprint(b.Corners, s.SealFootprintInflate);
        return new(b.Index, b.Center, axis, longDir, thickness, width, height, passes, footprint, inflated);
    }

    // candidate pairings: every two seals within range (both oriented toward each other), every seal + marker within range, and each seal alone
    public static List<SealPair> ComputePairs(List<SealCandidate> seals, List<ZoneMarker> markers, AutoMapSettings s)
    {
        List<SealPair> pairs = [];
        for (var i = 0; i < seals.Count; ++i)
        {
            for (var j = i + 1; j < seals.Count; ++j)
            {
                var d = (seals[j].Center - seals[i].Center).Length();
                if (d <= s.SealPairMaxDistance && d >= 0.5f)
                {
                    pairs.Add(new(i, j, -1, d, "seals"));
                }
            }
        }
        for (var i = 0; i < seals.Count; ++i)
        {
            for (var m = 0; m < markers.Count; ++m)
            {
                if (markers[m].Type != (int)LgbInstanceType.ExitRange)
                {
                    continue;
                }
                var d = (markers[m].Position - seals[i].Center).Length();
                if (d <= s.SealPairMaxDistance && d >= 0.5f)
                {
                    pairs.Add(new(i, -1, m, d, "seal + exit"));
                }
            }
        }
        for (var i = 0; i < seals.Count; ++i)
        {
            pairs.Add(new(i, -1, -1, 0f, "single seal"));
        }
        return pairs;
    }

    public static CentreEstimate EstimateCentre(in SealPair pair, List<SealCandidate> seals, List<ZoneMarker> markers, AutoMapSettings s, Vector3? preferNear)
    {
        if (pair.SealA < 0 || pair.SealA >= seals.Count)
        {
            return new(preferNear ?? Vector3.Zero, -1, -1, -1, 0f, true, "no seals");
        }
        var a = seals[pair.SealA];
        if (pair.SealB >= 0 && pair.SealB < seals.Count)
        {
            return TwoRays(pair, a, seals[pair.SealB]);
        }
        var d = a.ThinAxisWorld;
        if (pair.MarkerIndex >= 0 && pair.MarkerIndex < markers.Count)
        {
            // foot of the perpendicular from the marker onto the seal's inward ray, capped to the pair distance
            var target = markers[pair.MarkerIndex].Position;
            if (Vector3.Dot(d, target - a.Center) < 0f)
            {
                d = -d;
            }
            var dxz = Vector2.Normalize(new(d.X, d.Z));
            var w = new Vector2(target.X - a.Center.X, target.Z - a.Center.Z);
            var t = Math.Clamp(Vector2.Dot(w, dxz), 0f, w.Length());
            var c = new Vector2(a.Center.X, a.Center.Z) + dxz * (t * 0.5f);
            return new(new(c.X, (a.Center.Y + target.Y) * 0.5f, c.Y), pair.SealA, -1, pair.MarkerIndex, 0f, false, "seal + exit range");
        }
        if (preferNear is { } pn && Vector3.Dot(d, pn - a.Center) < 0f)
        {
            d = -d;
        }
        return new(a.Center + d * (s.MaxRadius * 0.5f), pair.SealA, -1, -1, 0f, true, "single seal");
    }

    private static CentreEstimate TwoRays(in SealPair pair, in SealCandidate sa, in SealCandidate sb)
    {
        var ci = sa.Center;
        var cj = sb.Center;
        var di = sa.ThinAxisWorld;
        var dj = sb.ThinAxisWorld;
        if (Vector3.Dot(di, cj - ci) < 0f)
        {
            di = -di;
        }
        if (Vector3.Dot(dj, ci - cj) < 0f)
        {
            dj = -dj;
        }
        var pi = new Vector2(ci.X, ci.Z);
        var pj = new Vector2(cj.X, cj.Z);
        var ei = Vector2.Normalize(new(di.X, di.Z));
        var ej = Vector2.Normalize(new(dj.X, dj.Z));
        var w = pi - pj;
        var a = Vector2.Dot(ei, ei);
        var b = Vector2.Dot(ei, ej);
        var c = Vector2.Dot(ej, ej);
        var d = Vector2.Dot(ei, w);
        var e = Vector2.Dot(ej, w);
        var denom = a * c - b * b;
        var y = (ci.Y + cj.Y) * 0.5f;
        if (MathF.Abs(b) > 0.99f || MathF.Abs(denom) < 1e-4f)
        {
            var mid = (pi + pj) * 0.5f;
            return new(new(mid.X, y, mid.Y), pair.SealA, pair.SealB, -1, 0f, true, "rays parallel - midpoint");
        }
        var t = (b * e - c * d) / denom;
        var u = (a * e - b * d) / denom;
        if (t < 0f || u < 0f)
        {
            var mid = (pi + pj) * 0.5f;
            return new(new(mid.X, y, mid.Y), pair.SealA, pair.SealB, -1, 0f, true, "rays diverge - midpoint");
        }
        var qi = pi + ei * t;
        var qj = pj + ej * u;
        var centre = (qi + qj) * 0.5f;
        return new(new(centre.X, y, centre.Y), pair.SealA, pair.SealB, -1, (qi - qj).Length(), false, "ray intersection");
    }

    public static int FindSeed(ZoneCollisionScene scene, TriangleAdjacency adj, in Vector3 centre, AutoMapSettings s)
    {
        var tris = scene.Triangles.Span;
        var best = -1;
        var bestDy = float.MaxValue;
        var n = adj.Candidates.Length;
        for (var i = 0; i < n; ++i)
        {
            ref readonly var t = ref tris[adj.Candidates[i]];
            if (!t.ContainsXZ(centre.X, centre.Z))
            {
                continue;
            }
            var dy = MathF.Abs(t.YAt(centre.X, centre.Z) - centre.Y);
            if (dy < bestDy)
            {
                bestDy = dy;
                best = adj.Candidates[i];
            }
        }
        if (best >= 0)
        {
            return best;
        }
        var bestD = s.SeedSearchRadius * s.SeedSearchRadius;
        for (var i = 0; i < n; ++i)
        {
            ref readonly var t = ref tris[adj.Candidates[i]];
            var c = t.Centroid;
            var dx = c.X - centre.X;
            var dz = c.Z - centre.Z;
            var d = dx * dx + dz * dz;
            if (d < bestD)
            {
                bestD = d;
                best = adj.Candidates[i];
            }
        }
        return best;
    }

    public static bool InsideSeal(in Vector3 p, List<SealCandidate> seals, IReadOnlySet<int> active, long scale)
    {
        var pt = new Point64((long)Math.Round(p.X * scale), (long)Math.Round(p.Z * scale));
        for (var i = 0; i < seals.Count; ++i)
        {
            if (!active.Contains(seals[i].BoxIndex))
            {
                continue;
            }
            var poly = seals[i].InflatedFootprintXZ;
            if (poly.Count >= 3 && Clipper.PointInPolygon(pt, poly) != PointInPolygonResult.IsOutside)
            {
                return true;
            }
        }
        return false;
    }

    // BehindPlane: the triangle is blocked only when it lies entirely behind the seal (on the far side of the inward-facing plane) within the
    // seal's width; triangles straddling the seal stay floor and the seal footprint cuts them later
    private static bool BehindSeal(in WorldTriangle t, List<SealCandidate> seals, IReadOnlySet<int> active, in Vector3 centre, float depth)
    {
        for (var i = 0; i < seals.Count; ++i)
        {
            var seal = seals[i];
            if (!active.Contains(seal.BoxIndex))
            {
                continue;
            }
            var n = new Vector2(seal.ThinAxisWorld.X, seal.ThinAxisWorld.Z);
            if (n.LengthSquared() < 1e-6f)
            {
                continue;
            }
            n = Vector2.Normalize(n);
            var c = new Vector2(seal.Center.X, seal.Center.Z);
            if (Vector2.Dot(n, new Vector2(centre.X, centre.Z) - c) < 0f)
            {
                n = -n; // inward
            }
            var l = new Vector2(seal.LongAxisWorld.X, seal.LongAxisWorld.Z);
            l = l.LengthSquared() > 1e-6f ? Vector2.Normalize(l) : new Vector2(-n.Y, n.X);
            var da = Vector2.Dot(n, new Vector2(t.A.X, t.A.Z) - c);
            var db = Vector2.Dot(n, new Vector2(t.B.X, t.B.Z) - c);
            var dc = Vector2.Dot(n, new Vector2(t.C.X, t.C.Z) - c);
            var half = seal.Thickness * 0.5f;
            if (da > -half || db > -half || dc > -half)
            {
                continue; // some vertex in front of (or inside) the seal: not entirely behind
            }
            if (MathF.Min(da, MathF.Min(db, dc)) < -depth)
            {
                continue; // far behind: some other path, let the polygon cut decide
            }
            var cen = t.Centroid;
            var lateral = MathF.Abs(Vector2.Dot(l, new Vector2(cen.X, cen.Z) - c));
            if (lateral <= seal.Width * 0.5f + 1f)
            {
                return true;
            }
        }
        return false;
    }

    public static AutoMapResult FloodFill(ZoneCollisionScene scene, TriangleAdjacency adj, int seed, List<SealCandidate> seals, IReadOnlySet<int> activeSeals, in Vector3 centre, AutoMapSettings s)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new AutoMapResult { Centre = centre, SeedTriangle = seed, AdjacencyMs = adj.BuildMs };
        if (seed < 0 || !adj.GlobalToLocal.TryGetValue(seed, out var seedLocal))
        {
            result.Status = "no floor triangle at the centre";
            return result;
        }
        var tris = scene.Triangles.Span;
        var nodeCount = adj.NodeCount;
        var visited = new bool[nodeCount];
        var queue = new int[nodeCount];
        var head = 0;
        var tail = 0;
        List<int> selected = [];
        List<int> selectedBoxes = [];
        List<int> blocked = [];
        visited[seedLocal] = true;
        queue[tail++] = seedLocal;
        var scale = BoxFootprintOps.DefaultScale;
        while (head < tail)
        {
            var cur = queue[head++];
            if (adj.IsBoxNode(cur))
            {
                selectedBoxes.Add(adj.FloorBoxes[cur - adj.Candidates.Length]);
            }
            else
            {
                selected.Add(adj.Candidates[cur]);
            }
            foreach (var nb in adj.NeighboursOf(cur))
            {
                if (visited[nb])
                {
                    continue;
                }
                visited[nb] = true;
                if (!adj.IsBoxNode(nb))
                {
                    ref readonly var t = ref tris[adj.Candidates[nb]];
                    var blockedBySeal = s.SealBlock switch
                    {
                        SealBlockMode.Centroid => InsideSeal(t.Centroid, seals, activeSeals, scale),
                        SealBlockMode.AnyVertex => InsideSeal(t.A, seals, activeSeals, scale) || InsideSeal(t.B, seals, activeSeals, scale) || InsideSeal(t.C, seals, activeSeals, scale),
                        _ => BehindSeal(t, seals, activeSeals, centre, s.SealBehindDepth),
                    };
                    if (blockedBySeal)
                    {
                        blocked.Add(adj.Candidates[nb]);
                        continue;
                    }
                }
                queue[tail++] = nb;
            }
        }
        result.Selected = [.. selected];
        result.SelectedBoxes = [.. selectedBoxes];
        result.BlockedBySeal = [.. blocked];
        result.FillMs = sw.ElapsedMilliseconds;
        result.Status = $"filled {selected.Count} triangles and {selectedBoxes.Count} floor box(es) from {adj.Candidates.Length} candidates, {blocked.Count} blocked by seals";
        return result;
    }

    // XZ footprints of non-walkable triangles near the selection (walls, props, steep faces) as inflated strips, for cutting the floor polygon
    // selected triangles are floor by definition and never cut; meshes marked as floor or excluded never cut either
    public static Paths64 ObstacleFootprints(ZoneCollisionScene scene, ReadOnlySpan<int> floorTriangles, AutoMapSettings s, out int obstacleTriangles, IReadOnlySet<int>? excludedMeshes = null, IReadOnlySet<int>? floorMeshes = null, IReadOnlySet<int>? rimTriangles = null, Paths64? rimBand = null)
    {
        obstacleTriangles = 0;
        var tris = scene.Triangles.Span;
        HashSet<int> selected = new(floorTriangles.Length);
        for (var i = 0; i < floorTriangles.Length; ++i)
        {
            selected.Add(floorTriangles[i]);
        }
        // rim slopes are walkable inside the rim band (up to the wall) but stay obstacles outside it, so a slope that also
        // covers a leaked part of the fill keeps carving it there
        var rimEdges = new Paths64();
        var floorMinY = float.MaxValue;
        var floorMaxY = float.MinValue;
        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;
        for (var i = 0; i < floorTriangles.Length; ++i)
        {
            var b = tris[floorTriangles[i]].Bounds;
            floorMinY = MathF.Min(floorMinY, b.Min.Y);
            floorMaxY = MathF.Max(floorMaxY, b.Max.Y);
            minX = MathF.Min(minX, b.Min.X);
            minZ = MathF.Min(minZ, b.Min.Z);
            maxX = MathF.Max(maxX, b.Max.X);
            maxZ = MathF.Max(maxZ, b.Max.Z);
        }
        if (floorTriangles.Length == 0)
        {
            return [];
        }
        var bandMin = floorMinY - s.ObstacleHeightBelow;
        var bandMax = floorMaxY + s.ObstacleHeightAbove;
        // the height band is evaluated against the floor directly under each obstacle, not the global floor range:
        // a multi-level room (bridges, upper galleries) would otherwise project geometry from other levels onto the arena
        var floorGrid = new FloorHeightGrid(tris, floorTriangles, minX, minZ, maxX, maxZ);
        var scale = BoxFootprintOps.DefaultScale;
        var minNormalY = s.MinNormalY;
        var edges = new Paths64();
        var meshes = scene.Meshes;
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            var wb = mesh.WorldBounds;
            if (mesh.TriCount == 0 || !scene.IsMeshEnabled(m) || (excludedMeshes != null && excludedMeshes.Contains(m)) || (floorMeshes != null && floorMeshes.Contains(m)) || wb.Max.X < minX || wb.Min.X > maxX || wb.Max.Z < minZ || wb.Min.Z > maxZ || wb.Max.Y < bandMin || wb.Min.Y > bandMax)
            {
                continue;
            }
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                ref readonly var t = ref tris[i];
                var walkable = selected.Contains(i) || (t.NormalY >= minNormalY && s.FloorMatches(t));
                if (walkable)
                {
                    continue;
                }
                var b = t.Bounds;
                if (b.Max.Y < bandMin || b.Min.Y > bandMax || b.Max.X < minX || b.Min.X > maxX || b.Max.Z < minZ || b.Min.Z > maxZ)
                {
                    continue;
                }
                if (b.Max.Y - b.Min.Y < s.ObstacleMinHeight)
                {
                    continue; // decal, low step, the side face of a raised plate
                }
                if (s.ObstacleLocalHeight)
                {
                    if (!floorGrid.Overlaps(b, s.ObstacleHeightBelow, s.ObstacleHeightAbove, out var floorTop))
                    {
                        continue; // no selected floor at this height below/around the triangle
                    }
                    if (b.Max.Y <= floorTop + s.StepHeight)
                    {
                        continue; // does not rise above the step height over the floor next to it (kerb, plate edge)
                    }
                }
                ++obstacleTriangles;
                var pa = new Point64((long)Math.Round(t.A.X * scale), (long)Math.Round(t.A.Z * scale));
                var pb = new Point64((long)Math.Round(t.B.X * scale), (long)Math.Round(t.B.Z * scale));
                var pc = new Point64((long)Math.Round(t.C.X * scale), (long)Math.Round(t.C.Z * scale));
                // closed outline as an open path so zero-area (vertical) triangles still become strips
                (rimTriangles != null && rimTriangles.Contains(i) ? rimEdges : edges).Add([pa, pb, pc, pa]);
            }
        }
        if (obstacleTriangles > s.ObstacleMaxTriangles)
        {
            return [];
        }
        // joined ends: the outline is offset as a closed polyline with no end caps, so two wall triangles meeting at a corner do not chamfer it
        var strips = edges.Count > 0 ? Clipper.InflatePaths(edges, s.ObstacleInflate * scale, JoinType.Miter, EndType.Joined) : [];
        if (rimEdges.Count > 0)
        {
            var rimStrips = Clipper.InflatePaths(rimEdges, s.ObstacleInflate * scale, JoinType.Miter, EndType.Joined);
            strips.AddRange(rimBand != null && rimBand.Count > 0 ? Clipper.Difference(rimStrips, rimBand, FillRule.NonZero) : rimStrips);
        }
        return strips;
    }

    // box colliders standing on the selected floor (props without a mesh): footprint cut like a mesh obstacle. Seals and floor boxes are handled
    // elsewhere; ignoredBoxes are the ones the author switched off on the canvas
    public static Paths64 ObstacleBoxFootprints(ZoneCollisionScene scene, ReadOnlySpan<int> floorTriangles, ReadOnlySpan<int> floorBoxes, List<SealCandidate> seals, AutoMapSettings s, out int obstacleBoxes, IReadOnlySet<int>? ignoredBoxes = null)
    {
        obstacleBoxes = 0;
        if (floorTriangles.Length == 0)
        {
            return [];
        }
        var tris = scene.Triangles.Span;
        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;
        for (var i = 0; i < floorTriangles.Length; ++i)
        {
            var b = tris[floorTriangles[i]].Bounds;
            minX = MathF.Min(minX, b.Min.X);
            minZ = MathF.Min(minZ, b.Min.Z);
            maxX = MathF.Max(maxX, b.Max.X);
            maxZ = MathF.Max(maxZ, b.Max.Z);
        }
        HashSet<int> skip = [];
        for (var i = 0; i < floorBoxes.Length; ++i)
        {
            skip.Add(floorBoxes[i]);
        }
        for (var i = 0; i < seals.Count; ++i)
        {
            skip.Add(seals[i].BoxIndex);
        }
        var floorGrid = new FloorHeightGrid(tris, floorTriangles, minX, minZ, maxX, maxZ);
        var result = new Paths64();
        for (var b = 0; b < scene.Boxes.Count; ++b)
        {
            var box = scene.Boxes[b];
            if (skip.Contains(b) || (ignoredBoxes != null && ignoredBoxes.Contains(b)) || !scene.IsBoxEnabled(b) || !s.BoxIsObstacle(box))
            {
                continue;
            }
            var wb = box.WorldBounds;
            if (wb.Max.X < minX || wb.Min.X > maxX || wb.Max.Z < minZ || wb.Min.Z > maxZ)
            {
                continue;
            }
            if (!floorGrid.Overlaps(wb, s.ObstacleHeightBelow, s.ObstacleHeightAbove, out var floorTop) || wb.Max.Y <= floorTop + s.StepHeight)
            {
                continue; // no floor under it at this height, or it does not rise above the step height
            }
            var fp = BoxFootprintOps.BoxFootprint(box.Corners, s.ObstacleInflate);
            if (fp.Count >= 3)
            {
                result.Add(fp);
                ++obstacleBoxes;
            }
        }
        return result;
    }

    // 2-yalm XZ grid over the selected floor triangles, answering "is there floor at this height near this bounds"
    private sealed class FloorHeightGrid
    {
        private const float CellSize = 2f;
        private readonly Dictionary<long, List<int>> _cells = [];
        private readonly WorldTriangle[] _tris;
        private readonly float _minX;
        private readonly float _minZ;

        public FloorHeightGrid(ReadOnlySpan<WorldTriangle> tris, ReadOnlySpan<int> floorTriangles, float minX, float minZ, float maxX, float maxZ)
        {
            _tris = tris.ToArray();
            _minX = minX;
            _minZ = minZ;
            for (var i = 0; i < floorTriangles.Length; ++i)
            {
                var ti = floorTriangles[i];
                var b = tris[ti].Bounds;
                var cx0 = CellX(b.Min.X);
                var cx1 = CellX(b.Max.X);
                var cz0 = CellZ(b.Min.Z);
                var cz1 = CellZ(b.Max.Z);
                for (var cx = cx0; cx <= cx1; ++cx)
                {
                    for (var cz = cz0; cz <= cz1; ++cz)
                    {
                        var key = ((long)cx << 32) | (uint)cz;
                        if (!_cells.TryGetValue(key, out var list))
                        {
                            list = [];
                            _cells[key] = list;
                        }
                        list.Add(ti);
                    }
                }
            }
        }

        private int CellX(float x) => (int)MathF.Floor((x - _minX) / CellSize);
        private int CellZ(float z) => (int)MathF.Floor((z - _minZ) / CellSize);

        // true when a floor triangle overlaps the bounds in XZ and the obstacle intersects [floorMin - below, floorMax + above];
        // floorTop = highest such floor triangle's top
        public bool Overlaps(in Bounds3 b, float below, float above, out float floorTop)
        {
            floorTop = float.MinValue;
            var found = false;
            var cx0 = CellX(b.Min.X);
            var cx1 = CellX(b.Max.X);
            var cz0 = CellZ(b.Min.Z);
            var cz1 = CellZ(b.Max.Z);
            for (var cx = cx0; cx <= cx1; ++cx)
            {
                for (var cz = cz0; cz <= cz1; ++cz)
                {
                    if (!_cells.TryGetValue(((long)cx << 32) | (uint)cz, out var list))
                    {
                        continue;
                    }
                    for (var i = 0; i < list.Count; ++i)
                    {
                        var fb = _tris[list[i]].Bounds;
                        if (fb.Max.X < b.Min.X || fb.Min.X > b.Max.X || fb.Max.Z < b.Min.Z || fb.Min.Z > b.Max.Z)
                        {
                            continue;
                        }
                        if (b.Max.Y >= fb.Min.Y - below && b.Min.Y <= fb.Max.Y + above)
                        {
                            found = true;
                            floorTop = MathF.Max(floorTop, fb.Max.Y);
                        }
                    }
                }
            }
            return found;
        }
    }

    private const float RimWallTouchEps = 0.25f; // XZ gap allowed between a rim slope and the wall it reaches

    // XZ distance between two segments (endpoint-to-segment both ways, 0 when they cross)
    private static float SegmentDistXZ(in Vector3 a0, in Vector3 a1, in Vector3 b0, in Vector3 b1)
    {
        var d1 = (a1.X - a0.X) * (b0.Z - a0.Z) - (a1.Z - a0.Z) * (b0.X - a0.X);
        var d2 = (a1.X - a0.X) * (b1.Z - a0.Z) - (a1.Z - a0.Z) * (b1.X - a0.X);
        var d3 = (b1.X - b0.X) * (a0.Z - b0.Z) - (b1.Z - b0.Z) * (a0.X - b0.X);
        var d4 = (b1.X - b0.X) * (a1.Z - b0.Z) - (b1.Z - b0.Z) * (a1.X - b0.X);
        if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)))
        {
            return 0f;
        }
        return MathF.Min(MathF.Min(DistXZToSegment(a0, b0, b1), DistXZToSegment(a1, b0, b1)), MathF.Min(DistXZToSegment(b0, a0, a1), DistXZToSegment(b1, a0, a1)));
    }

    // 2-yalm XZ grid over a list of triangles for proximity queries
    private sealed class TriangleXZGrid
    {
        private const float CellSize = 2f;
        private readonly Dictionary<long, List<int>> _cells = [];
        private readonly float _minX;
        private readonly float _minZ;

        public TriangleXZGrid(ReadOnlySpan<WorldTriangle> tris, List<int> triangles, float minX, float minZ, float maxX, float maxZ)
        {
            _minX = minX;
            _minZ = minZ;
            for (var i = 0; i < triangles.Count; ++i)
            {
                var ti = triangles[i];
                var b = tris[ti].Bounds;
                var cx0 = CellX(MathF.Max(b.Min.X, minX));
                var cx1 = CellX(MathF.Min(b.Max.X, maxX));
                var cz0 = CellZ(MathF.Max(b.Min.Z, minZ));
                var cz1 = CellZ(MathF.Min(b.Max.Z, maxZ));
                for (var cx = cx0; cx <= cx1; ++cx)
                {
                    for (var cz = cz0; cz <= cz1; ++cz)
                    {
                        var key = ((long)cx << 32) | (uint)cz;
                        if (!_cells.TryGetValue(key, out var list))
                        {
                            list = [];
                            _cells[key] = list;
                        }
                        list.Add(ti);
                    }
                }
            }
        }

        private int CellX(float x) => (int)MathF.Floor((x - _minX) / CellSize);
        private int CellZ(float z) => (int)MathF.Floor((z - _minZ) / CellSize);

        // true when some grid triangle's edge comes within eps of one of t's edges in XZ and their Y ranges overlap within yEps
        public bool Touches(ReadOnlySpan<WorldTriangle> tris, in WorldTriangle t, float eps, float yEps)
        {
            var b = t.Bounds;
            var cx0 = CellX(b.Min.X - eps);
            var cx1 = CellX(b.Max.X + eps);
            var cz0 = CellZ(b.Min.Z - eps);
            var cz1 = CellZ(b.Max.Z + eps);
            var seen = new HashSet<int>();
            for (var cx = cx0; cx <= cx1; ++cx)
            {
                for (var cz = cz0; cz <= cz1; ++cz)
                {
                    if (!_cells.TryGetValue(((long)cx << 32) | (uint)cz, out var list))
                    {
                        continue;
                    }
                    for (var i = 0; i < list.Count; ++i)
                    {
                        var wi = list[i];
                        if (!seen.Add(wi))
                        {
                            continue;
                        }
                        ref readonly var w = ref tris[wi];
                        var wb = w.Bounds;
                        if (wb.Max.X < b.Min.X - eps || wb.Min.X > b.Max.X + eps || wb.Max.Z < b.Min.Z - eps || wb.Min.Z > b.Max.Z + eps || wb.Max.Y < b.Min.Y - yEps || wb.Min.Y > b.Max.Y + yEps)
                        {
                            continue;
                        }
                        if (SegmentDistXZ(t.A, t.B, w.A, w.B) <= eps || SegmentDistXZ(t.A, t.B, w.B, w.C) <= eps || SegmentDistXZ(t.A, t.B, w.C, w.A) <= eps
                            || SegmentDistXZ(t.B, t.C, w.A, w.B) <= eps || SegmentDistXZ(t.B, t.C, w.B, w.C) <= eps || SegmentDistXZ(t.B, t.C, w.C, w.A) <= eps
                            || SegmentDistXZ(t.C, t.A, w.A, w.B) <= eps || SegmentDistXZ(t.C, t.A, w.B, w.C) <= eps || SegmentDistXZ(t.C, t.A, w.C, w.A) <= eps)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }

    private const long RimCellOffset = 1L << 20;
    private const ulong RimCellMask = (1UL << 21) - 1;

    private static ulong RimKey(in Vector3 v, float eps)
        => (((ulong)((long)MathF.Floor(v.X / eps) + RimCellOffset) & RimCellMask) << 42) | (((ulong)((long)MathF.Floor(v.Y / eps) + RimCellOffset) & RimCellMask) << 21) | ((ulong)((long)MathF.Floor(v.Z / eps) + RimCellOffset) & RimCellMask);

    private static ulong EdgeKey(ulong a, ulong b) => a < b ? a * 1000003UL ^ b : b * 1000003UL ^ a;

    private static float DistXZToSegment(in Vector3 p, in Vector3 a, in Vector3 b)
    {
        var ax = a.X;
        var az = a.Z;
        var dx = b.X - ax;
        var dz = b.Z - az;
        var len2 = dx * dx + dz * dz;
        var t = len2 > 1e-9f ? Math.Clamp(((p.X - ax) * dx + (p.Z - az) * dz) / len2, 0f, 1f) : 0f;
        var qx = ax + t * dx - p.X;
        var qz = az + t * dz - p.Z;
        return MathF.Sqrt(qx * qx + qz * qz);
    }

    // rim extension: steep-but-not-wall triangles adjoining the selection boundary, clipped to a band RimExtension wide around the boundary edges
    private readonly record struct WallSegment(Vector3 A, Vector3 B, int Triangle, float TopY);

    // boundary edges of the selection within WallSnap of a wall foot are extended to that wall: the sliver between the edge and the foot line
    // (a steep lip, a hole in the mesh) is unioned in, and the matched foot segments are returned so the final vertices can be snapped onto
    // them exactly. Nothing is added beyond the wall line or sideways past the edge, so the extension cannot leak
    public static Paths64 WallSnapPaths(ZoneCollisionScene scene, ReadOnlySpan<int> triangles, AutoMapSettings s, IReadOnlySet<int>? excludedMeshes, List<(Vector3 a, Vector3 b)> snapSegments, out int snappedEdges)
    {
        snappedEdges = 0;
        var r = s.WallSnap;
        if (r <= 0f || triangles.Length == 0)
        {
            return [];
        }
        var tris = scene.Triangles.Span;
        var eps = MathF.Max(s.WeldEps, 1e-4f);
        HashSet<int> selected = new(triangles.Length);
        Dictionary<ulong, int> edgeCount = new(triangles.Length * 3);
        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        for (var i = 0; i < triangles.Length; ++i)
        {
            selected.Add(triangles[i]);
            ref readonly var t = ref tris[triangles[i]];
            var ka = RimKey(t.A, eps);
            var kb = RimKey(t.B, eps);
            var kc = RimKey(t.C, eps);
            edgeCount[EdgeKey(ka, kb)] = edgeCount.GetValueOrDefault(EdgeKey(ka, kb)) + 1;
            edgeCount[EdgeKey(kb, kc)] = edgeCount.GetValueOrDefault(EdgeKey(kb, kc)) + 1;
            edgeCount[EdgeKey(kc, ka)] = edgeCount.GetValueOrDefault(EdgeKey(kc, ka)) + 1;
            var b = t.Bounds;
            minX = MathF.Min(minX, b.Min.X);
            minZ = MathF.Min(minZ, b.Min.Z);
            maxX = MathF.Max(maxX, b.Max.X);
            maxZ = MathF.Max(maxZ, b.Max.Z);
            minY = MathF.Min(minY, b.Min.Y);
            maxY = MathF.Max(maxY, b.Max.Y);
        }
        List<(Vector3 a, Vector3 b)> boundary = [];
        for (var i = 0; i < triangles.Length; ++i)
        {
            ref readonly var t = ref tris[triangles[i]];
            var ka = RimKey(t.A, eps);
            var kb = RimKey(t.B, eps);
            var kc = RimKey(t.C, eps);
            if (edgeCount[EdgeKey(ka, kb)] == 1) { boundary.Add((t.A, t.B)); }
            if (edgeCount[EdgeKey(kb, kc)] == 1) { boundary.Add((t.B, t.C)); }
            if (edgeCount[EdgeKey(kc, ka)] == 1) { boundary.Add((t.C, t.A)); }
        }
        if (boundary.Count == 0)
        {
            return [];
        }

        // wall foot segments: the lowest edge of steep unselected triangles near the selection (the top edge of a leaning wall would project inside the room)
        var wallNormalY = MathF.Cos(s.RimMaxSlopeDeg * MathF.PI / 180f);
        List<WallSegment> walls = [];
        var meshes = scene.Meshes;
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            var wb = mesh.WorldBounds;
            if (mesh.TriCount == 0 || !scene.IsMeshEnabled(m) || (excludedMeshes != null && excludedMeshes.Contains(m)) || wb.Max.X < minX - r || wb.Min.X > maxX + r || wb.Max.Z < minZ - r || wb.Min.Z > maxZ + r || wb.Max.Y < minY - 1f || wb.Min.Y > maxY + 3f)
            {
                continue;
            }
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                ref readonly var t = ref tris[i];
                if (selected.Contains(i) || t.NormalY >= wallNormalY)
                {
                    continue;
                }
                var b = t.Bounds;
                if (b.Max.X < minX - r || b.Min.X > maxX + r || b.Max.Z < minZ - r || b.Min.Z > maxZ + r || b.Max.Y < minY - 1f || b.Min.Y > maxY + 3f)
                {
                    continue;
                }
                var footY = b.Min.Y + 0.5f;
                AddWallSegment(walls, t.A, t.B, i, b.Max.Y, footY);
                AddWallSegment(walls, t.B, t.C, i, b.Max.Y, footY);
                AddWallSegment(walls, t.C, t.A, i, b.Max.Y, footY);
            }
        }
        if (walls.Count == 0)
        {
            return [];
        }
        var wallGrid = new SegmentGrid(walls, minX - r, minZ - r);

        // for every (edge, wall foot) pair within reach: the trapezoid between the edge and its projection on the foot line, clipped to the
        // capsule around the segment so nothing runs along the line past the wall's end; whatever lands beyond a wall line is cut off again
        // by that wall's obstacle strip and dropped as a disconnected piece
        var scale = BoxFootprintOps.DefaultScale;
        var fill = new Paths64();
        var capsules = new Paths64();
        HashSet<int> usedWalls = [];
        foreach (var (a, b) in boundary)
        {
            var edgeMinY = MathF.Min(a.Y, b.Y);
            var edgeMaxY = MathF.Max(a.Y, b.Y);
            var hit = false;
            foreach (var w in wallGrid.Near(a, b, r))
            {
                var seg = walls[w];
                var segMinY = MathF.Min(seg.A.Y, seg.B.Y);
                var segMaxY = MathF.Max(seg.A.Y, seg.B.Y);
                if (segMaxY < edgeMinY - 0.5f || segMinY > edgeMaxY + 1f || seg.TopY < edgeMaxY + s.StepHeight || SegmentDistXZ(a, b, seg.A, seg.B) > r)
                {
                    continue;
                }
                var pa = ProjectOnSegment(a, seg.A, seg.B, float.MaxValue);
                var pb = ProjectOnSegment(b, seg.A, seg.B, float.MaxValue);
                hit = true;
                if (usedWalls.Add(w))
                {
                    snapSegments.Add((seg.A, seg.B));
                    capsules.Add([new Point64((long)Math.Round(seg.A.X * scale), (long)Math.Round(seg.A.Z * scale)), new Point64((long)Math.Round(seg.B.X * scale), (long)Math.Round(seg.B.Z * scale))]);
                }
                var quad = new Path64
                {
                    new Point64((long)Math.Round(a.X * scale), (long)Math.Round(a.Z * scale)),
                    new Point64((long)Math.Round(b.X * scale), (long)Math.Round(b.Z * scale)),
                    new Point64((long)Math.Round(pb.X * scale), (long)Math.Round(pb.Y * scale)),
                    new Point64((long)Math.Round(pa.X * scale), (long)Math.Round(pa.Y * scale)),
                };
                if (Math.Abs(Clipper.Area(quad)) < 1.0)
                {
                    continue;
                }
                if (!Clipper.IsPositive(quad))
                {
                    quad.Reverse();
                }
                fill.Add(quad);
            }
            if (hit)
            {
                ++snappedEdges;
            }
        }
        if (fill.Count == 0)
        {
            return fill;
        }
        var reach = Clipper.InflatePaths(capsules, r * scale, JoinType.Round, EndType.Round);
        return Clipper.Intersect(Clipper.Union(fill, FillRule.NonZero), reach, FillRule.NonZero);
    }

    private static void AddWallSegment(List<WallSegment> walls, in Vector3 a, in Vector3 b, int tri, float topY, float footY)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        if (dx * dx + dz * dz >= 0.05f * 0.05f && a.Y <= footY && b.Y <= footY)
        {
            walls.Add(new(a, b, tri, topY));
        }
    }

    // XZ projection of p onto the segment a-b, clamped to the segment extended by 'ext' at both ends
    private static Vector2 ProjectOnSegment(in Vector3 p, in Vector3 a, in Vector3 b, float ext)
    {
        var ax = a.X;
        var az = a.Z;
        var dx = b.X - ax;
        var dz = b.Z - az;
        var len2 = dx * dx + dz * dz;
        if (len2 < 1e-9f)
        {
            return new(ax, az);
        }
        var t = ((p.X - ax) * dx + (p.Z - az) * dz) / len2;
        if (ext != float.MaxValue)
        {
            var len = MathF.Sqrt(len2);
            t = Math.Clamp(t, -ext / len, 1f + ext / len);
        }
        return new(ax + t * dx, az + t * dz);
    }

    // final vertices within tolerance of a matched wall foot are moved onto the foot line (its corner when near an endpoint), so the arena
    // edge carries the wall's own vertices instead of the 'inflate' inset of the obstacle strip
    public static void SnapVerticesToWalls(List<CollisionOutlinesExtractor.PolygonWithHoles> polys, List<(Vector3 a, Vector3 b)> segments, float tol)
    {
        if (segments.Count == 0 || tol <= 0f)
        {
            return;
        }
        foreach (var poly in polys)
        {
            SnapContour(poly.Outer, segments, tol);
            foreach (var hole in poly.Holes)
            {
                SnapContour(hole, segments, tol);
            }
        }
    }

    private static void SnapContour(List<Vector3> pts, List<(Vector3 a, Vector3 b)> segments, float tol)
    {
        var cornerTol = tol * 2f;
        for (var i = 0; i < pts.Count; ++i)
        {
            var p = pts[i];
            var best = tol;
            var target = p;
            foreach (var (a, b) in segments)
            {
                if (MathF.Min(a.X, b.X) - cornerTol > p.X || MathF.Max(a.X, b.X) + cornerTol < p.X || MathF.Min(a.Z, b.Z) - cornerTol > p.Z || MathF.Max(a.Z, b.Z) + cornerTol < p.Z || MathF.Min(a.Y, b.Y) > p.Y + 1f || MathF.Max(a.Y, b.Y) < p.Y - 1f)
                {
                    continue;
                }
                var da = MathF.Sqrt((a.X - p.X) * (a.X - p.X) + (a.Z - p.Z) * (a.Z - p.Z));
                var db = MathF.Sqrt((b.X - p.X) * (b.X - p.X) + (b.Z - p.Z) * (b.Z - p.Z));
                if (da <= cornerTol && da < best + tol)
                {
                    best = MathF.Min(best, da);
                    target = new(a.X, p.Y, a.Z);
                    continue;
                }
                if (db <= cornerTol && db < best + tol)
                {
                    best = MathF.Min(best, db);
                    target = new(b.X, p.Y, b.Z);
                    continue;
                }
                var d = DistXZToSegment(p, a, b);
                if (d < best)
                {
                    best = d;
                    var q = ProjectOnSegment(p, a, b, 0f);
                    target = new(q.X, p.Y, q.Y);
                }
            }
            pts[i] = target;
        }
    }

    // 2-yalm XZ grid over wall segments for the boundary-edge matching
    private sealed class SegmentGrid
    {
        private const float CellSize = 2f;
        private readonly Dictionary<long, List<int>> _cells = [];
        private readonly float _minX;
        private readonly float _minZ;
        private readonly HashSet<int> _seen = [];

        public SegmentGrid(List<WallSegment> walls, float minX, float minZ)
        {
            _minX = minX;
            _minZ = minZ;
            for (var i = 0; i < walls.Count; ++i)
            {
                var w = walls[i];
                var cx0 = CellX(MathF.Min(w.A.X, w.B.X));
                var cx1 = CellX(MathF.Max(w.A.X, w.B.X));
                var cz0 = CellZ(MathF.Min(w.A.Z, w.B.Z));
                var cz1 = CellZ(MathF.Max(w.A.Z, w.B.Z));
                for (var cx = cx0; cx <= cx1; ++cx)
                {
                    for (var cz = cz0; cz <= cz1; ++cz)
                    {
                        var key = ((long)cx << 32) | (uint)cz;
                        if (!_cells.TryGetValue(key, out var list))
                        {
                            _cells[key] = list = [];
                        }
                        list.Add(i);
                    }
                }
            }
        }

        private int CellX(float x) => (int)MathF.Floor((x - _minX) / CellSize);
        private int CellZ(float z) => (int)MathF.Floor((z - _minZ) / CellSize);

        public IEnumerable<int> Near(Vector3 a, Vector3 b, float r)
        {
            _seen.Clear();
            var cx0 = CellX(MathF.Min(a.X, b.X) - r);
            var cx1 = CellX(MathF.Max(a.X, b.X) + r);
            var cz0 = CellZ(MathF.Min(a.Z, b.Z) - r);
            var cz1 = CellZ(MathF.Max(a.Z, b.Z) + r);
            for (var cx = cx0; cx <= cx1; ++cx)
            {
                for (var cz = cz0; cz <= cz1; ++cz)
                {
                    if (_cells.TryGetValue(((long)cx << 32) | (uint)cz, out var list))
                    {
                        foreach (var i in list)
                        {
                            if (_seen.Add(i))
                            {
                                yield return i;
                            }
                        }
                    }
                }
            }
        }
    }

    public static Paths64 RimExtensionPaths(ZoneCollisionScene scene, ReadOnlySpan<int> triangles, AutoMapSettings s, IReadOnlySet<int>? excludedMeshes, HashSet<int> rimTriangles, out Paths64 band)
    {
        band = [];
        rimTriangles.Clear();
        if (s.RimExtension <= 0f || triangles.Length == 0)
        {
            return [];
        }
        var tris = scene.Triangles.Span;
        var eps = MathF.Max(s.WeldEps, 1e-4f);
        HashSet<int> selected = new(triangles.Length);
        Dictionary<ulong, int> edgeCount = new(triangles.Length * 3);
        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        for (var i = 0; i < triangles.Length; ++i)
        {
            selected.Add(triangles[i]);
            ref readonly var t = ref tris[triangles[i]];
            var ka = RimKey(t.A, eps);
            var kb = RimKey(t.B, eps);
            var kc = RimKey(t.C, eps);
            edgeCount[EdgeKey(ka, kb)] = edgeCount.GetValueOrDefault(EdgeKey(ka, kb)) + 1;
            edgeCount[EdgeKey(kb, kc)] = edgeCount.GetValueOrDefault(EdgeKey(kb, kc)) + 1;
            edgeCount[EdgeKey(kc, ka)] = edgeCount.GetValueOrDefault(EdgeKey(kc, ka)) + 1;
            var b = t.Bounds;
            minX = MathF.Min(minX, b.Min.X);
            minZ = MathF.Min(minZ, b.Min.Z);
            maxX = MathF.Max(maxX, b.Max.X);
            maxZ = MathF.Max(maxZ, b.Max.Z);
            minY = MathF.Min(minY, b.Min.Y);
            maxY = MathF.Max(maxY, b.Max.Y);
        }
        // boundary edges: shared by exactly one selected triangle
        List<(Vector3 a, Vector3 b)> boundary = [];
        HashSet<ulong> boundaryKeys = [];
        for (var i = 0; i < triangles.Length; ++i)
        {
            ref readonly var t = ref tris[triangles[i]];
            var ka = RimKey(t.A, eps);
            var kb = RimKey(t.B, eps);
            var kc = RimKey(t.C, eps);
            if (edgeCount[EdgeKey(ka, kb)] == 1) { boundary.Add((t.A, t.B)); boundaryKeys.Add(EdgeKey(ka, kb)); }
            if (edgeCount[EdgeKey(kb, kc)] == 1) { boundary.Add((t.B, t.C)); boundaryKeys.Add(EdgeKey(kb, kc)); }
            if (edgeCount[EdgeKey(kc, ka)] == 1) { boundary.Add((t.C, t.A)); boundaryKeys.Add(EdgeKey(kc, ka)); }
        }
        if (boundary.Count == 0)
        {
            return [];
        }

        // candidate rim triangles: unselected, not walls, near the selection's bounds in XZ and Y
        var r = s.RimExtension;
        var minNormalY = MathF.Cos(s.RimMaxSlopeDeg * MathF.PI / 180f);
        List<int> pool = [];
        List<int> walls = []; // unselected triangles steeper than the rim slope limit (the walls a rim chain must reach)
        var meshes = scene.Meshes;
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            var wb = mesh.WorldBounds;
            if (mesh.TriCount == 0 || !scene.IsMeshEnabled(m) || (excludedMeshes != null && excludedMeshes.Contains(m)) || wb.Max.X < minX - r || wb.Min.X > maxX + r || wb.Max.Z < minZ - r || wb.Min.Z > maxZ + r || wb.Max.Y < minY - 2f || wb.Min.Y > maxY + 4f)
            {
                continue;
            }
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                ref readonly var t = ref tris[i];
                if (selected.Contains(i))
                {
                    continue;
                }
                var b = t.Bounds;
                if (b.Max.X < minX - r || b.Min.X > maxX + r || b.Max.Z < minZ - r || b.Min.Z > maxZ + r)
                {
                    continue;
                }
                if (t.NormalY < minNormalY)
                {
                    if (s.RimRequireWall && b.Max.Y >= minY - 2f && b.Min.Y <= maxY + 4f)
                    {
                        walls.Add(i);
                    }
                    continue;
                }
                pool.Add(i);
            }
        }
        if (pool.Count == 0)
        {
            return [];
        }

        // hop outward from the boundary edges through edge-connected pool triangles, keeping those that reach into the band
        Dictionary<ulong, List<int>> poolEdges = new(pool.Count * 3);
        for (var p = 0; p < pool.Count; ++p)
        {
            ref readonly var t = ref tris[pool[p]];
            var ka = RimKey(t.A, eps);
            var kb = RimKey(t.B, eps);
            var kc = RimKey(t.C, eps);
            foreach (var k in (ReadOnlySpan<ulong>)[EdgeKey(ka, kb), EdgeKey(kb, kc), EdgeKey(kc, ka)])
            {
                if (!poolEdges.TryGetValue(k, out var list))
                {
                    poolEdges[k] = list = [];
                }
                list.Add(pool[p]);
            }
        }
        bool InBand(in WorldTriangle t)
        {
            var best = float.MaxValue;
            var c = t.Centroid;
            for (var e = 0; e < boundary.Count && best > r; ++e)
            {
                var (a, b) = boundary[e];
                best = MathF.Min(best, MathF.Min(DistXZToSegment(t.A, a, b), MathF.Min(DistXZToSegment(t.B, a, b), MathF.Min(DistXZToSegment(t.C, a, b), DistXZToSegment(c, a, b)))));
            }
            return best <= r;
        }
        Queue<(int tri, int hop)> queue = new();
        foreach (var k in boundaryKeys)
        {
            if (poolEdges.TryGetValue(k, out var list))
            {
                foreach (var tri in list)
                {
                    if (rimTriangles.Add(tri))
                    {
                        queue.Enqueue((tri, 1));
                    }
                }
            }
        }
        while (queue.Count > 0)
        {
            var (tri, hop) = queue.Dequeue();
            ref readonly var t = ref tris[tri];
            if (!InBand(t))
            {
                rimTriangles.Remove(tri);
                continue;
            }
            if (hop >= s.RimHops)
            {
                continue;
            }
            var ka = RimKey(t.A, eps);
            var kb = RimKey(t.B, eps);
            var kc = RimKey(t.C, eps);
            foreach (var k in (ReadOnlySpan<ulong>)[EdgeKey(ka, kb), EdgeKey(kb, kc), EdgeKey(kc, ka)])
            {
                if (poolEdges.TryGetValue(k, out var list))
                {
                    foreach (var next in list)
                    {
                        if (rimTriangles.Add(next))
                        {
                            queue.Enqueue((next, hop + 1));
                        }
                    }
                }
            }
        }
        if (s.RimRequireWall && rimTriangles.Count > 0)
        {
            // keep only the rim triangles whose chain (through other rim triangles) touches a wall; walls and slopes are
            // usually separate meshes that are not vertex-welded to each other, so touching is geometric (XZ distance + Y overlap)
            var wallGrid = new TriangleXZGrid(tris, walls, minX - r, minZ - r, maxX + r, maxZ + r);
            HashSet<int> reached = [];
            Queue<int> back = new();
            foreach (var tri in rimTriangles)
            {
                if (wallGrid.Touches(tris, tris[tri], RimWallTouchEps, 0.5f))
                {
                    reached.Add(tri);
                    back.Enqueue(tri);
                }
            }
            while (back.Count > 0)
            {
                var tri = back.Dequeue();
                ref readonly var t = ref tris[tri];
                var ka = RimKey(t.A, eps);
                var kb = RimKey(t.B, eps);
                var kc = RimKey(t.C, eps);
                foreach (var k in (ReadOnlySpan<ulong>)[EdgeKey(ka, kb), EdgeKey(kb, kc), EdgeKey(kc, ka)])
                {
                    if (poolEdges.TryGetValue(k, out var list))
                    {
                        foreach (var next in list)
                        {
                            if (rimTriangles.Contains(next) && reached.Add(next))
                            {
                                back.Enqueue(next);
                            }
                        }
                    }
                }
            }
            rimTriangles.IntersectWith(reached);
        }
        if (rimTriangles.Count == 0)
        {
            return [];
        }

        // band = boundary edges buffered by r; extension = rim triangle footprints clipped to the band
        var scale = BoxFootprintOps.DefaultScale;
        var edgePaths = new Paths64(boundary.Count);
        foreach (var (a, b) in boundary)
        {
            edgePaths.Add([new Point64((long)Math.Round(a.X * scale), (long)Math.Round(a.Z * scale)), new Point64((long)Math.Round(b.X * scale), (long)Math.Round(b.Z * scale))]);
        }
        band = Clipper.InflatePaths(edgePaths, r * scale, JoinType.Round, EndType.Round);
        var rimPaths = new Paths64(rimTriangles.Count);
        foreach (var tri in rimTriangles)
        {
            ref readonly var t = ref tris[tri];
            var pa = new Point64((long)Math.Round(t.A.X * scale), (long)Math.Round(t.A.Z * scale));
            var pb = new Point64((long)Math.Round(t.B.X * scale), (long)Math.Round(t.B.Z * scale));
            var pc = new Point64((long)Math.Round(t.C.X * scale), (long)Math.Round(t.C.Z * scale));
            Path64 path = [pa, pb, pc];
            if (!Clipper.IsPositive(path))
            {
                path.Reverse();
            }
            rimPaths.Add(path);
        }
        return Clipper.Intersect(rimPaths, band, FillRule.NonZero);
    }

    // union of the given triangles and floor boxes with seal boxes and obstacle footprints cut out, keeping the polygon containing the anchor when configured
    public static List<CollisionOutlinesExtractor.PolygonWithHoles> BuildPolygons(ZoneCollisionScene scene, ReadOnlySpan<int> triangles, ReadOnlySpan<int> floorBoxes, List<SealCandidate> seals, IReadOnlySet<int> activeSeals,
        List<Path64> extraUnion, List<Path64> extraCut, List<Vector3> extraYSource, Vector2? keepAnchor, AutoMapSettings s, out string keepStatus, out long unionMs, out int obstacleTriangles, out int obstacleBoxes, out int wallSnapEdges, HashSet<int> rim, IReadOnlySet<int>? excludedMeshes = null, IReadOnlySet<int>? floorMeshes = null, IReadOnlySet<int>? ignoredBoxes = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var builder = new TrianglePolygonBuilder(s.SnapEpsXZ, BoxFootprintOps.DefaultScale);
        builder.AddTriangles(scene.Triangles.Span, triangles);
        var polys = builder.Build(s.MinArea);
        if (s.SeamClose > 0f)
        {
            polys = BoxFootprintOps.Close(polys, s.SeamClose, s.MinArea);
        }
        List<Path64> cut = [.. extraCut];
        List<Path64> union = [.. extraUnion];
        List<Vector3> ySource = [.. extraYSource];
        var rimPaths = RimExtensionPaths(scene, triangles, s, excludedMeshes, rim, out var rimBand);
        if (rimPaths.Count > 0)
        {
            union.AddRange(rimPaths);
            var tris = scene.Triangles.Span;
            foreach (var tri in rim)
            {
                ref readonly var t = ref tris[tri];
                ySource.Add(t.A);
                ySource.Add(t.B);
                ySource.Add(t.C);
            }
        }
        List<(Vector3 a, Vector3 b)> snapSegments = [];
        var snapFill = WallSnapPaths(scene, triangles, s, excludedMeshes, snapSegments, out wallSnapEdges);
        if (snapFill.Count > 0)
        {
            union.AddRange(snapFill);
            foreach (var (a, b) in snapSegments)
            {
                ySource.Add(a);
                ySource.Add(b);
            }
        }
        for (var i = 0; i < floorBoxes.Length; ++i)
        {
            var box = scene.Boxes[floorBoxes[i]];
            var fp = BoxFootprintOps.BoxFootprint(box.Corners, 0f);
            if (fp.Count >= 3)
            {
                union.Add(fp);
                // the walkable surface of a floor box is its top face
                var top = box.WorldBounds.Max.Y;
                for (var k = 0; k < 8; ++k)
                {
                    ySource.Add(new(box.Corners[k].X, top, box.Corners[k].Z));
                }
            }
        }
        obstacleTriangles = 0;
        obstacleBoxes = 0;
        if (s.CutObstacles)
        {
            var footprints = ObstacleFootprints(scene, triangles, s, out obstacleTriangles, excludedMeshes, floorMeshes, rim, rimBand);
            if (obstacleTriangles <= s.ObstacleMaxTriangles)
            {
                cut.AddRange(footprints);
            }
        }
        if (s.CutBoxes)
        {
            cut.AddRange(ObstacleBoxFootprints(scene, triangles, floorBoxes, seals, s, out obstacleBoxes, ignoredBoxes));
        }
        for (var i = 0; i < seals.Count; ++i)
        {
            if (activeSeals.Contains(seals[i].BoxIndex) && seals[i].FootprintXZ.Count >= 3)
            {
                cut.Add(seals[i].FootprintXZ);
                ySource.AddRange(scene.Boxes[seals[i].BoxIndex].Corners);
            }
        }
        keepStatus = "";
        if (union.Count > 0)
        {
            // the floor union snaps vertices to the SnapEpsXZ grid while the extension paths carry raw vertices: grow them by the grid step so
            // they overlap the floor edge instead of leaving a hairline gap that would drop them as disconnected slivers
            var grown = new Paths64(union.Count);
            grown.AddRange(union);
            union = [.. Clipper.InflatePaths(grown, MathF.Max(s.SnapEpsXZ, 0.01f) * BoxFootprintOps.DefaultScale, JoinType.Miter, EndType.Polygon)];
        }
        if (cut.Count > 0 || union.Count > 0)
        {
            polys = BoxFootprintOps.Apply(polys, cut, union, ySource, keepAnchor, s.MinArea, out keepStatus);
        }
        if (snapSegments.Count > 0 && s.WallSnapVertices)
        {
            SnapVerticesToWalls(polys, snapSegments, s.ObstacleInflate + 0.03f);
        }
        if (s.CutObstacles && obstacleTriangles > s.ObstacleMaxTriangles)
        {
            keepStatus = $"obstacle cut skipped: {obstacleTriangles} obstacle triangles exceed the limit of {s.ObstacleMaxTriangles} (the fill leaked far - shrink the radius or block the leak). {keepStatus}";
        }
        unionMs = sw.ElapsedMilliseconds;
        return polys;
    }
}

// the mutable editing session: settings, centre, seals/pairs, adjacency cache, selected triangles/boxes and the resulting polygons
public sealed class ArenaMapSession(ZoneCollisionScene scene)
{
    public readonly ZoneCollisionScene Scene = scene;
    public readonly AutoMapSettings Settings = new();
    public Vector3 Centre;
    public bool CentreValid;
    public CentreEstimate LastEstimate;
    public List<SealCandidate> Seals = [];
    public readonly HashSet<int> ActiveSeals = [];
    public List<SealPair> Pairs = [];
    public int PairIndex = -1;
    public TriangleAdjacency? Adjacency;
    public readonly HashSet<int> Selected = [];
    public readonly HashSet<int> SelectedFloorBoxes = [];
    public readonly HashSet<int> FloorMeshes = []; // meshes forced to count as floor regardless of material
    public readonly HashSet<int> IgnoredBoxes = []; // obstacle boxes the author switched off (not cut)
    public AutoMapResult Last = new();
    public readonly HashSet<int> LastAutoResult = [];
    public List<CollisionOutlinesExtractor.PolygonWithHoles> Polygons = [];
    public string KeepStatus = "";
    public long UnionMs;
    public int ObstacleTriangles;
    public int ObstacleBoxes;
    public int WallSnapEdges;
    public readonly HashSet<int> RimTriangles = []; // triangles the rim extension used in the last recompute
    private (Vector3 centre, string floors, int mode, float slope, float radius, float weld, float step, int adjacency, float touch, int layerFingerprint) _adjacencyKey;

    public void DetectSeals()
    {
        Seals = ArenaAutoMapper.FindSeals(Scene, Settings);
        ActiveSeals.Clear();
        for (var i = 0; i < Seals.Count; ++i)
        {
            ActiveSeals.Add(Seals[i].BoxIndex);
        }
        Pairs = ArenaAutoMapper.ComputePairs(Seals, Scene.Markers, Settings);
        PairIndex = -1;
    }

    // default pair: nearest to the preferred point when given, otherwise the seal pair with the lowest indices (first boss room in layout order)
    public void ChooseDefaultPair(Vector3? preferNear)
    {
        PairIndex = -1;
        var best = float.MaxValue;
        for (var i = 0; i < Pairs.Count; ++i)
        {
            var p = Pairs[i];
            float score;
            if (preferNear is { } pn)
            {
                // nearest estimate wins; a lone seal only when nothing paired is close (its estimate sits MaxRadius/2 inside the door)
                var est = ArenaAutoMapper.EstimateCentre(p, Seals, Scene.Markers, Settings, pn);
                var d = (new Vector2(est.Centre.X, est.Centre.Z) - new Vector2(pn.X, pn.Z)).Length();
                if (p.IsSingle)
                {
                    var seal = Seals[p.SealA];
                    d = MathF.Min(d, (new Vector2(seal.Center.X, seal.Center.Z) - new Vector2(pn.X, pn.Z)).Length()) + 15f;
                }
                score = d + (est.UsedFallback && !p.IsSingle ? 5f : 0f);
            }
            else
            {
                if (p.IsSingle)
                {
                    continue;
                }
                score = p.SealA * 1000f + (p.SealB >= 0 ? p.SealB : 500 + p.MarkerIndex) + (p.SealB >= 0 ? 0f : 1e5f);
            }
            if (score < best)
            {
                best = score;
                PairIndex = i;
            }
        }
        if (PairIndex < 0 && Pairs.Count > 0)
        {
            PairIndex = 0;
        }
    }

    public void EstimateCentre(Vector3? preferNear)
    {
        if (PairIndex < 0 || PairIndex >= Pairs.Count)
        {
            ChooseDefaultPair(preferNear);
        }
        if (PairIndex < 0)
        {
            LastEstimate = new(preferNear ?? Vector3.Zero, -1, -1, -1, 0f, true, "no seals");
            Centre = LastEstimate.Centre;
            CentreValid = false;
            return;
        }
        LastEstimate = ArenaAutoMapper.EstimateCentre(Pairs[PairIndex], Seals, Scene.Markers, Settings, preferNear);
        Centre = LastEstimate.Centre;
        CentreValid = LastEstimate.Reason != "no seals";
    }

    public void SetCentre(Vector3 c)
    {
        Centre = c;
        CentreValid = true;
    }

    private int LayerFingerprint()
    {
        var h = 17;
        var layers = Scene.Layers;
        for (var i = 0; i < layers.Count; ++i)
        {
            h = h * 31 + (layers[i].Enabled ? 1 : 0);
        }
        return h * 31 + Scene.Meshes.Count;
    }

    private string FloorKey()
    {
        var sb = new StringBuilder();
        foreach (var f in Settings.FloorMaterials)
        {
            sb.Append(f.ToString()).Append(';');
        }
        return sb.ToString();
    }

    public TriangleAdjacency EnsureAdjacency()
    {
        var floorKey = FloorKey();
        foreach (var m in FloorMeshes)
        {
            floorKey += $"m{m};";
        }
        var key = (Centre, floorKey, (int)Settings.FloorMatchMode, Settings.MaxSlopeDeg, Settings.MaxRadius, Settings.WeldEps, Settings.StepHeight, (int)Settings.Adjacency, Settings.BoxFloorTouchEps, LayerFingerprint());
        if (Adjacency == null || key != _adjacencyKey)
        {
            Adjacency = TriangleAdjacency.Build(Scene, Centre, Settings, FloorMeshes);
            _adjacencyKey = key;
        }
        return Adjacency;
    }

    public void AutoMap()
    {
        var adj = EnsureAdjacency();
        var seed = ArenaAutoMapper.FindSeed(Scene, adj, Centre, Settings);
        Last = ArenaAutoMapper.FloodFill(Scene, adj, seed, Seals, ActiveSeals, Centre, Settings);
        Selected.Clear();
        LastAutoResult.Clear();
        SelectedFloorBoxes.Clear();
        foreach (var t in Last.Selected)
        {
            Selected.Add(t);
            LastAutoResult.Add(t);
        }
        foreach (var b in Last.SelectedBoxes)
        {
            SelectedFloorBoxes.Add(b);
        }
    }

    public void GrowFrom(int tri, int depth, HashSet<int> dst)
    {
        var adj = EnsureAdjacency();
        if (!adj.GlobalToLocal.TryGetValue(tri, out var start))
        {
            dst.Add(tri);
            return;
        }
        Dictionary<int, int> dist = new() { [start] = 0 };
        Queue<int> queue = new();
        queue.Enqueue(start);
        dst.Add(tri);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            var d = dist[cur];
            if (d >= depth)
            {
                continue;
            }
            foreach (var nb in adj.NeighboursOf(cur))
            {
                if (dist.ContainsKey(nb))
                {
                    continue;
                }
                dist[nb] = d + 1;
                if (adj.IsBoxNode(nb))
                {
                    SelectedFloorBoxes.Add(adj.FloorBoxes[nb - adj.Candidates.Length]);
                }
                else
                {
                    dst.Add(adj.Candidates[nb]);
                }
                queue.Enqueue(nb);
            }
        }
    }

    // add = select the mesh's walkable-slope triangles (all of them when floorOnly is false or the mesh is a forced floor mesh); !add = deselect all
    public void SelectMesh(int meshIndex, bool add, bool floorOnly)
    {
        var mesh = Scene.Meshes[meshIndex];
        var tris = Scene.Triangles.Span;
        var end = mesh.TriStart + mesh.TriCount;
        var forced = FloorMeshes.Contains(meshIndex);
        var minNormalY = Settings.MinNormalY;
        for (var i = mesh.TriStart; i < end; ++i)
        {
            if (add)
            {
                if (tris[i].NormalY >= minNormalY && (!floorOnly || forced || Settings.FloorMatches(tris[i])))
                {
                    Selected.Add(i);
                }
            }
            else
            {
                Selected.Remove(i);
            }
        }
    }

    public int PickTriangle(Vector2 xz, float? preferY)
    {
        var tris = Scene.Triangles.Span;
        var best = -1;
        var bestScore = float.MaxValue;
        var meshes = Scene.Meshes;
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (mesh.TriCount == 0 || !Scene.IsMeshEnabled(m) || !mesh.WorldBounds.ContainsXZ(xz.X, xz.Y))
            {
                continue;
            }
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                ref readonly var t = ref tris[i];
                if (!t.ContainsXZ(xz.X, xz.Y))
                {
                    continue;
                }
                var y = t.YAt(xz.X, xz.Y);
                var score = preferY is { } py ? MathF.Abs(y - py) : -y;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
        }
        return best;
    }

    public List<CollisionOutlinesExtractor.PolygonWithHoles> Recompute(List<Path64> extraUnion, List<Path64> extraCut, List<Vector3> extraYSource)
    {
        var snapshot = new int[Selected.Count];
        Selected.CopyTo(snapshot);
        var boxes = new int[SelectedFloorBoxes.Count];
        SelectedFloorBoxes.CopyTo(boxes);
        Polygons = ArenaAutoMapper.BuildPolygons(Scene, snapshot, boxes, Seals, ActiveSeals, extraUnion, extraCut, extraYSource, Settings.KeepPolygonContainingCentre ? new Vector2(Centre.X, Centre.Z) : null, Settings, out KeepStatus, out UnionMs, out ObstacleTriangles, out ObstacleBoxes, out WallSnapEdges, RimTriangles, null, FloorMeshes, IgnoredBoxes);
        return Polygons;
    }
}
