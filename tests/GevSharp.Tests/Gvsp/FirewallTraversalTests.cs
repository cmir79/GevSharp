using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;
using GevSharp.Gvsp;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 채널을 연 뒤 장치의 스트림 송신 포트(SCSP)로 한 바이트를 보내는 동작. 상태 기반 호스트 방화벽은 우리가 먼저 보낸 적 없는
/// UDP 를 버리므로, 이 한 번의 송신이 실장치에서 "패킷이 한 개도 안 들어오는" 것과 "풀레이트로 들어오는" 것을 가른다.
/// </summary>
public class FirewallTraversalTests
{
    private static uint Scp(uint offset) => GvbsAddr.StreamChannel(0, offset);

    /// <summary>SCSP 포트에서 듣는 소켓을 세우고 스트림을 시작한 뒤, 그 소켓이 받은 첫 데이터그램을 돌려준다.</summary>
    private static async Task<(byte[]? Datagram, IPEndPoint? From, int ScspPort)> StartAndListenAsync(
        Action<GevStreamOpt>? configure = null, uint? scspOverride = null)
    {
        using var deviceSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        deviceSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        deviceSocket.ReceiveTimeout = 2000;
        var scspPort = ((IPEndPoint)deviceSocket.LocalEndPoint!).Port;

        var regs = new FakeRegPort();
        regs.Set(Scp(GvbsAddr.ScspOffset), scspOverride ?? (uint)scspPort);

        var opt = StreamRig.DefaultOpt();
        configure?.Invoke(opt);

        await using var stream = new GevStream(regs, new TestResendPort(new GvspTestSender()), IPAddress.Loopback, opt,
            streamChannel: 0, deviceAddress: IPAddress.Loopback);
        await stream.StartAsync();

        var buffer = new byte[64];
        try
        {
            var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
            var n = deviceSocket.ReceiveFrom(buffer, ref from);
            return (buffer.AsSpan(0, n).ToArray(), (IPEndPoint)from, scspPort);
        }
        catch (SocketException)
        {
            return (null, null, scspPort);
        }
        finally
        {
            await stream.StopAsync();
        }
    }

    [Fact]
    public async Task OpeningTheChannelSendsOneDatagramToTheDeviceStreamSourcePort()
    {
        var (datagram, from, _) = await StartAndListenAsync();

        Assert.NotNull(datagram);
        Assert.Single(datagram!);
        // 스트림 소켓(= SCDA:SCP 로 장치에 알린 그 소켓)에서 나가야 방화벽 매핑이 들어올 흐름과 맞는다.
        Assert.NotNull(from);
        Assert.Equal(IPAddress.Loopback, from!.Address);
    }

