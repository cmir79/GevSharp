using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;
using GevSharp.Gvsp;

namespace GevSharp.Tests.Gvsp;

public class PacketSizeNegotiationTests
{
    /// <summary>
    /// 파이어테스트 SCPS 쓰기에 테스트 데이터그램(IP 크기 − 28 바이트)으로 답하는 가짜 장치. maxPassingSize 를 넘는 크기는 답하지 않는다.
    /// shortAnswerBytes 가 양수면 그런 큰 후보에도 그만큼 짧은 데이터그램으로 답한다 — 경로가 나르지 못하는데도
    /// 무언가 도착하는 상황(앞 후보의 늦은 답, 다른 트래픽)을 만든다.
    /// </summary>
    private sealed class FireTestDevice : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly byte[] _payload = new byte[GevStream.MaxPacketSize];
        private readonly FakeRegPort _regs;
        private readonly int _maxPassingSize;
        private readonly int _shortAnswerBytes;
        private int _shortAnswers;

        public FireTestDevice(FakeRegPort regs, int maxPassingSize, int shortAnswerBytes = 0)
        {
            _regs = regs;
            _maxPassingSize = maxPassingSize;
            _shortAnswerBytes = shortAnswerBytes;
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            regs.OnWrite = OnWrite;
        }

        public List<int> ProbedSizes { get; } = new();

        /// <summary>경로가 나를 수 없는 후보에 짧은 데이터그램으로 답한 횟수.</summary>
        public int ShortAnswerCount => Volatile.Read(ref _shortAnswers);

        private void OnWrite(uint addr, uint value)
        {
            if (addr != GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset) || (value & GvbsAddr.ScpsFireTest) == 0) return;

            var size = (int)(value & GvbsAddr.ScpsSizeMask);
            lock (ProbedSizes) ProbedSizes.Add(size);
            var isPassing = size <= _maxPassingSize;
            if (!isPassing && _shortAnswerBytes <= 0) return;

