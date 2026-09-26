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
    /// <summary>타임스탬프 없이 만든다(<see cref="Timestamp"/> = null).</summary>
    public GevFrameDiag(ulong frameId, GevFrameDropReason reason, int missingPackets, int expectedPackets, ushort code)
        : this(frameId, reason, missingPackets, expectedPackets, code, null)
    {
    }

    public GevFrameDiag(ulong frameId, GevFrameDropReason reason, int missingPackets, int expectedPackets, ushort code, ulong? timestamp)
    {
        FrameId = frameId;
        Reason = reason;
        MissingPackets = missingPackets;
        ExpectedPackets = expectedPackets;
        Code = code;
        Timestamp = timestamp;
    }

    /// <summary>GVSP 블록 ID.</summary>
    public ulong FrameId { get; }

    /// <summary>
    /// 이 블록의 리더가 실어 온 장치 타임스탬프(틱, <see cref="GevFrame.Timestamp"/> 와 같은 값). 리더를 받지 못했거나 이미지 리더로 읽을 수
    /// 없었으면 null. 블록 번호는 장치가 획득을 시작할 때마다 다시 세기도 하므로, "이 드롭이 어느 촬영의 장인가" 는 번호가 아니라 이것으로 가른다.
    /// </summary>
    public ulong? Timestamp { get; }

    public GevFrameDropReason Reason { get; }

    /// <summary><see cref="GevFrameDropReason.Incomplete"/> 일 때 못 받은 페이로드 패킷 수. 그 외는 0.</summary>
    public int MissingPackets { get; }

    /// <summary>리더로부터 계산한 예상 페이로드 패킷 수. 알 수 없으면 0.</summary>
    public int ExpectedPackets { get; }

    /// <summary>이유별 부가 코드 — Error: 상태 코드, Unsupported: 페이로드 타입, 그 외 0.</summary>
    public ushort Code { get; }

    public override string ToString() => Timestamp is { } ts
        ? $"frame {FrameId} (ts {ts}): {Reason} (missing {MissingPackets}/{ExpectedPackets}, code 0x{Code:X4})"
        : $"frame {FrameId}: {Reason} (missing {MissingPackets}/{ExpectedPackets}, code 0x{Code:X4})";
}
