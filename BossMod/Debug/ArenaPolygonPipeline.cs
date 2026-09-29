using Clipper2Lib;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Globalization;

namespace BossMod;

// polygons -> module-quality arena: simplification (stage B), per-contour enable flags and stats, snippet generation and pushing the
// result into a live boss module; shared by the live collision tab and the offline zone editor (main thread only, no game pointers)
public sealed class ArenaPolygonPipeline
{
    public readonly record struct SimplifySettings(float Epsilon, float Offset, int Decimals, bool TrimCollinear, float DropAreaBelow, float Closing, float Opening);

    public sealed class PreviewContour
    {
        public Vector3[] Raw = [];
        public WPos[] Simplified = [];
        public Vector3[] SimplifiedDraw = [];
        public bool Enabled = true;
        public float Area, MinY, MaxY, MeanY;
        public WPos BBMin, BBMax, Centroid;
    }

    public sealed class PreviewPolygon
    {
        public PreviewContour Outer = new();
        public List<PreviewContour> Holes = [];
    }

    public const long ClipperScale = TrianglePolygonBuilder.ClipperScale;

    // inputs
    public List<PolygonWithHoles> Raw = [];
    public Vector2 AnchorXZ; // the arena centre: the piece an opening cuts loose that contains it is the one to keep
    public string KeepStatus = "";
    // simplify knobs
    public float Epsilon = 0.02f;
    public float Offset;
    public int Decimals = 3;
    public bool TrimCollinear = true;
    public float DropAreaBelow;
    public float Closing;   // morphological closing radius: fills notches/gaps narrower than 2r (inflate then deflate)
    public float Opening = 0.25f; // morphological opening radius: removes spikes/slivers thinner than 2r (deflate then inflate); 0.25 = the player hitbox radius, passages it cannot enter become a straight wall
    public readonly List<WPos> DeletedVertices = []; // hand-deleted result vertices (matched by rounded XZ); their neighbours connect directly
    // codegen knobs
    public string ArenaFieldName = "arena";
    public bool AdjustForHitboxInwards = true; // emit AdjustForHitboxInwards: true so the framework shrinks the bounds by the hitbox radius
    public bool AdjustForHitboxOutwards; // emit AdjustForHitboxOutwards: true (grow by the hitbox radius instead)
    public bool AutoLayered = true; // the module snippet takes the layered form by itself when the enabled contours sit at more than one floor height
    // the ArenaBoundsCustom the snippet would produce, built on demand for the world/canvas preview so the framework's own post-processing
    // (hitbox offsets, polygon simplification) can be looked at before pasting
    private ArenaBoundsCustom? _previewBounds;
    private (int version, bool inwards, bool outwards, bool projection, ulong enabled) _previewBoundsKey;
    private (int version, ulong enabled, float gap, int count) _floorHeightsKey = (-1, 0UL, 0f, 0);
    private int _previewVersion;
    public string PreviewBoundsStatus = "";
    public float FlatThreshold = 0.5f;
    public bool EmitProjectionHeightZero;
    public float LayerGap = 1f;
    // outputs
    public readonly List<PreviewPolygon> Preview = [];
    public SimplifySettings LastSimplify;
    public int RawVertexCount, SimpVertexCount;
    public long SimplifyMs;
    public string Status = "";
    public PreviewContour? HoverContour;
    public bool SnippetDirty = true;
    private string _snippetCache = "";

    private readonly List<WPos[]> _genOuters = [];
    private readonly List<WPos[]> _genHoles = [];
    private readonly List<PreviewContour> _genOuterContours = [];

    public SimplifySettings CurrentSimplify => new(Epsilon, Offset, Decimals, TrimCollinear, DropAreaBelow, Closing, Opening);

    public void Reset()
    {
        Raw = [];
        Preview.Clear();
        DeletedVertices.Clear();
        KeepStatus = "";
        HoverContour = null;
        Status = "";
        SnippetDirty = true;
    }

    public void RebuildSimplified() => RebuildSimplified(CurrentSimplify);

    public void RebuildSimplified(in SimplifySettings s)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LastSimplify = s;
        var previousCount = Preview.Count;
        List<bool> previousEnabled = [];
        for (var i = 0; i < previousCount; ++i)
        {
            previousEnabled.Add(Preview[i].Outer.Enabled);
            var holes = Preview[i].Holes;
            for (var j = 0; j < holes.Count; ++j)
            {
                previousEnabled.Add(holes[j].Enabled);
            }
        }
        Preview.Clear();
        HoverContour = null;
        RawVertexCount = 0;
        SimpVertexCount = 0;

