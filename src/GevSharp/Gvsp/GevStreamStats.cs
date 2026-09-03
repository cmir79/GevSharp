namespace GevSharp;

/// <summary>
/// 스트림 카운터. 수신 스레드가 Interlocked 로 올리고, 소비자는 개별 프로퍼티나 <see cref="Snapshot"/> 으로 읽는다.
/// 프로퍼티는 각각 원자적이지만 서로 간의 일관성은 없다 — 한 시점의 값이 필요하면 스냅샷을 쓴다.
/// </summary>
public sealed class GevStreamStats
{
    private long _framesCompleted;
    private long _framesIncomplete;
    private long _framesDroppedNoBuffer;
    private long _framesDroppedError;
    private long _framesDroppedUnsupported;
    private long _framesDelivered;
    private long _packetsReceived;
    private long _packetsResent;
    private long _packetsMissing;
    private long _packetsDuplicated;
    private long _packetsIgnored;
    private long _firewallKeepAlives;
    private long _packetsUnsupported;
    private long _resendRequests;
    private long _resendRecovered;
    private long _errorPackets;
    private long _bytesReceived;
    private long _lastFrameId;

    /// <summary>모든 패킷이 모여 완성된 프레임 수.</summary>
    public long FramesCompleted => Volatile.Read(ref _framesCompleted);

    /// <summary>포기한(불완전) 프레임 수. 전달 여부와 무관하게 센다.</summary>
    public long FramesIncomplete => Volatile.Read(ref _framesIncomplete);

    /// <summary>풀에 빈 버퍼가 없어 버린 프레임 수.</summary>
    public long FramesDroppedNoBuffer => Volatile.Read(ref _framesDroppedNoBuffer);

    /// <summary>리더가 깨졌거나 크기가 맞지 않아 버린 프레임 수.</summary>
    public long FramesDroppedError => Volatile.Read(ref _framesDroppedError);

    /// <summary>이미지가 아닌 페이로드라서 버린 프레임 수.</summary>
    public long FramesDroppedUnsupported => Volatile.Read(ref _framesDroppedUnsupported);

    /// <summary>소비자가 <see cref="GevStream.ReceiveAsync"/>/<see cref="GevStream.TryReceive"/> 로 실제로 받아 간 프레임 수.</summary>
    public long FramesDelivered => Volatile.Read(ref _framesDelivered);

    /// <summary>소켓에서 읽은 데이터그램 수(오류·중복 포함).</summary>
    public long PacketsReceived => Volatile.Read(ref _packetsReceived);

    /// <summary>상태 0x0100 으로 도착한 패킷 수.</summary>
    public long PacketsResent => Volatile.Read(ref _packetsResent);

    /// <summary>포기한 프레임들에서 끝내 못 받은 페이로드 패킷 수의 합.</summary>
    public long PacketsMissing => Volatile.Read(ref _packetsMissing);

    /// <summary>이미 받은 id 로 다시 온 패킷 수.</summary>
    public long PacketsDuplicated => Volatile.Read(ref _packetsDuplicated);

    /// <summary>해석할 수 없거나(헤더 미달·범위 밖) 이미 닫힌 프레임의 것이라 버린 패킷 수.</summary>
    public long PacketsIgnored => Volatile.Read(ref _packetsIgnored);

    /// <summary>다루지 않는 content type(H.264·멀티존·멀티파트·GenDC)의 패킷 수.</summary>
    public long PacketsUnsupported => Volatile.Read(ref _packetsUnsupported);

    /// <summary>인바운드가 끊긴 사이 방화벽 매핑을 살리려고 보낸 한 바이트의 수. 스트림이 조용했던 적이 없거나 기능이 꺼져 있으면 0 이다.</summary>
    public long FirewallKeepAlives => Volatile.Read(ref _firewallKeepAlives);

    /// <summary>리센드를 요청한 패킷 수(명령 수가 아니라 요청 범위에 든 패킷 수의 합).</summary>
    public long ResendRequests => Volatile.Read(ref _resendRequests);

    /// <summary>리센드 패킷이 실제로 구멍을 메운 횟수.</summary>
    public long ResendRecovered => Volatile.Read(ref _resendRecovered);

