using System.Runtime.CompilerServices;

namespace GevSharp.Pfnc;

/// <summary>
/// 비트 패킹된 단일 성분 포맷(10/12비트, GVSP Packed 와 PFNC p)을 픽셀당 <see cref="ushort"/> 로 편다.
/// 값은 원 비트 수 그대로 둔다(10비트면 0..1023) — 16비트로 늘리는 스케일링은 하지 않는다.
/// 줄 단위로 동작한다: 소스 줄 간격(바이트)과 목적지 줄 간격(픽셀 수)을 따로 받아 PaddingX 가 있는 프레임을 복사 없이 다룬다.
/// Bayer 변형은 바이트 배치가 Mono 와 같으므로 같은 루틴으로 간다 — <see cref="Unpack(uint, ReadOnlySpan{byte}, int, Span{ushort}, int, int, int)"/> 가 코드로 분기한다.
/// 버퍼가 모자라면 <see cref="ArgumentException"/> — 잘린 결과를 조용히 내지 않는다.
/// 픽셀당 상위 8비트만 필요한 소비자는 <see cref="FoldToMono8(uint, ReadOnlySpan{byte}, int, Span{byte}, int, int, int)"/>(PixelUnpack.Fold.cs)를 쓴다 — 같은 묶음 디코더를 공유한다.
/// </summary>
public static partial class PixelUnpack
{
    // 구현한 바이트 배치(공개 자료 두 곳 이상에서 일치 확인). b0,b1,b2… 는 묶음 안의 바이트, P0,P1… 은 픽셀.
    //
    //  GVSP 12Packed (2픽셀 = 3바이트):  b0 = P0[11:4]            b1 = P1[3:0]<<4 | P0[3:0]                   b2 = P1[11:4]
    //  GVSP 10Packed (2픽셀 = 3바이트):  b0 = P0[9:2]             b1 = P1[1:0]<<4 | P0[1:0]  ([3:2]·[7:6] 패딩)  b2 = P1[9:2]
    //  PFNC 12p      (2픽셀 = 3바이트, LSB 우선):  P0 = b0 | (b1 & 0x0F)<<8        P1 = b1>>4 | b2<<4
    //  PFNC 10p      (4픽셀 = 5바이트, LSB 우선):  P0 = b0 | (b1 & 0x03)<<8        P1 = b1>>2 | (b2 & 0x0F)<<6
    //                                              P2 = b2>>4 | (b3 & 0x3F)<<4     P3 = b3>>6 | b4<<2
    //
    //  홀수 폭: 마지막 묶음은 있는 픽셀만큼의 바이트만 읽는다 — GVSP Packed 는 b2 가 패딩(줄은 3바이트 단위로 끝남),
    //  PFNC p 는 비트가 끝나는 바이트까지만(12p 홀수 = 2바이트, 10p 나머지 1/2/3 픽셀 = 2/3/4바이트).

    /// <summary>이 코드에 전용 언팩 루틴이 있는지(GVSP 10/12Packed, PFNC 10p/12p — Mono 와 Bayer 전부).</summary>
    public static bool CanUnpack(uint code)
    {
        if (!PixelFormatInfo.IsKnown(code)) return false;
        switch (PixelFormatInfo.Packing(code))
        {
            case PixelPacking.Gvsp10Packed:
            case PixelPacking.Gvsp12Packed:
            case PixelPacking.Pfnc10p:
            case PixelPacking.Pfnc12p:
                return true;
            default:
                return false;
        }
    }

    /// <summary>코드의 패킹 방식에 맞는 루틴으로 분기한다. 언팩 대상이 아닌 코드는 <see cref="ArgumentException"/>.</summary>
    public static void Unpack(uint code, ReadOnlySpan<byte> src, int srcStrideBytes, Span<ushort> dst, int dstStridePixels, int width, int height)
    {
        switch (SelectPacking(code))
        {
            case PixelPacking.Gvsp10Packed: Unpack10Packed(src, srcStrideBytes, dst, dstStridePixels, width, height); break;
            case PixelPacking.Gvsp12Packed: Unpack12Packed(src, srcStrideBytes, dst, dstStridePixels, width, height); break;
            case PixelPacking.Pfnc10p: Unpack10p(src, srcStrideBytes, dst, dstStridePixels, width, height); break;
            case PixelPacking.Pfnc12p: Unpack12p(src, srcStrideBytes, dst, dstStridePixels, width, height); break;
            default: throw UnhandledPacking(code);
        }
    }

