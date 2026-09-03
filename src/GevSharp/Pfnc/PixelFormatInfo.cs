using System.Globalization;
using Pattern = GevSharp.Pfnc.BayerPattern;

namespace GevSharp.Pfnc;

/// <summary>
/// 픽셀 포맷 코드 질의 — 비트 수·성분 수·패킹·Bayer 패턴·이름·줄/프레임 바이트.
/// 비트 수는 코드의 [23:16] 에서 바로 뽑으므로 표에 없는 벤더 코드에도 동작한다.
/// 표가 있어야 답할 수 있는 질의(<see cref="ComponentCount(uint)"/>, <see cref="Packing(uint)"/>)는 모르는 코드에 <see cref="GevException"/> 을 낸다 —
/// 0 이나 None 으로 얼버무리지 않는다.
/// 줄 바이트의 기본은 ceil(width × bpp / 8) 이고, 예외 두 갈래는 표에 픽셀 묶음 단위로 적혀 있다:
/// GVSP "Packed"(2픽셀 = 3바이트 — 홀수 폭이면 마지막 묶음의 셋째 바이트는 패딩)와 4:1:1 YUV/YCbCr(4픽셀 = 6바이트).
/// 4:2:2 는 bpp 가 16 이라 기본 공식(짝수 폭이면 매크로픽셀 계산과 같다)을 쓴다.
/// GVSP 리더의 줄 길이(<c>GvspImageLeader.LineBytes</c> → <c>GevFrame.Stride</c>)와 언팩·접기 루틴이 모두 이 규칙을 보므로 줄 길이의 정의는 여기 한 곳이다.
/// 이미지 전체 크기는 줄 길이 × 높이가 아니다 — 줄 사이에 패딩이 없으면 데이터가 줄에서 끊기지 않으므로 전체 픽셀 수로 한 번만 올린다.
/// 그 정의도 여기 한 곳(<see cref="PixelFormatInfo.FrameBytes(uint, int, int, int, int)"/>)이고, 줄이 바이트 경계에서 끝나는지는
/// <see cref="PixelFormatInfo.IsLineByteAligned(uint, int)"/> 가 답한다.
/// <c>uint</c> 오버로드가 기본이다 — 리터럴 0 은 enum 과 uint 양쪽으로 변환되어 모호하므로 <c>0u</c> 로 쓴다.
/// </summary>
public static class PixelFormatInfo
{
    /// <summary>[31:24] 계열 — 단일 성분.</summary>
    public const uint MonoSeries = 0x01000000;
    /// <summary>[31:24] 계열 — 다성분(RGB·YUV·YCbCr·Coord3D_ABC 등).</summary>
    public const uint ColorSeries = 0x02000000;
    /// <summary>bit31 — 벤더 전용 코드.</summary>
    public const uint CustomFlag = 0x80000000;
    private const uint SeriesMask = 0x7F000000;

    internal enum Kind
    {
        Mono,
        Bayer,
        Rgb,
        Yuv,
        YCbCr,
        Coord3D,
        Confidence,
        Data,
    }

    /// <summary>표 한 줄. 이름은 enum 멤버와 nameof 로 묶어 어긋날 수 없다.</summary>
    internal readonly struct Desc
    {
        public readonly PixelFormat Format;
        public readonly string Name;
        public readonly Kind Kind;
        public readonly int ComponentCount;
        public readonly PixelPacking Packing;
        public readonly Pattern BayerPattern;
        /// <summary>줄 바이트 계산의 픽셀 묶음 크기. 0 이면 비트 수 공식을 쓴다.</summary>
        public readonly int GroupPixels;
        /// <summary>묶음 하나의 바이트 수.</summary>
        public readonly int GroupBytes;
        /// <summary>픽셀 값의 유효 비트 수 — 부호 없는 정수 단일 성분(Mono·Bayer)만, 이름이 말하는 깊이(Mono10 도 Mono10Packed 도 10). 그 밖은 0.</summary>
        public readonly int Depth;

        public Desc(PixelFormat format, string name, Kind kind, int componentCount, PixelPacking packing,
                    Pattern bayerPattern = Pattern.None, int groupPixels = 0, int groupBytes = 0, int depth = 0)
        {
            Format = format;
            Name = name;
            Kind = kind;
            ComponentCount = componentCount;
            Packing = packing;
            BayerPattern = bayerPattern;
            GroupPixels = groupPixels;
            GroupBytes = groupBytes;
            Depth = depth;
        }
    }