    /// <summary>상태 0x8xxx 로 도착한 패킷 수.</summary>
    public long ErrorPackets => Volatile.Read(ref _errorPackets);

    /// <summary>소켓에서 읽은 바이트 수(GVSP 헤더 포함, IP/UDP 헤더 제외).</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>마지막으로 완성된 프레임의 블록 ID.</summary>
    public ulong LastFrameId => (ulong)Volatile.Read(ref _lastFrameId);

    internal void IncFramesCompleted() => Interlocked.Increment(ref _framesCompleted);
    internal void IncFramesIncomplete() => Interlocked.Increment(ref _framesIncomplete);
    internal void IncFramesDroppedNoBuffer() => Interlocked.Increment(ref _framesDroppedNoBuffer);
    internal void IncFramesDroppedError() => Interlocked.Increment(ref _framesDroppedError);
    internal void IncFramesDroppedUnsupported() => Interlocked.Increment(ref _framesDroppedUnsupported);
    internal void IncFramesDelivered() => Interlocked.Increment(ref _framesDelivered);
    internal void IncPacketsReceived() => Interlocked.Increment(ref _packetsReceived);
    internal void IncPacketsResent() => Interlocked.Increment(ref _packetsResent);
    internal void AddPacketsMissing(int n) => Interlocked.Add(ref _packetsMissing, n);
    internal void IncPacketsDuplicated() => Interlocked.Increment(ref _packetsDuplicated);
    internal void IncPacketsIgnored() => Interlocked.Increment(ref _packetsIgnored);
    internal void IncPacketsUnsupported() => Interlocked.Increment(ref _packetsUnsupported);
    internal void IncFirewallKeepAlives() => Interlocked.Increment(ref _firewallKeepAlives);
    internal void AddResendRequests(int n) => Interlocked.Add(ref _resendRequests, n);
    internal void IncResendRecovered() => Interlocked.Increment(ref _resendRecovered);
    internal void IncErrorPackets() => Interlocked.Increment(ref _errorPackets);
    internal void AddBytesReceived(int n) => Interlocked.Add(ref _bytesReceived, n);
    internal void SetLastFrameId(ulong id) => Interlocked.Exchange(ref _lastFrameId, (long)id);

    /// <summary>모든 카운터를 한 번에 읽는다. 각 값은 원자적이지만 카운터 간 순간 어긋남은 있을 수 있다.</summary>
    public GevStreamStatsSnap Snapshot() => new(
        FramesCompleted: FramesCompleted,
        FramesIncomplete: FramesIncomplete,
        FramesDroppedNoBuffer: FramesDroppedNoBuffer,
        FramesDroppedError: FramesDroppedError,
        FramesDroppedUnsupported: FramesDroppedUnsupported,
        FramesDelivered: FramesDelivered,
        PacketsReceived: PacketsReceived,
        PacketsResent: PacketsResent,
        PacketsMissing: PacketsMissing,
        PacketsDuplicated: PacketsDuplicated,
        PacketsIgnored: PacketsIgnored,
        PacketsUnsupported: PacketsUnsupported,
        FirewallKeepAlives: FirewallKeepAlives,
        ResendRequests: ResendRequests,
        ResendRecovered: ResendRecovered,
        ErrorPackets: ErrorPackets,
        BytesReceived: BytesReceived,
        LastFrameId: LastFrameId);
}

/// <summary><see cref="GevStreamStats.Snapshot"/> 결과 — 불변 복사본.</summary>
public readonly record struct GevStreamStatsSnap(
    long FramesCompleted,
    long FramesIncomplete,
    long FramesDroppedNoBuffer,
    long FramesDroppedError,
    long FramesDroppedUnsupported,
    long FramesDelivered,
    long PacketsReceived,
    long PacketsResent,
    long PacketsMissing,
    long PacketsDuplicated,
    long PacketsIgnored,
    long PacketsUnsupported,
    long FirewallKeepAlives,
    long ResendRequests,
    long ResendRecovered,
    long ErrorPackets,
    long BytesReceived,
    ulong LastFrameId);
