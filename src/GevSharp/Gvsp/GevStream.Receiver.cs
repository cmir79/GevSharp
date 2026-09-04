using System.Diagnostics;
using System.Net.Sockets;
using GevSharp.Gvcp;
using GevSharp.Gvsp;

namespace GevSharp;

/// <summary>
/// 수신 스레드. 패킷마다 헤더를 읽고, 리더로 프레임 슬롯을 열고, 페이로드를 (id−1)×패킷데이터 오프셋에 복사하고, 비트 배열로 도착을 기록한다.
/// 구멍은 "지금까지 받은 가장 높은 id 아래의 못 받은 id" 다 — 아직 보내지지 않았을 뿐인 꼬리는 구멍이 아니다. 꼬리는 트레일러가 왔거나,
/// 더 새로운 블록이 시작됐거나, 패킷 간격 시간 동안 아무것도 안 왔을 때 비로소 구멍으로 친다.
/// 구멍은 "처음 본 시각 + 유예" 가 지나면 연속 범위별로 리센드를 요청하고, 재요청 간격을 두고 반복하며,
/// 요청해 본 서로 다른 패킷 수가 예산(예상 패킷 수 × 비율)을 넘으면 그 프레임을 포기한다 — 다만 침묵만으로 짐작한 꼬리는 예외로,
/// 같은 크기의 별도 예산 안에서 앞부분만 묻고 프레임은 살려 둔다(장치가 프레임 도중 잠깐 쉰 것일 수 있다).
/// 장치가 어떤 패킷을 더는 갖고 있지 않다고 답하면 그 구멍만 포기하며, 마지막 데이터 패킷 뒤 보존 시간이 지나면 프레임을 포기한다.
/// 프레임은 연 순서대로만 닫는다(오래된 프레임이 기다리는 동안 새 프레임은 내보내지 않는다).
/// 패킷 경로에서는 할당하지 않는다 — 슬롯·비트 배열은 재사용하고, 로그는 레벨을 먼저 확인한다.
/// </summary>
public sealed partial class GevStream
{
    private const int MaxInFlightFrames = 4;
    private const int IdleReceiveTimeoutMs = 200;
    private const int ScratchSlackBytes = 64;
    private const int RecentClosedCount = 8;
    /// <summary>한 프레임의 패킷 id 상한 — 비트·마감 배열 크기를 묶는다(576 바이트 패킷으로 140 MB 프레임까지).</summary>
    private const int MaxPacketsPerFrame = 1 << 18;
    /// <summary>분류되지 않은 소켓 오류가 이만큼 연속되면 수신을 포기한다(시도 사이 1 ms 이므로 약 1 초).</summary>
    private const int MaxConsecutiveReceiveErrors = 1000;
    /// <summary>장치가 못 준다고 답한 구멍의 마감 표식 — 기다리지도 요청하지도 않는다.</summary>
    private const long UnrecoverableDeadline = long.MaxValue;

    private byte[] _scratch = Array.Empty<byte>();
    private int _dataBytesStd;
    private int _dataBytesExt;
    private long _initialTimeoutTicks;
    private long _packetTimeoutTicks;
    private long _retentionTicks;
    /// <summary>한 프레임이 가질 수 있는 최대 바이트 수 — 리더가 알린 크기와 청크 프레임이 배우는 크기를 함께 묶는다.</summary>
    private int _maxPayloadBytes;
    private bool _hasLoggedPayloadCeiling;
    /// <summary>방화벽 매핑 유지 간격(틱). 0 이면 유지용 송신을 하지 않는다.</summary>
    private long _punchIntervalTicks;
    private long _lastInboundTicks;
    private long _lastPunchTicks;
    private int _activeReceiveTimeoutMs;
    private bool _isResendEnabled;
    private double _requestRatio;
    private bool _isDeliverIncomplete;
    private int _payloadSizeHint;
    private bool _hasLoggedChunkOverflow;
    private bool _hasLoggedShortLeader;

    private readonly FrameSlot?[] _active = new FrameSlot?[MaxInFlightFrames];
    private readonly FrameSlot[] _freeSlots = new FrameSlot[MaxInFlightFrames];
    private int _activeCount;
    private int _freeSlotCount;
    private readonly ulong[] _recentClosed = new ulong[RecentClosedCount];
    /// <summary>같은 자리의 닫힌 프레임이 리더에서 받은 타임스탬프(리더를 못 본 프레임은 0) — 늦게 온 리더가 그 프레임의 것인지 가린다.</summary>
    private readonly ulong[] _recentClosedTimestamp = new ulong[RecentClosedCount];
    private int _recentClosedNext;
    private int _recentClosedFilled;
    private int _currentReceiveTimeoutMs = -1;
    private int _consecutiveReceiveErrors;
    private SocketError _receiveExitError;
    private uint _loggedUnsupportedPayloadTypes;
    private bool _hasLoggedOtherPayloadType;
    /// <summary>콘텐츠 타입은 7 비트(0..127) — 두 워드로 값별 "한 번만" 을 전부 덮는다.</summary>
    private ulong _loggedUnsupportedContentLo;
    private ulong _loggedUnsupportedContentHi;
    private bool _hasLoggedOversize;

    /// <summary>조립 중인 프레임 하나. 슬롯 4 개를 미리 만들어 돌려쓴다.</summary>
    private sealed class FrameSlot
    {
        public ulong BlockId;
        public bool IsExtendedIds;
        public FrameBuf? Buf;
        public int BufVersion;
        public bool IsSkipped;
        public GevFrameDropReason SkipReason;
        public ushort SkipCode;
        public bool HasLeader;
        public bool HasTrailer;
        public FrameMeta Meta;
        /// <summary>이미지 바이트 수. −1 은 미정(청크가 붙는 프레임) — 트레일러가 패킷 수를 정한다.</summary>
        public long ExpectedBytes;
        public int DataBytes;
        public bool IsDataBytesLearned;
        /// <summary>페이로드 패킷 수 N. 0 은 미정.</summary>
        public int ExpectedPackets;
        public uint HighestPacketId;
        public int ReceivedPayloads;
        public long ReceivedEnd;
        /// <summary>아직 못 받은 가장 낮은 id(리더 0 포함) — 구멍 검사 시작점.</summary>
        public uint ScanStart;
        public long FirstPacketTicks;
        public long LastPacketTicks;
        /// <summary>리센드를 요청해 본 서로 다른 패킷 수 — 예산과 견주는 값이다. 같은 구멍의 재요청은 여기 더해지지 않는다.</summary>
        public int RequestedPackets;
        /// <summary>
        /// 침묵으로 짐작한 꼬리를 물어본 서로 다른 패킷 수. 진짜 유실의 예산과 따로 센다 — 장치가 아직 보내지도 않은 패킷을 물어본 것이
        /// 그 프레임의 진짜 구멍을 메울 여지를 잡아먹으면, 잠깐 쉬었을 뿐인 장치에서 뒤늦은 유실이 통째로 복구 불능이 된다.
        /// </summary>
        public int SpeculativePackets;
        public bool IsResendDisabled;
        public bool IsRatioReached;
        /// <summary>예상 패킷 수까지 전부를 구멍 후보로 봐도 되는지 — 트레일러 도착, 새 블록 시작, 또는 패킷 간격 시간의 침묵 뒤에 참.</summary>
        public bool IsTailKnown;
        /// <summary>
        /// 꼬리를 침묵만으로 짐작했는지 — 트레일러도 더 새로운 블록도 보지 못한 채 패킷 간격 시간의 침묵으로만 정했으면 참.
        /// 장치가 프레임 도중 잠깐 쉰 것일 수도 있으므로, 이렇게 짐작한 꼬리의 구멍은 요청 예산을 넘겨도 프레임을 버릴 근거가 되지 못한다.
        /// </summary>
        public bool IsTailAssumed;
        /// <summary>구멍 집합이 바뀌었을 수 있으니(id 건너뜀·꼬리 확정) 다음 기회에 훑어야 한다.</summary>
        public bool IsScanNeeded;
        /// <summary>가장 이른 구멍 마감(유예 또는 재요청) — 이 시각 전에는 훑지 않는다. 구멍이 없으면 long.MaxValue.</summary>
        public long NextDueTicks;

        /// <summary>더는 리센드를 요청하지 않는다 — 예산 소진이거나 장치가 패킷을 버렸다고 답했다.</summary>
        public bool IsGivenUp => IsRatioReached || IsResendDisabled;

        private uint[] _received = Array.Empty<uint>();
        /// <summary>이 프레임에서 리센드를 요청해 본 적이 있는 id — 예산은 서로 다른 패킷 수로 세므로 재요청은 다시 세지 않는다.</summary>
        private uint[] _requested = Array.Empty<uint>();
        private long[] _deadline = Array.Empty<long>();
        private int _capacity;
        private int _highWater;

