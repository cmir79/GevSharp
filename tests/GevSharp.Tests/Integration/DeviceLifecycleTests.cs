using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Xml.Linq;
using GevSharp.Gvcp;
using GevSharp.Sim;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Integration;

/// <summary>
/// 시뮬레이터 대향 장치 생명주기: 프로브 → 열기(CCP·하트비트 타임아웃·Info 재읽기) → 두 번째 세션의 권한 → 하트비트 유지·상실 → 닫기(CCP 해제) → XML.
/// 시뮬레이터의 레지스터 이미지와 카운터로 장치 쪽에서 실제로 일어난 일을 확인한다.
/// </summary>
public class DeviceLifecycleTests
{
    // ---------------------------------------------------------------- probe

    [Fact]
    public async Task Probe_ReturnsSimulatorIdentity()
    {
        var opt = SimRig.DefaultSimOpt();
        opt.SerialNumber = "SIM4242";
        opt.UserDefinedName = "bench";
        using var sim = SimRig.StartSim(opt);

        var info = await GevDiscovery.ProbeAsync(sim.GvcpEndPoint, 1000, CancellationToken.None);

        Assert.NotNull(info);
        Assert.Equal("GevSharp", info!.Manufacturer);
        Assert.Equal("SimCamera", info.Model);
        Assert.Equal("1.0", info.DeviceVersion);
        Assert.Equal("in-process simulator", info.ManufacturerInfo);
        Assert.Equal("SIM4242", info.SerialNumber);
        Assert.Equal("bench", info.UserDefinedName);
        Assert.Equal(new PhysicalAddress(sim.Mac), info.Mac);
        Assert.Equal(IPAddress.Loopback, info.Address);
        Assert.Equal(IPAddress.Parse("255.0.0.0"), info.Subnet);
        Assert.Equal(IPAddress.Any, info.Gateway);
        Assert.Equal(IPAddress.Loopback, info.InterfaceAddress);
        Assert.Equal(2, info.SpecMajor);
        Assert.Equal(0, info.SpecMinor);
        Assert.True(info.IsBigEndianDevice);
        Assert.Equal(GevDeviceInfo.CharacterSetUtf8, info.CharacterSet);
        Assert.True(info.IsReachableDirectly);
        Assert.Equal(1, sim.DiscoveryCount);
        Assert.Null(sim.ControlOwner);   // 프로브는 제어권을 건드리지 않는다
    }

