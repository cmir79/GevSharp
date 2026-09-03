using GevSharp.Gvsp;

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 수신 핫패스의 무할당 계약(R12). 초당 수만 장이 지나는 자리라 패킷마다 무엇을 하나 새로 만들면 그것이 곧 GC 압력이 되고,
/// 잠깐의 정지가 소켓 버퍼를 넘겨 패킷 손실로 나타난다 — 손실은 리센드로 감춰져 원인이 보이지 않는다.
/// <para>
/// 재는 방법: <c>GC.GetAllocatedBytesForCurrentThread</c> 는 스레드별이라 수신 스레드 안에서만 그 스레드의 할당을 볼 수 있는데,
/// 거기에 계측을 심으면 계측이 곧 핫패스가 된다. 그래서 수신 스레드가 데이터그램 하나에 하는 일을 시험 스레드에서
/// 그대로 부른다(<c>GevStream.FeedPacketForTest</c>) — 소켓 호출만 빠지고 파싱·슬롯 찾기·조립·구멍 관리는 같은 코드다.
/// </para>
/// </summary>
public class ReceiverAllocationTests
{
    private const uint Mono8 = 0x0108_0001;
    private const int Width = 1024;
    private const int Height = 64;      // 65,536 바이트 → SCPS 1500 에서 페이로드 45 장

    private sealed class Packet
    {
        public byte[] Bytes = Array.Empty<byte>();
        public int Length;
    }

    /// <summary>블록 하나의 리더·페이로드·트레일러 바이트를 미리 만들어 둔다 — 계측 구간에서 아무것도 만들지 않게.</summary>
    private static Packet[] Prepare(GvspTestSender sender, ulong blockId)
    {
        var frame = sender.BuildFrame(blockId, Width, Height, Mono8);
        var list = new List<Packet>(frame.PacketCount + 2);
        for (uint id = 0; id <= frame.TrailerId; id++)
        {
            var bytes = sender.BuildPacketBytes(frame, id);
            list.Add(new Packet { Bytes = bytes, Length = bytes.Length });
        }
        return list.ToArray();
    }

    [Fact]
    public async Task AssemblingAFrameAllocatesNothingPerPacket()
    {
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 4;
        opt.ResendEnabled = false;      // 구멍이 없는 흐름을 잰다 — 리센드 요청은 별도 경로이고 이미 GVCP 쪽에서 무할당이 확인돼 있다
        await using var rig = new StreamRig(opt);
        // 소켓도 스레드도 없이 수신기만 조립한다 — 계측이 수신 스레드와 같은 상태를 두고 다투지 않게.
        rig.Stream.InitReceiverForTest(opt.PacketSize);
        rig.Sender.PacketSize = opt.PacketSize;

        const int Blocks = 24;
        var prepared = new Packet[Blocks][];
        for (var i = 0; i < Blocks; i++) prepared[i] = Prepare(rig.Sender, (ulong)(i + 1));

        // 예열: JIT, 풀 버퍼 첫 대여, 도착 비트·마감 배열의 첫 확보를 모두 계측 밖에서 치운다.
        for (var i = 0; i < 8; i++)
        {
            Feed(rig, prepared[i]);
            Drain(rig);
        }

        // 계측: 리더 + 마지막을 뺀 페이로드들. 마지막 페이로드와 트레일러는 계측 밖에 둔다 —
        // 마지막 조각이 도착하는 순간 프레임이 완성되며 GevFrame 하나가 만들어지고, 그것은 공개 API 가
        // 프레임 단위로 내보내는 대가이지 패킷마다 치르는 비용이 아니다(그 몫은 아래 테스트가 따로 잰다).
        long allocated = 0;
        var fed = 0;
        for (var i = 8; i < Blocks; i++)
        {
            var packets = prepared[i];
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var p = 0; p < packets.Length - 2; p++)
            {
                rig.Stream.FeedPacketForTest(packets[p].Bytes, packets[p].Length);
                fed++;
            }
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;

            // 계측 밖: 마지막 페이로드와 트레일러로 완성시키고 큐를 비워 버퍼를 돌려준다.
            for (var p = packets.Length - 2; p < packets.Length; p++)
            {
                rig.Stream.FeedPacketForTest(packets[p].Bytes, packets[p].Length);
            }
            Drain(rig);
        }

