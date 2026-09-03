namespace GevSharp;

/// <summary>
/// 스트림 수신 옵션. 시간 값은 전부 밀리초.
/// 리센드 타이밍 3종은 "구멍 발견 → 유예 → 첫 요청 → 재요청 간격 → 프레임 포기" 순서로 작동한다.
/// </summary>
public sealed class GevStreamOpt
{
    /// <summary>풀에 두는 프레임 버퍼 수. 소비자가 들고 있는 프레임 + 큐 대기 + 조립 중 프레임의 합이 이 값을 넘으면 새 프레임은 버려진다.</summary>
    public int BufferCount { get; set; } = 8;

    public PacketSizeMode PacketSizeMode { get; set; } = PacketSizeMode.Auto;

    /// <summary>
    /// SCPS 값(IP+UDP 헤더 포함 패킷 크기). <see cref="PacketSizeMode.Fixed"/> 일 때 그대로 쓰고,
    /// <see cref="PacketSizeMode.Auto"/> 일 때는 <see cref="GevStream.StartAsync"/> 가 협상 결과를 여기에 되돌려 놓는다.
    /// </summary>
    public int PacketSize { get; set; } = 1500;

    /// <summary>수신 소켓 버퍼 요청 크기. OS 가 실제로 준 값은 시작 시 로그로 남는다.</summary>
    public int SocketBufferBytes { get; set; } = 32 * 1024 * 1024;

    public bool ResendEnabled { get; set; } = true;

    /// <summary>구멍을 처음 본 뒤 첫 리센드 요청까지 기다리는 시간 — 순서 바뀐 패킷이 스스로 도착할 여유.</summary>
    public int InitialPacketTimeoutMs { get; set; } = 2;

    /// <summary>같은 구멍에 대한 리센드 재요청 간격. 수신 루프의 주기적 점검 간격이기도 하다.</summary>
    public int PacketTimeoutMs { get; set; } = 20;

    /// <summary>마지막 패킷 도착 후 이 시간이 지나도록 완성되지 않은 프레임은 포기한다.</summary>
    public int FrameRetentionMs { get; set; } = 100;

    /// <summary>
    /// 한 프레임에서 리센드를 요청할 수 있는 서로 다른 패킷 수의 상한(예상 패킷 수 대비 비율). 넘으면 그 프레임을 포기한다.
    /// 같은 구멍을 다시 묻는 재요청은 이 상한을 쓰지 않는다 — 재요청은 <see cref="PacketTimeoutMs"/> 간격으로 <see cref="FrameRetentionMs"/> 까지만 되풀이되므로 그 자체로 유한하다.
    /// 장치가 프레임 도중 <see cref="PacketTimeoutMs"/> 만큼 쉬어 꼬리를 침묵으로 짐작한 경우에는 이 상한을 넘겨도 프레임을 포기하지 않는다 —
    /// 상한 안에 들어가는 앞부분만 묻고, 장치가 이어 보내면 프레임은 그대로 완성된다.
    /// </summary>
    public double PacketRequestRatio { get; set; } = 0.25;

    /// <summary>참이면 포기한 프레임도 <see cref="GevFrame.IsComplete"/> = false 로 전달한다(빠진 영역은 0 으로 채운다). 기본은 버리고 세기만 한다.</summary>
    public bool DeliverIncompleteFrames { get; set; } = false;

    /// <summary>
    /// 한 프레임이 가질 수 있는 최대 바이트 수(기본 256 MiB). 리더가 이보다 큰 기하를 알리면 그 프레임을 버리고,
    /// 청크 프레임이 배우는 버퍼 크기도 여기서 멈춘다.
    /// 스트림 소켓은 세그먼트의 누구나 보낼 수 있고 GVSP 는 보낸 이를 확인하지 않으므로(architecture.md 참조),
    /// 상한이 없으면 리더 한 장이 풀 버퍼 전부를 GB 단위로 잡게 만들 수 있다.
    /// 실제로 그만한 프레임을 보내는 장치라면 <see cref="PayloadSize"/> 와 함께 올린다.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>
    /// 채널을 연 뒤 장치의 스트림 송신 포트(SCSP)로 한 바이트를 보내 상태 기반 호스트 방화벽에 매핑을 만든다.
    /// 이런 방화벽(윈도우의 공용 프로필이 대표적)은 우리가 먼저 보낸 적 없는 UDP 를 전부 버리므로,
    /// 이 한 번의 송신이 없으면 인바운드 규칙을 관리자 권한으로 등록하기 전까지 GVSP 가 한 패킷도 도착하지 않는다.
    /// 장치가 SCSP 를 읽게 해 주지 않거나 0 이면 조용히 넘어간다.
    /// </summary>
    public bool FirewallTraversal { get; set; } = true;

