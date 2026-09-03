namespace GevSharp.Pfnc;

/// <summary>
/// 단일 성분 포맷을 픽셀당 상위 8비트 한 바이트로 접는다 — 8비트 모노 이미지만 받는 소비자(표시·검사 파이프라인)용.
/// 접기 = 값 &gt;&gt; (유효 비트 수 − 8). 유효 비트 위의 컨테이너 비트(Mono12 의 상위 니블 등)는 바이트로 자르면서 떨어져 나가므로
/// 장치가 거기에 무엇을 채워 보내도 결과에 섞이지 않는다. Bayer 는 모자이크를 그대로 둔다 — 디모자이크는 하지 않는다.
/// 비트 배치는 <see cref="PixelUnpack"/> 의 묶음 디코더 한 벌을 그대로 쓴다.
/// </summary>
public static partial class PixelUnpack
{
    // 접기 경로 — 소스 바이트 배치별로 하나씩.
    private enum FoldKind
    {
        /// <summary>Mono8·Bayer*8 — 줄 복사.</summary>
        Copy8,
        /// <summary>Mono/Bayer 10·12·14·16 — 리틀엔디언 16비트 컨테이너, 값 &gt;&gt; (깊이 − 8).</summary>
        Le16,
        Gvsp10Packed,
        Gvsp12Packed,
        Pfnc10p,
        Pfnc12p,
    }

    /// <summary>
    /// 이 코드를 8비트로 접을 수 있는지 — Mono8·Bayer*8, 언팩된 Mono/Bayer 10·12·14·16, GVSP 10/12Packed, PFNC 10p/12p(Mono 와 Bayer 네 패턴 전부).
    /// 부호 있는 Mono8s, 1/2/4비트, 14p, 32비트, Coord3D·Confidence·Data, 다성분 포맷, 모르는 코드는 false.
    /// </summary>
    public static bool CanFoldToMono8(uint code) => TryPlanFold(code, out _, out _);

    /// <summary>
    /// 픽셀마다 상위 8비트를 dst 에 쓴다. 줄 단위: srcStrideBytes·dstStrideBytes 는 둘 다 바이트 단위 줄 간격(<see cref="Unpack"/> 의 목적지 간격은 픽셀 단위 —
    /// 이름의 단위 접미사가 그 차이다)이라 PaddingX 가 있는 프레임과 줄 간격이 다른 목적지를 복사 없이 다룬다. 접을 수 없는 코드·짧은 줄 간격·모자라는 버퍼는
    /// <see cref="ArgumentException"/>, 음수 치수와 int 를 넘는 줄·배열은 <see cref="ArgumentOutOfRangeException"/>. 할당하지 않는다.
    /// Mono8·Bayer*8 은 줄 복사(빈틈없는 버퍼끼리는 한 번에).
    /// </summary>
    public static void FoldToMono8(uint code, ReadOnlySpan<byte> src, int srcStrideBytes, Span<byte> dst, int dstStrideBytes, int width, int height)
    {
        var kind = SelectFold(code, out var shift);
        switch (kind)
        {
            case FoldKind.Copy8: FoldCopy8(src, srcStrideBytes, dst, dstStrideBytes, width, height); break;
            case FoldKind.Le16: FoldLe16(src, srcStrideBytes, dst, dstStrideBytes, width, height, shift); break;
            case FoldKind.Gvsp10Packed: Fold10Packed(src, srcStrideBytes, dst, dstStrideBytes, width, height); break;
            case FoldKind.Gvsp12Packed: Fold12Packed(src, srcStrideBytes, dst, dstStrideBytes, width, height); break;
            case FoldKind.Pfnc10p: Fold10p(src, srcStrideBytes, dst, dstStrideBytes, width, height); break;
            case FoldKind.Pfnc12p: Fold12p(src, srcStrideBytes, dst, dstStrideBytes, width, height); break;
            default:
                // TryPlanFold 가 고른 경로에 루틴이 없다 — 다른 루틴으로 조용히 보내지 않고 바로 낸다.
                throw new ArgumentException($"No fold routine for kind {kind} of pixel format {PixelFormatInfo.Name(code)}.", nameof(code));
        }
    }

    /// <summary>새 <c>byte[width × height]</c>(줄 간격 = width, 빈틈없음)를 만들어 접는다. 소스 줄 간격은 바이트.</summary>
    public static byte[] FoldToMono8(uint code, ReadOnlySpan<byte> src, int srcStrideBytes, int width, int height)
    {
        SelectFold(code, out _);   // 할당하기 전에 코드부터 거른다
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        var dst = new byte[PixelCount(width, height)];
        FoldToMono8(code, src, srcStrideBytes, dst, width, width, height);
        return dst;
    }

