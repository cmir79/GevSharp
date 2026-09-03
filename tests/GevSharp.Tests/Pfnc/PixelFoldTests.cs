using System.Text.RegularExpressions;
using GevSharp.Gvsp;
using GevSharp.Pfnc;

namespace GevSharp.Tests.Pfnc;

/// <summary>
/// 8비트 접기 검증 — 손으로 계산한 패턴, 테스트 쪽 독립 패커로 만든 무작위 픽셀(기대값 = 픽셀 &gt;&gt; (깊이 − 8)),
/// 무작위 바이트에 대한 언팩 → 시프트 동치, 버퍼·치수 검사, 지원 목록.
/// 패커와 깊이 분류는 라이브러리 코드를 쓰지 않고 PFNC 이름만 보고 정한다.
/// </summary>
public class PixelFoldTests
{
    // ---------- 손으로 계산한 패턴 ----------

    [Fact]
    public void Mono8_CopiesRowsHonouringStrides()
    {
        // 폭 3, 높이 2, 소스 줄 간격 5(쓰레기 2바이트), 목적지 줄 간격 4
        var src = new byte[] { 1, 2, 3, 0xEE, 0xEE, 4, 5, 6 };
        var dst = new byte[8];
        dst.AsSpan().Fill((byte)0xFF);
        PixelUnpack.FoldToMono8((uint)PixelFormat.Mono8, src, 5, dst, 4, 3, 2);
        Assert.Equal(new byte[] { 1, 2, 3, 0xFF, 4, 5, 6, 0xFF }, dst);

        // 빈틈없는 버퍼끼리(한 번에 복사하는 경로)
        var tight = new byte[6];
        PixelUnpack.FoldToMono8((uint)PixelFormat.BayerRG8, new byte[] { 1, 2, 3, 4, 5, 6 }, 3, tight, 3, 3, 2);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, tight);
    }

    [Fact]
    public void Unpacked16_TakesTopBitsOfTheDepth()
    {
        // 12비트 0xABC → 0xAB
        Assert.Equal(new byte[] { 0xAB }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12, new byte[] { 0xBC, 0x0A }, 2, 1, 1));
        // 10비트 0x2AD = 10 1010 1101 → 상위 8비트 1010 1011 = 0xAB
        Assert.Equal(new byte[] { 0xAB }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono10, new byte[] { 0xAD, 0x02 }, 2, 1, 1));
        // 14비트 0x1234 → >> 6 = 0x48, 0x3FFF → 0xFF
        Assert.Equal(new byte[] { 0x48, 0xFF }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono14, new byte[] { 0x34, 0x12, 0xFF, 0x3F }, 4, 2, 1));
        // 16비트 0xABCD → 상위 바이트 0xAB
        Assert.Equal(new byte[] { 0xAB }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono16, new byte[] { 0xCD, 0xAB }, 2, 1, 1));
    }

    [Fact]
    public void Unpacked16_IgnoresContainerBitsAboveTheDepth()
    {
        // Mono12 컨테이너의 상위 니블에 쓰레기(0xF)가 있어도 12비트 값 0xABC 의 상위 8비트 0xAB 만 남는다.
        Assert.Equal(new byte[] { 0xAB }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12, new byte[] { 0xBC, 0xFA }, 2, 1, 1));
        // Mono10: 0xFEAD 의 하위 10비트 = 0x2AD → 0xAB
        Assert.Equal(new byte[] { 0xAB }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono10, new byte[] { 0xAD, 0xFE }, 2, 1, 1));
        // Mono14: 0xD234 의 하위 14비트 = 0x1234 → 0x48
        Assert.Equal(new byte[] { 0x48 }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono14, new byte[] { 0x34, 0xD2 }, 2, 1, 1));
    }

    [Fact]
    public void Gvsp12Packed_HandPattern()
    {
        // P0 = 0xABD, P1 = 0xEFC → 상위 8비트 0xAB, 0xEF (= 묶음의 첫째·셋째 바이트)
        Assert.Equal(new byte[] { 0xAB, 0xEF }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12Packed, new byte[] { 0xAB, 0xCD, 0xEF }, 3, 2, 1));
    }

    [Fact]
    public void Gvsp10Packed_HandPattern()
    {
        // P0 = 0x2AD → 0xAB, P1 = 0x3BE → 0xEF; 가운데 바이트가 무엇이든 상위 8비트는 그대로
        Assert.Equal(new byte[] { 0xAB, 0xEF }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono10Packed, new byte[] { 0xAB, 0x21, 0xEF }, 3, 2, 1));
        Assert.Equal(new byte[] { 0xAB, 0xEF }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono10Packed, new byte[] { 0xAB, 0xFF, 0xEF }, 3, 2, 1));
    }

    [Fact]
    public void Pfnc12p_HandPattern()
    {
        // P0 = 0xDAB → 0xDA, P1 = 0xEFC → 0xEF
        Assert.Equal(new byte[] { 0xDA, 0xEF }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12p, new byte[] { 0xAB, 0xCD, 0xEF }, 3, 2, 1));
    }

    [Fact]
    public void Pfnc10p_HandPattern()
    {
        // 픽셀 0x123, 0x2AB, 0x0F0, 0x3C5 → >> 2 = 0x48, 0xAA, 0x3C, 0xF1
        Assert.Equal(new byte[] { 0x48, 0xAA, 0x3C, 0xF1 },
            PixelUnpack.FoldToMono8((uint)PixelFormat.Mono10p, new byte[] { 0x23, 0xAD, 0x0A, 0x4F, 0xF1 }, 5, 4, 1));
    }

    [Fact]
    public void SameBytesFoldDifferentlyUnderGvspAndPfncLayouts()
    {
        var bytes = new byte[] { 0x12, 0x34, 0x56 };
        Assert.Equal(new byte[] { 0x12, 0x56 }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12Packed, bytes, 3, 2, 1));   // 0x124, 0x563
        Assert.Equal(new byte[] { 0x41, 0x56 }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12p, bytes, 3, 2, 1));        // 0x412, 0x563
    }

    // ---------- Bayer: 같은 바이트면 Mono 와 같은 결과(모자이크 유지) ----------

    public static TheoryData<PixelFormat, PixelFormat> BayerMonoPairs()
    {
        var data = new TheoryData<PixelFormat, PixelFormat>();
        foreach (var f in Foldable())
        {
            var name = f.ToString();
            if (!name.StartsWith("Bayer", StringComparison.Ordinal)) continue;
            var mono = PixelFormatInfo.Parse("Mono" + name.Substring("BayerGR".Length));
            data.Add(f, mono);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(BayerMonoPairs))]
    public void BayerKeepsMosaic_SameBytesAsMono(PixelFormat bayer, PixelFormat mono)
    {
        const int width = 7, height = 3;
        var stride = PixelFormatInfo.LineBytes(bayer, width) + 2;
        var src = new byte[(height - 1) * stride + PixelFormatInfo.LineBytes(bayer, width)];
        new Random(unchecked((int)bayer)).NextBytes(src);

        var a = PixelUnpack.FoldToMono8((uint)bayer, src, stride, width, height);
        var b = PixelUnpack.FoldToMono8((uint)mono, src, stride, width, height);
        Assert.Equal(b, a);
        Assert.Equal(width * height, a.Length);
    }

    // ---------- 독립 패커: 무작위 픽셀 → 접기 == 픽셀 >> (깊이 − 8), 홀수 폭·줄 간격·복수 줄 ----------

    public static TheoryData<PixelFormat, int> FoldableShapes()
    {
        var data = new TheoryData<PixelFormat, int>();
        foreach (var f in Foldable())
            foreach (var w in new[] { 1, 3, 4, 5, 8, 65 })
                data.Add(f, w);
        return data;
    }

    [Theory]
    [MemberData(nameof(FoldableShapes))]
    public void FoldsRandomPixelsWithStrides(PixelFormat format, int width)
    {
        const int height = 3;
        var (family, depth) = Classify(format.ToString());
        var rng = new Random(unchecked((int)format) ^ (width * 7919));
        var pixels = new ushort[width * height];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (ushort)rng.Next(0, 1 << depth);

        var lineBytes = LineBytes(family, depth, width);
        var srcStride = lineBytes + 3;                       // 줄 끝에 쓰레기 3바이트
        var src = Pack(family, depth, pixels, width, height, srcStride);
        Assert.Equal(lineBytes, PixelFormatInfo.LineBytes(format, width));

        var dstStride = width + 2;                           // 목적지도 여분 2바이트
        var dst = new byte[height * dstStride];
        dst.AsSpan().Fill((byte)0xFF);

        PixelUnpack.FoldToMono8((uint)format, src, srcStride, dst, dstStride, width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                Assert.Equal((byte)(pixels[y * width + x] >> (depth - 8)), dst[y * dstStride + x]);
            Assert.Equal((byte)0xFF, dst[y * dstStride + width]);              // 여분 바이트는 손대지 않는다
            Assert.Equal((byte)0xFF, dst[y * dstStride + width + 1]);
        }

        // 배열 오버로드는 같은 값을 빈틈없이 돌려준다.
        var tight = PixelUnpack.FoldToMono8((uint)format, src, srcStride, width, height);
        Assert.Equal(width * height, tight.Length);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Assert.Equal(dst[y * dstStride + x], tight[y * width + x]);
    }

    // ---------- 무작위 바이트: 접기 == 언팩 → 시프트 ----------

    public static TheoryData<PixelFormat, int> PackedShapes()
    {
        var data = new TheoryData<PixelFormat, int>();
        foreach (var f in Foldable())
            if (PixelUnpack.CanUnpack((uint)f))
                foreach (var w in new[] { 1, 2, 3, 5, 8, 9, 64, 65 })
                    data.Add(f, w);
        return data;
    }

    [Theory]
    [MemberData(nameof(PackedShapes))]
    public void FoldEqualsUnpackThenShift(PixelFormat format, int width)
    {
        const int height = 2;
        var (_, depth) = Classify(format.ToString());
        var lineBytes = PixelFormatInfo.LineBytes(format, width);
        var stride = lineBytes + 1;
        var src = new byte[(height - 1) * stride + lineBytes];
        new Random(unchecked((int)format) * 31 + width).NextBytes(src);

        var unpacked = new ushort[width * height];
        PixelUnpack.Unpack((uint)format, src, stride, unpacked, width, width, height);
        var expected = new byte[unpacked.Length];
        for (var i = 0; i < expected.Length; i++) expected[i] = (byte)(unpacked[i] >> (depth - 8));

        var folded = new byte[width * height];
        PixelUnpack.FoldToMono8((uint)format, src, stride, folded, width, width, height);
        Assert.Equal(expected, folded);
    }

    // ---------- 버퍼·치수 검사 ----------

    public static TheoryData<PixelFormat> OnePerFamily() => new()
    {
        PixelFormat.Mono8, PixelFormat.Mono12, PixelFormat.Mono10Packed, PixelFormat.Mono12Packed, PixelFormat.Mono10p, PixelFormat.Mono12p,
    };

    [Theory]
    [MemberData(nameof(OnePerFamily))]
    public void ExactBufferSizesAreAccepted(PixelFormat format)
    {
        // 딱 맞는 크기: 마지막 줄은 줄 간격이 아니라 줄 바이트만 있으면 된다.
        const int width = 3, height = 3;
        var lineBytes = PixelFormatInfo.LineBytes(format, width);
        var srcStride = lineBytes + 2;
        var src = new byte[(height - 1) * srcStride + lineBytes];
        var dst = new byte[(height - 1) * 4 + width];
        PixelUnpack.FoldToMono8((uint)format, src, srcStride, dst, 4, width, height);
    }

    [Theory]
    [MemberData(nameof(OnePerFamily))]
    public void UndersizedBuffersThrow(PixelFormat format)
    {
        const int width = 3, height = 3;
        var lineBytes = PixelFormatInfo.LineBytes(format, width);
        var srcStride = lineBytes + 2;
        var okSrc = new byte[(height - 1) * srcStride + lineBytes];
        var okDst = new byte[(height - 1) * 4 + width];

        var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)format, new byte[okSrc.Length - 1], srcStride, okDst, 4, width, height));
        Assert.Equal("src", ex.ParamName);
        Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)format, Array.Empty<byte>(), srcStride, okDst, 4, width, height));

        var ex2 = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)format, okSrc, srcStride, new byte[okDst.Length - 1], 4, width, height));
        Assert.Equal("dst", ex2.ParamName);

        // 배열 오버로드도 소스가 모자라면 같은 예외
        var ex3 = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)format, new byte[okSrc.Length - 1], srcStride, width, height));
        Assert.Equal("src", ex3.ParamName);
    }

    [Theory]
    [MemberData(nameof(OnePerFamily))]
    public void StrideShorterThanLineThrows(PixelFormat format)
    {
        var src = new byte[100];
        var dst = new byte[100];
        const int width = 4;
        var lineBytes = PixelFormatInfo.LineBytes(format, width);
        var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)format, src, lineBytes - 1, dst, width, width, 2));
        Assert.Equal("srcStrideBytes", ex.ParamName);
        var ex2 = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)format, src, lineBytes, dst, width - 1, width, 2));
        Assert.Equal("dstStrideBytes", ex2.ParamName);
    }

    [Fact]
    public void NegativeDimensionsThrowAndZeroDimensionsDoNothing()
    {
        var src = new byte[16];
        var dst = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12p, src, 8, dst, 8, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono8, src, 8, dst, 8, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12, src, 8, -2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12, src, 8, 2, -1));

        dst.AsSpan().Fill((byte)7);
        PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12p, src, 8, dst, 8, 0, 4);
        PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12, src, 8, dst, 8, 4, 0);
        PixelUnpack.FoldToMono8((uint)PixelFormat.Mono8, Array.Empty<byte>(), 0, Array.Empty<byte>(), 0, 0, 0);
        Assert.All(dst, v => Assert.Equal((byte)7, v));
        Assert.Empty(PixelUnpack.FoldToMono8((uint)PixelFormat.Mono10Packed, src, 8, 0, 3));
        Assert.Empty(PixelUnpack.FoldToMono8((uint)PixelFormat.Mono16, src, 8, 3, 0));

        // 폭·높이가 0 이어도 줄 간격은 검사한다 — 쓰레기 간격을 조용히 받지 않는다.
        var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12, src, -99, dst, -99, 0, 4));
        Assert.Equal("srcStrideBytes", ex.ParamName);
        var ex2 = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12, src, 8, dst, -1, 4, 0));
        Assert.Equal("dstStrideBytes", ex2.ParamName);
        Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12Packed, src, 5, dst, 4, 4, 0));   // 폭 4 = 6바이트
        Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono8, src, -1, 0, 3));
    }

    [Fact]
    public void OversizedDimensionsThrowArgumentOutOfRange()
    {
        // int 를 넘는 줄·배열은 OverflowException 이 아니라 치수 인자 예외 — 호출자가 치수 오류를 ArgumentException 한 갈래로 잡는다.
        var src = new byte[16];
        var dst = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono16, src, 8, dst, 8, 1_500_000_000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12Packed, src, 8, dst, 8, 1_500_000_000, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono10p, src, 8, dst, 8, int.MaxValue, 1));
        // 배열 오버로드는 할당하기 전에 거른다.
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono8, src, 8, 100_000, 100_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12p, src, 8, 1_500_000_000, 2));
    }

    // ---------- 수신기의 줄 간격(GevFrame.Stride)과의 일치 ----------

    [Theory]
    [InlineData(PixelFormat.Mono12Packed, 641, 4)]
    [InlineData(PixelFormat.Mono10Packed, 3, 0)]
    [InlineData(PixelFormat.BayerRG12Packed, 5, 1)]
    [InlineData(PixelFormat.Mono12p, 3, 0)]
    [InlineData(PixelFormat.Mono10p, 5, 2)]
    [InlineData(PixelFormat.Mono12, 7, 0)]
    public void LeaderStrideFeedsFoldAndUnpackDirectly(PixelFormat format, int width, int paddingX)
    {
        // 수신기가 GevFrame.Stride 로 내보내는 값을 그대로 넘겨도 거부하지 않는다 — 홀수 폭에서 정의가 갈리면 여기서 잡힌다.
        // 줄이 바이트 경계에서 끝나지 않고 줄 패딩도 없으면 수신기는 Stride 0("줄 간격이 없다")을 내보내고,
        // 소비자는 이미지를 "폭 = 전체 픽셀 수, 높이 1" 인 한 덩어리로 다뤄야 한다.
        const int height = 3;
        var leader = new GvspImageLeader(0, GvspConst.PayloadImage, 0, (uint)format, (uint)width, height, 0, 0, (ushort)paddingX, 0);
        Assert.Equal(PixelFormatInfo.LineBytes(format, width) + paddingX, (int)leader.LineBytes);
        var stride = paddingX == 0 && !leader.IsLineByteAligned ? 0 : (int)leader.LineBytes;

        var src = new byte[leader.ImageBytes];
        new Random(width).NextBytes(src);
        var runWidth = stride == 0 ? width * height : width;
        var runHeight = stride == 0 ? 1 : height;
        var runStride = stride == 0 ? src.Length : stride;

        var dst = new byte[width * height];
        PixelUnpack.FoldToMono8((uint)format, src, runStride, dst, runWidth, runWidth, runHeight);
        Assert.Equal(dst, PixelUnpack.FoldToMono8((uint)format, src, runStride, runWidth, runHeight));
        if (PixelUnpack.CanUnpack((uint)format))
        {
            PixelUnpack.Unpack((uint)format, src, runStride, new ushort[width * height], runWidth, runWidth, runHeight);
            // 편의 오버로드는 같은 판단을 스스로 한다 — 호출자가 다시 하지 않아도 같은 결과여야 한다.
            Assert.Equal(width * height, PixelUnpack.UnpackToArray((uint)format, src, width, height, paddingX).Length);
        }
    }

    // ---------- 지원 목록 ----------

    [Fact]
    public void CanFoldMatchesTheDocumentedFamilies()
    {
        // 접을 수 있는 것: Mono/Bayer 8·10·12·14·16, 10p·12p, 10Packed·12Packed. 그 밖의 표 항목은 전부 false.
        var expected = new Regex(@"^(Mono|Bayer(GR|RG|GB|BG))(8|10|12|14|16|10p|12p|10Packed|12Packed)$");
        var count = 0;
        foreach (PixelFormat f in Enum.GetValues(typeof(PixelFormat)))
        {
            var should = expected.IsMatch(f.ToString());
            Assert.True(should == PixelUnpack.CanFoldToMono8((uint)f), $"{f}: expected CanFoldToMono8 = {should}");
            if (should) count++;
        }
        Assert.Equal(45, count);   // Mono 9 + Bayer 4 × 9
    }

    [Fact]
    public void TableDepthMatchesTheNameAndByteAlignedRowsFold()
    {
        // 표의 Mono/Bayer 행은 이름이 말하는 깊이를 그대로 갖고(Mono10Packed 도 10), 바이트 정렬 8..16비트 행은 전부 접힌다 —
        // 행을 넣으면서 깊이를 빠뜨리면 조용히 접히지 않는 게 아니라 여기서 걸린다.
        var named = new Regex(@"^(?:Mono|Bayer(?:GR|RG|GB|BG))(\d+)(p|Packed)?$");
        var foldable = 0;
        foreach (PixelFormat f in Enum.GetValues(typeof(PixelFormat)))
        {
            var code = (uint)f;
            if (!PixelFormatInfo.IsMono(code) && !PixelFormatInfo.IsBayer(code)) continue;
            var m = named.Match(f.ToString());
            if (!m.Success) continue;   // Mono8s: 부호 있는 변형은 깊이 0
            var depth = int.Parse(m.Groups[1].Value);
            Assert.Equal(depth, PixelFormatInfo.Depth(code));
            if (m.Groups[2].Length == 0 && depth >= 8 && depth <= 16)
            {
                Assert.True(PixelUnpack.CanFoldToMono8(code), $"{f}: byte-aligned {depth}-bit row must fold");
                foldable++;
            }
        }
        Assert.Equal(25, foldable);   // Mono 5 + Bayer 4 × 5
        Assert.Equal(0, PixelFormatInfo.Depth(PixelFormat.Mono8s));
        Assert.False(PixelUnpack.CanFoldToMono8((uint)PixelFormat.Mono32));   // 깊이 32 — 16비트 컨테이너가 아니다
    }

    [Fact]
    public void UnsupportedCodesAreRejected()
    {
        var codes = new[]
        {
            (uint)PixelFormat.Mono8s, (uint)PixelFormat.Mono1p, (uint)PixelFormat.Mono14p, (uint)PixelFormat.Mono32,
            (uint)PixelFormat.BayerGR14p, (uint)PixelFormat.RGB8, (uint)PixelFormat.YUV422_8, (uint)PixelFormat.Coord3D_A16,
            (uint)PixelFormat.Confidence8, (uint)PixelFormat.Data16, 0u, 0x810C0001u, 0x01100003u ^ 0x00000100u,
        };
        var src = new byte[64];
        var dst = new byte[64];
        foreach (var code in codes)
        {
            Assert.False(PixelUnpack.CanFoldToMono8(code), $"0x{code:X8}");
            var ex = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8(code, src, 8, dst, 4, 4, 2));
            Assert.Equal("code", ex.ParamName);
            var ex2 = Assert.Throws<ArgumentException>(() => PixelUnpack.FoldToMono8(code, src, 8, 4, 2));
            Assert.Equal("code", ex2.ParamName);
        }
    }

    // ---------- 배열 오버로드 ----------

    [Fact]
    public void ArrayOverloadIsTightlyPackedAndHonoursSourceStride()
    {
        // Mono12, 폭 2, 높이 2, 소스 줄 간격 6(패딩 2바이트): 0x123 0x456 / 0x789 0xABC
        var src = new byte[] { 0x23, 0x01, 0x56, 0x04, 0xEE, 0xEE, 0x89, 0x07, 0xBC, 0x0A };
        Assert.Equal(new byte[] { 0x12, 0x45, 0x78, 0xAB }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono12, src, 6, 2, 2));

        // Mono8 줄 간격 있는 경로
        Assert.Equal(new byte[] { 1, 2, 4, 5 }, PixelUnpack.FoldToMono8((uint)PixelFormat.Mono8, new byte[] { 1, 2, 3, 4, 5 }, 3, 2, 2));
    }

    // ---------- 테스트 쪽 독립 분류·패커(PFNC 이름만 본다) ----------

    private enum Family { Byte, Le16, GvspPacked, PfncP }

    private static readonly Regex NamePattern = new(@"^(?:Mono|Bayer(?:GR|RG|GB|BG))(\d+)(p|Packed)?$");

    private static IEnumerable<PixelFormat> Foldable()
    {
        foreach (PixelFormat f in Enum.GetValues(typeof(PixelFormat)))
            if (PixelUnpack.CanFoldToMono8((uint)f)) yield return f;
    }

    private static (Family family, int depth) Classify(string name)
    {
        var m = NamePattern.Match(name);
        if (!m.Success) throw new InvalidOperationException($"Not a foldable name: {name}");
        var depth = int.Parse(m.Groups[1].Value);
        var family = m.Groups[2].Value switch
        {
            "Packed" => Family.GvspPacked,
            "p" => Family.PfncP,
            _ => depth == 8 ? Family.Byte : Family.Le16,
        };
        return (family, depth);
    }

    private static int LineBytes(Family family, int depth, int width) => family switch
    {
        Family.Byte => width,
        Family.Le16 => width * 2,
        Family.GvspPacked => (width + 1) / 2 * 3,
        _ => (width * depth + 7) / 8,
    };

    /// <summary>
    /// 줄마다 stride 바이트 간격으로 픽셀을 채운다. 줄 밖·패딩 자리는 0xEE. 깊이 위의 컨테이너 비트(Le16 의 남는 상위 비트,
    /// GVSP 10Packed 가운데 바이트의 패딩 비트, PFNC p 마지막 바이트의 남는 비트)는 1로 세워 무시되는지 본다.
    /// </summary>
    private static byte[] Pack(Family family, int depth, ushort[] pixels, int width, int height, int stride)
    {
        var lineBytes = LineBytes(family, depth, width);
        var buf = new byte[(height - 1) * stride + lineBytes];
        buf.AsSpan().Fill((byte)0xEE);
        for (var y = 0; y < height; y++)
        {
            var o = y * stride;
            switch (family)
            {
                case Family.Byte:
                    for (var x = 0; x < width; x++) buf[o + x] = (byte)pixels[y * width + x];
                    break;
                case Family.Le16:
                    for (var x = 0; x < width; x++)
                    {
                        var v = pixels[y * width + x] | (0xFFFF << depth);   // 깊이 위는 전부 1
                        buf[o + 2 * x] = (byte)v;
                        buf[o + 2 * x + 1] = (byte)(v >> 8);
                    }
                    break;
                case Family.GvspPacked:
                {
                    var shift = depth - 8;
                    var lowMask = (1 << shift) - 1;
                    var padBits = depth == 10 ? 0xCC : 0x00;
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
                    break;
                }
                default:
                {
                    Array.Clear(buf, o, lineBytes);
                    long bit = 0;
                    for (var x = 0; x < width; x++)
                    {
                        int v = pixels[y * width + x];
                        for (var b = 0; b < depth; b++, bit++)
                            if (((v >> b) & 1) != 0) buf[o + (int)(bit >> 3)] |= (byte)(1 << (int)(bit & 7));
                    }
                    for (; bit < (long)lineBytes * 8; bit++)
                        buf[o + (int)(bit >> 3)] |= (byte)(1 << (int)(bit & 7));
                    break;
                }
            }
        }
        return buf;
    }
}
