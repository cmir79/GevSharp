using GevSharp.Pfnc;

namespace GevSharp.Tests.Pfnc;

/// <summary>
/// 네 가지 패킹을 손으로 계산한 바이트 패턴과, 테스트 쪽에서 독립적으로 구현한 패커가 만든 무작위 패턴 양쪽으로 검증한다.
/// 패커는 라이브러리 코드를 쓰지 않는다 — 문서에 적힌 배치를 비트 단위로 그대로 옮긴 것이다.
/// </summary>
public class PixelUnpackTests
{
    // ---------- 손으로 계산한 패턴 ----------

    [Fact]
    public void Gvsp12Packed_HandPattern()
    {
        // b0 = P0[11:4] = 0xAB, b1 = P1[3:0]<<4 | P0[3:0] = 0xCD, b2 = P1[11:4] = 0xEF → P0 = 0xABD, P1 = 0xEFC
        var dst = new ushort[2];
        PixelUnpack.Unpack12Packed(new byte[] { 0xAB, 0xCD, 0xEF }, 3, dst, 2, 2, 1);
        Assert.Equal(new ushort[] { 0xABD, 0xEFC }, dst);
    }

    [Fact]
    public void Gvsp10Packed_HandPattern_IgnoresPaddingBits()
    {
        // b1 = 0x21 = 0010_0001: [1:0] = 01 → P0 하위, [5:4] = 10 → P1 하위 → P0 = 0xAB<<2|1 = 0x2AD, P1 = 0xEF<<2|2 = 0x3BE
        var dst = new ushort[2];
        PixelUnpack.Unpack10Packed(new byte[] { 0xAB, 0x21, 0xEF }, 3, dst, 2, 2, 1);
        Assert.Equal(new ushort[] { 0x2AD, 0x3BE }, dst);

        // 패딩 비트 [3:2]·[7:6] 만 선 0xCC → 하위 2비트는 둘 다 0
        PixelUnpack.Unpack10Packed(new byte[] { 0xAB, 0xCC, 0xEF }, 3, dst, 2, 2, 1);
        Assert.Equal(new ushort[] { 0x2AC, 0x3BC }, dst);

        // 전부 1 → 하위 2비트 둘 다 3, 상위는 그대로
        PixelUnpack.Unpack10Packed(new byte[] { 0xAB, 0xFF, 0xEF }, 3, dst, 2, 2, 1);
        Assert.Equal(new ushort[] { 0x2AF, 0x3BF }, dst);
    }

    [Fact]
    public void Pfnc12p_HandPattern()
    {
        // LSB 우선: P0 = b0 | (b1 & 0x0F)<<8 = 0xDAB, P1 = b1>>4 | b2<<4 = 0xEFC
        var dst = new ushort[2];
        PixelUnpack.Unpack12p(new byte[] { 0xAB, 0xCD, 0xEF }, 3, dst, 2, 2, 1);
        Assert.Equal(new ushort[] { 0xDAB, 0xEFC }, dst);
    }

    [Fact]
    public void Pfnc10p_HandPattern()
    {
        // 픽셀 0x123, 0x2AB, 0x0F0, 0x3C5 를 LSB 우선으로 이어 붙인 40비트 = 0xF14F0AAD23 → 바이트 23 AD 0A 4F F1
        var dst = new ushort[4];
        PixelUnpack.Unpack10p(new byte[] { 0x23, 0xAD, 0x0A, 0x4F, 0xF1 }, 5, dst, 4, 4, 1);
        Assert.Equal(new ushort[] { 0x123, 0x2AB, 0x0F0, 0x3C5 }, dst);
    }

    [Fact]
    public void SameBytesDecodeDifferentlyUnderGvspAndPfncLayouts()
    {
        // 같은 3바이트라도 GVSP Packed 와 PFNC p 는 다른 픽셀을 뜻한다 — 둘을 섞어 쓰면 값이 어긋난다.
        var bytes = new byte[] { 0x12, 0x34, 0x56 };
        var gvsp = new ushort[2];
        var pfnc = new ushort[2];
        PixelUnpack.Unpack12Packed(bytes, 3, gvsp, 2, 2, 1);
        PixelUnpack.Unpack12p(bytes, 3, pfnc, 2, 2, 1);
        Assert.Equal(new ushort[] { 0x124, 0x563 }, gvsp);
        Assert.Equal(new ushort[] { 0x412, 0x563 }, pfnc);
    }

    // ---------- 독립 패커로 만든 무작위 패턴: 홀수 폭·줄 간격·복수 줄 ----------

