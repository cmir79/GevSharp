using System.Diagnostics;
using GevSharp.Gvcp;
using GevSharp.Gvsp;
using GevSharp.Sim;

namespace GevSharp.Tests.Simulator;

/// <summary>시뮬레이터의 GVSP 송신기를 원시 UDP 로 검증한다 — 프레임 구조·패턴 바이트·블록 ID, 손실 주입과 리센드, 파이어테스트.</summary>
public class SimGvspTests
{
    private const uint Mono8 = 0x0108_0001;
    private const uint Scp = GvbsAddr.StreamChannel0 + GvbsAddr.ScpOffset;
    private const uint Scps = GvbsAddr.StreamChannel0 + GvbsAddr.ScpsOffset;
    private const uint Scpd = GvbsAddr.StreamChannel0 + GvbsAddr.ScpdOffset;
    private const uint Scda = GvbsAddr.StreamChannel0 + GvbsAddr.ScdaOffset;
    private const uint Sccfg = GvbsAddr.StreamChannel0 + GvbsAddr.SccfgOffset;

    private static SimDevice StartDevice(Action<SimDeviceOpt>? configure = null)
    {
        var opt = new SimDeviceOpt { Width = 64, Height = 32, FrameRateHz = 200 };
        configure?.Invoke(opt);
        var dev = new SimDevice(opt);
        dev.Start();
        return dev;
    }

    /// <summary>제어권을 잡고 스트림 채널을 수신기로 향하게 한다.</summary>
    private static void OpenChannel(RawGvcpClient c, RawGvspReceiver rx, uint scps = 1500)
    {
        c.WriteRegOk(GvbsAddr.Ccp, GvbsAddr.CcpControl);
        c.WriteRegOk(Scps, scps);
        c.WriteRegOk(Scda, rx.AddressU32);
        c.WriteRegOk(Scp, (uint)rx.Port);
    }

