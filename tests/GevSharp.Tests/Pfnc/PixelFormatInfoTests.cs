using GevSharp.Pfnc;

namespace GevSharp.Tests.Pfnc;

public class PixelFormatInfoTests
{
    [Theory]
    [InlineData(0x01080001u, 8)]    // Mono8
    [InlineData(0x01100003u, 16)]   // Mono10 — 16비트 컨테이너
    [InlineData(0x010C0004u, 12)]   // Mono10Packed — 10비트지만 12비트 점유
    [InlineData(0x010A0046u, 10)]   // Mono10p
    [InlineData(0x010C0047u, 12)]   // Mono12p
    [InlineData(0x02180014u, 24)]   // RGB8
    [InlineData(0x02200016u, 32)]   // RGBa8
    [InlineData(0x020C001Eu, 12)]   // YUV411
    [InlineData(0x02100032u, 16)]   // YUV422
    [InlineData(0x026000C0u, 96)]   // Coord3D_ABC32f
    [InlineData(0x810C0001u, 12)]   // 벤더 전용 — 표에 없어도 비트 수는 코드에서 나온다
    [InlineData(0x01010037u, 1)]    // Mono1p
    public void BitsPerPixelComesFromCode(uint code, int bpp)
    {
        Assert.Equal(bpp, PixelFormatInfo.BitsPerPixel(code));
    }

    [Fact]
    public void ClassificationFlags()
    {
        Assert.True(PixelFormatInfo.IsMono(PixelFormat.Mono8));
        Assert.True(PixelFormatInfo.IsMono(PixelFormat.Mono12Packed));
        Assert.False(PixelFormatInfo.IsMono(PixelFormat.BayerRG8));
        Assert.False(PixelFormatInfo.IsMono(PixelFormat.RGB8));
        Assert.False(PixelFormatInfo.IsMono(PixelFormat.Coord3D_A16));
        Assert.False(PixelFormatInfo.IsMono(PixelFormat.Confidence8));

        Assert.True(PixelFormatInfo.IsBayer(PixelFormat.BayerGB12p));
        Assert.False(PixelFormatInfo.IsBayer(PixelFormat.Mono8));
        Assert.False(PixelFormatInfo.IsBayer(PixelFormat.RGB8));

        Assert.True(PixelFormatInfo.IsColor(PixelFormat.RGB8));
        Assert.True(PixelFormatInfo.IsColor(PixelFormat.YUV422_8));
        Assert.True(PixelFormatInfo.IsColor(PixelFormat.Coord3D_ABC32f));
        Assert.False(PixelFormatInfo.IsColor(PixelFormat.BayerBG8));
        Assert.False(PixelFormatInfo.IsColor(PixelFormat.Mono16));

        Assert.True(PixelFormatInfo.IsYuv(PixelFormat.YUV411_8_UYYVYY));
        Assert.True(PixelFormatInfo.IsYuv(PixelFormat.YCbCr422_8));
        Assert.False(PixelFormatInfo.IsYuv(PixelFormat.RGB8));

        Assert.True(PixelFormatInfo.IsPacked(PixelFormat.Mono10Packed));
        Assert.True(PixelFormatInfo.IsPacked(PixelFormat.Mono12p));
        Assert.True(PixelFormatInfo.IsPacked(PixelFormat.BayerGR10p));
        Assert.True(PixelFormatInfo.IsPacked(PixelFormat.RGB565p));
        Assert.True(PixelFormatInfo.IsPacked(PixelFormat.RGB10p32));
        Assert.False(PixelFormatInfo.IsPacked(PixelFormat.Mono10));
        Assert.False(PixelFormatInfo.IsPacked(PixelFormat.YUV422_8));
        Assert.False(PixelFormatInfo.IsPacked(PixelFormat.RGB8));
    }