    /// <summary>
    /// 새 <c>ushort[width × height]</c> 를 만들어 편다.
    /// <para>
    /// paddingX 가 있으면 줄이 저마다 따로 시작하므로 줄 간격은 <see cref="PixelFormatInfo.LineBytes(uint, int)"/> + paddingX 다.
    /// paddingX 가 0 이면 데이터가 줄에서 끊기지 않고 이어 붙으므로 전체를 한 덩어리로 푼다 — 줄이 바이트 경계에서
    /// 끝나지 않는 폭(홀수 폭 packed 등)에서는 다음 줄이 바이트 가운데에서 시작하기 때문에 줄 단위로 풀면 어긋난다.
    /// 줄이 바이트 경계에서 끝나는 폭이면 두 방식의 결과는 같다.
    /// </para>
    /// </summary>
    public static ushort[] UnpackToArray(uint code, ReadOnlySpan<byte> src, int width, int height, int paddingX = 0)
    {
        var packing = SelectPacking(code);
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        if (paddingX < 0) throw new ArgumentOutOfRangeException(nameof(paddingX), paddingX, "PaddingX must not be negative.");
        var pixels = PixelCount(width, height);

        // 이어진 한 덩어리는 "폭 = 전체 픽셀 수, 높이 1" 인 한 줄과 같다 — 같은 루틴에 그 모양으로 넘긴다.
        var runWidth = paddingX == 0 ? pixels : width;
        var runHeight = paddingX == 0 ? (width == 0 || height == 0 ? 0 : 1) : height;
        // 치수 검사를 목적지 할당보다 먼저 한다 — 나중에 하면 결국 거절할 치수 때문에 GB 단위 배열을 먼저 잡게 된다.
        var srcStride = LineInt((long)PixelFormatInfo.LineBytes(code, runWidth) + paddingX, runWidth);
        var dst = new ushort[pixels];
        switch (packing)
        {
            case PixelPacking.Gvsp10Packed: Unpack10Packed(src, srcStride, dst, runWidth, runWidth, runHeight); break;
            case PixelPacking.Gvsp12Packed: Unpack12Packed(src, srcStride, dst, runWidth, runWidth, runHeight); break;
            case PixelPacking.Pfnc10p: Unpack10p(src, srcStride, dst, runWidth, runWidth, runHeight); break;
            case PixelPacking.Pfnc12p: Unpack12p(src, srcStride, dst, runWidth, runWidth, runHeight); break;
            default: throw UnhandledPacking(code);
        }
        return dst;
    }

    private static PixelPacking SelectPacking(uint code)
    {
        if (!CanUnpack(code))
            throw new ArgumentException($"Pixel format {PixelFormatInfo.Name(code)} is not a 10/12-bit packed format; nothing to unpack.", nameof(code));
        return PixelFormatInfo.Packing(code);
    }

    // CanUnpack 이 true 라고 한 패킹에 루틴이 없다 — 지원 목록과 분기가 어긋난 것이므로 다른 루틴으로 조용히 보내지 않고 바로 낸다.
    private static ArgumentException UnhandledPacking(uint code)
        => new($"No unpack routine for packing {PixelFormatInfo.Packing(code)} of pixel format {PixelFormatInfo.Name(code)}.", nameof(code));

    // ---------- 묶음 디코더 ----------
    // 비트 배치를 아는 곳은 여기뿐이다. 언팩 루틴과 8비트 접기 루틴이 같은 디코더를 부르므로 배치가 틀리면 양쪽이 같이 틀리고 같이 잡힌다.
    // 인자는 0..255 바이트, 반환값은 원 비트 수 범위 안(10비트 0..1023, 12비트 0..4095) — 위쪽에 쓰레기 비트가 남지 않는다.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Gvsp12PackedP0(int b0, int b1) => (b0 << 4) | (b1 & 0x0F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Gvsp12PackedP1(int b1, int b2) => (b2 << 4) | (b1 >> 4);

