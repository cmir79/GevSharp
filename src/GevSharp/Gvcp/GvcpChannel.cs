using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GevSharp.Gvcp;

/// <summary>GVCP 채널 타이밍 옵션. 시간 단위는 ms.</summary>
public sealed class GvcpChannelOpt
{
    /// <summary>한 번 보내고 ACK 를 기다리는 시간.</summary>
    public int TimeoutMs { get; set; } = 500;
    /// <summary>첫 전송이 응답 없이 끝난 뒤 다시 보내는 횟수(총 전송 = 1 + Retries). 전부 응답이 없으면 <see cref="GevTimeoutException"/>.</summary>
    public int Retries { get; set; } = 3;
    /// <summary>세션이 따로 정해 주지 않았을 때의 <see cref="MaxPendingAckWaitMs"/>.</summary>
    public const int DefaultMaxPendingAckWaitMs = 10000;
    /// <summary>
    /// PENDING_ACK 가 요청한 추가 대기의 상한(한 요청 누적). PENDING_ACK 를 받은 요청은 재전송하지 않으므로
    /// 한 요청에서 연장을 받는 시도는 하나뿐이고, 이 값이 곧 그 요청이 연장으로 쓸 수 있는 전부다.
    /// </summary>
    public int MaxPendingAckWaitMs { get; set; } = DefaultMaxPendingAckWaitMs;
}

/// <summary>
/// 장치 하나와 주고받는 GVCP 제어 채널. UDP 소켓 하나, 수신 전용 백그라운드 스레드 하나.
/// 요청은 <see cref="SemaphoreSlim"/> 으로 직렬화되어 한 번에 하나만 나간다 — 장치는 명령을 하나씩 처리하고, 하트비트도 같은 줄에 선다.
/// 응답은 req_id 와 기대한 ack command 로 대조한다. 늦게 온 응답·남의 패킷은 세고 버린다.
/// PENDING_ACK 는 장치가 알려 준 시간만큼 대기를 연장한다(상한 <see cref="GvcpChannelOpt.MaxPendingAckWaitMs"/>).
/// 재시도는 같은 req_id 로 같은 패킷을 다시 보낸다 — 첫 전송의 늦은 응답도 그대로 유효하다.
/// 다만 PENDING_ACK 를 본 요청은 재전송하지 않고 그 자리에서 <see cref="GevTimeoutException"/> 으로 끝난다:
/// 재전송은 명령이 유실됐을 때를 위한 것인데 PENDING_ACK 는 명령이 도착해 실행 중이라는 장치의 대답이므로,
/// 다시 보내면 장치가 같은 명령을 두 번 실행할 수 있고 한 요청이 줄을 붙드는 시간도 재시도 횟수만큼 배가 된다.
/// </summary>
public sealed class GvcpChannel : IDisposable, IGvcpResendPort
{
    private const string LogSrc = "GvcpChannel";
    private const int RxBufferSize = 2048;
    private const int NoAckScratchSize = 64;
    private const int RxThreadJoinMs = 2000;
    /// <summary>수신 호출이 이만큼 연달아 실패하면 소켓이 살아날 가망이 없다고 보고 채널을 닫는다.</summary>
    private const int RxMaxConsecutiveFailures = 100;

    /// <summary>
    /// 오류 ACK 가 WRITEREG/WRITEMEM index 를 실어 오면 <see cref="GevStatusException"/> 의 <see cref="Exception.Data"/> 에 이 키로 int 를 넣는다 —
    /// 묶음 쓰기에서 몇 번째 항목이 거절됐는지(앞 항목은 적용됐고 그 항목부터는 아니다).
    /// </summary>
    public const string FailedIndexKey = "FailedIndex";

    private readonly Socket _socket;
    private readonly Thread _rxThread;
    private readonly SemaphoreSlim _reqLock = new(1, 1);
    /// <summary>요청 송신 버퍼 — <see cref="_reqLock"/> 안에서만 쓴다.</summary>
    private readonly byte[] _sendBuf = new byte[GvcpPacket.MaxCmdSize];
    /// <summary>ack 없는 명령용 작은 버퍼(netstandard2.0/2.1 은 SendTo 의 Span 오버로드가 없다) — <see cref="_noAckLock"/> 안에서만 쓴다.</summary>
    private readonly byte[] _noAckScratch = new byte[NoAckScratchSize];
    private readonly object _noAckLock = new();
    /// <summary>미리 직렬화해 둔 장치 주소 — byte[] 송신 오버로드가 호출마다 EndPoint 를 직렬화(할당)하지 않게 한다.</summary>
    private readonly EndPoint _sendTarget;
    private readonly GvcpChannelOpt _opt;
#if NET8_0_OR_GREATER
    /// <summary>장치 주소의 직렬화 사본 — Span 송신에 넘겨 호출마다 EndPoint 를 직렬화(할당)하지 않는다.</summary>
    private readonly SocketAddress _deviceSockAddr;
#endif

