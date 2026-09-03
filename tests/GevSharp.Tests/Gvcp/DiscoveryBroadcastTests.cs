using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvcp;

/// <summary>
/// 탐색의 브로드캐스트 절반 — 인터페이스를 전부 열거하는지, 인터페이스마다 제한·지향 브로드캐스트를 둘 다 만드는지,
/// 그리고 그것이 실제로 소켓 밖으로 나가는지. 다른 탐색 테스트는 응답기 유니캐스트로 소켓·반복·병합 경로만 타므로
/// 브로드캐스트를 꺼도 전부 통과한다 — 여기가 그 구멍을 막는다.
/// </summary>
public class DiscoveryBroadcastTests
{
    private static GevNet.IfInfo Iface(string address, string? mask, bool loopback = false) => new()
    {
        Name = "test",
        Address = IPAddress.Parse(address),
        Mask = mask is null ? null : IPAddress.Parse(mask),
        IsLoopback = loopback,
    };

    // ---------------------------------------------------------------- 보낼 곳 계산

    [Fact]
    public void BothBroadcastsAreSent_LimitedFirst()
    {
        var targets = GevDiscovery.BuildTargets(Iface("192.168.10.5", "255.255.255.0"), new GevDiscoveryOpt { Port = 3956 });

        Assert.Equal(2, targets.Count);
        Assert.Equal(new IPEndPoint(IPAddress.Broadcast, 3956), targets[0]);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.10.255"), 3956), targets[1]);
    }

    [Theory]
    [InlineData("192.168.10.5", "255.255.255.0", "192.168.10.255")]
    [InlineData("10.1.2.3", "255.255.0.0", "10.1.255.255")]
    [InlineData("172.16.4.9", "255.255.255.240", "172.16.4.15")]
    [InlineData("127.0.0.1", "255.0.0.0", "127.255.255.255")]
    public void DirectedBroadcastComesFromTheMask(string address, string mask, string expected)
    {
        var targets = GevDiscovery.BuildTargets(Iface(address, mask), new GevDiscoveryOpt { LimitedBroadcast = false, Port = 3956 });
        Assert.Equal(new IPEndPoint(IPAddress.Parse(expected), 3956), Assert.Single(targets));
    }

    [Fact]
    public void MaskUnknown_SkipsTheDirectedBroadcastButKeepsTheLimitedOne()
    {
        // 마스크를 못 읽는 인터페이스가 있다 — 그것 때문에 그 인터페이스가 통째로 조용해지면 안 된다.
        var targets = GevDiscovery.BuildTargets(Iface("192.168.10.5", null), new GevDiscoveryOpt { Port = 3956 });
        Assert.Equal(new IPEndPoint(IPAddress.Broadcast, 3956), Assert.Single(targets));
    }

    [Fact]
    public void SlashZeroMask_DoesNotSendToTheSamePlaceTwice()
    {
        // 마스크가 /0 이면 지향 브로드캐스트가 255.255.255.255 라 제한 브로드캐스트와 같은 주소가 된다.
        var targets = GevDiscovery.BuildTargets(Iface("192.168.10.5", "0.0.0.0"), new GevDiscoveryOpt { Port = 3956 });
        Assert.Equal(new IPEndPoint(IPAddress.Broadcast, 3956), Assert.Single(targets));
    }

    [Fact]
    public void UnicastTargetsAreAppendedToTheBroadcasts_NotInsteadOfThem()
    {
        var extra = new IPEndPoint(IPAddress.Parse("192.168.10.77"), 3956);
        var targets = GevDiscovery.BuildTargets(
            Iface("192.168.10.5", "255.255.255.0"),
            new GevDiscoveryOpt { Port = 3956, UnicastTargets = new[] { extra } });

        Assert.Equal(3, targets.Count);
        Assert.Equal(extra, targets[2]);
    }