    // 값은 공개 표 두 곳 이상에서 일치하는 것만 실었다. 정적 초기화 순서상 이 배열이 아래 사전들보다 먼저 와야 한다.
    private static readonly Desc[] Table =
    {
        new(PixelFormat.Mono1p, nameof(PixelFormat.Mono1p), Kind.Mono, 1, PixelPacking.Pfnc1p, depth: 1),
        new(PixelFormat.Mono2p, nameof(PixelFormat.Mono2p), Kind.Mono, 1, PixelPacking.Pfnc2p, depth: 2),
        new(PixelFormat.Mono4p, nameof(PixelFormat.Mono4p), Kind.Mono, 1, PixelPacking.Pfnc4p, depth: 4),
        new(PixelFormat.Mono8, nameof(PixelFormat.Mono8), Kind.Mono, 1, PixelPacking.None, depth: 8),
        new(PixelFormat.Mono8s, nameof(PixelFormat.Mono8s), Kind.Mono, 1, PixelPacking.None),
        new(PixelFormat.Mono10, nameof(PixelFormat.Mono10), Kind.Mono, 1, PixelPacking.None, depth: 10),
        new(PixelFormat.Mono10p, nameof(PixelFormat.Mono10p), Kind.Mono, 1, PixelPacking.Pfnc10p, depth: 10),
        new(PixelFormat.Mono10Packed, nameof(PixelFormat.Mono10Packed), Kind.Mono, 1, PixelPacking.Gvsp10Packed, Pattern.None, 2, 3, depth: 10),
        new(PixelFormat.Mono12, nameof(PixelFormat.Mono12), Kind.Mono, 1, PixelPacking.None, depth: 12),
        new(PixelFormat.Mono12p, nameof(PixelFormat.Mono12p), Kind.Mono, 1, PixelPacking.Pfnc12p, depth: 12),
        new(PixelFormat.Mono12Packed, nameof(PixelFormat.Mono12Packed), Kind.Mono, 1, PixelPacking.Gvsp12Packed, Pattern.None, 2, 3, depth: 12),
        new(PixelFormat.Mono14, nameof(PixelFormat.Mono14), Kind.Mono, 1, PixelPacking.None, depth: 14),
        new(PixelFormat.Mono14p, nameof(PixelFormat.Mono14p), Kind.Mono, 1, PixelPacking.Pfnc14p, depth: 14),
        new(PixelFormat.Mono16, nameof(PixelFormat.Mono16), Kind.Mono, 1, PixelPacking.None, depth: 16),
        new(PixelFormat.Mono32, nameof(PixelFormat.Mono32), Kind.Mono, 1, PixelPacking.None, depth: 32),
        new(PixelFormat.BayerGR8, nameof(PixelFormat.BayerGR8), Kind.Bayer, 1, PixelPacking.None, Pattern.GR, depth: 8),
        new(PixelFormat.BayerGR10, nameof(PixelFormat.BayerGR10), Kind.Bayer, 1, PixelPacking.None, Pattern.GR, depth: 10),
        new(PixelFormat.BayerGR10p, nameof(PixelFormat.BayerGR10p), Kind.Bayer, 1, PixelPacking.Pfnc10p, Pattern.GR, depth: 10),
        new(PixelFormat.BayerGR10Packed, nameof(PixelFormat.BayerGR10Packed), Kind.Bayer, 1, PixelPacking.Gvsp10Packed, Pattern.GR, 2, 3, depth: 10),
        new(PixelFormat.BayerGR12, nameof(PixelFormat.BayerGR12), Kind.Bayer, 1, PixelPacking.None, Pattern.GR, depth: 12),
        new(PixelFormat.BayerGR12p, nameof(PixelFormat.BayerGR12p), Kind.Bayer, 1, PixelPacking.Pfnc12p, Pattern.GR, depth: 12),
        new(PixelFormat.BayerGR12Packed, nameof(PixelFormat.BayerGR12Packed), Kind.Bayer, 1, PixelPacking.Gvsp12Packed, Pattern.GR, 2, 3, depth: 12),
        new(PixelFormat.BayerGR14, nameof(PixelFormat.BayerGR14), Kind.Bayer, 1, PixelPacking.None, Pattern.GR, depth: 14),
        new(PixelFormat.BayerGR14p, nameof(PixelFormat.BayerGR14p), Kind.Bayer, 1, PixelPacking.Pfnc14p, Pattern.GR, depth: 14),
        new(PixelFormat.BayerGR16, nameof(PixelFormat.BayerGR16), Kind.Bayer, 1, PixelPacking.None, Pattern.GR, depth: 16),
        new(PixelFormat.BayerRG8, nameof(PixelFormat.BayerRG8), Kind.Bayer, 1, PixelPacking.None, Pattern.RG, depth: 8),
        new(PixelFormat.BayerRG10, nameof(PixelFormat.BayerRG10), Kind.Bayer, 1, PixelPacking.None, Pattern.RG, depth: 10),
        new(PixelFormat.BayerRG10p, nameof(PixelFormat.BayerRG10p), Kind.Bayer, 1, PixelPacking.Pfnc10p, Pattern.RG, depth: 10),
        new(PixelFormat.BayerRG10Packed, nameof(PixelFormat.BayerRG10Packed), Kind.Bayer, 1, PixelPacking.Gvsp10Packed, Pattern.RG, 2, 3, depth: 10),
        new(PixelFormat.BayerRG12, nameof(PixelFormat.BayerRG12), Kind.Bayer, 1, PixelPacking.None, Pattern.RG, depth: 12),
        new(PixelFormat.BayerRG12p, nameof(PixelFormat.BayerRG12p), Kind.Bayer, 1, PixelPacking.Pfnc12p, Pattern.RG, depth: 12),
        new(PixelFormat.BayerRG12Packed, nameof(PixelFormat.BayerRG12Packed), Kind.Bayer, 1, PixelPacking.Gvsp12Packed, Pattern.RG, 2, 3, depth: 12),
        new(PixelFormat.BayerRG14, nameof(PixelFormat.BayerRG14), Kind.Bayer, 1, PixelPacking.None, Pattern.RG, depth: 14),
        new(PixelFormat.BayerRG14p, nameof(PixelFormat.BayerRG14p), Kind.Bayer, 1, PixelPacking.Pfnc14p, Pattern.RG, depth: 14),
        new(PixelFormat.BayerRG16, nameof(PixelFormat.BayerRG16), Kind.Bayer, 1, PixelPacking.None, Pattern.RG, depth: 16),
        new(PixelFormat.BayerGB8, nameof(PixelFormat.BayerGB8), Kind.Bayer, 1, PixelPacking.None, Pattern.GB, depth: 8),
        new(PixelFormat.BayerGB10, nameof(PixelFormat.BayerGB10), Kind.Bayer, 1, PixelPacking.None, Pattern.GB, depth: 10),
        new(PixelFormat.BayerGB10p, nameof(PixelFormat.BayerGB10p), Kind.Bayer, 1, PixelPacking.Pfnc10p, Pattern.GB, depth: 10),
        new(PixelFormat.BayerGB10Packed, nameof(PixelFormat.BayerGB10Packed), Kind.Bayer, 1, PixelPacking.Gvsp10Packed, Pattern.GB, 2, 3, depth: 10),
        new(PixelFormat.BayerGB12, nameof(PixelFormat.BayerGB12), Kind.Bayer, 1, PixelPacking.None, Pattern.GB, depth: 12),
        new(PixelFormat.BayerGB12p, nameof(PixelFormat.BayerGB12p), Kind.Bayer, 1, PixelPacking.Pfnc12p, Pattern.GB, depth: 12),
        new(PixelFormat.BayerGB12Packed, nameof(PixelFormat.BayerGB12Packed), Kind.Bayer, 1, PixelPacking.Gvsp12Packed, Pattern.GB, 2, 3, depth: 12),
        new(PixelFormat.BayerGB14, nameof(PixelFormat.BayerGB14), Kind.Bayer, 1, PixelPacking.None, Pattern.GB, depth: 14),
        new(PixelFormat.BayerGB14p, nameof(PixelFormat.BayerGB14p), Kind.Bayer, 1, PixelPacking.Pfnc14p, Pattern.GB, depth: 14),
        new(PixelFormat.BayerGB16, nameof(PixelFormat.BayerGB16), Kind.Bayer, 1, PixelPacking.None, Pattern.GB, depth: 16),
        new(PixelFormat.BayerBG8, nameof(PixelFormat.BayerBG8), Kind.Bayer, 1, PixelPacking.None, Pattern.BG, depth: 8),
        new(PixelFormat.BayerBG10, nameof(PixelFormat.BayerBG10), Kind.Bayer, 1, PixelPacking.None, Pattern.BG, depth: 10),
        new(PixelFormat.BayerBG10p, nameof(PixelFormat.BayerBG10p), Kind.Bayer, 1, PixelPacking.Pfnc10p, Pattern.BG, depth: 10),
        new(PixelFormat.BayerBG10Packed, nameof(PixelFormat.BayerBG10Packed), Kind.Bayer, 1, PixelPacking.Gvsp10Packed, Pattern.BG, 2, 3, depth: 10),
        new(PixelFormat.BayerBG12, nameof(PixelFormat.BayerBG12), Kind.Bayer, 1, PixelPacking.None, Pattern.BG, depth: 12),
        new(PixelFormat.BayerBG12p, nameof(PixelFormat.BayerBG12p), Kind.Bayer, 1, PixelPacking.Pfnc12p, Pattern.BG, depth: 12),
        new(PixelFormat.BayerBG12Packed, nameof(PixelFormat.BayerBG12Packed), Kind.Bayer, 1, PixelPacking.Gvsp12Packed, Pattern.BG, 2, 3, depth: 12),
        new(PixelFormat.BayerBG14, nameof(PixelFormat.BayerBG14), Kind.Bayer, 1, PixelPacking.None, Pattern.BG, depth: 14),
        new(PixelFormat.BayerBG14p, nameof(PixelFormat.BayerBG14p), Kind.Bayer, 1, PixelPacking.Pfnc14p, Pattern.BG, depth: 14),
        new(PixelFormat.BayerBG16, nameof(PixelFormat.BayerBG16), Kind.Bayer, 1, PixelPacking.None, Pattern.BG, depth: 16),
        new(PixelFormat.RGB8, nameof(PixelFormat.RGB8), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.BGR8, nameof(PixelFormat.BGR8), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGBa8, nameof(PixelFormat.RGBa8), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.BGRa8, nameof(PixelFormat.BGRa8), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.RGB10, nameof(PixelFormat.RGB10), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.BGR10, nameof(PixelFormat.BGR10), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGB10p, nameof(PixelFormat.RGB10p), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.BGR10p, nameof(PixelFormat.BGR10p), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.RGB10p32, nameof(PixelFormat.RGB10p32), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.RGB12, nameof(PixelFormat.RGB12), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.BGR12, nameof(PixelFormat.BGR12), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGB12p, nameof(PixelFormat.RGB12p), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.BGR12p, nameof(PixelFormat.BGR12p), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.RGB14, nameof(PixelFormat.RGB14), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.BGR14, nameof(PixelFormat.BGR14), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGB16, nameof(PixelFormat.RGB16), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.BGR16, nameof(PixelFormat.BGR16), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGBa10, nameof(PixelFormat.RGBa10), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.RGBa10p, nameof(PixelFormat.RGBa10p), Kind.Rgb, 4, PixelPacking.Other),
        new(PixelFormat.RGBa12, nameof(PixelFormat.RGBa12), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.RGBa12p, nameof(PixelFormat.RGBa12p), Kind.Rgb, 4, PixelPacking.Other),
        new(PixelFormat.RGBa14, nameof(PixelFormat.RGBa14), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.RGBa16, nameof(PixelFormat.RGBa16), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.BGRa10, nameof(PixelFormat.BGRa10), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.BGRa10p, nameof(PixelFormat.BGRa10p), Kind.Rgb, 4, PixelPacking.Other),
        new(PixelFormat.BGRa12, nameof(PixelFormat.BGRa12), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.BGRa12p, nameof(PixelFormat.BGRa12p), Kind.Rgb, 4, PixelPacking.Other),
        new(PixelFormat.BGRa14, nameof(PixelFormat.BGRa14), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.BGRa16, nameof(PixelFormat.BGRa16), Kind.Rgb, 4, PixelPacking.None),
        new(PixelFormat.RGB565p, nameof(PixelFormat.RGB565p), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.BGR565p, nameof(PixelFormat.BGR565p), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.RGB8_Planar, nameof(PixelFormat.RGB8_Planar), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGB10_Planar, nameof(PixelFormat.RGB10_Planar), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGB12_Planar, nameof(PixelFormat.RGB12_Planar), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGB16_Planar, nameof(PixelFormat.RGB16_Planar), Kind.Rgb, 3, PixelPacking.None),
        new(PixelFormat.RGB10V1Packed, nameof(PixelFormat.RGB10V1Packed), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.RGB12V1Packed, nameof(PixelFormat.RGB12V1Packed), Kind.Rgb, 3, PixelPacking.Other),
        new(PixelFormat.YUV8_UYV, nameof(PixelFormat.YUV8_UYV), Kind.Yuv, 3, PixelPacking.None),
        new(PixelFormat.YUV411_8_UYYVYY, nameof(PixelFormat.YUV411_8_UYYVYY), Kind.Yuv, 3, PixelPacking.None, Pattern.None, 4, 6),
        new(PixelFormat.YUV422_8, nameof(PixelFormat.YUV422_8), Kind.Yuv, 3, PixelPacking.None),
        new(PixelFormat.YUV422_8_UYVY, nameof(PixelFormat.YUV422_8_UYVY), Kind.Yuv, 3, PixelPacking.None),
        new(PixelFormat.YCbCr8, nameof(PixelFormat.YCbCr8), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr8_CbYCr, nameof(PixelFormat.YCbCr8_CbYCr), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr411_8, nameof(PixelFormat.YCbCr411_8), Kind.YCbCr, 3, PixelPacking.None, Pattern.None, 4, 6),
        new(PixelFormat.YCbCr411_8_CbYYCrYY, nameof(PixelFormat.YCbCr411_8_CbYYCrYY), Kind.YCbCr, 3, PixelPacking.None, Pattern.None, 4, 6),
        new(PixelFormat.YCbCr422_8, nameof(PixelFormat.YCbCr422_8), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr422_8_CbYCrY, nameof(PixelFormat.YCbCr422_8_CbYCrY), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr601_8_CbYCr, nameof(PixelFormat.YCbCr601_8_CbYCr), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr601_411_8_CbYYCrYY, nameof(PixelFormat.YCbCr601_411_8_CbYYCrYY), Kind.YCbCr, 3, PixelPacking.None, Pattern.None, 4, 6),
        new(PixelFormat.YCbCr601_422_8, nameof(PixelFormat.YCbCr601_422_8), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr601_422_8_CbYCrY, nameof(PixelFormat.YCbCr601_422_8_CbYCrY), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr709_8_CbYCr, nameof(PixelFormat.YCbCr709_8_CbYCr), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr709_411_8_CbYYCrYY, nameof(PixelFormat.YCbCr709_411_8_CbYYCrYY), Kind.YCbCr, 3, PixelPacking.None, Pattern.None, 4, 6),
        new(PixelFormat.YCbCr709_422_8, nameof(PixelFormat.YCbCr709_422_8), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.YCbCr709_422_8_CbYCrY, nameof(PixelFormat.YCbCr709_422_8_CbYCrY), Kind.YCbCr, 3, PixelPacking.None),
        new(PixelFormat.Coord3D_A8, nameof(PixelFormat.Coord3D_A8), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_A16, nameof(PixelFormat.Coord3D_A16), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_A32f, nameof(PixelFormat.Coord3D_A32f), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_B8, nameof(PixelFormat.Coord3D_B8), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_B16, nameof(PixelFormat.Coord3D_B16), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_B32f, nameof(PixelFormat.Coord3D_B32f), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_C8, nameof(PixelFormat.Coord3D_C8), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_C16, nameof(PixelFormat.Coord3D_C16), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_C32f, nameof(PixelFormat.Coord3D_C32f), Kind.Coord3D, 1, PixelPacking.None),
        new(PixelFormat.Coord3D_AC8, nameof(PixelFormat.Coord3D_AC8), Kind.Coord3D, 2, PixelPacking.None),
        new(PixelFormat.Coord3D_AC16, nameof(PixelFormat.Coord3D_AC16), Kind.Coord3D, 2, PixelPacking.None),
        new(PixelFormat.Coord3D_AC32f, nameof(PixelFormat.Coord3D_AC32f), Kind.Coord3D, 2, PixelPacking.None),
        new(PixelFormat.Coord3D_ABC8, nameof(PixelFormat.Coord3D_ABC8), Kind.Coord3D, 3, PixelPacking.None),
        new(PixelFormat.Coord3D_ABC16, nameof(PixelFormat.Coord3D_ABC16), Kind.Coord3D, 3, PixelPacking.None),
        new(PixelFormat.Coord3D_ABC32f, nameof(PixelFormat.Coord3D_ABC32f), Kind.Coord3D, 3, PixelPacking.None),
        new(PixelFormat.Coord3D_ABC8_Planar, nameof(PixelFormat.Coord3D_ABC8_Planar), Kind.Coord3D, 3, PixelPacking.None),
        new(PixelFormat.Coord3D_ABC16_Planar, nameof(PixelFormat.Coord3D_ABC16_Planar), Kind.Coord3D, 3, PixelPacking.None),
        new(PixelFormat.Coord3D_ABC32f_Planar, nameof(PixelFormat.Coord3D_ABC32f_Planar), Kind.Coord3D, 3, PixelPacking.None),
        new(PixelFormat.Confidence1, nameof(PixelFormat.Confidence1), Kind.Confidence, 1, PixelPacking.None),
        new(PixelFormat.Confidence1p, nameof(PixelFormat.Confidence1p), Kind.Confidence, 1, PixelPacking.Pfnc1p),
        new(PixelFormat.Confidence8, nameof(PixelFormat.Confidence8), Kind.Confidence, 1, PixelPacking.None),
        new(PixelFormat.Confidence16, nameof(PixelFormat.Confidence16), Kind.Confidence, 1, PixelPacking.None),
        new(PixelFormat.Confidence32f, nameof(PixelFormat.Confidence32f), Kind.Confidence, 1, PixelPacking.None),
        new(PixelFormat.Data8, nameof(PixelFormat.Data8), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data8s, nameof(PixelFormat.Data8s), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data16, nameof(PixelFormat.Data16), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data16s, nameof(PixelFormat.Data16s), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data32, nameof(PixelFormat.Data32), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data32s, nameof(PixelFormat.Data32s), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data32f, nameof(PixelFormat.Data32f), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data64, nameof(PixelFormat.Data64), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data64s, nameof(PixelFormat.Data64s), Kind.Data, 1, PixelPacking.None),
        new(PixelFormat.Data64f, nameof(PixelFormat.Data64f), Kind.Data, 1, PixelPacking.None),
    };

