using System.Net;
using GevSharp.Gvcp;
using GevSharp.Sim;

namespace GevSharp.Tests.Integration;

/// <summary>
/// 종단 간 통합 테스트 한 벌: 루프백 임시 포트의 <see cref="SimDevice"/> 하나와, 그 장치를 내부 오버로드(포트 지정)로 연
/// <see cref="GevDevice"/> 하나. 테스트마다 새로 만들고 finally 에서 버린다 — 장치 → 시뮬레이터 순으로 닫아
/// CCP 해제(WRITEREG CCP = 0)가 살아 있는 시뮬레이터에 닿게 한다.
/// 스트림·획득 제어는 GenApi 노드맵을 거치지 않고 <see cref="SimFeatureAddr"/> 의 피처 레지스터를 직접 쓴다.
/// </summary>
internal sealed class SimRig : IAsyncDisposable
{
    private SimRig(SimDevice sim, GevDevice device, GevDeviceOpt deviceOpt)
    {
        Sim = sim;
        Device = device;
        DeviceOpt = deviceOpt;
    }

    public SimDevice Sim { get; }
    public GevDevice Device { get; }
    public GevDeviceOpt DeviceOpt { get; }

    /// <summary>시뮬레이터가 듣는 GVCP 엔드포인트(127.0.0.1:임시 포트).</summary>
    public IPEndPoint EndPoint => Sim.GvcpEndPoint;

    /// <summary>시뮬레이터를 띄우고 장치를 연다. 열기에 실패하면 시뮬레이터도 정리하고 예외를 그대로 던진다.</summary>
    public static async Task<SimRig> StartAsync(Action<SimDeviceOpt>? sim = null, Action<GevDeviceOpt>? device = null)
    {
        var simOpt = DefaultSimOpt();
        sim?.Invoke(simOpt);
        var simDevice = StartSim(simOpt);
        try
        {
            var deviceOpt = DefaultDeviceOpt();
            device?.Invoke(deviceOpt);
            var gevDevice = await GevDevice.OpenAsync(simDevice.GvcpEndPoint, deviceOpt);
            return new SimRig(simDevice, gevDevice, deviceOpt);
        }
        catch
        {
            simDevice.Dispose();
            throw;
        }
    }

    /// <summary>시뮬레이터만 띄운다(장치는 테스트가 직접 연다).</summary>
    public static SimDevice StartSim(SimDeviceOpt? opt = null)
    {
        var dev = new SimDevice(opt ?? DefaultSimOpt());
        dev.Start();
        return dev;
    }

    /// <summary>
    /// 작은 프레임(128×64 Mono8 = 8192 바이트 → 1500 SCPS 에서 페이로드 6 개), 자유 실행 30 Hz.
    /// 자유 실행은 벽시계가 페이스를 정하므로 과부하 러너에서는 소비자가 밀려 NoBuffer 드롭·리센드 이력 소진이 난다 — 프레임 순서·드롭 0 처럼
    /// 정확한 결과를 단정하는 테스트는 <see cref="StartTriggeredAcquisitionAsync"/> + <see cref="TriggerAsync"/> 로 테스트가 직접 페이스를 정한다.
    /// </summary>
    public static SimDeviceOpt DefaultSimOpt() => new()
    {
        BindAddress = IPAddress.Loopback,
        Width = 128,
        Height = 64,
        FrameRateHz = 30,
    };

    /// <summary>
    /// GVCP 타임아웃 3 s, 재시도 1 회, 루프백 고정. 시뮬레이터는 즉시 답하므로 타임아웃은 실패 판정에만 쓰인다 — 짧게 두면 과부하에서
    /// 늦게 온 ACK 를 타임아웃으로 치고 같은 명령을 재전송하는데, 시뮬레이터는 req_id 로 중복을 걸러 내지 않아 AcquisitionStart 같은
    /// 자기 소거 명령이 두 번 실행된다(단일 프레임 촬영에 프레임이 하나 더 나오는 식). 1 s 로는 굶주린 러너에서 실제로 그 일이 났다
    /// (루프백 왕복이 1 s 를 넘겨 WRITEREG 가 두 번 나갔다). 이것은 재시도 예산이지 기대 응답 시간이 아니므로 넉넉해도 정상 경로는 느려지지 않는다.
    /// </summary>
    public static GevDeviceOpt DefaultDeviceOpt() => new()
    {
        GvcpTimeoutMs = 3000,
        GvcpRetries = 1,
        // 하트비트를 시험하지 않는 테스트가 하트비트 때문에 깨지지 않게 여유를 크게 둔다 — 주기 500 ms 에 장치 타임아웃 10 s 면
        // 스무 번을 연달아 놓쳐야 만료다. 굶주린 러너에서 주기 1 s / 타임아웃 3 s 로는 실제로 제어권을 잃었다.
        // 만료 자체를 시험하는 테스트는 두 값을 직접 지정한다.
        HeartbeatTimeoutMs = 10_000,
        HeartbeatPeriodMs = 500,
        LocalAddress = IPAddress.Loopback,
    };

    /// <summary>
    /// 고정 1500, 버퍼 4, 리센드 유예 2 ms / 재요청 500 ms / 보존 2 s.
    /// 재요청 간격(PacketTimeoutMs)은 "이만큼 조용하면 꼬리가 다 왔다" 는 판정에도 쓰인다 — 과부하 러너에서는 시뮬레이터의 송신 스레드가
    /// 한 프레임 도중에 수십~수백 ms 선점당하므로, 그보다 넉넉해야 멀쩡한 프레임이 불완전으로 닫히지 않는다.
    /// </summary>
    public static GevStreamOpt DefaultStreamOpt() => new()
    {
        PacketSizeMode = PacketSizeMode.Fixed,
        PacketSize = 1500,
        BufferCount = 4,
        SocketBufferBytes = 4 * 1024 * 1024,
        InitialPacketTimeoutMs = 2,
        PacketTimeoutMs = 500,
        FrameRetentionMs = 2000,
        ReceiverPriority = ThreadPriority.Normal,
    };