    public static TheoryData<PixelPacking, int, int> Shapes()
    {
        var data = new TheoryData<PixelPacking, int, int>();
        foreach (var p in new[] { PixelPacking.Gvsp10Packed, PixelPacking.Gvsp12Packed, PixelPacking.Pfnc10p, PixelPacking.Pfnc12p })
            foreach (var w in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 64, 65 })
                foreach (var h in new[] { 1, 3 })
                    data.Add(p, w, h);
        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void UnpacksRandomRowsWithStrides(PixelPacking packing, int width, int height)
    {
        var bits = Bits(packing);
        var rng = new Random(width * 131 + height * 17 + (int)packing);
        var pixels = new ushort[width * height];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (ushort)rng.Next(0, 1 << bits);

        var lineBytes = LineBytes(packing, width);
        var srcStride = lineBytes + 3;                       // 줄 끝에 쓰레기 3바이트
        var src = Pack(packing, pixels, width, height, srcStride);

        var dstStride = width + 2;                           // 목적지도 여분 2픽셀
        var dst = new ushort[height * dstStride];
        dst.AsSpan().Fill((ushort)0xFFFF);

        Run(packing, src, srcStride, dst, dstStride, width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                Assert.Equal(pixels[y * width + x], dst[y * dstStride + x]);
            Assert.Equal((ushort)0xFFFF, dst[y * dstStride + width]);         // 여분 픽셀은 손대지 않는다
            Assert.Equal((ushort)0xFFFF, dst[y * dstStride + width + 1]);
        }

        // 표의 줄 바이트 계산과 언팩이 같은 줄 길이를 본다.
        Assert.Equal(lineBytes, PixelFormatInfo.LineBytes(RepresentativeCode(packing), width));
    }

    [Fact]
    public void ExactBufferSizesAreAccepted()
    {
        // 딱 맞는 크기(마지막 줄은 줄 간격이 아니라 줄 바이트만 있으면 된다)
        var src = new byte[2 * 8 + 5];   // 3줄, 폭 3 (12p: 5바이트), 줄 간격 8
        var dst = new ushort[2 * 4 + 3]; // 줄 간격 4
        PixelUnpack.Unpack12p(src, 8, dst, 4, 3, 3);
        PixelUnpack.Unpack12Packed(new byte[2 * 8 + 6], 8, dst, 4, 3, 3);
        PixelUnpack.Unpack10p(new byte[2 * 8 + 4], 8, dst, 4, 3, 3);
        PixelUnpack.Unpack10Packed(new byte[2 * 8 + 6], 8, dst, 4, 3, 3);
    }

