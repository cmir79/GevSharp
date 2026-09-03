using GevSharp.GenApi.Model;

namespace GevSharp.GenApi.Runtime;

/// <summary>
/// 레지스터 원시 바이트와 정수·실수 사이의 변환, 그리고 수식 값(<see cref="GenApiValue"/>)의 정수화 규칙.
/// 바이트 순서는 XML 의 Endianess 를 따르고, 부호 확장은 길이가 8 미만인 Signed 레지스터에만 건다.
/// 실수를 정수 자리에 넣을 때는 가장 가까운 정수로 반올림한다(절삭이 아니다 — 1999.9999 가 1999 로 떨어지면 왕복이 깨진다).
/// </summary>
internal static class NumericCodec
{
    /// <summary>길이 1..8 바이트를 부호 없는 64비트로 모은다.</summary>
    public static ulong DecodeUnsigned(byte[] bytes, int length, Endianess endianess)
    {
        ulong v = 0;
        if (endianess == Endianess.BigEndian)
        {
            for (var i = 0; i < length; i++) v = (v << 8) | bytes[i];
        }
        else
        {
            for (var i = length - 1; i >= 0; i--) v = (v << 8) | bytes[i];
        }
        return v;
    }

    /// <summary>부호 규칙을 적용한 정수. Signed 이고 길이가 8 미만이면 최상위 비트로 부호 확장한다.</summary>
    public static long DecodeInt64(byte[] bytes, int length, Endianess endianess, Sign sign, string nodeName)
    {
        var u = DecodeUnsigned(bytes, length, endianess);
        return SignExtend(u, length * 8, sign, nodeName);
    }

    /// <summary>
    /// bits 폭의 값에 부호 규칙을 적용한다(폭 64 는 그대로). 부호 없는 64비트 폭에서 값이 long 상한을 넘으면
    /// 노드가 내놓을 수 있는 값이 아니다 — 조용히 음수로 감기면 노드 자신이 알리는 범위(<see cref="MaxOfWidth"/>) 밖의 값이
    /// 흘러나오고 그 값을 다시 쓸 수도 없으므로, 노드 이름을 담아 던진다.
    /// </summary>
    public static long SignExtend(ulong value, int bits, Sign sign, string nodeName)
    {
        if (sign == Sign.Signed && bits < 64)
        {
            var shift = 64 - bits;
            return ((long)(value << shift)) >> shift;
        }
        if (sign == Sign.Unsigned && bits >= 64 && value > long.MaxValue)
            throw new GenApiException($"Node '{nodeName}' holds unsigned value {value}, which exceeds the largest value this node map represents ({long.MaxValue}).", nodeName);
        return unchecked((long)value);
    }

    /// <summary>정수의 하위 length 바이트를 지정한 바이트 순서로 쓴다.</summary>
    public static void EncodeInt64(long value, byte[] dst, int length, Endianess endianess)
    {
        var u = unchecked((ulong)value);
        if (endianess == Endianess.BigEndian)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                dst[i] = (byte)u;
                u >>= 8;
            }
        }
        else
        {
            for (var i = 0; i < length; i++)
            {
                dst[i] = (byte)u;
                u >>= 8;
            }
        }
    }

    /// <summary>bits 폭·부호로 표현 가능한 최소값.</summary>
    public static long MinOfWidth(int bits, Sign sign)
    {
        if (sign == Sign.Unsigned) return 0;
        return bits >= 64 ? long.MinValue : -(1L << (bits - 1));
    }

    /// <summary>
    /// bits 폭·부호로 표현 가능한 최대값. 부호 없는 64비트는 long 으로 담을 수 있는 상한까지이며,
    /// 그 위의 값은 <see cref="SignExtend"/> 가 읽는 자리에서 거절하므로 이 상한과 실제로 읽히는 값이 어긋나지 않는다.
    /// </summary>
    public static long MaxOfWidth(int bits, Sign sign)
    {
        if (sign == Sign.Unsigned) return bits >= 64 ? long.MaxValue : (1L << bits) - 1;
        return bits >= 64 ? long.MaxValue : (1L << (bits - 1)) - 1;
    }

    /// <summary>bits 폭의 하위 비트 마스크(폭 64 는 전체).</summary>
    public static ulong MaskOfWidth(int bits) => bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

    /// <summary>IEEE 754 실수 레지스터(4 또는 8 바이트) 디코딩.</summary>
    public static double DecodeFloat(byte[] bytes, int length, Endianess endianess)
    {
        var bits = DecodeUnsigned(bytes, length, endianess);
        return length == 4 ? BitsToSingle((uint)bits) : BitConverter.Int64BitsToDouble(unchecked((long)bits));
    }

    /// <summary>IEEE 754 실수 레지스터 인코딩. 4 바이트는 단정도로 좁힌다.</summary>
    public static void EncodeFloat(double value, byte[] dst, int length, Endianess endianess)
    {
        var bits = length == 4 ? SingleToBits((float)value) : unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        EncodeInt64(unchecked((long)bits), dst, length, endianess);
    }

    private static float BitsToSingle(uint bits)
    {
#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        return BitConverter.Int32BitsToSingle(unchecked((int)bits));
#else
        return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
#endif
    }

    private static uint SingleToBits(float value)
    {
#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        return unchecked((uint)BitConverter.SingleToInt32Bits(value));
#else
        return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
#endif
    }

    /// <summary>수식 값을 정수로. 실수는 가장 가까운 정수로 반올림(.5 는 0 에서 먼 쪽), NaN·무한대·범위 밖은 <see cref="GenApiException"/>.</summary>
    public static long ToInt64(GenApiValue value, string nodeName)
    {
        if (value.IsInteger) return value.AsInt64;
        return RoundToInt64(value.AsDouble, nodeName);
    }

    public static long RoundToInt64(double value, string nodeName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new GenApiException($"Value {value} of node '{nodeName}' is not a finite number.", nodeName);
        var r = Math.Round(value, MidpointRounding.AwayFromZero);
        if (r < -9223372036854775808.0 || r >= 9223372036854775808.0)
            throw new GenApiException($"Value {value} of node '{nodeName}' is outside the 64-bit integer range.", nodeName);
        return (long)r;
    }
}