    private int _reqIdCounter;
    private volatile PendingRequest? _pending;
    private volatile bool _isDisposed;
    private long _staleAckCount;
    private long _foreignPacketCount;
    private long _malformedPacketCount;
    private long _pendingAckCount;

    public GvcpChannel(IPEndPoint device, IPAddress? localAddress = null, GvcpChannelOpt? opt = null)
    {
        DeviceEndPoint = device ?? throw new ArgumentNullException(nameof(device));
        if (device.AddressFamily != AddressFamily.InterNetwork)
            throw new GevException($"{device} is not an IPv4 endpoint; GVCP runs over IPv4 only");
        // 호출자가 준 인스턴스를 그대로 쥐지 않고 값만 옮겨 온다 — 채널이 세션에 맞춰 상한을 다시 정할 때(SetMaxPendingAckWaitMs)
        // 호출자의 객체를, 나아가 같은 객체로 만든 다른 채널까지 조용히 바꿔 놓지 않기 위해서다.
        // ⚠ GvcpChannelOpt 에 항목을 더하면 여기에도 더한다.
        _opt = opt is null
            ? new GvcpChannelOpt()
            : new GvcpChannelOpt { TimeoutMs = opt.TimeoutMs, Retries = opt.Retries, MaxPendingAckWaitMs = opt.MaxPendingAckWaitMs };
        if (_opt.TimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(opt), "TimeoutMs must be positive");
        if (_opt.Retries < 0) throw new ArgumentOutOfRangeException(nameof(opt), "Retries must not be negative");
        if (_opt.MaxPendingAckWaitMs < 0) throw new ArgumentOutOfRangeException(nameof(opt), "MaxPendingAckWaitMs must not be negative");
        _sendTarget = new PreSerializedEndPoint(device);
#if NET8_0_OR_GREATER
        _deviceSockAddr = device.Serialize();
#endif

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.Bind(new IPEndPoint(localAddress ?? IPAddress.Any, 0));
            GevNet.DisableIcmpReset(_socket);
            LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        }
        catch
        {
            _socket.Dispose();
            throw;
        }

