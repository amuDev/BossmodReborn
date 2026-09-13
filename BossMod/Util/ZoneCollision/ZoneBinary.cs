using System.Buffers.Binary;

namespace BossMod;

public sealed class ZoneFormatException(string message) : Exception(message);

// bounds-checked little-endian reads; a malformed file must never take the game down
public static class ZoneBinary
{
    private static void Check(ReadOnlySpan<byte> d, int off, int size)
    {
        if (off < 0 || off + size > d.Length)
        {
            throw new ZoneFormatException($"read of {size} bytes at 0x{off:X} outside file of {d.Length} bytes");
        }
    }

    public static int I32(ReadOnlySpan<byte> d, int off)
    {
        Check(d, off, 4);
        return BinaryPrimitives.ReadInt32LittleEndian(d[off..]);
    }

    public static uint U32(ReadOnlySpan<byte> d, int off)
    {
        Check(d, off, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(d[off..]);
    }

    public static ushort U16(ReadOnlySpan<byte> d, int off)
    {
        Check(d, off, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(d[off..]);
    }

    public static ulong U64(ReadOnlySpan<byte> d, int off)
    {
        Check(d, off, 8);
        return BinaryPrimitives.ReadUInt64LittleEndian(d[off..]);
    }

    public static float F32(ReadOnlySpan<byte> d, int off)
    {
        Check(d, off, 4);
        return BinaryPrimitives.ReadSingleLittleEndian(d[off..]);
    }

    public static Vector3 Vec3(ReadOnlySpan<byte> d, int off) => new(F32(d, off), F32(d, off + 4), F32(d, off + 8));

    public static string CStr(ReadOnlySpan<byte> d, int off)
    {
        if (off < 0 || off >= d.Length)
        {
            throw new ZoneFormatException($"string at 0x{off:X} outside file of {d.Length} bytes");
        }
        var rest = d[off..];
        var end = rest.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]);
    }

    public static string Magic(ReadOnlySpan<byte> d, int off)
    {
        Check(d, off, 4);
        return Encoding.ASCII.GetString(d.Slice(off, 4));
    }
}

public readonly record struct Bounds3(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 Size => Max - Min;

    public bool IntersectsXZCircle(Vector2 c, float r)
    {
        var dx = MathF.Max(Min.X - c.X, MathF.Max(0f, c.X - Max.X));
        var dz = MathF.Max(Min.Z - c.Y, MathF.Max(0f, c.Y - Max.Z));
        return dx * dx + dz * dz <= r * r;
    }

    public bool ContainsXZ(float x, float z) => x >= Min.X && x <= Max.X && z >= Min.Z && z <= Max.Z;

    public static Bounds3 FromPoints(ReadOnlySpan<Vector3> pts)
    {
        if (pts.Length == 0)
        {
            return default;
        }
        var min = pts[0];
        var max = pts[0];
        for (var i = 1; i < pts.Length; ++i)
        {
            min = Vector3.Min(min, pts[i]);
            max = Vector3.Max(max, pts[i]);
        }
        return new(min, max);
    }

    public static Bounds3 Transform(in Bounds3 local, in Matrix4x4 world)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; ++i)
        {
            var c = new Vector3((i & 1) != 0 ? local.Max.X : local.Min.X, (i & 2) != 0 ? local.Max.Y : local.Min.Y, (i & 4) != 0 ? local.Max.Z : local.Min.Z);
            corners[i] = Vector3.Transform(c, world);
        }
        return FromPoints(corners);
    }

    public static Bounds3 Union(in Bounds3 a, in Bounds3 b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
}

public enum EulerOrder : byte { XYZ, XZY, YXZ, YZX, ZXY, ZYX }

// layout files store euler angles (radians); the composition order is not documented, the validator pins RotationOrder empirically
public static class ZoneTransform
{
    public static EulerOrder RotationOrder = EulerOrder.XYZ;
    public static bool ScaleFirst = true; // world = S * R * T (row-vector convention, as Matrix4x3.FullMatrix)

    public static Matrix4x4 Compose(in Vector3 translation, in Vector3 eulerRad, in Vector3 scale) => Compose(translation, eulerRad, scale, RotationOrder, ScaleFirst);

    public static Matrix4x4 Compose(in Vector3 translation, in Vector3 eulerRad, in Vector3 scale, EulerOrder order, bool scaleFirst = true)
    {
        var s = Matrix4x4.CreateScale(scale);
        var r = Rotation(eulerRad, order);
        var t = Matrix4x4.CreateTranslation(translation);
        return scaleFirst ? s * r * t : r * s * t;
    }

    // first named axis is applied first (row-vector convention: v * Rx * Ry * Rz for XYZ)
    public static Matrix4x4 Rotation(in Vector3 e, EulerOrder order)
    {
        var rx = Matrix4x4.CreateRotationX(e.X);
        var ry = Matrix4x4.CreateRotationY(e.Y);
        var rz = Matrix4x4.CreateRotationZ(e.Z);
        return order switch
        {
            EulerOrder.XYZ => rx * ry * rz,
            EulerOrder.XZY => rx * rz * ry,
            EulerOrder.YXZ => ry * rx * rz,
            EulerOrder.YZX => ry * rz * rx,
            EulerOrder.ZXY => rz * rx * ry,
            _ => rz * ry * rx
        };
    }
}