    private static FoldKind SelectFold(uint code, out int shift)
    {
        if (!TryPlanFold(code, out var kind, out shift))
            throw new ArgumentException(
                $"Pixel format {PixelFormatInfo.Name(code)} cannot be folded to 8 bits; supported are single-component Mono/Bayer 8, 10, 12, 14, 16, 10Packed, 12Packed, 10p and 12p.",
                nameof(code));
        return kind;
    }

    // 코드 → 접기 경로와 시프트 양. 패킹 포맷은 패킹 방식이 깊이를 정하고, 바이트 정렬 포맷은 표의 깊이(PixelFormatInfo.Depth)를 본다 —
    // 8 은 줄 복사, 9..16 은 16비트 컨테이너. 깊이 0(부호 있는 Mono8s 등)과 16 초과(Mono32)는 접지 않는다.
    private static bool TryPlanFold(uint code, out FoldKind kind, out int shift)
    {
        kind = FoldKind.Copy8;
        shift = 0;
        if (!PixelFormatInfo.IsKnown(code)) return false;
        if (!PixelFormatInfo.IsMono(code) && !PixelFormatInfo.IsBayer(code)) return false;
        switch (PixelFormatInfo.Packing(code))
        {
            case PixelPacking.Gvsp10Packed: kind = FoldKind.Gvsp10Packed; shift = 2; return true;
            case PixelPacking.Gvsp12Packed: kind = FoldKind.Gvsp12Packed; shift = 4; return true;
            case PixelPacking.Pfnc10p: kind = FoldKind.Pfnc10p; shift = 2; return true;
            case PixelPacking.Pfnc12p: kind = FoldKind.Pfnc12p; shift = 4; return true;
            case PixelPacking.None:
                var depth = PixelFormatInfo.Depth(code);
                if (depth < 8 || depth > 16) return false;
                kind = depth == 8 ? FoldKind.Copy8 : FoldKind.Le16;
                shift = depth - 8;
                return true;
            default:
                return false;
        }
    }

    // ---------- 접기 루틴 ----------

    // 8비트: 줄 복사. 양쪽 다 빈틈없으면 한 번에 옮긴다.
    private static void FoldCopy8(ReadOnlySpan<byte> src, int srcStrideBytes, Span<byte> dst, int dstStrideBytes, int width, int height)
    {
        if (!ValidateFold(src, srcStrideBytes, dst, dstStrideBytes, width, height, width)) return;
        if (srcStrideBytes == width && dstStrideBytes == width)
        {
            src.Slice(0, width * height).CopyTo(dst);   // 검사가 통과했으므로 width × height 는 src.Length 안 — 오버플로 없음
            return;
        }
        for (var y = 0; y < height; y++)
            src.Slice(y * srcStrideBytes, width).CopyTo(dst.Slice(y * dstStrideBytes, width));
    }

    // 리틀엔디언 16비트 컨테이너: 값 >> shift 를 바이트로 자른다(깊이 위의 비트는 이때 떨어진다).
    private static void FoldLe16(ReadOnlySpan<byte> src, int srcStrideBytes, Span<byte> dst, int dstStrideBytes, int width, int height, int shift)
    {
        var lineBytes = width < 0 ? 0 : LineInt((long)width * 2, width);
        if (!ValidateFold(src, srcStrideBytes, dst, dstStrideBytes, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStrideBytes, width);
            for (int x = 0, i = 0; x < width; x++, i += 2)
                d[x] = (byte)((s[i] | (s[i + 1] << 8)) >> shift);
        }
    }

    private static void Fold12Packed(ReadOnlySpan<byte> src, int srcStrideBytes, Span<byte> dst, int dstStrideBytes, int width, int height)
    {
        var lineBytes = GvspPackedLineBytes(width);
        if (!ValidateFold(src, srcStrideBytes, dst, dstStrideBytes, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStrideBytes, width);
            int x = 0, i = 0;
            for (; x + 1 < width; x += 2, i += 3)
            {
                int b0 = s[i], b1 = s[i + 1], b2 = s[i + 2];
                d[x] = (byte)(Gvsp12PackedP0(b0, b1) >> 4);
                d[x + 1] = (byte)(Gvsp12PackedP1(b1, b2) >> 4);
            }
            if (x < width)
                d[x] = (byte)(Gvsp12PackedP0(s[i], s[i + 1]) >> 4);
        }
    }

