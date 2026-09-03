using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using GevSharp.Gvcp;
using GevSharp.Sim;
using GevSharp.Xml;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Integration;

/// <summary>
/// 시뮬레이터 대향 장치 세션 2부: 장치 쪽 하트비트 만료로 CCP 가 풀렸을 때, 열기 도중의 취소, 동시 레지스터 접근의 직렬화, XML 디스크 캐시.
/// 표준 포트(3956)를 쓰는 공개 API 경로는 다른 컬렉션과 겹치면 안 되므로 <see cref="StandardPortTests"/> 에 따로 있다.
/// </summary>
public class DeviceControlTests
{
    // ---------------------------------------------------------------- control lost by the device

    [Fact]
    public async Task Heartbeat_WhenTheDeviceDropsCcp_RaisesControlLost_AndDisposeSkipsTheRelease()
    {
        // 하트비트 주기를 장치 타임아웃보다 길게 둔다 — 장치가 먼저 CCP 를 비우고, 다음 하트비트가 0 을 읽는다.
        await using var rig = await SimRig.StartAsync(
            sim: o => o.HeartbeatTimeoutMs = 200,
            device: o => { o.HeartbeatTimeoutMs = 200; o.HeartbeatPeriodMs = 600; });
        Assert.Equal(600, rig.Device.HeartbeatPeriodMs);
        var lost = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Device.ControlLost += (_, ex) => lost.TrySetResult(ex);

        await SimRig.WaitUntilAsync(() => rig.Sim.ControlOwner is null, 10_000, "the simulator to expire the heartbeat");
        Assert.Equal(1, rig.Sim.HeartbeatTimeouts);
        Assert.Equal(0u, rig.Sim.Registers.ReadU32(GvbsAddr.Ccp));

        var done = await Task.WhenAny(lost.Task, Task.Delay(3000));
        Assert.True(ReferenceEquals(done, lost.Task), "ControlLost did not fire after the device released CCP on its own");
        var ex = Assert.IsType<GevControlLostException>(await lost.Task);
        Assert.Contains("released", ex.Message);
        Assert.False(rig.Device.IsOpen);
        await Assert.ThrowsAsync<GevControlLostException>(() => rig.Device.WriteRegAsync(SimFeatureAddr.Width, 128));
        await Assert.ThrowsAsync<GevControlLostException>(() => rig.Device.OpenStreamAsync());

        // 제어권이 없으니 닫기는 CCP = 0 을 쓰지 않는다.
        var writesBefore = rig.Sim.WriteRegCount;
        await rig.Device.DisposeAsync();
        Assert.Equal(writesBefore, rig.Sim.WriteRegCount);
        Assert.Null(rig.Sim.ControlOwner);
        Assert.True(rig.Device.Gvcp.IsDisposed);
    }

    // ---------------------------------------------------------------- cancelled open

    [Fact]
    public async Task Open_WithAnAlreadyCancelledToken_ThrowsAndLeavesNoControlBehind()
    {
        using var sim = SimRig.StartSim();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GevDevice.OpenAsync(sim.GvcpEndPoint, SimRig.DefaultDeviceOpt(), cts.Token));