    [Fact]
    public void UnknownCodesUseCodeStructureOnly()
    {
        const uint customMono12 = 0x810C0001;   // 벤더 전용, 12비트 점유
        const uint customColor = 0x82100005;
        Assert.False(PixelFormatInfo.IsKnown(customMono12));
        Assert.True(PixelFormatInfo.IsCustom(customMono12));
        Assert.True(PixelFormatInfo.IsMono(customMono12));
        Assert.False(PixelFormatInfo.IsBayer(customMono12));
        Assert.True(PixelFormatInfo.IsPacked(customMono12));
        Assert.True(PixelFormatInfo.IsColor(customColor));
        Assert.False(PixelFormatInfo.IsPacked(customColor));
        Assert.Equal(BayerPattern.None, PixelFormatInfo.BayerPattern(customMono12));
        Assert.Equal(PixelFormat.Unknown, PixelFormatInfo.ToPixelFormat(customMono12));
        Assert.Equal(PixelFormat.Mono8, PixelFormatInfo.ToPixelFormat(0x01080001));
        Assert.Throws<GevException>(() => PixelFormatInfo.ComponentCount(customMono12));
        Assert.Throws<GevException>(() => PixelFormatInfo.Packing(customMono12));
    }

    [Theory]
    [InlineData(PixelFormat.Mono8, 1)]
    [InlineData(PixelFormat.BayerRG12Packed, 1)]
    [InlineData(PixelFormat.RGB8, 3)]
    [InlineData(PixelFormat.BGR16, 3)]
    [InlineData(PixelFormat.RGBa8, 4)]
    [InlineData(PixelFormat.BGRa12p, 4)]
    [InlineData(PixelFormat.YUV422_8, 3)]
    [InlineData(PixelFormat.YCbCr411_8, 3)]
    [InlineData(PixelFormat.Coord3D_AC16, 2)]
    [InlineData(PixelFormat.Coord3D_ABC32f, 3)]
    [InlineData(PixelFormat.Coord3D_C32f, 1)]
    [InlineData(PixelFormat.Confidence8, 1)]
    public void ComponentCounts(PixelFormat f, int count)
    {
        Assert.Equal(count, PixelFormatInfo.ComponentCount(f));
    }

    [Theory]
    [InlineData(PixelFormat.BayerGR8, BayerPattern.GR)]
    [InlineData(PixelFormat.BayerRG10Packed, BayerPattern.RG)]
    [InlineData(PixelFormat.BayerGB12p, BayerPattern.GB)]
    [InlineData(PixelFormat.BayerBG16, BayerPattern.BG)]
    [InlineData(PixelFormat.BayerBG14p, BayerPattern.BG)]
    [InlineData(PixelFormat.Mono8, BayerPattern.None)]
    [InlineData(PixelFormat.RGB8, BayerPattern.None)]
    public void BayerPatterns(PixelFormat f, BayerPattern pattern)
    {
        Assert.Equal(pattern, PixelFormatInfo.BayerPattern(f));
        Assert.Equal(pattern, PixelFormatInfo.BayerPattern((uint)f));
    }

    [Theory]
    [InlineData(PixelFormat.Mono10Packed, PixelPacking.Gvsp10Packed)]
    [InlineData(PixelFormat.BayerBG10Packed, PixelPacking.Gvsp10Packed)]
    [InlineData(PixelFormat.Mono12Packed, PixelPacking.Gvsp12Packed)]
    [InlineData(PixelFormat.BayerGR12Packed, PixelPacking.Gvsp12Packed)]
    [InlineData(PixelFormat.Mono10p, PixelPacking.Pfnc10p)]
    [InlineData(PixelFormat.BayerRG10p, PixelPacking.Pfnc10p)]
    [InlineData(PixelFormat.Mono12p, PixelPacking.Pfnc12p)]
    [InlineData(PixelFormat.BayerGB12p, PixelPacking.Pfnc12p)]
    [InlineData(PixelFormat.Mono14p, PixelPacking.Pfnc14p)]
    [InlineData(PixelFormat.Mono1p, PixelPacking.Pfnc1p)]
    [InlineData(PixelFormat.Confidence1p, PixelPacking.Pfnc1p)]
    [InlineData(PixelFormat.Mono8, PixelPacking.None)]
    [InlineData(PixelFormat.Mono12, PixelPacking.None)]
    [InlineData(PixelFormat.RGB565p, PixelPacking.Other)]
    [InlineData(PixelFormat.RGB10V1Packed, PixelPacking.Other)]
    public void PackingKinds(PixelFormat f, PixelPacking packing)
    {
        Assert.Equal(packing, PixelFormatInfo.Packing(f));
    }

