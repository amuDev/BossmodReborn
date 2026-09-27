namespace BossMod;

// one closed outline with its holes (world XZ with the sampled Y kept): what the polygon builders produce and the arena pipeline consumes
public sealed class PolygonWithHoles
{
    public List<Vector3> Outer = [];
    public List<List<Vector3>> Holes = [];
}

// how a triangle's material is matched against the wanted id
public enum MaterialMatchMode
{
    EffectiveMasked, // masked compare on the effective material
    EffectiveExact, // effective == wantedId
    PrimMasked, // masked compare on the raw primitive material
    PrimExact // raw primitive material == wantedId
}
