namespace GevSharp.Sim;

/// <summary>
/// 시뮬레이터 고유 피처 레지스터 주소(부트스트랩 밖의 피처 페이지 0x0001_0000..0x0001_0FFF). 전부 32비트 빅엔디언.
/// 각 레지스터의 폭·접근·리셋값과 대응하는 XML 노드는 docs/sim-register-map.md 에 정리돼 있다.
/// </summary>
public static class SimFeatureAddr
{
    public const uint FeatureBase = 0x0001_0000;
    public const uint FeatureEnd = 0x0001_1000;

    public const uint Width = 0x0001_0000;
    public const uint Height = 0x0001_0004;
    public const uint OffsetX = 0x0001_0008;
    public const uint OffsetY = 0x0001_000C;
    /// <summary>PFNC 코드 그대로.</summary>
    public const uint PixelFormat = 0x0001_0010;
    /// <summary>노출 시간(타임스탬프 틱 = ns). ExposureTime(µs) 은 XML Converter 가 환산한다.</summary>
    public const uint ExposureTimeRaw = 0x0001_0014;
    /// <summary>0..2 — GainRaw 블록의 인덱스.</summary>
    public const uint GainSelector = 0x0001_0018;
    /// <summary>GainRaw[0]; [n] = GainRaw0 + 4n (n = 0..2).</summary>
    public const uint GainRaw0 = 0x0001_001C;
    public const int GainCount = 3;
    /// <summary>bit0 = TriggerMode(1 = On), bits[7:4] = TriggerSource(0 Software, 1 Line0, 2 Line1).</summary>
    public const uint TriggerControl = 0x0001_0028;
    public const uint TriggerModeMask = 0x0000_0001;
    public const uint TriggerSourceMask = 0x0000_00F0;
    public const int TriggerSourceShift = 4;
    /// <summary>0 Continuous, 1 SingleFrame, 2 MultiFrame.</summary>
    public const uint AcquisitionMode = 0x0001_002C;
    /// <summary>1 을 쓰면 획득 시작. 즉시 0 으로 돌아온다(self-clearing).</summary>
    public const uint AcquisitionStart = 0x0001_0030;
    /// <summary>1 을 쓰면 획득 정지. 즉시 0 으로 돌아온다.</summary>
    public const uint AcquisitionStop = 0x0001_0034;
    /// <summary>읽기 전용: 획득 중이면 1.</summary>
    public const uint AcquisitionStatus = 0x0001_0038;
    /// <summary>IEEE 754 binary32, 빅엔디언. Hz.</summary>
    public const uint AcquisitionFrameRate = 0x0001_003C;
    /// <summary>0 Off(전부 0), 1 DiagonalRamp((b + y + frameId) &amp; 0xFF), 2 FrameCounter(모든 바이트 = frameId &amp; 0xFF).</summary>
    public const uint TestPattern = 0x0001_0040;
    /// <summary>0 Default, 1 UserSet1.</summary>
    public const uint UserSetSelector = 0x0001_0044;
    /// <summary>1 을 쓰면 피처 페이지를 생성 시 값으로 되돌린다. 즉시 0 으로 돌아온다.</summary>
    public const uint UserSetLoad = 0x0001_0048;
    /// <summary>MultiFrame 모드에서 한 번의 시작으로 보낼 프레임 수.</summary>
    public const uint AcquisitionFrameCount = 0x0001_004C;
    /// <summary>Boolean: 0/1. 픽셀 내용에는 영향을 주지 않는다(레지스터 왕복 검증용).</summary>
    public const uint ReverseX = 0x0001_0050;
    /// <summary>읽기 전용 상한 — Width 의 pMax.</summary>
    public const uint WidthMax = 0x0001_0054;
    /// <summary>읽기 전용 상한 — Height 의 pMax.</summary>
    public const uint HeightMax = 0x0001_0058;
    /// <summary>읽기 전용: 지금까지 전송한 프레임 수.</summary>
    public const uint FrameCounter = 0x0001_005C;
    /// <summary>1 을 쓰면 소프트웨어 트리거 한 번(TriggerMode=On 일 때 한 프레임). 즉시 0 으로 돌아온다.</summary>
    public const uint TriggerSoftware = 0x0001_0060;

    public const uint TestPatternOff = 0;
    public const uint TestPatternDiagonalRamp = 1;
    public const uint TestPatternFrameCounter = 2;

    public const uint AcquisitionModeContinuous = 0;
    public const uint AcquisitionModeSingleFrame = 1;
    public const uint AcquisitionModeMultiFrame = 2;
}
