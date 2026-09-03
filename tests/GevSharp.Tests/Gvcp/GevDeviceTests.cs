using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using GevSharp.Gvcp;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvcp;

/// <summary>루프백 응답기로 장치 세션의 열기·CCP·하트비트·제어권 상실·닫기·레지스터/메모리 접근을 확인한다.</summary>
public class GevDeviceTests
{
    private static GevDeviceOpt FastOpt(Action<GevDeviceOpt>? tweak = null)
    {
        var o = new GevDeviceOpt { GvcpTimeoutMs = 300, GvcpRetries = 1, HeartbeatTimeoutMs = 3000, HeartbeatPeriodMs = 1000 };
        tweak?.Invoke(o);
        return o;
    }

    private static int IndexOfWrite(IReadOnlyList<GvcpTestResponder.RequestRecord> reqs, uint addr)
    {
        for (var i = 0; i < reqs.Count; i++)
            if (reqs[i].Command == GvcpConst.WriteRegCmd && reqs[i].Addresses.Contains(addr)) return i;
        return -1;
    }

    private static bool HasWriteOf(IReadOnlyList<GvcpTestResponder.RequestRecord> reqs, uint addr, uint value)
    {
        foreach (var q in reqs)
        {
            if (q.Command != GvcpConst.WriteRegCmd) continue;
            for (var i = 0; i < q.Addresses.Length; i++)
                if (q.Addresses[i] == addr && q.Values[i] == value) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- open

    [Fact]
    public async Task OpenReadsBootstrapTakesControlThenWritesHeartbeatTimeout()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());

        Assert.True(dev.IsOpen);
        Assert.Equal(IPAddress.Loopback, dev.Address);
        Assert.Equal(IPAddress.Loopback, dev.LocalAddress);
        Assert.Equal(IPAddress.Loopback, dev.Gvcp.LocalEndPoint.Address);
        Assert.Equal(GevAccessMode.Control, dev.AccessMode);
        Assert.Equal("GevSharp Test", dev.Info.Manufacturer);
        Assert.Equal("Responder", dev.Info.Model);
        Assert.Equal("SN0001", dev.Info.SerialNumber);
        Assert.Equal(PhysicalAddress.Parse("00-11-22-33-44-55"), dev.Info.Mac);
        Assert.Equal(1, dev.Info.SpecMajor);
        Assert.Equal(2, dev.Info.SpecMinor);
        Assert.Equal(IPAddress.Loopback, dev.Info.InterfaceAddress);
        Assert.Equal(r.ReadU32(GvbsAddr.GvcpCapability), dev.GvcpCapability);
        Assert.Equal(125_000_000ul, dev.TimestampTickFrequency);
        Assert.Equal(GvbsAddr.CcpControl, r.ReadU32(GvbsAddr.Ccp));
        Assert.Equal(3000u, r.ReadU32(GvbsAddr.HeartbeatTimeout));
        Assert.Equal(3000, dev.DeviceHeartbeatTimeoutMs);
        Assert.Equal(1000, dev.HeartbeatPeriodMs);

        var reqs = r.Requests;
        // 식별 블록은 필드별로 읽는다 — 첫 요청은 Version READREG 다(GevDeviceInfoReadTests 가 전체 요청 목록을 본다).
        Assert.Equal(GvcpConst.ReadRegCmd, reqs[0].Command);
        Assert.Equal(GvbsAddr.Version, Assert.Single(reqs[0].Addresses));
        var ccp = IndexOfWrite(reqs, GvbsAddr.Ccp);
        var hb = IndexOfWrite(reqs, GvbsAddr.HeartbeatTimeout);
        Assert.True(ccp >= 0, "CCP was not written");
        Assert.True(hb > ccp, "heartbeat timeout must be written after control is taken");
    }

