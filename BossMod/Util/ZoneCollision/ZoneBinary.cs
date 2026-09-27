using System.Buffers.Binary;
using System.Globalization;

namespace BossMod;

public sealed class ZoneFormatException(string message) : Exception(message);

// bounds-checked little-endian reads; a malformed file must never take the game down
public static class ZoneBinary
{
    private static void Check(ReadOnlySpan<byte> d, int off, int size)
    {
        if (off < 0 || size < 0 || off > d.Length - size)
        {
            throw new ZoneFormatException($"read of {size} bytes at 0x{off:X} outside file of {d.Length} bytes");
        }
    }

    public static byte U8(ReadOnlySpan<byte> d, int off)
    {
        Check(d, off, 1);
        return d[off];
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

    // FNV-1a 64 over the bytes of v, chained from h (seed with Fnv1aOffset); used for stable layout path ids
    public const ulong Fnv1aOffset = 14695981039346656037ul;

    public static ulong Fnv1a64(ulong h, ulong v)
    {
        for (var i = 0; i < 8; ++i)
        {
            h = (h ^ (v & 0xFF)) * 1099511628211ul;
            v >>= 8;
        }
        return h;
    }

    public static ulong Fnv1a64(ulong h, string s)
    {
        foreach (var c in s)
        {
            h = (h ^ (byte)c) * 1099511628211ul;
            h = (h ^ (byte)(c >> 8)) * 1099511628211ul;
        }
        return h;
    }

    // hex text as the ui and the project files carry ids ("1E8FB8", "0x1E8FB8"); surrounding whitespace is ignored
    public static bool TryParseHex(string s, out ulong value)
    {
        var t = s.AsSpan().Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }
        return ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

public readonly record struct Bounds3(Vector3 Min, Vector3 Max)
{
    // the identity of Union: Min above every point, Max below every point
    public static readonly Bounds3 Empty = new(new(float.MaxValue), new(float.MinValue));

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

    // the 8 corners of local placed by world, in ZoneBoxInstance.UnitCorners order (x from bit 2, y from bit 1, z from bit 0)
    public static void TransformCorners(in Bounds3 local, in Matrix4x4 world, Span<Vector3> corners)
    {
        for (var i = 0; i < 8; ++i)
        {
            var c = new Vector3((i & 4) != 0 ? local.Max.X : local.Min.X, (i & 2) != 0 ? local.Max.Y : local.Min.Y, (i & 1) != 0 ? local.Max.Z : local.Min.Z);
            corners[i] = Vector3.Transform(c, world);
        }
    }

    public static Bounds3 Transform(in Bounds3 local, in Matrix4x4 world)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        TransformCorners(local, world, corners);
        return FromPoints(corners);
    }

    public static Bounds3 Union(in Bounds3 a, in Bounds3 b) => new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));
}

// layout files store euler angles (radians) applied X, then Y, then Z; world = S * R * T in the row-vector convention (as Matrix4x3.FullMatrix)
public static class ZoneTransform
{
    public static Matrix4x4 Compose(in Vector3 translation, in Vector3 eulerRad, in Vector3 scale) => Matrix4x4.CreateScale(scale) * Rotation(eulerRad) * Matrix4x4.CreateTranslation(translation);

    public static Matrix4x4 Rotation(in Vector3 e) => Matrix4x4.CreateRotationX(e.X) * Matrix4x4.CreateRotationY(e.Y) * Matrix4x4.CreateRotationZ(e.Z);
}