    private static void Fold10Packed(ReadOnlySpan<byte> src, int srcStrideBytes, Span<byte> dst, int dstStrideBytes, int width, int height)
    {
        var lineBytes = GvspPackedLineBytes(width);
        if (!ValidateFold(src, srcStrideBytes, dst, dstStrideBytes, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStrideBytes, width);
            int x = 0, i = 0;
            for (; x + 1 < width; x += 2, i += 3)
            {
                int b0 = s[i], b1 = s[i + 1], b2 = s[i + 2];
                d[x] = (byte)(Gvsp10PackedP0(b0, b1) >> 2);
                d[x + 1] = (byte)(Gvsp10PackedP1(b1, b2) >> 2);
            }
            if (x < width)
                d[x] = (byte)(Gvsp10PackedP0(s[i], s[i + 1]) >> 2);
        }
    }

    private static void Fold12p(ReadOnlySpan<byte> src, int srcStrideBytes, Span<byte> dst, int dstStrideBytes, int width, int height)
    {
        var lineBytes = PfncLineBytes(width, 12);
        if (!ValidateFold(src, srcStrideBytes, dst, dstStrideBytes, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStrideBytes, width);
            int x = 0, i = 0;
            for (; x + 1 < width; x += 2, i += 3)
            {
                int b0 = s[i], b1 = s[i + 1], b2 = s[i + 2];
                d[x] = (byte)(Pfnc12pP0(b0, b1) >> 4);
                d[x + 1] = (byte)(Pfnc12pP1(b1, b2) >> 4);
            }
            if (x < width)
                d[x] = (byte)(Pfnc12pP0(s[i], s[i + 1]) >> 4);
        }
    }

    private static void Fold10p(ReadOnlySpan<byte> src, int srcStrideBytes, Span<byte> dst, int dstStrideBytes, int width, int height)
    {
        var lineBytes = PfncLineBytes(width, 10);
        if (!ValidateFold(src, srcStrideBytes, dst, dstStrideBytes, width, height, lineBytes)) return;
        for (var y = 0; y < height; y++)
        {
            var s = src.Slice(y * srcStrideBytes, lineBytes);
            var d = dst.Slice(y * dstStrideBytes, width);
            int x = 0, i = 0;
            for (; x + 3 < width; x += 4, i += 5)
            {
                int b0 = s[i], b1 = s[i + 1], b2 = s[i + 2], b3 = s[i + 3], b4 = s[i + 4];
                d[x] = (byte)(Pfnc10pP0(b0, b1) >> 2);
                d[x + 1] = (byte)(Pfnc10pP1(b1, b2) >> 2);
                d[x + 2] = (byte)(Pfnc10pP2(b2, b3) >> 2);
                d[x + 3] = (byte)(Pfnc10pP3(b3, b4) >> 2);
            }
            var rem = width - x;
            if (rem >= 1) d[x] = (byte)(Pfnc10pP0(s[i], s[i + 1]) >> 2);
            if (rem >= 2) d[x + 1] = (byte)(Pfnc10pP1(s[i + 1], s[i + 2]) >> 2);
            if (rem >= 3) d[x + 2] = (byte)(Pfnc10pP2(s[i + 2], s[i + 3]) >> 2);
        }
    }

    // 치수·간격·버퍼 크기 검사(목적지는 바이트 단위, 한 줄 = width 바이트). 간격은 폭·높이가 0 이어도 검사한다(쓰레기 인자를 조용히 받지 않는다);
    // 그다음 0 이면 할 일이 없어 false. PixelUnpack.Validate 와 같은 순서.
    private static bool ValidateFold(ReadOnlySpan<byte> src, int srcStrideBytes, Span<byte> dst, int dstStrideBytes, int width, int height, int lineBytes)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        if (srcStrideBytes < lineBytes)
            throw new ArgumentException($"Source stride {srcStrideBytes} is shorter than the line of {lineBytes} bytes for width {width}.", nameof(srcStrideBytes));
        if (dstStrideBytes < width)
            throw new ArgumentException($"Destination stride {dstStrideBytes} is shorter than width {width} bytes.", nameof(dstStrideBytes));
        if (width == 0 || height == 0) return false;
        var needSrc = (long)(height - 1) * srcStrideBytes + lineBytes;
        if (src.Length < needSrc)
            throw new ArgumentException($"Source buffer too small: {src.Length} bytes, need {needSrc} for {width}x{height}.", nameof(src));
        var needDst = (long)(height - 1) * dstStrideBytes + width;
        if (dst.Length < needDst)
            throw new ArgumentException($"Destination buffer too small: {dst.Length} bytes, need {needDst} for {width}x{height}.", nameof(dst));
        return true;
    }
}
