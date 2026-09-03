using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;
using GevSharp.Gvsp;

namespace GevSharp;

/// <summary>
/// GVSP 스트림 수신기. 소켓 하나·수신 스레드 하나로 패킷을 받아 풀 버퍼에 조립하고, 완성된 프레임을 유한 큐로 넘긴다.
/// 시작 순서: 소켓 바인드 → SCDA/SCP → SCPS 플래그 읽기 → 패킷 크기 협상 → SCPS/SCPD → 스레드. AcquisitionStart 는 보내지 않는다(GenApi 쪽 몫).
/// SCPS 는 크기 외에 장치가 켜 둔 플래그(단편화 금지·빅엔디언)를 지키고, Auto 협상은 단편화 금지로 검증했으므로 스트리밍도 같은 조건으로 쓴다.
/// 정지 순서: SCP = 0, SCDA = 0 → 소켓 닫기(수신 블로킹 해제) → 스레드 합류 → 큐를 <see cref="GevStreamClosedException"/> 으로 닫기.
/// </summary>
public sealed partial class GevStream : IAsyncDisposable
{
    internal const int MinPacketSize = 576;
    internal const int MaxPacketSize = 16000;

    private const string LogSrc = "GevStream";
    private const int StateNew = 0;
    private const int StateStarting = 1;
    private const int StateStarted = 2;
    private const int StateStopping = 3;
    private const int StateStopped = 4;
    /// <summary>정지가 수신 스레드를 기다리는 상한. 제어 채널의 같은 상한과 맞춘다.</summary>
    private const int ReceiverJoinMs = 2000;

    private readonly IGevPort _regs;
    private readonly IGvcpResendPort _resend;
    private readonly IPAddress _localAddress;
    /// <summary>장치 주소 — 방화벽 통과용 한 바이트를 보낼 목적지. null 이면 그 단계를 건너뛴다.</summary>
    private readonly IPAddress? _deviceAddress;
    private readonly GevStreamOpt _opt;
    private readonly int _channel;
    private readonly GevFramePool _pool;
    private readonly GevStreamStats _stats = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private AsyncBoundedQueue<GevFrame>? _queue;
    private Socket? _socket;
    private Thread? _thread;
    private volatile bool _isStopRequested;
    private int _state;
    /// <summary>시작 시 장치 SCPS 에서 읽은 플래그 비트(단편화 금지·빅엔디언) — 크기를 쓸 때마다 함께 써서 장치 설정을 지우지 않는다.</summary>
    private uint _scpsFlags;

    /// <param name="regs">스트림 채널 레지스터(SCP/SCPS/SCPD/SCDA)를 쓰는 경로.</param>
    /// <param name="resend">PACKETRESEND 출구 — 제어 소켓에서 보내야 장치가 받아 준다.</param>
    /// <param name="localAddress">스트림 소켓을 바인드할 호스트 IPv4 — SCDA 로도 쓰인다.</param>
    /// <param name="opt">수신 옵션. null 이면 기본값. 값 범위가 어긋나면 <see cref="ArgumentOutOfRangeException"/>.</param>
    /// <param name="streamChannel">스트림 채널 번호(0 부터).</param>
    /// <param name="deviceAddress">장치 IPv4 — 방화벽 통과용 한 바이트를 보낼 목적지. null 이면 그 단계를 건너뛴다.</param>
    internal GevStream(IGevPort regs, IGvcpResendPort resend, IPAddress localAddress, GevStreamOpt? opt, int streamChannel = 0, IPAddress? deviceAddress = null)
    {
        _regs = regs ?? throw new ArgumentNullException(nameof(regs));
        _resend = resend ?? throw new ArgumentNullException(nameof(resend));
        _localAddress = localAddress ?? throw new ArgumentNullException(nameof(localAddress));
        _deviceAddress = deviceAddress;
        if (localAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Local address must be an IPv4 address.", nameof(localAddress));
        }
        if (streamChannel < 0 || streamChannel > 511) throw new ArgumentOutOfRangeException(nameof(streamChannel));

        _opt = opt ?? new GevStreamOpt();
        _opt.Validate();
        _channel = streamChannel;
        _pool = new GevFramePool(_opt.BufferCount, _opt.PayloadSize ?? 0);
        PacketSize = _opt.PacketSize;
    }