    private static readonly Dictionary<uint, Desc> ByCode = BuildByCode();
    private static readonly Dictionary<string, PixelFormat> ByName = BuildByName();

    private static Dictionary<uint, Desc> BuildByCode()
    {
        var d = new Dictionary<uint, Desc>(Table.Length);
        foreach (var e in Table) d.Add((uint)e.Format, e);
        return d;
    }

    private static Dictionary<string, PixelFormat> BuildByName()
    {
        var d = new Dictionary<string, PixelFormat>(Table.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var e in Table) d.Add(e.Name, e.Format);
        return d;
    }

    private static Desc Require(uint code)
        => ByCode.TryGetValue(code, out var d) ? d : throw new GevException($"Unknown pixel format 0x{code:X8}.");

    /// <summary>표에 있는 코드인지. 0(Unknown)은 아니다.</summary>
    public static bool IsKnown(uint code) => ByCode.ContainsKey(code);
    public static bool IsKnown(PixelFormat format) => IsKnown((uint)format);

    /// <summary>표에 있으면 enum 값, 아니면 <see cref="PixelFormat.Unknown"/>. 벤더 코드를 그대로 다루려면 uint 를 쓴다.</summary>
    public static PixelFormat ToPixelFormat(uint code) => ByCode.ContainsKey(code) ? (PixelFormat)code : PixelFormat.Unknown;

