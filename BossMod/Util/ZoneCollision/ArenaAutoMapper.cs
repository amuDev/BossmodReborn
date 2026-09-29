using Clipper2Lib;
using System.Threading;

namespace BossMod;

public enum AdjacencyMode : byte { SharedEdge, SharedVertex, EdgeInterval }
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
    public MaterialMatchMode FloorMatchMode = MaterialMatchMode.EffectiveMasked;
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
    public float MaxSlopeDeg = 55f;
    public float WeldEps = 1e-3f;
    public float StepHeight = 0.5f; // neighbours are linked across a vertical step up to this height (raised plates, kerbs); 0 = exact 3D weld only
    public float SeamClose = 0.05f; // morphological closing radius applied to the floor union: fuses hairline slits between plates/tiles whose outlines do not coincide exactly (they would become notches once the bounds are offset inwards)
    public AdjacencyMode Adjacency = AdjacencyMode.EdgeInterval;
    public float EdgeSnap = 0.02f; // EdgeInterval: two floor edges connect when they run collinear within this XZ distance and overlap along their length (T-junctions, seams between separately welded meshes)
    public float GapBridge = 1f;   // two floor triangles whose edges come within this XZ gap at matching height connect (slatted bridges, plank ends floating over terrain, mesh seams that do not touch); 0 = off
    public float GapBridgeRise = 0.7f; // extra height tolerance per yalm of gap (a plank end sits above the ground it leads onto)
    // materials the game itself refuses to walk on (the 0x2000000 flag, and surface id 0x11) are never floor, whatever the whitelist or a forced-floor mesh says
    public bool ExcludeUnwalkableMaterials = true;
    // steep facets edge-connected to the floor are promoted when the whole patch is only a step rough around a walkable overall grade (rocky
    // cave floors and terrain skins), instead of needing the mesh marked as floor by hand
    public bool ReliefPromotion = true;
    public float ReliefStep = 0.5f;
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
    public bool ObstacleUnderFloor = true;   // an obstacle's footprint is not cut where selected floor runs above it (a cliff face under a bridge, rocks under a plank): the 2D projection keeps the walkable surface
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

    public static bool IsUnwalkableMaterial(ulong material) => (material & 0x2000000) != 0 || (material & 0x1F) == 0x11;

    public bool IsBlocked(in WorldTriangle t) => ExcludeUnwalkableMaterials && IsUnwalkableMaterial(t.Effective);

    public bool FloorMatches(in WorldTriangle t)
    {
        if (IsBlocked(t))
        {
            return false;
        }
        var usePrim = FloorMatchMode is MaterialMatchMode.PrimExact or MaterialMatchMode.PrimMasked;
        var material = usePrim ? t.Material : t.Effective;
        var exact = FloorMatchMode is MaterialMatchMode.PrimExact or MaterialMatchMode.EffectiveExact;
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

// a seal with its partner: another seal of the same room, a node (warp, pop point, exit gate, exit range; MarkerIndex + its position in
// Target) or nothing (Target = the room centre when the seal's area is known, so the inward side is still right); IsRoom = both seals of a boss arena
public readonly record struct SealPair(int SealA, int SealB, int MarkerIndex, float Distance, string Kind, Vector3 Target = default, bool IsRoom = false)
{
    public bool IsSingle => SealB < 0 && MarkerIndex < 0;
    public bool HasTarget => Target != default;
}

// the author's word over the automatic pairing: PartnerPathId 0 = keep this seal single, a seal box node = pair with that seal, a marker node = pair with that node
public readonly record struct PairOverride(ulong SealPathId, ulong PartnerPathId, bool PartnerIsNode);

// NoSeals: nothing to estimate from, the centre is the preferred point or the origin
public readonly record struct CentreEstimate(Vector3 Centre, int SealA, int SealB, int MarkerIndex, float RayGap, bool UsedFallback, string Reason, bool NoSeals = false);

public delegate bool MeshTest(int mesh, in Bounds3 worldBounds);

// candidate floor triangles (material/slope/radius) plus floor boxes as extra nodes, with CSR neighbour lists built by welding vertices on a quantized grid
public sealed class TriangleAdjacency
{
    public int[] Candidates = [];      // global triangle indices, local node i < Candidates.Length
    public int[] FloorBoxes = [];      // box indices, local node Candidates.Length + j
    public int[] NeighbourStart = [];
    public int[] Neighbours = [];
    public int[] GlobalToLocal = [];   // indexed by global triangle, -1 when not a candidate
    public TriangleGrid? CandidateGrid; // XZ grid over the candidates (null without candidates)
    public long BuildMs;
    public int Promoted, GapLinks, ForcedLinks;

    public int NodeCount => Candidates.Length + FloorBoxes.Length;
    public bool IsBoxNode(int local) => local >= Candidates.Length;
    public int LocalOf(int global) => (uint)global < (uint)GlobalToLocal.Length ? GlobalToLocal[global] : -1;

    private const long CellOffset = 1L << 20;
    private const ulong CellMask = (1UL << 21) - 1;

    // 21 bits per axis of a quantized position; the same packing keys welded vertices, rim chains and boundary edges
    public static ulong PackCell(long x, long y, long z) => (((ulong)(x + CellOffset) & CellMask) << 42) | (((ulong)(y + CellOffset) & CellMask) << 21) | ((ulong)(z + CellOffset) & CellMask);

    public static ulong WeldKey(in Vector3 v, float eps) => PackCell((long)MathF.Floor(v.X / eps), (long)MathF.Floor(v.Y / eps), (long)MathF.Floor(v.Z / eps));

    public static ulong EdgeKey(ulong a, ulong b) => a < b ? a * 1000003UL ^ b : b * 1000003UL ^ a;

    // every triangle of the enabled, non-empty meshes the test accepts, in mesh order; the visitor applies its own per-triangle window
    public static void ForEachTriangleOfMeshes<T>(ZoneCollisionScene scene, MeshTest test, ref T visitor, CancellationToken ct = default) where T : struct, ITriangleVisitor
    {
        var meshes = scene.Meshes;
        for (var m = 0; m < meshes.Count; ++m)
        {
            var mesh = meshes[m];
            if (mesh.TriCount == 0 || !scene.IsMeshEnabled(m) || !test(m, mesh.WorldBounds))
            {
                continue;
            }
            var end = mesh.TriStart + mesh.TriCount;
            for (var i = mesh.TriStart; i < end; ++i)
            {
                if ((i & 4095) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
                visitor.Visit(i);
            }
        }
    }

    public static bool OverlapsBox(in Bounds3 b, float minX, float minZ, float maxX, float maxZ, float minY, float maxY)
        => b.Max.X >= minX && b.Min.X <= maxX && b.Max.Z >= minZ && b.Min.Z <= maxZ && b.Max.Y >= minY && b.Min.Y <= maxY;

    private struct CandidateCollector(ZoneTriangleStore store, AutoMapSettings s, bool[] forcedFloorMesh, HashSet<int> forced, PathRegion? region, Vector3 centre, List<int> candidates, List<int> steep) : ITriangleVisitor
    {
        private readonly float _minNormalY = s.MinNormalY;
        private readonly float _r2 = s.MaxRadius * s.MaxRadius;

        public void Visit(int i)
        {
            ref readonly var t = ref store[i];
            if (forced.Contains(i))
            {
                candidates.Add(i); // walked on: floor by evidence
                return;
            }
            if (s.IsBlocked(t) || (!forcedFloorMesh[t.MeshIndex] && !s.FloorMatches(t)))
            {
                return;
            }
            var walkable = t.NormalY >= _minNormalY;
            if (!walkable && (!s.ReliefPromotion || t.NormalY < 1e-6f))
            {
                return;
            }
            var c = t.Centroid;
            if (region != null)
            {
                if (!region.Contains(c))
                {
                    return;
                }
            }
            else
            {
                var dx = c.X - centre.X;
                var dz = c.Z - centre.Z;
                if (dx * dx + dz * dz > _r2)
                {
                    return;
                }
            }
            (walkable ? candidates : steep).Add(i);
        }
    }

    private struct WallCollector(ZoneTriangleStore store, float minNormalY, List<int> walls) : ITriangleVisitor
    {
        public void Visit(int i)
        {
            if (store[i].NormalY < minNormalY)
            {
                walls.Add(i);
            }
        }
    }

    // candidate triangles touching a floor box's inflated footprint
    private struct BoxTouch(ZoneTriangleStore store, int[] globalToLocal, Path64 footprint, Bounds3 wb, float e2, float ey, long scale, List<(int a, int b)> pairs, int boxNode) : ITriangleVisitor
    {
        public void Visit(int g)
        {
            ref readonly var t = ref store[g];
            if (!OverlapsBox(t.Bounds, wb.Min.X - e2, wb.Min.Z - e2, wb.Max.X + e2, wb.Max.Z + e2, wb.Min.Y - ey, wb.Max.Y + ey))
            {
                return;
            }
            if (BoxFootprintOps.Contains(footprint, TrianglePolygonBuilder.ToP64(t.A, scale)) || BoxFootprintOps.Contains(footprint, TrianglePolygonBuilder.ToP64(t.B, scale))
                || BoxFootprintOps.Contains(footprint, TrianglePolygonBuilder.ToP64(t.C, scale)) || BoxFootprintOps.Contains(footprint, TrianglePolygonBuilder.ToP64(t.Centroid, scale)))
            {
                pairs.Add((globalToLocal[g], boxNode));
            }
        }
    }

    // floorMeshes: meshes the author marked as floor regardless of material (transparent platforms, odd surface ids)
    // region: candidates come from a corridor around a walked path instead of a ring around the centre; forcedTriangles are candidates whatever
    // their material or slope (the player stood on them); forcedLinks join two candidates the player stepped between (a bridge the geometry does not join)
    public static TriangleAdjacency Build(ZoneCollisionScene scene, in Vector3 centre, AutoMapSettings s, IReadOnlySet<int>? floorMeshes = null, PathRegion? region = null, IReadOnlyList<int>? forcedTriangles = null, IReadOnlyList<(int a, int b)>? forcedLinks = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var adj = new TriangleAdjacency();
        var tris = scene.Triangles.Span;
        var minNormalY = s.MinNormalY;
        var cxz = new Vector2(centre.X, centre.Z);
        // weld vertices; 21 bits per axis at eps covers +-(2^20 * eps) yalms, fall back to a coarser grid for huge coordinates
        var eps = s.WeldEps;
        var maxCoord = MathF.Max(MathF.Abs(scene.Bounds.Min.X), MathF.Max(MathF.Abs(scene.Bounds.Max.X), MathF.Max(MathF.Abs(scene.Bounds.Min.Z), MathF.Abs(scene.Bounds.Max.Z))));
        while (maxCoord / eps >= CellOffset - 1 && eps < 1f)
        {
            eps *= 2f;
        }
        List<int> candidates = [];
        List<int> steep = []; // material-eligible triangles that fail the slope test, for the relief promotion
        HashSet<int> forced = forcedTriangles != null ? [.. forcedTriangles] : [];
        var meshes = scene.Meshes;
        var forcedFloorMesh = new bool[meshes.Count];
        if (floorMeshes != null)
        {
            foreach (var m in floorMeshes)
            {
                if ((uint)m < (uint)forcedFloorMesh.Length)
                {
                    forcedFloorMesh[m] = true;
                }
            }
        }
        bool MeshNear(int m, in Bounds3 b) => region != null ? region.IntersectsBounds(b) : b.IntersectsXZCircle(cxz, s.MaxRadius);
        var collector = new CandidateCollector(scene.Triangles, s, forcedFloorMesh, forced, region, centre, candidates, steep);
        ForEachTriangleOfMeshes(scene, MeshNear, ref collector, ct);
        if (steep.Count > 0)
        {
            adj.Promoted = ReliefPromotion.Promote(tris, candidates, steep, minNormalY, s.ReliefStep, eps, candidates);
        }
        ct.ThrowIfCancellationRequested();
        adj.Candidates = [.. candidates];
        var n = adj.Candidates.Length;
        adj.GlobalToLocal = new int[scene.Triangles.Count];
        Array.Fill(adj.GlobalToLocal, -1);
        for (var i = 0; i < n; ++i)
        {
            adj.GlobalToLocal[adj.Candidates[i]] = i;
        }
        adj.CandidateGrid = n > 0 ? new TriangleGrid(tris, adj.Candidates) : null;

        // floor boxes inside the radius become extra nodes
        List<int> floorBoxes = [];
        for (var b = 0; b < scene.Boxes.Count; ++b)
        {
            var box = scene.Boxes[b];
            if (scene.IsBoxEnabled(b) && s.BoxIsFloor(box) && MeshNear(b, box.WorldBounds))
            {
                floorBoxes.Add(b);
            }
        }
        adj.FloorBoxes = [.. floorBoxes];

        var nodeCount = adj.NodeCount;
        var counts = new int[nodeCount + 1];
        List<(int a, int b)> pairs = [];
        EdgeRec[]? edges = null;
        if (s.Adjacency == AdjacencyMode.EdgeInterval)
        {
            edges = CandidateEdges(tris, adj.Candidates);
            EdgeIntervalPairs(edges, s.EdgeSnap, s.StepHeight, pairs, ct);
        }
        else
        {
            WeldedPairs(tris, adj.Candidates, s, eps, pairs);
        }
        ct.ThrowIfCancellationRequested();

        // floor boxes link to every candidate triangle touching their (inflated) footprint, and to each other when their footprints touch
        var scale = BoxFootprintOps.DefaultScale;
        var visited = adj.CandidateGrid != null && adj.FloorBoxes.Length > 0 ? new VisitStamp(scene.Triangles.Count) : null;
        for (var j = 0; j < adj.FloorBoxes.Length; ++j)
        {
            var box = scene.Boxes[adj.FloorBoxes[j]];
            var footprint = BoxFootprintOps.Inflate(box.FootprintXZ, s.BoxFloorTouchEps, scale);
            var wb = box.WorldBounds;
            var e2 = s.BoxFloorTouchEps;
            var ey = s.BoxFloorTouchHeight;
            if (adj.CandidateGrid != null)
            {
                var touch = new BoxTouch(scene.Triangles, adj.GlobalToLocal, footprint, wb, e2, ey, scale, pairs, n + j);
                adj.CandidateGrid.ForEachInRect(wb.Min.X - e2, wb.Min.Z - e2, wb.Max.X + e2, wb.Max.Z + e2, visited, ref touch);
            }
            for (var k = 0; k < j; ++k)
            {
                var other = scene.Boxes[adj.FloorBoxes[k]].WorldBounds;
                if (OverlapsBox(wb, other.Min.X - e2, other.Min.Z - e2, other.Max.X + e2, other.Max.Z + e2, other.Min.Y - ey, other.Max.Y + ey))
                {
                    pairs.Add((n + k, n + j));
                }
            }
        }

        if (s.GapBridge > 0f && n > 1)
        {
            edges ??= CandidateEdges(tris, adj.Candidates);
            adj.GapLinks = GapLinkPairs(scene, edges, n, s, MeshNear, pairs, ct);
        }
        if (forcedLinks != null)
        {
            foreach (var (ga, gb) in forcedLinks)
            {
                var la = adj.LocalOf(ga);
                var lb = adj.LocalOf(gb);
                if (la >= 0 && lb >= 0 && la != lb)
                {
                    pairs.Add((la, lb));
                    ++adj.ForcedLinks;
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

    // SharedEdge / SharedVertex: vertices welded on the eps grid; with a step tolerance vertices weld in XZ only and each link is checked for
    // its Y difference afterwards, so a raised plate (0.1 y step) still links to its neighbour while the ceiling slab above the floor does not
    private static void WeldedPairs(ReadOnlySpan<WorldTriangle> tris, int[] candidates, AutoMapSettings s, float eps, List<(int a, int b)> pairs)
    {
        var n = candidates.Length;
        var stepWeld = s.StepHeight > 0f;
        Dictionary<ulong, int> weld = new(n * 2);
        var triVerts = new int[3 * n];
        var triY = new float[3 * n];
        for (var i = 0; i < n; ++i)
        {
            ref readonly var t = ref tris[candidates[i]];
            triVerts[3 * i] = stepWeld ? WeldIdXZ(weld, t.A, eps) : WeldId(weld, t.A, eps);
            triVerts[3 * i + 1] = stepWeld ? WeldIdXZ(weld, t.B, eps) : WeldId(weld, t.B, eps);
            triVerts[3 * i + 2] = stepWeld ? WeldIdXZ(weld, t.C, eps) : WeldId(weld, t.C, eps);
            triY[3 * i] = t.A.Y;
            triY[3 * i + 1] = t.B.Y;
            triY[3 * i + 2] = t.C.Y;
        }
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
    }

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

    // EdgeInterval adjacency: two triangles connect when one edge of each runs collinear in XZ within snap and they overlap along their length,
    // with the height gap along the usable part of the overlap within the step height; shared edge intervals include T-junctions and the seams
    // between separately welded meshes, while merely touching at a corner never joins islands or storeys (ported from the live arena's CanStep)
    private readonly record struct EdgeRec(Vector3 A, Vector3 B, int Tri);

    private static EdgeRec[] CandidateEdges(ReadOnlySpan<WorldTriangle> tris, int[] candidates)
    {
        var n = candidates.Length;
        var edges = new EdgeRec[3 * n];
        for (var i = 0; i < n; ++i)
        {
            ref readonly var t = ref tris[candidates[i]];
            edges[3 * i] = new(t.A, t.B, i);
            edges[3 * i + 1] = new(t.B, t.C, i);
            edges[3 * i + 2] = new(t.C, t.A, i);
        }
        return edges;
    }

    private const float EdgeCell = 2f;

    private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    private static void EdgeIntervalPairs(EdgeRec[] edges, float snap, float step, List<(int a, int b)> pairs, CancellationToken ct)
    {
        var cells = new XZHashGrid(EdgeCell);
        HashSet<long> linked = []; // a pair links once, whichever cells or edges it shares; a failed test proves nothing, another edge pair may still step
        for (var e = 0; e < edges.Length; ++e)
        {
            if ((e & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            ref readonly var edge = ref edges[e];
            var x0 = cells.CellOf(MathF.Min(edge.A.X, edge.B.X) - snap);
            var x1 = cells.CellOf(MathF.Max(edge.A.X, edge.B.X) + snap);
            var z0 = cells.CellOf(MathF.Min(edge.A.Z, edge.B.Z) - snap);
            var z1 = cells.CellOf(MathF.Max(edge.A.Z, edge.B.Z) + snap);
            for (var cz = z0; cz <= z1; ++cz)
            {
                for (var cx = x0; cx <= x1; ++cx)
                {
                    var list = cells.GetOrAddCell(cx, cz, out var isNew);
                    if (isNew)
                    {
                        list.Add(e);
                        continue;
                    }
                    for (var k = 0; k < list.Count; ++k)
                    {
                        ref readonly var other = ref edges[list[k]];
                        if (other.Tri == edge.Tri || linked.Contains(PairKey(other.Tri, edge.Tri)) || !CanStep(edge, other, snap, step))
                        {
                            continue;
                        }
                        linked.Add(PairKey(other.Tri, edge.Tri));
                        pairs.Add((other.Tri, edge.Tri));
                    }
                    list.Add(e);
                }
            }
        }
    }

    // gap links: floor triangles that do not touch but come within GapBridge in XZ with their surfaces at matching height (a slatted bridge, a
    // plank end hovering over the ground it leads onto, a seam between meshes that leaves a slit) connect, unless a wall stands in the gap
    private static int GapLinkPairs(ZoneCollisionScene scene, EdgeRec[] edges, int n, AutoMapSettings s, MeshTest near, List<(int a, int b)> pairs, CancellationToken ct)
    {
        var gap = s.GapBridge;
        // components of the graph so far: a gap link between two triangles already connected adds nothing and is skipped before any geometry
        var sets = new UnionFind(n);
        foreach (var (a, b) in pairs)
        {
            if (a < n && b < n)
            {
                sets.Union(a, b);
            }
        }
        // walls near the region: anything steeper than the slope limit on an enabled mesh, whatever its material
        List<int> walls = [];
        var wallCollector = new WallCollector(scene.Triangles, s.MinNormalY, walls);
        ForEachTriangleOfMeshes(scene, near, ref wallCollector, ct);
        var wallGrid = walls.Count > 0 ? new TriangleGrid(scene.Triangles.Span, CollectionsMarshal.AsSpan(walls)) : null;
        var cells = new XZHashGrid(EdgeCell);
        var added = 0;
        for (var e = 0; e < edges.Length; ++e)
        {
            if ((e & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            ref readonly var edge = ref edges[e];
            var x0 = cells.CellOf(MathF.Min(edge.A.X, edge.B.X) - gap);
            var x1 = cells.CellOf(MathF.Max(edge.A.X, edge.B.X) + gap);
            var z0 = cells.CellOf(MathF.Min(edge.A.Z, edge.B.Z) - gap);
            var z1 = cells.CellOf(MathF.Max(edge.A.Z, edge.B.Z) + gap);
            for (var cz = z0; cz <= z1; ++cz)
            {
                for (var cx = x0; cx <= x1; ++cx)
                {
                    var list = cells.GetOrAddCell(cx, cz, out var isNew);
                    if (isNew)
                    {
                        list.Add(e);
                        continue;
                    }
                    for (var k = 0; k < list.Count; ++k)
                    {
                        ref readonly var other = ref edges[list[k]];
                        if (other.Tri == edge.Tri || sets.Find(other.Tri) == sets.Find(edge.Tri))
                        {
                            continue;
                        }
                        if (!GapBridgeable(edge, other, gap, s.StepHeight, s.GapBridgeRise, scene.Triangles, wallGrid))
                        {
                            continue;
                        }
                        sets.Union(other.Tri, edge.Tri);
                        pairs.Add((other.Tri, edge.Tri));
                        ++added;
                    }
                    list.Add(e);
                }
            }
        }
        return added;
    }

    private struct GapWallTest(ZoneTriangleStore store, Vector2 s0, Vector2 s1, float lowY, float highY) : ITriangleVisitor
    {
        public bool Blocked;

        public void Visit(int w)
        {
            if (Blocked)
            {
                return;
            }
            ref readonly var t = ref store[w];
            var wb = t.Bounds;
            if (wb.Max.Y < lowY || wb.Min.Y > highY)
            {
                return;
            }
            Blocked = SegmentCrossesTriangleXZ(s0, s1, t);
        }
    }

    // closest points of the two edges in XZ within the gap, surfaces within the step (plus the rise allowance) there, no wall crossing the gap
    private static bool GapBridgeable(in EdgeRec a, in EdgeRec b, float gap, float step, float rise, ZoneTriangleStore store, TriangleGrid? walls)
    {
        var (d2, ta, tb) = ArenaAutoMapper.SegmentDistSqXZ(a.A, a.B, b.A, b.B);
        if (d2 > gap * gap || d2 < 1e-4f * 1e-4f)
        {
            return false;
        }
        var d = MathF.Sqrt(d2);
        var pa = Vector3.Lerp(a.A, a.B, ta);
        var pb = Vector3.Lerp(b.A, b.B, tb);
        if (MathF.Abs(pa.Y - pb.Y) > step + rise * d)
        {
            return false;
        }
        if (walls == null)
        {
            return true;
        }
        // a wall triangle whose XZ shape the gap segment crosses, in the height band of the crossing, blocks the link (thin walls between two floors)
        var test = new GapWallTest(store, new(pa.X, pa.Z), new(pb.X, pb.Z), MathF.Min(pa.Y, pb.Y) - 0.3f, MathF.Max(pa.Y, pb.Y) + 1.8f);
        walls.ForEachInRect(MathF.Min(pa.X, pb.X), MathF.Min(pa.Z, pb.Z), MathF.Max(pa.X, pb.X), MathF.Max(pa.Z, pb.Z), null, ref test);
        return !test.Blocked;
    }

    private static bool SegmentCrossesTriangleXZ(Vector2 s0, Vector2 s1, in WorldTriangle t)
    {
        Vector2 a = new(t.A.X, t.A.Z), b = new(t.B.X, t.B.Z), c = new(t.C.X, t.C.Z);
        return t.ContainsXZ(s0.X, s0.Y) || t.ContainsXZ(s1.X, s1.Y) || SegmentsIntersect(s0, s1, a, b) || SegmentsIntersect(s0, s1, b, c) || SegmentsIntersect(s0, s1, c, a);

        static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;
        static bool SegmentsIntersect(Vector2 p, Vector2 p2, Vector2 q, Vector2 q2)
        {
            var r = p2 - p;
            var s = q2 - q;
            var denom = Cross(r, s);
            if (MathF.Abs(denom) < 1e-9f)
            {
                return false;
            }
            var t = Cross(q - p, s) / denom;
            var u = Cross(q - p, r) / denom;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }
    }

    private static bool CanStep(EdgeRec a, EdgeRec b, float snap, float step)
    {
        // refer to the longer edge so a tiny segment does not magnify the other edge's endpoint roundoff when testing collinearity
        if (XZLengthSq(b) > XZLengthSq(a))
        {
            (a, b) = (b, a);
        }
        double dx = a.B.X - a.A.X, dz = a.B.Z - a.A.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (length <= 1e-6d)
        {
            return false;
        }
        dx /= length;
        dz /= length;
        double bax = b.A.X - a.A.X, baz = b.A.Z - a.A.Z, bbx = b.B.X - a.A.X, bbz = b.B.Z - a.A.Z;
        var separation = Math.Max(Math.Abs(dx * baz - dz * bax), Math.Abs(dx * bbz - dz * bbx));
        if (separation > snap)
        {
            return false;
        }
        // the snap distance is not a minimum edge length: sub-snap shared intervals are useful on rocky meshes when the edges coincide
        var minimum = separation <= 1e-5d ? 1e-5d : snap;
        var start = bax * dx + baz * dz;
        var end = bbx * dx + bbz * dz;
        var low = Math.Max(0d, Math.Min(start, end));
        var high = Math.Min(length, Math.Max(start, end));
        if (high - low <= minimum || Math.Abs(end - start) <= minimum)
        {
            return false;
        }
        // the height difference varies linearly along the overlap: keep the usable interval instead of rejecting a whole ramp edge for its high end
        var gapLow = Gap(low);
        var gapHigh = Gap(high);
        var limit = step + snap;
        var change = gapHigh - gapLow;
        if (Math.Abs(change) < 1e-6d)
        {
            return Math.Abs(gapLow) <= limit;
        }
        var t0 = (-limit - gapLow) / change;
        var t1 = (limit - gapLow) / change;
        var begin = Math.Max(0d, Math.Min(t0, t1));
        var finish = Math.Min(1d, Math.Max(t0, t1));
        return begin < finish && (high - low) * (finish - begin) > minimum;

        double Gap(double p) => a.A.Y + (a.B.Y - a.A.Y) * (p / length) - (b.A.Y + (b.B.Y - b.A.Y) * ((p - start) / (end - start)));
    }

    private static float XZLengthSq(in EdgeRec e)
    {
        var dx = e.B.X - e.A.X;
        var dz = e.B.Z - e.A.Z;
        return dx * dx + dz * dz;
    }

    private static int WeldId(Dictionary<ulong, int> weld, in Vector3 v, float eps)
    {
        var key = WeldKey(v, eps);
        if (!weld.TryGetValue(key, out var id))
        {
            id = weld.Count;
            weld[key] = id;
        }
        return id;
    }

    public ReadOnlySpan<int> NeighboursOf(int local) => Neighbours.AsSpan(NeighbourStart[local], NeighbourStart[local + 1] - NeighbourStart[local]);

    // breadth-first over the neighbour lists from the seeds (local nodes): the visitor accepts or refuses each node when it is discovered
    // (seeds at depth 0), refused nodes are neither taken nor expanded, and nodes at maxDepth are taken but not expanded
    public void Walk<T>(ReadOnlySpan<int> seeds, int maxDepth, ref T visitor, CancellationToken ct = default) where T : struct, IWalkVisitor
    {
        var nodeCount = NodeCount;
        var visited = new bool[nodeCount];
        var queue = new int[nodeCount];
        var depth = new int[nodeCount];
        var head = 0;
        var tail = 0;
        foreach (var seed in seeds)
        {
            if ((uint)seed < (uint)nodeCount && !visited[seed])
            {
                visited[seed] = true;
                if (visitor.Enter(seed, 0))
                {
                    queue[tail++] = seed;
                }
            }
        }
        while (head < tail)
        {
            if ((head & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            var cur = queue[head++];
            var d = depth[cur];
            if (d >= maxDepth)
            {
                continue;
            }
            foreach (var nb in NeighboursOf(cur))
            {
                if (visited[nb])
                {
                    continue;
                }
                visited[nb] = true;
                if (visitor.Enter(nb, d + 1))
                {
                    depth[nb] = d + 1;
                    queue[tail++] = nb;
                }
            }
        }
    }
}

public interface IWalkVisitor
{
    bool Enter(int node, int depth);
}

public sealed class AutoMapResult
{
    public Vector3 Centre;
    public int[] Selected = [];
    public int[] SelectedBoxes = [];
    public string Status = "";
    public long FillMs;
}

// which boxes classify as seals (IsSealBox: material and geometry): the box and the seal settings decide that, the scene state does not, so a
// re-detect after a scene change only re-reads which of them are placed / enabled. Boxes are fixed once the scene is loaded
public sealed class SealClassCache
{
    private ZoneCollisionScene? _scene;
    private int _boxCount = -1;
    private ulong _materialValue, _materialMask;
    private bool _requireExactMask, _geometryFilter;
    private float _maxThickness, _minWidth, _minWidthToHeight, _footprintInflate;
    private readonly List<SealCandidate> _seals = [];

    // every box that passes IsSealBox under the settings, in box order
    public List<SealCandidate> Classified(ZoneCollisionScene scene, AutoMapSettings s)
    {
        if (_scene == scene && _boxCount == scene.Boxes.Count && _materialValue == s.SealMaterialValue && _materialMask == s.SealMaterialMask && _requireExactMask == s.SealRequireExactMask
            && _geometryFilter == s.SealGeometryFilter && _maxThickness == s.SealMaxThickness && _minWidth == s.SealMinWidth && _minWidthToHeight == s.SealMinWidthToHeight && _footprintInflate == s.SealFootprintInflate)
        {
            return _seals;
        }
        _seals.Clear();
        var boxes = scene.Boxes;
        for (var i = 0; i < boxes.Count; ++i)
        {
            if (ArenaAutoMapper.IsSealBox(boxes[i], s, out var seal))
            {
                _seals.Add(seal);
            }
        }
        _scene = scene;
        _boxCount = boxes.Count;
        _materialValue = s.SealMaterialValue;
        _materialMask = s.SealMaterialMask;
        _requireExactMask = s.SealRequireExactMask;
        _geometryFilter = s.SealGeometryFilter;
        _maxThickness = s.SealMaxThickness;
        _minWidth = s.SealMinWidth;
        _minWidthToHeight = s.SealMinWidthToHeight;
        _footprintInflate = s.SealFootprintInflate;
        return _seals;
    }
}

public static class ArenaAutoMapper
{
    // FindSeals over the cached classification: the same list (a new one per call, box order), only the placed / enabled part is evaluated
    public static List<SealCandidate> FindSeals(ZoneCollisionScene scene, AutoMapSettings s, SealClassCache cache)
    {
        List<SealCandidate> result = [];
        var classified = cache.Classified(scene, s);
        for (var i = 0; i < classified.Count; ++i)
        {
            var box = classified[i].BoxIndex;
            if (!scene.IsBoxPlaced(box) || (!s.SealIncludeInactive && !scene.IsBoxEnabled(box)))
            {
                continue;
            }
            result.Add(classified[i]);
        }
        return result;
    }

    // seals are listed while their instance is placed in the scene; a seal whose event object currently has the collision removed is listed
    // only with SealIncludeInactive (it is still a room boundary the author may want to cut)
    public static List<SealCandidate> FindSeals(ZoneCollisionScene scene, AutoMapSettings s)
    {
        List<SealCandidate> result = [];
        var boxes = scene.Boxes;
        for (var i = 0; i < boxes.Count; ++i)
        {
            if (!scene.IsBoxPlaced(i) || (!s.SealIncludeInactive && !scene.IsBoxEnabled(i)))
            {
                continue;
            }
            if (IsSealBox(boxes[i], s, out var seal))
            {
                result.Add(seal);
            }
        }
        return result;
    }

    // seal material (and exact designer mask), thin-wide-door geometry when the filter is on
    public static bool IsSealBox(ZoneBoxInstance b, AutoMapSettings s, out SealCandidate seal)
    {
        seal = default;
        if (b.Kind != LgbColliderKind.Box || (b.MatValue & s.SealMaterialMask) != (s.SealMaterialValue & s.SealMaterialMask) || (s.SealRequireExactMask && b.MatMask != s.SealMaterialMask))
        {
            return false;
        }
        seal = MakeSeal(b, s);
        return !s.SealGeometryFilter || seal.PassesGeometry;
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
        var footprint = b.FootprintXZ;
        var inflated = BoxFootprintOps.Inflate(footprint, s.SealFootprintInflate);
        return new(b.Index, b.Center, axis, longDir, thickness, width, height, passes, footprint, inflated);
    }

    // pairing from the layout: every seal belongs to one room. Seals inside the same boss arena (map range with map 0) pair with each other;
    // seals left alone pair with the nearest node on their inward side (warp, player pop point / landing, exit gate, exit range); the rest stay
    // single but know which side the room is on. Overrides replace the automatic choice for their seal
    public static List<SealPair> AutoPairRooms(List<SealCandidate> seals, ZoneCollisionScene scene, ZoneSceneModel model, AutoMapSettings s, IReadOnlyList<PairOverride>? overrides = null)
    {
        var n = seals.Count;
        // only map ranges with map 0 mark boss arenas; a zone without them (older dungeons) pairs by geometry alone
        var bossAreas = model.Areas.Where(a => a.Map == 0).ToList();
        // the boss area of each seal: the smallest one whose bounds (inflated, seals sit on the edge) contain its centre
        var areaOf = new int[n];
        // two seal boxes at the same door (a vfx box over a bg box): the later one is a duplicate and follows the first
        var duplicateOf = new int[n];
        Array.Fill(duplicateOf, -1);
        for (var i = 0; i < n; ++i)
        {
            for (var j = 0; j < i; ++j)
            {
                if (duplicateOf[j] < 0 && (seals[i].Center - seals[j].Center).Length() < 3f && MathF.Abs(Vector3.Dot(seals[i].ThinAxisWorld, seals[j].ThinAxisWorld)) > 0.9f)
                {
                    duplicateOf[i] = j;
                    break;
                }
            }
        }
        for (var i = 0; i < n; ++i)
        {
            areaOf[i] = -1;
            var bestSize = float.MaxValue;
            for (var a = 0; a < bossAreas.Count; ++a)
            {
                var b = bossAreas[a].WorldBounds;
                var c = seals[i].Center;
                if (c.X >= b.Min.X - 4f && c.X <= b.Max.X + 4f && c.Z >= b.Min.Z - 4f && c.Z <= b.Max.Z + 4f && c.Y >= b.Min.Y - 8f && c.Y <= b.Max.Y + 8f)
                {
                    var size = (b.Max.X - b.Min.X) * (b.Max.Z - b.Min.Z);
                    if (size < bestSize)
                    {
                        bestSize = size;
                        areaOf[i] = a;
                    }
                }
            }
        }
        var partner = new int[n];
        Array.Fill(partner, -1);
        List<SealPair> pairs = [];
        // (1) within a boss area: the two seals whose inward rays meet best
        foreach (var group in Enumerable.Range(0, n).Where(i => areaOf[i] >= 0 && duplicateOf[i] < 0).GroupBy(i => areaOf[i]))
        {
            var members = group.ToList();
            while (members.Count >= 2)
            {
                var best = (i: -1, j: -1, gap: float.MaxValue);
                for (var x = 0; x < members.Count; ++x)
                {
                    for (var y = x + 1; y < members.Count; ++y)
                    {
                        var i = members[x];
                        var j = members[y];
                        if ((seals[i].Center - seals[j].Center).Length() < 4f)
                        {
                            continue; // the same doorway
                        }
                        var est = TwoRays(new(i, j, -1, 0f, "room"), seals[i], seals[j]);
                        var gap = est.UsedFallback ? 1000f + (seals[i].Center - seals[j].Center).Length() : est.RayGap;
                        if (gap < best.gap)
                        {
                            best = (i, j, gap);
                        }
                    }
                }
                if (best.i < 0)
                {
                    break;
                }
                partner[best.i] = best.j;
                partner[best.j] = best.i;
                pairs.Add(new(Math.Min(best.i, best.j), Math.Max(best.i, best.j), -1, (seals[best.i].Center - seals[best.j].Center).Length(), best.gap < 1000f ? "room" : "room (midpoint)", bossAreas[group.Key].WorldBounds.Center, true));
                members.Remove(best.i);
                members.Remove(best.j);
            }
        }
        // (2) seals still free: the closest other free seal whose inward rays meet (a room the map ranges do not mark); never across two
        // different boss areas
        for (var i = 0; i < n; ++i)
        {
            if (partner[i] >= 0 || duplicateOf[i] >= 0)
            {
                continue;
            }
            var best = (j: -1, gap: float.MaxValue, d: 0f);
            for (var j = 0; j < n; ++j)
            {
                if (j == i || partner[j] >= 0 || duplicateOf[j] >= 0 || areaOf[i] >= 0 && areaOf[j] >= 0 && areaOf[i] != areaOf[j])
                {
                    continue;
                }
                var d = (seals[j].Center - seals[i].Center).Length();
                if (d > s.SealPairMaxDistance || d < 4f)
                {
                    continue;
                }
                var est = TwoRays(new(i, j, -1, d, "seals"), seals[i], seals[j]);
                if (!est.UsedFallback && est.RayGap < 8f && est.RayGap + d * 0.05f < best.gap)
                {
                    best = (j, est.RayGap + d * 0.05f, d);
                }
            }
            if (best.j >= 0)
            {
                partner[i] = best.j;
                partner[best.j] = i;
                pairs.Add(new(Math.Min(i, best.j), Math.Max(i, best.j), -1, best.d, "seals"));
            }
        }
        // (3) the rest pair with a node on their inward side, else stay single facing their room
        var nodes = PairNodes(scene, model);
        for (var i = 0; i < n; ++i)
        {
            if (partner[i] >= 0 || duplicateOf[i] >= 0)
            {
                continue;
            }
            var seal = seals[i];
            var inward = areaOf[i] >= 0 ? bossAreas[areaOf[i]].WorldBounds.Center - seal.Center : Vector3.Zero;
            var best = (m: -1, d: float.MaxValue, kind: "", pos: Vector3.Zero);
            foreach (var (m, pos, kind) in nodes)
            {
                var d = (pos - seal.Center).Length();
                if (d > s.SealPairMaxDistance || d < 0.5f)
                {
                    continue;
                }
                // known room side: the node must be on it
                if (inward != Vector3.Zero && Vector3.Dot(new Vector3(inward.X, 0f, inward.Z), new Vector3(pos.X - seal.Center.X, 0f, pos.Z - seal.Center.Z)) < 0f)
                {
                    continue;
                }
                if (d < best.d)
                {
                    best = (m, d, kind, pos);
                }
            }
            if (best.m >= 0)
            {
                pairs.Add(new(i, -1, best.m, best.d, $"seal + {best.kind}", best.pos));
            }
            else
            {
                pairs.Add(new(i, -1, -1, 0f, areaOf[i] >= 0 ? "single seal (room known)" : "single seal", areaOf[i] >= 0 ? bossAreas[areaOf[i]].WorldBounds.Center : default));
            }
        }
        // duplicates face wherever their door's first box faces
        for (var i = 0; i < n; ++i)
        {
            if (duplicateOf[i] < 0)
            {
                continue;
            }
            var p = pairs.Find(x => x.SealA == duplicateOf[i] || x.SealB == duplicateOf[i]);
            var target = p.SealB >= 0 ? seals[p.SealA == duplicateOf[i] ? p.SealB : p.SealA].Center : p.HasTarget ? p.Target : default;
            pairs.Add(new(i, -1, -1, 0f, $"duplicate of seal {duplicateOf[i]}", target));
        }
        if (overrides != null && overrides.Count > 0)
        {
            ApplyOverrides(pairs, seals, scene, nodes, overrides, areaOf, bossAreas);
        }
        pairs.Sort((a, b) => a.SealA.CompareTo(b.SealA));
        return pairs;
    }

    // the nodes a seal may pair with: warps and the exit gate (actor positions), player pop points, replay-confirmed landings, exit ranges
    public static List<(int marker, Vector3 pos, string kind)> PairNodes(ZoneCollisionScene scene, ZoneSceneModel model)
    {
        List<(int, Vector3, string)> r = [];
        foreach (var eo in model.EventObjects)
        {
            if (eo.Role is ZoneObjectRole.Warp or ZoneObjectRole.Exit or ZoneObjectRole.Shortcut && eo.MarkerIndex >= 0)
            {
                r.Add((eo.MarkerIndex, eo.ActorPosition, eo.Role == ZoneObjectRole.Exit ? "exit gate" : eo.Role == ZoneObjectRole.Shortcut ? "shortcut" : "warp"));
            }
        }
        foreach (var p in model.PopPoints)
        {
            if (p.PopType == LgbPopType.Pc && p.MarkerIndex >= 0)
            {
                r.Add((p.MarkerIndex, p.Position, "pop point"));
            }
        }
        for (var m = 0; m < scene.Markers.Count; ++m)
        {
            if (scene.Markers[m].Type == (int)LgbInstanceType.ExitRange)
            {
                r.Add((m, scene.Markers[m].Position, "exit range"));
            }
        }
        return r;
    }

    public static ulong SealPathId(ZoneCollisionScene scene, in SealCandidate seal)
    {
        var node = scene.Boxes[seal.BoxIndex].NodeIndex;
        return node >= 0 ? scene.Nodes[node].PathId : scene.Boxes[seal.BoxIndex].LayoutObjectId;
    }

    public static ulong MarkerPathId(ZoneCollisionScene scene, int marker)
    {
        var node = scene.Markers[marker].NodeIndex;
        return node >= 0 ? scene.Nodes[node].PathId : scene.Markers[marker].LayoutObjectId;
    }

    private static void ApplyOverrides(List<SealPair> pairs, List<SealCandidate> seals, ZoneCollisionScene scene, List<(int marker, Vector3 pos, string kind)> nodes, IReadOnlyList<PairOverride> overrides, int[] areaOf, List<ZoneMapArea> bossAreas)
    {
        int SealBy(ulong pathId) => seals.FindIndex(x => SealPathId(scene, x) == pathId);
        void Detach(int seal)
        {
            for (var k = pairs.Count - 1; k >= 0; --k)
            {
                var p = pairs[k];
                if (p.SealA == seal || p.SealB == seal)
                {
                    pairs.RemoveAt(k);
                    var other = p.SealA == seal ? p.SealB : p.SealA;
                    if (other >= 0)
                    {
                        pairs.Add(new(other, -1, -1, 0f, areaOf[other] >= 0 ? "single seal (room known)" : "single seal", areaOf[other] >= 0 ? bossAreas[areaOf[other]].WorldBounds.Center : default));
                    }
                }
            }
        }
        foreach (var o in overrides)
        {
            var i = SealBy(o.SealPathId);
            if (i < 0)
            {
                continue;
            }
            Detach(i);
            if (o.PartnerPathId == 0)
            {
                pairs.Add(new(i, -1, -1, 0f, "manual: single", areaOf[i] >= 0 ? bossAreas[areaOf[i]].WorldBounds.Center : default));
            }
            else if (!o.PartnerIsNode)
            {
                var j = SealBy(o.PartnerPathId);
                if (j < 0 || j == i)
                {
                    continue;
                }
                Detach(j);
                pairs.Add(new(Math.Min(i, j), Math.Max(i, j), -1, (seals[i].Center - seals[j].Center).Length(), "manual: seals"));
            }
            else
            {
                var node = nodes.Find(x => MarkerPathId(scene, x.marker) == o.PartnerPathId);
                if (node.kind.Length == 0)
                {
                    continue;
                }
                pairs.Add(new(i, -1, node.marker, (node.pos - seals[i].Center).Length(), $"manual: seal + {node.kind}", node.pos));
            }
        }
    }

    // the seal's thin axis in XZ, zero when the box lies flat (a hatch, a filter-off cube): no inward direction to estimate from
    private static Vector2 InwardXZ(in Vector3 thinAxis)
    {
        var d = new Vector2(thinAxis.X, thinAxis.Z);
        return d.LengthSquared() < 1e-3f * 1e-3f ? Vector2.Zero : Vector2.Normalize(d);
    }

    public static CentreEstimate EstimateCentre(in SealPair pair, List<SealCandidate> seals, List<ZoneMarker> markers, AutoMapSettings s, Vector3? preferNear)
    {
        if (pair.SealA < 0 || pair.SealA >= seals.Count)
        {
            return new(preferNear ?? Vector3.Zero, -1, -1, -1, 0f, true, "no seals", true);
        }
        var a = seals[pair.SealA];
        if (pair.SealB >= 0 && pair.SealB < seals.Count)
        {
            return TwoRays(pair, a, seals[pair.SealB]);
        }
        var d = a.ThinAxisWorld;
        if (pair.MarkerIndex >= 0 && pair.MarkerIndex < markers.Count)
        {
            // foot of the perpendicular from the node onto the seal's inward ray, capped to the pair distance
            var target = pair.HasTarget ? pair.Target : markers[pair.MarkerIndex].Position;
            if (Vector3.Dot(d, target - a.Center) < 0f)
            {
                d = -d;
            }
            var dxz = InwardXZ(d);
            if (dxz == Vector2.Zero)
            {
                return new(a.Center, pair.SealA, -1, pair.MarkerIndex, 0f, true, "seal lies flat - seal centre");
            }
            var w = new Vector2(target.X - a.Center.X, target.Z - a.Center.Z);
            var t = Math.Clamp(Vector2.Dot(w, dxz), 0f, w.Length());
            var c = new Vector2(a.Center.X, a.Center.Z) + dxz * (t * 0.5f);
            return new(new(c.X, (a.Center.Y + target.Y) * 0.5f, c.Y), pair.SealA, -1, pair.MarkerIndex, 0f, false, pair.Kind);
        }
        if (pair.HasTarget)
        {
            // the seal's room is known from the map ranges: face it
            if (Vector3.Dot(d, pair.Target - a.Center) < 0f)
            {
                d = -d;
            }
            return new(a.Center + d * (s.MaxRadius * 0.5f), pair.SealA, -1, -1, 0f, false, "single seal facing its room");
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
        var y = (ci.Y + cj.Y) * 0.5f;
        var ei = InwardXZ(di);
        var ej = InwardXZ(dj);
        if (ei == Vector2.Zero || ej == Vector2.Zero)
        {
            var flat = (pi + pj) * 0.5f;
            return new(new(flat.X, y, flat.Y), pair.SealA, pair.SealB, -1, 0f, true, "seal lies flat - midpoint");
        }
        var w = pi - pj;
        var a = Vector2.Dot(ei, ei);
        var b = Vector2.Dot(ei, ej);
        var c = Vector2.Dot(ej, ej);
        var d = Vector2.Dot(ei, w);
        var e = Vector2.Dot(ej, w);
        var denom = a * c - b * b;
        if (MathF.Abs(b) > 0.99f || MathF.Abs(denom) < 1e-4f)
        {
            // two doors on opposite walls face each other with parallel rays: the room is between them when the doors line up
            var mid = (pi + pj) * 0.5f;
            var offset = MathF.Abs(ei.X * w.Y - ei.Y * w.X);
            if (offset <= 10f)
            {
                return new(new(mid.X, y, mid.Y), pair.SealA, pair.SealB, -1, offset, false, "facing doors - midpoint");
            }
            return new(new(mid.X, y, mid.Y), pair.SealA, pair.SealB, -1, offset, true, "rays parallel - midpoint");
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

    // the candidate under (x, z) whose surface is nearest to y in height, or within maxDy when given; -1 when none contains the point
    private struct SeedUnder(ZoneTriangleStore store, float x, float z, float y, float maxDy) : ITriangleVisitor
    {
        public int Best = -1;
        public float BestDy = maxDy;

        public void Visit(int g)
        {
            ref readonly var t = ref store[g];
            if (!t.ContainsXZ(x, z))
            {
                return;
            }
            var dy = MathF.Abs(t.YAt(x, z) - y);
            if (dy < BestDy)
            {
                BestDy = dy;
                Best = g;
            }
        }
    }

    // the candidate whose centroid is nearest to (x, z) within the radius
    private struct SeedNear(ZoneTriangleStore store, float x, float z, float radius) : ITriangleVisitor
    {
        public int Best = -1;
        public float BestD2 = radius * radius;

        public void Visit(int g)
        {
            var c = store[g].Centroid;
            var dx = c.X - x;
            var dz = c.Z - z;
            var d = dx * dx + dz * dz;
            if (d < BestD2)
            {
                BestD2 = d;
                Best = g;
            }
        }
    }

    // the grid triangle under p in XZ whose surface is nearest to p's height, within maxDy; -1 when none
    public static int TriangleUnder(ZoneCollisionScene scene, TriangleGrid grid, in Vector3 p, float maxDy)
    {
        var under = new SeedUnder(scene.Triangles, p.X, p.Z, p.Y, maxDy);
        grid.ForEachInRect(p.X, p.Z, p.X, p.Z, null, ref under);
        return under.Best;
    }

    public static int FindSeed(ZoneCollisionScene scene, TriangleAdjacency adj, in Vector3 centre, AutoMapSettings s)
    {
        if (adj.CandidateGrid == null)
        {
            return -1;
        }
        var under = TriangleUnder(scene, adj.CandidateGrid, centre, float.MaxValue);
        if (under >= 0)
        {
            return under;
        }
        var r = s.SeedSearchRadius;
        var near = new SeedNear(scene.Triangles, centre.X, centre.Z, r);
        adj.CandidateGrid.ForEachInRect(centre.X - r, centre.Z - r, centre.X + r, centre.Z + r, null, ref near);
        return near.Best;
    }

    public static bool InsideSeal(in Vector3 p, List<SealCandidate> seals, IReadOnlySet<int> active, long scale)
    {
        var pt = TrianglePolygonBuilder.ToP64(p, scale);
        for (var i = 0; i < seals.Count; ++i)
        {
            if (active.Contains(seals[i].BoxIndex) && BoxFootprintOps.Contains(seals[i].InflatedFootprintXZ, pt))
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
            var n = InwardXZ(seal.ThinAxisWorld);
            if (n == Vector2.Zero)
            {
                continue;
            }
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

    public static AutoMapResult FloodFill(ZoneCollisionScene scene, TriangleAdjacency adj, int seed, List<SealCandidate> seals, IReadOnlySet<int> activeSeals, in Vector3 centre, AutoMapSettings s, CancellationToken ct = default)
        => FloodFill(scene, adj, seed >= 0 ? [seed] : [], seals, activeSeals, centre, s, ct);

    // takes every reachable node except triangles a seal blocks
    private struct FillVisitor(ZoneCollisionScene scene, TriangleAdjacency adj, List<SealCandidate> seals, IReadOnlySet<int> activeSeals, Vector3 centre, AutoMapSettings s, List<int> selected, List<int> selectedBoxes) : IWalkVisitor
    {
        private readonly long _scale = BoxFootprintOps.DefaultScale;
        public int Blocked;

        public bool Enter(int node, int depth)
        {
            if (adj.IsBoxNode(node))
            {
                selectedBoxes.Add(adj.FloorBoxes[node - adj.Candidates.Length]);
                return true;
            }
            var g = adj.Candidates[node];
            if (depth > 0)
            {
                ref readonly var t = ref scene.Triangles[g];
                var blockedBySeal = s.SealBlock switch
                {
                    SealBlockMode.Centroid => InsideSeal(t.Centroid, seals, activeSeals, _scale),
                    SealBlockMode.AnyVertex => InsideSeal(t.A, seals, activeSeals, _scale) || InsideSeal(t.B, seals, activeSeals, _scale) || InsideSeal(t.C, seals, activeSeals, _scale),
                    _ => BehindSeal(t, seals, activeSeals, centre, s.SealBehindDepth),
                };
                if (blockedBySeal)
                {
                    ++Blocked;
                    return false;
                }
            }
            selected.Add(g);
            return true;
        }
    }

    // several seeds: every triangle the player stood on starts the fill (a path through a dungeon segment)
    public static AutoMapResult FloodFill(ZoneCollisionScene scene, TriangleAdjacency adj, ReadOnlySpan<int> seeds, List<SealCandidate> seals, IReadOnlySet<int> activeSeals, in Vector3 centre, AutoMapSettings s, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = new AutoMapResult { Centre = centre };
        var local = new List<int>(seeds.Length);
        foreach (var seed in seeds)
        {
            var l = adj.LocalOf(seed);
            if (l >= 0)
            {
                local.Add(l);
            }
        }
        if (local.Count == 0)
        {
            result.Status = seeds.Length <= 1 ? "no floor triangle at the centre" : "no floor triangle under the path";
            return result;
        }
        List<int> selected = [];
        List<int> selectedBoxes = [];
        var visitor = new FillVisitor(scene, adj, seals, activeSeals, centre, s, selected, selectedBoxes);
        adj.Walk(CollectionsMarshal.AsSpan(local), int.MaxValue, ref visitor, ct);
        result.Selected = [.. selected];
        result.SelectedBoxes = [.. selectedBoxes];
        result.FillMs = sw.ElapsedMilliseconds;
        result.Status = $"filled {selected.Count} triangles and {selectedBoxes.Count} floor box(es) from {adj.Candidates.Length} candidates{(seeds.Length > 1 ? $" and {seeds.Length} path seeds" : "")}, {visitor.Blocked} blocked by seals{(adj.GapLinks > 0 ? $", {adj.GapLinks} gap links" : "")}{(adj.ForcedLinks > 0 ? $", {adj.ForcedLinks} walked links" : "")}";
        return result;
    }

    // unselected, non-walkable triangles in the floor's XZ range and height band: their outlines as obstacle strips, rim triangles apart
    private struct ObstacleCollector(ZoneCollisionScene scene, FloorSelection floor, AutoMapSettings s, IReadOnlySet<int>? rimTriangles, float bandMin, float bandMax, long scale, Paths64 edges, List<float> edgeBase, Paths64 rimEdges) : ITriangleVisitor
    {
        private readonly float _minNormalY = s.MinNormalY;
        private readonly Bounds3 _fb = floor.Bounds;
        public int ObstacleTriangles;

        public void Visit(int i)
        {
            ref readonly var t = ref scene.Triangles[i];
            var walkable = floor.Contains(i) || (t.NormalY >= _minNormalY && s.FloorMatches(t));
            if (walkable)
            {
                return;
            }
            var b = t.Bounds;
            if (!TriangleAdjacency.OverlapsBox(b, _fb.Min.X, _fb.Min.Z, _fb.Max.X, _fb.Max.Z, bandMin, bandMax))
            {
                return;
            }
            if (b.Max.Y - b.Min.Y < s.ObstacleMinHeight)
            {
                return; // decal, low step, the side face of a raised plate
            }
            if (s.ObstacleLocalHeight)
            {
                if (!floor.Overlaps(b, s.ObstacleHeightBelow, s.ObstacleHeightAbove, out var floorTop))
                {
                    return; // no selected floor at this height below/around the triangle
                }
                if (b.Max.Y <= floorTop + s.StepHeight)
                {
                    return; // does not rise above the step height over the floor next to it (kerb, plate edge)
                }
            }
            ++ObstacleTriangles;
            var pa = TrianglePolygonBuilder.ToP64(t.A, scale);
            var pb = TrianglePolygonBuilder.ToP64(t.B, scale);
            var pc = TrianglePolygonBuilder.ToP64(t.C, scale);
            // closed outline as an open path so zero-area (vertical) triangles still become strips
            if (rimTriangles != null && rimTriangles.Contains(i))
            {
                rimEdges.Add([pa, pb, pc, pa]);
            }
            else
            {
                edges.Add([pa, pb, pc, pa]);
                edgeBase.Add(b.Min.Y);
            }
        }
    }

    // XZ footprints of non-walkable triangles near the selection (walls, props, steep faces) as inflated strips, for cutting the floor polygon
    // selected triangles are floor by definition and never cut; meshes marked as floor or excluded never cut either
    public static Paths64 ObstacleFootprints(ZoneCollisionScene scene, FloorSelection floor, AutoMapSettings s, out int obstacleTriangles, IReadOnlySet<int>? excludedMeshes = null, IReadOnlySet<int>? floorMeshes = null, IReadOnlySet<int>? rimTriangles = null, Paths64? rimBand = null, CancellationToken ct = default)
    {
        obstacleTriangles = 0;
        if (floor.Count == 0)
        {
            return [];
        }
        // rim slopes are walkable inside the rim band (up to the wall) but stay obstacles outside it, so a slope that also
        // covers a leaked part of the fill keeps carving it there
        var rimEdges = new Paths64();
        var fb = floor.Bounds;
        var bandMin = fb.Min.Y - s.ObstacleHeightBelow;
        var bandMax = fb.Max.Y + s.ObstacleHeightAbove;
        // the height band is evaluated against the floor directly under each obstacle, not the global floor range:
        // a multi-level room (bridges, upper galleries) would otherwise project geometry from other levels onto the arena
        var scale = BoxFootprintOps.DefaultScale;
        var edges = new Paths64();
        List<float> edgeBase = []; // lowest point of each obstacle outline in edges, for the floor-above test
        var collector = new ObstacleCollector(scene, floor, s, rimTriangles, bandMin, bandMax, scale, edges, edgeBase, rimEdges);
        TriangleAdjacency.ForEachTriangleOfMeshes(scene, (int m, in Bounds3 wb) => (excludedMeshes == null || !excludedMeshes.Contains(m)) && (floorMeshes == null || !floorMeshes.Contains(m)) && TriangleAdjacency.OverlapsBox(wb, fb.Min.X, fb.Min.Z, fb.Max.X, fb.Max.Z, bandMin, bandMax), ref collector, ct);
        obstacleTriangles = collector.ObstacleTriangles;
        if (obstacleTriangles > s.ObstacleMaxTriangles)
        {
            return [];
        }
        // joined ends: the outline is offset as a closed polyline with no end caps, so two wall triangles meeting at a corner do not chamfer it
        Paths64 strips;
        if (edges.Count == 0)
        {
            strips = [];
        }
        else if (s.ObstacleUnderFloor && s.ObstacleLocalHeight)
        {
            strips = StripsMinusFloorAbove(scene, floor, edges, edgeBase, s, scale, ct);
        }
        else
        {
            strips = Clipper.InflatePaths(edges, s.ObstacleInflate * scale, JoinType.Miter, EndType.Joined);
        }
        if (rimEdges.Count > 0)
        {
            var rimStrips = Clipper.InflatePaths(rimEdges, s.ObstacleInflate * scale, JoinType.Miter, EndType.Joined);
            strips.AddRange(rimBand != null && rimBand.Count > 0 ? Clipper.Difference(rimStrips, rimBand, FillRule.NonZero) : rimStrips);
        }
        return strips;
    }

    // obstacles in 2-yalm height buckets by their base: each bucket's strips lose the area where selected floor runs clearly above that base
    // (a bridge over rocks, a cliff face beside a plank end), so the surface the player walks on stays in the projection; a wall that rises
    // through the floor above (its base is at that floor) keeps its full strip. Buckets go from the highest down, so the floor cover of a
    // bucket is the previous cover plus the floor that came within reach: one union grown incrementally
    private static Paths64 StripsMinusFloorAbove(ZoneCollisionScene scene, FloorSelection floor, Paths64 edges, List<float> edgeBase, AutoMapSettings s, long scale, CancellationToken ct)
    {
        var tris = scene.Triangles.Span;
        var floorPaths = new (Path64 path, float minY)[floor.Triangles.Length];
        for (var i = 0; i < floor.Triangles.Length; ++i)
        {
            ref readonly var t = ref tris[floor.Triangles[i]];
            floorPaths[i] = ([TrianglePolygonBuilder.ToP64(t.A, scale), TrianglePolygonBuilder.ToP64(t.B, scale), TrianglePolygonBuilder.ToP64(t.C, scale)], t.Bounds.Min.Y);
        }
        Array.Sort(floorPaths, (a, b) => b.minY.CompareTo(a.minY)); // highest first: the cover for a bucket is a prefix
        var order = new int[edges.Count];
        var bucketOf = new int[edges.Count];
        for (var i = 0; i < order.Length; ++i)
        {
            order[i] = i;
            bucketOf[i] = (int)MathF.Floor(edgeBase[i] / 2f);
        }
        Array.Sort(order, (a, b) => bucketOf[b].CompareTo(bucketOf[a])); // highest bucket first
        var result = new Paths64();
        var inflate = s.ObstacleInflate * scale;
        var lift = s.StepHeight + 1f; // floor this much above the obstacle's base is another level
        var covered = 0; // floorPaths prefix already in the cover union
        Paths64? cover = null;
        var pending = new Paths64();
        for (var k = 0; k < order.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var bucket = bucketOf[order[k]];
            var group = new Paths64();
            var baseY = float.MaxValue;
            for (; k < order.Length && bucketOf[order[k]] == bucket; ++k)
            {
                group.Add(edges[order[k]]);
                baseY = MathF.Min(baseY, edgeBase[order[k]]);
            }
            var strips = Clipper.InflatePaths(group, inflate, JoinType.Miter, EndType.Joined);
            var threshold = baseY + lift;
            pending.Clear();
            for (; covered < floorPaths.Length && floorPaths[covered].minY > threshold; ++covered)
            {
                pending.Add(floorPaths[covered].path);
            }
            if (pending.Count > 0)
            {
                if (cover != null)
                {
                    pending.AddRange(cover);
                }
                cover = Clipper.Union(pending, FillRule.NonZero);
            }
            if (cover != null)
            {
                strips = Clipper.Difference(strips, cover, FillRule.NonZero);
            }
            result.AddRange(strips);
        }
        return result;
    }

    // box colliders standing on the selected floor (props without a mesh): footprint cut like a mesh obstacle. Seals and floor boxes are handled
    // elsewhere; ignoredBoxes are the ones the author switched off on the canvas
    public static Paths64 ObstacleBoxFootprints(ZoneCollisionScene scene, FloorSelection floor, ReadOnlySpan<int> floorBoxes, List<SealCandidate> seals, AutoMapSettings s, out int obstacleBoxes, IReadOnlySet<int>? ignoredBoxes = null)
    {
        obstacleBoxes = 0;
        if (floor.Count == 0)
        {
            return [];
        }
        var fb = floor.Bounds;
        HashSet<int> skip = [];
        for (var i = 0; i < floorBoxes.Length; ++i)
        {
            skip.Add(floorBoxes[i]);
        }
        for (var i = 0; i < seals.Count; ++i)
        {
            skip.Add(seals[i].BoxIndex);
        }
        var result = new Paths64();
        for (var b = 0; b < scene.Boxes.Count; ++b)
        {
            var box = scene.Boxes[b];
            if (skip.Contains(b) || (ignoredBoxes != null && ignoredBoxes.Contains(b)) || !scene.IsBoxEnabled(b) || !s.BoxIsObstacle(box))
            {
                continue;
            }
            var wb = box.WorldBounds;
            if (wb.Max.X < fb.Min.X || wb.Min.X > fb.Max.X || wb.Max.Z < fb.Min.Z || wb.Min.Z > fb.Max.Z)
            {
                continue;
            }
            if (!floor.Overlaps(wb, s.ObstacleHeightBelow, s.ObstacleHeightAbove, out var floorTop) || wb.Max.Y <= floorTop + s.StepHeight)
            {
                continue; // no floor under it at this height, or it does not rise above the step height
            }
            var fp = BoxFootprintOps.Inflate(box.FootprintXZ, s.ObstacleInflate);
            if (fp.Count >= 3)
            {
                result.Add(fp);
                ++obstacleBoxes;
            }
        }
        return result;
    }

    private const float RimWallTouchEps = 0.25f; // XZ gap allowed between a rim slope and the wall it reaches

    // XZ parameter of p's projection onto the line a-b (unclamped; 0 for a degenerate segment)
    private static float ProjectT(in Vector3 p, in Vector3 a, in Vector3 b)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var len2 = dx * dx + dz * dz;
        return len2 > 1e-9f ? ((p.X - a.X) * dx + (p.Z - a.Z) * dz) / len2 : 0f;
    }

    // squared XZ distance from p to the segment a-b, with the clamped parameter of the nearest point
    private static (float d2, float t) PointSegmentSqXZ(in Vector3 p, in Vector3 a, in Vector3 b)
    {
        var t = Math.Clamp(ProjectT(p, a, b), 0f, 1f);
        var qx = a.X + t * (b.X - a.X) - p.X;
        var qz = a.Z + t * (b.Z - a.Z) - p.Z;
        return (qx * qx + qz * qz, t);
    }

    private static float DistXZToSegment(in Vector3 p, in Vector3 a, in Vector3 b) => MathF.Sqrt(PointSegmentSqXZ(p, a, b).d2);

    // closest approach of two segments in XZ as a squared distance with the parameters of the closest points: 0 at a proper crossing
    // (the crossing point on both), else the nearest of the four endpoint-to-segment projections
    public static (float d2, float ta, float tb) SegmentDistSqXZ(in Vector3 a0, in Vector3 a1, in Vector3 b0, in Vector3 b1)
    {
        var d1 = (a1.X - a0.X) * (b0.Z - a0.Z) - (a1.Z - a0.Z) * (b0.X - a0.X);
        var d2 = (a1.X - a0.X) * (b1.Z - a0.Z) - (a1.Z - a0.Z) * (b1.X - a0.X);
        var d3 = (b1.X - b0.X) * (a0.Z - b0.Z) - (b1.Z - b0.Z) * (a0.X - b0.X);
        var d4 = (b1.X - b0.X) * (a1.Z - b0.Z) - (b1.Z - b0.Z) * (a1.X - b0.X);
        if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)))
        {
            return (0f, d3 / (d3 - d4), d1 / (d1 - d2));
        }
        var best = PointSegmentSqXZ(a0, b0, b1);
        var r = (d2: best.d2, ta: 0f, tb: best.t);
        best = PointSegmentSqXZ(a1, b0, b1);
        if (best.d2 < r.d2)
        {
            r = (best.d2, 1f, best.t);
        }
        best = PointSegmentSqXZ(b0, a0, a1);
        if (best.d2 < r.d2)
        {
            r = (best.d2, best.t, 0f);
        }
        best = PointSegmentSqXZ(b1, a0, a1);
        if (best.d2 < r.d2)
        {
            r = (best.d2, best.t, 1f);
        }
        return r;
    }

    // some wall triangle's edge within eps of one of t's edges in XZ, Y ranges overlapping within yEps and the wall rising at least riseAbove over t's top
    private struct WallTouch(ZoneTriangleStore store, WorldTriangle t, float eps, float yEps, float riseAbove) : ITriangleVisitor
    {
        private readonly Bounds3 _b = t.Bounds;
        private readonly float _eps2 = eps * eps;
        public bool Found;

        public void Visit(int wi)
        {
            if (Found)
            {
                return;
            }
            ref readonly var w = ref store[wi];
            var wb = w.Bounds;
            if (!TriangleAdjacency.OverlapsBox(wb, _b.Min.X - eps, _b.Min.Z - eps, _b.Max.X + eps, _b.Max.Z + eps, _b.Min.Y - yEps, _b.Max.Y + yEps) || wb.Max.Y < _b.Max.Y + riseAbove)
            {
                return;
            }
            Found = EdgesTouch(t.A, t.B, w) || EdgesTouch(t.B, t.C, w) || EdgesTouch(t.C, t.A, w);
        }

        private readonly bool EdgesTouch(in Vector3 a, in Vector3 b, in WorldTriangle w)
            => SegmentDistSqXZ(a, b, w.A, w.B).d2 <= _eps2 || SegmentDistSqXZ(a, b, w.B, w.C).d2 <= _eps2 || SegmentDistSqXZ(a, b, w.C, w.A).d2 <= _eps2;
    }

    // true when some wall triangle's edge comes within eps of one of t's edges in XZ, their Y ranges overlap within yEps and the wall
    // triangle rises at least riseAbove over t's top: a wall the slope leads up to, not the face of a cliff the slope drops off
    private static bool TouchesWall(ZoneTriangleStore store, TriangleGrid walls, VisitStamp visited, in WorldTriangle t, float eps, float yEps, float riseAbove)
    {
        var b = t.Bounds;
        var touch = new WallTouch(store, t, eps, yEps, riseAbove);
        walls.ForEachInRect(b.Min.X - eps, b.Min.Z - eps, b.Max.X + eps, b.Max.Z + eps, visited, ref touch);
        return touch.Found;
    }

    // rim extension: steep-but-not-wall triangles adjoining the selection boundary, clipped to a band RimExtension wide around the boundary edges
    private readonly record struct WallSegment(Vector3 A, Vector3 B, int Triangle, float TopY);

    // wall foot segments: the lowest edge of steep unselected triangles near the selection (the top edge of a leaning wall would project inside the room)
    private struct WallFootCollector(ZoneCollisionScene scene, FloorSelection floor, float wallNormalY, Bounds3 fb, float r, List<WallSegment> walls) : ITriangleVisitor
    {
        public void Visit(int i)
        {
            ref readonly var t = ref scene.Triangles[i];
            if (floor.Contains(i) || t.NormalY >= wallNormalY)
            {
                return;
            }
            var b = t.Bounds;
            if (!TriangleAdjacency.OverlapsBox(b, fb.Min.X - r, fb.Min.Z - r, fb.Max.X + r, fb.Max.Z + r, fb.Min.Y - 1f, fb.Max.Y + 3f))
            {
                return;
            }
            var footY = b.Min.Y + 0.5f;
            AddWallSegment(walls, t.A, t.B, i, b.Max.Y, footY);
            AddWallSegment(walls, t.B, t.C, i, b.Max.Y, footY);
            AddWallSegment(walls, t.C, t.A, i, b.Max.Y, footY);
        }
    }

    // boundary edges of the selection within WallSnap of a wall foot are extended to that wall: the sliver between the edge and the foot line
    // (a steep lip, a hole in the mesh) is unioned in, and the matched foot segments are returned so the final vertices can be snapped onto
    // them exactly. Nothing is added beyond the wall line or sideways past the edge, so the extension cannot leak
    public static Paths64 WallSnapPaths(ZoneCollisionScene scene, FloorSelection floor, AutoMapSettings s, IReadOnlySet<int>? excludedMeshes, List<(Vector3 a, Vector3 b)> snapSegments, out int snappedEdges, CancellationToken ct = default)
    {
        snappedEdges = 0;
        var r = s.WallSnap;
        if (r <= 0f || floor.Count == 0)
        {
            return [];
        }
        var fb = floor.Bounds;
        var boundary = floor.BoundaryEdges;
        if (boundary.Count == 0)
        {
            return [];
        }

        var wallNormalY = MathF.Cos(s.RimMaxSlopeDeg * MathF.PI / 180f);
        List<WallSegment> walls = [];
        var collector = new WallFootCollector(scene, floor, wallNormalY, fb, r, walls);
        TriangleAdjacency.ForEachTriangleOfMeshes(scene, (int m, in Bounds3 wb) => (excludedMeshes == null || !excludedMeshes.Contains(m)) && TriangleAdjacency.OverlapsBox(wb, fb.Min.X - r, fb.Min.Z - r, fb.Max.X + r, fb.Max.Z + r, fb.Min.Y - 1f, fb.Max.Y + 3f), ref collector, ct);
        if (walls.Count == 0)
        {
            return [];
        }
        var wallGrid = new SegmentGrid(walls.Count, i => (walls[i].A, walls[i].B));

        // for every (edge, wall foot) pair within reach: the trapezoid between the edge and its projection on the foot line, clipped to the
        // capsule around the segment so nothing runs along the line past the wall's end; whatever lands beyond a wall line is cut off again
        // by that wall's obstacle strip and dropped as a disconnected piece
        var scale = BoxFootprintOps.DefaultScale;
        var fill = new Paths64();
        var capsules = new Paths64();
        HashSet<int> usedWalls = [];
        List<int> near = [];
        var r2 = r * r;
        for (var e = 0; e < boundary.Count; ++e)
        {
            if ((e & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            var (a, b) = boundary[e];
            var edgeMinY = MathF.Min(a.Y, b.Y);
            var edgeMaxY = MathF.Max(a.Y, b.Y);
            var hit = false;
            wallGrid.Near(a, b, r, near);
            foreach (var w in near)
            {
                var seg = walls[w];
                var segMinY = MathF.Min(seg.A.Y, seg.B.Y);
                var segMaxY = MathF.Max(seg.A.Y, seg.B.Y);
                if (segMaxY < edgeMinY - 0.5f || segMinY > edgeMaxY + 1f || seg.TopY < edgeMaxY + s.StepHeight || SegmentDistSqXZ(a, b, seg.A, seg.B).d2 > r2)
                {
                    continue;
                }
                var pa = ProjectOnSegment(a, seg.A, seg.B, float.MaxValue);
                var pb = ProjectOnSegment(b, seg.A, seg.B, float.MaxValue);
                hit = true;
                if (usedWalls.Add(w))
                {
                    snapSegments.Add((seg.A, seg.B));
                    capsules.Add([TrianglePolygonBuilder.ToP64(seg.A, scale), TrianglePolygonBuilder.ToP64(seg.B, scale)]);
                }
                var quad = new Path64 { TrianglePolygonBuilder.ToP64(a, scale), TrianglePolygonBuilder.ToP64(b, scale), TrianglePolygonBuilder.ToP64(pb, scale), TrianglePolygonBuilder.ToP64(pa, scale) };
                if (Math.Abs(Clipper.Area(quad)) < 1d)
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

    // XZ projection of p onto the segment a-b, clamped to the segment extended by 'ext' at both ends (float.MaxValue = the whole line)
    private static Vector2 ProjectOnSegment(in Vector3 p, in Vector3 a, in Vector3 b, float ext)
    {
        var dx = b.X - a.X;
        var dz = b.Z - a.Z;
        var len2 = dx * dx + dz * dz;
        if (len2 < 1e-9f)
        {
            return new(a.X, a.Z);
        }
        var t = ProjectT(p, a, b);
        if (ext != float.MaxValue)
        {
            var len = MathF.Sqrt(len2);
            t = Math.Clamp(t, -ext / len, 1f + ext / len);
        }
        return new(a.X + t * dx, a.Z + t * dz);
    }

    // final vertices within tolerance of a matched wall foot are moved onto the foot line (its corner when near an endpoint), so the arena
    // edge carries the wall's own vertices instead of the 'inflate' inset of the obstacle strip
    public static void SnapVerticesToWalls(List<PolygonWithHoles> polys, List<(Vector3 a, Vector3 b)> segments, float tol)
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
        var cornerTol2 = cornerTol * cornerTol;
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
                var da2 = (a.X - p.X) * (a.X - p.X) + (a.Z - p.Z) * (a.Z - p.Z);
                if (da2 <= cornerTol2)
                {
                    var da = MathF.Sqrt(da2);
                    if (da < best + tol)
                    {
                        best = MathF.Min(best, da);
                        target = new(a.X, p.Y, a.Z);
                        continue;
                    }
                }
                var db2 = (b.X - p.X) * (b.X - p.X) + (b.Z - p.Z) * (b.Z - p.Z);
                if (db2 <= cornerTol2)
                {
                    var db = MathF.Sqrt(db2);
                    if (db < best + tol)
                    {
                        best = MathF.Min(best, db);
                        target = new(b.X, p.Y, b.Z);
                        continue;
                    }
                }
                var (d2, t) = PointSegmentSqXZ(p, a, b);
                if (d2 < best * best)
                {
                    best = MathF.Sqrt(d2);
                    target = new(a.X + t * (b.X - a.X), p.Y, a.Z + t * (b.Z - a.Z));
                }
            }
            pts[i] = target;
        }
    }

    // 2-yalm XZ grid over segments (wall feet, boundary edges) for the edge-matching passes
    private sealed class SegmentGrid
    {
        private const float CellSize = 2f;
        private readonly XZHashGrid _cells = new(CellSize);
        private readonly VisitStamp _seen;

        public SegmentGrid(int count, Func<int, (Vector3 a, Vector3 b)> segment)
        {
            _seen = new(count);
            for (var i = 0; i < count; ++i)
            {
                var (a, b) = segment(i);
                _cells.Add(i, MathF.Min(a.X, b.X), MathF.Min(a.Z, b.Z), MathF.Max(a.X, b.X), MathF.Max(a.Z, b.Z));
            }
        }

        private struct Collect(VisitStamp seen, List<int> dst) : IRingVisitor
        {
            public readonly double BestDistSq => 0d;

            public readonly void Visit(List<int> ids)
            {
                foreach (var i in ids)
                {
                    if (seen.Visit(i))
                    {
                        dst.Add(i);
                    }
                }
            }
        }

        // the segments whose cells overlap the rect around a-b grown by r, each once
        public void Near(in Vector3 a, in Vector3 b, float r, List<int> dst)
        {
            dst.Clear();
            _seen.Next();
            var c = new Collect(_seen, dst);
            _cells.ForEachInRect(MathF.Min(a.X, b.X) - r, MathF.Min(a.Z, b.Z) - r, MathF.Max(a.X, b.X) + r, MathF.Max(a.Z, b.Z) + r, ref c);
        }
    }

    // unselected, non-wall triangles near the selection (rim candidates) and the steep ones (the walls a rim chain must reach)
    private struct RimPoolCollector(ZoneCollisionScene scene, FloorSelection floor, AutoMapSettings s, float minNormalY, Bounds3 fb, float r, List<int> pool, List<int> walls) : ITriangleVisitor
    {
        public void Visit(int i)
        {
            ref readonly var t = ref scene.Triangles[i];
            if (floor.Contains(i))
            {
                return;
            }
            var b = t.Bounds;
            if (b.Max.X < fb.Min.X - r || b.Min.X > fb.Max.X + r || b.Max.Z < fb.Min.Z - r || b.Min.Z > fb.Max.Z + r)
            {
                return;
            }
            if (t.NormalY < minNormalY)
            {
                if (s.RimRequireWall && b.Max.Y >= fb.Min.Y - 2f && b.Min.Y <= fb.Max.Y + 4f)
                {
                    walls.Add(i);
                }
                return;
            }
            pool.Add(i);
        }
    }

    public static Paths64 RimExtensionPaths(ZoneCollisionScene scene, FloorSelection floor, AutoMapSettings s, IReadOnlySet<int>? excludedMeshes, HashSet<int> rimTriangles, out Paths64 band, CancellationToken ct = default)
    {
        band = [];
        rimTriangles.Clear();
        if (s.RimExtension <= 0f || floor.Count == 0)
        {
            return [];
        }
        var tris = scene.Triangles.Span;
        var eps = floor.WeldEps;
        var fb = floor.Bounds;
        // boundary edges: shared by exactly one selected triangle; keys map an edge to the height of its floor edge (the chain's source floor)
        var boundary = floor.BoundaryEdges;
        var boundaryKeys = floor.BoundaryKeys;
        if (boundary.Count == 0)
        {
            return [];
        }

        // candidate rim triangles: unselected, not walls, near the selection's bounds in XZ and Y
        var r = s.RimExtension;
        var minNormalY = MathF.Cos(s.RimMaxSlopeDeg * MathF.PI / 180f);
        List<int> pool = [];
        List<int> walls = []; // unselected triangles steeper than the rim slope limit (the walls a rim chain must reach)
        var collector = new RimPoolCollector(scene, floor, s, minNormalY, fb, r, pool, walls);
        TriangleAdjacency.ForEachTriangleOfMeshes(scene, (int m, in Bounds3 wb) => (excludedMeshes == null || !excludedMeshes.Contains(m)) && TriangleAdjacency.OverlapsBox(wb, fb.Min.X - r, fb.Min.Z - r, fb.Max.X + r, fb.Max.Z + r, fb.Min.Y - 2f, fb.Max.Y + 4f), ref collector, ct);
        if (pool.Count == 0)
        {
            return [];
        }

        // hop outward from the boundary edges through edge-connected pool triangles, keeping those that reach into the band
        Dictionary<ulong, List<int>> poolEdges = new(pool.Count * 3);
        for (var p = 0; p < pool.Count; ++p)
        {
            ref readonly var t = ref tris[pool[p]];
            var ka = TriangleAdjacency.WeldKey(t.A, eps);
            var kb = TriangleAdjacency.WeldKey(t.B, eps);
            var kc = TriangleAdjacency.WeldKey(t.C, eps);
            foreach (var k in (ReadOnlySpan<ulong>)[TriangleAdjacency.EdgeKey(ka, kb), TriangleAdjacency.EdgeKey(kb, kc), TriangleAdjacency.EdgeKey(kc, ka)])
            {
                if (!poolEdges.TryGetValue(k, out var list))
                {
                    poolEdges[k] = list = [];
                }
                list.Add(pool[p]);
            }
        }
        // the pool triangles sharing an edge with tri, into a reused list
        List<int> neighbours = [];
        void EdgeNeighbours(int tri)
        {
            neighbours.Clear();
            ref readonly var t = ref scene.Triangles[tri];
            var ka = TriangleAdjacency.WeldKey(t.A, eps);
            var kb = TriangleAdjacency.WeldKey(t.B, eps);
            var kc = TriangleAdjacency.WeldKey(t.C, eps);
            foreach (var k in (ReadOnlySpan<ulong>)[TriangleAdjacency.EdgeKey(ka, kb), TriangleAdjacency.EdgeKey(kb, kc), TriangleAdjacency.EdgeKey(kc, ka)])
            {
                if (poolEdges.TryGetValue(k, out var list))
                {
                    neighbours.AddRange(list);
                }
            }
        }
        // within r of some boundary edge: the edges near the triangle's bounds are the only ones that can be
        var boundaryGrid = new SegmentGrid(boundary.Count, i => boundary[i]);
        List<int> nearEdges = [];
        var r2 = r * r;
        bool InBand(in WorldTriangle t)
        {
            var b = t.Bounds;
            boundaryGrid.Near(b.Min, b.Max, r, nearEdges);
            var c = t.Centroid;
            foreach (var e in nearEdges)
            {
                var (a, bb) = boundary[e];
                if (PointSegmentSqXZ(t.A, a, bb).d2 <= r2 || PointSegmentSqXZ(t.B, a, bb).d2 <= r2 || PointSegmentSqXZ(t.C, a, bb).d2 <= r2 || PointSegmentSqXZ(c, a, bb).d2 <= r2)
                {
                    return true;
                }
            }
            return false;
        }
        Queue<(int tri, int hop)> queue = new();
        Dictionary<int, float> rootY = []; // rim triangle -> height of the floor edge its chain started from
        foreach (var (k, floorY) in boundaryKeys)
        {
            if (poolEdges.TryGetValue(k, out var list))
            {
                foreach (var tri in list)
                {
                    if (rimTriangles.Add(tri))
                    {
                        rootY[tri] = floorY;
                        queue.Enqueue((tri, 1));
                    }
                }
            }
        }
        var steps = 0;
        while (queue.Count > 0)
        {
            if ((++steps & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            var (tri, hop) = queue.Dequeue();
            if (!InBand(tris[tri]))
            {
                rimTriangles.Remove(tri);
                continue;
            }
            if (hop >= s.RimHops)
            {
                continue;
            }
            EdgeNeighbours(tri);
            foreach (var next in neighbours)
            {
                if (rimTriangles.Add(next))
                {
                    rootY[next] = rootY[tri];
                    queue.Enqueue((next, hop + 1));
                }
            }
        }
        if (s.RimRequireWall && rimTriangles.Count > 0)
        {
            // keep only the rim triangles whose chain (through other rim triangles) touches a wall; walls and slopes are
            // usually separate meshes that are not vertex-welded to each other, so touching is geometric (XZ distance + Y overlap)
            var wallGrid = new TriangleGrid(tris, CollectionsMarshal.AsSpan(walls));
            var wallVisited = new VisitStamp(scene.Triangles.Count);
            HashSet<int> reached = [];
            Queue<int> back = new();
            foreach (var tri in rimTriangles)
            {
                ref readonly var t = ref tris[tri];
                // a slope that has dropped more than the step height below its source floor edge leads down to the wall: the player
                // cannot reach that wall from the floor, so the chain is not accepted and the edge stays on the floor vertices
                if (t.Bounds.Min.Y < rootY[tri] - s.StepHeight)
                {
                    continue;
                }
                if (TouchesWall(scene.Triangles, wallGrid, wallVisited, t, RimWallTouchEps, 0.5f, s.StepHeight))
                {
                    reached.Add(tri);
                    back.Enqueue(tri);
                }
            }
            ct.ThrowIfCancellationRequested();
            while (back.Count > 0)
            {
                var tri = back.Dequeue();
                EdgeNeighbours(tri);
                foreach (var next in neighbours)
                {
                    if (rimTriangles.Contains(next) && reached.Add(next))
                    {
                        back.Enqueue(next);
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
            edgePaths.Add([TrianglePolygonBuilder.ToP64(a, scale), TrianglePolygonBuilder.ToP64(b, scale)]);
        }
        band = Clipper.InflatePaths(edgePaths, r * scale, JoinType.Round, EndType.Round);
        var rimPaths = new Paths64(rimTriangles.Count);
        foreach (var tri in rimTriangles)
        {
            ref readonly var t = ref tris[tri];
            Path64 path = [TrianglePolygonBuilder.ToP64(t.A, scale), TrianglePolygonBuilder.ToP64(t.B, scale), TrianglePolygonBuilder.ToP64(t.C, scale)];
            if (!Clipper.IsPositive(path))
            {
                path.Reverse();
            }
            rimPaths.Add(path);
        }
        return Clipper.Intersect(rimPaths, band, FillRule.NonZero);
    }

    // union of the given triangles and floor boxes with seal boxes and obstacle footprints cut out, keeping the polygon containing the anchor when configured
    public static List<PolygonWithHoles> BuildPolygons(ZoneCollisionScene scene, ReadOnlySpan<int> triangles, ReadOnlySpan<int> floorBoxes, List<SealCandidate> seals, IReadOnlySet<int> activeSeals,
        List<Path64> extraUnion, List<Path64> extraCut, List<Vector3> extraYSource, Vector2? keepAnchor, AutoMapSettings s, out string keepStatus, out long unionMs, out int obstacleTriangles, out int obstacleBoxes, out int wallSnapEdges, HashSet<int> rim, IReadOnlySet<int>? excludedMeshes = null, IReadOnlySet<int>? floorMeshes = null, IReadOnlySet<int>? ignoredBoxes = null, List<Vector2>? keepAny = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var builder = new TrianglePolygonBuilder(s.SnapEpsXZ, BoxFootprintOps.DefaultScale);
        builder.AddTriangles(scene.Triangles.Span, triangles);
        var polys = builder.Build(s.MinArea);
        ct.ThrowIfCancellationRequested();
        var floor = new FloorSelection(scene.Triangles, triangles, s.WeldEps);
        if (s.SeamClose > 0f)
        {
            polys = BoxFootprintOps.Close(polys, s.SeamClose, s.MinArea);
        }
        List<Path64> cut = [.. extraCut];
        Paths64 union = [.. extraUnion];
        List<Vector3> ySource = [.. extraYSource];
        var rimPaths = RimExtensionPaths(scene, floor, s, excludedMeshes, rim, out var rimBand, ct);
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
        var snapFill = WallSnapPaths(scene, floor, s, excludedMeshes, snapSegments, out wallSnapEdges, ct);
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
            var fp = box.FootprintXZ;
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
            var footprints = ObstacleFootprints(scene, floor, s, out obstacleTriangles, excludedMeshes, floorMeshes, rim, rimBand, ct);
            if (obstacleTriangles <= s.ObstacleMaxTriangles)
            {
                cut.AddRange(footprints);
            }
        }
        if (s.CutBoxes)
        {
            cut.AddRange(ObstacleBoxFootprints(scene, floor, floorBoxes, seals, s, out obstacleBoxes, ignoredBoxes));
        }
        for (var i = 0; i < seals.Count; ++i)
        {
            if (activeSeals.Contains(seals[i].BoxIndex) && seals[i].FootprintXZ.Count >= 3)
            {
                cut.Add(seals[i].FootprintXZ);
                ySource.AddRange(scene.Boxes[seals[i].BoxIndex].Corners);
            }
        }
        ct.ThrowIfCancellationRequested();
        keepStatus = "";
        if (union.Count > 0)
        {
            // the floor union snaps vertices to the SnapEpsXZ grid while the extension paths carry raw vertices: grow them by the grid step so
            // they overlap the floor edge instead of leaving a hairline gap that would drop them as disconnected slivers
            union = Clipper.InflatePaths(union, MathF.Max(s.SnapEpsXZ, 0.01f) * BoxFootprintOps.DefaultScale, JoinType.Miter, EndType.Polygon);
        }
        // the keep-containing-centre step applies whenever an anchor is known, cuts or not (the raw union can already be several pieces)
        if (cut.Count > 0 || union.Count > 0 || keepAnchor != null)
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
        if (keepAny != null && keepAny.Count > 0 && polys.Count > 0)
        {
            // path mode: every piece the player stood in stays (a bridge severed from the ground by a cut is still walked), the rest goes
            var scale = BoxFootprintOps.DefaultScale;
            var pts = new Point64[keepAny.Count];
            for (var i = 0; i < pts.Length; ++i)
            {
                pts[i] = TrianglePolygonBuilder.ToP64(keepAny[i], scale);
            }
            var before = polys.Count;
            List<PolygonWithHoles> kept = [];
            foreach (var poly in polys)
            {
                var outer = BoxFootprintOps.ToPath64(poly.Outer, false);
                var holes = new Paths64(poly.Holes.Count);
                foreach (var h in poly.Holes)
                {
                    holes.Add(BoxFootprintOps.ToPath64(h, false));
                }
                foreach (var pt in pts)
                {
                    if (BoxFootprintOps.Contains(outer, pt) && !holes.Exists(h => Clipper.PointInPolygon(pt, h) == PointInPolygonResult.IsInside))
                    {
                        kept.Add(poly);
                        break;
                    }
                }
            }
            polys = kept;
            keepStatus = $"kept {polys.Count} polygon(s) the path runs through, dropped {before - polys.Count}";
        }
        unionMs = sw.ElapsedMilliseconds;
        return polys;
    }
}

// everything the adjacency cache depends on
public readonly record struct AdjacencyKey(Vector3 Centre, string Floors, int Mode, float Slope, float Radius, float Weld, float Step, int Adjacency, float Touch, long Activity,
    float EdgeSnap, bool Blacklist, bool Relief, float ReliefStep, float Gap = 0f, float GapRise = 0f, long Path = 0, float TouchHeight = 0f);

// one auto-map run: a snapshot of the session inputs taken on the UI thread, computed on a worker, published back with ApplyAutoMap
public sealed class AutoMapJob
{
    public required AutoMapSettings Settings;
    public required Vector3 Centre;
    public required List<SealCandidate> Seals;
    public required HashSet<int> ActiveSeals;
    public required HashSet<int> FloorMeshes;
    public required AdjacencyKey Key;
    public TriangleAdjacency? Adjacency; // reused from the session when its key still matches, else built by Run
    public AutoMapResult Result = new();
    public PathRegion? Region;           // path mode: the corridor around the walked samples replaces the ring around the centre
    public List<int>? ForcedTriangles;
    public List<(int a, int b)>? ForcedLinks;
}

// a walked path as a candidate region: any point within the corridor of some sample
public sealed class PathRegion
{
    public readonly List<Vector3> Samples;
    public readonly float Corridor;
    public readonly Vector2 Min, Max;
    private readonly XZHashGrid _cells;

    public PathRegion(List<Vector3> samples, float corridor)
    {
        Samples = samples;
        Corridor = MathF.Max(corridor, 1f);
        _cells = new(Corridor);
        var bounds = samples.Count > 0 ? Bounds3.FromPoints(CollectionsMarshal.AsSpan(samples)) : new(new(float.MaxValue), new(float.MinValue));
        Min = new(bounds.Min.X, bounds.Min.Z);
        Max = new(bounds.Max.X, bounds.Max.Z);
        for (var i = 0; i < samples.Count; ++i)
        {
            _cells.AddPoint(i, samples[i].X, samples[i].Z);
        }
    }

    public bool IntersectsBounds(in Bounds3 b) => Samples.Count > 0 && b.Max.X >= Min.X - Corridor && b.Min.X <= Max.X + Corridor && b.Max.Z >= Min.Y - Corridor && b.Min.Z <= Max.Y + Corridor;

    // within the corridor of a sample in XZ and within 6 y of it in height (the level the player was on)
    public bool Contains(in Vector3 p)
    {
        var cx = _cells.CellOf(p.X);
        var cz = _cells.CellOf(p.Z);
        var r2 = Corridor * Corridor;
        for (var dz = -1; dz <= 1; ++dz)
        {
            for (var dx = -1; dx <= 1; ++dx)
            {
                if (_cells.Cell(cx + dx, cz + dz) is not { } list)
                {
                    continue;
                }
                foreach (var i in list)
                {
                    var s = Samples[i];
                    var ddx = s.X - p.X;
                    var ddz = s.Z - p.Z;
                    if (ddx * ddx + ddz * ddz <= r2 && MathF.Abs(s.Y - p.Y) <= 6f)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    // the samples thinned to a spacing, as XZ points (keep-any anchors)
    public List<Vector2> Thinned(float spacing)
    {
        List<Vector2> r = [];
        foreach (var s in Samples)
        {
            var p = new Vector2(s.X, s.Z);
            if (r.Count == 0 || (r[^1] - p).Length() >= spacing)
            {
                r.Add(p);
            }
        }
        return r;
    }
}

// the mutable editing session: settings, centre, seals/pairs, adjacency cache, selected triangles/boxes and the resulting polygons,
// plus the scene state (layers / nodes / event object states) the scene is resolved under
public sealed class ArenaMapSession
{
    public readonly ZoneCollisionScene Scene;
    public readonly ZoneSceneModel Model;
    public readonly AutoMapSettings Settings = new();
    public readonly ZoneSceneState State = new();
    public readonly List<ZoneEObjRule> EObjRules = [];
    public readonly List<ZoneScene> Scenes = [];
    public int ActiveSceneIndex = -1;
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
    public readonly List<PairOverride> PairOverrides = []; // the author's pairing decisions, by node path id (survive reloads and scene changes)
    // path mode: the floor is what the party walked over (a dungeon segment), not a ring around the centre; seals do not block the fill, the corridor bounds it
    public PathRegion? Path;
    public List<int> ForcedTriangles = [];
    public List<(int a, int b)> ForcedLinks = [];
    public AutoMapResult Last = new();
    public readonly HashSet<int> LastAutoResult = [];
    public List<PolygonWithHoles> Polygons = [];
    public string KeepStatus = "";
    public int ObstacleTriangles;
    public int ObstacleBoxes;
    public int WallSnapEdges;
    public readonly HashSet<int> RimTriangles = []; // triangles the rim extension used in the last recompute
    private AdjacencyKey _adjacencyKey;

    public ArenaMapSession(ZoneCollisionScene scene, IZoneSheetSource? sheets = null)
    {
        Scene = scene;
        Model = ZoneSceneModel.Build(scene, sheets ?? NullZoneSheetSource.Instance, Settings);
        Resolve();
    }

    public ZoneScene? ActiveScene => ActiveSceneIndex >= 0 && ActiveSceneIndex < Scenes.Count ? Scenes[ActiveSceneIndex] : null;

    public void SetPath(List<Vector3> samples, float corridor, List<int> forcedTriangles, List<(int a, int b)> forcedLinks)
    {
        Path = new(samples, corridor);
        ForcedTriangles = forcedTriangles;
        ForcedLinks = forcedLinks;
        if (samples.Count > 0)
        {
            SetCentre(samples[0]);
        }
    }

    public void ClearPath()
    {
        Path = null;
        ForcedTriangles = [];
        ForcedLinks = [];
    }

    // the triangles under the path samples: the nearest candidate in height (within 2 y) at each sample's XZ
    private static List<int> PathSeeds(ZoneCollisionScene scene, TriangleAdjacency adj, PathRegion region, CancellationToken ct)
    {
        List<int> seeds = [];
        var grid = adj.CandidateGrid;
        if (grid == null)
        {
            return seeds;
        }
        HashSet<int> seen = [];
        Vector3? last = null;
        var n = 0;
        foreach (var s in region.Samples)
        {
            if ((++n & 4095) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (last is { } l && (l - s).Length() < 1f)
            {
                continue;
            }
            last = s;
            var best = ArenaAutoMapper.TriangleUnder(scene, grid, s, 2f);
            if (best >= 0 && seen.Add(best))
            {
                seeds.Add(best);
            }
        }
        return seeds;
    }

    // re-resolve the scene under the current state; every consumer of IsMeshEnabled/IsBoxEnabled sees the new arrays at once
    public void Resolve()
    {
        ZoneSceneResolver.Resolve(Scene, State, Model, EObjRules);
        Adjacency = null;
    }

    public void EnsureResolved()
    {
        if (Scene.Activity.Dirty)
        {
            Resolve();
        }
    }

    public void SetLayerEnabled(int layerIndex, bool enabled)
    {
        var id = Scene.Layers[layerIndex].PathId;
        if (enabled)
        {
            State.DisabledLayers.Remove(id);
        }
        else
        {
            State.DisabledLayers.Add(id);
        }
        ActiveScene?.State.CopyFrom(State);
        Resolve();
    }

    public void SetNodeOverride(int nodeIndex, bool? active)
    {
        var id = Scene.Nodes[nodeIndex].PathId;
        if (active is { } a)
        {
            State.NodeOverrides[id] = a;
        }
        else
        {
            State.NodeOverrides.Remove(id);
        }
        ActiveScene?.State.CopyFrom(State);
        Resolve();
    }

    // null clears the state (back to the layout default 0)
    public void SetEObjState(int eobjIndex, ushort? state, bool objectChannel = false)
    {
        var id = Model.EventObjects[eobjIndex].PathId;
        var dict = objectChannel ? State.EObjObjectStates : State.EObjStates;
        if (state is { } s)
        {
            dict[id] = s;
        }
        else
        {
            dict.Remove(id);
        }
        ActiveScene?.State.CopyFrom(State);
        Resolve();
    }

    public ushort? EObjState(int eobjIndex, bool objectChannel = false)
    {
        var dict = objectChannel ? State.EObjObjectStates : State.EObjStates;
        return dict.TryGetValue(Model.EventObjects[eobjIndex].PathId, out var s) ? s : null;
    }

    public void ActivateScene(int index, bool redetectSeals = true)
    {
        if (index < 0 || index >= Scenes.Count)
        {
            return;
        }
        ActiveSceneIndex = index;
        State.CopyFrom(Scenes[index].State);
        Resolve();
        if (redetectSeals)
        {
            DetectSeals();
        }
    }

    // the current state with one event object forced to a state: 0 = collision on (sealed / closed), 7 = removed (open)
    public ZoneScene DeriveScene(int eobjIndex, ushort state, string name)
    {
        var scene = new ZoneScene { Name = name, State = State.Clone(), Source = ZoneSceneSource.Derived };
        scene.State.EObjStates[Model.EventObjects[eobjIndex].PathId] = state;
        return scene;
    }

    private readonly SealClassCache _sealClasses = new();

    // seals are geometry markers: every placed seal is listed, but only the ones with collision under the current state are cut by default
    public void DetectSeals()
    {
        EnsureResolved();
        Seals = ArenaAutoMapper.FindSeals(Scene, Settings, _sealClasses);
        ActiveSeals.Clear();
        for (var i = 0; i < Seals.Count; ++i)
        {
            if (Scene.IsBoxEnabled(Seals[i].BoxIndex))
            {
                ActiveSeals.Add(Seals[i].BoxIndex);
            }
        }
        Pairs = ArenaAutoMapper.AutoPairRooms(Seals, Scene, Model, Settings, PairOverrides);
        PairIndex = -1;
    }

    public ulong SealPathId(int seal) => ArenaAutoMapper.SealPathId(Scene, Seals[seal]);

    // the pair a seal sits in, -1 when it is single
    public int PairOf(int seal) => Pairs.FindIndex(p => (p.SealA == seal || p.SealB == seal) && !p.IsSingle);

    public void SetPairOverride(int seal, ulong partnerPathId, bool partnerIsNode)
    {
        var id = SealPathId(seal);
        PairOverrides.RemoveAll(o => o.SealPathId == id);
        PairOverrides.Add(new(id, partnerPathId, partnerIsNode));
    }

    public void ClearPairOverride(int seal)
    {
        var id = SealPathId(seal);
        PairOverrides.RemoveAll(o => o.SealPathId == id);
    }

    // what a seal could pair with: other seals within twice the pair distance, nodes within it
    public List<(string label, ulong pathId, bool isNode, float distance)> PartnerCandidates(int seal)
    {
        List<(string, ulong, bool, float)> r = [];
        var c = Seals[seal].Center;
        for (var j = 0; j < Seals.Count; ++j)
        {
            if (j == seal)
            {
                continue;
            }
            var d = (Seals[j].Center - c).Length();
            if (d <= Settings.SealPairMaxDistance * 2f)
            {
                r.Add(($"seal {j}", SealPathId(j), false, d));
            }
        }
        foreach (var (m, pos, kind) in ArenaAutoMapper.PairNodes(Scene, Model))
        {
            var d = (pos - c).Length();
            if (d <= Settings.SealPairMaxDistance * 1.5f)
            {
                var marker = Scene.Markers[m];
                var eo = marker.Type == (int)LgbInstanceType.EventObject ? Model.EventObjectByKey.GetValueOrDefault(marker.InstanceKey, -1) : -1;
                r.Add(($"{kind} {(eo >= 0 ? Model.EventObjects[eo].Label : $"key 0x{marker.InstanceKey:X}")}", ArenaAutoMapper.MarkerPathId(Scene, m), true, d));
            }
        }
        r.Sort((a, b) => a.Item4.CompareTo(b.Item4));
        return r;
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
                // rooms first (both seals of a boss arena), then seal + node, then loose seal pairs; lowest indices = first room in layout order
                score = p.SealA * 1000f + (p.SealB >= 0 ? p.SealB : 500 + p.MarkerIndex) + (p.IsRoom ? 0f : p.SealB >= 0 ? 2e5f : 1e5f);
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
            LastEstimate = new(preferNear ?? Vector3.Zero, -1, -1, -1, 0f, true, "no seals", true);
            Centre = LastEstimate.Centre;
            CentreValid = false;
            return;
        }
        LastEstimate = ArenaAutoMapper.EstimateCentre(Pairs[PairIndex], Seals, Scene.Markers, Settings, preferNear);
        Centre = LastEstimate.Centre;
        CentreValid = !LastEstimate.NoSeals;
    }

    public void SetCentre(Vector3 c)
    {
        Centre = c;
        CentreValid = true;
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

    private AdjacencyKey CurrentAdjacencyKey()
    {
        EnsureResolved();
        var floorKey = FloorKey();
        foreach (var m in FloorMeshes)
        {
            floorKey += $"m{m};";
        }
        return new(Centre, floorKey, (int)Settings.FloorMatchMode, Settings.MaxSlopeDeg, Settings.MaxRadius, Settings.WeldEps, Settings.StepHeight, (int)Settings.Adjacency, Settings.BoxFloorTouchEps, Scene.Activity.Fingerprint ^ ((long)Scene.Meshes.Count << 48),
            Settings.EdgeSnap, Settings.ExcludeUnwalkableMaterials, Settings.ReliefPromotion, Settings.ReliefStep, Settings.GapBridge, Settings.GapBridgeRise, PathKey(), Settings.BoxFloorTouchHeight);
    }

    private long PathKey()
    {
        if (Path == null)
        {
            return 0;
        }
        var h = ZoneBinary.Fnv1aOffset;
        h = ZoneBinary.Fnv1a64(h, (ulong)Path.Samples.Count);
        h = ZoneBinary.Fnv1a64(h, (ulong)BitConverter.SingleToInt32Bits(Path.Corridor));
        h = ZoneBinary.Fnv1a64(h, (ulong)ForcedTriangles.Count);
        h = ZoneBinary.Fnv1a64(h, (ulong)ForcedLinks.Count);
        foreach (var s in Path.Samples)
        {
            h = ZoneBinary.Fnv1a64(h, (ulong)(uint)BitConverter.SingleToInt32Bits(s.X) << 32 | (uint)BitConverter.SingleToInt32Bits(s.Z));
        }
        return (long)h;
    }

    // the adjacency for the current inputs (the same ones the key describes: path corridor and walked links included)
    public TriangleAdjacency EnsureAdjacency()
    {
        var key = CurrentAdjacencyKey();
        if (Adjacency == null || key != _adjacencyKey)
        {
            Adjacency = TriangleAdjacency.Build(Scene, Centre, Settings, FloorMeshes, Path, Path != null ? ForcedTriangles : null, Path != null ? ForcedLinks : null);
            _adjacencyKey = key;
        }
        return Adjacency;
    }

    // synchronous auto-map (project load); the editor button runs the same three steps with Run on a worker
    public void AutoMap()
    {
        var job = PrepareAutoMap();
        RunAutoMap(job);
        ApplyAutoMap(job);
    }

    // UI thread: snapshot the inputs
    public AutoMapJob PrepareAutoMap()
    {
        var key = CurrentAdjacencyKey();
        return new()
        {
            Settings = Settings.Clone(),
            Centre = Centre,
            Seals = Seals,
            ActiveSeals = Path != null ? [] : [.. ActiveSeals],
            FloorMeshes = [.. FloorMeshes],
            Key = key,
            Adjacency = Adjacency != null && key == _adjacencyKey ? Adjacency : null,
            Region = Path,
            ForcedTriangles = Path != null ? [.. ForcedTriangles] : null,
            ForcedLinks = Path != null ? [.. ForcedLinks] : null,
        };
    }

    // any thread: the scene is read-only while a job runs (layer toggles and terrain loads change the key, so the result is then discarded)
    public void RunAutoMap(AutoMapJob job, CancellationToken ct = default)
    {
        job.Adjacency ??= TriangleAdjacency.Build(Scene, job.Centre, job.Settings, job.FloorMeshes, job.Region, job.ForcedTriangles, job.ForcedLinks, ct);
        if (job.Region != null)
        {
            var seeds = PathSeeds(Scene, job.Adjacency, job.Region, ct);
            job.Result = ArenaAutoMapper.FloodFill(Scene, job.Adjacency, CollectionsMarshal.AsSpan(seeds), job.Seals, job.ActiveSeals, job.Centre, job.Settings, ct);
            return;
        }
        var seed = ArenaAutoMapper.FindSeed(Scene, job.Adjacency, job.Centre, job.Settings);
        job.Result = ArenaAutoMapper.FloodFill(Scene, job.Adjacency, seed, job.Seals, job.ActiveSeals, job.Centre, job.Settings, ct);
    }

    // UI thread: publish; false when the inputs changed since Prepare (the caller runs again)
    public bool ApplyAutoMap(AutoMapJob job)
    {
        if (job.Key != CurrentAdjacencyKey() || !ReferenceEquals(job.Seals, Seals) || job.Region == null && !job.ActiveSeals.SetEquals(ActiveSeals))
        {
            return false;
        }
        Adjacency = job.Adjacency;
        _adjacencyKey = job.Key;
        Last = job.Result;
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
        return true;
    }

    // takes every node reached: triangles into dst, floor boxes into the session's selection
    private struct GrowVisitor(TriangleAdjacency adj, HashSet<int> dst, HashSet<int> boxes) : IWalkVisitor
    {
        public bool Enter(int node, int depth)
        {
            if (adj.IsBoxNode(node))
            {
                boxes.Add(adj.FloorBoxes[node - adj.Candidates.Length]);
            }
            else
            {
                dst.Add(adj.Candidates[node]);
            }
            return true;
        }
    }

    public void GrowFrom(int tri, int depth, HashSet<int> dst)
    {
        var adj = EnsureAdjacency();
        var start = adj.LocalOf(tri);
        dst.Add(tri);
        if (start < 0)
        {
            return;
        }
        var visitor = new GrowVisitor(adj, dst, SelectedFloorBoxes);
        adj.Walk([start], depth, ref visitor);
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
                if (tris[i].NormalY >= minNormalY && !Settings.IsBlocked(tris[i]) && (!floorOnly || forced || Settings.FloorMatches(tris[i])))
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

    public List<PolygonWithHoles> Recompute(List<Path64> extraUnion, List<Path64> extraCut, List<Vector3> extraYSource)
    {
        var snapshot = new int[Selected.Count];
        Selected.CopyTo(snapshot);
        var boxes = new int[SelectedFloorBoxes.Count];
        SelectedFloorBoxes.CopyTo(boxes);
        var keepAny = Path?.Thinned(2f);
        Polygons = ArenaAutoMapper.BuildPolygons(Scene, snapshot, boxes, Seals, ActiveSeals, extraUnion, extraCut, extraYSource, Settings.KeepPolygonContainingCentre && Path == null ? new Vector2(Centre.X, Centre.Z) : null, Settings, out KeepStatus, out _, out ObstacleTriangles, out ObstacleBoxes, out WallSnapEdges, RimTriangles, null, FloorMeshes, IgnoredBoxes, keepAny);
        return Polygons;
    }
}
