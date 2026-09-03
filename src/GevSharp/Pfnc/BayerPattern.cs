namespace GevSharp.Pfnc;

/// <summary>
/// Bayer 컬러 필터 배열의 시작 패턴 — 이름의 두 글자는 첫 줄 첫 두 픽셀의 색이다(GR = 첫 줄이 G R G R …, 둘째 줄이 B G B G …).
/// </summary>
public enum BayerPattern
{
    /// <summary>Bayer 포맷이 아니다.</summary>
    None,
    GR,
    RG,
    GB,
    BG,
}