        public void Reset(ulong blockId, bool extendedIds, long now)
        {
            if (_highWater > 0)
            {
                Array.Clear(_received, 0, (_highWater + 31) >> 5);
                Array.Clear(_requested, 0, (_highWater + 31) >> 5);
                Array.Clear(_deadline, 0, _highWater);
                _highWater = 0;
            }
            BlockId = blockId;
            IsExtendedIds = extendedIds;
            Buf = null;
            BufVersion = 0;
            IsSkipped = false;
            SkipReason = GevFrameDropReason.Incomplete;
            SkipCode = 0;
            HasLeader = false;
            HasTrailer = false;
            Meta = default;
            ExpectedBytes = -1;
            DataBytes = 0;
            IsDataBytesLearned = false;
            ExpectedPackets = 0;
            HighestPacketId = 0;
            ReceivedPayloads = 0;
            ReceivedEnd = 0;
            ScanStart = 0;
            FirstPacketTicks = now;
            LastPacketTicks = now;
            RequestedPackets = 0;
            SpeculativePackets = 0;
            IsResendDisabled = false;
            IsRatioReached = false;
            IsTailKnown = false;
            IsTailAssumed = false;
            IsScanNeeded = true;
            NextDueTicks = long.MaxValue;
        }

        /// <summary>id 0..ids−1 을 담을 수 있게 한다. 자라는 일은 드물다(첫 프레임, 해상도 증가).</summary>
        public void EnsureCapacity(int ids)
        {
            if (ids <= _capacity) return;
            var words = (ids + 31) >> 5;
            var newCapacity = words << 5;
            Array.Resize(ref _received, words);
            Array.Resize(ref _requested, words);
            Array.Resize(ref _deadline, newCapacity);
            _capacity = newCapacity;
        }

        public bool IsReceived(uint id) => id < (uint)_capacity && (_received[id >> 5] & (1u << (int)(id & 31))) != 0;

        public void MarkReceived(uint id)
        {
            _received[id >> 5] |= 1u << (int)(id & 31);
            Touch(id);
        }

        public bool IsWordFull(int word) => _received[word] == uint.MaxValue;

        public bool IsRequested(uint id) => id < (uint)_capacity && (_requested[id >> 5] & (1u << (int)(id & 31))) != 0;

        public void MarkRequested(uint id)
        {
            _requested[id >> 5] |= 1u << (int)(id & 31);
            Touch(id);
        }

        public long GetDeadline(uint id) => _deadline[id];

        public void SetDeadline(uint id, long value)
        {
            _deadline[id] = value;
            Touch(id);
        }

        /// <summary>1..N 중 받은 패킷 수를 비트에서 다시 센다 — N 이 바뀌었을 때.</summary>
        public int CountReceivedPayloads()
        {
            var n = ExpectedPackets;
            if (n <= 0) return 0;
            var count = 0;
            var lastWord = Math.Min((n >> 5), _received.Length - 1);
            for (var w = 0; w <= lastWord; w++)
            {
                var bits = _received[w];
                if (w == 0) bits &= ~1u;
                if (w == (n >> 5))
                {
                    var keep = (n & 31) + 1;
                    if (keep < 32) bits &= (1u << keep) - 1;
                }
                count += PopCount(bits);
            }
            return count;
        }

        private void Touch(uint id)
        {
            var next = (int)id + 1;
            if (next > _highWater) _highWater = next;
        }

        private static int PopCount(uint x)
        {
            x -= (x >> 1) & 0x5555_5555u;
            x = (x & 0x3333_3333u) + ((x >> 2) & 0x3333_3333u);
            x = (x + (x >> 4)) & 0x0F0F_0F0Fu;
            return (int)((x * 0x0101_0101u) >> 24);
        }
    }

    private void InitReceiver(int packetSize)
    {
        _scratch = new byte[Math.Max(packetSize, 9000) + ScratchSlackBytes];
        _dataBytesStd = GvspConst.DataBytesPerPacket(packetSize, extendedIds: false);
        _dataBytesExt = GvspConst.DataBytesPerPacket(packetSize, extendedIds: true);
        _initialTimeoutTicks = MsToTicks(_opt.InitialPacketTimeoutMs);
        _packetTimeoutTicks = MsToTicks(_opt.PacketTimeoutMs);
        _retentionTicks = MsToTicks(_opt.FrameRetentionMs);
        // 조립 중인 프레임이 있으면 가장 짧은 마감(유예) 간격으로 깨어나 구멍을 본다. 패킷이 흐르는 동안은 타임아웃이 걸리지 않는다.
        _activeReceiveTimeoutMs = Math.Max(1, Math.Min(_opt.InitialPacketTimeoutMs, _opt.PacketTimeoutMs));
        _maxPayloadBytes = Math.Min(_opt.MaxPayloadBytes, int.MaxValue - ScratchSlackBytes);
        _hasLoggedPayloadCeiling = false;
        _hasLoggedShortLeader = false;
        _punchIntervalTicks = _opt.FirewallTraversal && _opt.FirewallTraversalIntervalMs > 0
            ? MsToTicks(_opt.FirewallTraversalIntervalMs)
            : 0;
        _lastInboundTicks = Stopwatch.GetTimestamp();
        _isResendEnabled = _opt.ResendEnabled && _opt.PacketRequestRatio > 0;
        _requestRatio = _opt.PacketRequestRatio;
        _isDeliverIncomplete = _opt.DeliverIncompleteFrames;
        _payloadSizeHint = _opt.PayloadSize ?? 0;

        for (var i = 0; i < MaxInFlightFrames; i++)
        {
            _freeSlots[i] = new FrameSlot();
            _active[i] = null;
        }
        _freeSlotCount = MaxInFlightFrames;
        _activeCount = 0;
        _recentClosedNext = 0;
        _recentClosedFilled = 0;
        _currentReceiveTimeoutMs = -1;
    }

    private static long MsToTicks(int ms) => (long)ms * Stopwatch.Frequency / 1000;

