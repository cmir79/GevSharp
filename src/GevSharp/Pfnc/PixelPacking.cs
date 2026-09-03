namespace GevSharp.Pfnc;

/// <summary>
/// 픽셀 비트 패킹 방식 — 언팩 루틴 선택과 줄 바이트 계산의 근거. 같은 비트 수라도 GVSP "Packed" 와 PFNC "p" 는 바이트 배치가 다르다.
/// </summary>
public enum PixelPacking
{
    /// <summary>성분마다 바이트 정렬(8/16/32/64비트 컨테이너). 언팩이 필요 없다.</summary>
    None,
    /// <summary>GVSP 고유 10비트: 2픽셀 = 3바이트. b0 = P0 상위 8비트, b1 의 [1:0]·[5:4] 가 P0·P1 의 하위 2비트, b2 = P1 상위 8비트.</summary>
    Gvsp10Packed,
    /// <summary>GVSP 고유 12비트: 2픽셀 = 3바이트. b0 = P0 상위 8비트, b1 의 [3:0]·[7:4] 가 P0·P1 의 하위 니블, b2 = P1 상위 8비트.</summary>
    Gvsp12Packed,
    /// <summary>PFNC 1비트 LSB 우선 — 8픽셀 = 1바이트.</summary>
    Pfnc1p,
    /// <summary>PFNC 2비트 LSB 우선 — 4픽셀 = 1바이트.</summary>
    Pfnc2p,
    /// <summary>PFNC 4비트 LSB 우선 — 2픽셀 = 1바이트.</summary>
    Pfnc4p,
    /// <summary>PFNC 10비트 LSB 우선 — 4픽셀 = 5바이트.</summary>
    Pfnc10p,
    /// <summary>PFNC 12비트 LSB 우선 — 2픽셀 = 3바이트.</summary>
    Pfnc12p,
    /// <summary>PFNC 14비트 LSB 우선 — 4픽셀 = 7바이트.</summary>
    Pfnc14p,
    /// <summary>다성분 패킹(RGB565p, RGB10p32, RGB10p, RGB10V1Packed 등). 전용 언팩 루틴은 없다.</summary>
    Other,
}