    /// <summary>bit31 이 선 벤더 전용 코드인지.</summary>
    public static bool IsCustom(uint code) => (code & CustomFlag) != 0;

    /// <summary>픽셀당 점유 비트 수 = 코드의 [23:16]. 패킹 포맷은 실제 점유량(Mono10Packed = 12, Mono10p = 10).</summary>
    public static int BitsPerPixel(uint code) => (int)((code >> 16) & 0xFF);
    public static int BitsPerPixel(PixelFormat format) => BitsPerPixel((uint)format);

    /// <summary>단일 성분 휘도(Mono 계열). Bayer·Coord3D·Confidence·Data 는 아니다. 모르는 코드는 계열 바이트(0x01)로 판단한다.</summary>
    public static bool IsMono(uint code)
        => ByCode.TryGetValue(code, out var d) ? d.Kind == Kind.Mono : (code & SeriesMask) == MonoSeries;
    public static bool IsMono(PixelFormat format) => IsMono((uint)format);

    /// <summary>Bayer 모자이크 포맷인지. 모르는 코드는 false.</summary>
    public static bool IsBayer(uint code) => ByCode.TryGetValue(code, out var d) && d.Kind == Kind.Bayer;
    public static bool IsBayer(PixelFormat format) => IsBayer((uint)format);