        var rawCount = Raw.Count;
        for (var i = 0; i < rawCount; ++i)
        {
            var raw = Raw[i];
            RawVertexCount += raw.Outer.Count;
            var holeCount = raw.Holes.Count;
            for (var h = 0; h < holeCount; ++h)
            {
                RawVertexCount += raw.Holes[h].Count;
            }

            // source vertices used to recover Y for simplified/offset points
            List<Vector3> yPoints = [.. raw.Outer];
            for (var h = 0; h < holeCount; ++h)
            {
                yPoints.AddRange(raw.Holes[h]);
            }
            var ySource = new YSampler(yPoints);

            var paths = new Paths64(1 + holeCount) { BoxFootprintOps.ToPath64(raw.Outer, false, ClipperScale) };
            for (var h = 0; h < holeCount; ++h)
            {
                paths.Add(BoxFootprintOps.ToPath64(raw.Holes[h], true, ClipperScale));
            }

            if (s.Offset != 0f || s.Closing > 0f || s.Opening > 0f)
            {
                var shaped = paths;
                // miter joins keep corners sharp, so closing/opening only change the parts thinner than the diameter
                if (s.Closing > 0f)
                {
                    shaped = BoxFootprintOps.ClosePaths(shaped, s.Closing, ClipperScale);
                }
                if (s.Opening > 0f)
                {
                    shaped = Clipper.InflatePaths(Clipper.InflatePaths(shaped, -s.Opening * ClipperScale, JoinType.Miter, EndType.Polygon), s.Opening * ClipperScale, JoinType.Miter, EndType.Polygon);
                }
                if (s.Offset != 0f)
                {
                    shaped = Clipper.InflatePaths(shaped, s.Offset * ClipperScale, JoinType.Miter, EndType.Polygon);
                }
                var tree = new PolyTree64();
                Clipper.BooleanOp(ClipType.Union, shaped, null, tree, FillRule.NonZero);
                var before = Preview.Count;
                var collector = new PreviewCollector(this, ySource, raw, s);
                BoxFootprintOps.WalkTree(tree, ref collector);
                if (s.Opening > 0f && Preview.Count - before > 1)
                {
                    // the opening cut pieces off that were only joined through a passage narrower than the hitbox: unreachable, keep the
                    // piece around the arena centre when it is known, else the largest
                    var best = -1;
                    if (AnchorXZ != default)
                    {
                        var anchor = TrianglePolygonBuilder.ToP64(AnchorXZ, ClipperScale);
                        for (var k = before; k < Preview.Count && best < 0; ++k)
                        {
                            if (BoxFootprintOps.Contains(ToPath64(Preview[k].Outer.Simplified), anchor))
                            {
                                best = k;
                            }
                        }
                    }
                    if (best < 0)
                    {
                        best = before;
                        for (var k = before + 1; k < Preview.Count; ++k)
                        {
                            if (Preview[k].Outer.Area > Preview[best].Outer.Area)
                            {
                                best = k;
                            }
                        }
                    }
                    for (var k = Preview.Count - 1; k >= before; --k)
                    {
                        if (k != best)
                        {
                            Preview.RemoveAt(k);
                        }
                    }
                }
            }
            else
            {
                var poly = new PreviewPolygon();
                if (!FinishContour(poly.Outer, paths[0], ySource, raw.Outer, s))
                {
                    continue;
                }
                for (var h = 0; h < holeCount; ++h)
                {
                    var hole = new PreviewContour();
                    if (FinishContour(hole, paths[1 + h], ySource, raw.Holes[h], s))
                    {
                        poly.Holes.Add(hole);
                    }
                }
                Preview.Add(poly);
            }
        }