    [Fact]
    public async Task ExclusiveWithSwitchoverSetsAllCcpBits()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => { o.AccessMode = GevAccessMode.Exclusive; o.AllowSwitchover = true; }));

        Assert.Equal(GvbsAddr.CcpControl | GvbsAddr.CcpExclusive | GvbsAddr.CcpSwitchoverEnable, r.ReadU32(GvbsAddr.Ccp));
        Assert.Equal(GevAccessMode.Exclusive, dev.AccessMode);
    }

    [Fact]
    public async Task ReadOnlyNeverWritesAndRunsNoHeartbeat()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => { o.AccessMode = GevAccessMode.ReadOnly; o.HeartbeatPeriodMs = 20; }));

        Assert.True(dev.IsOpen);
        Assert.Equal(0, dev.HeartbeatPeriodMs);
        await Task.Delay(150);
        Assert.Equal(0u, r.ReadU32(GvbsAddr.Ccp));
        Assert.Equal(0, r.CountOf(GvcpConst.WriteRegCmd));
        Assert.Equal(0, r.CountOf(GvcpConst.WriteMemCmd));
        Assert.Equal(0, r.CountOfReg(GvcpConst.ReadRegCmd, GvbsAddr.Ccp));

        Assert.Equal(r.ReadU32(GvbsAddr.Version), await dev.ReadRegAsync(GvbsAddr.Version));
        await dev.DisposeAsync();
        Assert.Equal(0, r.CountOf(GvcpConst.WriteRegCmd));
    }

    [Fact]
    public async Task OpenFailsWithControlLostWhenAnotherHostHoldsCcp()
    {
        using var r = new GvcpTestResponder();
        r.IsCcpHeldByOther = true;

        var ex = await Assert.ThrowsAsync<GevControlLostException>(() => GevDevice.OpenAsync(r.EndPoint, FastOpt()));

        Assert.Contains("another application", ex.Message);
        Assert.Equal(0, r.CountOfReg(GvcpConst.WriteRegCmd, GvbsAddr.HeartbeatTimeout));
        Assert.Equal(0u, r.ReadU32(GvbsAddr.Ccp));
    }

    [Fact]
    public async Task OpenFailsWithTimeoutWhenDeviceIsSilent()
    {
        using var r = new GvcpTestResponder();
        r.IsSilent = true;
        var sw = Stopwatch.StartNew();

        await Assert.ThrowsAsync<GevTimeoutException>(() => GevDevice.OpenAsync(r.EndPoint, FastOpt(o => { o.GvcpTimeoutMs = 40; o.GvcpRetries = 1; })));

        // 예산은 40 ms × 2 지만 그 80 ms 를 재지는 않는다 — 과부하에서는 소켓·스레드·타이머의 고정 비용이 그보다 크다.
        // 이 상한이 겨냥하는 것은 옵션이 무시되고 PENDING_ACK 상한(기본 10 s)까지 붙잡히거나 포기 자체가 사라지는 회귀다.
        // 몇 번 보냈는지는 바로 아래 CountOf 가 시간과 무관하게 못 박는다.
        Assert.True(sw.ElapsedMilliseconds < 8000, $"open took {sw.ElapsedMilliseconds} ms for a 40 ms x 2 budget");
        // 첫 요청(Version READREG)을 재시도 한 번까지 두 번 보내고 포기한다.
        Assert.Equal(2, r.CountOf(GvcpConst.ReadRegCmd));
        Assert.Equal(0, r.CountOf(GvcpConst.ReadMemCmd));
    }

    [Fact]
    public async Task OpenValidatesOptions()
    {
        using var r = new GvcpTestResponder();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GevDevice.OpenAsync(r.EndPoint, new GevDeviceOpt { GvcpTimeoutMs = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GevDevice.OpenAsync(r.EndPoint, new GevDeviceOpt { HeartbeatPeriodMs = 0 }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GevDevice.OpenAsync((IPAddress)null!));
        Assert.Empty(r.Requests);
    }

    [Fact]
    public async Task ExplicitLocalAddressIsHonoured()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => o.LocalAddress = IPAddress.Loopback));
        Assert.Equal(IPAddress.Loopback, dev.LocalAddress);
        Assert.Equal(IPAddress.Loopback, dev.Gvcp.LocalEndPoint.Address);
    }

    // ---------------------------------------------------------------- heartbeat / control lost

    [Fact]
    public async Task HeartbeatReadsCcpPeriodically()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => o.HeartbeatPeriodMs = 25));

        await GvcpChannelTests.WaitUntilAsync(() => r.CountOfReg(GvcpConst.ReadRegCmd, GvbsAddr.Ccp) >= 3, what: "heartbeat reads");
        Assert.True(dev.IsOpen);
        Assert.Equal(25, dev.HeartbeatPeriodMs);
    }

    [Fact]
    public async Task ThreeConsecutiveHeartbeatFailuresRaiseControlLost()
    {
        using var r = new GvcpTestResponder();
        // GVCP 예산 500 ms / 재시도 없음: 이 값은 응답기가 침묵한 뒤 하트비트 한 번이 실패로 판정되기까지의 시간이면서,
        // 그 전에 열기의 왕복 하나에 주어지는 시간이기도 하다. 40 ms 로는 굶주린 러너에서 열기 자체가 타임아웃으로 깨졌다 —
        // 짧게 잡아 아끼는 것은 실패 판정 시간(3 회 × 520 ms)뿐이고, 잃는 것은 시험이 성립할 조건이다.
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => { o.HeartbeatPeriodMs = 20; o.GvcpTimeoutMs = 500; o.GvcpRetries = 0; }));
        var lost = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dev.ControlLost += (d, ex) => lost.TrySetResult(ex);

        r.IsSilent = true;
        // 상한은 "판정이 나오기는 한다" 는 뜻뿐이다 — 세 번의 실패에 필요한 시간(약 1.6 s)보다 한참 위에 둔다.
        var done = await Task.WhenAny(lost.Task, Task.Delay(15_000));
        Assert.Same(lost.Task, done);

        Assert.IsType<GevTimeoutException>(await lost.Task);
        Assert.False(dev.IsOpen);
        Assert.True(r.CountOfReg(GvcpConst.ReadRegCmd, GvbsAddr.Ccp) >= GevDevice.HeartbeatMaxFailures);
        await Assert.ThrowsAsync<GevControlLostException>(() => dev.ReadRegAsync(0));

        r.IsSilent = false;
        await dev.DisposeAsync();
        Assert.False(HasWriteOf(r.Requests, GvbsAddr.Ccp, 0));
    }

    [Fact]
    public async Task CcpReleasedElsewhereRaisesControlLost()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => o.HeartbeatPeriodMs = 20));
        var lost = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        dev.ControlLost += (d, ex) => lost.TrySetResult(ex);

        r.WriteU32(GvbsAddr.Ccp, 0);
        var done = await Task.WhenAny(lost.Task, Task.Delay(5000));
        Assert.Same(lost.Task, done);

        Assert.IsType<GevControlLostException>(await lost.Task);
        Assert.False(dev.IsOpen);
    }

    [Fact]
    public async Task DisposeFromInsideControlLostHandlerDoesNotDeadlock()
    {
        using var r = new GvcpTestResponder();
        var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => o.HeartbeatPeriodMs = 20));
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dev.ControlLost += async (d, ex) =>
        {
            try
            {
                await d.DisposeAsync();
                disposed.TrySetResult(true);
            }
            catch (Exception e)
            {
                disposed.TrySetException(e);
            }
        };

        r.WriteU32(GvbsAddr.Ccp, 0);
        var done = await Task.WhenAny(disposed.Task, Task.Delay(5000));
        Assert.Same(disposed.Task, done);
        Assert.True(await disposed.Task);
        Assert.True(dev.Gvcp.IsDisposed);
    }

    [Fact]
    public async Task PendingAckWaitCapIsDerivedFromTheHeartbeatUnlessItIsSetExplicitly()
    {
        using var r = new GvcpTestResponder();
        await using (var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt()))
        {
            // 장치가 받아들인 타임아웃 3000, 주기 1000, GVCP 응답 창 300 → 3000 - 1000 - 2*300.
            Assert.Equal(3000, dev.DeviceHeartbeatTimeoutMs);
            Assert.Equal(1400, dev.Gvcp.Opt.MaxPendingAckWaitMs);
            // 지켜야 하는 것은 "응답 창 + 상한 < 장치 타임아웃" 이 아니라 마지막 CCP 읽기부터 다음 CCP 읽기까지의 공백이다:
            // (주기) + (PENDING_ACK 요청 하나가 줄을 붙드는 시간 = 응답 창 + 상한) + (하트비트 자신의 왕복 한 번).
            var worstGapMs = dev.HeartbeatPeriodMs + (dev.Gvcp.Opt.TimeoutMs + dev.Gvcp.Opt.MaxPendingAckWaitMs) + dev.Gvcp.Opt.TimeoutMs;
            Assert.True(worstGapMs <= dev.DeviceHeartbeatTimeoutMs, $"worst heartbeat gap {worstGapMs} ms vs device timeout {dev.DeviceHeartbeatTimeoutMs} ms");
        }

        // 명시한 값은 그대로 간다.
        await using (var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => o.MaxPendingAckWaitMs = 250)))
            Assert.Equal(250, dev.Gvcp.Opt.MaxPendingAckWaitMs);

        // 하트비트가 없는 세션은 좁힐 이유가 없다 — 채널 기본값 그대로.
        await using (var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => o.AccessMode = GevAccessMode.ReadOnly)))
            Assert.Equal(GvcpChannelOpt.DefaultMaxPendingAckWaitMs, dev.Gvcp.Opt.MaxPendingAckWaitMs);

        // 여유가 남지 않는 설정(1000 - 333 - 2*300)에서도 응답 창 하나는 남긴다 — 0 으로 떨어뜨리면 PENDING_ACK 이 무의미해진다.
        // (그 상황을 경고로 알리는지는 GevDeviceLogTests 가 본다.)
        await using (var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => { o.HeartbeatTimeoutMs = 1000; o.HeartbeatPeriodMs = null; })))
            Assert.Equal(300, dev.Gvcp.Opt.MaxPendingAckWaitMs);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GevDevice.OpenAsync(r.EndPoint, new GevDeviceOpt { MaxPendingAckWaitMs = -1 }));
    }

    [Fact]
    public async Task AStalledPendingAckReleasesTheChannelWithinTheHeartbeatWindow()
    {
        // 장치가 "60 초 뒤에 끝난다" 고 예고해 놓고 본 응답을 보내지 않으면, 상한이 없는 채널은 그 요청 하나로
        // 10 초 넘게 줄을 붙든다. 하트비트도 같은 줄에 서므로 아무것도 실패하지 않았는데 제어권이 날아간다.
        // 재시도는 출하 기본값(3)으로 둔다 — 상한이 시도마다 새로 붙으면 붙들리는 시간이 네 배가 되므로 여기서 걸린다.
        using var r = new GvcpTestResponder();
        var opt = FastOpt(o => { o.GvcpTimeoutMs = 200; o.GvcpRetries = 3; o.HeartbeatTimeoutMs = 1200; o.HeartbeatPeriodMs = 400; });
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, opt);
        Assert.Equal(400, dev.Gvcp.Opt.MaxPendingAckWaitMs);   // 1200 - 400 - 2*200

        r.PendingAckStallAddr = 0x4000;
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<GevTimeoutException>(() => dev.ReadRegAsync(0x4000));
        sw.Stop();

        // PENDING_ACK 를 받은 명령은 다시 보내지 않는다 — 장치가 이미 받아 실행 중이라고 답했고, 재전송은 두 번 실행될 위험만 있다.
        Assert.Equal(1, r.CountOfReg(GvcpConst.ReadRegCmd, 0x4000));
        Assert.Equal(1, dev.Gvcp.PendingAckCount);
        // 상한이 채널 기본값(10 s)이면 10 초 넘게, 시도마다 상한이 새로 붙으면 4 x (200 + 400) 만큼 붙들린다.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"the stalled request held the GVCP channel for {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task TheHeartbeatKeepsReachingTheDeviceWhileAStalledRequestHoldsTheChannel()
    {
        // 위 시험이 재는 "붙들린 시간" 이 실제로 지키려는 것 — 장치가 보는 CCP 읽기 사이의 공백이 장치 하트비트 타임아웃 안에 머무는 것.
        // 상한을 명시해 최악의 공백(주기 300 + 붙들림 300 + 왕복)이 타임아웃 1200 에 여유를 두고 들어오게 한다.
        // 상한이 없으면(10 s) 공백은 10 초가 되고, 상한이 시도마다 붙으면 4 x (150 + 150) = 1200 이라 역시 넘는다.
        using var r = new GvcpTestResponder();
        var opt = FastOpt(o =>
        {
            o.GvcpTimeoutMs = 150;
            o.GvcpRetries = 3;
            o.HeartbeatTimeoutMs = 1200;
            o.HeartbeatPeriodMs = 300;
            o.MaxPendingAckWaitMs = 150;
        });
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, opt);
        await Task.Delay(700);   // 하트비트가 몇 번 돈다

        r.PendingAckStallAddr = 0x4000;
        await Assert.ThrowsAsync<GevTimeoutException>(() => dev.ReadRegAsync(0x4000));
        r.PendingAckStallAddr = null;
        await Task.Delay(700);   // 놓아 준 뒤 하트비트가 다시 온다
        var until = r.ElapsedMs;

        var beats = r.Requests
            .Where(q => q.Command == GvcpConst.ReadRegCmd && q.Addresses.Contains(GvbsAddr.Ccp) && q.AtMs <= until)
            .Select(q => q.AtMs)
            .ToList();
        Assert.True(beats.Count >= 3, $"only {beats.Count} heartbeat reads arrived");
        var maxGapMs = 0L;
        for (var i = 1; i < beats.Count; i++) maxGapMs = Math.Max(maxGapMs, beats[i] - beats[i - 1]);
        Assert.True(maxGapMs < dev.DeviceHeartbeatTimeoutMs,
            $"the device saw a {maxGapMs} ms gap between CCP reads; its heartbeat timeout is {dev.DeviceHeartbeatTimeoutMs} ms (beats at {string.Join(", ", beats)})");
        Assert.True(dev.IsOpen);
    }

    [Fact]
    public async Task ADeviceThatUsesPendingAckDuringOpenStillOpens()
    {
        // 여는 동안에는 하트비트가 아직 돌지 않는다 — 앞질러 좁힐 것이 없다. 열기 순서의 명령 하나에 PENDING_ACK 로
        // 시간을 버는 장치(여기서는 하트비트 타임아웃 레지스터)를 채널 기본 상한으로 받아 주고, 좁히기는 열린 뒤에 한다.
        using var r = new GvcpTestResponder();
        r.PendingAckAddr = GvbsAddr.HeartbeatTimeout;
        r.PendingAckMs = 5000;         // 장치가 예고하는 완료 시간
        r.PendingAckDelayMs = 700;     // 실제 응답 — 응답 창(200)은 물론 좁힌 상한(200 + 400)도 넘는다
        var opt = FastOpt(o => { o.GvcpTimeoutMs = 200; o.GvcpRetries = 0; o.HeartbeatTimeoutMs = 1200; o.HeartbeatPeriodMs = 400; });

        await using var dev = await GevDevice.OpenAsync(r.EndPoint, opt);
        r.PendingAckMs = 0;
        r.PendingAckAddr = null;

        Assert.True(dev.IsOpen);
        Assert.True(dev.Gvcp.PendingAckCount >= 1);
        // 열린 뒤에는 하트비트에 맞춰 좁아져 있다.
        Assert.Equal(400, dev.Gvcp.Opt.MaxPendingAckWaitMs);   // 1200 - 400 - 2*200
    }

    // ---------------------------------------------------------------- dispose

    [Fact]
    public async Task DisposeReleasesControlAndClosesChannel()
    {
        using var r = new GvcpTestResponder();
        var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        Assert.Equal(GvbsAddr.CcpControl, r.ReadU32(GvbsAddr.Ccp));

        await dev.DisposeAsync();

        Assert.Equal(0u, r.ReadU32(GvbsAddr.Ccp));
        var last = r.Requests[r.Requests.Count - 1];
        Assert.Equal(GvcpConst.WriteRegCmd, last.Command);
        Assert.Equal(GvbsAddr.Ccp, last.Addresses[0]);
        Assert.Equal(0u, last.Values[0]);
        Assert.False(dev.IsOpen);
        Assert.True(dev.Gvcp.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dev.ReadRegAsync(0));

        var count = r.Requests.Count;
        await dev.DisposeAsync();
        await Task.Delay(50);
        Assert.Equal(count, r.Requests.Count);
    }

    [Fact]
    public async Task DisposeGivesUpReleasingControlWithinTheFixedBudget()
    {
        using var r = new GvcpTestResponder();
        // 채널 예산은 1000 ms × (1 + 20) = 21 s 지만 닫기는 고정 예산(GevDevice.CcpReleaseMaxMs = 2 s) 안에 포기해야 한다.
        // 재시도를 크게 잡아 정상 동작(2 s 근처)과 회귀(채널 예산 21 s)를 멀찍이 떼어 놓는다 — 그래야 상한을
        // 굶주린 스케줄러가 넘지 못할 만큼 넉넉히 두고도 회귀를 놓치지 않는다.
        var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt(o => { o.GvcpTimeoutMs = 1000; o.GvcpRetries = 20; o.HeartbeatPeriodMs = 60_000; }));
        r.IsSilent = true;

        var sw = Stopwatch.StartNew();
        await dev.DisposeAsync();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 8000, $"dispose took {sw.ElapsedMilliseconds} ms; the fixed release budget is {GevDevice.CcpReleaseMaxMs} ms, the channel budget 21 s");
        Assert.True(dev.Gvcp.IsDisposed);
        Assert.False(dev.IsOpen);
        Assert.True(HasWriteOf(r.Requests, GvbsAddr.Ccp, 0), "CCP release was not attempted");
    }

    // ---------------------------------------------------------------- registers

    [Fact]
    public async Task ReadRegsBatchesByMaxRegsPerPacketWhenConcatenationIsSupported()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        const int n = 300;
        var addrs = new uint[n];
        for (var i = 0; i < n; i++)
        {
            addrs[i] = (uint)(0x4000 + i * 4);
            r.WriteU32(addrs[i], (uint)(0x10000 + i));
        }
        var before = r.CountOf(GvcpConst.ReadRegCmd);

        var values = await dev.ReadRegsAsync(addrs);

        Assert.Equal(n, values.Length);
        for (var i = 0; i < n; i++) Assert.Equal((uint)(0x10000 + i), values[i]);
        var batches = r.Requests.Skip(0).Where(q => q.Command == GvcpConst.ReadRegCmd && q.Addresses.Length > 1).Select(q => q.Addresses.Length).ToList();
        Assert.Equal(new[] { 135, 135, 30 }, batches);
        Assert.Equal(before + 3, r.CountOf(GvcpConst.ReadRegCmd));
        Assert.Empty(await dev.ReadRegsAsync(Array.Empty<uint>()));
    }

    [Fact]
    public async Task ReadRegsGoOneAtATimeWithoutConcatenation()
    {
        using var r = new GvcpTestResponder();
        r.WriteU32(GvbsAddr.GvcpCapability, GvbsAddr.GvcpCapWriteMem);
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        r.WriteU32(0x4000, 1);
        r.WriteU32(0x4004, 2);
        r.WriteU32(0x4008, 3);

        var values = await dev.ReadRegsAsync(new uint[] { 0x4000, 0x4004, 0x4008 });

        Assert.Equal(new uint[] { 1, 2, 3 }, values);
        Assert.DoesNotContain(r.Requests, q => q.Command == GvcpConst.ReadRegCmd && q.Addresses.Length > 1);
        Assert.Equal(3, r.Requests.Count(q => q.Command == GvcpConst.ReadRegCmd && q.Addresses[0] >= 0x4000));

        await dev.WriteRegsAsync(new[] { new KeyValuePair<uint, uint>(0x4000, 10), new KeyValuePair<uint, uint>(0x4004, 20) });
        Assert.DoesNotContain(r.Requests, q => q.Command == GvcpConst.WriteRegCmd && q.Addresses.Length > 1);
        Assert.Equal(10u, r.ReadU32(0x4000));
        Assert.Equal(20u, r.ReadU32(0x4004));
    }

    [Fact]
    public async Task WriteRegsBatchesByMaxWriteRegsPerPacket()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        const int n = 100;
        var writes = new KeyValuePair<uint, uint>[n];
        for (var i = 0; i < n; i++) writes[i] = new KeyValuePair<uint, uint>((uint)(0x5000 + i * 4), (uint)(0x20000 + i));

        await dev.WriteRegsAsync(writes);

        for (var i = 0; i < n; i++) Assert.Equal((uint)(0x20000 + i), r.ReadU32((uint)(0x5000 + i * 4)));
        var batches = r.Requests.Where(q => q.Command == GvcpConst.WriteRegCmd && q.Addresses.Length > 1).Select(q => q.Addresses.Length).ToList();
        Assert.Equal(new[] { 67, 33 }, batches);
        await dev.WriteRegsAsync(Array.Empty<KeyValuePair<uint, uint>>());
    }

    [Fact]
    public async Task WriteRegsReportsTheFailedEntryAcrossBatches()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        const int n = 100;
        var writes = new KeyValuePair<uint, uint>[n];
        for (var i = 0; i < n; i++) writes[i] = new KeyValuePair<uint, uint>((uint)(0x7000 + i * 4), (uint)(i + 1));
        // 두 번째 묶음(67..99)의 14 번째 항목이 거절된다 — 보고되는 번호는 묶음 안 번호가 아니라 전체 번호여야 한다.
        r.ErrorAddr = writes[80].Key;
        r.ErrorStatus = GvcpConst.StatusWriteProtect;

        var ex = await Assert.ThrowsAsync<GevStatusException>(() => dev.WriteRegsAsync(writes));

        Assert.Equal(GvcpConst.StatusWriteProtect, ex.Status);
        Assert.Equal(80, Assert.IsType<int>(ex.Data[GvcpChannel.FailedIndexKey]));
        for (var i = 0; i < 80; i++) Assert.Equal((uint)(i + 1), r.ReadU32(writes[i].Key));
        for (var i = 80; i < n; i++) Assert.Equal(0u, r.ReadU32(writes[i].Key));
        Assert.True(dev.IsOpen);
    }

    [Fact]
    public async Task WriteRegWithEmptyAckPayloadIsAccepted()
    {
        using var r = new GvcpTestResponder();
        r.IsAckEmptyForWrites = true;
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        await dev.WriteRegAsync(0x4000, 77);
        Assert.Equal(77u, r.ReadU32(0x4000));
    }

    [Fact]
    public async Task RegisterErrorsSurfaceAsStatusExceptions()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        r.ErrorAddr = 0x4000;
        r.ErrorStatus = GvcpConst.StatusInvalidAddress;

        var ex = await Assert.ThrowsAsync<GevStatusException>(() => dev.ReadRegAsync(0x4000));
        Assert.Equal(GvcpConst.StatusInvalidAddress, ex.Status);
        await Assert.ThrowsAsync<GevStatusException>(() => dev.WriteRegAsync(0x4000, 1));
        await Assert.ThrowsAsync<GevException>(() => dev.ReadRegAsync(0x4001));
        Assert.True(dev.IsOpen);
    }

    // ---------------------------------------------------------------- memory

    [Fact]
    public async Task ReadMemChunksAndTrimsUnalignedWindow()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        for (var i = 0; i < 0x800; i++) r.Memory[0x2000 + i] = (byte)(i * 7);
        var dst = new byte[1100];

        await dev.ReadMemAsync(0x2001, dst);

        for (var i = 0; i < dst.Length; i++) Assert.Equal((byte)((i + 1) * 7), dst[i]);
        var reads = r.Requests.Where(q => q.Command == GvcpConst.ReadMemCmd && q.Addresses[0] >= 0x2000).ToList();
        Assert.Equal(3, reads.Count);
        foreach (var q in reads)
        {
            GvcpPacket.ReadMemFields(q.Payload, out var addr, out var count);
            Assert.Equal(0u, addr & 3);
            Assert.Equal(0, count & 3);
            Assert.InRange(count, 4, GvcpConst.MaxMemPayload);
        }
        await dev.ReadMemAsync(0x2000, Memory<byte>.Empty);
    }

    [Fact]
    public async Task WriteMemUnalignedUsesReadModifyWriteAndChunksLargeBlocks()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        for (var i = 0; i < 16; i++) r.Memory[0x3000 + i] = 0xFF;

        await dev.WriteMemAsync(0x3002, new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 0xFF, 0xFF, 1, 2, 3, 0xFF, 0xFF, 0xFF }, r.Memory.AsSpan(0x3000, 8).ToArray());
        // 머리 워드(0x3000)와 꼬리 워드(0x3004) 를 4 바이트씩만 읽는다.
        var boundaryReads = r.Requests.Where(q => q.Command == GvcpConst.ReadMemCmd && q.Addresses[0] >= 0x3000).ToList();
        Assert.Equal(new uint[] { 0x3000, 0x3004 }, boundaryReads.Select(q => q.Addresses[0]).ToArray());
        foreach (var q in boundaryReads)
        {
            GvcpPacket.ReadMemFields(q.Payload, out _, out var count);
            Assert.Equal(4, count);
        }
        var write = Assert.Single(r.Requests, q => q.Command == GvcpConst.WriteMemCmd);
        Assert.Equal(0x3000u, write.Addresses[0]);
        Assert.Equal(8, write.Payload.Length - 4);

        var big = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
        await dev.WriteMemAsync(0x4000, big);
        Assert.Equal(big, r.Memory.AsSpan(0x4000, 1024).ToArray());
        var chunks = r.Requests.Where(q => q.Command == GvcpConst.WriteMemCmd && q.Addresses[0] >= 0x4000).Select(q => q.Payload.Length - 4).ToList();
        Assert.Equal(new[] { 512, 512 }, chunks);
    }

    [Fact]
    public async Task WriteMemUnalignedReadsOnlyTheBoundaryWords()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        for (var i = 0; i < 0x420; i++) r.Memory[0x8000 + i] = 0xEE;
        var data = Enumerable.Range(0, 1031).Select(i => (byte)(i + 1)).ToArray();
        var before = r.Requests.Count;

        // 0x8002..0x8409 → 정렬 창 0x8000..0x840C: 머리 2 바이트, 꼬리 3 바이트만 보존하면 된다.
        await dev.WriteMemAsync(0x8002, data);

        var reads = r.Requests.Skip(before).Where(q => q.Command == GvcpConst.ReadMemCmd).ToList();
        Assert.Equal(new uint[] { 0x8000, 0x8408 }, reads.Select(q => q.Addresses[0]).ToArray());
        foreach (var q in reads)
        {
            GvcpPacket.ReadMemFields(q.Payload, out _, out var count);
            Assert.Equal(4, count);
        }
        Assert.Equal(new byte[] { 0xEE, 0xEE }, r.Memory.AsSpan(0x8000, 2).ToArray());
        Assert.Equal(data, r.Memory.AsSpan(0x8002, 1031).ToArray());
        Assert.Equal(new byte[] { 0xEE, 0xEE, 0xEE }, r.Memory.AsSpan(0x8409, 3).ToArray());
        var chunks = r.Requests.Skip(before).Where(q => q.Command == GvcpConst.WriteMemCmd).Select(q => q.Payload.Length - 4).ToList();
        Assert.Equal(new[] { 512, 512, 12 }, chunks);

        // 머리와 꼬리가 같은 워드면 한 번만 읽는다.
        before = r.Requests.Count;
        await dev.WriteMemAsync(0x8001, new byte[] { 0xAA, 0xBB });
        Assert.Single(r.Requests.Skip(before), q => q.Command == GvcpConst.ReadMemCmd);
        Assert.Equal(new byte[] { 0xEE, 0xAA, 0xBB, 0x02 }, r.Memory.AsSpan(0x8000, 4).ToArray());

        // 머리만 어긋나고 끝은 정렬돼 있으면 꼬리는 읽지 않는다.
        before = r.Requests.Count;
        await dev.WriteMemAsync(0x8003, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 });
        var headOnly = Assert.Single(r.Requests.Skip(before), q => q.Command == GvcpConst.ReadMemCmd);
        Assert.Equal(0x8000u, headOnly.Addresses[0]);
        Assert.Equal(new byte[] { 0xEE, 0xAA, 0xBB, 0x11, 0x22, 0x33, 0x44, 0x55 }, r.Memory.AsSpan(0x8000, 8).ToArray());
    }

    [Fact]
    public async Task ReadStringDecodesNulTerminatedRegister()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        Encoding.UTF8.GetBytes("hello").CopyTo(r.Memory, 0x5000);

        Assert.Equal("hello", await dev.ReadStringAsync(0x5000, 32));
        Assert.Equal("Responder", await dev.ReadStringAsync(GvbsAddr.ModelName, GvbsAddr.ModelNameLen));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => dev.ReadStringAsync(0x5000, 0));
    }

    [Fact]
    public async Task ReadStringHonoursTheDeviceCharacterSetAndKeepsPadding()
    {
        using var r = new GvcpTestResponder();
        Encoding.UTF8.GetBytes("  padded  ").CopyTo(r.Memory, 0x5000);
        Encoding.UTF8.GetBytes("Kamera-ü").CopyTo(r.Memory, 0x5020);

        await using (var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt()))
        {
            Assert.Equal(GevDeviceInfo.CharacterSetUtf8, dev.Info.CharacterSet);
            Assert.Equal("  padded  ", await dev.ReadStringAsync(0x5000, 32));
            Assert.Equal("Kamera-ü", await dev.ReadStringAsync(0x5020, 32));
        }

        // ASCII 를 알린 장치: 0x80 이상 바이트는 대체 문자로 남고 예외는 나지 않는다.
        r.WriteU32(GvbsAddr.DeviceMode, 0x8000_0002);
        await using (var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt()))
        {
            Assert.Equal(GevDeviceInfo.CharacterSetAscii, dev.Info.CharacterSet);
            Assert.Equal("  padded  ", await dev.ReadStringAsync(0x5000, 32));
            Assert.Equal("Kamera-??", await dev.ReadStringAsync(0x5020, 32));
        }
    }

    [Fact]
    public async Task AnAckLongerThanRequestedIsAcceptedOnBothReadMemPaths()
    {
        // 길이를 워드 단위로 올려 붙여 답하는 장치가 있다. 그 응답이 부트스트랩 문자열 읽기에서는 통하고
        // 사용자 메모리 읽기에서는 치명적이면 같은 장치가 열리기는 해도 쓰이지는 못한다 — 두 경로가 같아야 한다.
        using var r = new GvcpTestResponder();
        for (var i = 0; i < 0x40; i++) r.Memory[0x6000 + i] = (byte)(0x40 + i);
        r.ReadMemLengthDelta = 8;

        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        Assert.Equal("GevSharp Test", dev.Info.Manufacturer);
        Assert.Equal("SN0001", dev.Info.SerialNumber);

        var dst = new byte[20];
        await dev.ReadMemAsync(0x6002, dst);
        for (var i = 0; i < dst.Length; i++) Assert.Equal((byte)(0x42 + i), dst[i]);

        // 짧은 응답은 여전히 오류다 — 그건 없는 데이터이고 조용히 0 으로 채우면 안 된다.
        r.ReadMemLengthDelta = -4;
        await Assert.ThrowsAsync<GevException>(() => dev.ReadMemAsync(0x6002, new byte[20]));
    }

    [Fact]
    public async Task MemoryRangeOverflowIsRejected()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        await Assert.ThrowsAsync<GevException>(() => dev.ReadMemAsync(0xFFFF_FFFC, new byte[8]));
        await Assert.ThrowsAsync<GevException>(() => dev.WriteMemAsync(0xFFFF_FFFE, new byte[4]));
    }

    // ---------------------------------------------------------------- IGevPort

    [Fact]
    public async Task PortMapsAlignedWordsToRegAccessAndTheRestToMemAccess()
    {
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        IGevPort port = dev;
        r.WriteU32(0x1000, 0x11223344);
        r.WriteU32(0x1004, 0x55667788);

        var word = new byte[4];
        await port.ReadAsync(0x1000, word);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(word));
        Assert.Equal(1, r.CountOfReg(GvcpConst.ReadRegCmd, 0x1000));
        Assert.Equal(0, r.CountOfReg(GvcpConst.ReadMemCmd, 0x1000));

        await port.WriteAsync(0x1004, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        Assert.Equal(0xDEADBEEFu, r.ReadU32(0x1004));
        Assert.Equal(1, r.CountOfReg(GvcpConst.WriteRegCmd, 0x1004));

        var two = new byte[8];
        await port.ReadAsync(0x1000, two);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0xDE, 0xAD, 0xBE, 0xEF }, two);
        Assert.Equal(1, r.CountOfReg(GvcpConst.ReadMemCmd, 0x1000));

        await port.WriteAsync(0x1001, new byte[] { 9, 9 });
        Assert.Equal(new byte[] { 0x11, 9, 9, 0x44 }, r.Memory.AsSpan(0x1000, 4).ToArray());

        // 끝이 32비트 공간을 넘는 접근은 잘못된 접근이라 예외다.
        await Assert.ThrowsAsync<GevException>(() => port.WriteAsync(0xFFFF_FFFEUL, new byte[4]).AsTask());
    }

    [Fact]
    public async Task ChunkedMemoryAccessNeverWrapsPastTheTopOfTheAddressSpace()
    {
        // 분할 읽기·쓰기가 청크마다 주소를 32비트로 다시 더하면, 꼭대기 근처 요청이 0 근처로 감겨
        // 서로 다른 영역을 한 버퍼에 이어 붙이고도 오류를 내지 않는다. 범위 검사와 넓은 커서로 그 자리를 막는다.
        using var r = new GvcpTestResponder();
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());

        // 끝이 32비트를 넘는 요청은 감기지 않고 거절된다(여러 청크로 쪼개지는 크기로 시험한다).
        var beforeWrap = r.Requests.Count;                        // 열기 시퀀스의 부트스트랩 읽기는 세지 않는다
        await Assert.ThrowsAsync<GevException>(() => dev.ReadMemAsync(0xFFFF_FF00, new byte[1024]));
        await Assert.ThrowsAsync<GevException>(() => dev.WriteMemAsync(0xFFFF_FF00, new byte[1024]));
        var afterWrap = r.Requests.Skip(beforeWrap).ToArray();
        Assert.Empty(afterWrap);                                   // 거절은 패킷을 내보내기 전에 난다 — 감긴 요청도, 첫 청크도 없다

        // 정상 범위의 여러 청크 요청은 주소가 단조 증가한다.
        var before = r.Requests.Count;
        await dev.ReadMemAsync(0x1000, new byte[1024]);
        var chunkAddrs = r.Requests.Skip(before)
            .Where(req => req.Command == GvcpConst.ReadMemCmd)
            .SelectMany(req => req.Addresses)
            .ToArray();
        Assert.Equal(new uint[] { 0x1000, 0x1200 }, chunkAddrs);
    }

    [Fact]
    public async Task WideGenApiAddressUsesItsLow32BitsBecauseGvcpCarriesNoMore()
    {
        // 벤더 XML 이 32비트를 넘는 주소를 리터럴로 적는다(실장치: 파일 접근 기준주소 0xFFFFD0000000).
        // 상위 비트는 GVCP 에 실을 수 없는 장식이고 실제 레지스터는 하위 32비트에 있다 — 경고를 남기고 그쪽을 읽는다.
        using var r = new GvcpTestResponder();
        r.WriteU32(0x2000, 0xABCDEF01);
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, FastOpt());
        IGevPort port = dev;

        var warnings = new List<string>();
        var prevSink = GevLog.Sink;
        var prevLevel = GevLog.MinLevel;
        GevLog.MinLevel = GevLogLevel.Warn;
        GevLog.Sink = (level, _, message, _) => { if (level == GevLogLevel.Warn) lock (warnings) warnings.Add(message); };
        try
        {
            var buf = new byte[4];
            await port.ReadAsync(0xFFFF_0000_2000UL, buf);
            Assert.Equal(new byte[] { 0xAB, 0xCD, 0xEF, 0x01 }, buf);

            // 같은 주소를 다시 읽어도 경고는 한 번만 — 노드마다 매번 읽는 자리라 로그가 넘치면 안 된다.
            await port.ReadAsync(0xFFFF_0000_2000UL, buf);
        }
        finally
        {
            GevLog.Sink = prevSink;
            GevLog.MinLevel = prevLevel;
        }

        var wide = warnings.Where(w => w.Contains("0xFFFF00002000")).ToArray();
        Assert.Single(wide);
        Assert.Contains("0x00002000", wide[0]);
    }
}