    /// <summary>다성분 계열(계열 바이트 0x02: RGB·YUV·YCbCr·Coord3D_ABC 등). 코드만으로 판단하므로 모르는 코드에도 동작한다. Bayer 는 아니다.</summary>
    public static bool IsColor(uint code) => (code & SeriesMask) == ColorSeries;
    public static bool IsColor(PixelFormat format) => IsColor((uint)format);

    /// <summary>YUV 또는 YCbCr 계열인지. 모르는 코드는 false.</summary>
    public static bool IsYuv(uint code) => ByCode.TryGetValue(code, out var d) && (d.Kind == Kind.Yuv || d.Kind == Kind.YCbCr);
    public static bool IsYuv(PixelFormat format) => IsYuv((uint)format);

    /// <summary>성분이 바이트 경계에 정렬되지 않은(비트 패킹) 포맷인지. 모르는 코드는 bpp 가 8의 배수가 아니면 true.</summary>
    public static bool IsPacked(uint code)
        => ByCode.TryGetValue(code, out var d) ? d.Packing != PixelPacking.None : BitsPerPixel(code) % 8 != 0;
    public static bool IsPacked(PixelFormat format) => IsPacked((uint)format);

    /// <summary>패킹 방식. 모르는 코드는 <see cref="GevException"/>.</summary>
    public static PixelPacking Packing(uint code) => Require(code).Packing;
    public static PixelPacking Packing(PixelFormat format) => Packing((uint)format);