        // keep enable flags when the layout did not change
        var newCount = 0;
        for (var i = 0; i < Preview.Count; ++i)
        {
            newCount += 1 + Preview[i].Holes.Count;
        }
        if (newCount == previousEnabled.Count)
        {
            var k = 0;
            for (var i = 0; i < Preview.Count; ++i)
            {
                Preview[i].Outer.Enabled = previousEnabled[k++];
                var holes = Preview[i].Holes;
                for (var j = 0; j < holes.Count; ++j)
                {
                    holes[j].Enabled = previousEnabled[k++];
                }
            }
        }
        SimplifyMs = sw.ElapsedMilliseconds;
        SnippetDirty = true;
        ++_previewVersion;
    }

    public void InvalidatePreviewBounds()
    {
        _previewBounds = null;
        ++_previewVersion;
    }

    private static Path64 ToPath64(WPos[] pts)
    {
        var path = new Path64(pts.Length);
        for (var i = 0; i < pts.Length; ++i)
        {
            path.Add(TrianglePolygonBuilder.ToP64(new Vector2(pts[i].X, pts[i].Z), ClipperScale));
        }
        return path;
    }

    // FNV-1a over the enable flags of every contour, in table order (the editor writes the flags directly, so no counter can track them)
    private ulong EnabledHash()
    {
        var h = 14695981039346656037UL;
        for (var i = 0; i < Preview.Count; ++i)
        {
            h = (h ^ (Preview[i].Outer.Enabled ? 1UL : 2UL)) * 1099511628211UL;
            var holes = Preview[i].Holes;
            for (var j = 0; j < holes.Count; ++j)
            {
                h = (h ^ (holes[j].Enabled ? 1UL : 2UL)) * 1099511628211UL;
            }
        }
        return h;
    }

    // cached bounds for the preview; rebuilt when the simplified contours, the enabled flags or the codegen options changed
    public ArenaBoundsCustom? PreviewBounds()
    {
        var key = (_previewVersion, AdjustForHitboxInwards, AdjustForHitboxOutwards, EmitProjectionHeightZero, EnabledHash());
        if (_previewBounds == null || key != _previewBoundsKey)
        {
            _previewBoundsKey = key;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _previewBounds = BuildArenaBounds();
            if (_previewBounds != null)
            {
                var parts = _previewBounds.Shape.Parts;
                var verts = 0;
                for (var i = 0; i < parts.Count; ++i)
                {
                    verts += parts[i].Vertices.Count;
                }
                PreviewBoundsStatus = $"bounds: {parts.Count} part(s), {verts} verts, centre ({_previewBounds.Center.X:f2}, {_previewBounds.Center.Z:f2}), r {_previewBounds.Radius:f1}, built in {sw.ElapsedMilliseconds} ms{(AdjustForHitboxInwards ? ", hitbox inwards" : "")}{(AdjustForHitboxOutwards ? ", hitbox outwards" : "")}";
            }
            else
            {
                PreviewBoundsStatus = "bounds: no enabled polygons";
            }
        }
        return _previewBounds;
    }

    // deleted vertices match within half a unit of the last decimal on each axis, so they survive a change of the rounding
    private void RemoveDeletedVertices(List<WPos> pts, int decimals)
    {
        var deleted = DeletedVertices;
        if (deleted.Count == 0)
        {
            return;
        }
        var half = 0.5f * MathF.Pow(10f, -decimals);
        var tol2 = 2f * half * half + 1e-9f;
        for (var i = pts.Count - 1; i >= 0 && pts.Count > 3; --i)
        {
            for (var d = 0; d < deleted.Count; ++d)
            {
                if ((pts[i] - deleted[d]).LengthSq() < tol2)
                {
                    pts.RemoveAt(i);
                    break;
                }
            }
        }
    }

    // after rounding, a hairline slit in the union becomes an exact out-and-back spike (A, B, A) or a 180 degree turn that the
    // pre-rounding TrimCollinear could not see; those spikes would become real 1-yalm notches once the framework offsets the
    // bounds inwards for the hitbox, so drop them (and, when trimming, exactly collinear points) until nothing changes
    private static void RemoveSpikes(List<WPos> pts, bool trimCollinear)
    {
        var changed = true;
        while (changed && pts.Count >= 3)
        {
            changed = false;
            for (var i = 0; i < pts.Count && pts.Count >= 3; ++i)
            {
                var prev = pts[(i + pts.Count - 1) % pts.Count];
                var cur = pts[i];
                var next = pts[(i + 1) % pts.Count];
                var a = cur - prev;
                var b = next - cur;
                var cross = a.X * b.Z - a.Z * b.X;
                var dot = a.X * b.X + a.Z * b.Z;
                var spike = prev == next || (MathF.Abs(cross) < 1e-4f && dot < 0f);
                var collinear = trimCollinear && MathF.Abs(cross) < 1e-4f && dot > 0f;
                if (spike || collinear || cur == prev)
                {
                    pts.RemoveAt(i);
                    --i;
                    changed = true;
                }
            }
        }
    }

    // the shaped tree as preview polygons; every outer carries the raw outer's vertices, a hole its raw hole's: by index when the shaping kept
    // the hole count, else the raw hole with the nearest centroid, none when the raw polygon had no holes
    private readonly struct PreviewCollector : ITreeVisitor
    {
        private readonly ArenaPolygonPipeline _owner;
        private readonly YSampler _ySource;
        private readonly PolygonWithHoles _raw;
        private readonly SimplifySettings _s;
        private readonly Vector2[] _rawHoleCentroids;
        private readonly List<(PreviewPolygon poly, bool byIndex)> _open = []; // outers still taking holes, innermost last

        public PreviewCollector(ArenaPolygonPipeline owner, YSampler ySource, PolygonWithHoles raw, in SimplifySettings s)
        {
            _owner = owner;
            _ySource = ySource;
            _raw = raw;
            _s = s;
            _rawHoleCentroids = new Vector2[raw.Holes.Count];
            for (var h = 0; h < raw.Holes.Count; ++h)
            {
                var c = Vector2.Zero;
                var pts = raw.Holes[h];
                for (var i = 0; i < pts.Count; ++i)
                {
                    c += new Vector2(pts[i].X, pts[i].Z);
                }
                _rawHoleCentroids[h] = pts.Count > 0 ? c / pts.Count : c;
            }
        }

        public bool Outer(Path64 path, int holeCount)
        {
            var poly = new PreviewPolygon();
            if (!_owner.FinishContour(poly.Outer, path, _ySource, _raw.Outer, _s))
            {
                return false;
            }
            _open.Add((poly, holeCount == _raw.Holes.Count));
            return true;
        }

        public void Hole(Path64 path, int index)
        {
            var (poly, byIndex) = _open[^1];
            var hole = new PreviewContour();
            if (_owner.FinishContour(hole, path, _ySource, RawHole(path, index, byIndex), _s))
            {
                poly.Holes.Add(hole);
            }
        }

        public void EndOuter()
        {
            _owner.Preview.Add(_open[^1].poly);
            _open.RemoveAt(_open.Count - 1);
        }

        private List<Vector3> RawHole(Path64 path, int index, bool byIndex)
        {
            if (_rawHoleCentroids.Length == 0)
            {
                return [];
            }
            if (byIndex)
            {
                return _raw.Holes[index];
            }
            var c = Vector2.Zero;
            for (var i = 0; i < path.Count; ++i)
            {
                c += new Vector2((float)(path[i].X / (double)ClipperScale), (float)(path[i].Y / (double)ClipperScale));
            }
            c /= path.Count;
            var best = 0;
            var bestD = float.MaxValue;
            for (var h = 0; h < _rawHoleCentroids.Length; ++h)
            {
                var d = (_rawHoleCentroids[h] - c).LengthSquared();
                if (d < bestD)
                {
                    bestD = d;
                    best = h;
                }
            }
            return _raw.Holes[best];
        }
    }

    // simplify, round and convert one clipper contour; returns false when it degenerates
    private bool FinishContour(PreviewContour dst, Path64 path, YSampler ySource, List<Vector3> rawContour, in SimplifySettings s)
    {
        if (s.Epsilon > 0f)
        {
            path = Clipper.SimplifyPath(path, s.Epsilon * ClipperScale, true);
        }
        if (s.TrimCollinear)
        {
            path = Clipper.TrimCollinear(path);
        }
        if (path.Count < 3)
        {
            return false;
        }

        List<WPos> pts = new(path.Count);
        var n = path.Count;
        for (var i = 0; i < n; ++i)
        {
            var p = path[i];
            var w = new WPos(MathF.Round((float)(p.X / (double)ClipperScale), s.Decimals), MathF.Round((float)(p.Y / (double)ClipperScale), s.Decimals));
            if (pts.Count > 0 && pts[^1] == w)
            {
                continue;
            }
            pts.Add(w);
        }
        if (pts.Count > 1 && pts[0] == pts[^1])
        {
            pts.RemoveAt(pts.Count - 1);
        }
        RemoveDeletedVertices(pts, s.Decimals);
        RemoveSpikes(pts, s.TrimCollinear);
        if (pts.Count < 3)
        {
            return false;
        }

        // stats
        var area = 0f;
        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;
        var cx = 0f;
        var cz = 0f;
        var count = pts.Count;
        for (var i = 0; i < count; ++i)
        {
            var a = pts[i];
            var b = pts[(i + 1) % count];
            area += a.X * b.Z - b.X * a.Z;
            minX = MathF.Min(minX, a.X);
            minZ = MathF.Min(minZ, a.Z);
            maxX = MathF.Max(maxX, a.X);
            maxZ = MathF.Max(maxZ, a.Z);
            cx += a.X;
            cz += a.Z;
        }
        if (area < 0f)
        {
            pts.Reverse(); // emit every contour counter-clockwise, like the raw exporter
        }
        area = MathF.Abs(area) * 0.5f;
        if (s.DropAreaBelow > 0f && area < s.DropAreaBelow)
        {
            return false;
        }

        var minY = float.MaxValue;
        var maxY = float.MinValue;
        var sumY = 0f;
        var rawN = rawContour.Count;
        for (var i = 0; i < rawN; ++i)
        {
            var y = rawContour[i].Y;
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
            sumY += y;
        }

        dst.Raw = [.. rawContour];
        dst.Simplified = [.. pts];
        dst.SimplifiedDraw = new Vector3[count];
        for (var i = 0; i < count; ++i)
        {
            dst.SimplifiedDraw[i] = new(pts[i].X, ySource.Sample(pts[i].X, pts[i].Z), pts[i].Z);
        }
        dst.Area = area;
        dst.MinY = rawN > 0 ? minY : 0f;
        dst.MaxY = rawN > 0 ? maxY : 0f;
        dst.MeanY = rawN > 0 ? sumY / rawN : 0f;
        dst.BBMin = new(minX, minZ);
        dst.BBMax = new(maxX, maxZ);
        dst.Centroid = new(cx / count, cz / count);
        SimpVertexCount += count;
        return true;
    }

    // the polygons whose outer is enabled, and the enabled holes of one of them
    private IEnumerable<PreviewPolygon> EnabledPolygons()
    {
        for (var i = 0; i < Preview.Count; ++i)
        {
            if (Preview[i].Outer.Enabled)
            {
                yield return Preview[i];
            }
        }
    }

    private static IEnumerable<PreviewContour> EnabledHoles(PreviewPolygon poly)
    {
        for (var h = 0; h < poly.Holes.Count; ++h)
        {
            if (poly.Holes[h].Enabled)
            {
                yield return poly.Holes[h];
            }
        }
    }

    public int EnabledContourCounts(out int holes)
    {
        var outers = 0;
        holes = 0;
        foreach (var poly in EnabledPolygons())
        {
            ++outers;
            foreach (var _ in EnabledHoles(poly))
            {
                ++holes;
            }
        }
        return outers;
    }

    private void CollectEnabled(List<WPos[]> outers, List<WPos[]> holes, List<PreviewContour> outerContours)
    {
        outers.Clear();
        holes.Clear();
        outerContours.Clear();
        foreach (var poly in EnabledPolygons())
        {
            outers.Add(poly.Outer.Simplified);
            outerContours.Add(poly.Outer);
            foreach (var hole in EnabledHoles(poly))
            {
                holes.Add(hole.Simplified);
            }
        }
    }

    private (bool flat, float meanY, float minY, float maxY) YStats(List<PreviewContour> contours)
    {
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        var sum = 0f;
        var n = contours.Count;
        for (var i = 0; i < n; ++i)
        {
            minY = MathF.Min(minY, contours[i].MinY);
            maxY = MathF.Max(maxY, contours[i].MaxY);
            sum += contours[i].MeanY;
        }
        if (n == 0)
        {
            return (false, 0f, 0f, 0f);
        }
        return (maxY - minY <= FlatThreshold, sum / n, minY, maxY);
    }

    private ArenaBoundsCustom? BuildArenaBounds(List<WPos[]> outers, List<WPos[]> holes, bool flat, float meanY)
    {
        if (outers.Count == 0)
        {
            return null;
        }
        var union = new Shape[outers.Count];
        for (var i = 0; i < union.Length; ++i)
        {
            union[i] = new PolygonCustom(outers[i]);
        }
        Shape[]? difference = null;
        if (holes.Count > 0)
        {
            difference = new Shape[holes.Count];
            for (var i = 0; i < difference.Length; ++i)
            {
                difference[i] = new PolygonCustom(holes[i]);
            }
        }
        try
        {
            var bounds = new ArenaBoundsCustom(union, difference, AdjustForHitboxInwards: AdjustForHitboxInwards, AdjustForHitboxOutwards: AdjustForHitboxOutwards);
            if (flat)
            {
                var floor = MathF.Round(meanY, 1);
                bounds.Y = floor + 0.1f;
                bounds.BorderY = floor;
                if (EmitProjectionHeightZero)
                {
                    bounds.WorldProjectionHeight = 0f;
                }
            }
            return bounds;
        }
        catch (Exception ex)
        {
            Status = $"arena build failed: {ex.Message}";
            return null;
        }
    }

    // builds the ArenaBoundsCustom from the enabled contours (Y set when the floor is flat)
    public ArenaBoundsCustom? BuildArenaBounds()
    {
        CollectEnabled(_genOuters, _genHoles, _genOuterContours);
        var (flat, meanY, _, _) = YStats(_genOuterContours);
        return BuildArenaBounds(_genOuters, _genHoles, flat, meanY);
    }

    // the enabled contours as plain vertex lists (saved with a project so rules of another project can emit this arena)
    public List<SavedPolygon> ExportEnabledPolygons(out bool flat, out float meanY)
    {
        List<SavedPolygon> result = [];
        foreach (var poly in EnabledPolygons())
        {
            var sp = new SavedPolygon { Outer = Flatten(poly.Outer.Simplified), MeanY = poly.Outer.MeanY, MinY = poly.Outer.MinY, MaxY = poly.Outer.MaxY };
            foreach (var hole in EnabledHoles(poly))
            {
                sp.Holes.Add(Flatten(hole.Simplified));
            }
            result.Add(sp);
        }
        CollectEnabled(_genOuters, _genHoles, _genOuterContours);
        (flat, meanY, _, _) = YStats(_genOuterContours);
        return result;

        static float[] Flatten(WPos[] pts)
        {
            var r = new float[pts.Length * 2];
            for (var i = 0; i < pts.Length; ++i)
            {
                r[2 * i] = pts[i].X;
                r[2 * i + 1] = pts[i].Z;
            }
            return r;
        }
    }

    // the number of floor heights among the enabled contours, cached per preview version / enable flags / layer gap (read every frame)
    public int FloorHeightCount
    {
        get
        {
            var enabled = EnabledHash();
            if (_floorHeightsKey.version != _previewVersion || _floorHeightsKey.enabled != enabled || _floorHeightsKey.gap != LayerGap)
            {
                _floorHeightsKey = (_previewVersion, enabled, LayerGap, CountYClusters(out _));
            }
            return _floorHeightsKey.count;
        }
    }

    public string BuildModuleSnippet(string provenanceHeader, bool midFightSwap)
    {
        CollectEnabled(_genOuters, _genHoles, _genOuterContours);
        if (!midFightSwap && AutoLayered && ClusterCollected(out var clusters) >= 2)
        {
            return LayeredSnippetCollected(provenanceHeader, clusters);
        }
        if (_genOuters.Count == 0)
        {
            return "// no enabled polygons";
        }
        var (flat, meanY, minY, maxY) = YStats(_genOuterContours);
        var bounds = BuildArenaBounds(_genOuters, _genHoles, flat, meanY);
        var name = ArenaFieldName.Length > 0 ? ArenaFieldName : "arena";
        var sb = new StringBuilder();
        sb.Append(provenanceHeader);
        CollisionArenaCodeGen.AppendVertexFields(sb, _genOuters, _genHoles, LastSimplify.Decimals);
        sb.Append(CollisionArenaCodeGen.ArenaDeclaration(name, _genOuters.Count, _genHoles.Count, flat, meanY, minY, maxY, EmitProjectionHeightZero, AdjustForHitboxInwards, AdjustForHitboxOutwards));
        if (midFightSwap)
        {
            sb.AppendLine($"Arena.Bounds = {name};");
            sb.AppendLine($"Arena.Center = {name}.Center;");
        }
        else
        {
            sb.Append($"// ctor: base(ws, primary, {name}.Center, {name})");
            if (bounds != null)
            {
                sb.Append($"   center = ({CollisionArenaCodeGen.F(bounds.Center.X, 3)}, {CollisionArenaCodeGen.F(bounds.Center.Z, 3)})");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private int CountYClusters(out List<List<int>> clusters)
    {
        CollectEnabled(_genOuters, _genHoles, _genOuterContours);
        return ClusterCollected(out clusters);
    }

    // the collected outer contours grouped by mean height (gaps above LayerGap start a new layer), lowest first
    private int ClusterCollected(out List<List<int>> clusters)
    {
        clusters = [];
        var n = _genOuterContours.Count;
        var order = new int[n];
        for (var i = 0; i < n; ++i)
        {
            order[i] = i;
        }
        Array.Sort(order, (a, b) => _genOuterContours[a].MeanY.CompareTo(_genOuterContours[b].MeanY));
        for (var i = 0; i < n; ++i)
        {
            var idx = order[i];
            if (clusters.Count == 0 || _genOuterContours[idx].MeanY - _genOuterContours[clusters[^1][^1]].MeanY > LayerGap)
            {
                clusters.Add([idx]);
            }
            else
            {
                clusters[^1].Add(idx);
            }
        }
        return clusters.Count;
    }

    public string BuildLayeredSnippet(string provenanceHeader)
    {
        if (CountYClusters(out var clusters) < 2)
        {
            return "// fewer than two floor heights - use the module snippet";
        }
        return LayeredSnippetCollected(provenanceHeader, clusters);
    }

    // the layered snippet over the collected contours and their clusters
    private string LayeredSnippetCollected(string provenanceHeader, List<List<int>> clusters)
    {
        var clusterCount = clusters.Count;
        var bounds = BuildArenaBounds(_genOuters, _genHoles, false, 0f);
        var sb = new StringBuilder();
        sb.Append(provenanceHeader);
        var decimals = LastSimplify.Decimals;
        for (var i = 0; i < _genOuters.Count; ++i)
        {
            CollisionArenaCodeGen.AppendVertexField(sb, $"vertices{i}", _genOuters[i], decimals, $" // floor Y {CollisionArenaCodeGen.F(_genOuterContours[i].MeanY, 1)}");
        }
        for (var i = 0; i < _genHoles.Count; ++i)
        {
            CollisionArenaCodeGen.AppendVertexField(sb, $"hole{i}", _genHoles[i], decimals, "");
        }
        sb.AppendLine("private static (WPos center, ArenaBoundsCustom arena) BuildArena()");
        sb.AppendLine("{");
        var center = bounds?.Center ?? default;
        sb.AppendLine($"    var center = new WPos({CollisionArenaCodeGen.F(center.X, 3)}, {CollisionArenaCodeGen.F(center.Z, 3)}); // AABB center of the union, computed by the exporter");
        for (var i = 0; i < _genOuters.Count; ++i)
        {
            sb.AppendLine($"    var shape{i} = new PolygonCustom(vertices{i});");
        }
        for (var i = 0; i < _genHoles.Count; ++i)
        {
            sb.AppendLine($"    var holeShape{i} = new PolygonCustom(hole{i});");
        }
        sb.Append("    var arena = new ArenaBoundsCustom([");
        for (var i = 0; i < _genOuters.Count; ++i)
        {
            sb.Append(i > 0 ? ", " : "").Append($"shape{i}");
        }
        sb.Append(']');
        if (_genHoles.Count > 0)
        {
            sb.Append(", [");
            for (var i = 0; i < _genHoles.Count; ++i)
            {
                sb.Append(i > 0 ? ", " : "").Append($"holeShape{i}");
            }
            sb.Append(']');
        }
        sb.Append(", WorldProjectionLayers: [");
        for (var c = 0; c < clusterCount; ++c)
        {
            var members = clusters[c];
            var y = 0f;
            for (var m = 0; m < members.Count; ++m)
            {
                y += _genOuterContours[members[m]].MeanY;
            }
            y /= members.Count;
            var floor = MathF.Round(y, 1);
            sb.Append(c > 0 ? ", " : "");
            if (members.Count == 1)
            {
                sb.Append($"new(new RelSimplifiedComplexPolygon(shape{members[0]}.Contour(center)), {CollisionArenaCodeGen.F(floor + 0.1f, 1)}, borderY: {CollisionArenaCodeGen.F(floor, 1)})");
            }
            else
            {
                sb.Append("new(new RelSimplifiedComplexPolygon([");
                for (var m = 0; m < members.Count; ++m)
                {
                    sb.Append(m > 0 ? ", " : "").Append($"new RelPolygonWithHoles(shape{members[m]}.Contour(center))");
                }
                sb.Append($"]), {CollisionArenaCodeGen.F(floor + 0.1f, 1)}, borderY: {CollisionArenaCodeGen.F(floor, 1)})");
            }
        }
        sb.AppendLine("]);");
        if (_genHoles.Count > 0)
        {
            sb.AppendLine("    // holes: attach them to their layer polygon with RelPolygonWithHoles.AddHole(holeShapeN.Contour(center)) if a layer needs them");
        }
        sb.AppendLine("    return (arena.Center, arena);");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public string BuildRawSnippet(CollisionOutlinesExtractor.ClipboardVectorFormat fmt)
    {
        List<PolygonWithHoles> polys = [];
        foreach (var poly in EnabledPolygons())
        {
            var p = new PolygonWithHoles { Outer = [.. poly.Outer.SimplifiedDraw], Holes = [] };
            foreach (var hole in EnabledHoles(poly))
            {
                p.Holes.Add([.. hole.SimplifiedDraw]);
            }
            polys.Add(p);
        }
        return CollisionOutlinesExtractor.FormatForClipboard(polys, fmt, LastSimplify.Decimals);
    }

    // --- UI pieces ---

    public void DrawSimplifyControls()
    {
        ImGui.SetNextItemWidth(140f);
        ImGui.SliderFloat("Simplify epsilon (yalm)", ref Epsilon, 0f, 0.5f, "%.3f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Douglas-Peucker tolerance: vertices closer than this to the line between their neighbours are dropped");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        ImGui.InputFloat("Offset (yalm)", ref Offset, 0.1f, 0.5f, "%.2f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Negative shrinks the arena (e.g. -0.5 for the player hitbox radius when the edge is deadly)");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        ImGui.SliderInt("Decimals", ref Decimals, 2, 5);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Decimal places of the emitted coordinates");
        }
        ImGui.SameLine();
        ImGui.Checkbox("Trim collinear", ref TrimCollinear);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Drop vertices that lie exactly on the line between their neighbours (and 180-degree spikes)");
        }
        ImGui.SetNextItemWidth(140f);
        ImGui.SliderFloat("Drop area below (yalm^2)", ref DropAreaBelow, 0f, 50f, "%.1f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Contours (outers and holes) smaller than this are left out of the snippet");
        }
        ImGui.SetNextItemWidth(140f);
        ImGui.SliderFloat("Close notches (yalm)", ref Closing, 0f, 5f, "%.2f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Fills concave notches and gaps narrower than twice this radius (e.g. wall props the player never touches)");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        ImGui.SliderFloat("Remove spikes (yalm)", ref Opening, 0f, 5f, "%.2f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Morphological opening: removes convex spikes, slivers and passages thinner than twice this radius, leaving a straight wall across their mouth. 0.25 = the player hitbox radius (nothing narrower than the hitbox is reachable)");
        }
    }

    public void DrawCodegenControls()
    {
        ImGui.SetNextItemWidth(120f);
        SnippetDirty |= ImGui.InputText("Arena field name", ref ArenaFieldName, 32);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Name of the static ArenaBoundsCustom field in the snippet");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        SnippetDirty |= ImGui.InputFloat("Flat-floor Y spread", ref FlatThreshold, 0.1f, 0.5f, "%.2f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("When the recovered floor heights spread less than this the snippet sets Y/BorderY to the floor; otherwise it leaves Y unset (boss height) and suggests the layered snippet");
        }
        ImGui.SameLine();
        SnippetDirty |= ImGui.Checkbox("Emit WorldProjectionHeight = 0f", ref EmitProjectionHeightZero);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Add WorldProjectionHeight = 0f to the bounds (flat 3D arena projection instead of the default wall height)");
        }
        ImGui.SameLine();
        // the two hitbox offsets are exclusive: switching one on switches the other off
        if (ImGui.Checkbox("AdjustForHitboxInwards", ref AdjustForHitboxInwards))
        {
            SnippetDirty = true;
            if (AdjustForHitboxInwards)
            {
                AdjustForHitboxOutwards = false;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Emit AdjustForHitboxInwards: true - the framework offsets the bounds inwards by the player hitbox radius (0.5y), the equivalent of walking the edge with the hitbox circle; keep Offset at 0 when this is on. Exclusive with Outwards");
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Outwards", ref AdjustForHitboxOutwards))
        {
            SnippetDirty = true;
            if (AdjustForHitboxOutwards)
            {
                AdjustForHitboxInwards = false;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Emit AdjustForHitboxOutwards: true - the framework grows the bounds by the player hitbox radius instead. Exclusive with Inwards. The 'ArenaBoundsCustom' preview shows the result of either flag");
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        ImGui.InputFloat("Layer Y gap", ref LayerGap, 0.5f, 1f, "%.1f");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Layered snippet: contours whose mean heights differ by more than this become separate ArenaProjectionLayers");
        }
    }

    public void DrawCodegenButtons(Func<string> provenanceHeader)
    {
        var outers = EnabledContourCounts(out _);
        if (outers > 0)
        {
            var heights = FloorHeightCount;
            ImGui.Checkbox("auto layered", ref AutoLayered);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("When the enabled contours sit at more than one floor height (see 'Layer Y gap'), the module snippet is the layered form (one ArenaProjectionLayer per height) without asking");
            }
            ImGui.SameLine();
            ImGui.TextDisabled(heights >= 2 ? $"{heights} floor heights: the module snippet is {(AutoLayered ? "the layered form" : "flat (auto layered off)")}" : "one floor height");
        }
        using var disabled = ImRaii.Disabled(outers == 0);
        if (ImGui.Button("Copy module snippet"))
        {
            ImGui.SetClipboardText(BuildModuleSnippet(provenanceHeader(), false));
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Copy the vertex arrays and the static ArenaBoundsCustom declaration for a module (paste at class scope, ctor uses arena.Center, arena)");
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy mid-fight swap snippet"))
        {
            ImGui.SetClipboardText(BuildModuleSnippet(provenanceHeader(), true));
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Same polygon as Arena.Bounds = ...; Arena.Center = ...; lines for an arena change during the fight");
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy layered snippet"))
        {
            ImGui.SetClipboardText(BuildLayeredSnippet(provenanceHeader()));
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("One ArenaProjectionLayer per floor height for multi-level rooms (see 'Layer Y gap')");
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy raw (WPos)"))
        {
            ImGui.SetClipboardText(BuildRawSnippet(CollisionOutlinesExtractor.ClipboardVectorFormat.Vector2XZ));
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Just the simplified vertices as new(x, z) lists, no declaration");
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy raw (Vector3)"))
        {
            ImGui.SetClipboardText(BuildRawSnippet(CollisionOutlinesExtractor.ClipboardVectorFormat.Vector3XYZ));
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("The simplified vertices with their recovered floor height as new(x, y, z) lists");
        }
    }

    private BossModule? _appliedModule;
    private ArenaBounds? _appliedPrevBounds;
    private WPos _appliedPrevCenter;

    // pushes the polygon into the active boss module's arena (radar, hints, pathfinding) so it can be checked in place; restore puts the module's own arena back
    public void DrawApplyButtons(BossModuleManager? bmm)
    {
        var outers = EnabledContourCounts(out _);
        var module = bmm?.ActiveModule;
        var canApply = outers > 0 && module != null;
        using (ImRaii.Disabled(!canApply))
        {
            if (ImGui.Button("Apply to active boss module"))
            {
                var bounds = BuildArenaBounds();
                if (bounds != null)
                {
                    if (!ReferenceEquals(_appliedModule, module))
                    {
                        _appliedModule = module;
                        _appliedPrevBounds = module!.Arena.Bounds;
                        _appliedPrevCenter = module.Arena.Center;
                    }
                    module!.Arena.Bounds = bounds;
                    module.Arena.Center = bounds.Center;
                    Status = $"applied to {module.GetType().Name}: center=({bounds.Center.X:f2}, {bounds.Center.Z:f2}) r={bounds.Radius:f1}";
                }
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(!canApply ? (outers == 0 ? "no enabled polygons" : "no active boss module") : "Push this polygon into the running boss module (radar, hints, pathfinding) until 'Restore module arena' or the module unloads - works in replays too");
        }
        ImGui.SameLine();
        var canRestore = _appliedModule != null && ReferenceEquals(_appliedModule, module) && _appliedPrevBounds != null;
        using (ImRaii.Disabled(!canRestore))
        {
            if (ImGui.Button("Restore module arena"))
            {
                module!.Arena.Bounds = _appliedPrevBounds!;
                module.Arena.Center = _appliedPrevCenter;
                _appliedModule = null;
                _appliedPrevBounds = null;
                Status = "module arena restored";
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Put the module's own arena back");
            }
        }
    }

    public void SnippetPreviewBox(Func<string> provenanceHeader)
    {
        if (EnabledContourCounts(out _) == 0)
        {
            return;
        }
        if (SnippetDirty)
        {
            _snippetCache = BuildModuleSnippet(provenanceHeader(), false);
            SnippetDirty = false;
        }
        ImGui.InputTextMultiline("##snippet", ref _snippetCache, 65536, new Vector2(-1f, ImGui.GetTextLineHeight() * 8f), ImGuiInputTextFlags.ReadOnly);
    }

    public void DrawPreviewTable()
    {
        if (Preview.Count == 0)
        {
            return;
        }
        if (ImGui.SmallButton("Enable all"))
        {
            SetAllContours(true);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Disable slivers below area"))
        {
            var count = Preview.Count;
            for (var i = 0; i < count; ++i)
            {
                var poly = Preview[i];
                if (poly.Outer.Area < DropAreaBelow)
                {
                    poly.Outer.Enabled = false;
                }
                for (var h = 0; h < poly.Holes.Count; ++h)
                {
                    if (poly.Holes[h].Area < DropAreaBelow)
                    {
                        poly.Holes[h].Enabled = false;
                    }
                }
            }
            SnippetDirty = true;
        }

        HoverContour = null;
        using var table = ImRaii.Table("##preview", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
        if (!table)
        {
            return;
        }
        ImGui.TableSetupColumn("on");
        ImGui.TableSetupColumn("#");
        ImGui.TableSetupColumn("kind");
        ImGui.TableSetupColumn("verts raw→simp");
        ImGui.TableSetupColumn("area");
        ImGui.TableSetupColumn("bbox");
        ImGui.TableSetupColumn("Y mean (spread)");
        ImGui.TableHeadersRow();
        var n = Preview.Count;
        for (var i = 0; i < n; ++i)
        {
            var poly = Preview[i];
            DrawContourRow(poly.Outer, i, "outer", i * 64);
            for (var h = 0; h < poly.Holes.Count; ++h)
            {
                DrawContourRow(poly.Holes[h], i, $"  hole {h}", i * 64 + 1 + h);
            }
        }
    }

    private void DrawContourRow(PreviewContour c, int index, string kind, int id)
    {
        using var pid = ImRaii.PushId(id);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.Checkbox("##on", ref c.Enabled))
        {
            SnippetDirty = true;
        }
        ImGui.TableNextColumn();
        ImGui.Selectable(index.ToString(), false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap);
        if (ImGui.IsItemHovered())
        {
            HoverContour = c;
        }
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(kind);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{c.Raw.Length}→{c.Simplified.Length}");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{c.Area:f1}");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"({c.BBMin.X:f1}, {c.BBMin.Z:f1})..({c.BBMax.X:f1}, {c.BBMax.Z:f1})");
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{c.MeanY:f2} ({c.MaxY - c.MinY:f2})");
    }

    public void SetAllContours(bool enabled)
    {
        var count = Preview.Count;
        for (var i = 0; i < count; ++i)
        {
            Preview[i].Outer.Enabled = enabled;
            for (var h = 0; h < Preview[i].Holes.Count; ++h)
            {
                Preview[i].Holes[h].Enabled = enabled;
            }
        }
        SnippetDirty = true;
    }
}

// pure text builders for the arena snippets (repo conventions: collection expressions, f suffixes, 5 vertices per line)
public static class CollisionArenaCodeGen
{
    public static string F(float v, int decimals)
    {
        var s = v.ToString("F" + decimals, CultureInfo.InvariantCulture);
        return (s[0] == '-' && !s.AsSpan(1).ContainsAnyExcept("0.") ? s[1..] : s) + "f"; // a value rounding to zero is written without the sign
    }

    public static void AppendVertexField(StringBuilder sb, string name, WPos[] pts, int decimals, string trailingComment)
    {
        sb.Append($"private static readonly WPos[] {name} = [");
        var n = pts.Length;
        for (var i = 0; i < n; ++i)
        {
            if (i > 0)
            {
                sb.Append(i % 5 == 0 ? ",\r\n    " : ", ");
            }
            sb.Append($"new({F(pts[i].X, decimals)}, {F(pts[i].Z, decimals)})");
        }
        sb.Append("];").Append(trailingComment).Append("\r\n");
    }

    // vertexPrefix distinguishes several arenas in one snippet ("room" -> roomVertices / roomHole0)
    public static void AppendVertexFields(StringBuilder sb, List<WPos[]> outers, List<WPos[]> holes, int decimals, string vertexPrefix = "")
    {
        var single = outers.Count == 1 && holes.Count == 0;
        for (var i = 0; i < outers.Count; ++i)
        {
            AppendVertexField(sb, single ? $"{vertexPrefix}{Cap(vertexPrefix, "vertices")}" : $"{vertexPrefix}{Cap(vertexPrefix, "vertices")}{i}", outers[i], decimals, "");
        }
        for (var i = 0; i < holes.Count; ++i)
        {
            AppendVertexField(sb, $"{vertexPrefix}{Cap(vertexPrefix, "hole")}{i}", holes[i], decimals, "");
        }
    }

    private static string Cap(string prefix, string word) => prefix.Length > 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word;

    public static string ArenaDeclaration(string name, int outerCount, int holeCount, bool flat, float meanY, float minY, float maxY, bool emitProjectionHeightZero, bool adjustForHitboxInwards = false, bool adjustForHitboxOutwards = false, string vertexPrefix = "")
    {
        var single = outerCount == 1 && holeCount == 0;
        var vertices = $"{vertexPrefix}{Cap(vertexPrefix, "vertices")}";
        var hole = $"{vertexPrefix}{Cap(vertexPrefix, "hole")}";
        var sb = new StringBuilder();
        sb.Append($"private static readonly ArenaBoundsCustom {name} = new([");
        for (var i = 0; i < outerCount; ++i)
        {
            sb.Append(i > 0 ? ", " : "").Append($"new PolygonCustom({(single ? vertices : $"{vertices}{i}")})");
        }
        sb.Append(']');
        if (holeCount > 0)
        {
            sb.Append(", [");
            for (var i = 0; i < holeCount; ++i)
            {
                sb.Append(i > 0 ? ", " : "").Append($"new PolygonCustom({hole}{i})");
            }
            sb.Append(']');
        }
        if (adjustForHitboxInwards)
        {
            sb.Append(", AdjustForHitboxInwards: true");
        }
        if (adjustForHitboxOutwards)
        {
            sb.Append(", AdjustForHitboxOutwards: true");
        }
        sb.Append(')');
        if (flat)
        {
            var floor = MathF.Round(meanY, 1);
            sb.Append($" {{ Y = {F(floor + 0.1f, 1)}, BorderY = {F(floor, 1)}");
            if (emitProjectionHeightZero)
            {
                sb.Append(", WorldProjectionHeight = 0f");
            }
            sb.Append($" }}; // floor Y {F(floor, 1)} (spread {F(maxY - minY, 2)}); Y = floor + 0.1 follows existing modules, adjust if the border sinks");
        }
        else
        {
            sb.Append($"; // floor not flat (Y {F(minY, 1)}..{F(maxY, 1)}): leave Y = NaN (boss height) or use the layered snippet");
        }
        sb.Append("\r\n");
        return sb.ToString();
    }
}