    [Fact]
    public void BothBroadcastsDisabledAndNoUnicast_LeavesNothingToSend()
    {
        var targets = GevDiscovery.BuildTargets(
            Iface("192.168.10.5", "255.255.255.0"),
            new GevDiscoveryOpt { LimitedBroadcast = false, DirectedBroadcast = false });
        Assert.Empty(targets);
    }

    // ---------------------------------------------------------------- 인터페이스 열거

    [Fact]
    public void EveryUpIpv4InterfaceIsEnumerated_AndLoopbackIsOptIn()
    {
        var withLoopback = GevNet.GetIpv4Interfaces(includeLoopback: true);
        var withoutLoopback = GevNet.GetIpv4Interfaces(includeLoopback: false);

        Assert.NotEmpty(withLoopback);
        Assert.All(withLoopback, i => Assert.Equal(AddressFamily.InterNetwork, i.Address.AddressFamily));
        Assert.Contains(withLoopback, i => i.IsLoopback);
        Assert.DoesNotContain(withoutLoopback, i => i.IsLoopback);
        // 루프백을 뺀 것이 전부다 — 다른 인터페이스가 함께 사라지면 카메라 전용 NIC 이 탐색에서 빠진다.
        Assert.Equal(withLoopback.Count(i => !i.IsLoopback), withoutLoopback.Count);
        // 마스크를 아는 인터페이스는 지향 브로드캐스트를 만들 수 있어야 한다.
        Assert.All(withLoopback.Where(i => i.Mask is not null), i => Assert.NotNull(i.DirectedBroadcast));
    }

    // ---------------------------------------------------------------- 실제 전송

    [Fact]
    public async Task DiscoverySendsToBothBroadcastAddresses_OverTheLoopbackInterface()
    {
        // 계산한 대상이 정말 소켓 밖으로 나가는지 — 0.0.0.0 에 묶은 청취자는 자기 포트로 온 브로드캐스트를 받는다.
        // 루프백에 묶인 소켓에서 보내므로 이 트래픽은 루프백 밖으로 나가지 않는다.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        listener.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var seen = new List<byte[]>();
        var reader = Task.Run(() =>
        {
            var buf = new byte[512];
            listener.ReceiveTimeout = 2000;
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                int n;
                try { n = listener.ReceiveFrom(buf, ref from); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                lock (seen) seen.Add(buf.AsSpan(0, n).ToArray());
            }
        });

        await GevDiscovery.DiscoverAsync(new GevDiscoveryOpt
        {
            Interfaces = new[] { IPAddress.Loopback },
            Port = port,
            TimeoutMs = 400,
            Repeat = 1,
        });

        listener.Dispose();
        await reader;

        List<byte[]> packets;
        lock (seen) packets = seen.ToList();
        // 제한 브로드캐스트 하나 + 루프백의 지향 브로드캐스트(127.255.255.255) 하나.
        Assert.Equal(2, packets.Count);
        Assert.All(packets, p => AssertIsDiscoveryCommand(p));
        // 같은 창의 두 전송은 같은 req_id 를 쓴다 — 응답을 한 창의 것으로 묶기 위해서다.
        Assert.Equal(ReqIdOf(packets[0]), ReqIdOf(packets[1]));
    }

    private static ushort ReqIdOf(byte[] packet) => (ushort)((packet[6] << 8) | packet[7]);

    private static void AssertIsDiscoveryCommand(byte[] packet)
    {
        Assert.Equal(8, packet.Length);
        Assert.Equal(GvcpConst.PacketTypeCmd, packet[0]);
        Assert.Equal(GvcpConst.DiscoveryCmd, (ushort)((packet[2] << 8) | packet[3]));
        Assert.Equal(0, (packet[4] << 8) | packet[5]);      // 페이로드 없음
        // ack 요구 + 브로드캐스트 ACK 허용 — 이것이 없으면 장치가 유니캐스트로만 답해 브로드캐스트 탐색이 조용해진다.
        Assert.Equal(GvcpConst.FlagAckRequired | GvcpConst.FlagAllowBroadcastAck, packet[1]);
    }
}