    /// <summary>
    /// 인바운드가 이만큼 없으면 방화벽 통과용 한 바이트를 다시 보낸다(0 = 하지 않음). <see cref="FirewallTraversal"/> 이 꺼져 있으면 무시된다.
    /// 상태 기반 방화벽의 매핑은 유휴로 두면 만료된다 — 연속 스트리밍 중에는 들어오는 패킷이 매핑을 살리지만,
    /// 트리거 간격이 벌어지거나 획득을 멈춘 채 스트림을 열어 두면 만료돼 다음 프레임이 통째로 사라진다.
    /// 기본 15 초는 흔한 유휴 만료(60 초)보다 넉넉히 짧고, 비용은 15 초에 한 바이트다.
    /// </summary>
    public int FirewallTraversalIntervalMs { get; set; } = 15_000;

    /// <summary>스트림 소켓 포트. null 이면 임시 포트.</summary>
    public int? LocalPort { get; set; }

    /// <summary>
    /// SCPD(패킷 간 지연, 타임스탬프 틱). 요청한 값과 장치 값이 다르면 맞춘다 — <b>0 은 "지연 없음" 이지 "그대로 두기" 가 아니다.</b>
    /// 앞선 세션이 남긴 지연을 그대로 두면 프레임레이트가 조용히 깎인다.
    /// <para>
    /// 틱은 장치마다 주파수가 다르므로(실측 125 MHz 와 66.67 MHz) 같은 시간도 다른 숫자가 된다 —
    /// 시간으로 다루려면 <see cref="GevDevice.TimestampTickFrequency"/> 로 환산한다.
    /// </para>
    /// </summary>
    public int InterPacketDelay { get; set; } = 0;

    public ThreadPriority ReceiverPriority { get; set; } = ThreadPriority.AboveNormal;

    /// <summary>프레임 버퍼 초기 크기. null 이면 첫 리더가 알려 주는 크기로 풀이 늦게 자란다. 청크가 붙는 프레임은 PayloadSize 노드 값을 넣어 주는 편이 좋다.</summary>
    public int? PayloadSize { get; set; }

    /// <summary>값 범위를 검사한다. 잘못된 값은 <see cref="ArgumentOutOfRangeException"/>.</summary>
    internal void Validate()
    {
        if (BufferCount < 1) throw new ArgumentOutOfRangeException(nameof(BufferCount), "BufferCount must be at least 1.");
        if (PacketSize < GevStream.MinPacketSize || PacketSize > GevStream.MaxPacketSize)
            throw new ArgumentOutOfRangeException(nameof(PacketSize), $"PacketSize must be between {GevStream.MinPacketSize} and {GevStream.MaxPacketSize}.");
        if (SocketBufferBytes < 0) throw new ArgumentOutOfRangeException(nameof(SocketBufferBytes));
        if (InitialPacketTimeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(InitialPacketTimeoutMs));
        if (PacketTimeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(PacketTimeoutMs), "PacketTimeoutMs must be at least 1.");
        if (FrameRetentionMs < 1) throw new ArgumentOutOfRangeException(nameof(FrameRetentionMs), "FrameRetentionMs must be at least 1.");
        if (PacketRequestRatio < 0 || PacketRequestRatio > 1 || double.IsNaN(PacketRequestRatio))
            throw new ArgumentOutOfRangeException(nameof(PacketRequestRatio), "PacketRequestRatio must be within [0, 1].");
        if (MaxPayloadBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes), "MaxPayloadBytes must be positive.");
        if (FirewallTraversalIntervalMs < 0) throw new ArgumentOutOfRangeException(nameof(FirewallTraversalIntervalMs), "FirewallTraversalIntervalMs must not be negative.");
        if (LocalPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(LocalPort));
        if (InterPacketDelay < 0) throw new ArgumentOutOfRangeException(nameof(InterPacketDelay));
        if (PayloadSize is < 0) throw new ArgumentOutOfRangeException(nameof(PayloadSize));
    }
}
