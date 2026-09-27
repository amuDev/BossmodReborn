using Clipper2Lib;

namespace BossMod;

// height source for clipper output vertices (scaled integer XZ): the union builder's exact lookup or the nearest-vertex sampler
public interface IHeightSource
{
    float Sample(Point64 p, long scale);
}

// nearest-vertex height lookup over a point cloud (polygon vertices, box corners, rim triangles): a uniform XZ hash grid searched ring by ring
// outward from the query cell, stopping as soon as no unvisited cell can hold a closer point; replaces a linear scan per output vertex
public sealed class YSampler : IHeightSource
{
    private const float CellSize = 1f;
    private readonly Vector3[] _points;
    private readonly XZHashGrid _grid = new(CellSize);

    public YSampler(List<Vector3> points)
    {
        _points = [.. points];
        for (var i = 0; i < _points.Length; ++i)
        {
            _grid.AddPoint(i, _points[i].X, _points[i].Z);
        }
    }

    public int Count => _points.Length;

    private struct Nearest(Vector3[] points, float x, float z) : IRingVisitor
    {
        public float Best = float.MaxValue;
        public float Y;

        public readonly double BestDistSq => Best;

        public void Visit(List<int> ids)
        {
            for (var i = 0; i < ids.Count; ++i)
            {
                var v = points[ids[i]];
                var ddx = v.X - x;
                var ddz = v.Z - z;
                var d = ddx * ddx + ddz * ddz;
                if (d < Best)
                {
                    Best = d;
                    Y = v.Y;
                }
            }
        }
    }

    // Y of the point nearest to (x, z); 0 when empty (matches the previous linear scan)
    public float Sample(float x, float z)
    {
        if (_points.Length == 0)
        {
            return 0f;
        }
        var v = new Nearest(_points, x, z);
        _grid.NearestRings(_grid.CellOf(x), _grid.CellOf(z), ref v, CellSize);
        return v.Y;
    }

    public float Sample(Point64 p, long scale) => Sample((float)(p.X / (double)scale), (float)(p.Y / (double)scale));
}