        Assert.Null(sim.ControlOwner);
        Assert.Equal(0, sim.WriteRegCount);
        Assert.Equal(0u, sim.Registers.ReadU32(GvbsAddr.Ccp));
    }

    [Fact]
    public async Task Open_CancelledWhileTheCcpWriteIsInFlight_StillReleasesControl()
    {
        // 장치는 WRITEREG 마다 PENDING_ACK 를 보내고 1.5 s 뒤에 ACK 한다. CCP 를 잡는 순간(시뮬레이터 이벤트) 토큰을 취소하면
        // 호스트는 ACK 를 못 본 채 열기를 포기한다 — 장치는 이미 CCP 를 적용했으므로 실패한 열기가 놓아 줘야 한다(design-requirements R21).
        // 장치 쪽 하트비트 만료는 30 s 로 밀어 둔다(취소된 열기는 하트비트 타임아웃을 쓰지 못하므로 이 초기값이 그대로다) —
        // 아래에서 제어권이 비면 그것은 명시적 해제이지 만료가 아니어야 한다. 해제 쓰기도 PENDING_ACK 1.5 s 를 타고
        // 굶주린 러너에서는 더 밀리므로, 만료는 그 합보다 한참 뒤에 있어야 이 구분이 유지된다.
        var simOpt = SimRig.DefaultSimOpt();
        simOpt.SupportPendingAck = true;
        simOpt.PendingAckDelayMs = 1500;
        simOpt.HeartbeatTimeoutMs = 30_000;
        using var sim = SimRig.StartSim(simOpt);
        using var cts = new CancellationTokenSource();
        // 취소는 미리 띄워 둔 전용 스레드가 한다. 시뮬레이터 스레드에서 곧바로 Cancel 하면 라이브러리 계속이 그 스레드에서 돌아
        // 시뮬레이터가 막히고, 그때 새로 만든 스레드나 풀 스레드는 굶주린 러너에서 잡히기까지 수백 ms 가 걸려 ACK 에 진다.
        // 여기서는 이미 대기 중인 스레드를 깨우기만 하므로 ACK(1.5 s 뒤)보다 확실히 먼저 닿는다.
        var ccpTaken = new ManualResetEventSlim(false);
        var canceller = new Thread(() =>
        {
            try
            {
                if (ccpTaken.Wait(30_000)) cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 테스트가 먼저 끝나 토큰 원본이 사라진 경우 — 취소할 대상도 없다.
            }
        })
        { IsBackground = true, Name = "cancel-open" };
        canceller.Start();
        sim.ControlOwnerChanged += owner => { if (owner is not null) ccpTaken.Set(); };

        var opt = SimRig.DefaultDeviceOpt();
        opt.GvcpRetries = 0;
        // 판정 대상은 취소이지 타임아웃이 아니다 — 취소가 닿기 전에 쓰기가 제풀에 타임아웃으로 끝나면 엉뚱한 예외가 나온다.
        opt.GvcpTimeoutMs = 5000;
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GevDevice.OpenAsync(sim.GvcpEndPoint, opt, cts.Token));
        // 취소가 제대로 먹으면 열기는 CCP 쓰기가 나간 직후에 끝난다. 그 "직후" 를 재지는 않는다 —
        // 굶주린 스케줄러에서는 취소가 닿는 것부터가 늦어 3.5 s 까지 늘어난 적이 있다(실측).
        // 이 상한이 잡는 것은 "열기가 아예 돌아오지 않는다" 뿐이다. 취소를 무시하고 ACK 를 기다리는 회귀는 시뮬레이터가
        // 1.5 s 뒤에 답하므로 시간으로는 걸리지 않고, 그 몫은 위의 ThrowsAny<OperationCanceledException> 가 맡는다.
        Assert.True(sw.ElapsedMilliseconds < 8000, $"the cancelled open took {sw.ElapsedMilliseconds} ms; cancellation must not wait for the channel's timeout budget");

        // 해제 쓰기도 PENDING_ACK 1.5 s 를 탄다 — 과부하에서 밀리는 몫까지 얹어 넉넉히 기다린다.
        // 넉넉해도 뜻은 그대로다: 해제를 아예 시도하지 않는 회귀에서는 아무리 기다려도 제어권이 비지 않는다.
        const int releaseWaitMs = 8000;
        var released = false;
        var release = System.Diagnostics.Stopwatch.StartNew();
        while (release.ElapsedMilliseconds < releaseWaitMs)
        {
            if (sim.ControlOwner is null) { released = true; break; }
            await Task.Delay(10);
        }
        Assert.True(released,
            "OpenAsync was cancelled while its CCP write was in flight; the device applied the write, so the failed open must release it (R21). "
            + "GevDevice marks the CCP write as sent before it goes out (GevDevice.cs, InitAsync) and DisposeAsync writes CCP = 0 on that flag alone, "
            + $"not only after the ACK. {releaseWaitMs} ms after the failed open CCP still reads 0x{sim.Registers.ReadU32(GvbsAddr.Ccp):X} with owner {sim.ControlOwner}.");
        Assert.Equal(0, sim.HeartbeatTimeouts);   // 명시적 해제였어야 한다 — 만료가 아니라
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task ConcurrentRegisterAccess_IsSerialized_WithoutCrossTalk()
    {
        // 하트비트가 5 ms 마다 요청 사이에 끼어든다.
        await using var rig = await SimRig.StartAsync(device: o => o.HeartbeatPeriodMs = 5);
        const int workers = 8;
        const int rounds = 30;
        const uint scratchBase = SimFeatureAddr.FeatureBase + 0x100;   // 표에 없는 피처 페이지 주소 — 평범한 RAM
        var heartbeatsBefore = rig.Sim.HeartbeatObserved;

        var tasks = Enumerable.Range(0, workers).Select(async w =>
        {
            var addr = scratchBase + 4u * (uint)w;
            var mem = new byte[4];
            for (var i = 0; i < rounds; i++)
            {
                var value = (uint)(w << 24) | (uint)(i * 7919 + 1);
                await rig.Device.WriteRegAsync(addr, value);
                var back = await rig.Device.ReadRegAsync(addr);
                Assert.True(value == back, $"worker {w} round {i}: wrote 0x{value:X8} to 0x{addr:X8} but read back 0x{back:X8}");
                await rig.Device.ReadMemAsync(addr, mem);
                var viaMem = BinaryPrimitives.ReadUInt32BigEndian(mem);
                Assert.True(value == viaMem, $"worker {w} round {i}: READMEM of 0x{addr:X8} returned 0x{viaMem:X8}, expected 0x{value:X8}");
            }
            return (uint)(w << 24) | (uint)((rounds - 1) * 7919 + 1);
        }).ToArray();
        var finals = await Task.WhenAll(tasks);

        for (var w = 0; w < workers; w++)
            Assert.Equal(finals[w], rig.Sim.Registers.ReadU32(scratchBase + 4u * (uint)w));
        Assert.Equal(0, rig.Device.Gvcp.StaleAckCount);
        Assert.Equal(0, rig.Device.Gvcp.ForeignPacketCount);
        Assert.Equal(0, rig.Device.Gvcp.MalformedPacketCount);
        Assert.Equal(0, rig.Sim.MalformedCount);
        // 하트비트는 사용자 트래픽 곁에서 계속 돈다 — 과부하 러너에서는 굶은 스레드 풀이 워커 창 안에서 타이머에 차례를 안 줄 수 있으므로
        // "창 안에서" 가 아니라 "트래픽을 거치고도 계속 온다" 를 본다(짧게 기다린다).
        await SimRig.WaitUntilAsync(() => rig.Sim.HeartbeatObserved > heartbeatsBefore, 10_000, "a heartbeat CCP read to reach the device after the concurrent traffic");
        Assert.True(rig.Device.IsOpen);
        Assert.Equal(rig.Device.Gvcp.LocalEndPoint, rig.Sim.ControlOwner);
    }

    // ---------------------------------------------------------------- xml disk cache

    [Fact]
    public async Task XmlCache_IsWrittenOnTheFirstLoad_AndServesTheNextSessionWithoutReadingTheXmlRegion()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "GevSharp.Tests", "xmlcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var sim = SimRig.StartSim();
            var opt = SimRig.DefaultDeviceOpt();
            opt.XmlCacheDir = cacheDir;

            string firstXml;
            await using (var first = await GevDevice.OpenAsync(sim.GvcpEndPoint, opt))
            {
                var doc = await first.GetXmlAsync();
                firstXml = doc.Xml;
                Assert.Equal("SimCamera.xml", doc.FileName);
            }
            Assert.True(sim.ReadMemCount > 10, $"the first load must read the XML region from the device (READMEM count {sim.ReadMemCount})");

            var expectedPath = Path.Combine(cacheDir, GevXmlLoader.CacheFileName("GevSharp", "SimCamera", "1.0", "SimCamera.xml"));
            var files = Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir) : Array.Empty<string>();
            Assert.True(File.Exists(expectedPath), $"cache file '{expectedPath}' missing; the directory holds: {string.Join(", ", files)}");
            Assert.Single(files);
            var bytes = File.ReadAllBytes(expectedPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "cache file must not carry a BOM");
            Assert.Equal(firstXml, Encoding.UTF8.GetString(bytes));

            // 두 번째 세션: 식별 문자열과 URL 만 읽고 XML 영역은 읽지 않는다.
            await using var second = await GevDevice.OpenAsync(sim.GvcpEndPoint, opt);
            var readsBefore = sim.ReadMemCount;
            var again = await second.GetXmlAsync();
            var readsDuring = sim.ReadMemCount - readsBefore;
            Assert.Equal(firstXml, again.Xml);
            Assert.Equal("SimCamera.xml", again.FileName);
            var xmlRegionReads = (sim.Registers.XmlLength + GvcpConst.MaxMemPayload - 1) / GvcpConst.MaxMemPayload;
            Assert.True(readsDuring < 10,
                $"a cache hit should only read the identification strings and the URL, but {readsDuring} READMEMs were issued (the XML region alone takes {xmlRegionReads})");
        }
        finally
        {
            try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