    // 가운데 바이트의 패딩 비트([3:2]·[7:6])는 마스크로 버린다.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Gvsp10PackedP0(int b0, int b1) => (b0 << 2) | (b1 & 0x03);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Gvsp10PackedP1(int b1, int b2) => (b2 << 2) | ((b1 >> 4) & 0x03);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pfnc12pP0(int b0, int b1) => b0 | ((b1 & 0x0F) << 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pfnc12pP1(int b1, int b2) => (b1 >> 4) | (b2 << 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pfnc10pP0(int b0, int b1) => b0 | ((b1 & 0x03) << 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pfnc10pP1(int b1, int b2) => (b1 >> 2) | ((b2 & 0x0F) << 6);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pfnc10pP2(int b2, int b3) => (b2 >> 4) | ((b3 & 0x3F) << 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Pfnc10pP3(int b3, int b4) => (b3 >> 6) | (b4 << 2);

    // ---------- 언팩 루틴 ----------

    /// <summary>GVSP 12Packed(Mono12Packed·Bayer*12Packed) → 12비트 값.</summary>
    public static void Unpack12Packed(ReadOnlySpan<byte> src, int srcStrideBytes, Span<ushort> dst, int dstStridePixels, int width, int height)
    {
        var lineBytes = GvspPackedLineBytes(width);
        if (!Validate(src, srcStrideBytes, dst, dstStridePixels, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStridePixels, width);
            int x = 0, i = 0;
            for (; x + 1 < width; x += 2, i += 3)
            {
                int b0 = s[i], b1 = s[i + 1], b2 = s[i + 2];
                d[x] = (ushort)Gvsp12PackedP0(b0, b1);
                d[x + 1] = (ushort)Gvsp12PackedP1(b1, b2);
            }
            if (x < width)
                d[x] = (ushort)Gvsp12PackedP0(s[i], s[i + 1]);
        }
    }

    /// <summary>GVSP 10Packed(Mono10Packed·Bayer*10Packed) → 10비트 값. 가운데 바이트의 패딩 비트([3:2]·[7:6])는 무시한다.</summary>
    public static void Unpack10Packed(ReadOnlySpan<byte> src, int srcStrideBytes, Span<ushort> dst, int dstStridePixels, int width, int height)
    {
        var lineBytes = GvspPackedLineBytes(width);
        if (!Validate(src, srcStrideBytes, dst, dstStridePixels, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStridePixels, width);
            int x = 0, i = 0;
            for (; x + 1 < width; x += 2, i += 3)
            {
                int b0 = s[i], b1 = s[i + 1], b2 = s[i + 2];
                d[x] = (ushort)Gvsp10PackedP0(b0, b1);
                d[x + 1] = (ushort)Gvsp10PackedP1(b1, b2);
            }
            if (x < width)
                d[x] = (ushort)Gvsp10PackedP0(s[i], s[i + 1]);
        }
    }

    /// <summary>PFNC 12p(Mono12p·Bayer*12p) → 12비트 값.</summary>
    public static void Unpack12p(ReadOnlySpan<byte> src, int srcStrideBytes, Span<ushort> dst, int dstStridePixels, int width, int height)
    {
        var lineBytes = PfncLineBytes(width, 12);
        if (!Validate(src, srcStrideBytes, dst, dstStridePixels, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStridePixels, width);
            int x = 0, i = 0;
            for (; x + 1 < width; x += 2, i += 3)
            {
                int b0 = s[i], b1 = s[i + 1], b2 = s[i + 2];
                d[x] = (ushort)Pfnc12pP0(b0, b1);
                d[x + 1] = (ushort)Pfnc12pP1(b1, b2);
            }
            if (x < width)
                d[x] = (ushort)Pfnc12pP0(s[i], s[i + 1]);
        }
    }