        Assert.True(fed > 600, $"only {fed} packets went through the hot path; the measurement is too small to mean anything");
        Assert.Equal(0, allocated);
        Assert.Equal(Blocks, rig.Stream.Stats.FramesCompleted);
    }

    [Fact]
    public async Task CompletingAFrameAllocatesOnlyTheFrameObject()
    {
        // 프레임 하나를 완성하는 값은 GevFrame 객체 하나다 — 픽셀 버퍼는 풀에서 빌려 오고 복사본을 뜨지 않는다.
        // 여기서 크게 나오면 프레임마다 페이로드만 한 배열이 새로 잡히고 있다는 뜻이고, 그것은 초당 수십 MB 다.
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 4;
        opt.ResendEnabled = false;
        await using var rig = new StreamRig(opt);
        rig.Stream.InitReceiverForTest(opt.PacketSize);
        rig.Sender.PacketSize = opt.PacketSize;

        const int Blocks = 12;
        var prepared = new Packet[Blocks][];
        for (var i = 0; i < Blocks; i++) prepared[i] = Prepare(rig.Sender, (ulong)(i + 1));
        for (var i = 0; i < 4; i++) { Feed(rig, prepared[i]); Drain(rig); }

        long allocated = 0;
        for (var i = 4; i < Blocks; i++)
        {
            var packets = prepared[i];
            for (var p = 0; p < packets.Length - 2; p++) rig.Stream.FeedPacketForTest(packets[p].Bytes, packets[p].Length);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var p = packets.Length - 2; p < packets.Length; p++) rig.Stream.FeedPacketForTest(packets[p].Bytes, packets[p].Length);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;

            Drain(rig);
        }

        var frames = Blocks - 4;
        var perFrame = allocated / frames;
        // 이미지가 64 KiB 인데 프레임당 값이 그 근처면 버퍼가 복사되고 있는 것이다. 객체 머리 몇십 바이트가 정상이다.
        Assert.True(perFrame <= 256, $"completing a frame allocated {perFrame} bytes; only the GevFrame object should be new (image is {Width * Height} bytes)");
    }

    [Fact]
    public async Task PacketsForAnAlreadyClosedBlockAllocateNothingEither()
    {
        // 늦게 온 패킷·중복 패킷은 실제 스트림에서 흔하다(리센드가 겹치거나 장치가 다시 보낸다).
        // 버릴 패킷이라도 그 판단 자체가 패킷마다 할당하면 손실이 잦은 링크에서 오히려 더 비싸진다.
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 4;
        opt.ResendEnabled = false;
        await using var rig = new StreamRig(opt);
        rig.Stream.InitReceiverForTest(opt.PacketSize);
        rig.Sender.PacketSize = opt.PacketSize;

        var packets = Prepare(rig.Sender, 1);
        Feed(rig, packets);
        Drain(rig);
        // 한 번 더 예열 — 늦은 패킷 경로의 JIT 까지 치운다.
        for (var p = 0; p < packets.Length; p++) rig.Stream.FeedPacketForTest(packets[p].Bytes, packets[p].Length);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var r = 0; r < 20; r++)
        {
            for (var p = 0; p < packets.Length; p++) rig.Stream.FeedPacketForTest(packets[p].Bytes, packets[p].Length);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1, rig.Stream.Stats.FramesCompleted);       // 닫힌 블록이 다시 완성되지는 않는다
    }

    private static void Feed(StreamRig rig, Packet[] packets)
    {
        for (var p = 0; p < packets.Length; p++) rig.Stream.FeedPacketForTest(packets[p].Bytes, packets[p].Length);
    }

    private static void Drain(StreamRig rig)
    {
        while (rig.Stream.TryDrainForTest(out var frame)) frame.Dispose();
    }
}
