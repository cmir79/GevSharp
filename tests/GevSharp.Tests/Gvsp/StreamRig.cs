using System.Net;

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 스트림 통합 테스트 한 벌: 메모리 레지스터 포트 + 루프백 송신기 + 리센드 대역 + <see cref="GevStream"/>.
/// 리센드 대역은 송신기에 되묻는 구조라 "유실 → 요청 → 재전송 → 완성" 이 한 프로세스 안에서 끝난다.
/// </summary>
internal sealed class StreamRig : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Queue<GevFrameDiag> _dropped = new();
    private readonly SemaphoreSlim _droppedSignal = new(0);

    public StreamRig(GevStreamOpt? opt = null, int streamChannel = 0)
    {
        Opt = opt ?? DefaultOpt();
        Resend = new TestResendPort(Sender);
        Stream = new GevStream(Regs, Resend, IPAddress.Loopback, Opt, streamChannel);
        Stream.FrameDropped += diag =>
        {
            lock (_lock) _dropped.Enqueue(diag);
            _droppedSignal.Release();
        };
    }

    public FakeRegPort Regs { get; } = new();
    public GvspTestSender Sender { get; } = new();
    public TestResendPort Resend { get; }
    public GevStream Stream { get; }
    public GevStreamOpt Opt { get; }

    public int DroppedCount
    {
        get { lock (_lock) return _dropped.Count; }
    }

    /// <summary>짧은 타이밍으로 맞춘 기본 옵션 — 유예 2 ms, 재요청 20 ms, 보존 100 ms.</summary>
    public static GevStreamOpt DefaultOpt() => new()
    {
        PacketSizeMode = PacketSizeMode.Fixed,
        PacketSize = 1500,
        BufferCount = 4,
        SocketBufferBytes = 4 * 1024 * 1024,
        InitialPacketTimeoutMs = 2,
        PacketTimeoutMs = 20,
        FrameRetentionMs = 100,
        ReceiverPriority = ThreadPriority.Normal,
    };

    public async Task StartAsync()
    {
        await Stream.StartAsync();
        Sender.Target = new IPEndPoint(IPAddress.Loopback, Stream.LocalPort);
        Sender.PacketSize = Stream.PacketSize;
    }

    /// <summary>
    /// 다음 프레임을 받는다. 시한을 넘기면 "안 왔다" 로 끝나지 않고 수신기가 그동안 무엇을 보았는지 함께 실어 던진다 —
    /// 프레임이 오지 않는 원인은 버려짐·버퍼 고갈·소켓 어느 쪽이든 될 수 있고, 그 구분이 없으면 다시 재현할 때까지 알 수가 없다.
    /// </summary>
    public async Task<GevFrame> ReceiveAsync(int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await Stream.ReceiveAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"No frame within {timeoutMs} ms. {Describe()}");
        }
    }

    /// <summary>실패 메시지에 붙이는 수신기 상태 한 줄.</summary>
    public string Describe()
    {
        var s = Stream.Stats.Snapshot();
        string dropped;
        lock (_lock) dropped = _dropped.Count == 0 ? "none" : string.Join(", ", _dropped.Select(d => $"block {d.FrameId} {d.Reason}/0x{d.Code:X4}"));
        return $"stats: completed {s.FramesCompleted}, incomplete {s.FramesIncomplete}, dropped(error) {s.FramesDroppedError}, "
            + $"dropped(no buffer) {s.FramesDroppedNoBuffer}, packets {s.PacketsReceived} received / {s.PacketsIgnored} ignored, "
            + $"resend requests {s.ResendRequests}; pool {Stream.PoolFreeBuffers}/{Opt.BufferCount} free of {Stream.PoolBufferBytes} bytes; "
            + $"drop events: {dropped}";
    }

    public async Task<GevFrameDiag> WaitDroppedAsync(int timeoutMs = 10_000)
    {
        if (!await _droppedSignal.WaitAsync(timeoutMs))
        {
            throw new TimeoutException("No FrameDropped event within the timeout.");
        }
        lock (_lock) return _dropped.Dequeue();
    }

    public async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met within the timeout.");
            await Task.Delay(5);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        Sender.Dispose();
        _droppedSignal.Dispose();
    }
}
