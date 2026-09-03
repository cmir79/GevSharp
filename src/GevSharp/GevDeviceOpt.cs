using System.Net;

namespace GevSharp;

/// <summary>제어 채널 권한(CCP) 모드.</summary>
public enum GevAccessMode
{
    /// <summary>CCP control 비트 — 쓰기 가능, 다른 호스트는 읽기만.</summary>
    Control,
    /// <summary>CCP exclusive + control — 다른 호스트는 읽기도 막힌다.</summary>
    Exclusive,
    /// <summary>CCP 를 건드리지 않는다. 읽기 전용, 하트비트 없음.</summary>
    ReadOnly,
}

/// <summary>장치 세션 옵션. 시간 단위는 ms.</summary>
public sealed class GevDeviceOpt
{
    public GevAccessMode AccessMode { get; set; } = GevAccessMode.Control;
    /// <summary>GVCP 한 번 전송의 응답 대기.</summary>
    public int GvcpTimeoutMs { get; set; } = 500;
    /// <summary>GVCP 첫 전송이 응답 없이 끝난 뒤 다시 보내는 횟수(총 전송 = 1 + GvcpRetries).</summary>
    public int GvcpRetries { get; set; } = 3;
    /// <summary>제어권을 잡을 때 GVBS 0x0938 에 쓰는 장치 쪽 하트비트 타임아웃.</summary>
    public int HeartbeatTimeoutMs { get; set; } = 3000;
    /// <summary>하트비트(CCP 읽기) 주기. null = 장치가 받아들인 타임아웃 / 3.</summary>
    public int? HeartbeatPeriodMs { get; set; }
    /// <summary>
    /// PENDING_ACK 이 늘릴 수 있는 추가 대기의 상한. PENDING_ACK 을 받은 요청 하나가 GVCP 줄을 붙드는 시간이 여기서 정해진다.
    /// null = 하트비트에 맞춰 자동 — 붙들린 시간이 장치 하트비트 타임아웃을 먹어 치워 아무것도 실패하지 않았는데 제어권을 잃는 일을 막는다.
    /// 자동 값은 하트비트를 시작하기 직전에 정해진다. 여는 동안에는 아직 하트비트가 없으므로 채널 기본값
    /// (<see cref="Gvcp.GvcpChannelOpt.DefaultMaxPendingAckWaitMs"/>)으로 열고, 하트비트가 없는
    /// <see cref="GevAccessMode.ReadOnly"/> 세션은 그 값을 그대로 쓴다.
    /// </summary>
    public int? MaxPendingAckWaitMs { get; set; }
    /// <summary>GVCP 소켓을 묶을 호스트 주소. null = 자동(탐색 인터페이스 → 같은 서브넷 인터페이스 → OS 라우팅).</summary>
    public IPAddress? LocalAddress { get; set; }
    /// <summary>카메라 XML 디스크 캐시 폴더. null = 캐시 없음.</summary>
    public string? XmlCacheDir { get; set; }
    /// <summary>CCP 에 switchover-enable 비트를 세운다 — 다른 호스트가 제어권을 넘겨받을 수 있게 한다.</summary>
    public bool AllowSwitchover { get; set; } = false;

    internal void Validate()
    {
        if (GvcpTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(GvcpTimeoutMs), "must be positive");
        if (GvcpRetries < 0) throw new ArgumentOutOfRangeException(nameof(GvcpRetries), "must not be negative");
        if (HeartbeatTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(HeartbeatTimeoutMs), "must be positive");
        if (HeartbeatPeriodMs is <= 0) throw new ArgumentOutOfRangeException(nameof(HeartbeatPeriodMs), "must be positive when set");
        if (MaxPendingAckWaitMs is < 0) throw new ArgumentOutOfRangeException(nameof(MaxPendingAckWaitMs), "must not be negative when set");
    }
}
