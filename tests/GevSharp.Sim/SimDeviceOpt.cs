using System.Net;

namespace GevSharp.Sim;

/// <summary>
/// 인프로세스 시뮬레이터 장치 옵션. 생성 시점에 한 번 읽어 부트스트랩·피처 레지스터를 채우고,
/// 전송 관련 값(DropPacket, MaxPacketSize, ResendHistoryFrames)은 실행 중에도 참조한다.
/// 실제 장치는 GVCP 를 3956 포트에서 듣지만, 테스트는 임시 포트(0)를 써서 여러 인스턴스를 동시에 띄운다.
/// </summary>
public sealed class SimDeviceOpt
{
    /// <summary>GVCP·GVSP 소켓을 묶을 IPv4 주소. 부트스트랩의 현재 IP 레지스터(0x0024)에도 그대로 들어간다. IPv4 가 아니면 생성자가 거절한다.</summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    /// <summary>
    /// GVCP 포트. 0 = 임시 포트. 소켓은 <see cref="BindAddress"/>(유니캐스트 주소)에 묶이므로 어느 포트에서든 브로드캐스트 DISCOVERY 는
    /// 받지 못한다 — <see cref="SimDevice.GvcpEndPoint"/> 로의 유니캐스트 프로브만 동작한다. 실제 장치와 같은 3956 을 지정하면
    /// 그 표준 포트로 오는 유니캐스트를 받는다(호스트당 한 인스턴스).
    /// </summary>
    public int GvcpPort { get; set; }

    /// <summary>장치가 내보낼 GenApi XML 본문. null = 내장 SimCamera.xml.</summary>
    public string? GenApiXml { get; set; }

    /// <summary>Width 피처 레지스터 초기값(픽셀).</summary>
    public int Width { get; set; } = 640;

    /// <summary>Height 피처 레지스터 초기값(픽셀).</summary>
    public int Height { get; set; } = 480;

    /// <summary>PixelFormat 피처 레지스터 초기값(PFNC 코드). 기본 Mono8.</summary>
    public uint PixelFormat { get; set; } = 0x01080001;

    /// <summary>AcquisitionFrameRate 초기값. 자유 실행 모드에서 프레임 간격을 정한다.</summary>
    public double FrameRateHz { get; set; } = 30;

    /// <summary>하트비트 타임아웃 레지스터(0x0938)의 초기값(ms). 제어권 보유자의 명령이 이 시간 동안 없으면 CCP 를 비운다.</summary>
    public int HeartbeatTimeoutMs { get; set; } = 3000;

    /// <summary>true 면 WRITEREG 에 PENDING_ACK 를 먼저 보내고 <see cref="PendingAckDelayMs"/> 뒤에 실제 ACK 를 보낸다. GVCP 능력 비트에도 반영된다.</summary>
    public bool SupportPendingAck { get; set; }

    /// <summary>PENDING_ACK 가 알리는 대기 시간이자 실제 ACK 까지의 지연(ms).</summary>
    public int PendingAckDelayMs { get; set; } = 100;

    /// <summary>GVSP 확장 ID(64비트 블록 ID·32비트 패킷 ID, 20바이트 헤더)의 초기 상태. SCCFG 레지스터로 실행 중에도 바꿀 수 있다.</summary>
    public bool ExtendedIds { get; set; }

    /// <summary>리센드 요청에 응할 수 있도록 보관하는 최근 프레임 수.</summary>
    public int ResendHistoryFrames { get; set; } = 8;

    /// <summary>
    /// (frameId, packetId) → true 면 그 페이로드 패킷을 첫 전송에서 버린다(손실 주입). 리더·트레일러와 리센드 사본에는 적용하지 않는다.
    /// 전송 스레드에서 호출되므로 가볍게 유지한다.
    /// </summary>
    public Func<ulong, uint, bool>? DropPacket { get; set; }

    /// <summary>SCPS 파이어테스트 패킷 크기 상한(IP 헤더 포함 바이트). 이보다 큰 테스트 요청은 조용히 무시해 협상 실패를 재현한다. null = 제한 없음.</summary>
    public int? MaxPacketSize { get; set; }

    /// <summary>SCPS 레지스터의 초기 패킷 크기(IP 헤더 포함 바이트). 실제 장치도 협상 전에 기본값을 갖는다.</summary>
    public int DefaultPacketSize { get; set; } = 1500;

    /// <summary>
    /// true 면 부트스트랩 예약 워드 <see cref="SimDevice.ReservedWordHoles"/>(0x0020, 0x0040)를 구현하지 않은 것처럼 군다 — 그 주소에서 시작하는
    /// READREG/READMEM 은 INVALID_ADDRESS 이고, 그 워드를 지나는 벌크 READMEM 은 응답에서 그 워드를 빼고 뒤의 워드를 당겨 요청한 길이를 채운다.
    /// 일부 실제 장치의 벌크 읽기 동작이라, 식별 블록을 한 번에 읽는 호스트는 0x0024 이후 필드가 밀려 보인다. 필드별 읽기 경로의 회귀 테스트용.
    /// DISCOVERY_ACK 는 영향을 받지 않는다(장치가 만드는 고정 이미지다).
    /// </summary>
    public bool HasReservedWordHoles { get; set; }

    public string Manufacturer { get; set; } = "GevSharp";
    public string Model { get; set; } = "SimCamera";
    public string DeviceVersion { get; set; } = "1.0";
    public string ManufacturerInfo { get; set; } = "in-process simulator";
    public string SerialNumber { get; set; } = "SIM0001";
    public string UserDefinedName { get; set; } = "";
}
