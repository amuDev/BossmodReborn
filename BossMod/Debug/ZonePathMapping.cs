namespace BossMod;

// the run cut into segments (pulls to the first boss, the boss, pulls to the next, ...) and, per segment, what the party walked over:
// the samples of every player, the triangles under them and the steps between two triangles the geometry may not join (bridges)
public sealed class ZonePathSegment
{
    public string Name = "";
    public double T0, T1;
    public List<Vector3> Samples = [];
    public List<int> ForcedTriangles = [];
    public List<(int a, int b)> ForcedLinks = [];
}

public static class ZonePathMapping
{
    // boundaries at every boss pull and death; a teleport inside a segment needs no split, the corridor is a union anyway
    public static List<(string name, double t0, double t1)> Segments(ZoneSceneTimelineFile tl)
    {
        List<(string, double, double)> r = [];
        var start = tl.Rules.Find(x => x.Kind == ZoneRuleKind.InstanceStart)?.T ?? 0d;
        var t = start;
        var n = 0;
        foreach (var b in tl.Bosses.OrderBy(b => b.Pull))
        {
            var pull = Math.Max(b.Pull - 2d, t);
            var death = b.Death >= 0 ? b.Death + 2d : tl.Duration;
            if (pull - t > 5d)
            {
                r.Add(($"pulls {++n} to {b.Name}", t, pull));
            }
            r.Add(($"boss {b.Name}", pull, death));
            t = death;
        }
        if (tl.Duration - t > 5d)
        {
            r.Add((tl.Bosses.Count > 0 ? "after the last boss" : "run", t, tl.Duration));
        }
        return r;
    }

    // maxStep: two consecutive samples of one player closer than this are one step; both ends picked to a triangle within 1.5 y of the player's height
    public static List<ZonePathSegment> Build(ZoneCollisionScene scene, ZoneTrianglePicker picker, ZoneSceneTimelineFile tl, float maxStep = 3f)
    {
        List<ZonePathSegment> result = [];
        List<int> scratch = [];
        foreach (var (name, t0, t1) in Segments(tl))
        {
            var seg = BuildSegment(scene, picker, tl, name, t0, t1, scratch, maxStep);
            if (seg.Samples.Count > 0)
            {
                result.Add(seg);
            }
        }
        return result;
    }

    // one segment of the run (a name and a time window), without mapping the others; the segment is returned even when no sample fell into the window
    public static ZonePathSegment BuildSegment(ZoneCollisionScene scene, ZoneTrianglePicker picker, ZoneSceneTimelineFile tl, string name, double t0, double t1, List<int>? scratch = null, float maxStep = 3f)
    {
        scratch ??= [];
        var tris = scene.Triangles.Span;
        var seg = new ZonePathSegment { Name = name, T0 = t0, T1 = t1 };
        HashSet<int> forced = [];
        HashSet<long> links = [];
        foreach (var p in tl.Players)
        {
            var s = p.Samples;
            var n = s.Length / 4;
            var prevTri = -1;
            Vector3 prev = default;
            for (var i = 0; i < n; ++i)
            {
                var t = s[4 * i];
                if (t < t0 || t > t1)
                {
                    prevTri = -1;
                    continue;
                }
                var pos = new Vector3(s[4 * i + 1], s[4 * i + 2], s[4 * i + 3]);
                seg.Samples.Add(pos);
                var tri = picker.PickTriangleNearY(new WPos(pos.X, pos.Z), pos.Y, scratch);
                if (tri >= 0 && MathF.Abs(tris[tri].YAt(pos.X, pos.Z) - pos.Y) > 1.5f)
                {
                    tri = -1;
                }
                if (tri >= 0)
                {
                    forced.Add(tri);
                    if (prevTri >= 0 && prevTri != tri && (pos - prev).Length() <= maxStep)
                    {
                        links.Add(prevTri < tri ? ((long)prevTri << 32) | (uint)tri : ((long)tri << 32) | (uint)prevTri);
                    }
                }
                prevTri = tri;
                prev = pos;
            }
        }
        seg.ForcedTriangles = [.. forced];
        seg.ForcedLinks = new(links.Count);
        foreach (var l in links)
        {
            seg.ForcedLinks.Add(((int)(l >> 32), (int)(uint)l));
        }
        return seg;
    }
}