        _rxThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = $"GevSharp GVCP rx {device}",
        };
        _rxThread.Start();
        GevLog.Debug(LogSrc, $"channel {LocalEndPoint} -> {DeviceEndPoint} opened");
    }

    public IPEndPoint LocalEndPoint { get; }
    public IPEndPoint DeviceEndPoint { get; }
    /// <summary>이 채널이 실제로 쓰는 타이밍 값(생성자에 넘긴 객체의 사본). 진단용으로 읽는다.</summary>
    public GvcpChannelOpt Opt => _opt;
    public bool IsDisposed => _isDisposed;

    /// <summary>req_id 가 맞지 않거나 대기 중인 요청이 없을 때 온 ACK 수.</summary>
    public long StaleAckCount => Interlocked.Read(ref _staleAckCount);
    /// <summary>장치 주소가 아닌 곳에서 온 패킷 수.</summary>
    public long ForeignPacketCount => Interlocked.Read(ref _foreignPacketCount);
    /// <summary>헤더·길이가 맞지 않는 패킷 수.</summary>
    public long MalformedPacketCount => Interlocked.Read(ref _malformedPacketCount);
    /// <summary>받은 PENDING_ACK 수.</summary>
    public long PendingAckCount => Interlocked.Read(ref _pendingAckCount);

    // ------------------------------------------------------------------ request / response

    /// <summary>
    /// 명령을 보내고 ACK 를 기다린다. 오류 status 는 <see cref="GevStatusException"/>, 재시도까지 무응답이면 <see cref="GevTimeoutException"/>,
    /// 응답이 깨졌으면 <see cref="GevException"/>, 채널이 닫혔으면 <see cref="ObjectDisposedException"/>.
    /// PENDING_ACK 를 받고도 연장한 기한까지 응답이 없으면 재전송하지 않고 바로 <see cref="GevTimeoutException"/> —
    /// 장치가 이미 명령을 받아 실행 중이라고 답한 것이라 다시 보내면 두 번 실행될 수 있다.
    /// </summary>
    public async Task<GvcpAck> RequestAsync(GvcpCmd cmd, CancellationToken ct = default)
    {
        if (cmd is null) throw new ArgumentNullException(nameof(cmd));
        if (!cmd.IsAckRequired)
            throw new ArgumentException($"{cmd.Name} has ack-required = 0; use SendNoAck", nameof(cmd));
        ThrowIfDisposed();

        await _reqLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var reqId = NextReqId(ref _reqIdCounter);
            var length = cmd.Length;
            cmd.WriteTo(_sendBuf, reqId);
            var attempts = 1 + _opt.Retries;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var pending = new PendingRequest(reqId, cmd.ExpectedAck);
                _pending = pending;
                try
                {
                    Send(_sendBuf, length);
                    var ack = await WaitForAckAsync(pending, ct).ConfigureAwait(false);
                    if (ack is not null)
                    {
                        if (ack.IsError)
                            throw StatusError(cmd, ack);
                        return ack;
                    }
                }
                finally
                {
                    _pending = null;
                }

                // PENDING_ACK 를 본 요청은 다시 보내지 않는다 — 장치는 명령을 받아 실행 중이라고 답했으므로 유실이 아니고,
                // 같은 명령을 또 보내면 두 번 실행될 수 있다. 재시도마다 연장 예산이 다시 붙어 줄을 붙드는 시간이
                // (1 + Retries) 배로 늘어나는 것도 여기서 끊는다 — 하트비트가 그 줄에 같이 서 있다.
                if (Interlocked.Read(ref pending.PendingDeadlineMs) > 0)
                    throw new GevTimeoutException(
                        $"{cmd.Name} to {DeviceEndPoint} was answered with PENDING_ACK but never completed within its {pending.BudgetMs} ms budget; "
                        + "the command is not resent because the device has already taken it");

                if (GevLog.IsEnabled(GevLogLevel.Debug))
                    GevLog.Debug(LogSrc, $"{cmd.Name} req_id {reqId} to {DeviceEndPoint}: no reply within {_opt.TimeoutMs} ms (attempt {attempt}/{attempts})");
            }

            throw new GevTimeoutException($"{cmd.Name} to {DeviceEndPoint} timed out after {attempts} attempt(s) of {_opt.TimeoutMs} ms");
        }
        finally
        {
            _reqLock.Release();
        }
    }

    private void Send(byte[] buffer, int length)
    {
        try
        {
            _socket.SendTo(buffer, 0, length, SocketFlags.None, _sendTarget);
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(GvcpChannel));
        }
        catch (SocketException ex)
        {
            throw new GevException($"GVCP send to {DeviceEndPoint} failed: {ex.SocketErrorCode}", ex);
        }
    }

    /// <summary>오류 status 를 예외로 바꾼다. WRITEREG/WRITEMEM 의 index 가 있으면 <see cref="FailedIndexKey"/> 로 실어 준다.</summary>
    private GevStatusException StatusError(GvcpCmd cmd, GvcpAck ack)
    {
        var hasIndex = ack.TryGetWriteIndex(out var index);
        var ex = new GevStatusException(cmd.Name, ack.Status, hasIndex ? (int)index : null);
        if (hasIndex)
        {
            ex.Data[FailedIndexKey] = (int)index;
            GevLog.Warn(LogSrc, $"{cmd.Name} req_id {ack.ReqId} to {DeviceEndPoint} rejected at entry {index} ({GvcpConst.StatusName(ack.Status)})");
        }
        return ex;
    }

    /// <summary>ACK 가 오면 그것, 기한(연장 포함)이 지나면 null. 취소는 OperationCanceledException.</summary>
    private async Task<GvcpAck?> WaitForAckAsync(PendingRequest pending, CancellationToken ct)
    {
        var startMs = GevClock.NowMs();
        var deadlineMs = startMs + _opt.TimeoutMs;
        // 이 요청이 쓸 예산은 시작할 때 한 번 읽어 고정한다 — 기다리는 도중에 상한이 바뀌어도 한 요청 안에서는 흔들리지 않는다.
        var maxWaitMs = (long)_opt.TimeoutMs + _opt.MaxPendingAckWaitMs;
        pending.BudgetMs = maxWaitMs;
        var capMs = startMs + maxWaitMs;

        while (true)
        {
            var remainingMs = deadlineMs - GevClock.NowMs();
            if (remainingMs > 0)
            {
                // 시계 값이 어긋나도 한 요청의 예산(타임아웃 + PENDING_ACK 상한)을 넘겨 기다리지 않는다.
                var delayMs = (int)Math.Min(Math.Min(remainingMs, maxWaitMs), int.MaxValue);
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(delayMs, delayCts.Token);
                var winner = await Task.WhenAny(pending.Tcs.Task, delay).ConfigureAwait(false);
                if (winner != delay) delayCts.Cancel();
            }

            if (pending.Tcs.Task.IsCompleted)
                return await pending.Tcs.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 기한은 지났다 — 그 사이 PENDING_ACK 가 기한을 미뤘는지 본다.
            var now = GevClock.NowMs();
            var extended = Interlocked.Read(ref pending.PendingDeadlineMs);
            if (extended > 0)
            {
                // 장치가 예고한 완료 시각 **뒤에 보통의 응답 창을 한 번 더** 준다. 예고 시각 정각에 끊으면
                // 타이머 오차만큼 늦은 ACK 가 타임아웃이 되어, 같은 req_id 로 재전송한 명령을 장치가 두 번 실행한다.
                var next = Math.Min(extended + _opt.TimeoutMs, capMs);
                if (next > deadlineMs)
                {
                    if (GevLog.IsEnabled(GevLogLevel.Debug))
                        GevLog.Debug(LogSrc, $"req_id {pending.ReqId}: PENDING_ACK extends the wait by {next - now} ms");
                    deadlineMs = next;
                    continue;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// PENDING_ACK 추가 대기의 상한을 다시 정한다. 장치가 실제로 받아들인 하트비트 타임아웃을 알고 난 뒤
    /// 세션이 요청 하나가 이 줄을 붙들 수 있는 시간을 그 타임아웃 안으로 맞춰 준다.
    /// 이미 기다리고 있는 요청은 시작할 때 잡은 예산으로 끝나고, 다음 요청부터 새 값이 적용된다.
    /// 생성자에서 사본을 떠 두므로 여기서 바꿔도 호출자가 넘긴 <see cref="GvcpChannelOpt"/> 객체는 그대로다.
    /// </summary>
    internal void SetMaxPendingAckWaitMs(int ms)
    {
        if (ms < 0) throw new ArgumentOutOfRangeException(nameof(ms), "must not be negative");
        if (_opt.MaxPendingAckWaitMs == ms) return;
        if (GevLog.IsEnabled(GevLogLevel.Debug))
            GevLog.Debug(LogSrc, $"PENDING_ACK wait cap {_opt.MaxPendingAckWaitMs} -> {ms} ms");
        _opt.MaxPendingAckWaitMs = ms;
    }

    /// <summary>1..65535 를 돌며 0 은 건너뛴다. 스레드 안전.</summary>
    internal static ushort NextReqId(ref int counter)
    {
        while (true)
        {
            var v = (ushort)(Interlocked.Increment(ref counter) & 0xFFFF);
            if (v != 0) return v;
        }
    }

    // ------------------------------------------------------------------ fire-and-forget

    /// <summary>ack-required = 0 인 완성 패킷을 그대로 보낸다. 스레드 안전, 어느 대상에서나 라이브러리 쪽 할당 없음.</summary>
    public void SendNoAck(ReadOnlySpan<byte> packet)
    {
        ThrowIfDisposed();
        if (packet.Length < GvcpConst.HeaderSize)
            throw new GevException($"GVCP packet too short to send: {packet.Length} bytes");
        try
        {
#if NET8_0_OR_GREATER
            _socket.SendTo(packet, SocketFlags.None, _deviceSockAddr);
#else
            SendNoAckBuffered(packet);
#endif
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(GvcpChannel));
        }
        catch (SocketException ex)
        {
            throw new GevException($"GVCP send to {DeviceEndPoint} failed: {ex.SocketErrorCode}", ex);
        }
    }

    /// <summary>
    /// Span·SocketAddress 송신 오버로드가 없는 대상의 송신 경로 — 고정 버퍼(또는 풀 버퍼)에 옮겨 담고
    /// 미리 직렬화한 주소로 보낸다. 소켓의 byte[] 오버로드는 넘긴 EndPoint 를 호출마다 직렬화하므로
    /// <see cref="DeviceEndPoint"/> 를 그대로 넘기면 재전송 한 장마다 주소 버퍼가 새로 생긴다.
    /// 어느 대상에서나 컴파일해 둔다 — 한 대상으로만 도는 시험에서도 이 경로를 덮을 수 있어야 한다.
    /// </summary>
    internal void SendNoAckBuffered(ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= NoAckScratchSize)
        {
            lock (_noAckLock)
            {
                packet.CopyTo(_noAckScratch);
                _socket.SendTo(_noAckScratch, 0, packet.Length, SocketFlags.None, _sendTarget);
            }
        }
        else
        {
            var rented = ArrayPool<byte>.Shared.Rent(packet.Length);
            try
            {
                packet.CopyTo(rented);
                _socket.SendTo(rented, 0, packet.Length, SocketFlags.None, _sendTarget);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>PACKETRESEND_CMD 를 스택 버퍼에 조립해 보낸다. GVSP 수신 스레드에서 불린다.</summary>
    public void SendPacketResend(ulong blockId, uint firstPacketId, uint lastPacketId, bool extendedIds, int streamChannel = 0)
    {
        Span<byte> buf = stackalloc byte[GvcpPacket.PacketResendMaxSize];
        var len = GvcpPacket.WritePacketResend(buf, NextReqId(ref _reqIdCounter), blockId, firstPacketId, lastPacketId, extendedIds, streamChannel);
        SendNoAck(buf.Slice(0, len));
    }

    // ------------------------------------------------------------------ receive loop

    private void ReceiveLoop()
    {
        var buf = new byte[RxBufferSize];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        var consecutiveFailures = 0;
        while (!_isDisposed)
        {
            int n;
            try
            {
                n = _socket.ReceiveFrom(buf, ref from);
                consecutiveFailures = 0;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // ICMP port unreachable — 요청 쪽은 타임아웃으로 알아서 끝난다.
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                Interlocked.Increment(ref _malformedPacketCount);
                GevLog.Warn(LogSrc, $"dropped a datagram larger than {RxBufferSize} bytes from {from}");
                continue;
            }
            catch (SocketException ex)
            {
                if (_isDisposed) break;
                consecutiveFailures++;
                if (consecutiveFailures == 1)
                    GevLog.Warn(LogSrc, $"receive failed: {ex.SocketErrorCode}", ex);
                else if (GevLog.IsEnabled(GevLogLevel.Trace))
                    GevLog.Trace(LogSrc, $"receive failed again ({consecutiveFailures} in a row): {ex.SocketErrorCode}");
                if (consecutiveFailures >= RxMaxConsecutiveFailures)
                {
                    // 스스로 회복하지 않는 소켓 — 경고를 무한히 찍는 대신 채널을 닫아 요청 쪽이 즉시 실패하게 한다.
                    GevLog.Error(LogSrc, $"receive failed {consecutiveFailures} times in a row ({ex.SocketErrorCode}); closing channel {LocalEndPoint} -> {DeviceEndPoint}", ex);
                    _pending?.Tcs.TrySetException(new GevException($"GVCP receive on {LocalEndPoint} kept failing ({ex.SocketErrorCode}); channel closed", ex));
                    Dispose();
                    break;
                }
                Thread.Sleep(RxBackoffMs(consecutiveFailures));
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                HandlePacket(buf, n, from);
            }
            catch (Exception ex)
            {
                // 수신 스레드는 어떤 패킷에도 죽지 않는다.
                GevLog.Error(LogSrc, "unexpected failure while handling a GVCP packet", ex);
            }
        }
    }

    /// <summary>연속 실패 횟수에 따른 재시도 간격 — 10, 20, 50, 그 뒤로는 100 ms.</summary>
    private static int RxBackoffMs(int consecutiveFailures) => consecutiveFailures switch
    {
        1 => 10,
        2 => 20,
        3 => 50,
        _ => 100,
    };

    private void HandlePacket(byte[] buf, int n, EndPoint from)
    {
        // 장치는 명령을 받은 그 소켓(주소+포트)에서 응답한다 — 다른 곳에서 온 것은 이 채널의 응답이 아니다.
        if (from is not IPEndPoint fromIp || !fromIp.Equals(DeviceEndPoint))
        {
            Interlocked.Increment(ref _foreignPacketCount);
            if (GevLog.IsEnabled(GevLogLevel.Trace))
                GevLog.Trace(LogSrc, $"ignored {n} bytes from {from} (device is {DeviceEndPoint})");
            return;
        }

        var packet = buf.AsSpan(0, n);
        if (!GvcpAckHeader.TryParse(packet, out var header))
        {
            Interlocked.Increment(ref _malformedPacketCount);
            GevLog.Warn(LogSrc, $"malformed GVCP reply from {from}: {n} bytes");
            return;
        }

        var pending = _pending;
        if (pending is null || header.ReqId != pending.ReqId)
        {
            Interlocked.Increment(ref _staleAckCount);
            if (GevLog.IsEnabled(GevLogLevel.Debug))
                GevLog.Debug(LogSrc, $"stale {GvcpPacket.CommandName(header.Command)} ack req_id {header.ReqId} dropped (waiting for {(pending is null ? "nothing" : pending.ReqId.ToString())})");
            return;
        }

        if (header.Command == GvcpConst.PendingAck)
        {
            var ttcMs = header.Length >= GvcpPacket.PendingAckPayloadSize
                ? BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(GvcpConst.HeaderSize + 2))
                : 0;
            Interlocked.Increment(ref _pendingAckCount);
            Interlocked.Exchange(ref pending.PendingDeadlineMs, GevClock.NowMs() + ttcMs);
            if (GevLog.IsEnabled(GevLogLevel.Debug))
                GevLog.Debug(LogSrc, $"req_id {header.ReqId}: PENDING_ACK, device asks for {ttcMs} ms");
            return;
        }

        if (header.Command != pending.ExpectedAck && !header.IsError)
        {
            // 오류 응답은 req_id 만 맞으면 받아들인다(장치마다 ack command 를 채우는 방식이 다르다). 정상 응답은 종류까지 맞아야 한다.
            Interlocked.Increment(ref _staleAckCount);
            GevLog.Warn(LogSrc, $"req_id {header.ReqId}: expected {GvcpPacket.CommandName(pending.ExpectedAck)} ack but got 0x{header.Command:X4}; dropped");
            return;
        }

        var ack = new GvcpAck(header.Status, header.Command, header.ReqId, packet.Slice(GvcpConst.HeaderSize, header.Length).ToArray());
        pending.Tcs.TrySetResult(ack);
    }

    // ------------------------------------------------------------------ lifecycle

    private void ThrowIfDisposed()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GvcpChannel));
    }

    /// <summary>소켓을 닫고 수신 스레드가 끝나기를 기다린다. 대기 중인 요청은 <see cref="ObjectDisposedException"/> 으로 끝난다.</summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        try
        {
            _socket.Dispose();
        }
        catch (Exception ex)
        {
            GevLog.Debug(LogSrc, $"socket close: {ex.Message}");
        }

        _pending?.Tcs.TrySetException(new ObjectDisposedException(nameof(GvcpChannel)));

        if (Thread.CurrentThread != _rxThread && _rxThread.IsAlive && !_rxThread.Join(RxThreadJoinMs))
            GevLog.Warn(LogSrc, "receive thread did not stop within the join timeout");

        GevLog.Debug(LogSrc, $"channel {LocalEndPoint} -> {DeviceEndPoint} closed");
    }

    /// <summary>
    /// 주소를 한 번만 직렬화해 두고 <see cref="Serialize"/> 에서 그 사본을 그대로 돌려주는 종단점.
    /// 소켓은 송신할 때 이 버퍼를 읽기만 하므로 사본 하나를 계속 재사용할 수 있다.
    /// </summary>
    private sealed class PreSerializedEndPoint : EndPoint
    {
        private readonly IPEndPoint _endPoint;
        private readonly SocketAddress _address;

        public PreSerializedEndPoint(IPEndPoint endPoint)
        {
            _endPoint = endPoint;
            _address = endPoint.Serialize();
        }

        public override AddressFamily AddressFamily => _endPoint.AddressFamily;
        public override SocketAddress Serialize() => _address;
        public override EndPoint Create(SocketAddress socketAddress) => _endPoint.Create(socketAddress);
        public override string ToString() => _endPoint.ToString();
    }

    /// <summary>진행 중인 요청 하나. 수신 스레드는 req_id 가 맞는 응답을 여기에 넣는다.</summary>
    private sealed class PendingRequest
    {
        public readonly ushort ReqId;
        public readonly ushort ExpectedAck;
        public readonly TaskCompletionSource<GvcpAck> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>PENDING_ACK 가 정한 절대 기한(ms, <see cref="GevClock.NowMs"/> 기준). 0 = 없음.</summary>
        public long PendingDeadlineMs;
        /// <summary>이 시도가 시작할 때 잡은 대기 예산(응답 창 + PENDING_ACK 상한, ms) — 진단 문구에 쓴다.</summary>
        public long BudgetMs;

        public PendingRequest(ushort reqId, ushort expectedAck)
        {
            ReqId = reqId;
            ExpectedAck = expectedAck;
        }
    }
}