    /// <summary>픽셀 모델의 성분 수(Mono/Bayer 1, RGB 3, RGBa 4, YUV 3 — 서브샘플링과 무관). 모르는 코드는 <see cref="GevException"/>.</summary>
    public static int ComponentCount(uint code) => Require(code).ComponentCount;
    public static int ComponentCount(PixelFormat format) => ComponentCount((uint)format);

    /// <summary>
    /// 픽셀 값의 유효 비트 수 — 부호 없는 정수 단일 성분(Mono·Bayer)에 한해 이름이 말하는 깊이(Mono10 과 Mono10Packed 둘 다 10; bpp 는 컨테이너·점유 크기라 다르다).
    /// 부호 있는 Mono8s, Coord3D·Confidence·Data, 다성분 포맷, 모르는 코드는 0.
    /// </summary>
    public static int Depth(uint code) => ByCode.TryGetValue(code, out var d) ? d.Depth : 0;
    public static int Depth(PixelFormat format) => Depth((uint)format);

    /// <summary>Bayer 시작 패턴. Bayer 가 아니거나 모르는 코드면 <see cref="Pattern.None"/>.</summary>
    public static Pattern BayerPattern(uint code) => ByCode.TryGetValue(code, out var d) ? d.BayerPattern : Pattern.None;
    public static Pattern BayerPattern(PixelFormat format) => BayerPattern((uint)format);