    [Fact]
    public void UndersizedSourceThrows()
    {
        var dst = new ushort[3 * 3];
        var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12p(new byte[2 * 5 + 4], 5, dst, 3, 3, 3));
        Assert.Equal("src", ex.ParamName);
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12Packed(new byte[2 * 6 + 5], 6, dst, 3, 3, 3));
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack10p(new byte[2 * 4 + 3], 4, dst, 3, 3, 3));
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack10Packed(new byte[2 * 6 + 5], 6, dst, 3, 3, 3));
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12p(Array.Empty<byte>(), 5, dst, 3, 3, 3));
    }

    [Fact]
    public void UndersizedDestinationThrows()
    {
        var src = new byte[3 * 6];
        var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12Packed(src, 6, new ushort[2 * 3 + 2], 3, 3, 3));
        Assert.Equal("dst", ex.ParamName);
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack10Packed(src, 6, new ushort[8], 3, 3, 3));
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12p(src, 6, new ushort[8], 3, 3, 3));
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack10p(src, 6, new ushort[8], 3, 3, 3));
    }

    [Fact]
    public void StrideShorterThanLineThrows()
    {
        var src = new byte[100];
        var dst = new ushort[100];
        var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12Packed(src, 5, dst, 4, 4, 2));   // 폭 4 = 6바이트
        Assert.Equal("srcStrideBytes", ex.ParamName);
        var ex2 = Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12Packed(src, 6, dst, 3, 4, 2));
        Assert.Equal("dstStridePixels", ex2.ParamName);
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack10p(src, 4, dst, 4, 4, 2));                 // 폭 4 = 5바이트
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12p(src, 4, dst, 3, 3, 2));                 // 폭 3 = 5바이트
    }

    [Fact]
    public void NegativeDimensionsThrowAndZeroDimensionsDoNothing()
    {
        var src = new byte[16];
        var dst = new ushort[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.Unpack12p(src, 8, dst, 8, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.Unpack10Packed(src, 8, dst, 8, 1, -1));
        dst.AsSpan().Fill((ushort)7);
        PixelUnpack.Unpack12p(src, 8, dst, 8, 0, 4);
        PixelUnpack.Unpack10p(src, 8, dst, 8, 4, 0);
        PixelUnpack.Unpack12Packed(Array.Empty<byte>(), 0, Array.Empty<ushort>(), 0, 0, 0);
        Assert.All(dst, v => Assert.Equal((ushort)7, v));

        // 폭·높이가 0 이어도 줄 간격은 검사한다 — 쓰레기 간격을 조용히 받지 않는다.
        var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12p(src, -99, dst, -99, 0, 4));
        Assert.Equal("srcStrideBytes", ex.ParamName);
        var ex2 = Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack10p(src, 8, dst, -1, 4, 0));
        Assert.Equal("dstStridePixels", ex2.ParamName);
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack12Packed(src, 5, dst, 4, 4, 0));   // 폭 4 = 6바이트
    }

    [Fact]
    public void OversizedDimensionsThrowArgumentOutOfRange()
    {
        // int 를 넘는 줄·배열은 OverflowException 이 아니라 치수 인자 예외.
        var src = new byte[16];
        var dst = new ushort[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.Unpack12Packed(src, 8, dst, 8, 1_500_000_000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.Unpack10p(src, 8, dst, 8, int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.Unpack((uint)PixelFormat.Mono12p, src, 8, dst, 8, 1_500_000_000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.UnpackToArray((uint)PixelFormat.Mono12p, src, 100_000, 100_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.UnpackToArray((uint)PixelFormat.Mono10Packed, src, 1_500_000_000, 1));
    }

    // ---------- 코드 기반 분기 ----------

    [Fact]
    public void DispatchByCodeUsesSameRoutineForBayerVariants()
    {
        var bytes = new byte[] { 0xAB, 0xCD, 0xEF };
        var a = new ushort[2];
        var b = new ushort[2];
        PixelUnpack.Unpack((uint)PixelFormat.BayerRG12Packed, bytes, 3, a, 2, 2, 1);
        PixelUnpack.Unpack12Packed(bytes, 3, b, 2, 2, 1);
        Assert.Equal(b, a);

        PixelUnpack.Unpack((uint)PixelFormat.BayerBG12p, bytes, 3, a, 2, 2, 1);
        PixelUnpack.Unpack12p(bytes, 3, b, 2, 2, 1);
        Assert.Equal(b, a);

        PixelUnpack.Unpack((uint)PixelFormat.BayerGB10Packed, bytes, 3, a, 2, 2, 1);
        PixelUnpack.Unpack10Packed(bytes, 3, b, 2, 2, 1);
        Assert.Equal(b, a);

        var five = new byte[] { 0x23, 0xAD, 0x0A, 0x4F, 0xF1 };
        var c = new ushort[4];
        PixelUnpack.Unpack((uint)PixelFormat.BayerGR10p, five, 5, c, 4, 4, 1);
        Assert.Equal(new ushort[] { 0x123, 0x2AB, 0x0F0, 0x3C5 }, c);
    }

    [Fact]
    public void CanUnpackAndRejection()
    {
        Assert.True(PixelUnpack.CanUnpack((uint)PixelFormat.Mono10Packed));
        Assert.True(PixelUnpack.CanUnpack((uint)PixelFormat.Mono12Packed));
        Assert.True(PixelUnpack.CanUnpack((uint)PixelFormat.Mono10p));
        Assert.True(PixelUnpack.CanUnpack((uint)PixelFormat.Mono12p));
        Assert.True(PixelUnpack.CanUnpack((uint)PixelFormat.BayerBG12Packed));
        Assert.False(PixelUnpack.CanUnpack((uint)PixelFormat.Mono8));
        Assert.False(PixelUnpack.CanUnpack((uint)PixelFormat.Mono12));
        Assert.False(PixelUnpack.CanUnpack((uint)PixelFormat.Mono14p));
        Assert.False(PixelUnpack.CanUnpack((uint)PixelFormat.RGB565p));
        Assert.False(PixelUnpack.CanUnpack(0u));
        Assert.False(PixelUnpack.CanUnpack(0x810C0001u));

        var src = new byte[16];
        var dst = new ushort[16];
        var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack((uint)PixelFormat.Mono8, src, 4, dst, 4, 4, 1));
        Assert.Equal("code", ex.ParamName);
        Assert.Throws<ArgumentException>(() => PixelUnpack.Unpack(0x810C0001u, src, 4, dst, 4, 4, 1));
        Assert.Throws<ArgumentException>(() => PixelUnpack.UnpackToArray((uint)PixelFormat.Mono12, src, 4, 1));
    }

    [Fact]
    public void UnpackToArrayHonoursPaddingX()
    {
        // 폭 3 의 12p 줄 = 5바이트, PaddingX 2 → 줄 간격 7
        var pixels = new ushort[] { 0x111, 0x222, 0x333, 0x444, 0x555, 0x666 };
        var src = Pack(PixelPacking.Pfnc12p, pixels, 3, 2, 7);
        var result = PixelUnpack.UnpackToArray((uint)PixelFormat.Mono12p, src, 3, 2, paddingX: 2);
        Assert.Equal(pixels, result);

        // 패딩이 없으면 데이터가 줄에서 끊기지 않는다 — 폭 3 × 높이 2 는 "6픽셀 한 줄" 로 실린다(9바이트).
        // 10비트 쪽은 값 범위가 0..1023 이라 별도 픽셀을 쓴다.
        var pixels10 = new ushort[] { 0x111, 0x222, 0x333, 0x044, 0x155, 0x266 };
        var packed = Pack(PixelPacking.Gvsp10Packed, pixels10, 6, 1, 9);
        Assert.Equal(9, packed.Length);
        Assert.Equal(pixels10, PixelUnpack.UnpackToArray((uint)PixelFormat.BayerRG10Packed, packed, 3, 2));
    }

    // ---------- 테스트 쪽 독립 패커 ----------

    private static int Bits(PixelPacking p) => p is PixelPacking.Gvsp10Packed or PixelPacking.Pfnc10p ? 10 : 12;

    private static int LineBytes(PixelPacking p, int width) => p switch
    {
        PixelPacking.Gvsp10Packed or PixelPacking.Gvsp12Packed => (width + 1) / 2 * 3,
        _ => (width * Bits(p) + 7) / 8,
    };

    private static uint RepresentativeCode(PixelPacking p) => p switch
    {
        PixelPacking.Gvsp10Packed => (uint)PixelFormat.Mono10Packed,
        PixelPacking.Gvsp12Packed => (uint)PixelFormat.Mono12Packed,
        PixelPacking.Pfnc10p => (uint)PixelFormat.Mono10p,
        _ => (uint)PixelFormat.Mono12p,
    };

    private static void Run(PixelPacking p, byte[] src, int srcStride, ushort[] dst, int dstStride, int width, int height)
    {
        switch (p)
        {
            case PixelPacking.Gvsp10Packed: PixelUnpack.Unpack10Packed(src, srcStride, dst, dstStride, width, height); break;
            case PixelPacking.Gvsp12Packed: PixelUnpack.Unpack12Packed(src, srcStride, dst, dstStride, width, height); break;
            case PixelPacking.Pfnc10p: PixelUnpack.Unpack10p(src, srcStride, dst, dstStride, width, height); break;
            default: PixelUnpack.Unpack12p(src, srcStride, dst, dstStride, width, height); break;
        }
    }

    /// <summary>줄마다 stride 바이트 간격으로 픽셀을 채운다. 줄 밖·패딩 자리는 0xEE(GVSP 10Packed 의 패딩 비트는 1로 세워 무시되는지 본다).</summary>
    private static byte[] Pack(PixelPacking p, ushort[] pixels, int width, int height, int stride)
    {
        var lineBytes = LineBytes(p, width);
        var buf = new byte[(height - 1) * stride + lineBytes];
        buf.AsSpan().Fill((byte)0xEE);
        for (var y = 0; y < height; y++)
        {
            var o = y * stride;
            if (p is PixelPacking.Gvsp10Packed or PixelPacking.Gvsp12Packed)
            {
                var shift = Bits(p) - 8;
                var lowMask = (1 << shift) - 1;
                var padBits = p == PixelPacking.Gvsp10Packed ? 0xCC : 0x00;
                for (var x = 0; x < width; x += 2, o += 3)
                {
                    int p0 = pixels[y * width + x];
                    buf[o] = (byte)(p0 >> shift);
                    if (x + 1 < width)
                    {
                        int p1 = pixels[y * width + x + 1];
                        buf[o + 1] = (byte)(((p1 & lowMask) << 4) | (p0 & lowMask) | padBits);
                        buf[o + 2] = (byte)(p1 >> shift);
                    }
                    else
                    {
                        buf[o + 1] = (byte)((p0 & lowMask) | padBits);
                        // buf[o + 2] 는 패딩 — 0xEE 그대로
                    }
                }
            }
            else
            {
                var bits = Bits(p);
                Array.Clear(buf, o, lineBytes);
                long bit = 0;
                for (var x = 0; x < width; x++)
                {
                    int v = pixels[y * width + x];
                    for (var b = 0; b < bits; b++, bit++)
                        if (((v >> b) & 1) != 0) buf[o + (int)(bit >> 3)] |= (byte)(1 << (int)(bit & 7));
                }
                // 마지막 바이트의 남는 비트는 1로 세워 두어 무시되는지 본다.
                for (; bit < (long)lineBytes * 8; bit++)
                    buf[o + (int)(bit >> 3)] |= (byte)(1 << (int)(bit & 7));
            }
        }
        return buf;
    }
}