    [Fact]
    public async Task MappingIsRefreshedWhileNoPacketsArrive()
    {
        // 상태 기반 방화벽의 매핑은 유휴로 두면 만료된다 — 트리거 간격이 벌어지거나 획득을 멈춘 채 스트림을 열어 두면
        // 다음 프레임이 통째로 사라진다. 인바운드가 끊긴 동안 유지용 한 바이트가 계속 나가야 한다.
        using var deviceSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        deviceSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var scspPort = ((IPEndPoint)deviceSocket.LocalEndPoint!).Port;

        var regs = new FakeRegPort();
        regs.Set(Scp(GvbsAddr.ScspOffset), (uint)scspPort);

        var opt = StreamRig.DefaultOpt();
        opt.FirewallTraversalIntervalMs = 60;          // 짧게 잡아 테스트가 오래 걸리지 않게
        await using var stream = new GevStream(regs, new TestResendPort(new GvspTestSender()), IPAddress.Loopback, opt,
            streamChannel: 0, deviceAddress: IPAddress.Loopback);
        await stream.StartAsync();
        try
        {
            // 프레임을 하나도 보내지 않는다 — 수신기는 계속 타임아웃하며 조용한 구간을 본다.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (stream.Stats.FirewallKeepAlives < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.True(stream.Stats.FirewallKeepAlives >= 2,
                $"the stream sent {stream.Stats.FirewallKeepAlives} keep-alive datagram(s) while nothing arrived for seconds; "
                + "a stateful firewall would have dropped the mapping and the next frame with it");

            // 최초 통과 한 개 + 유지용 — 장치 쪽 소켓이 실제로 받는다.
            var buffer = new byte[64];
            deviceSocket.ReceiveTimeout = 2000;
            var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
            Assert.Equal(1, deviceSocket.ReceiveFrom(buffer, ref from));
            Assert.Equal(1, deviceSocket.ReceiveFrom(buffer, ref from));
        }
        finally
        {
            await stream.StopAsync();
        }
    }

    [Fact]
    public async Task KeepAliveCanBeTurnedOffWithoutTurningOffTheInitialTraversal()
    {
        using var deviceSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        deviceSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var scspPort = ((IPEndPoint)deviceSocket.LocalEndPoint!).Port;

        var regs = new FakeRegPort();
        regs.Set(Scp(GvbsAddr.ScspOffset), (uint)scspPort);

        var opt = StreamRig.DefaultOpt();
        opt.FirewallTraversalIntervalMs = 0;           // 유지용만 끈다
        await using var stream = new GevStream(regs, new TestResendPort(new GvspTestSender()), IPAddress.Loopback, opt,
            streamChannel: 0, deviceAddress: IPAddress.Loopback);
        await stream.StartAsync();
        try
        {
            await Task.Delay(300);
            Assert.Equal(0, stream.Stats.FirewallKeepAlives);

            var buffer = new byte[64];
            deviceSocket.ReceiveTimeout = 1000;
            var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
            Assert.Equal(1, deviceSocket.ReceiveFrom(buffer, ref from));    // 최초 통과는 그대로 나갔다
            Assert.Throws<SocketException>(() => deviceSocket.ReceiveFrom(buffer, ref from));
        }
        finally
        {
            await stream.StopAsync();
        }
    }

    [Fact]
    public async Task FirewallTraversalCanBeTurnedOff()
    {
        var (datagram, _, _) = await StartAndListenAsync(o => o.FirewallTraversal = false);

        Assert.Null(datagram);
    }

    [Fact]
    public async Task DeviceThatReportsNoStreamSourcePortIsPunchedAtTheHostPortInstead()
    {
        // SCSP = 0 이어도 뚫기를 건너뛰면 안 된다 — 포트까지 따지는 방화벽 뒤에서는 한 패킷도 오지 않는다.
        // 실측한 그런 장치는 우리가 준 호스트 포트 번호를 그대로 자기 송신 포트로 썼으므로 그 번호로 뚫는다.
        // 같은 주소에서는 한 포트를 둘이 쓸 수 없으니 장치 역은 다른 루프백 주소에 같은 번호로 세운다.
        var deviceAddress = IPAddress.Parse("127.0.0.2");
        for (var attempt = 0; ; attempt++)
        {
            using var deviceSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            deviceSocket.Bind(new IPEndPoint(deviceAddress, 0));
            deviceSocket.ReceiveTimeout = 2000;
            var port = ((IPEndPoint)deviceSocket.LocalEndPoint!).Port;

            var regs = new FakeRegPort();
            regs.Set(Scp(GvbsAddr.ScspOffset), 0);

            var opt = StreamRig.DefaultOpt();
            opt.LocalPort = port;

            await using var stream = new GevStream(regs, new TestResendPort(new GvspTestSender()), IPAddress.Loopback, opt,
                streamChannel: 0, deviceAddress: deviceAddress);
            try
            {
                await stream.StartAsync();
            }
            catch (SocketException) when (attempt < 4)
            {
                // 그 번호를 이 주소에서 이미 누가 쓰고 있었다 — 다른 번호로 다시 잡는다.
                continue;
            }

            var buffer = new byte[64];
            var from = (EndPoint)new IPEndPoint(IPAddress.Any, 0);
            var n = deviceSocket.ReceiveFrom(buffer, ref from);
            await stream.StopAsync();

            Assert.Equal(1, n);
            Assert.Equal(port, ((IPEndPoint)from).Port);
            return;
        }
    }

    [Fact]
    public async Task TraversalHappensBeforeThePacketSizeProbeSoTheTestPacketCanArrive()
    {
        // Auto 협상은 장치가 보낸 테스트 패킷이 도착해야 성립한다 — 방화벽을 먼저 뚫지 않으면 그 패킷도 막힌다.
        // 여기서는 순서만 본다: SCSP 읽기가 파이어테스트 SCPS 쓰기보다 앞서야 한다.
        using var deviceSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        deviceSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var scspPort = ((IPEndPoint)deviceSocket.LocalEndPoint!).Port;

        var order = new List<string>();
        var regs = new OrderedRegPort(order);
        regs.Set(Scp(GvbsAddr.ScspOffset), (uint)scspPort);

        var opt = StreamRig.DefaultOpt();
        opt.PacketSizeMode = PacketSizeMode.Auto;
        await using var stream = new GevStream(regs, new TestResendPort(new GvspTestSender()), IPAddress.Loopback, opt,
            streamChannel: 0, deviceAddress: IPAddress.Loopback);
        await stream.StartAsync();
        await stream.StopAsync();

        var scspRead = order.IndexOf("read:SCSP");
        var fireTest = order.IndexOf("write:SCPS-firetest");
        Assert.True(scspRead >= 0, "the stream never read SCSP: " + string.Join(", ", order));
        Assert.True(fireTest >= 0, "the stream never fired a test packet: " + string.Join(", ", order));
        Assert.True(scspRead < fireTest, $"SCSP must be read before the fire test, got {string.Join(", ", order)}");
    }

    /// <summary>SCSP 읽기와 파이어테스트 쓰기의 순서만 기록하는 포트.</summary>
    private sealed class OrderedRegPort : IGevPort
    {
        private readonly FakeRegPort _inner = new();
        private readonly List<string> _order;

        public OrderedRegPort(List<string> order) => _order = order;

        public void Set(uint addr, uint value) => _inner.Set(addr, value);

        public ValueTask ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct = default)
        {
            if (address == Scp(GvbsAddr.ScspOffset)) lock (_order) _order.Add("read:SCSP");
            return _inner.ReadAsync(address, buffer, ct);
        }

        public ValueTask WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (address == Scp(GvbsAddr.ScpsOffset) && data.Length == 4
                && (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.Span) & GvbsAddr.ScpsFireTest) != 0)
            {
                lock (_order) _order.Add("write:SCPS-firetest");
            }
            return _inner.WriteAsync(address, data, ct);
        }
    }
}