    private static void StartMultiFrame(RawGvcpClient c, uint count)
    {
        c.WriteRegOk(SimFeatureAddr.AcquisitionMode, SimFeatureAddr.AcquisitionModeMultiFrame);
        c.WriteRegOk(SimFeatureAddr.AcquisitionFrameCount, count);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);
    }

    /// <summary>AcquisitionStatus 가 0 으로 돌아올 때까지(송신 스레드가 마무리에 들어갈 때까지) 기다린다.</summary>
    private static void WaitUntilIdle(RawGvcpClient c)
    {
        var sw = Stopwatch.StartNew();
        while (c.ReadReg(SimFeatureAddr.AcquisitionStatus) != 0)
        {
            if (sw.ElapsedMilliseconds > 10_000) Assert.Fail("acquisition did not finish within 10 s");
            Thread.Sleep(1);
        }
    }

    /// <summary>조건이 설 때까지 기다린다. 시간 제한은 "멈춘 시험을 끝낸다" 는 뜻뿐이라 굶주린 러너를 재지 않도록 넉넉히 둔다.</summary>
    private static void WaitUntil(Func<bool> condition, int timeoutMs, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"timed out after {timeoutMs} ms waiting for {what}");
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// 호스트가 이 프로세스의 스레드를 제때 돌려주고 있는지 재는 탐침. 시뮬레이터에서 나온 값은 하나도 쓰지 않는다 —
    /// 프레임에서 뽑은 시간으로 문을 열면, 잡으려는 결함(장치가 명령보다 느려짐)이 스스로 문을 닫아 버린다.
    /// 두 신호를 함께 보고 **둘 다** 좋을 때만 문을 연다. 짧은 잠 20 번은 타이머 눈금이 밀리는 정도를,
    /// 양보 200 번은 코어를 물고 있는 실행 대기 스레드가 있는지를 드러낸다. 한 신호만으로는 문이 잘못 열린다 —
    /// 코어 수의 세 배로 굶긴 기계에서 잠 쪽이 한가할 때와 같은 39 ms 로 나온 표본도, 양보 쪽이 2 ms 로 나온 표본도 있었다.
    /// 문턱(45 ms · 2 ms)은 실측 60여 표본으로 갈랐다: 한가한 16 코어에서 38~41 ms · 0 ms, 세 배로 굶기면
    /// 39 ms 이상 · 2 ms 이상이라 두 값이 함께 문턱 아래인 표본은 굶은 쪽에 하나도 없었다. 양보 쪽은 문턱을 넘기면 바로 그만둔다 —
    /// 굶은 기계에서 200 번을 다 채우면 그것만으로 1 초 가까이 걸리기 때문이다.
    /// 한가한 기계에서도 순간적인 선점 한 번으로 값이 튀는 일이 있어(실측 31 ms) 세 번까지 다시 잰다.
    /// 문이 닫히는 쪽은 안전하다(단언을 건너뛸 뿐이다). 타이머 눈금이 굵은 호스트에서는 잠 쪽이 늘 문턱을 넘어
    /// 이 단언이 서지 않는데, 그것도 같은 안전한 방향이다.
    /// </summary>
    private static bool HostSchedulesPromptly()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var sleepProbe = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) Thread.Sleep(1);
            long sleepMs = sleepProbe.ElapsedMilliseconds;

            long yieldMs = long.MaxValue;
            if (sleepMs < 45)
            {
                var yieldProbe = Stopwatch.StartNew();
                for (int i = 0; i < 200 && yieldProbe.ElapsedMilliseconds < 2; i++) Thread.Yield();
                yieldMs = yieldProbe.ElapsedMilliseconds;
            }
            if (sleepMs < 45 && yieldMs < 2) return true;
        }
        return false;
    }

    /// <summary>테스트 쪽에서 독립적으로 계산한 기대 픽셀: Mono8 DiagonalRamp 는 (x + y + frameId) &amp; 0xFF.</summary>
    private static byte ExpectedMono8(int x, int y, ulong frameId) => (byte)(x + y + (int)frameId);

    private static void AssertFrame(RawGvspFrame f, ulong blockId, int width, int height, int dataBytes)
    {
        Assert.True(f.IsComplete, $"frame {blockId} incomplete: leader={f.Leader is not null} trailer={f.Trailer is not null} payloads={f.Payloads.Count}");
        Assert.Equal(blockId, f.BlockId);

        var leader = f.Leader!;
        Assert.Equal(0u, leader.PacketId);
        Assert.Equal(GvspConst.ImageLeaderDataSize, leader.Data.Length);
        Assert.Equal(GvspConst.PayloadImage, leader.DataU16(2));
        Assert.Equal(Mono8, leader.DataU32(12));
        Assert.Equal((uint)width, leader.DataU32(16));
        Assert.Equal((uint)height, leader.DataU32(20));
        Assert.Equal(0u, leader.DataU32(24));
        Assert.Equal(0u, leader.DataU32(28));
        Assert.Equal(0, leader.DataU16(32));
        Assert.Equal(0, leader.DataU16(34));

        int total = width * height;
        int expectedPackets = (total + dataBytes - 1) / dataBytes;
        Assert.Equal(expectedPackets, f.Payloads.Count);
        for (uint p = 1; p <= (uint)expectedPackets; p++)
        {
            Assert.True(f.Payloads.ContainsKey(p), $"payload packet {p} missing in frame {blockId}");
            int expectedLen = p < (uint)expectedPackets ? dataBytes : total - dataBytes * (expectedPackets - 1);
            Assert.Equal(expectedLen, f.Payloads[p].Data.Length);
        }

        var trailer = f.Trailer!;
        Assert.Equal((uint)expectedPackets + 1, trailer.PacketId);
        Assert.Equal(GvspConst.TrailerDataSize, trailer.Data.Length);
        Assert.Equal(GvspConst.PayloadImage, trailer.DataU16(2));
        Assert.Equal((uint)height, trailer.DataU32(4));

        var img = f.Assemble();
        Assert.Equal(total, img.Length);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (img[y * width + x] != ExpectedMono8(x, y, blockId))
                    Assert.Fail($"frame {blockId} pixel ({x},{y}) = {img[y * width + x]}, expected {ExpectedMono8(x, y, blockId)}");
        Assert.Equal(SimDevice.BuildPatternFrame(width, height, Mono8, blockId), img);
    }

    // ---- 스트림 ----

    [Fact]
    public void Stream_MultiFrame_DeliversFramesWithPatternAndSequentialBlockIds()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();
        var sent = new List<ulong>();
        dev.FrameSent += id => { lock (sent) sent.Add(id); };

        OpenChannel(c, rx, scps: 1500);
        c.WriteRegOk(Scpd, 50_000);   // 50 µs 패킷 간격 — 경로만 태운다
        var wall = Stopwatch.StartNew();   // 프레임 1 의 리더 타임스탬프보다 먼저 시작한다
        StartMultiFrame(c, 3);
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.AcquisitionStart));   // 자기 소거

        var frames = rx.CollectFrames(3, idleTimeoutMs: 3000);
        long wallNs = wall.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency;

        Assert.Equal(new ulong[] { 1, 2, 3 }, frames.Keys.OrderBy(k => k).ToArray());
        int dataBytes = 1500 - 28 - 8;
        foreach (var id in new ulong[] { 1, 2, 3 }) AssertFrame(frames[id], id, 64, 32, dataBytes);

        // 리더 타임스탬프는 단조 증가하고, 두 프레임 간격은 프레임 속도(200 Hz)를 지킨 만큼 벌어진다.
        // 하한 5 ms: 두 주기(10 ms)의 절반 — 장치가 프레임 속도를 무시하고 몰아 보내면(간격 ≈ 0) 걸린다.
        // 상한: 호스트가 잰 실제 경과 시간 — 장치 눈금이 1 GHz(1 틱 = 1 ns)보다 빠르면 걸린다. CPU 가 굶어 전송이 늦어지면
        //       벽시계도 같이 늘어나므로, 이 경계는 스케줄링 지연으로는 깨지지 않고 눈금이 틀렸을 때만 깨진다.
        ulong t1 = frames[1].Leader!.DataU64(4), t3 = frames[3].Leader!.DataU64(4);
        Assert.True(t3 > t1);
        // 위 상한은 장치가 명령보다 느리게 프레임을 내보내면 벽시계도 같이 늘어나 놓친다(200 Hz 를 2 Hz 로 잘못 지켜도 통과한다).
        // 그 갈래는 절대 시간 상한이라야 잡히므로, 호스트가 굶지 않았을 때만 여기서 함께 본다: 두 주기 10 ms 에 여섯 배 여유.
        // 문을 여는 판단은 시뮬레이터와 무관한 스케줄링 탐침이 내린다 — 굶은 기계에서는 건너뛰고 위의 상한만 남는다.
        if (HostSchedulesPromptly()) Assert.InRange(t3 - t1, 5_000_000ul, 60_000_000ul);
        Assert.InRange(t3 - t1, 5_000_000ul, (ulong)wallNs);

        Assert.Equal(0, rx.Drain(150));                    // MultiFrame 3 이면 거기서 끝
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.AcquisitionStatus));
        Assert.Equal(3, dev.FramesSent);
        Assert.Equal(3u, c.ReadReg(SimFeatureAddr.FrameCounter));
        Assert.Equal(3 * (2 + 2), dev.PacketsSent);          // 프레임마다 리더 + 페이로드 2 + 트레일러
        lock (sent) Assert.Equal(new ulong[] { 1, 2, 3 }, sent);
    }

    [Fact]
    public void Stream_ContinuousUntilAcquisitionStop()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        c.WriteRegOk(SimFeatureAddr.AcquisitionMode, SimFeatureAddr.AcquisitionModeContinuous);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);
        Assert.Equal(1u, c.ReadReg(SimFeatureAddr.AcquisitionStatus));
        Assert.True(dev.IsAcquiring);

        var frames = rx.CollectFrames(4, idleTimeoutMs: 3000);
        Assert.True(frames.Count >= 4);

        c.WriteRegOk(SimFeatureAddr.AcquisitionStop, 1);
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.AcquisitionStop));
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.AcquisitionStatus));
        Assert.False(dev.IsAcquiring);
        rx.Drain(100);                                     // 정지 직전에 나간 패킷을 비운다
        Assert.Equal(0, rx.Drain(150));
        int sentAtStop = dev.FramesSent;
        Assert.True(sentAtStop >= 4);

        // 다시 시작하면 블록 ID 는 이어서 센다
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);
        var more = rx.CollectFrames(1, idleTimeoutMs: 3000);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStop, 1);
        Assert.Contains((ulong)sentAtStop + 1, more.Keys);

        // 이 테스트의 AcquisitionStop 은 응답기 스레드에서 송신 스레드를 거둔다 — 장치가 한 명령 안에서 가장 오래
        // 스스로 기다리는 자리다. 여기를 재두지 않으면 "응답기가 송신기에 붙들린다" 는 회귀를 알아채는 곳이
        // 클라이언트의 4 초 상한밖에 남지 않아, 초 단위로 붙들려도 아무것도 실패하지 않는다.
        // 첫 잣대는 시간이 아니라 사건이다: 정지 요청이 선 뒤에도 송신기가 신호가 아닌 시간 만료로 깼다면,
        // 응답기는 그 잠이 끝나기를 기다린 것이다. 굶은 호스트에서도 값이 부풀지 않으므로 조건 없이 단언한다.
        Assert.Equal(0, dev.SenderWakeMissedCount);

        // 둘째 잣대는 붙들린 시간 자체다 — 어떤 이유로든 초 단위로 붙들리면 걸린다. 다만 거두는 대기가
        // 스케줄러에 걸려 있어 코어 수의 세 배로 굶기면 1284 ms 까지 재였으므로, 굶지 않은 호스트에서만 본다.
        if (HostSchedulesPromptly())
            Assert.True(dev.MaxCommandHandleMs < 1000, $"the GVCP responder held one command for {dev.MaxCommandHandleMs} ms");
    }

    [Fact]
    public void Stream_NotSentWhileChannelClosed()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        c.WriteRegOk(GvbsAddr.Ccp, GvbsAddr.CcpControl);
        c.WriteRegOk(Scda, rx.AddressU32);
        c.WriteRegOk(Scp, 0);                              // 채널 닫힘
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);
        Assert.Equal(0, rx.Drain(150));
        Assert.Equal(0, dev.FramesSent);

        c.WriteRegOk(Scp, (uint)rx.Port);                  // 획득 중에 채널을 열면 그때부터 나간다
        Assert.NotEmpty(rx.CollectFrames(1, idleTimeoutMs: 3000));
        c.WriteRegOk(SimFeatureAddr.AcquisitionStop, 1);
    }

    [Fact]
    public void Stream_MultiFrame_WaitsForChannelAndCountsOnlySentFrames()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        c.WriteRegOk(GvbsAddr.Ccp, GvbsAddr.CcpControl);
        c.WriteRegOk(Scda, rx.AddressU32);
        c.WriteRegOk(Scp, 0);                              // 채널 닫힘
        StartMultiFrame(c, 3);
        Assert.Equal(0, rx.Drain(150));                    // 200 Hz 면 30 주기가 지났지만 횟수는 소진되지 않는다
        Assert.Equal(0, dev.FramesSent);
        Assert.Equal(1u, c.ReadReg(SimFeatureAddr.AcquisitionStatus));

        c.WriteRegOk(Scp, (uint)rx.Port);                  // 채널을 열면 그때부터 세 프레임 전부 나온다
        var frames = rx.CollectFrames(3, idleTimeoutMs: 3000);
        Assert.Equal(new ulong[] { 1, 2, 3 }, frames.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(0, rx.Drain(150));
        Assert.Equal(3, dev.FramesSent);
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.AcquisitionStatus));
    }

    [Fact]
    public void AcquisitionStart_WhileRunningIsIgnored_AndBackToBackSingleFramesAreNotLost()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);
        Assert.NotEmpty(rx.CollectFrames(1, idleTimeoutMs: 3000));
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);  // 이미 획득 중 — 무시하되 기록한다
        Assert.Contains("already running", dev.LastError);
        Assert.True(dev.IsAcquiring);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStop, 1);
        Assert.False(dev.IsAcquiring);
        rx.Drain(100);
        Assert.Equal(0, rx.Drain(150));
        int sentBefore = dev.FramesSent;

        // SingleFrame 을 잇달아 시작하면 매번 정확히 한 프레임 — 직전 스레드가 마무리 중이어도 시작이 유실되지 않는다
        c.WriteRegOk(SimFeatureAddr.AcquisitionMode, SimFeatureAddr.AcquisitionModeSingleFrame);
        for (int i = 1; i <= 5; i++)
        {
            c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);
            var frames = rx.CollectFrames(1, idleTimeoutMs: 3000);
            Assert.Equal((ulong)(sentBefore + i), Assert.Single(frames).Key);
            WaitUntilIdle(c);
        }
        Assert.Equal(0, rx.Drain(150));
        Assert.Equal(sentBefore + 5, dev.FramesSent);
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.AcquisitionStatus));
    }

    [Fact]
    public void Stream_PacketSizeFromScps_AndSingleFrameMode()
    {
        using var dev = StartDevice(o => { o.Width = 100; o.Height = 50; });
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx, scps: 576);                     // dataBytes = 540 → 5000 바이트 = 540 × 9 + 140 → 페이로드 10 개
        c.WriteRegOk(SimFeatureAddr.AcquisitionMode, SimFeatureAddr.AcquisitionModeSingleFrame);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);

        var frames = rx.CollectFrames(1, idleTimeoutMs: 3000);
        var f = Assert.Single(frames).Value;
        int dataBytes = 576 - 28 - 8;
        AssertFrame(f, 1, 100, 50, dataBytes);
        Assert.Equal(10, f.Payloads.Count);
        Assert.Equal(140, f.Payloads[10].Data.Length);
        Assert.Equal(0, rx.Drain(150));
        Assert.Equal(1, dev.FramesSent);
    }

    [Fact]
    public void Stream_BlockIdWrapsFrom65535To1()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        dev.SeedBlockId(65534);
        OpenChannel(c, rx);
        StartMultiFrame(c, 3);

        var frames = rx.CollectFrames(3, idleTimeoutMs: 3000);
        Assert.Equal(new ulong[] { 1, 2, 65535 }, frames.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(2ul, dev.LastBlockId);
        AssertFrame(frames[65535], 65535, 64, 32, 1500 - 28 - 8);
        AssertFrame(frames[1], 1, 64, 32, 1500 - 28 - 8);
    }

    [Fact]
    public void Stream_ExtendedIds_Use20ByteHeaderAndDoNotWrap()
    {
        using var dev = StartDevice(o => o.ExtendedIds = true);
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        Assert.Equal(SimStreamBits.SccfgExtendedIds, c.ReadReg(Sccfg));
        dev.SeedBlockId(65535);
        OpenChannel(c, rx);
        StartMultiFrame(c, 2);

        var frames = rx.CollectFrames(2, idleTimeoutMs: 3000);
        Assert.Equal(new ulong[] { 65536, 65537 }, frames.Keys.OrderBy(k => k).ToArray());
        int dataBytes = 1500 - 28 - 20;
        foreach (var f in frames.Values)
        {
            Assert.True(f.Leader!.IsExtended);
            Assert.True(f.Trailer!.IsExtended);
            Assert.All(f.Payloads.Values, p => Assert.True(p.IsExtended));
            AssertFrame(f, f.BlockId, 64, 32, dataBytes);
        }
    }

    [Fact]
    public void Stream_OtherBitDepthUsesByteRamp()
    {
        const uint mono16 = 0x0110_0007;
        using var dev = StartDevice(o => { o.PixelFormat = mono16; o.Width = 16; o.Height = 4; });
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        c.WriteRegOk(SimFeatureAddr.AcquisitionMode, SimFeatureAddr.AcquisitionModeSingleFrame);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);

        var f = Assert.Single(rx.CollectFrames(1, idleTimeoutMs: 3000)).Value;
        Assert.Equal(mono16, f.Leader!.DataU32(12));
        var img = f.Assemble();
        Assert.Equal(16 * 2 * 4, img.Length);
        for (int y = 0; y < 4; y++)
            for (int b = 0; b < 32; b++)
                Assert.Equal((byte)(b + y + 1), img[y * 32 + b]);
    }

    // ---- 트리거 ----

    [Fact]
    public void Trigger_SoftwareTriggerReleasesOneFrame_AndTriggersArmedBeforeStartAreDiscarded()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        c.WriteRegOk(SimFeatureAddr.TriggerControl, SimFeatureAddr.TriggerModeMask);   // TriggerMode = On, TriggerSource = Software
        c.WriteRegOk(SimFeatureAddr.TriggerSoftware, 1);                               // 시작 전에 무장한 트리거 — AcquisitionStart 가 버린다
        Assert.Equal(0u, c.ReadReg(SimFeatureAddr.TriggerSoftware));                   // 자기 소거
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);

        Assert.Equal(0, rx.Drain(200));                                                // 트리거 없이는 프레임이 없다
        Assert.Equal(0, dev.FramesSent);
        Assert.True(dev.IsAcquiring);

        c.WriteRegOk(SimFeatureAddr.TriggerSoftware, 1);
        var frames = rx.CollectFrames(1, idleTimeoutMs: 3000);
        Assert.Equal(new ulong[] { 1 }, frames.Keys.ToArray());
        AssertFrame(frames[1], 1, 64, 32, 1500 - 28 - 8);
        Assert.Equal(0, rx.Drain(200));                                                // 트리거 하나에 프레임 하나
        Assert.Equal(1, dev.FramesSent);

        c.WriteRegOk(SimFeatureAddr.TriggerSoftware, 1);
        Assert.Contains(2ul, rx.CollectFrames(1, idleTimeoutMs: 3000).Keys);
        Assert.Equal(2, dev.FramesSent);

        // 정지 뒤에 무장한 트리거도 다음 실행으로 새지 않는다
        c.WriteRegOk(SimFeatureAddr.AcquisitionStop, 1);
        c.WriteRegOk(SimFeatureAddr.TriggerSoftware, 1);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);
        Assert.Equal(0, rx.Drain(200));
        Assert.Equal(2, dev.FramesSent);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStop, 1);
    }

    [Fact]
    public void Trigger_SoftwareTriggerIsNotArmedWhileTriggerModeIsOff()
    {
        // 짜임: 프레임 1 이 나간 직후 송신기는 프레임 2 의 자유 실행 대기에 들어가고, 그 대기가 끝나기 전에 TriggerMode 를 켜야
        // 세 번째 프레임이 자유 실행으로 나가지 않는다. 주기가 500 ms 였을 때는 그 안에 Sleep(100) 두 번과 레지스터 왕복을 넣어
        // 여유가 5 배뿐이었고 굶주린 러너에서는 그것으로 모자란다 — 주기를 2 s 로 늘리고 잠 대신 실제 사건을 기다린다.
        using var dev = StartDevice(o => o.FrameRateHz = 0.5);                         // 2 s 주기 — 그 사이에 레지스터를 바꾼다
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);                              // 자유 실행: 프레임 1 은 즉시, 프레임 2 는 2 s 뒤
        // 프레임 1 이 실제로 나갈 때까지 기다린다 — 송신기가 프레임 2 의 대기에 들어간 뒤라야 이어지는 쓰기가 프레임 2 를 가로채지 않는다.
        WaitUntil(() => dev.FramesSent >= 1, 30_000, "the first free-running frame");
        Thread.Sleep(50);
        c.WriteRegOk(SimFeatureAddr.TriggerSoftware, 1);                               // TriggerMode = Off 이므로 무장되지 않아야 한다
        c.WriteRegOk(SimFeatureAddr.TriggerControl, SimFeatureAddr.TriggerModeMask);   // 이제부터 트리거 모드

        var frames = rx.CollectFrames(2, idleTimeoutMs: 3000);
        Assert.Equal(new ulong[] { 1, 2 }, frames.Keys.OrderBy(k => k).ToArray());
        Assert.Equal(0, rx.Drain(300));                                                // 묵은 트리거가 세 번째 프레임을 내보내지 않는다
        Assert.Equal(2, dev.FramesSent);

        c.WriteRegOk(SimFeatureAddr.TriggerSoftware, 1);                               // 트리거 모드에서 쓴 것만 프레임이 된다
        Assert.Contains(3ul, rx.CollectFrames(1, idleTimeoutMs: 3000).Keys);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStop, 1);
    }

    // ---- 손실 주입·리센드 ----

    [Fact]
    public void Resend_DroppedPacketIsResentWithStatus0x0100_OnlyForOwner()
    {
        using var dev = StartDevice(o => o.DropPacket = (frame, packet) => frame == 2 && packet == 1);
        using var owner = new RawGvcpClient(dev.GvcpEndPoint);
        using var other = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(owner, rx);
        StartMultiFrame(owner, 3);
        var frames = rx.CollectFrames(3, idleTimeoutMs: 3000);

        Assert.True(frames[1].IsComplete);
        Assert.True(frames[3].IsComplete);
        Assert.False(frames[2].IsComplete);
        Assert.False(frames[2].Payloads.ContainsKey(1));
        Assert.True(frames[2].Payloads.ContainsKey(2));
        Assert.Equal(1, dev.PacketsDropped);

        // 제어권이 없는 쪽의 요청은 무시된다
        other.SendPacketResend(2, 1, 1);
        Assert.Null(rx.Receive(200));
        var ignored = Assert.Single(dev.ResendRequests);
        Assert.False(ignored.IsAccepted);
        Assert.Equal(other.LocalEndPoint, ignored.Sender);
        Assert.Equal(0, dev.PacketsResent);

        // 보유자의 요청은 같은 바이트를 status 0x0100 으로 다시 보낸다
        owner.SendPacketResend(2, 1, 1);
        var resent = rx.Receive(10_000);
        Assert.NotNull(resent);
        Assert.Equal(GvspConst.StatusPacketResend, resent!.Status);
        Assert.Equal(2ul, resent.BlockId);
        Assert.Equal(RawGvspPacket.Payload, resent.ContentType);
        Assert.Equal(1u, resent.PacketId);
        Assert.Equal(1500 - 28 - 8, resent.Data.Length);
        for (int i = 0; i < resent.Data.Length; i++)
        {
            int x = i % 64, y = i / 64;
            Assert.Equal(ExpectedMono8(x, y, 2), resent.Data[i]);
        }
        Assert.Null(rx.Receive(100));
        Assert.Equal(1, dev.PacketsResent);
        var accepted = dev.ResendRequests.Last();
        Assert.True(accepted.IsAccepted);
        Assert.Equal((2ul, 1u, 1u), (accepted.BlockId, accepted.FirstPacketId, accepted.LastPacketId));

        // 리센드 사본은 다시 떨어뜨리지 않는다 — 두 번째 요청도 도착한다
        owner.SendPacketResend(2, 1, 1);
        Assert.NotNull(rx.Receive(10_000));
        Assert.Equal(1, dev.PacketsDropped);

        // 채워 넣으면 프레임이 완성된다
        frames[2].Payloads[1] = resent;
        AssertFrame(frames[2], 2, 64, 32, 1500 - 28 - 8);
    }

    [Fact]
    public void Resend_WholeFrameIncludingLeaderAndTrailer()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        StartMultiFrame(c, 2);
        rx.CollectFrames(2, idleTimeoutMs: 3000);

        c.SendPacketResend(1, 0, 3);
        var got = new List<RawGvspPacket>();
        while (got.Count < 4 && rx.Receive(10_000) is { } p) got.Add(p);

        Assert.Equal(4, got.Count);
        Assert.All(got, p => Assert.Equal(GvspConst.StatusPacketResend, p.Status));
        Assert.All(got, p => Assert.Equal(1ul, p.BlockId));
        Assert.Equal(new[] { RawGvspPacket.Leader, RawGvspPacket.Payload, RawGvspPacket.Payload, RawGvspPacket.Trailer }, got.Select(p => p.ContentType).ToArray());
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, got.Select(p => p.PacketId).ToArray());
        Assert.Equal(GvspConst.ImageLeaderDataSize, got[0].Data.Length);
        Assert.Equal(GvspConst.TrailerDataSize, got[3].Data.Length);
        Assert.Equal(4, dev.PacketsResent);
    }

    [Fact]
    public void Resend_UnknownBlock_SendsPacketUnavailableErrorPackets()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        StartMultiFrame(c, 1);
        rx.CollectFrames(1, idleTimeoutMs: 3000);

        c.SendPacketResend(999, 5, 6);
        var e1 = rx.Receive(10_000);
        var e2 = rx.Receive(10_000);

        Assert.NotNull(e1);
        Assert.NotNull(e2);
        Assert.Equal(GvspConst.StatusPacketUnavailable, e1!.Status);
        Assert.Equal(GvspConst.StatusPacketUnavailable, e2!.Status);
        Assert.Equal(999ul, e1.BlockId);
        Assert.Equal(5u, e1.PacketId);
        Assert.Equal(6u, e2.PacketId);
        Assert.Empty(e1.Data);
        Assert.Null(rx.Receive(100));
        Assert.Equal(2, dev.ResendErrorPackets);
        Assert.Equal(0, dev.PacketsResent);
        Assert.Contains("not in the resend history", dev.LastError);
    }

    [Fact]
    public void Resend_HistoryIsBounded()
    {
        using var dev = StartDevice(o => o.ResendHistoryFrames = 2);
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        StartMultiFrame(c, 4);
        rx.CollectFrames(4, idleTimeoutMs: 3000);

        c.SendPacketResend(1, 1, 1);                       // 이력 밖
        var e = rx.Receive(10_000);
        Assert.NotNull(e);
        Assert.Equal(GvspConst.StatusPacketUnavailable, e!.Status);

        c.SendPacketResend(4, 1, 1);                       // 이력 안
        var ok = rx.Receive(10_000);
        Assert.NotNull(ok);
        Assert.Equal(GvspConst.StatusPacketResend, ok!.Status);
        Assert.Equal(4ul, ok.BlockId);
    }

    [Fact]
    public void ResendRequests_ListIsBoundedAndClearable()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);

        const int total = SimDevice.ResendRequestsCap + 6;
        for (uint i = 0; i < total; i++) c.SendPacketResend(i, 1, 1);   // 제어권이 없으니 전부 무시되고 기록만 남는다
        c.ReadReg(GvbsAddr.Version);                                     // 서버는 도착 순서대로 처리한다 — 이 응답이 오면 앞의 것은 끝났다

        Assert.Equal(6, dev.ResendRequestsTrimmed);
        var kept = dev.ResendRequests;
        Assert.Equal(SimDevice.ResendRequestsCap, kept.Count);
        Assert.Equal(6ul, kept[0].BlockId);                              // 오래된 것부터 버린다
        Assert.Equal((ulong)(total - 1), kept[kept.Count - 1].BlockId);
        Assert.All(kept, r => Assert.False(r.IsAccepted));

        dev.ClearResendRequests();
        Assert.Empty(dev.ResendRequests);
        Assert.Equal(6, dev.ResendRequestsTrimmed);
    }

    [Fact]
    public void Resend_ExtendedIdsCommandIsParsed()
    {
        using var dev = StartDevice(o => o.ExtendedIds = true);
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        dev.SeedBlockId(0x1_0000_0000);
        OpenChannel(c, rx);
        StartMultiFrame(c, 1);
        rx.CollectFrames(1, idleTimeoutMs: 3000);

        c.SendPacketResend(0x1_0000_0001, 2, 2, extended: true);
        var p = rx.Receive(10_000);

        Assert.NotNull(p);
        Assert.True(p!.IsExtended);
        Assert.Equal(GvspConst.StatusPacketResend, p.Status);
        Assert.Equal(0x1_0000_0001ul, p.BlockId);
        Assert.Equal(2u, p.PacketId);
    }

    // ---- SCPS 파이어테스트 ----

    [Fact]
    public void FireTest_PacketArrivesUpToCapAndNotAbove()
    {
        using var dev = StartDevice(o => o.MaxPacketSize = 1500);
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        c.WriteRegOk(GvbsAddr.Ccp, GvbsAddr.CcpControl);
        c.WriteRegOk(Scda, rx.AddressU32);
        c.WriteRegOk(Scp, (uint)rx.Port);

        c.WriteRegOk(Scps, GvbsAddr.ScpsFireTest | 1400);
        var t1 = rx.ReceiveRaw(1000);
        Assert.NotNull(t1);
        Assert.Equal(1400 - 28, t1!.Length);
        Assert.All(t1, b => Assert.Equal((byte)0, b));
        Assert.Equal(1400u, c.ReadReg(Scps));               // 비트는 지워지고 크기만 남는다
        Assert.Equal(1, dev.TestPacketsSent);

        c.WriteRegOk(Scps, GvbsAddr.ScpsFireTest | 9000);   // 상한 초과 → 무시
        Assert.Null(rx.ReceiveRaw(250));
        Assert.Equal(9000u, c.ReadReg(Scps));
        Assert.Equal(1, dev.TestPacketsIgnored);

        c.WriteRegOk(Scps, GvbsAddr.ScpsFireTest | 1500);   // 상한과 같으면 보낸다
        var t3 = rx.ReceiveRaw(1000);
        Assert.NotNull(t3);
        Assert.Equal(1500 - 28, t3!.Length);
        Assert.Equal(2, dev.TestPacketsSent);

        c.WriteRegOk(Scps, GvbsAddr.ScpsDoNotFragment | 1200);   // 파이어 비트 없이 쓰면 패킷 없음, 다른 비트는 보존
        Assert.Null(rx.ReceiveRaw(150));
        Assert.Equal(GvbsAddr.ScpsDoNotFragment | 1200, c.ReadReg(Scps));
    }

    [Fact]
    public void FireTest_WithoutDestinationIsIgnored()
    {
        using var dev = StartDevice();
        using var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        c.WriteRegOk(Scps, GvbsAddr.ScpsFireTest | 1400);   // SCDA/SCP 미설정 — 보낼 곳이 없으니 의도적으로 무시(호스트는 채널을 먼저 연다)
        Assert.Null(rx.ReceiveRaw(150));
        Assert.Equal(1, dev.TestPacketsIgnored);
        Assert.Contains("SCDA/SCP not set", dev.LastError);
    }

    [Fact]
    public void Stop_EndsAcquisitionAndSockets()
    {
        var dev = StartDevice();
        var c = new RawGvcpClient(dev.GvcpEndPoint);
        using var rx = new RawGvspReceiver();

        OpenChannel(c, rx);
        c.WriteRegOk(SimFeatureAddr.AcquisitionStart, 1);
        Assert.NotEmpty(rx.CollectFrames(2, idleTimeoutMs: 3000));

        dev.Stop();
        rx.Drain(100);
        Assert.Equal(0, rx.Drain(150));
        Assert.False(dev.IsRunning);
        Assert.False(dev.IsAcquiring);
        c.SendRaw(RawGvcpClient.BuildCmd(GvcpConst.ReadRegCmd, GvcpConst.FlagAckRequired, 1, RawGvcpClient.ReadRegPayload(GvbsAddr.Version)));
        Assert.Null(c.Receive(150));
        c.Dispose();
        dev.Dispose();
    }
}