    /// <summary>스트림 채널 0 을 열고 시작한다(SCDA/SCP/SCPS 설정까지). AcquisitionStart 는 보내지 않는다.</summary>
    public async Task<GevStream> OpenStreamAsync(GevStreamOpt? opt = null, Action<GevStream>? beforeStart = null)
    {
        var stream = await Device.OpenStreamAsync(opt ?? DefaultStreamOpt());
        beforeStart?.Invoke(stream);
        await stream.StartAsync();
        return stream;
    }

    /// <summary>AcquisitionMode 를 쓰고 AcquisitionStart = 1. Continuous 가 기본.</summary>
    public async Task StartAcquisitionAsync(uint mode = SimFeatureAddr.AcquisitionModeContinuous, uint frameCount = 0)
    {
        await Device.WriteRegAsync(SimFeatureAddr.AcquisitionMode, mode);
        if (frameCount > 0) await Device.WriteRegAsync(SimFeatureAddr.AcquisitionFrameCount, frameCount);
        await Device.WriteRegAsync(SimFeatureAddr.AcquisitionStart, 1);
    }

    public Task StopAcquisitionAsync() => Device.WriteRegAsync(SimFeatureAddr.AcquisitionStop, 1);

    /// <summary>
    /// TriggerMode = On(소스 Software)으로 두고 Continuous 획득을 켠다. 이후 프레임은 <see cref="TriggerAsync"/> 마다 하나씩만 나간다 —
    /// 벽시계가 아니라 테스트가 페이스를 정하므로 러너가 아무리 느려도 순서·개수·드롭 수가 그대로다.
    /// </summary>
    public async Task StartTriggeredAcquisitionAsync()
    {
        await Device.WriteRegAsync(SimFeatureAddr.TriggerControl, SimFeatureAddr.TriggerModeMask);
        await StartAcquisitionAsync();
    }

    /// <summary>
    /// 소프트웨어 트리거 한 번을 쓰고 시뮬레이터가 그 프레임을 다 내보낼 때까지(FramesSent 증가) 기다린다. 보낸 블록 ID 를 돌려준다.
    /// 시뮬레이터의 트리거는 플래그 하나라 연달아 쓰면 합쳐지므로, 반드시 앞 프레임이 나간 뒤에 다음을 쓴다.
    /// </summary>
    public async Task<ulong> TriggerAsync(int timeoutMs = 10_000)
    {
        var before = Sim.FramesSent;
        await Device.WriteRegAsync(SimFeatureAddr.TriggerSoftware, 1);
        await WaitUntilAsync(() => Sim.FramesSent > before, timeoutMs, "the simulator to send the triggered frame");
        return Sim.LastBlockId;
    }

    /// <summary>트리거 하나 → 프레임 하나. 호출자가 Dispose 한다.</summary>
    public async Task<GevFrame> TriggerAndReceiveAsync(GevStream stream, int timeoutMs = 10_000)
    {
        await TriggerAsync(timeoutMs);
        return await ReceiveAsync(stream, timeoutMs);
    }

    /// <summary>스트림 채널 0 레지스터를 시뮬레이터 쪽에서 직접 읽는다.</summary>
    public uint ReadStreamReg(uint offset) => Sim.Registers.ReadU32(GvbsAddr.StreamChannel(0, offset));

    /// <summary>시뮬레이터의 현재 Width/Height/PixelFormat 으로 기대 프레임 바이트를 만든다.</summary>
    public byte[] ExpectedFrame(ulong frameId)
        => SimDevice.BuildPatternFrame(
            (int)Sim.Registers.ReadU32(SimFeatureAddr.Width),
            (int)Sim.Registers.ReadU32(SimFeatureAddr.Height),
            Sim.Registers.ReadU32(SimFeatureAddr.PixelFormat),
            frameId,
            Sim.Registers.ReadU32(SimFeatureAddr.TestPattern));

    /// <summary>
    /// 프레임 하나를 기다린다. 시간 안에 오지 않으면 <see cref="TimeoutException"/> — 취소 예외가 실패 이유를 가리지 않게.
    /// 이 상한은 "멈춘 시험을 끝낸다" 는 뜻뿐이라 넉넉히 둔다 — 프레임이 얼마나 빨리 오는지를 재는 자리가 아니다.
    /// </summary>
    public static async Task<GevFrame> ReceiveAsync(GevStream stream, int timeoutMs = 10_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await stream.ReceiveAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException($"No frame within {timeoutMs} ms (stats: {stream.Stats.Snapshot()}).");
        }
    }

    /// <summary>count 개의 프레임을 받아 돌려준다. 호출자가 Dispose 한다.</summary>
    public static async Task<List<GevFrame>> ReceiveManyAsync(GevStream stream, int count, int timeoutMs = 10_000)
    {
        var frames = new List<GevFrame>(count);
        for (var i = 0; i < count; i++) frames.Add(await ReceiveAsync(stream, timeoutMs));
        return frames;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for: {what}");
            await Task.Delay(5);
        }
    }

    /// <summary>장치를 먼저(CCP 해제), 그 다음 시뮬레이터를 닫는다. 장치 닫기 실패는 시뮬레이터 정리를 막지 않는다.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Device.DisposeAsync();
        }
        finally
        {
            Sim.Dispose();
        }
    }
}