    [Fact]
    public async Task Probe_NoDevice_ReturnsNull()
    {
        // 아무도 듣지 않는 임시 포트 — 바인드해서 번호를 얻고 바로 닫는다.
        int port;
        using (var probe = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        const int budgetMs = 200;
        var sw = Stopwatch.StartNew();
        var info = await GevDiscovery.ProbeAsync(new IPEndPoint(IPAddress.Loopback, port), budgetMs, CancellationToken.None);

        Assert.Null(info);
        // 아래 경계가 이 시험의 몫이다 — 닫힌 포트로 보내면 ICMP 도달 불가가 곧바로 돌아오고, 수신 루프는 그것을 무시하고
        // 계속 받아야 한다(GvcpChannel 의 ConnectionReset 처리). 그 처리가 사라져 요청이 곧바로 접히면 예산을 다 쓰기 전에
        // 끝나 여기서 걸린다. 이 경로는 듣는 상대가 없는 포트에서만 생긴다. 부하는 시간을 늘릴 뿐이라 이 경계는 흔들리지 않는다.
        // 위 경계는 "포기 자체가 사라지거나 TimeoutMs 를 초로 읽는다" 만 겨냥한다. 200 ms 를 재지는 않는다 —
        // 소켓·수신 스레드·정리의 고정 비용이 굶주린 스케줄러에서 그보다 훨씬 커진다(이 경로만 재서 3.5 s).
        // 예산을 통째로 잘못 잡는 회귀(채널 기본값 2 s)는 시간이 아니라 요청 수로 잡아야 하는데 아무도 듣지 않는 포트에서는 셀 수 없다 —
        // 그 몫은 응답기를 두고 재시도 수를 못 박는 GevDiscoveryTests.ProbeReturnsNullWhenNothingAnswers 가 맡는다.
        Assert.True(sw.ElapsedMilliseconds >= budgetMs - 20, $"probe gave up after only {sw.ElapsedMilliseconds} ms of a {budgetMs} ms budget");
        Assert.True(sw.ElapsedMilliseconds < 15_000, $"probe took {sw.ElapsedMilliseconds} ms for a {budgetMs} ms timeout");
    }

    // ---------------------------------------------------------------- open

    [Fact]
    public async Task Open_TakesControl_WritesHeartbeatTimeout_AndRereadsInfo()
    {
        using var sim = SimRig.StartSim();
        var probed = await GevDiscovery.ProbeAsync(sim.GvcpEndPoint, 1000, CancellationToken.None);
        Assert.NotNull(probed);
        Assert.Equal("", probed!.UserDefinedName);

        // 프로브 뒤에 바뀐 값이 Info 에 보이면 열 때 부트스트랩을 다시 읽은 것이다.
        sim.Registers.WriteString(GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen, "renamed");

        var opt = SimRig.DefaultDeviceOpt();
        opt.HeartbeatTimeoutMs = 1500;
        await using var dev = await GevDevice.OpenAsync(sim.GvcpEndPoint, opt);

        Assert.True(dev.IsOpen);
        Assert.Equal(GevAccessMode.Control, dev.AccessMode);
        Assert.Equal(IPAddress.Loopback, dev.Address);
        Assert.Equal(IPAddress.Loopback, dev.LocalAddress);
        Assert.Equal("renamed", dev.Info.UserDefinedName);
        Assert.Equal(probed.Mac, dev.Info.Mac);
        Assert.Equal(probed.SerialNumber, dev.Info.SerialNumber);
        Assert.Equal(probed.Model, dev.Info.Model);

        Assert.Equal(GvbsAddr.CcpControl, sim.Registers.ReadU32(GvbsAddr.Ccp));
        Assert.Equal(dev.Gvcp.LocalEndPoint, sim.ControlOwner);
        Assert.Equal((uint)dev.Gvcp.LocalEndPoint.Port, sim.Registers.ReadU32(GvbsAddr.PrimaryAppPort));
        Assert.Equal(0x7F00_0001u, sim.Registers.ReadU32(GvbsAddr.PrimaryAppIp));

        Assert.Equal(1500u, sim.Registers.ReadU32(GvbsAddr.HeartbeatTimeout));
        Assert.Equal(1500, dev.DeviceHeartbeatTimeoutMs);
        Assert.Equal(500, dev.HeartbeatPeriodMs);
        Assert.Equal(sim.Registers.ReadU32(GvbsAddr.GvcpCapability), dev.GvcpCapability);
        Assert.NotEqual(0u, dev.GvcpCapability & GvbsAddr.GvcpCapPacketResend);
        Assert.Equal(1_000_000_000ul, dev.TimestampTickFrequency);
    }

    [Fact]
    public async Task Open_Exclusive_SetsExclusiveAndControlBits()
    {
        using var sim = SimRig.StartSim();
        var opt = SimRig.DefaultDeviceOpt();
        opt.AccessMode = GevAccessMode.Exclusive;
        opt.AllowSwitchover = true;
        await using var dev = await GevDevice.OpenAsync(sim.GvcpEndPoint, opt);

        Assert.Equal(GvbsAddr.CcpControl | GvbsAddr.CcpExclusive | GvbsAddr.CcpSwitchoverEnable, sim.Registers.ReadU32(GvbsAddr.Ccp));
        Assert.Equal(dev.Gvcp.LocalEndPoint, sim.ControlOwner);
    }

    // ---------------------------------------------------------------- second sessions

    [Fact]
    public async Task SecondSession_ReadOnly_CanReadButWritesAreAccessDenied()
    {
        await using var rig = await SimRig.StartAsync();
        var ro = SimRig.DefaultDeviceOpt();
        ro.AccessMode = GevAccessMode.ReadOnly;
        await using var reader = await GevDevice.OpenAsync(rig.EndPoint, ro);

        Assert.True(reader.IsOpen);
        Assert.Equal(GevAccessMode.ReadOnly, reader.AccessMode);
        Assert.Equal(0, reader.HeartbeatPeriodMs);
        Assert.Equal(0x0002_0000u, await reader.ReadRegAsync(GvbsAddr.Version));
        Assert.Equal(128u, await reader.ReadRegAsync(SimFeatureAddr.Width));
        Assert.Equal("GevSharp", await reader.ReadStringAsync(GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen));

        var ex = await Assert.ThrowsAsync<GevStatusException>(() => reader.WriteRegAsync(SimFeatureAddr.Width, 256));
        Assert.Equal(GvcpConst.StatusAccessDenied, ex.Status);
        Assert.Equal(128u, rig.Sim.Registers.ReadU32(SimFeatureAddr.Width));

        var exMem = await Assert.ThrowsAsync<GevStatusException>(() => reader.WriteMemAsync(SimFeatureAddr.Width, new byte[] { 0, 0, 1, 0 }));
        Assert.Equal(GvcpConst.StatusAccessDenied, exMem.Status);

        // 읽기 전용 세션은 스트림 채널을 설정할 수 없다.
        await Assert.ThrowsAsync<GevControlLostException>(() => reader.OpenStreamAsync());

        // 제어권은 그대로 첫 세션에 있고, 그쪽 쓰기는 통한다.
        Assert.Equal(rig.Device.Gvcp.LocalEndPoint, rig.Sim.ControlOwner);
        Assert.True(rig.Device.IsOpen);
        await rig.Device.WriteRegAsync(SimFeatureAddr.Width, 256);
        Assert.Equal(256u, rig.Sim.Registers.ReadU32(SimFeatureAddr.Width));
        Assert.Equal(256u, await reader.ReadRegAsync(SimFeatureAddr.Width));
    }

    [Fact]
    public async Task SecondSession_Control_WhileFirstHoldsCcp_ThrowsControlLost()
    {
        await using var rig = await SimRig.StartAsync();
        var writesBefore = rig.Sim.WriteRegCount;

        var ex = await Assert.ThrowsAsync<GevControlLostException>(() => GevDevice.OpenAsync(rig.EndPoint, SimRig.DefaultDeviceOpt()));
        Assert.Contains("another application", ex.Message);

        var exclusive = SimRig.DefaultDeviceOpt();
        exclusive.AccessMode = GevAccessMode.Exclusive;
        await Assert.ThrowsAsync<GevControlLostException>(() => GevDevice.OpenAsync(rig.EndPoint, exclusive));

        // 거절된 세션은 CCP 시도 외에 아무것도 쓰지 않았고(하트비트 타임아웃 미변경), 첫 세션은 멀쩡하다.
        Assert.Equal(writesBefore + 2, rig.Sim.WriteRegCount);
        Assert.Equal(10_000u, rig.Sim.Registers.ReadU32(GvbsAddr.HeartbeatTimeout));
        Assert.Equal(GvbsAddr.CcpControl, rig.Sim.Registers.ReadU32(GvbsAddr.Ccp));
        Assert.Equal(rig.Device.Gvcp.LocalEndPoint, rig.Sim.ControlOwner);
        Assert.True(rig.Device.IsOpen);
        Assert.Equal(0x0002_0000u, await rig.Device.ReadRegAsync(GvbsAddr.Version));
    }

    // ---------------------------------------------------------------- heartbeat

    [Fact]
    public async Task Heartbeat_KeepsControlForThreeDeviceTimeouts()
    {
        // 장치가 만료를 재는 기준은 "CCP 읽기 요청이 도착한 간격" 이다. 그 간격은 주기 + (응답을 기다리는 시간)이고,
        // 뒤엣것은 굶주린 러너에서 GVCP 타임아웃까지 늘어난다 — 그러니 GVCP 타임아웃을 짧게(재시도는 넉넉히) 두어
        // 요청이 최소한 그 간격으로는 계속 나가게 하고, 장치 타임아웃은 그보다 한참 크게 잡는다.
        // 주기 150 ms + GVCP 예산 500 ms = 요청 간격 상한 650 ms 대 장치 타임아웃 3 s. 앞서 주기 250 ms 에 GVCP 3 s 로는
        // 응답 한 번이 늦자 요청 간격이 타임아웃을 넘겨 제어권을 잃었다. 호스트 쪽 상실 판정은 하트비트 세 번 연속 실패라,
        // 재시도 다섯 번(= 한 번의 하트비트에 3 s)까지 두어도 이 시험 안에서는 닿지 않는다.
        // 장치 타임아웃 5 s: 하트비트 루프는 스레드 풀 작업이라, CPU 가 수십 배로 굶주리면 요청 간격이 아니라 루프 자체가
        // 몇 초씩 멈춰 선다(3 s 로는 러너가 20 배로 굶은 판에서 실제로 제어권을 잃었다). 이 시험만은 그 정지 시간보다
        // 장치 타임아웃이 커야 성립하므로, 대기 시간(타임아웃의 3 배)을 치르고 여유를 산다.
        const int deviceTimeoutMs = 5000;
        const int periodMs = 150;
        await using var rig = await SimRig.StartAsync(
            sim: o => o.HeartbeatTimeoutMs = deviceTimeoutMs,
            device: o =>
            {
                o.HeartbeatTimeoutMs = deviceTimeoutMs;
                o.HeartbeatPeriodMs = periodMs;
                o.GvcpTimeoutMs = 500;
                o.GvcpRetries = 5;
            });
        Assert.Equal(deviceTimeoutMs, rig.Device.DeviceHeartbeatTimeoutMs);
        Assert.Equal(periodMs, rig.Device.HeartbeatPeriodMs);
        var observedAtOpen = rig.Sim.HeartbeatObserved;

        await Task.Delay(3 * deviceTimeoutMs);   // 장치 타임아웃의 3 배 — 부하는 이 대기를 늘릴 뿐이라 시험이 약해지지 않는다

        Assert.True(rig.Device.IsOpen);
        Assert.Equal(0, rig.Sim.HeartbeatTimeouts);
        Assert.Equal(rig.Device.Gvcp.LocalEndPoint, rig.Sim.ControlOwner);
        Assert.Equal(GvbsAddr.CcpControl, rig.Sim.Registers.ReadU32(GvbsAddr.Ccp));
        // 하트비트가 실제로 오갔는지는 개수로 본다. "정해진 시간 안에 몇 번" 으로 재면 굶주린 러너를 재는 것이 되므로
        // "결국 이만큼은 온다" 로 기다린다 — 하트비트가 끊기는 회귀에서는 개수가 늘지 않아 여기서 시간 초과로 걸린다.
        await SimRig.WaitUntilAsync(() => rig.Sim.HeartbeatObserved - observedAtOpen >= 5, 30_000, "five heartbeat CCP reads");
    }

    [Fact]
    public async Task Heartbeat_UnreachableDevice_RaisesControlLostAndClosesSession()
    {
        var simOpt = SimRig.DefaultSimOpt();
        simOpt.HeartbeatTimeoutMs = 300;
        var sim = SimRig.StartSim(simOpt);
        GevDevice? dev = null;
        try
        {
            // GVCP 타임아웃 200 ms / 재시도 0: 상실 판정이 빨리 나면서도, 과부하 러너에서 열기의 왕복이 타임아웃에 걸리지 않을 만큼은 둔다.
            var opt = SimRig.DefaultDeviceOpt();
            opt.HeartbeatTimeoutMs = 300;
            opt.HeartbeatPeriodMs = 100;   // 상실 판정을 빨리 보려고 주기를 직접 지정한다(기본 리그 값은 여유 쪽으로 크다)
            opt.GvcpTimeoutMs = 200;
            opt.GvcpRetries = 0;
            dev = await GevDevice.OpenAsync(sim.GvcpEndPoint, opt);
            Assert.Equal(100, dev.HeartbeatPeriodMs);

            var lost = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            dev.ControlLost += (d, ex) => lost.TrySetResult(ex);

            // GVCP 서버를 통째로 내린다 — 하트비트가 닿을 곳이 없다(시뮬레이터에는 GVCP 만 멈추는 옵션이 없다).
            var sw = Stopwatch.StartNew();
            sim.Dispose();

            // 상실 판정은 실패 3 회 × (주기 100 ms + 타임아웃 200 ms) ≈ 900 ms 에 나온다. 그 900 ms 를 상한으로 잡지는 않는다 —
            // 과부하에서는 하트비트 태스크가 밀려 몇 배로 늘어나고, 그것은 라이브러리가 늦은 것이 아니다.
            // 이 대기가 지키는 것은 "판정이 나오기는 한다" 이며, 판정이 사라지는 회귀는 여기서 그대로 걸린다.
            const int lostWaitMs = 10_000;
            var done = await Task.WhenAny(lost.Task, Task.Delay(lostWaitMs));
            sw.Stop();
            Assert.True(ReferenceEquals(done, lost.Task), $"ControlLost did not fire within {lostWaitMs} ms after the device became unreachable (waited {sw.ElapsedMilliseconds} ms)");

            Assert.IsType<GevTimeoutException>(await lost.Task);
            Assert.False(dev.IsOpen);
            await Assert.ThrowsAsync<GevControlLostException>(() => dev.ReadRegAsync(GvbsAddr.Version));

            // 제어권이 이미 없으므로 닫기는 CCP 해제를 시도하지 않고 바로 끝난다.
            var disposeSw = Stopwatch.StartNew();
            var d = dev;
            dev = null;
            await d.DisposeAsync();
            // 상한은 "닫기가 끝나기는 한다" 를 지킨다 — 이미 죽은 채널에 해제 쓰기를 걸고 그 예산이나 수신 스레드 조인에
            // 매달려 돌아오지 않으면 깨진다. 정상 경로(제어권이 없으니 해제 시도 자체가 없다)는 왕복이 하나도 없어 즉시 끝난다.
            Assert.True(disposeSw.ElapsedMilliseconds < 8000, $"DisposeAsync took {disposeSw.ElapsedMilliseconds} ms after control was lost");
            Assert.True(d.Gvcp.IsDisposed);
        }
        finally
        {
            if (dev is not null) await dev.DisposeAsync();
            sim.Dispose();
        }
    }

    // ---------------------------------------------------------------- dispose

    [Fact]
    public async Task Dispose_ReleasesCcp_AndLetsTheNextSessionTakeControl()
    {
        using var sim = SimRig.StartSim();
        var owners = new List<IPEndPoint?>();
        sim.ControlOwnerChanged += o => { lock (owners) owners.Add(o); };

        // 명시적으로 닫는 것이 시험 대상이지만, 단정이 실패해도 세션이 남지 않게 한다(두 번 닫아도 안전한 것도 아래에서 확인한다).
        await using var dev = await GevDevice.OpenAsync(sim.GvcpEndPoint, SimRig.DefaultDeviceOpt());
        var first = dev.Gvcp.LocalEndPoint;
        Assert.Equal(first, sim.ControlOwner);

        await dev.DisposeAsync();

        Assert.Null(sim.ControlOwner);
        Assert.Equal(0u, sim.Registers.ReadU32(GvbsAddr.Ccp));
        Assert.Equal(0u, sim.Registers.ReadU32(GvbsAddr.PrimaryAppPort));
        Assert.Equal(0u, sim.Registers.ReadU32(GvbsAddr.PrimaryAppIp));
        Assert.Equal(0, sim.HeartbeatTimeouts);   // 타임아웃이 아니라 명시적 해제였다
        Assert.False(dev.IsOpen);
        Assert.True(dev.Gvcp.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => dev.ReadRegAsync(GvbsAddr.Version));
        lock (owners) Assert.Equal(new IPEndPoint?[] { first, null }, owners);

        // 두 번 닫아도 안전하고, 다음 세션은 기다림 없이 제어권을 잡는다.
        await dev.DisposeAsync();
        await using var next = await GevDevice.OpenAsync(sim.GvcpEndPoint, SimRig.DefaultDeviceOpt());
        Assert.Equal(next.Gvcp.LocalEndPoint, sim.ControlOwner);
        Assert.NotEqual(first, next.Gvcp.LocalEndPoint);
    }

    // ---------------------------------------------------------------- register / memory access

    [Fact]
    public async Task RegisterAndMemoryAccess_RoundTripThroughTheSimulator()
    {
        await using var rig = await SimRig.StartAsync();
        var dev = rig.Device;

        Assert.Equal("GevSharp", await dev.ReadStringAsync(GvbsAddr.ManufacturerName, GvbsAddr.ManufacturerNameLen));
        Assert.Equal("SIM0001", await dev.ReadStringAsync(GvbsAddr.SerialNumber, GvbsAddr.SerialNumberLen));

        // 정렬되지 않은 길이의 WRITEMEM — 꼬리 워드는 읽기-수정-쓰기로 보존된다.
        await dev.WriteMemAsync(GvbsAddr.UserDefinedName, Encoding.UTF8.GetBytes("cam-7\0"));
        Assert.Equal("cam-7", rig.Sim.Registers.ReadString(GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen));
        Assert.Equal("cam-7", await dev.ReadStringAsync(GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen));

        // 묶음 읽기(concatenation 능력 비트가 서 있다).
        var regs = await dev.ReadRegsAsync(new uint[] { SimFeatureAddr.Width, SimFeatureAddr.Height, SimFeatureAddr.PixelFormat, SimFeatureAddr.WidthMax });
        Assert.Equal(new uint[] { 128, 64, 0x0108_0001, 4096 }, regs);

        // 쓰기 보호 레지스터 → WRITE_PROTECT.
        var ro = await Assert.ThrowsAsync<GevStatusException>(() => dev.WriteRegAsync(GvbsAddr.Version, 1));
        Assert.Equal(GvcpConst.StatusWriteProtect, ro.Status);

        // 묶음 쓰기에서 두 번째 항목이 거절되면 FailedIndex = 1 이고 그 앞 항목만 적용된다.
        var batch = await Assert.ThrowsAsync<GevStatusException>(() => dev.WriteRegsAsync(new[]
        {
            new KeyValuePair<uint, uint>(SimFeatureAddr.Width, 200),
            new KeyValuePair<uint, uint>(GvbsAddr.Version, 1),
            new KeyValuePair<uint, uint>(SimFeatureAddr.Height, 50),
        }));
        Assert.Equal(GvcpConst.StatusWriteProtect, batch.Status);
        Assert.Equal(1, batch.FailedIndex);
        Assert.Equal(200u, rig.Sim.Registers.ReadU32(SimFeatureAddr.Width));
        Assert.Equal(64u, rig.Sim.Registers.ReadU32(SimFeatureAddr.Height));

        // 매핑되지 않은 주소 → INVALID_ADDRESS.
        var bad = await Assert.ThrowsAsync<GevStatusException>(() => dev.ReadRegAsync(0x0002_0000));
        Assert.Equal(GvcpConst.StatusInvalidAddress, bad.Status);

        // 자기 소거 명령 레지스터: 쓰고 읽으면 0.
        await dev.WriteRegAsync(SimFeatureAddr.UserSetLoad, 1);
        Assert.Equal(0u, await dev.ReadRegAsync(SimFeatureAddr.UserSetLoad));
        Assert.Equal(128u, await dev.ReadRegAsync(SimFeatureAddr.Width));   // UserSetLoad 가 기본값으로 되돌렸다
    }

    // ---------------------------------------------------------------- xml

    [Fact]
    public async Task GetXml_ReturnsSimulatorXml_AndCachesPerSession()
    {
        await using var rig = await SimRig.StartAsync();
        var resource = SimDevice.DefaultGenApiXml;
        Assert.Equal(resource, rig.Sim.GenApiXml);

        var readMemBefore = rig.Sim.ReadMemCount;
        var doc = await rig.Device.GetXmlAsync();
        var readMemAfterFirst = rig.Sim.ReadMemCount;
        Assert.True(readMemAfterFirst > readMemBefore, "the XML must come from device memory (READMEM)");

        Assert.Equal("SimCamera.xml", doc.FileName);
        Assert.StartsWith("Local:SimCamera.xml;", doc.Url, StringComparison.Ordinal);
        Assert.Null(doc.SchemaVersion);

        // 본문은 내장 자원과 같아야 한다. 로더는 앞뒤 NUL·공백·BOM 을 걷어내므로(설계) 꼬리의 줄바꿈만 다를 수 있다 — 그 밖의 차이는 결함이다.
        Assert.Equal(resource.Trim('\0', ' ', '\t', '\r', '\n', '\uFEFF'), doc.Xml);
        Assert.StartsWith(doc.Xml, resource, StringComparison.Ordinal);
        Assert.True(resource.Substring(doc.Xml.Length).Trim('\r', '\n', ' ', '\t').Length == 0,
            "GetXmlAsync differs from the embedded XML by more than trailing whitespace");
        Assert.Equal("RegisterDescription", XDocument.Parse(doc.Xml).Root!.Name.LocalName);

        // 두 번째 호출은 같은 인스턴스, 장치 접근 없음.
        var again = await rig.Device.GetXmlAsync();
        Assert.Same(doc, again);
        Assert.Equal(readMemAfterFirst, rig.Sim.ReadMemCount);

        // 바이트 수준: 장치 메모리의 XML 영역은 내장 자원의 UTF-8 바이트와 정확히 같다(전송 경로에 손실이 없다).
        var expectedBytes = Encoding.UTF8.GetBytes(resource);
        var actualBytes = new byte[expectedBytes.Length];
        await rig.Device.ReadMemAsync(SimRegisterMap.XmlRegionBase, actualBytes);
        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public async Task GetXml_WorksFromAReadOnlySession()
    {
        using var sim = SimRig.StartSim();
        var ro = SimRig.DefaultDeviceOpt();
        ro.AccessMode = GevAccessMode.ReadOnly;
        await using var reader = await GevDevice.OpenAsync(sim.GvcpEndPoint, ro);

        var doc = await reader.GetXmlAsync();

        Assert.Equal("RegisterDescription", XDocument.Parse(doc.Xml).Root!.Name.LocalName);
        Assert.Equal(0, sim.WriteRegCount);
        Assert.Equal(0, sim.WriteMemCount);
    }
}