    /// <summary>PFNC 이름. 0 은 "Unknown", 표에 없으면 "0x%08X".</summary>
    public static string Name(uint code)
    {
        if (code == 0) return nameof(PixelFormat.Unknown);
        return ByCode.TryGetValue(code, out var d) ? d.Name : $"0x{code:X8}";
    }
    public static string Name(PixelFormat format) => Name((uint)format);

    /// <summary>
    /// PFNC 이름(대소문자 무시) 또는 "0x" 16진 코드를 해석한다. 16진 코드는 표에 없어도 값 그대로 받는다("0x0" 은 Unknown).
    /// 십진 숫자는 받지 않는다 — 코드는 항상 16진으로 적는다.
    /// </summary>
    public static bool TryParse(string? name, out PixelFormat format)
    {
        format = PixelFormat.Unknown;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var s = name!.Trim();
        if (ByName.TryGetValue(s, out format)) return true;
        if (string.Equals(s, nameof(PixelFormat.Unknown), StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Length > 2 && s[0] == '0' && (s[1] == 'x' || s[1] == 'X')
            && uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            format = (PixelFormat)code;
            return true;
        }
        return false;
    }

    /// <summary><see cref="TryParse"/> 와 같되 실패하면 <see cref="GevException"/>.</summary>
    public static PixelFormat Parse(string name)
        => TryParse(name, out var f) ? f : throw new GevException($"Unrecognized pixel format name '{name}'.");

    /// <summary>
    /// 폭 width 픽셀 한 줄의 바이트 수(PaddingX 제외). 기본 ceil(width × bpp / 8);
    /// GVSP Packed 는 (width + 1) / 2 × 3, 4:1:1 은 (width + 3) / 4 × 6. bpp 가 0 인 코드(Unknown 등)는 <see cref="GevException"/>,
    /// int 를 넘는 줄은 <see cref="ArgumentOutOfRangeException"/>.
    /// <para>
    /// ⚠ 이것은 줄이 따로 시작할 때(줄 사이에 패딩이 있을 때)의 줄 길이다. 패딩이 없으면 묶음 단위 포맷의 데이터는
    /// 줄에서 끊기지 않고 이어 붙으므로 이 값을 높이만큼 곱하면 실제보다 커진다 — 이미지 전체 크기는
    /// <see cref="FrameBytes(uint, int, int, int, int)"/> 로 구한다.
    /// </para>
    /// </summary>
    public static int LineBytes(uint code, int width)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (BitsPerPixel(code) == 0) throw new GevException($"Pixel format 0x{code:X8} carries no bits-per-pixel field; cannot size a line.");
        var bytes = LineBytesLong(code, width);
        if (bytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), width, $"Width {width} needs a {bytes}-byte line, above the int limit.");
        return (int)bytes;
    }
    public static int LineBytes(PixelFormat format, int width) => LineBytes((uint)format, width);

    /// <summary>
    /// <see cref="LineBytes(uint, int)"/> 와 같은 규칙을 long 으로 — 아직 검증하지 않은 리더 치수(size_x 는 u32)를 그대로 넣는 자리용이라
    /// 넘침도 bpp 0 도 예외가 아니다(bpp 0 이면 0). 음수 폭만 <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    internal static long LineBytesLong(uint code, long width) => PackedBytesLong(code, width);

    /// <summary>
    /// 픽셀 <paramref name="pixels"/> 개를 이어 실었을 때의 바이트 수 — 이 파일이 가진 단 하나의 크기 규칙이다.
    /// 묶음 단위 포맷(GVSP Packed 는 2픽셀 3바이트, 4:1:1 은 4픽셀 6바이트)은 묶음이 쪼개지지 않으므로 마지막 묶음을 통째로 세고,
    /// 그 밖에는 ceil(pixels × bpp / 8). 아직 검증하지 않은 리더 치수를 그대로 넣는 자리라 넘침도 bpp 0 도 예외가 아니다(bpp 0 이면 0).
    /// </summary>
    private static long PackedBytesLong(uint code, long pixels)
    {
        if (pixels < 0) throw new ArgumentOutOfRangeException(nameof(pixels), pixels, "Pixel count must not be negative.");
        if (ByCode.TryGetValue(code, out var d) && d.GroupPixels > 0)
            return (pixels + d.GroupPixels - 1) / d.GroupPixels * d.GroupBytes;
        return (pixels * BitsPerPixel(code) + 7) / 8;
    }

    /// <summary>
    /// 폭 <paramref name="width"/> 픽셀 한 줄이 바이트 경계에서 끝나는지. 거짓이면 줄 사이에 패딩이 없는 한 다음 줄이
    /// 바이트 가운데에서 시작하므로 "줄 간격" 이라는 것이 없다 — 그런 이미지는 줄 단위가 아니라 이어진 한 덩어리로 다룬다.
    /// </summary>
    public static bool IsLineByteAligned(uint code, int width)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (ByCode.TryGetValue(code, out var d) && d.GroupPixels > 0) return width % d.GroupPixels == 0;
        return (long)width * BitsPerPixel(code) % 8 == 0;
    }
    public static bool IsLineByteAligned(PixelFormat format, int width) => IsLineByteAligned((uint)format, width);

    /// <summary>
    /// 이미지 페이로드 바이트 수.
    /// <para>
    /// 줄 사이에 패딩이 있으면(paddingX > 0) 줄이 저마다 따로 시작해야 하므로 줄마다 마지막 묶음을 채운다 —
    /// height × (LineBytes + paddingX) + paddingY.
    /// </para>
    /// <para>
    /// 패딩이 없으면 데이터는 줄에서 끊기지 않고 이어 붙는다. 그때는 전체 픽셀 수(width × height)에 대해 한 번만 올린다 —
    /// 줄마다 올리면 홀수 폭에서 높이만큼 더 세게 된다(실측: 2591 × 64 12비트 packed 은 248,736 바이트이지 248,832 가 아니다).
    /// </para>
    /// bpp 가 0 인 코드는 <see cref="GevException"/>, 치수가 int 를 넘기는 줄을 요구하면 <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    public static long FrameBytes(uint code, int width, int height, int paddingX = 0, int paddingY = 0)
    {
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        if (paddingX < 0) throw new ArgumentOutOfRangeException(nameof(paddingX), paddingX, "PaddingX must not be negative.");
        if (paddingY < 0) throw new ArgumentOutOfRangeException(nameof(paddingY), paddingY, "PaddingY must not be negative.");
        LineBytes(code, width);         // bpp 0 · 줄 넘침을 여기서 걸러 낸다(값은 아래 규칙이 정한다)
        return ImageBytesLong(code, width, height, paddingX, paddingY);
    }
    public static long FrameBytes(PixelFormat format, int width, int height, int paddingX = 0, int paddingY = 0)
        => FrameBytes((uint)format, width, height, paddingX, paddingY);

    /// <summary>
    /// <see cref="FrameBytes(uint, int, int, int, int)"/> 와 같은 규칙을 long 으로 — 아직 검증하지 않은 리더 치수(size_x·size_y 는 u32)를
    /// 그대로 넣는 자리용이라 bpp 0 도 예외가 아니고, 넘치는 치수는 <see cref="long.MaxValue"/> 로 포화시켜 호출자의 상한 검사에 맡긴다
    /// (조용히 되감겨 작은 수가 되면 그 검사를 통과해 버린다). 음수만 <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    internal static long ImageBytesLong(uint code, long width, long height, long paddingX, long paddingY)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must not be negative.");
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must not be negative.");
        if (paddingX < 0) throw new ArgumentOutOfRangeException(nameof(paddingX), paddingX, "PaddingX must not be negative.");
        if (paddingY < 0) throw new ArgumentOutOfRangeException(nameof(paddingY), paddingY, "PaddingY must not be negative.");

        long body;
        if (paddingX == 0)
        {
            // 줄 패딩이 없으면 묶음이 줄에서 끊기지 않는다 — 전체 픽셀 수로 한 번만 올린다.
            // PackedBytesLong 이 최대 3배로 부풀리므로 픽셀 수를 그 여유 안에서만 곱한다.
            const long MaxPixels = long.MaxValue / 8;
            if (width != 0 && height > MaxPixels / width) return long.MaxValue;
            body = PackedBytesLong(code, width * height);
        }
        else
        {
            var stride = PackedBytesLong(code, width) + paddingX;
            if (stride != 0 && height > long.MaxValue / stride) return long.MaxValue;
            body = stride * height;
        }
        return body > long.MaxValue - paddingY ? long.MaxValue : body + paddingY;
    }

    /// <summary>표 전체(테스트·진단용).</summary>
    internal static IReadOnlyList<Desc> All => Table;
}