    private void ReceiveLoop()
    {
        var socket = _socket;
        if (socket is null) return;
        GevLog.Debug(_logSrc, $"Receiver thread started on port {LocalPort}.");

        try
        {
            while (!_isStopRequested)
            {
                int length;
                try
                {
                    // 타임아웃 조정도 try 안에서 — 정지 중 닫힌 소켓은 여기서도 ObjectDisposedException 을 내며, 그것은 오류가 아니라 정상 종료다.
                    UpdateReceiveTimeout(socket);
                    length = socket.Receive(_scratch, 0, _scratch.Length, SocketFlags.None);
                }
                catch (SocketException ex)
                {
                    if (_isStopRequested || !HandleReceiveError(ex)) break;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _consecutiveReceiveErrors = 0;
                OnPacket(length, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception ex)
        {
            GevLog.Error(_logSrc, "Receiver thread terminated by an unexpected error.", ex);
            _queue?.Complete(new GevStreamClosedException("Receiver thread failed: " + ex.Message));
        }
        finally
        {
            AbandonAllFrames();
            if (!_isStopRequested)
            {
                // 정지 요청 없이 나왔다면 소켓이 죽은 것이다 — 소비자가 영원히 기다리지 않게 큐를 닫는다(이미 닫혔으면 무시된다).
                _queue?.Complete(new GevStreamClosedException($"Receiver thread stopped: stream socket receive failed ({_receiveExitError})."));
            }
            GevLog.Debug(_logSrc, $"Receiver thread on port {LocalPort} exited.");
        }
    }

    /// <summary>수신 오류 분류. 계속 돌아도 되면 true, 루프를 끝내야 하면 false.</summary>
    private bool HandleReceiveError(SocketException ex)
    {
        switch (ex.SocketErrorCode)
        {
            case SocketError.TimedOut:
            case SocketError.WouldBlock:
            // IOPending 은 "겹친 수신이 아직 끝나지 않았다" 는 뜻이지 오류가 아니다 — 이번 호출에 데이터가 실려 오지 않았을 뿐이고
            // 잃은 것도 없다. 윈도우에서 수신 타임아웃을 오가며 바꾸는 블로킹 소켓이 이따금 이 값을 돌려준다(실카메라 풀레이트
            // 60 초에 7 회 관측, 그 구간에도 누락 패킷 0). 오류로 다루면 경고가 쌓이고 1 ms 를 자는 사이 침묵이 길어져
            // 보내지도 않은 꼬리를 재요청하게 된다 — 타임아웃과 똑같이 "한 번 더 받아 보자" 로 넘긴다.
            case SocketError.IOPending:
            {
                var now = Stopwatch.GetTimestamp();
                MaybePunchFirewall(now);
                OnTick(now);
                return true;
            }
            case SocketError.MessageSize:
                _stats.IncPacketsIgnored();
                if (!_hasLoggedOversize)
                {
                    _hasLoggedOversize = true;
                    GevLog.Warn(_logSrc, $"Received a datagram larger than the scratch buffer ({_scratch.Length} bytes); the device sends packets bigger than the negotiated size {PacketSize}.");
                }
                return true;
            case SocketError.ConnectionReset:
                // 데이터그램 소켓의 ICMP 되돌림 — 오류가 아니다.
                return true;
            case SocketError.Interrupted:
            case SocketError.OperationAborted:
            case SocketError.NotSocket:
            case SocketError.Shutdown:
                _receiveExitError = ex.SocketErrorCode;
                return false;
            default:
                // 분류되지 않은 오류 — 처음 한 번만 남기고 잠깐 쉬었다 다시 시도하되, 계속되면 수신을 포기한다(무한 재시도·로그 홍수 방지).
                _consecutiveReceiveErrors++;
                if (_consecutiveReceiveErrors == 1)
                {
                    GevLog.Warn(_logSrc, $"Stream socket receive failed: {ex.SocketErrorCode}; retrying.", ex);
                }
                else if (_consecutiveReceiveErrors >= MaxConsecutiveReceiveErrors)
                {
                    GevLog.Error(_logSrc, $"Stream socket receive kept failing with {ex.SocketErrorCode} for {_consecutiveReceiveErrors} consecutive attempts; receiver stopped.", ex);
                    _receiveExitError = ex.SocketErrorCode;
                    return false;
                }
                Thread.Sleep(1);
                return true;
        }
    }

    private void UpdateReceiveTimeout(Socket socket)
    {
        var desired = _activeCount > 0 ? _activeReceiveTimeoutMs : IdleReceiveTimeoutMs;
        if (desired == _currentReceiveTimeoutMs) return;
        socket.ReceiveTimeout = desired;
        _currentReceiveTimeoutMs = desired;
    }

    /// <summary>
    /// 인바운드가 오래 끊겼으면 방화벽 매핑을 살리는 한 바이트를 다시 보낸다. 상태 기반 방화벽의 매핑은 유휴로 두면 만료되고,
    /// 그러면 다시 흐르기 시작한 GVSP 가 통째로 버려진다 — 트리거 간격이 벌어지거나 획득을 멈춘 채 스트림을 열어 둔 경우다.
    /// 패킷이 흐르는 동안에는 여기까지 오지 않는다(수신이 타임아웃될 때만 불린다).
    /// </summary>
    private void MaybePunchFirewall(long now)
    {
        if (_punchIntervalTicks == 0) return;
        if (now - _lastInboundTicks < _punchIntervalTicks) return;
        if (now - _lastPunchTicks < _punchIntervalTicks) return;

        var socket = _socket;
        if (socket is null) return;
        _lastPunchTicks = now;
        if (SendPunch(socket))
        {
            _stats.IncFirewallKeepAlives();
            if (GevLog.IsEnabled(GevLogLevel.Debug))
                GevLog.Debug(_logSrc, $"Firewall traversal: refreshed the mapping after {(now - _lastInboundTicks) * 1000 / Stopwatch.Frequency} ms without inbound packets.");
        }
    }

    private void OnTick(long now) => CheckCompletion(now, null);

    // ---------------------------------------------------------------- 패킷 분류

    private void OnPacket(int length, long now)
    {
        // 들어오는 패킷 자체가 방화벽 매핑을 살린다 — 마지막 인바운드 시각만 적어 두고 유지용 송신은 조용할 때만 한다.
        _lastInboundTicks = now;
        _stats.IncPacketsReceived();
        _stats.AddBytesReceived(length);

        if (!GvspPacketView.TryParse(_scratch, length, out var view))
        {
            _stats.IncPacketsIgnored();
            return;
        }

        var slot = FindSlot(view.BlockId);

        if (view.IsError)
        {
            _stats.IncErrorPackets();
            if (slot is not null)
            {
                // 오류 답신은 프레임의 진행이 아니다 — 마지막 패킷 시각을 밀지 않는다. 밀어 주면 "조금 있다 다시 물어라"(0x8010·0x8014)로
                // 답하는 장치에서 재요청과 오류가 서로를 되먹여 보존 시간이 영영 지나지 않고, 프레임과 그 버퍼가 갇힌다.
                // 프레임을 살려 두는 것은 실제로 도착한 데이터(원본이든 리센드 사본이든)뿐이다.
                // 이 시각에는 판정이 둘 걸려 있다: 보존 시간(프레임 포기)과 침묵으로 짐작하는 꼬리 확정. 오류 답신은 둘 다 미루지 않는다 —
                // 장치가 "그 패킷은 못 준다" 고 답한 것이 아직 보낼 것이 남았다는 뜻은 아니기 때문이다. 짐작한 꼬리는 그 자체로는
                // 프레임을 버리지 못하므로(CanAbandonForRange) 일찍 확정돼도 멀쩡한 프레임을 잃지 않는다.
                if (view.Status == GvcpConst.StatusPacketUnavailable || view.Status == GvcpConst.StatusPacketRemovedFromMemory)
                {
                    MarkUnrecoverable(slot, view.PacketId, andPrevious: false);
                }
                else if (view.Status == GvcpConst.StatusPacketAndPrevRemovedFromMemory)
                {
                    MarkUnrecoverable(slot, view.PacketId, andPrevious: true);
                }
            }
            if (GevLog.IsEnabled(GevLogLevel.Debug))
            {
                GevLog.Debug(_logSrc, $"Error packet status 0x{view.Status:X4} ({GvcpConst.StatusName(view.Status)}) for block {view.BlockId} packet {view.PacketId}.");
            }
            if (slot is not null && !slot.IsSkipped && slot.IsScanNeeded) CheckMissing(slot, now);
            CheckCompletion(now, slot);
            return;
        }

        if (view.IsResent) _stats.IncPacketsResent();

        switch (view.ContentType)
        {
            case GvspConst.ContentLeader:
                if (slot is null)
                {
                    if (!ShouldOpenForLeader(in view)) return;
                    slot = OpenSlot(view.BlockId, view.IsExtendedId, now);
                }
                OnLeader(slot, in view, now);
                break;

            case GvspConst.ContentPayload:
                if (slot is null)
                {
                    if (!ShouldOpenBlock(view.BlockId, view.IsExtendedId)) { _stats.IncPacketsIgnored(); return; }
                    slot = OpenSlot(view.BlockId, view.IsExtendedId, now);
                }
                OnPayload(slot, in view, now);
                break;

            case GvspConst.ContentTrailer:
                if (slot is null)
                {
                    // 마지막 페이로드가 오는 순간 프레임을 닫으므로 트레일러는 대개 닫힌 뒤에 온다 — 정상이라 세지 않는다.
                    if (IsRecentlyClosed(view.BlockId)) return;
                    if (!ShouldOpenBlock(view.BlockId, view.IsExtendedId)) { _stats.IncPacketsIgnored(); return; }
                    slot = OpenSlot(view.BlockId, view.IsExtendedId, now);
                }
                OnTrailer(slot, in view, now);
                break;

            case GvspConst.ContentAllIn:
                if (slot is null)
                {
                    if (!ShouldOpenForLeader(in view)) return;
                    slot = OpenSlot(view.BlockId, view.IsExtendedId, now);
                }
                OnAllIn(slot, in view, now);
                break;

            default:
                _stats.IncPacketsUnsupported();
                LogUnsupportedContentOnce(view.ContentType);
                if (slot is not null)
                {
                    slot.LastPacketTicks = now;
                    if (!slot.IsSkipped) MarkSkipped(slot, GevFrameDropReason.Unsupported, view.ContentType);
                }
                CheckCompletion(now, slot);
                return;
        }

        // 구멍 집합이 바뀌었거나 마감이 됐을 때만 훑는다 — 그 밖의 패킷은 O(1) 로 지나간다.
        if (!slot.IsSkipped && (slot.IsScanNeeded || now >= slot.NextDueTicks)) CheckMissing(slot, now);
        CheckCompletion(now, slot);
    }

    private FrameSlot? FindSlot(ulong blockId)
    {
        for (var i = 0; i < _activeCount; i++)
        {
            var s = _active[i]!;
            if (s.BlockId == blockId) return s;
        }
        return null;
    }

    /// <summary>
    /// 리더 없이 온 페이로드/트레일러로 슬롯을 열어도 되는지. 최근에 닫은 블록의 늦은 패킷이나, 조립 중인 것보다 오래된 블록은 열지 않는다.
    /// 리더가 뒤늦게 오거나 리센드로 되살아날 수 있으므로 새 블록이면 연다.
    /// </summary>
    private bool ShouldOpenBlock(ulong blockId, bool extendedIds)
    {
        if (IsRecentlyClosed(blockId)) return false;
        if (_activeCount > 0 && IsOlderBlock(blockId, _active[_activeCount - 1]!.BlockId, extendedIds)) return false;
        return true;
    }

    /// <summary>
    /// 리더(또는 올인)로 새 슬롯을 열어도 되는지. 리더는 원래 새 프레임의 시작이지만, 이미 닫힌 프레임의 리더가 뒤늦게 오면(리센드 사본, 또는
    /// 리센드 사본이 먼저 도착해 프레임을 끝낸 뒤 오는 원본) 새 슬롯을 열어 버퍼를 붙들고 가짜 불완전 프레임으로 끝난다.
    /// 증거가 있을 때만 거른다 — 최근에 닫은 블록이면서 리센드 표시가 있거나 타임스탬프가 닫힌 프레임과 같으면 중복이고, 조립 중인 것보다
    /// 오래된 블록의 리센드 리더는 닫힌 지 오래된 프레임의 것이라 무시한다. 그 밖의 오래된 블록 번호는 장치가 블록 번호를 다시 세기 시작한
    /// 것(촬영 재시작)이므로 새 프레임으로 연다 — 단일 프레임 촬영을 반복하는 장치는 매번 블록 1 부터 보낸다. 재시작으로 연 블록이
    /// 조립 중인 블록보다 새롭지 않은 경우의 꼬리 확정은 <see cref="OpenSlot"/> 이 블록 순서를 보고 건너뛴다.
    /// </summary>
    private bool ShouldOpenForLeader(in GvspPacketView view)
    {
        var closedAt = IndexOfRecentlyClosed(view.BlockId);
        if (closedAt >= 0)
        {
            var timestamp = GvspImageLeader.TryRead(view.Data, out var leader) ? leader.Timestamp : 0;
            if (view.IsResent || (timestamp != 0 && timestamp == _recentClosedTimestamp[closedAt]))
            {
                _stats.IncPacketsDuplicated();
                return false;
            }
            return true;
        }
        if (view.IsResent && _activeCount > 0 && IsOlderBlock(view.BlockId, _active[_activeCount - 1]!.BlockId, view.IsExtendedId))
        {
            _stats.IncPacketsIgnored();
            return false;
        }
        return true;
    }

    private static bool IsOlderBlock(ulong id, ulong newest, bool extendedIds)
    {
        if (id == newest) return false;
        if (extendedIds) return id < newest;
        return ((newest - id) & 0xFFFF) < 0x8000;
    }

    private FrameSlot OpenSlot(ulong blockId, bool extendedIds, long now)
    {
        if (_activeCount == MaxInFlightFrames)
        {
            if (GevLog.IsEnabled(GevLogLevel.Debug))
            {
                GevLog.Debug(_logSrc, $"Too many frames in flight; abandoning oldest block {_active[0]!.BlockId} to open block {blockId}.");
            }
            CloseSlot(0);
        }

        // 장치는 블록을 순서대로 보낸다 — 새 블록이 시작됐으면 그보다 오래된 블록들은 다 보내진 것이므로 꼬리도 구멍 후보가 된다.
        // 여는 블록이 정말 더 새로운 것만 확정한다 — 같거나 새로운 블록의 꼬리를 확정하면 아직 보내지지도 않은 패킷을 구멍으로 요청해
        // 요청 예산을 태우고 멀쩡한 프레임을 포기하게 된다.
        for (var i = 0; i < _activeCount; i++)
        {
            var older = _active[i]!;
            if (!older.IsTailKnown && IsOlderBlock(older.BlockId, blockId, extendedIds) && (older.ReceivedPayloads > 0 || older.HasTrailer))
            {
                older.IsTailKnown = true;
                older.IsTailAssumed = false;
                older.IsScanNeeded = true;
            }
        }

        var slot = _freeSlots[--_freeSlotCount];
        slot.Reset(blockId, extendedIds, now);
        slot.EnsureCapacity(1);
        _active[_activeCount++] = slot;
        return slot;
    }

    private bool IsRecentlyClosed(ulong blockId) => IndexOfRecentlyClosed(blockId) >= 0;

    private int IndexOfRecentlyClosed(ulong blockId)
    {
        for (var i = 0; i < _recentClosedFilled; i++)
        {
            if (_recentClosed[i] == blockId) return i;
        }
        return -1;
    }

    private void PushRecentClosed(ulong blockId, ulong timestamp)
    {
        _recentClosed[_recentClosedNext] = blockId;
        _recentClosedTimestamp[_recentClosedNext] = timestamp;
        _recentClosedNext = (_recentClosedNext + 1) % RecentClosedCount;
        if (_recentClosedFilled < RecentClosedCount) _recentClosedFilled++;
    }

    // ---------------------------------------------------------------- 리더 / 페이로드 / 트레일러

    private void OnLeader(FrameSlot slot, in GvspPacketView view, long now)
    {
        slot.LastPacketTicks = now;
        if (slot.HasLeader)
        {
            _stats.IncPacketsDuplicated();
            return;
        }
        if (slot.IsSkipped) return;

        // 종류를 먼저 본다. payload_type 은 어느 리더에나 같은 자리(데이터 [2:3])에 있고, 리더의 나머지 모양은 그 종류가 정한다 —
        // 이미지 리더로 먼저 읽으려 들면, 우리가 다루지 않는 종류가 "헤더가 깨졌다" 로 잘못 분류된다. 실측으로 그 일이 났다:
        // 청크 모드를 켠 장치가 payload_type 4(chunk data)의 12바이트 리더를 보내는데, 그것을 36바이트 이미지 리더로 읽으려다
        // 짧다고 버려 프레임이 한 장도 오지 않았고 통계에는 unsupported 가 아니라 error 만 쌓였다.
        if (!view.TryReadLeaderPayloadType(out var leaderType))
        {
            if (!_hasLoggedShortLeader)
            {
                _hasLoggedShortLeader = true;
                GevLog.Warn(_logSrc, $"Block {slot.BlockId}: leader carries {view.DataLength} bytes, too few to even name its payload type; frame dropped. Further occurrences are counted but not logged.");
            }
            MarkSkipped(slot, GevFrameDropReason.Error, GvcpConst.StatusInvalidHeader);
            return;
        }
        if (leaderType != GvspConst.PayloadImage && leaderType != GvspConst.PayloadExtendedChunkData)
        {
            LogUnsupportedPayloadOnce(leaderType);
            slot.HasLeader = true;
            MarkSkipped(slot, GevFrameDropReason.Unsupported, leaderType);
            return;
        }

        if (!view.TryReadImageLeader(out var leader))
        {
            // 종류는 이미지(또는 확장 청크)라고 해 놓고 리더가 짧다 — 장치가 자기 선언과 다른 것을 보낸 것이라 그대로 알린다.
            if (!_hasLoggedShortLeader)
            {
                _hasLoggedShortLeader = true;
                GevLog.Warn(_logSrc, $"Block {slot.BlockId}: leader declares payload type 0x{leaderType:X4} but carries only {view.DataLength} bytes, "
                    + $"fewer than the {GvspConst.ImageLeaderDataSize} that leader needs; frame dropped. Further occurrences are counted but not logged.");
            }
            MarkSkipped(slot, GevFrameDropReason.Error, GvcpConst.StatusInvalidHeader);
            return;
        }

        if (!ApplyLeader(slot, in leader, view.IsExtendedId)) return;

        // 리더도 "받은 패킷 0" 이다 — 리센드로 되살아난 리더는 구멍을 메운 것으로 센다.
        if (view.IsResent) _stats.IncResendRecovered();
        slot.MarkReceived(0);
        AdvanceScanStart(slot);
    }

    /// <summary>리더 필드를 슬롯에 옮기고 버퍼를 잡는다. 이 프레임을 조립할 수 없으면 슬롯을 건너뛰기로 표시하고 false.</summary>
    private bool ApplyLeader(FrameSlot slot, in GvspImageLeader leader, bool extendedIds)
    {
        slot.HasLeader = true;

        var payloadType = leader.PayloadTypeBase;
        if (payloadType != GvspConst.PayloadImage && payloadType != GvspConst.PayloadExtendedChunkData)
        {
            LogUnsupportedPayloadOnce(payloadType);
            MarkSkipped(slot, GevFrameDropReason.Unsupported, payloadType);
            return false;
        }

        var imageBytes = leader.ImageBytes;
        // 상한을 둔다 — 리더 한 장이 풀 버퍼 전부의 크기를 정하므로, 손상되거나 남이 보낸 리더가 GB 단위 할당을 부르면 안 된다.
        if (leader.SizeX == 0 || leader.SizeY == 0 || leader.SizeX > int.MaxValue || leader.SizeY > int.MaxValue
            || leader.OffsetX > int.MaxValue || leader.OffsetY > int.MaxValue
            || leader.LineBytes > int.MaxValue || imageBytes <= 0 || imageBytes > _maxPayloadBytes)
        {
            GevLog.Warn(_logSrc, $"Leader of block {slot.BlockId} carries an unusable geometry ({leader.SizeX}x{leader.SizeY}, format 0x{leader.PixelFormat:X8}, {imageBytes} bytes, limit {_maxPayloadBytes}); frame dropped.");
            MarkSkipped(slot, GevFrameDropReason.Error, GvcpConst.StatusInvalidParameter);
            return false;
        }

        var hasChunk = leader.HasChunkData;
        slot.ExpectedBytes = hasChunk ? -1 : imageBytes;
        slot.Meta.FrameId = slot.BlockId;
        slot.Meta.Timestamp = leader.Timestamp;
        slot.Meta.PixelFormatCode = leader.PixelFormat;
        slot.Meta.PayloadType = leader.PayloadType;
        slot.Meta.Width = (int)leader.SizeX;
        slot.Meta.Height = (int)leader.SizeY;
        slot.Meta.OffsetX = (int)leader.OffsetX;
        slot.Meta.OffsetY = (int)leader.OffsetY;
        slot.Meta.PaddingX = leader.PaddingX;
        slot.Meta.PaddingY = leader.PaddingY;
        // 줄이 바이트 경계에서 끝나지 않고 줄 패딩도 없으면 다음 줄이 바이트 가운데에서 시작한다 — 줄 간격이라는 것이 없으므로 0 을 내보낸다.
        slot.Meta.Stride = leader.PaddingX == 0 && !leader.IsLineByteAligned ? 0 : (int)leader.LineBytes;
        slot.Meta.PayloadSize = (int)imageBytes;
        slot.Meta.HasChunkData = hasChunk;

        if (slot.DataBytes == 0) slot.DataBytes = extendedIds ? _dataBytesExt : _dataBytesStd;

        // 청크가 붙으면 이미지보다 커진다 — 힌트(PayloadSize 옵션)나 지금까지의 최대 크기 중 큰 쪽을 잡는다.
        var needed = hasChunk
            ? Math.Max((int)imageBytes, Math.Max(_payloadSizeHint, _pool.BufferBytes))
            : (int)imageBytes;

        if (slot.Buf is null)
        {
            if (!_pool.TryRent(needed, out var buf, out var version))
            {
                MarkSkipped(slot, GevFrameDropReason.NoBuffer, 0);
                return false;
            }
            slot.Buf = buf;
            slot.BufVersion = version;
        }
        else if (slot.Buf.Data.Length < needed)
        {
            // 리더보다 먼저 온 페이로드로 잡아 둔 버퍼가 작다 — 이미 쓴 바이트를 옮길 길이 없으니 이 프레임은 포기한다.
            GevLog.Warn(_logSrc, $"Block {slot.BlockId}: payload arrived before the leader into a {slot.Buf.Data.Length}-byte buffer but the frame needs {needed} bytes; frame dropped.");
            MarkSkipped(slot, GevFrameDropReason.Error, GvcpConst.StatusOverflow);
            return false;
        }

        RecomputeExpectedPackets(slot);
        return !slot.IsSkipped;
    }

    private void OnPayload(FrameSlot slot, in GvspPacketView view, long now)
    {
        slot.LastPacketTicks = now;
        var id = view.PacketId;
        if (id == 0 || id >= MaxPacketsPerFrame)
        {
            _stats.IncPacketsIgnored();
            return;
        }

        var length = view.DataLength;
        if (slot.IsSkipped)
        {
            // 청크가 버퍼를 넘친 프레임은 버리되, 끝까지 크기를 배워 다음 프레임의 버퍼를 한 번에 맞춘다.
            // 리더가 청크를 선언한 프레임에서만 배운다 — 리더 없는 프레임의 패킷 id 는 크기의 근거가 되지 못한다.
            if (slot.SkipCode == GvcpConst.StatusOverflow && slot.HasLeader && slot.Meta.HasChunkData
                && slot.DataBytes > 0 && length > 0)
            {
                GrowPayloadHint((long)(id - 1) * slot.DataBytes + length);
            }
            return;
        }

        if (length <= 0)
        {
            _stats.IncPacketsIgnored();
            return;
        }

        if (slot.DataBytes == 0) slot.DataBytes = view.IsExtendedId ? _dataBytesExt : _dataBytesStd;
        LearnDataBytes(slot, id, length);
        if (slot.IsSkipped) return;

        slot.EnsureCapacity((int)id + 1);
        if (slot.IsReceived(id))
        {
            _stats.IncPacketsDuplicated();
            return;
        }

        var buf = slot.Buf;
        if (buf is null)
        {
            // 리더 전에 온 페이로드 — 지금까지 알려진 최대 프레임 크기로 버퍼를 잡아 둔다.
            var size = Math.Max(_pool.BufferBytes, _payloadSizeHint);
            if (size <= 0)
            {
                // 크기를 짐작할 근거가 없다 — 리더가 오면 그때 잡는다(이 패킷은 리센드로 다시 받는다).
                if (GevLog.IsEnabled(GevLogLevel.Debug))
                {
                    GevLog.Debug(_logSrc, $"Block {slot.BlockId}: payload {id} arrived before any leader; no buffer size known yet, packet dropped.");
                }
                _stats.IncPacketsIgnored();
                return;
            }
            if (!_pool.TryRent(size, out buf, out var version))
            {
                MarkSkipped(slot, GevFrameDropReason.NoBuffer, 0);
                return;
            }
            slot.Buf = buf;
            slot.BufVersion = version;
        }

        var offset = (long)(id - 1) * slot.DataBytes;
        var limit = slot.ExpectedBytes >= 0 ? slot.ExpectedBytes : buf.Data.Length;
        // ExpectedBytes < 0 은 "리더가 청크를 알렸고 최종 크기를 모른다" 는 뜻이라야 한다.
        // 리더를 못 본 프레임은 크기를 배울 근거가 없으니 버퍼를 넘는 패킷을 그냥 버린다(아래 offset >= limit 경로).
        if (slot.ExpectedBytes < 0 && slot.HasLeader && slot.Meta.HasChunkData && offset + length > limit)
        {
            // 청크가 붙은 프레임이 버퍼보다 크다 — 이 프레임은 버리고, 배운 크기로 다음 프레임의 버퍼를 키운다.
            GrowPayloadHint(offset + length);
            if (!_hasLoggedChunkOverflow)
            {
                _hasLoggedChunkOverflow = true;
                GevLog.Warn(_logSrc, $"Block {slot.BlockId}: chunk payload exceeds the {buf.Data.Length}-byte frame buffer; frame dropped and buffers will grow. Set GevStreamOpt.PayloadSize to the device PayloadSize to avoid this.");
            }
            MarkSkipped(slot, GevFrameDropReason.Error, GvcpConst.StatusOverflow);
            return;
        }
        if (offset >= limit)
        {
            _stats.IncPacketsIgnored();
            return;
        }
        // 마지막 패킷이 워드 정렬 등으로 조금 길어도 프레임 경계까지만 싣는다.
        var copyLength = (int)Math.Min(length, limit - offset);
        Buffer.BlockCopy(_scratch, view.DataOffset, buf.Data, (int)offset, copyLength);

        slot.MarkReceived(id);
        if (view.IsResent) _stats.IncResendRecovered();
        if (id > slot.HighestPacketId)
        {
            // id 를 건너뛰었다면 그 사이가 구멍이다 — 다음 훑기에서 유예 마감을 받는다.
            if (id > slot.HighestPacketId + 1) slot.IsScanNeeded = true;
            slot.HighestPacketId = id;
        }
        var end = offset + copyLength;
        if (end > slot.ReceivedEnd) slot.ReceivedEnd = end;
        if (slot.ExpectedPackets == 0 || id <= (uint)slot.ExpectedPackets) slot.ReceivedPayloads++;
        AdvanceScanStart(slot);
    }

    /// <summary>
    /// 패킷당 데이터 길이를 배운다. 기본은 SCPS 에서 계산한 값이고, 첫 페이로드(id 1)가 프레임보다 짧으면 그 길이가 진짜 값이다.
    /// id 1 을 못 받았으면 마지막이 아닌 것이 확실한 패킷(id &lt; 예상 수)의 길이로 배운다 — 아직 아무 바이트도 싣기 전이라 오프셋이 어긋나지 않는다.
    /// 기본값보다 긴 패킷이 오면 장치가 SCPS 를 무시하는 것이므로 그 길이를 따른다.
    /// </summary>
    private void LearnDataBytes(FrameSlot slot, uint id, int length)
    {
        if (!slot.IsDataBytesLearned)
        {
            if (id == 1)
            {
                slot.IsDataBytesLearned = true;
                var isWholeFrame = slot.ExpectedBytes >= 0 && length >= slot.ExpectedBytes;
                if (!isWholeFrame && length != slot.DataBytes) SetDataBytes(slot, length);
                return;
            }
            if (slot.ReceivedPayloads == 0 && slot.ExpectedPackets > 0 && id < (uint)slot.ExpectedPackets && length != slot.DataBytes)
            {
                slot.IsDataBytesLearned = true;
                SetDataBytes(slot, length);
                return;
            }
        }
        if (length > slot.DataBytes) SetDataBytes(slot, length);
    }

    /// <summary>
    /// 청크가 붙은 프레임이 버퍼를 넘쳤을 때, 다음 프레임의 버퍼를 한 번에 맞추려고 배운 크기를 키운다.
    /// **리더가 청크를 선언한 프레임에서만** 부른다 — 리더 없이 온 페이로드의 패킷 id 로 계산한 오프셋을 크기로 믿으면
    /// 패킷 하나가 힌트를 GB 단위로 부풀리고 그 뒤의 모든 풀 버퍼가 그 크기로 잡힌다.
    /// 상한(<see cref="GevStreamOpt.MaxPayloadBytes"/>)을 넘는 값은 배우지 않고 한 번 경고한다.
    /// </summary>
    private void GrowPayloadHint(long bytes)
    {
        if (bytes <= _payloadSizeHint) return;
        if (bytes > _maxPayloadBytes)
        {
            if (!_hasLoggedPayloadCeiling)
            {
                _hasLoggedPayloadCeiling = true;
                GevLog.Warn(_logSrc, $"A frame claims {bytes} bytes, above the {_maxPayloadBytes}-byte limit; buffers are not grown. Raise GevStreamOpt.MaxPayloadBytes if the device really sends frames that large.");
            }
            return;
        }
        _payloadSizeHint = (int)bytes;
    }

    private void SetDataBytes(FrameSlot slot, int dataBytes)
    {
        if (GevLog.IsEnabled(GevLogLevel.Debug))
        {
            GevLog.Debug(_logSrc, $"Block {slot.BlockId}: payload bytes per packet {slot.DataBytes} -> {dataBytes}.");
        }
        slot.DataBytes = dataBytes;
        RecomputeExpectedPackets(slot);
    }

    /// <summary>이미지 크기와 패킷당 데이터 길이에서 N 을 계산한다. 트레일러가 이미 N 을 알려 줬으면 그 값을 지킨다.</summary>
    private void RecomputeExpectedPackets(FrameSlot slot)
    {
        if (slot.ExpectedBytes < 0 || slot.DataBytes <= 0) return;
        var n = (slot.ExpectedBytes + slot.DataBytes - 1) / slot.DataBytes;
        if (n >= MaxPacketsPerFrame)
        {
            GevLog.Warn(_logSrc, $"Block {slot.BlockId}: {slot.ExpectedBytes} bytes at {slot.DataBytes} bytes/packet needs {n} packets, above the limit of {MaxPacketsPerFrame}; frame dropped.");
            MarkSkipped(slot, GevFrameDropReason.Error, GvcpConst.StatusInvalidParameter);
            return;
        }
        if (!slot.HasTrailer) slot.ExpectedPackets = (int)n;
        slot.EnsureCapacity(slot.ExpectedPackets + 2);
        slot.ReceivedPayloads = slot.CountReceivedPayloads();
    }

    private void OnTrailer(FrameSlot slot, in GvspPacketView view, long now)
    {
        slot.LastPacketTicks = now;
        if (slot.HasTrailer)
        {
            _stats.IncPacketsDuplicated();
            return;
        }

        // id 를 확인한 뒤에야 트레일러로 인정한다 — 먼저 표시해 두면 깨진 트레일러 하나가 리더의 패킷 수 계산까지 막아 프레임이 영영 안 닫힌다.
        var id = view.PacketId;
        if (id == 0 || id >= MaxPacketsPerFrame)
        {
            _stats.IncPacketsIgnored();
            return;
        }
        slot.HasTrailer = true;
        if (slot.IsSkipped) return;

        var n = (int)id - 1;
        slot.EnsureCapacity(n + 2);

        if (view.TryReadTrailer(out var trailer) && slot.HasLeader && slot.ExpectedBytes >= 0
            && trailer.SizeY > 0 && trailer.SizeY < (uint)slot.Meta.Height)
        {
            // 가변 높이: 실제 줄 수만큼만 유효하다.
            slot.Meta.Height = (int)trailer.SizeY;
            slot.ExpectedBytes = (long)slot.Meta.Stride * trailer.SizeY + slot.Meta.PaddingY;
            slot.Meta.PayloadSize = (int)slot.ExpectedBytes;
        }

        if (slot.ExpectedPackets != n && GevLog.IsEnabled(GevLogLevel.Debug))
        {
            GevLog.Debug(_logSrc, $"Block {slot.BlockId}: trailer sets packet count {slot.ExpectedPackets} -> {n}.");
        }
        slot.ExpectedPackets = n;
        slot.ReceivedPayloads = slot.CountReceivedPayloads();
        // 트레일러가 왔으니 1..N 전부가 구멍 후보다 — 짐작이 아니라 근거다.
        slot.IsTailKnown = true;
        slot.IsTailAssumed = false;
        slot.IsScanNeeded = true;
    }

    /// <summary>올인 패킷: 리더 36 바이트, 이미지 바이트, 끝에 트레일러 8 바이트가 한 데이터그램에 들어 있다.</summary>
    private void OnAllIn(FrameSlot slot, in GvspPacketView view, long now)
    {
        slot.LastPacketTicks = now;
        if (slot.HasLeader)
        {
            _stats.IncPacketsDuplicated();
            return;
        }
        if (slot.IsSkipped) return;

        var data = view.Data;
        if (!GvspImageLeader.TryRead(data, out var leader))
        {
            MarkSkipped(slot, GevFrameDropReason.Error, GvcpConst.StatusInvalidHeader);
            return;
        }
        if (!ApplyLeader(slot, in leader, view.IsExtendedId)) return;

        var available = data.Length - GvspConst.ImageLeaderDataSize;
        var payloadLength = slot.ExpectedBytes >= 0
            ? (int)Math.Min(slot.ExpectedBytes, available)
            : Math.Max(0, available - GvspConst.TrailerDataSize);
        var buf = slot.Buf!;
        if (payloadLength > buf.Data.Length || (slot.ExpectedBytes >= 0 && payloadLength < slot.ExpectedBytes))
        {
            GevLog.Warn(_logSrc, $"Block {slot.BlockId}: all-in packet carries {available} payload bytes but the frame needs {slot.ExpectedBytes}; frame dropped.");
            MarkSkipped(slot, GevFrameDropReason.Error, GvcpConst.StatusInvalidHeader);
            return;
        }

        Buffer.BlockCopy(_scratch, view.DataOffset + GvspConst.ImageLeaderDataSize, buf.Data, 0, payloadLength);
        slot.DataBytes = Math.Max(payloadLength, 1);
        slot.IsDataBytesLearned = true;
        slot.HasTrailer = true;
        slot.ExpectedPackets = 1;
        slot.EnsureCapacity(3);
        slot.MarkReceived(0);
        slot.MarkReceived(1);
        slot.ReceivedPayloads = 1;
        slot.HighestPacketId = 1;
        slot.ReceivedEnd = payloadLength;
        if (view.IsResent) _stats.IncResendRecovered();
        AdvanceScanStart(slot);
    }

    private static void AdvanceScanStart(FrameSlot slot)
    {
        while (slot.IsReceived(slot.ScanStart)) slot.ScanStart++;
    }

    private void MarkSkipped(FrameSlot slot, GevFrameDropReason reason, ushort code)
    {
        slot.IsSkipped = true;
        slot.SkipReason = reason;
        slot.SkipCode = code;
        if (slot.Buf is not null)
        {
            _pool.Return(slot.Buf, slot.BufVersion);
            slot.Buf = null;
        }
    }

    /// <summary>
    /// 장치가 더는 갖고 있지 않다고 답한 패킷(0x800C·0x8012 는 그 id, 0x8011 은 그 id 까지 전부)을 못 메우는 구멍으로 표시한다.
    /// 다른 구멍의 리센드는 계속되고, 남은 구멍이 전부 이런 것뿐이면 <see cref="CheckMissing"/> 이 프레임의 리센드를 끝낸다.
    /// 오류 패킷이 가리키는 id 를 쓸 수 없으면(0 이거나 범위 밖, 또는 리더까지 잃은 프레임) 어느 구멍인지 모르니 프레임 전체의 리센드를 끈다.
    /// </summary>
    private void MarkUnrecoverable(FrameSlot slot, uint packetId, bool andPrevious)
    {
        if (slot.IsSkipped) return;
        // 오류 패킷이 실어 온 id 는 장치가 준 값이라 믿고 배열을 잡거나 그만큼 훑으면 안 된다 —
        // 이 프레임이 담을 수 있는 범위(예상 패킷 수, 아직 모르면 지금까지 받은 최고 id)를 넘으면 어느 구멍인지 모른다고 보고
        // 프레임 전체의 리센드를 끈다. 그러지 않으면 한 패킷이 수 MB 배열과 수십만 회 루프를 수신 스레드에 떠안긴다.
        var maxUsableId = slot.ExpectedPackets > 0 ? (uint)slot.ExpectedPackets + 1 : slot.HighestPacketId;
        if (packetId == 0 || packetId >= MaxPacketsPerFrame || packetId > maxUsableId || (andPrevious && !slot.HasLeader))
        {
            slot.IsResendDisabled = true;
            return;
        }

        slot.EnsureCapacity((int)packetId + 1);
        var first = andPrevious ? 1u : packetId;
        for (var id = first; id <= packetId; id++)
        {
            if (!slot.IsReceived(id)) slot.SetDeadline(id, UnrecoverableDeadline);
        }
        slot.IsScanNeeded = true;
    }

    // ---------------------------------------------------------------- 구멍 검사와 리센드

    /// <summary>
    /// ScanStart 부터 검사 상한까지 못 받은 id 를 훑는다. 상한은 지금까지 받은 최고 id 이고, 꼬리가 확정된 뒤에는 예상 패킷 수 N 이다.
    /// 처음 본 구멍은 유예 마감을 받고, 마감이 지난 구멍은 연속 범위로 묶어 한 번에 요청한다. 요청해 본 서로 다른 패킷 수가 예산(N × 비율)을 넘으면 더 요청하지 않는다.
    /// 예산을 넘긴 프레임은 포기하지만, 침묵만으로 짐작한 꼬리(<see cref="FrameSlot.IsTailAssumed"/>)는 예외다 — 장치가 잠깐 쉰 것일 수도 있어
    /// 예산에 들어가는 앞부분만 묻고 프레임은 보존 시간까지 살려 둔다.
    /// 훑고 나면 가장 이른 마감을 <see cref="FrameSlot.NextDueTicks"/> 에 남겨 그 전에는 다시 훑지 않는다.
    /// </summary>
    private void CheckMissing(FrameSlot slot, long now)
    {
        slot.IsScanNeeded = false;
        slot.NextDueTicks = long.MaxValue;
        if (!_isResendEnabled || slot.IsGivenUp || slot.IsSkipped) return;

        var limit = slot.HighestPacketId;
        if (slot.ExpectedPackets > 0 && (slot.IsTailKnown || limit > (uint)slot.ExpectedPackets)) limit = (uint)slot.ExpectedPackets;
        if (slot.ScanStart > limit) return;
        var basis = slot.ExpectedPackets > 0 ? slot.ExpectedPackets : (int)limit;
        var maxRequests = Math.Max(1, (int)Math.Ceiling(basis * _requestRatio));
        slot.EnsureCapacity((int)limit + 1);

        var nextDue = long.MaxValue;
        var hasSent = false;
        var hasUnrecoverable = false;
        var inRange = false;
        uint rangeStart = 0;
        var id = slot.ScanStart;
        while (id <= limit)
        {
            // 다 받은 워드는 통째로 건너뛴다 — 구멍이 앞쪽에 하나 있을 때 전체를 훑지 않기 위해.
            if ((id & 31) == 0 && id + 31 <= limit && slot.IsWordFull((int)(id >> 5)))
            {
                if (inRange)
                {
                    if (!SendResend(slot, rangeStart, id - 1, now, maxRequests, CanAbandonForRange(slot, rangeStart))) return;
                    hasSent = true;
                    inRange = false;
                }
                id += 32;
                continue;
            }

            var due = false;
            if (!slot.IsReceived(id))
            {
                var deadline = slot.GetDeadline(id);
                if (deadline == UnrecoverableDeadline)
                {
                    // 장치가 못 준다고 답한 구멍 — 요청 범위를 끊고 지나간다.
                    hasUnrecoverable = true;
                }
                else
                {
                    if (deadline == 0)
                    {
                        deadline = now + _initialTimeoutTicks;
                        slot.SetDeadline(id, deadline);
                    }
                    if (now >= deadline) due = true;
                    else if (deadline < nextDue) nextDue = deadline;
                }
            }

            if (due)
            {
                if (!inRange)
                {
                    rangeStart = id;
                    inRange = true;
                }
            }
            else if (inRange)
            {
                if (!SendResend(slot, rangeStart, id - 1, now, maxRequests, CanAbandonForRange(slot, rangeStart))) return;
                hasSent = true;
                inRange = false;
            }
            id++;
        }

        if (inRange)
        {
            if (!SendResend(slot, rangeStart, limit, now, maxRequests, CanAbandonForRange(slot, rangeStart))) return;
            hasSent = true;
        }

        // 방금 요청한 구멍은 재요청 간격 뒤에 다시 본다.
        if (hasSent && now + _packetTimeoutTicks < nextDue) nextDue = now + _packetTimeoutTicks;
        slot.NextDueTicks = nextDue;

        // 꼬리까지 다 봤는데 기다릴 마감도 보낸 요청도 없고 못 메우는 구멍만 남았다 — 더 요청할 것이 없으니 프레임은 곧 닫힌다.
        if (hasUnrecoverable && !hasSent && nextDue == long.MaxValue && slot.IsTailKnown) slot.IsResendDisabled = true;
    }

    /// <summary>
    /// 이 범위의 구멍이 예산을 넘겼을 때 프레임을 포기해도 되는지. 장치가 실제로 지나간 id(지금까지 받은 최고 id 이하)의 구멍은 진짜 유실이라
    /// 포기해도 되지만, 침묵만으로 짐작한 꼬리는 장치가 잠깐 쉰 것일 수도 있어 그것만으로 프레임을 버리면 패킷 하나 잃지 않은 프레임까지 사라진다.
    /// 구멍 범위는 최고 id 를 가로지르지 않으므로(최고 id 는 받은 패킷이라 범위를 끊는다) 범위 시작만 보면 어느 쪽인지 갈린다.
    /// </summary>
    private static bool CanAbandonForRange(FrameSlot slot, uint rangeStart)
        => !slot.IsTailAssumed || rangeStart <= slot.HighestPacketId;

    /// <summary>
    /// [first, last] 리센드를 보낸다. 예산은 "이 프레임에서 리센드를 요청해 본 서로 다른 패킷 수" 다 — 같은 구멍을 다시 묻는 것은
    /// 예산을 더 쓰지 않는다. 재요청까지 예산에 얹으면 장치의 리센드 응답이 재요청 간격보다 느린 링크에서 손실률이 예산 안에 있는
    /// 프레임까지(패킷 하나만 남은 프레임까지) 버리게 된다. 재요청 횟수는 구멍마다 재요청 간격으로 한 번씩, 보존 시간까지만이라 그 자체로 유한하다.
    /// 예산을 넘으면 프레임을 포기 표시하고 false — 다만 <paramref name="canAbandon"/> 이 거짓인 범위(침묵만으로 짐작한 꼬리)는
    /// 예산에 들어가는 앞부분까지만 잘라 묻고 프레임은 그대로 둔다.
    /// 두 부류는 예산을 따로 쓴다: 진짜 유실은 <see cref="FrameSlot.RequestedPackets"/>, 짐작한 꼬리는 <see cref="FrameSlot.SpeculativePackets"/> 로
    /// 각각 상한이 <paramref name="maxRequests"/> 다. 한 통으로 세면 아직 보내지지도 않은 꼬리를 물어본 것이 그 프레임의 진짜 구멍을
    /// 메울 여지를 잡아먹어, 잠깐 쉰 장치에서 뒤늦은 유실이 복구 불능이 된다.
    /// </summary>
    private bool SendResend(FrameSlot slot, uint first, uint last, long now, int maxRequests, bool canAbandon)
    {
        var newPackets = 0;
        for (var id = first; id <= last; id++)
        {
            if (!slot.IsRequested(id)) newPackets++;
        }
        var spent = canAbandon ? slot.RequestedPackets : slot.SpeculativePackets;
        if (spent + newPackets > maxRequests)
        {
            if (canAbandon)
            {
                slot.IsRatioReached = true;
                if (GevLog.IsEnabled(GevLogLevel.Debug))
                {
                    GevLog.Debug(_logSrc, $"Block {slot.BlockId}: resend budget exhausted ({slot.RequestedPackets} packets requested, {newPackets} more needed, limit {maxRequests}); frame abandoned.");
                }
                return false;
            }

            // 예산에 남은 만큼만 앞에서부터 자른다. 이미 요청해 본 id 는 예산을 쓰지 않으므로 잘린 범위에 그대로 남는다.
            var room = maxRequests - spent;
            var taken = 0;
            var clamped = first;
            var hasAny = false;
            for (var id = first; id <= last; id++)
            {
                if (!slot.IsRequested(id))
                {
                    if (taken == room) break;
                    taken++;
                }
                clamped = id;
                hasAny = true;
            }
            // 예산이 남지 않아 새로 물을 것이 없다 — 이 범위는 그냥 지나간다(프레임은 살아 있다).
            if (!hasAny) return true;
            last = clamped;
            newPackets = taken;
        }

        var count = (int)(last - first + 1);
        try
        {
            _resend.SendPacketResend(slot.BlockId, first, last, slot.IsExtendedIds, _channel);
        }
        catch (Exception ex)
        {
            GevLog.Warn(_logSrc, $"Packet resend request for block {slot.BlockId} failed; resend disabled for this frame.", ex);
            slot.IsResendDisabled = true;
            return true;
        }

        if (canAbandon) slot.RequestedPackets += newPackets;
        else slot.SpeculativePackets += newPackets;
        _stats.AddResendRequests(count);
        var deadline = now + _packetTimeoutTicks;
        for (var id = first; id <= last; id++)
        {
            slot.MarkRequested(id);
            slot.SetDeadline(id, deadline);
        }

        if (GevLog.IsEnabled(GevLogLevel.Debug))
        {
            GevLog.Debug(_logSrc, $"Block {slot.BlockId}: resend requested for packets {first}..{last}.");
        }
        return true;
    }

    // ---------------------------------------------------------------- 프레임 닫기

    /// <summary>
    /// 오래된 순서로 슬롯을 본다. 완성됐거나 포기해야 할 프레임은 닫고, 아직 기다려야 하는 프레임을 만나면 그 뒤의 프레임은 닫지 않는다(순서 보존).
    /// 버퍼를 쥐지 않은(건너뛰기) 슬롯은 순서를 막지 않는다.
    /// 포기 시점: 리센드를 더 요청하지 않는 프레임(예산 소진·장치 거절·리센드 꺼짐)은 마지막 패킷 뒤 재요청 간격 하나만 더 기다리고,
    /// 그 밖의 프레임은 보존 시간까지 기다린다. 기다리는 프레임은 마감이 되거나 꼬리가 확정될 때 구멍을 다시 본다.
    /// </summary>
    private void CheckCompletion(long now, FrameSlot? current)
    {
        var canClose = true;
        var i = 0;
        while (i < _activeCount)
        {
            var slot = _active[i]!;
            var isNewest = i == _activeCount - 1;
            var idleTicks = now - slot.LastPacketTicks;

            if (slot.IsSkipped)
            {
                if (slot.HasTrailer || idleTicks >= _retentionTicks)
                {
                    CloseSlot(i);
                    continue;
                }
                i++;
                continue;
            }

            if (canClose)
            {
                if (IsComplete(slot))
                {
                    CloseSlot(i);
                    continue;
                }
                // 리더만 온 가장 새 프레임은 기다린다 — 노출이 긴 촬영에서 리더가 먼저 오는 장치가 있다.
                var isLoneLeader = isNewest && slot.HasLeader && slot.ReceivedPayloads == 0 && !slot.HasTrailer;
                if (!isLoneLeader)
                {
                    var isWaitingForResend = _isResendEnabled && !slot.IsGivenUp;
                    var giveUpTicks = isWaitingForResend ? _retentionTicks : _packetTimeoutTicks;
                    if (idleTicks >= giveUpTicks || (!_isResendEnabled && !isNewest))
                    {
                        CloseSlot(i);
                        continue;
                    }
                }
            }

            canClose = false;
            if (!ReferenceEquals(slot, current))
            {
                // 패킷 간격 시간 동안 침묵했으면 장치는 이 프레임을 다 보낸 것이다 — 꼬리도 구멍으로 친다.
                if (!slot.IsTailKnown && idleTicks >= _packetTimeoutTicks && (slot.ReceivedPayloads > 0 || slot.HasTrailer))
                {
                    slot.IsTailKnown = true;
                    slot.IsTailAssumed = true;
                    slot.IsScanNeeded = true;
                }
                if (slot.IsScanNeeded || now >= slot.NextDueTicks) CheckMissing(slot, now);
            }
            i++;
        }
    }

    private static bool IsComplete(FrameSlot slot)
        => slot.HasLeader && slot.Buf is not null && slot.ExpectedPackets > 0 && slot.ReceivedPayloads >= slot.ExpectedPackets;

    private void CloseSlot(int index)
    {
        var slot = _active[index]!;
        for (var j = index; j < _activeCount - 1; j++) _active[j] = _active[j + 1];
        _active[--_activeCount] = null;
        _freeSlots[_freeSlotCount++] = slot;
        PushRecentClosed(slot.BlockId, slot.HasLeader ? slot.Meta.Timestamp : 0);
        FinishSlot(slot);
    }

    /// <summary>닫힌 슬롯의 결과를 큐·카운터·이벤트로 내보내고 버퍼 소유권을 넘기거나 돌려준다.</summary>
    private void FinishSlot(FrameSlot slot)
    {
        if (slot.IsSkipped)
        {
            switch (slot.SkipReason)
            {
                case GevFrameDropReason.NoBuffer: _stats.IncFramesDroppedNoBuffer(); break;
                case GevFrameDropReason.Unsupported: _stats.IncFramesDroppedUnsupported(); break;
                default: _stats.IncFramesDroppedError(); break;
            }
            RaiseDropped(slot.BlockId, slot.SkipReason, 0, slot.ExpectedPackets, slot.SkipCode);
            return;
        }

        var buf = slot.Buf;
        if (IsComplete(slot))
        {
            FinalizePayloadSize(slot);
            slot.Meta.IsComplete = true;
            slot.Meta.MissingPackets = 0;
            slot.Meta.ExpectedPackets = slot.ExpectedPackets;
            _stats.IncFramesCompleted();
            _stats.SetLastFrameId(slot.BlockId);
            Enqueue(slot);
            return;
        }

        var expected = slot.ExpectedPackets > 0 ? slot.ExpectedPackets : (int)slot.HighestPacketId;
        var missing = Math.Max(0, expected - slot.ReceivedPayloads);
        _stats.IncFramesIncomplete();
        _stats.AddPacketsMissing(missing);
        if (GevLog.IsEnabled(GevLogLevel.Debug))
        {
            GevLog.Debug(_logSrc, $"Block {slot.BlockId} incomplete: {slot.ReceivedPayloads}/{expected} payload packets, leader {(slot.HasLeader ? "yes" : "no")}, {slot.RequestedPackets} packets requested for resend.");
        }
        RaiseDropped(slot.BlockId, GevFrameDropReason.Incomplete, missing, expected, 0);

        if (_isDeliverIncomplete && slot.HasLeader && buf is not null && slot.ExpectedPackets > 0)
        {
            ZeroHoles(slot);
            FinalizePayloadSize(slot);
            slot.Meta.IsComplete = false;
            slot.Meta.MissingPackets = missing;
            slot.Meta.ExpectedPackets = expected;
            Enqueue(slot);
            return;
        }

        if (buf is not null)
        {
            _pool.Return(buf, slot.BufVersion);
            slot.Buf = null;
        }
    }

    /// <summary>청크가 붙은 프레임은 리더로 크기를 알 수 없다 — 실제로 받은 끝까지를 유효 바이트로 삼는다.</summary>
    private static void FinalizePayloadSize(FrameSlot slot)
    {
        if (slot.ExpectedBytes < 0) slot.Meta.PayloadSize = (int)Math.Min(slot.ReceivedEnd, slot.Buf!.Data.Length);
    }

    /// <summary>불완전 프레임을 내보내기 전에 못 받은 패킷 자리를 0 으로 비운다 — 이전 프레임의 픽셀이 새어 보이지 않게.</summary>
    private static void ZeroHoles(FrameSlot slot)
    {
        var data = slot.Buf!.Data;
        var limit = slot.ExpectedBytes >= 0 ? slot.ExpectedBytes : slot.ReceivedEnd;
        for (uint id = 1; id <= (uint)slot.ExpectedPackets; id++)
        {
            if (slot.IsReceived(id)) continue;
            var offset = (long)(id - 1) * slot.DataBytes;
            if (offset >= limit) break;
            var length = (int)Math.Min(slot.DataBytes, limit - offset);
            Array.Clear(data, (int)offset, length);
        }
    }

    private void Enqueue(FrameSlot slot)
    {
        var frame = new GevFrame(_pool, slot.Buf!, slot.BufVersion, in slot.Meta);
        slot.Buf = null;
        if (!_queue!.TryEnqueue(frame))
        {
            // 큐 용량은 풀 크기와 같아 원래 여기 올 수 없다 — 왔다면 버퍼를 잃지 않게 돌려준다.
            frame.Dispose();
            _stats.IncFramesDroppedNoBuffer();
            RaiseDropped(slot.BlockId, GevFrameDropReason.NoBuffer, 0, slot.ExpectedPackets, 0);
        }
    }

    private void RaiseDropped(ulong blockId, GevFrameDropReason reason, int missing, int expected, ushort code)
    {
        var handler = FrameDropped;
        if (handler is null) return;
        try
        {
            handler(new GevFrameDiag(blockId, reason, missing, expected, code));
        }
        catch (Exception ex)
        {
            GevLog.Warn(_logSrc, "FrameDropped handler threw.", ex);
        }
    }

    /// <summary>스레드 종료 시 조립 중이던 프레임의 버퍼를 돌려준다. 통계에는 세지 않는다(중단이지 손실이 아니다).</summary>
    private void AbandonAllFrames()
    {
        for (var i = 0; i < _activeCount; i++)
        {
            var slot = _active[i]!;
            if (slot.Buf is not null)
            {
                _pool.Return(slot.Buf, slot.BufVersion);
                slot.Buf = null;
            }
            _freeSlots[_freeSlotCount++] = slot;
            _active[i] = null;
        }
        _activeCount = 0;
    }

    // ---------------------------------------------------------------- 한 번만 남기는 로그

    /// <summary>정의된 값(32 미만)은 값별로 한 번, 그 위의 예약·깨진 값은 통틀어 한 번만 — 리더마다 문자열을 만들거나 로그를 쏟지 않는다.</summary>
    private void LogUnsupportedPayloadOnce(ushort payloadType)
    {
        if (payloadType < 32)
        {
            var bit = 1u << payloadType;
            if ((_loggedUnsupportedPayloadTypes & bit) != 0) return;
            _loggedUnsupportedPayloadTypes |= bit;
        }
        else
        {
            if (_hasLoggedOtherPayloadType) return;
            _hasLoggedOtherPayloadType = true;
        }
        GevLog.Warn(_logSrc, $"Unsupported GVSP payload type {payloadType}; frames of this type are dropped (only image and extended-chunk payloads are assembled).");
    }

    /// <summary>콘텐츠 타입은 7 비트라 두 워드로 값별 한 번씩을 빠짐없이 거른다 — 패킷마다 오는 값이라 하나라도 새면 핫패스에서 문자열이 만들어진다.</summary>
    private void LogUnsupportedContentOnce(byte contentType)
    {
        var bit = 1ul << (contentType & 63);
        if (contentType < 64)
        {
            if ((_loggedUnsupportedContentLo & bit) != 0) return;
            _loggedUnsupportedContentLo |= bit;
        }
        else
        {
            if ((_loggedUnsupportedContentHi & bit) != 0) return;
            _loggedUnsupportedContentHi |= bit;
        }
        GevLog.Warn(_logSrc, $"Unsupported GVSP content type {contentType}; packets of this type are ignored.");
    }
}