            var port = (int)(_regs.LastValue(GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset)) ?? 0);
            Assert.True(port > 0, "SCP must be written before the fire test.");
            var length = isPassing ? size - GvspConst.IpUdpOverhead : _shortAnswerBytes;
            if (!isPassing) Interlocked.Increment(ref _shortAnswers);
            _socket.SendTo(_payload, 0, length, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));
        }

        public void Dispose() => _socket.Close();
    }

    private static GevStreamOpt AutoOpt()
    {
        var opt = StreamRig.DefaultOpt();
        opt.PacketSizeMode = PacketSizeMode.Auto;
        return opt;
    }

    [Fact]
    public async Task NegotiatedSizeStopsAtThePathLimit()
    {
        await using var rig = new StreamRig(AutoOpt());
        using var device = new FireTestDevice(rig.Regs, maxPassingSize: 4000);
        rig.Stream.MtuResolver = _ => 9000;

        await rig.StartAsync();

        Assert.InRange(rig.Stream.PacketSize, 3900, 4000);
        Assert.Equal(rig.Stream.PacketSize, rig.Opt.PacketSize);
        Assert.Equal(0, rig.Stream.PacketSize % 4);

        var scps = GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset);
        var scpsWrites = rig.Regs.Writes.Where(w => w.Addr == scps).Select(w => w.Value).ToArray();
        var final = scpsWrites[scpsWrites.Length - 1];
        Assert.Equal((uint)rig.Stream.PacketSize, final & GvbsAddr.ScpsSizeMask);
        Assert.Equal(0u, final & GvbsAddr.ScpsFireTest);
        // 단편화 금지 조건으로 검증한 크기이므로 스트리밍도 단편화 금지로 간다.
        Assert.NotEqual(0u, final & GvbsAddr.ScpsDoNotFragment);
        // 파이어테스트는 전부 단편화 금지와 함께 — 아니면 경로보다 큰 크기도 쪼개져 도착해 통과로 보인다.
        Assert.All(scpsWrites.Take(scpsWrites.Length - 1), v =>
        {
            Assert.NotEqual(0u, v & GvbsAddr.ScpsFireTest);
            Assert.NotEqual(0u, v & GvbsAddr.ScpsDoNotFragment);
        });

        // MTU 와 1500 을 먼저 찔러 본다.
        Assert.Equal(9000, device.ProbedSizes[0]);
        Assert.Equal(1500, device.ProbedSizes[1]);

        // 협상된 크기로 실제 스트리밍이 된다.
        var sent = rig.Sender.SendFrame(1, 200, 100, 0x01080001, seed: 3);
        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(sent.PacketCount, frame.ExpectedPackets);
    }

    [Fact]
    public async Task AShortDatagramDoesNotConfirmALargerSize()
    {
        // 후보 크기보다 작은 데이터그램은 그 크기가 경로를 통과했다는 증거가 아니다 — 앞 후보의 늦은 답이거나 다른 트래픽일 수 있다.
        // 그것을 통과로 받으면 경로가 나르지 못하는 크기로 스트리밍하게 돼 그 뒤 모든 프레임이 조각난다.
        await using var rig = new StreamRig(AutoOpt());
        using var device = new FireTestDevice(rig.Regs, maxPassingSize: 4000, shortAnswerBytes: 100);
        rig.Stream.MtuResolver = _ => 9000;

        await rig.StartAsync();

        // 장치가 큰 후보에도 무언가 보냈음을 먼저 확인한다 — 아무것도 안 왔으면 이 테스트는 검사를 재지 못한 것이 된다.
        Assert.True(device.ShortAnswerCount > 0, "the stub must have answered at least one oversize probe with a short datagram");
        Assert.InRange(rig.Stream.PacketSize, 3900, 4000);
        Assert.Equal(9000, device.ProbedSizes[0]);

        var scps = GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset);
        Assert.Equal((uint?)rig.Stream.PacketSize, rig.Regs.LastValue(scps) & GvbsAddr.ScpsSizeMask);

        // 협상된 크기로 실제 스트리밍이 된다.
        var sent = rig.Sender.SendFrame(1, 200, 100, 0x01080001, seed: 4);
        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
    }

    [Fact]
    public async Task FullMtuIsAcceptedOnTheFirstProbe()
    {
        await using var rig = new StreamRig(AutoOpt());
        using var device = new FireTestDevice(rig.Regs, maxPassingSize: 16000);
        rig.Stream.MtuResolver = _ => 9000;

        await rig.StartAsync();

        Assert.Equal(9000, rig.Stream.PacketSize);
        Assert.Equal(new[] { 9000 }, device.ProbedSizes);
    }

    [Fact]
    public async Task FallsBackTo1500WhenNothingAnswers()
    {
        await using var rig = new StreamRig(AutoOpt());
        using var device = new FireTestDevice(rig.Regs, maxPassingSize: 0);
        rig.Stream.MtuResolver = _ => 9000;

        await rig.StartAsync();

        Assert.Equal(1500, rig.Stream.PacketSize);
        Assert.Contains(9000, device.ProbedSizes);
        Assert.Contains(1500, device.ProbedSizes);
        Assert.Contains(576, device.ProbedSizes);
    }

    [Fact]
    public async Task MtuAboveCapIsClampedAndUnknownMtuProbesFrom9000()
    {
        await using var rig = new StreamRig(AutoOpt());
        using var device = new FireTestDevice(rig.Regs, maxPassingSize: 16000);
        rig.Stream.MtuResolver = _ => 65535;

        await rig.StartAsync();
        Assert.Equal(16000, rig.Stream.PacketSize);

        // MTU 를 모르면 1500 으로 묶지 않고 9000 부터 찔러 본다 — 이분 탐색은 확인된 크기에서 아래로만 가므로 시작점이 상한이다.
        await using var rig2 = new StreamRig(AutoOpt());
        using var device2 = new FireTestDevice(rig2.Regs, maxPassingSize: 16000);
        rig2.Stream.MtuResolver = _ => throw new InvalidOperationException("no interface");

        await rig2.StartAsync();
        Assert.Equal(9000, rig2.Stream.PacketSize);
        Assert.Equal(9000, device2.ProbedSizes[0]);

        await using var rig3 = new StreamRig(AutoOpt());
        using var device3 = new FireTestDevice(rig3.Regs, maxPassingSize: 16000);
        rig3.Stream.MtuResolver = _ => 0;

        await rig3.StartAsync();
        Assert.Equal(9000, rig3.Stream.PacketSize);
    }

    [Fact]
    public async Task ScpsWritesPreserveTheDeviceFlagBits()
    {
        // 장치가 SCPS 에 켜 둔 빅엔디언·단편화 금지 비트는 크기를 쓸 때 지워지면 안 된다 — Fixed 는 있는 그대로, Auto 는 단편화 금지를 더한다.
        var scps = GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset);
        var deviceFlags = GvbsAddr.ScpsBigEndian | GvbsAddr.ScpsDoNotFragment;

        await using var fixedRig = new StreamRig();
        fixedRig.Regs.Set(scps, deviceFlags | 8000u);
        await fixedRig.StartAsync();
        Assert.Equal((uint?)(deviceFlags | 1500u), fixedRig.Regs.LastValue(scps));

        await using var autoRig = new StreamRig(AutoOpt());
        using var device = new FireTestDevice(autoRig.Regs, maxPassingSize: 16000);
        autoRig.Regs.Set(scps, GvbsAddr.ScpsBigEndian | 8000u);
        autoRig.Stream.MtuResolver = _ => 9000;
        await autoRig.StartAsync();
        Assert.Equal((uint?)(GvbsAddr.ScpsBigEndian | GvbsAddr.ScpsDoNotFragment | 9000u), autoRig.Regs.LastValue(scps));
        Assert.Equal(new[] { 9000 }, device.ProbedSizes);
    }

    [Fact]
    public void InterfaceMtuLookupDoesNotThrow()
    {
        // 루프백의 MTU 는 OS 마다 다르게 보고된다(0 이나 음수 포함) — 예외만 없으면 된다.
        _ = GevStream.InterfaceMtu(IPAddress.Loopback);
        Assert.Equal(0, GevStream.InterfaceMtu(IPAddress.Parse("192.0.2.1")));
    }
}
