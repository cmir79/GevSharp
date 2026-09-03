using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using GevSharp.Gvcp;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvcp;

/// <summary>유니캐스트 프로브(루프백 응답기)·응답 해석·중복 제거·인터페이스 선택·로컬 주소 결정을 확인한다.</summary>
public class GevDiscoveryTests
{
    // ---------------------------------------------------------------- probe

    [Fact]
    public async Task ProbeReturnsDeviceInfoFromLoopbackResponder()
    {
        using var r = new GvcpTestResponder();

        var info = await GevDiscovery.ProbeAsync(r.EndPoint, 1000, default);

        Assert.NotNull(info);
        Assert.Equal("Responder", info!.Model);
        Assert.Equal("GevSharp Test", info.Manufacturer);
        Assert.Equal(IPAddress.Loopback, info.Address);
        Assert.Equal(IPAddress.Loopback, info.InterfaceAddress);
        Assert.True(info.IsReachableDirectly);
        var req = Assert.Single(r.Requests);
        Assert.Equal(GvcpConst.DiscoveryCmd, req.Command);
        Assert.Equal(GvcpConst.FlagAckRequired, req.Flags);
        Assert.NotEqual(0, req.ReqId);
    }

    [Fact]
    public async Task ProbeReturnsNullWhenNothingAnswers()
    {
        using var r = new GvcpTestResponder();
        r.IsSilent = true;
        const int budgetMs = 100;   // 프로브 한 번의 예산 — ProbeAsync 는 재시도 없이 이만큼만 기다린다
        var sw = Stopwatch.StartNew();

        var info = await GevDiscovery.ProbeAsync(r.EndPoint, budgetMs, default);

        Assert.Null(info);
        // 아래 두 경계가 지키는 것은 "프로브가 자기 예산으로 포기한다" 이지 "100 ms 안에 끝난다" 가 아니다.
        // 소켓 생성·수신 스레드 기동·타이머·정리의 고정 비용은 굶주린 스케줄러에서 예산의 수십 배까지 늘어난다 —
        // 이 경로만 떼어 재보면 결함이 하나도 없는데도 프로브 한 번이 3.5 s 까지 걸리고, 그중 라이브러리가 기다린 몫은 예산뿐이다.
        //  - 아래 경계: 예산을 다 쓰기도 전에 포기하면 깨진다(기한 계산이 시작 시각·부호를 잘못 잡는 회귀).
        //    부하는 시간을 늘릴 뿐이라 이 경계는 흔들리지 않는다. Task.Delay 가 타이머 눈금만큼 일찍 깰 수 있어 20 ms 만 덜어 준다.
        //  - 위 경계: 포기 자체가 사라지거나 TimeoutMs 를 초로 읽는(100 ms → 100 s) 회귀만 겨냥한다.
        //    그보다 작은 예산 회귀 — 이를테면 채널 기본값(500 ms × 4 = 2 s) — 은 이 경계로 걸리지 않고,
        //    아래 Assert.Single 이 시계 없이 못 박는다(기본값이면 Retries 3 이라 네 번 보낸다). 응답기가 침묵하므로 PENDING_ACK 는 올 수 없다.
        Assert.True(sw.ElapsedMilliseconds >= budgetMs - 20, $"probe gave up after only {sw.ElapsedMilliseconds} ms of a {budgetMs} ms budget");
        Assert.True(sw.ElapsedMilliseconds < 15_000, $"probe took {sw.ElapsedMilliseconds} ms for a {budgetMs} ms budget");
        // 응답기는 다른 스레드에서 요청을 기록한다 — 굶주린 스케줄러에서는 프로브가 끝난 뒤에야 기록될 수 있어 따라잡기를 기다린다.
        // 기다리는 대상은 테스트 대역폭(응답기의 기록)이지 라이브러리의 동작이 아니다. 예산을 몇 번 쓰는지는 이 단정이 못 박는다 — 재시도 없음.
        // 따라잡기는 "1 개 이상" 에서 풀리므로, 세기 전에 잠깐 더 두어 늦게 도착한 두 번째 전송도 보이게 한다.
        await GvcpChannelTests.WaitUntilAsync(() => r.Requests.Count >= 1, timeoutMs: 10_000, what: "the responder logged the probe");
        await Task.Delay(50);
        Assert.Single(r.Requests);
    }