    /// <summary>PFNC 10p(Mono10p·Bayer*10p) → 10비트 값.</summary>
    public static void Unpack10p(ReadOnlySpan<byte> src, int srcStrideBytes, Span<ushort> dst, int dstStridePixels, int width, int height)
    {
        var lineBytes = PfncLineBytes(width, 10);
        if (!Validate(src, srcStrideBytes, dst, dstStridePixels, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStridePixels, width);
            int x = 0, i = 0;
            for (; x + 3 < width; x += 4, i += 5)
            {
                int b0 = s[i], b1 = s[i + 1], b2 = s[i + 2], b3 = s[i + 3], b4 = s[i + 4];
                d[x] = (ushort)Pfnc10pP0(b0, b1);
                d[x + 1] = (ushort)Pfnc10pP1(b1, b2);
                d[x + 2] = (ushort)Pfnc10pP2(b2, b3);
                d[x + 3] = (ushort)Pfnc10pP3(b3, b4);
            }
            var rem = width - x;
            if (rem >= 1) d[x] = (ushort)Pfnc10pP0(s[i], s[i + 1]);
            if (rem >= 2) d[x + 1] = (ushort)Pfnc10pP1(s[i + 1], s[i + 2]);
            if (rem >= 3) d[x + 2] = (ushort)Pfnc10pP2(s[i + 2], s[i + 3]);
        }
    }

    // GVSP Packed 한 줄: 2픽셀마다 3바이트, 홀수 폭은 마지막 묶음까지 채운다(셋째 바이트 패딩). PixelFormatInfo.LineBytes 와 같은 값.
    private static int GvspPackedLineBytes(int width) => width < 0 ? 0 : LineInt(((long)width + 1) / 2 * 3, width);

    // PFNC p 한 줄: 비트를 이어 붙이고 바이트 경계로 올림.
    private static int PfncLineBytes(int width, int bits) => width < 0 ? 0 : LineInt(((long)width * bits + 7) / 8, width);

    // long 으로 센 줄 바이트를 int 로. 넘치면 폭이 너무 큰 것 — OverflowException 이 아니라 치수 인자 예외로 내서 호출자가 치수 오류를 한 갈래로 잡게 한다.
    private static int LineInt(long bytes, int width)
        => bytes <= int.MaxValue ? (int)bytes : throw new ArgumentOutOfRangeException(nameof(width), width, $"Width {width} needs a {bytes}-byte line, above the int limit.");

    // 새 목적지 배열의 길이(width × height). 넘치면 치수 인자 예외.
    private static int PixelCount(int width, int height)
    {
        var n = (long)width * height;
        if (n > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), width, $"{width}x{height} pixels exceed the maximum array length.");
        return (int)n;
    }

    // 치수·간격·버퍼 크기 검사. 간격은 폭·높이가 0 이어도 검사한다(쓰레기 인자를 조용히 받지 않는다); 그다음 0 이면 할 일이 없어 false.
    private static bool Validate(ReadOnlySpan<byte> src, int srcStrideBytes, Span<ushort> dst, int dstStridePixels, int width, int height, int lineBytes)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        if (srcStrideBytes < lineBytes)
            throw new ArgumentException($"Source stride {srcStrideBytes} is shorter than the packed line of {lineBytes} bytes for width {width}.", nameof(srcStrideBytes));
        if (dstStridePixels < width)
            throw new ArgumentException($"Destination stride {dstStridePixels} pixels is shorter than width {width}.", nameof(dstStridePixels));
        if (width == 0 || height == 0) return false;
        var needSrc = (long)(height - 1) * srcStrideBytes + lineBytes;
        if (src.Length < needSrc)
            throw new ArgumentException($"Source buffer too small: {src.Length} bytes, need {needSrc} for {width}x{height}.", nameof(src));
        var needDst = (long)(height - 1) * dstStridePixels + width;
        if (dst.Length < needDst)
            throw new ArgumentException($"Destination buffer too small: {dst.Length} pixels, need {needDst} for {width}x{height}.", nameof(dst));
        return true;
    }
}