    [Fact]
    public void NameOfUnknownCodeIsHex()
    {
        Assert.Equal("0x810C0001", PixelFormatInfo.Name(0x810C0001u));
        Assert.Equal("Unknown", PixelFormatInfo.Name(0u));
        Assert.Equal("Unknown", PixelFormatInfo.Name(PixelFormat.Unknown));
        Assert.Equal("BayerRG12Packed", PixelFormatInfo.Name(0x010C002Bu));
    }

    [Fact]
    public void TryParseRoundTripsEveryName()
    {
        foreach (PixelFormat f in Enum.GetValues(typeof(PixelFormat)))
        {
            var name = PixelFormatInfo.Name(f);
            Assert.True(PixelFormatInfo.TryParse(name, out var back), name);
            Assert.Equal(f, back);
            Assert.Equal(f, PixelFormatInfo.Parse(name));
        }
    }

    [Fact]
    public void TryParseAcceptsCaseInsensitiveNamesAndHex()
    {
        Assert.True(PixelFormatInfo.TryParse("mono12packed", out var a));
        Assert.Equal(PixelFormat.Mono12Packed, a);
        Assert.True(PixelFormatInfo.TryParse("  BayerRG8 ", out var b));
        Assert.Equal(PixelFormat.BayerRG8, b);
        Assert.True(PixelFormatInfo.TryParse("0x01080001", out var c));
        Assert.Equal(PixelFormat.Mono8, c);
        Assert.True(PixelFormatInfo.TryParse("0X810C0001", out var d));
        Assert.Equal(0x810C0001u, (uint)d);
        Assert.True(PixelFormatInfo.TryParse("0x0", out var e));
        Assert.Equal(PixelFormat.Unknown, e);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mono9")]
    [InlineData("17301505")]
    [InlineData("0x")]
    [InlineData("0xZZ")]
    public void TryParseRejectsGarbage(string? text)
    {
        Assert.False(PixelFormatInfo.TryParse(text, out var f));
        Assert.Equal(PixelFormat.Unknown, f);
        if (text is not null) Assert.Throws<GevException>(() => PixelFormatInfo.Parse(text));
    }

    [Theory]
    [InlineData(PixelFormat.Mono8, 640, 640)]
    [InlineData(PixelFormat.Mono12, 640, 1280)]
    [InlineData(PixelFormat.Mono16, 641, 1282)]
    [InlineData(PixelFormat.RGB8, 640, 1920)]
    [InlineData(PixelFormat.RGBa8, 7, 28)]
    [InlineData(PixelFormat.Mono12Packed, 640, 960)]
    [InlineData(PixelFormat.Mono12Packed, 641, 963)]     // 홀수 폭: 마지막 묶음 3바이트 전부
    [InlineData(PixelFormat.Mono10Packed, 3, 6)]
    [InlineData(PixelFormat.Mono10Packed, 1, 3)]
    [InlineData(PixelFormat.BayerGB12Packed, 5, 9)]
    [InlineData(PixelFormat.Mono12p, 640, 960)]
    [InlineData(PixelFormat.Mono12p, 3, 5)]              // 36비트 → 5바이트
    [InlineData(PixelFormat.Mono12p, 1, 2)]
    [InlineData(PixelFormat.Mono10p, 4, 5)]
    [InlineData(PixelFormat.Mono10p, 5, 7)]
    [InlineData(PixelFormat.Mono10p, 6, 8)]
    [InlineData(PixelFormat.Mono10p, 7, 9)]
    [InlineData(PixelFormat.BayerBG10p, 1, 2)]
    [InlineData(PixelFormat.Mono14p, 4, 7)]
    [InlineData(PixelFormat.Mono1p, 9, 2)]
    [InlineData(PixelFormat.Mono4p, 3, 2)]
    [InlineData(PixelFormat.YUV411_8_UYYVYY, 8, 12)]
    [InlineData(PixelFormat.YUV411_8_UYYVYY, 5, 12)]     // 4:1:1 은 4픽셀 묶음 단위
    [InlineData(PixelFormat.YCbCr411_8, 4, 6)]
    [InlineData(PixelFormat.YUV422_8, 640, 1280)]
    [InlineData(PixelFormat.RGB565p, 10, 20)]
    [InlineData(PixelFormat.RGB10p32, 10, 40)]
    [InlineData(PixelFormat.Coord3D_ABC32f, 2, 24)]
    [InlineData(PixelFormat.Mono8, 0, 0)]
    public void LineBytes(PixelFormat f, int width, int bytes)
    {
        Assert.Equal(bytes, PixelFormatInfo.LineBytes(f, width));
        Assert.Equal(bytes, PixelFormatInfo.LineBytes((uint)f, width));
    }

    [Fact]
    public void LineBytesForUnknownCodeUsesBitsFromCode()
    {
        Assert.Equal(12, PixelFormatInfo.LineBytes(0x810C0001u, 8));     // 12 bpp, 표에 없음 → 비트 공식
        Assert.Equal(5, PixelFormatInfo.LineBytes(0x810C0001u, 3));      // 묶음 예외는 표에 있는 코드에만 적용
        Assert.Throws<GevException>(() => PixelFormatInfo.LineBytes(0u, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatInfo.LineBytes(PixelFormat.Mono8, -1));
        // int 를 넘는 줄은 OverflowException 이 아니라 치수 인자 예외
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatInfo.LineBytes(PixelFormat.Mono16, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatInfo.LineBytes(PixelFormat.Mono12Packed, 1_500_000_000));
    }

    [Fact]
    public void LineBytesLongFollowsTheSameRuleWithoutOverflow()
    {
        // 리더의 u32 폭을 그대로 넣는 자리 — 같은 규칙, long, 예외 없음(bpp 0 은 0).
        Assert.Equal(963L, PixelFormatInfo.LineBytesLong((uint)PixelFormat.Mono12Packed, 641));
        Assert.Equal(6L, PixelFormatInfo.LineBytesLong((uint)PixelFormat.Mono10Packed, 3));
        Assert.Equal(12L, PixelFormatInfo.LineBytesLong((uint)PixelFormat.YUV411_8_UYYVYY, 5));
        Assert.Equal(5L, PixelFormatInfo.LineBytesLong((uint)PixelFormat.Mono12p, 3));
        Assert.Equal(5L, PixelFormatInfo.LineBytesLong(0x810C0001u, 3));
        Assert.Equal(((long)uint.MaxValue * 16 + 7) / 8, PixelFormatInfo.LineBytesLong((uint)PixelFormat.Mono16, uint.MaxValue));
        Assert.Equal(((long)uint.MaxValue + 1) / 2 * 3, PixelFormatInfo.LineBytesLong((uint)PixelFormat.Mono12Packed, uint.MaxValue));
        Assert.Equal(0L, PixelFormatInfo.LineBytesLong(0u, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatInfo.LineBytesLong((uint)PixelFormat.Mono8, -1));
    }

    [Theory]
    [InlineData(PixelFormat.Mono8, 8)]
    [InlineData(PixelFormat.Mono10, 10)]
    [InlineData(PixelFormat.Mono10Packed, 10)]   // bpp 는 12 지만 깊이는 10
    [InlineData(PixelFormat.Mono10p, 10)]
    [InlineData(PixelFormat.Mono12, 12)]
    [InlineData(PixelFormat.Mono12Packed, 12)]
    [InlineData(PixelFormat.Mono14, 14)]
    [InlineData(PixelFormat.Mono14p, 14)]
    [InlineData(PixelFormat.Mono16, 16)]
    [InlineData(PixelFormat.Mono32, 32)]
    [InlineData(PixelFormat.Mono1p, 1)]
    [InlineData(PixelFormat.Mono4p, 4)]
    [InlineData(PixelFormat.BayerGR12, 12)]
    [InlineData(PixelFormat.BayerBG10Packed, 10)]
    [InlineData(PixelFormat.BayerGB14p, 14)]
    [InlineData(PixelFormat.Mono8s, 0)]          // 부호 있음
    [InlineData(PixelFormat.RGB8, 0)]
    [InlineData(PixelFormat.RGB12, 0)]
    [InlineData(PixelFormat.YUV422_8, 0)]
    [InlineData(PixelFormat.Coord3D_A16, 0)]
    [InlineData(PixelFormat.Confidence8, 0)]
    [InlineData(PixelFormat.Data16, 0)]
    [InlineData(PixelFormat.Unknown, 0)]
    public void DepthIsTheNamedBitDepthOfUnsignedMonoAndBayer(PixelFormat f, int depth)
    {
        Assert.Equal(depth, PixelFormatInfo.Depth(f));
        Assert.Equal(depth, PixelFormatInfo.Depth((uint)f));
    }

    [Fact]
    public void DepthOfUnknownCodeIsZero()
    {
        Assert.Equal(0, PixelFormatInfo.Depth(0x810C0001u));
        Assert.Equal(0, PixelFormatInfo.Depth(0x01100003u ^ 0x00000100u));
    }

    [Fact]
    public void FrameBytesAddsPadding()
    {
        Assert.Equal(480L * (640 + 4) + 16, PixelFormatInfo.FrameBytes(PixelFormat.Mono8, 640, 480, 4, 16));
        // 줄 패딩이 있으면 줄마다 마지막 묶음을 채운다 — 홀수 폭 963 바이트 줄 × 480.
        Assert.Equal(480L * (963 + 4), PixelFormatInfo.FrameBytes(PixelFormat.Mono12Packed, 641, 480, 4));
        // 패딩이 없으면 묶음이 줄에서 끊기지 않는다 — 641 × 480 = 307,680 픽셀 = 153,840 묶음 × 3.
        Assert.Equal(153_840L * 3, PixelFormatInfo.FrameBytes(PixelFormat.Mono12Packed, 641, 480));
        Assert.NotEqual(480L * 963, PixelFormatInfo.FrameBytes(PixelFormat.Mono12Packed, 641, 480));
        // 실측 기하 — 2591 × 64 12비트 packed 은 248,736 바이트이지 줄마다 올린 248,832 가 아니다.
        Assert.Equal(248_736L, PixelFormatInfo.FrameBytes(PixelFormat.Mono12Packed, 2591, 64));
        Assert.Equal(0L, PixelFormatInfo.FrameBytes(PixelFormat.Mono8, 640, 0));
        Assert.Equal(0L, PixelFormatInfo.FrameBytes(PixelFormat.RGB8, 0, 480));
        // 큰 프레임은 int 를 넘어도 long 으로 그대로 나온다.
        Assert.Equal(20000L * 20000 * 12, PixelFormatInfo.FrameBytes(PixelFormat.Coord3D_ABC32f, 20000, 20000));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatInfo.FrameBytes(PixelFormat.Mono8, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatInfo.FrameBytes(PixelFormat.Mono8, 1, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelFormatInfo.FrameBytes(PixelFormat.Mono8, 1, 1, 0, -1));
    }
}