    [Fact]
    public async Task ProbeSkipsTruncatedDiscoveryAck()
    {
        using var r = new GvcpTestResponder();
        r.TruncateDiscoveryTo = 100;

        Assert.Null(await GevDiscovery.ProbeAsync(r.EndPoint, 500, default));
    }

    [Fact]
    public async Task ProbeOnClosedPortReturnsNull()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        Assert.Null(await GevDiscovery.ProbeAsync(new IPEndPoint(IPAddress.Loopback, port), 100, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GevDiscovery.ProbeAsync(IPAddress.Loopback, 0));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GevDiscovery.ProbeAsync((IPAddress)null!));
    }

    // ---------------------------------------------------------------- reply parsing

    private static byte[] DiscoveryAckPacket(ushort status, ushort command, byte[] payload)
    {
        var p = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(0), status);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), command);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), 1);
        payload.CopyTo(p, 8);
        return p;
    }

    [Fact]
    public void ParseDiscoveryReplyAcceptsFullAckAndRejectsTheRest()
    {
        var from = new IPEndPoint(IPAddress.Parse("192.168.1.100"), GvcpConst.Port);
        var iface = IPAddress.Parse("192.168.1.10");
        var bootstrap = GevDeviceInfoTests.BuildBootstrap();

        var ok = GevDiscovery.ParseDiscoveryReply(DiscoveryAckPacket(0, GvcpConst.DiscoveryAck, bootstrap), from, iface);
        Assert.NotNull(ok);
        Assert.Equal("Cam", ok!.Model);
        Assert.Equal(iface, ok.InterfaceAddress);

        var truncated = DiscoveryAckPacket(0, GvcpConst.DiscoveryAck, bootstrap.AsSpan(0, 200).ToArray());
        Assert.Null(GevDiscovery.ParseDiscoveryReply(truncated, from, iface));

        var error = DiscoveryAckPacket(GvcpConst.StatusNotImplemented, GvcpConst.DiscoveryAck, bootstrap);
        Assert.Null(GevDiscovery.ParseDiscoveryReply(error, from, iface));

        var wrongCommand = DiscoveryAckPacket(0, GvcpConst.ReadRegAck, bootstrap);
        Assert.Null(GevDiscovery.ParseDiscoveryReply(wrongCommand, from, iface));

        var declaresMoreThanCarried = DiscoveryAckPacket(0, GvcpConst.DiscoveryAck, bootstrap);
        Assert.Null(GevDiscovery.ParseDiscoveryReply(declaresMoreThanCarried.AsSpan(0, 100).ToArray(), from, iface));
        Assert.Null(GevDiscovery.ParseDiscoveryReply(new byte[3], from, iface));
    }

    // ---------------------------------------------------------------- dedupe

    private static GevDeviceInfo Info(string mac, string ip, string subnet, string iface)
        => new()
        {
            Mac = PhysicalAddress.Parse(mac),
            Address = IPAddress.Parse(ip),
            Subnet = IPAddress.Parse(subnet),
            Gateway = IPAddress.Any,
            InterfaceAddress = IPAddress.Parse(iface),
            SpecMajor = 2,
            SpecMinor = 0,
            DeviceMode = 0,
            SupportedIpCfg = 0,
            CurrentIpCfg = 0,
            Manufacturer = "m",
            Model = "d",
            DeviceVersion = "1",
            ManufacturerInfo = "",
            SerialNumber = mac,
            UserDefinedName = "",
        };

    [Fact]
    public void DedupePrefersSameSubnetInterfaceAndKeepsFirstSeenOrder()
    {
        var replies = new[]
        {
            Info("00-00-00-00-00-01", "192.168.1.100", "255.255.255.0", "10.0.0.5"),
            Info("00-00-00-00-00-02", "10.0.0.50", "255.0.0.0", "10.0.0.5"),
            Info("00-00-00-00-00-01", "192.168.1.100", "255.255.255.0", "192.168.1.10"),
            Info("00-00-00-00-00-02", "10.0.0.50", "255.0.0.0", "192.168.1.10"),
            Info("00-00-00-00-00-01", "192.168.1.100", "255.255.255.0", "172.16.0.1"),
        };

        var result = GevDiscovery.Dedupe(replies);

        Assert.Equal(2, result.Count);
        Assert.Equal(PhysicalAddress.Parse("00-00-00-00-00-01"), result[0].Mac);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), result[0].InterfaceAddress);
        Assert.Equal(PhysicalAddress.Parse("00-00-00-00-00-02"), result[1].Mac);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), result[1].InterfaceAddress);
        Assert.Empty(GevDiscovery.Dedupe(Array.Empty<GevDeviceInfo>()));
    }

    // ---------------------------------------------------------------- broadcast paths

    [Fact]
    public async Task DiscoverWithNoInterfacesReturnsEmptyImmediately()
    {
        // 상한을 창 길이 그 자체로 잡는다 — 쓸 인터페이스가 없으면 창을 열지 않고 곧바로 돌아와야 하므로,
        // 창을 그대로 기다리는 회귀는 windowMs 를 넘겨 반드시 깨진다. 창을 5 s 로 크게 두어 굶주린 스케줄러의
        // 고정 비용(실측 수백 ms)과 회귀(5 s+)가 겹치지 않게 한다.
        const int windowMs = 5000;
        var sw = Stopwatch.StartNew();
        var result = await GevDiscovery.DiscoverAsync(new GevDiscoveryOpt { Interfaces = Array.Empty<IPAddress>(), TimeoutMs = windowMs });
        Assert.Empty(result);
        Assert.True(sw.ElapsedMilliseconds < windowMs, $"discovery took {sw.ElapsedMilliseconds} ms although it had no interface to open a {windowMs} ms window on");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => GevDiscovery.DiscoverAsync(new GevDiscoveryOpt { TimeoutMs = 0 }));
    }

    /// <summary>루프백 인터페이스 하나, 브로드캐스트 대신 응답기 유니캐스트 — 소켓·반복·수신·병합 경로를 그대로 탄다.</summary>
    private static GevDiscoveryOpt LoopbackOpt(int timeoutMs, int repeat, params IPEndPoint[] targets) => new()
    {
        Interfaces = new[] { IPAddress.Loopback },
        LimitedBroadcast = false,
        DirectedBroadcast = false,
        UnicastTargets = targets,
        TimeoutMs = timeoutMs,
        Repeat = repeat,
    };

    [Fact]
    public async Task DiscoverCollectsRepliesFromEveryTargetAndDedupesByMac()
    {
        using var r1 = new GvcpTestResponder();
        using var r2 = new GvcpTestResponder();
        r2.WriteU32(GvbsAddr.MacLow, 0x2233_4466);
        // 창 1 s — 이 테스트가 보는 것은 창 길이가 아니라 "대상마다 Repeat 번 보내고 온 응답을 MAC 으로 합친다" 이다.
        // 창은 굶주린 스케줄러에서 응답기 스레드가 깨어 답할 시간까지 담을 만큼만 넉넉하면 된다(300 ms 로는 응답 하나를 놓쳤다).
        const int windowMs = 1000;
        var sw = Stopwatch.StartNew();

        var result = await GevDiscovery.DiscoverAsync(LoopbackOpt(windowMs, 2, r1.EndPoint, r2.EndPoint));

        // 상한은 창을 재려는 것이 아니라(과부하에서는 소켓·스레드 비용이 얹힌다) 창 계산이 통째로 어긋나는 회귀 —
        // 단위를 초로 잘못 읽어 1000 s 를 기다린다든가, 창이 끝나도 수신 태스크가 끝나지 않는다든가 — 를 겨냥한다.
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"discovery took {sw.ElapsedMilliseconds} ms for a {windowMs} ms window");
        // 응답기마다 Repeat 번 답했지만 MAC 으로 하나씩만 남는다.
        Assert.Equal(2, result.Count);
        Assert.Contains(result, d => d.Mac.Equals(PhysicalAddress.Parse("00-11-22-33-44-55")));
        Assert.Contains(result, d => d.Mac.Equals(PhysicalAddress.Parse("00-11-22-33-44-66")));
        Assert.All(result, d => Assert.Equal(IPAddress.Loopback, d.InterfaceAddress));
        Assert.All(result, d => Assert.Equal("Responder", d.Model));

        foreach (var r in new[] { r1, r2 })
        {
            // 응답기의 기록은 다른 스레드에서 채워진다 — 창이 닫힌 순간 아직 기록 전일 수 있어 따라잡기를 기다린다.
            // 두 번째 전송이 아예 사라지는 회귀에서는 기다려도 오지 않아 그대로 실패한다.
            await GvcpChannelTests.WaitUntilAsync(() => r.CountOf(GvcpConst.DiscoveryCmd) >= 2, timeoutMs: 10_000, what: "both DISCOVERY_CMDs logged");
            await Task.Delay(50);   // 따라잡기가 "2 개 이상" 에서 풀리므로, 세 번째 전송이 늦게 오는 회귀도 보이도록 잠깐 더 둔다
            var cmds = r.Requests.Where(q => q.Command == GvcpConst.DiscoveryCmd).ToList();
            Assert.Equal(2, cmds.Count);
            Assert.All(cmds, q => Assert.Equal((byte)(GvcpConst.FlagAckRequired | GvcpConst.FlagAllowBroadcastAck), q.Flags));
            Assert.Single(cmds.Select(q => q.ReqId).Distinct());
            Assert.All(cmds, q => Assert.NotEqual(0, q.ReqId));
        }
    }

    [Fact]
    public async Task DiscoverSkipsTruncatedRepliesInsteadOfCreatingGhosts()
    {
        using var r = new GvcpTestResponder();
        r.TruncateDiscoveryTo = 100;

        var result = await GevDiscovery.DiscoverAsync(LoopbackOpt(200, 1, r.EndPoint));

        Assert.Empty(result);
        // 응답기의 기록 따라잡기를 기다린 뒤에 센다(기록은 다른 스레드가 채운다). 전송이 사라지면 기다려도 0 이라 그대로 실패한다.
        await GvcpChannelTests.WaitUntilAsync(() => r.CountOf(GvcpConst.DiscoveryCmd) >= 1, timeoutMs: 10_000, what: "the DISCOVERY_CMD was logged");
        await Task.Delay(50);   // "1 개 이상" 에서 풀리므로, 두 번째 전송이 늦게 오는 회귀도 보이도록 잠깐 더 둔다
        Assert.Equal(1, r.CountOf(GvcpConst.DiscoveryCmd));
    }

    [Fact]
    public async Task DiscoverWithoutAnyTargetReturnsEmptyImmediately()
    {
        // 보낼 대상이 하나도 없으면 창을 열지 않고 곧바로 돌아온다 — 상한을 창 길이로 잡아,
        // 창을 그대로 기다리는 회귀만 깨지고 굶주린 스케줄러에는 흔들리지 않게 한다.
        const int windowMs = 5000;
        var sw = Stopwatch.StartNew();
        var result = await GevDiscovery.DiscoverAsync(LoopbackOpt(windowMs, 2));
        Assert.Empty(result);
        Assert.True(sw.ElapsedMilliseconds < windowMs, $"discovery took {sw.ElapsedMilliseconds} ms although it had no target to open a {windowMs} ms window for");
    }

    [Fact]
    public async Task DiscoverOnLoopbackBroadcastCompletesWithinTheWindow()
    {
        // 루프백으로 나가는 실제 브로드캐스트는 응답할 상대가 없다 — 보내기가 막히거나 실패해도 창이 끝나면 돌아오는지만 본다.
        // 상한은 창(150 ms)을 재는 것이 아니라 "창이 끝나도 돌아오지 않는다" 를 겨냥한다: 보내기 실패가 반복 루프를 멈춰 세우거나
        // 수신 태스크를 영원히 기다리면 깨진다. 굶주린 스케줄러의 고정 비용보다는 한참 위에 둔다.
        var sw = Stopwatch.StartNew();
        await GevDiscovery.DiscoverAsync(new GevDiscoveryOpt { Interfaces = new[] { IPAddress.Loopback }, TimeoutMs = 150, Repeat = 2 });
        Assert.True(sw.ElapsedMilliseconds < 10_000, $"discovery took {sw.ElapsedMilliseconds} ms for a 150 ms window");
    }

    [Fact]
    public async Task DiscoverHonoursCancellation()
    {
        using var cts = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GevDiscovery.DiscoverAsync(new GevDiscoveryOpt { Interfaces = new[] { IPAddress.Loopback }, TimeoutMs = 5000 }, cts.Token));
    }

    [Fact]
    public async Task ForceIpValidatesInputsAndNeedsAnInterface()
    {
        var mac = PhysicalAddress.Parse("00-11-22-33-44-55");
        var ip = IPAddress.Parse("192.168.1.20");
        var mask = IPAddress.Parse("255.255.255.0");
        var gw = IPAddress.Parse("192.168.1.1");

        await Assert.ThrowsAsync<ArgumentNullException>(() => GevDiscovery.ForceIpAsync(null!, ip, mask, gw));
        await Assert.ThrowsAsync<GevException>(() => GevDiscovery.ForceIpAsync(mac, ip, mask, gw, new GevDiscoveryOpt { Interfaces = Array.Empty<IPAddress>() }));
        await Assert.ThrowsAsync<GevException>(() => GevDiscovery.ForceIpAsync(mac, IPAddress.IPv6Loopback, mask, gw, new GevDiscoveryOpt { Interfaces = new[] { IPAddress.Loopback } }));

        // 루프백 인터페이스로는 브로드캐스트가 막힐 수 있다 — 보내졌거나 "보낼 길이 없다"로 끝나야 하고, 어느 쪽이든 멈추지 않는다.
        try
        {
            await GevDiscovery.ForceIpAsync(mac, ip, mask, gw, new GevDiscoveryOpt { Interfaces = new[] { IPAddress.Loopback } });
        }
        catch (GevException ex)
        {
            Assert.Contains("FORCEIP", ex.Message);
        }
    }

    [Fact]
    public async Task ForceIpReachesAUnicastTargetWithTheRequestedAddresses()
    {
        using var r = new GvcpTestResponder();
        var mac = PhysicalAddress.Parse("00-11-22-33-44-55");
        var ip = IPAddress.Parse("192.168.1.20");
        var mask = IPAddress.Parse("255.255.255.0");
        var gw = IPAddress.Parse("192.168.1.1");

        await GevDiscovery.ForceIpAsync(mac, ip, mask, gw, LoopbackOpt(100, 1, r.EndPoint));

        await GvcpChannelTests.WaitUntilAsync(() => r.CountOf(GvcpConst.ForceIpCmd) == 1, what: "FORCEIP received");
        var req = Assert.Single(r.Requests, q => q.Command == GvcpConst.ForceIpCmd);
        Assert.Equal((byte)(GvcpConst.FlagAckRequired | GvcpConst.FlagAllowBroadcastAck), req.Flags);
        Assert.Equal(GvcpPacket.ForceIpPayloadSize, req.Payload.Length);
        GvcpPacket.ReadForceIp(req.Payload, out var gotMac, out var gotIp, out var gotMask, out var gotGw);
        Assert.Equal(mac, gotMac);
        Assert.Equal(ip, gotIp);
        Assert.Equal(mask, gotMask);
        Assert.Equal(gw, gotGw);
    }

    // ---------------------------------------------------------------- GevNet

    [Fact]
    public void SubnetArithmetic()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.255"), GevNet.DirectedBroadcast(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0")));
        Assert.Equal(IPAddress.Parse("10.255.255.255"), GevNet.DirectedBroadcast(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("255.0.0.0")));
        Assert.True(GevNet.IsSameSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.200"), IPAddress.Parse("255.255.255.0")));
        Assert.False(GevNet.IsSameSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.2.10"), IPAddress.Parse("255.255.255.0")));
        Assert.False(GevNet.IsSameSubnet(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.11"), IPAddress.Any));
        Assert.False(GevNet.IsSameSubnet(IPAddress.IPv6Loopback, IPAddress.Loopback, IPAddress.Parse("255.0.0.0")));
        Assert.Equal(0xC0A80101u, GevNet.Ipv4ToUInt32(IPAddress.Parse("192.168.1.1")));
        Assert.Equal(IPAddress.Parse("192.168.1.1"), GevNet.Ipv4FromUInt32(0xC0A80101u));
    }

    [Fact]
    public void ResolveLocalAddressFindsLoopbackAndRejectsIpv6()
    {
        Assert.Equal(IPAddress.Loopback, GevNet.ResolveLocalAddress(IPAddress.Loopback));
        Assert.Throws<GevException>(() => GevNet.ResolveLocalAddress(IPAddress.IPv6Loopback));
        Assert.Throws<ArgumentNullException>(() => GevNet.ResolveLocalAddress(null!));

        var ifaces = GevNet.GetIpv4Interfaces(includeLoopback: true);
        Assert.Contains(ifaces, i => i.IsLoopback && i.Address.Equals(IPAddress.Loopback));
        Assert.DoesNotContain(GevNet.GetIpv4Interfaces(includeLoopback: false), i => i.IsLoopback);
    }
}