    /// <summary>스트림 소켓의 로컬 포트. <see cref="StartAsync"/> 뒤에 유효.</summary>
    public int LocalPort { get; private set; }

    /// <summary>실제로 쓰는 SCPS 값(IP+UDP 헤더 포함). Auto 모드는 협상 결과.</summary>
    public int PacketSize { get; private set; }

    public GevStreamStats Stats => _stats;

    public bool IsStarted => Volatile.Read(ref _state) == StateStarted;

    /// <summary>프레임을 전달하지 못했을 때 수신 스레드에서 호출된다 — 가볍게 처리해야 한다.</summary>
    public event Action<GevFrameDiag>? FrameDropped;

    /// <summary>테스트용: 인터페이스 MTU 조회를 바꿔 끼운다. null 이면 실제 인터페이스를 본다.</summary>
    internal Func<IPAddress, int>? MtuResolver { get; set; }

    /// <summary>
    /// 소켓을 열고 장치 스트림 채널을 이 소켓으로 향하게 한 뒤 수신 스레드를 띄운다. 두 번 부르면 <see cref="InvalidOperationException"/>.
    /// 레지스터 쓰기 실패는 그대로 던지며, 그 경우 소켓은 닫히고 스트림은 정지 상태가 된다.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state != StateNew)
            {
                throw new InvalidOperationException(_state == StateStarted || _state == StateStarting
                    ? "Stream is already started."
                    : "Stream cannot be restarted after it was stopped.");
            }
            _state = StateStarting;

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var hasWrittenScp = false;
            try
            {
                socket.Bind(new IPEndPoint(_localAddress, _opt.LocalPort ?? 0));
                socket.ReceiveBufferSize = _opt.SocketBufferBytes;
                var granted = socket.ReceiveBufferSize;
                SocketReceiveBufferBytes = granted;
                LocalPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
                GevLog.Info(LogSrc, $"Stream socket bound to {_localAddress}:{LocalPort}; receive buffer requested {_opt.SocketBufferBytes} bytes, granted {granted} bytes.");
                if (granted < _opt.SocketBufferBytes)
                {
                    GevLog.Warn(LogSrc, $"OS granted a smaller receive buffer ({granted} bytes) than requested ({_opt.SocketBufferBytes} bytes); packet loss under load is more likely.");
                }
                _socket = socket;

                // 장치가 테스트 패킷을 보낼 목적지를 먼저 알려 준다.
                await WriteRegAsync(GvbsAddr.ScdaOffset, ToUInt32(_localAddress), ct).ConfigureAwait(false);
                await WriteRegAsync(GvbsAddr.ScpOffset, (uint)LocalPort, ct).ConfigureAwait(false);
                hasWrittenScp = true;

                await PunchFirewallAsync(socket, ct).ConfigureAwait(false);

                // 장치가 SCPS 에 켜 둔 플래그를 읽어 둔다 — 크기만 쓰면 단편화 금지·빅엔디언 설정이 지워진다.
                var currentScps = await ReadRegAsync(GvbsAddr.ScpsOffset, ct).ConfigureAwait(false);
                _scpsFlags = currentScps & (GvbsAddr.ScpsDoNotFragment | GvbsAddr.ScpsBigEndian);

                var isAuto = _opt.PacketSizeMode == PacketSizeMode.Auto;
                var size = isAuto
                    ? await NegotiatePacketSizeAsync(socket, ct).ConfigureAwait(false)
                    : _opt.PacketSize;
                // Auto 는 단편화 금지 조건으로 통과한 크기다 — 스트리밍도 같은 조건이어야 경로가 바뀌어도 단편화가 조용히 돌아오지 않는다.
                var scps = (uint)size | _scpsFlags | (isAuto ? GvbsAddr.ScpsDoNotFragment : 0u);
                await WriteRegAsync(GvbsAddr.ScpsOffset, scps, ct).ConfigureAwait(false);
                PacketSize = size;
                _opt.PacketSize = size;

                if (_opt.InterPacketDelay > 0)
                {
                    await WriteRegAsync(GvbsAddr.ScpdOffset, (uint)_opt.InterPacketDelay, ct).ConfigureAwait(false);
                }

                InitReceiver(size);
                _queue = new AsyncBoundedQueue<GevFrame>(_opt.BufferCount);

                var thread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "gevsharp-gvsp-" + LocalPort,
                    Priority = _opt.ReceiverPriority,
                };
                _thread = thread;
                thread.Start();
                _state = StateStarted;
                GevLog.Info(LogSrc, $"Stream started on port {LocalPort}, packet size {size}, {_opt.BufferCount} buffers, resend {(_opt.ResendEnabled ? "on" : "off")}.");
            }
            catch
            {
                _socket = null;
                _isStopRequested = true;
                socket.Close();
                if (hasWrittenScp)
                {
                    // 장치가 닫힌 포트로 쏘지 않게 최선을 다해 되돌린다 — 여기서의 실패는 원래 예외를 가리지 않는다.
                    try { await WriteRegAsync(GvbsAddr.ScpOffset, 0, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { GevLog.Warn(LogSrc, "Failed to reset SCP after a failed start.", ex); }
                }
                _queue?.Complete(new GevStreamClosedException("Stream failed to start."));
                _state = StateStopped;
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// 장치 전송을 끄고(SCP = 0, SCDA = 0) 소켓을 닫아 수신 스레드를 깨운 뒤 합류한다. 조립 중이던 프레임은 버려지고,
    /// 큐에 남은 프레임은 반납되며, 대기 중인 <see cref="ReceiveAsync"/> 는 <see cref="GevStreamClosedException"/> 으로 끝난다.
    /// 여러 번 불러도 된다. 레지스터 쓰기 실패는 로그만 남기고 로컬 정리는 끝까지 진행한다.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        var thread = _thread;
        if (thread is not null && ReferenceEquals(Thread.CurrentThread, thread))
        {
            throw new InvalidOperationException("StopAsync must not be called from the receiver thread (for example inside a FrameDropped handler).");
        }

        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == StateStopped) return;
            if (_state == StateNew)
            {
                _state = StateStopped;
                return;
            }
            _state = StateStopping;
            _isStopRequested = true;

            try { await WriteRegAsync(GvbsAddr.ScpOffset, 0, ct).ConfigureAwait(false); }
            catch (Exception ex) { GevLog.Warn(LogSrc, "Failed to write SCP = 0 while stopping the stream.", ex); }
            try { await WriteRegAsync(GvbsAddr.ScdaOffset, 0, ct).ConfigureAwait(false); }
            catch (Exception ex) { GevLog.Warn(LogSrc, "Failed to write SCDA = 0 while stopping the stream.", ex); }

            var socket = _socket;
            _socket = null;
            socket?.Close();

            thread = _thread;
            _thread = null;
            if (thread is not null && thread.IsAlive)
            {
                // 상한 없이 기다리지 않는다. 소켓을 닫으면 블로킹 수신이 깨어나는 것이 보통이지만 그것을 보장하는 규격은 없고,
                // 여기서 무한히 기다리면 정지가 영영 돌아오지 않는다. 게다가 이 대기는 스레드풀 스레드를 하나 붙들고 있어서,
                // 코어가 적은 기계에서 정지가 몇 개 겹치면 풀이 고갈된다. 제어 채널은 이미 같은 상한을 두고 있다.
                // 시한을 넘겨도 할 일은 그대로 한다 — 소켓은 이미 닫혔고 아래에서 큐를 비워 버퍼를 돌려준다.
                var joined = await Task.Run(() => thread.Join(ReceiverJoinMs)).ConfigureAwait(false);
                if (!joined)
                {
                    GevLog.Warn(LogSrc, $"Receiver thread did not stop within {ReceiverJoinMs} ms; the socket is closed and the buffers are returned regardless.");
                }
            }

            // 큐는 **항상** 비운다. 수신 스레드가 먼저(소켓이 죽어) 큐를 닫아 두었을 수도 있는데, 그때 건너뛰면
            // 큐에 남은 완성 프레임이 Dispose 되지 않아 풀 버퍼가 영영 돌아오지 않는다 — 이 메서드가 약속한 것과 정반대다.
            var queue = _queue;
            if (queue is not null)
            {
                // TryDrain 은 완료된 큐에서도 던지지 않는다 — TryDequeue 를 쓰면 첫 항목에서 완료 예외가 나
                // 나머지 프레임의 버퍼가 반납되지 않는다.
                while (queue.TryDrain(out var pending)) pending.Dispose();
                if (!queue.IsCompleted) queue.Complete(new GevStreamClosedException("Stream has been stopped."));
            }

            _state = StateStopped;
            GevLog.Info(LogSrc, $"Stream on port {LocalPort} stopped: {_stats.FramesCompleted} completed, {_stats.FramesIncomplete} incomplete, {_stats.FramesDroppedNoBuffer} dropped (no buffer), {_stats.ResendRecovered} packets recovered by resend.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>다음 프레임을 기다린다. 시작 전이거나 정지된 스트림이면 <see cref="GevStreamClosedException"/>. 받은 프레임은 반드시 Dispose 한다.</summary>
    public ValueTask<GevFrame> ReceiveAsync(CancellationToken ct = default)
    {
        var queue = _queue ?? throw new GevStreamClosedException("Stream is not started.");
        var pending = queue.DequeueAsync(ct);
        if (pending.IsCompletedSuccessfully)
        {
            _stats.IncFramesDelivered();
            return pending;
        }
        return AwaitAndCountAsync(pending);
    }

    private async ValueTask<GevFrame> AwaitAndCountAsync(ValueTask<GevFrame> pending)
    {
        var frame = await pending.ConfigureAwait(false);
        _stats.IncFramesDelivered();
        return frame;
    }

    /// <summary>기다리지 않고 큐에서 프레임을 꺼낸다. 시작 전이거나 정지된 스트림이면 <see cref="GevStreamClosedException"/>.</summary>
    public bool TryReceive(out GevFrame? frame)
    {
        var queue = _queue ?? throw new GevStreamClosedException("Stream is not started.");
        if (queue.TryDequeue(out var item))
        {
            _stats.IncFramesDelivered();
            frame = item;
            return true;
        }
        frame = null;
        return false;
    }

    /// <summary><see cref="StopAsync"/> 뒤 빌려 주지 않은 버퍼를 놓는다. 소비자가 아직 든 프레임은 그대로 유효하다.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _pool.ReleaseFree();
    }

    /// <summary>
    /// 상태 기반 호스트 방화벽에 이 소켓과 장치 스트림 송신 포트 사이의 매핑을 만든다.
    /// 그런 방화벽은 우리가 먼저 보낸 적 없는 UDP 를 버리므로, 이 한 바이트가 없으면 GVSP 가 한 패킷도 도착하지 않는다
    /// (관리자 권한으로 인바운드 규칙을 등록하는 것이 유일한 대안이 된다).
    /// SCSP 는 채널을 연 뒤(SCP 를 쓴 뒤)에야 값이 서지만, 끝까지 0 으로 두는 장치도 있다. 그때는 호스트 포트 번호로 뚫는다 —
    /// 실측한 그런 장치는 우리가 준 포트 번호를 그대로 자기 송신 포트로 썼다.
    /// </summary>
    private async Task PunchFirewallAsync(Socket socket, CancellationToken ct)
    {
        if (!_opt.FirewallTraversal || _deviceAddress is null) return;

        // 통과의 목적은 "이 로컬 포트로 되돌아오는 길을 열어 두는 것" 이다. 상태 기반 방화벽은 우리가 먼저 내보낸
        // 흐름만 되돌려 보내므로, 무엇보다 먼저 이 소켓에서 장치 쪽으로 한 바이트가 나가야 한다.
        // 장치가 자기 스트림 송신 포트(SCSP)를 알려 주면 그리로 보내는 것이 가장 정확하지만, 알려 주지 않는 장치도 있다
        // (실측: 한 벤더는 SCSP 를 0 으로 둔다). 그때 통과를 통째로 건너뛰면 그 장치는 방화벽 뒤에서 한 패킷도 못 받는다 —
        // 파이어테스트조차 576 바이트에서 응답이 없었다. 알려 주지 않으면 제어 포트(GVCP 3956)로라도 보낸다.
        int port;
        try
        {
            var scsp = await ReadRegAsync(GvbsAddr.ScspOffset, ct).ConfigureAwait(false);
            port = (int)(scsp & 0xFFFF);
        }
        catch (GevStatusException ex)
        {
            GevLog.Debug(LogSrc, $"Device refused the SCSP register ({GvcpConst.StatusName(ex.Status)}); punching the control port instead.");
            port = 0;
        }

        if (port == 0)
        {
            // SCSP 를 끝까지 0 으로 두는 장치는 실측에서 호스트 포트 번호를 그대로 자기 송신 포트로 썼다
            // (SCP 에 54629 를 쓰자 장치가 54629 에서 보냈다). 포트까지 따지는 방화벽은 우리가 그 포트로
            // 먼저 보낸 적이 있어야만 되돌려 보내므로, 모를 때는 제어 포트가 아니라 이 번호로 뚫는다 —
            // 제어 포트로 뚫으면 매핑이 어긋나 한 패킷도 오지 않는다(그 상태로 파이어테스트도 전부 실패했다).
            port = LocalPort;
            GevLog.Debug(LogSrc, $"Device reports no stream source port (SCSP = 0); punching {port} instead, since such a device mirrors the host port it was given.");
        }

        // 유지용 재송신이 레지스터를 다시 읽지 않도록 목적지를 기억해 둔다 — 수신 스레드에서 불리는 경로다.
        _punchTarget = new IPEndPoint(_deviceAddress, port);
        if (SendPunch(socket))
        {
            GevLog.Debug(LogSrc, $"Firewall traversal: sent one byte from {_localAddress}:{LocalPort} to {_punchTarget}.");
        }
    }

    /// <summary>
    /// 방화벽 매핑을 살리는 한 바이트를 보낸다. 성공하면 true. 수신 스레드에서도 불리므로 예외를 밖으로 내지 않는다.
    /// </summary>
    private bool SendPunch(Socket socket)
    {
        var target = _punchTarget;
        if (target is null) return false;
        try
        {
            socket.SendTo(_punchPayload, target);
            return true;
        }
        catch (SocketException ex)
        {
            if (!_hasLoggedPunchFailure)
            {
                _hasLoggedPunchFailure = true;
                GevLog.Warn(LogSrc, $"Firewall traversal to {target} failed ({ex.SocketErrorCode}); a host firewall may drop the incoming stream.");
            }
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>방화벽 통과용 한 바이트 — 재사용해 수신 스레드에서 할당하지 않는다.</summary>
    private readonly byte[] _punchPayload = new byte[1];
    private IPEndPoint? _punchTarget;
    private bool _hasLoggedPunchFailure;

    // ---- 진단용 내부 창구 (테스트가 풀 상태와 소켓 사망 경로를 확인한다) ----

    /// <summary>
    /// OS 가 실제로 내준 수신 버퍼 크기(바이트) — <see cref="StartAsync"/> 뒤에 유효하며 요청값(<see cref="GevStreamOpt.SocketBufferBytes"/>)보다 작을 수 있다.
    /// 버퍼가 요청대로 잡혔는지는 부하 상황의 유실률을 가르므로 로그와 함께 수치로 남긴다.
    /// </summary>
    internal int SocketReceiveBufferBytes { get; private set; }

    /// <summary>풀 버퍼 한 장의 크기(바이트). 리더가 알린 크기에 따라 자란다.</summary>
    internal int PoolBufferBytes => _pool.BufferBytes;

    /// <summary>지금 빌려 가지 않은 풀 버퍼 수 — 정지 뒤에는 BufferCount 와 같아야 한다.</summary>
    internal int PoolFreeBuffers => _pool.FreeCount;

    /// <summary>
    /// 수신 스레드가 소켓 사망으로 큐를 닫은 상태를 만든다. 그 경로는 밖에서 유도할 수 없는데,
    /// 그때 <see cref="StopAsync"/> 가 큐에 남은 프레임을 반납하는지는 반드시 지켜야 하는 계약이라 테스트가 필요하다.
    /// </summary>
    internal void SimulateReceiverQueueCompletion(Exception cause) => _queue?.Complete(cause);

    /// <summary>조립이 끝나 큐에 든 프레임을 동기로 꺼낸다 — 할당 계측이 비동기 대기의 할당에 섞이지 않게.</summary>
    internal bool TryDrainForTest(out GevFrame frame)
    {
        frame = null!;
        return _queue is not null && _queue.TryDrain(out frame);
    }

    /// <summary>
    /// 수신기를 스레드도 소켓도 없이 조립만 해 둔다 — <see cref="FeedPacketForTest"/> 로 패킷을 직접 먹이기 위한 것.
    /// 시작한 스트림에는 쓰지 않는다(수신 스레드와 같은 상태를 건드리게 된다).
    /// </summary>
    internal void InitReceiverForTest(int packetSize)
    {
        PacketSize = packetSize;
        _opt.PacketSize = packetSize;
        InitReceiver(packetSize);
        _queue = new AsyncBoundedQueue<GevFrame>(_opt.BufferCount);
    }

    /// <summary>
    /// 수신 스레드가 데이터그램 하나에 하는 일을 부른 스레드에서 그대로 밟는다 — 소켓 호출만 빠진다.
    /// <para>
    /// 핫패스가 패킷마다 무엇을 할당하는지는 그 일을 하는 스레드 안에서만 잴 수 있는데
    /// (<c>GC.GetAllocatedBytesForCurrentThread</c> 는 스레드별이다), 수신 스레드에 계측을 심으면 그 계측이 곧 핫패스가 된다.
    /// 그래서 같은 코드를 시험 스레드로 불러 잰다. <see cref="InitReceiverForTest"/> 로 조립한 스트림에만 쓴다.
    /// </para>
    /// </summary>
    internal void FeedPacketForTest(byte[] packet, int length)
    {
        var scratch = _scratch ?? throw new InvalidOperationException("The receiver has not been initialised.");
        Buffer.BlockCopy(packet, 0, scratch, 0, length);
        OnPacket(length, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private ValueTask WriteRegAsync(uint offset, uint value, CancellationToken ct)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return _regs.WriteAsync(GvbsAddr.StreamChannel(_channel, offset), bytes, ct);
    }

    private async ValueTask<uint> ReadRegAsync(uint offset, CancellationToken ct)
    {
        var bytes = new byte[4];
        await _regs.ReadAsync(GvbsAddr.StreamChannel(_channel, offset), bytes, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
