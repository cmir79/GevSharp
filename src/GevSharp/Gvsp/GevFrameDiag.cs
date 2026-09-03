namespace GevSharp;

/// <summary>프레임을 전달하지 못한 이유.</summary>
public enum GevFrameDropReason
{
    /// <summary>리센드까지 시도했지만 패킷이 다 모이지 않았다(리더 유실 포함).</summary>
    Incomplete,

    /// <summary>풀에 빈 버퍼가 없었다 — 소비자가 프레임을 돌려주지 않고 있다.</summary>
    NoBuffer,

    /// <summary>리더가 깨졌거나 크기가 버퍼에 맞지 않는 등 조립 자체가 불가능했다. <see cref="GevFrameDiag.Code"/> 에 상태 코드가 실린다.</summary>
    Error,

    /// <summary>이미지가 아닌 페이로드 종류(JPEG·H.264·멀티파트 등). <see cref="GevFrameDiag.Code"/> 에 페이로드 타입이 실린다.</summary>
    Unsupported,
}

/// <summary>
/// 버려진 프레임의 진단 정보. <see cref="GevStream.FrameDropped"/> 로 수신 스레드에서 전달되므로 구조체로 두어 할당이 없다.
/// </summary>
public readonly struct GevFrameDiag
{
    public GevFrameDiag(ulong frameId, GevFrameDropReason reason, int missingPackets, int expectedPackets, ushort code)
    {
        FrameId = frameId;
        Reason = reason;
        MissingPackets = missingPackets;
        ExpectedPackets = expectedPackets;
        Code = code;
    }

    /// <summary>GVSP 블록 ID.</summary>
    public ulong FrameId { get; }

    public GevFrameDropReason Reason { get; }

    /// <summary><see cref="GevFrameDropReason.Incomplete"/> 일 때 못 받은 페이로드 패킷 수. 그 외는 0.</summary>
    public int MissingPackets { get; }

    /// <summary>리더로부터 계산한 예상 페이로드 패킷 수. 알 수 없으면 0.</summary>
    public int ExpectedPackets { get; }

    /// <summary>이유별 부가 코드 — Error: 상태 코드, Unsupported: 페이로드 타입, 그 외 0.</summary>
    public ushort Code { get; }

    public override string ToString() => $"frame {FrameId}: {Reason} (missing {MissingPackets}/{ExpectedPackets}, code 0x{Code:X4})";
}
