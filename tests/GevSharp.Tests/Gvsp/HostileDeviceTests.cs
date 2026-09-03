using GevSharp.Gvcp;
using GevSharp.Gvsp;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 스트림 소켓에는 세그먼트의 누구나 보낼 수 있고 GVSP 는 보낸 이를 확인하지 않는다(docs/architecture.md).
/// 그래서 손상되거나 남이 보낸 패킷 한 장이 호스트 자원을 얼마나 끌어갈 수 있는지가 그 자체로 계약이다 —
/// 여기 있는 것들은 전부 "패킷 하나로 GB 단위 할당이나 수십만 회 루프를 부를 수 있는가" 를 묻는다.
/// </summary>
public class HostileDeviceTests
{
    private const uint Mono8 = 0x0108_0001;

    private static GevStreamOpt SmallBufferOpt()
    {
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 2;
        return opt;
    }

    [Fact]
    public async Task PayloadWithoutALeaderNeverGrowsTheBuffers()
    {
        // 리더 없이 온 페이로드의 패킷 id 로 계산한 오프셋을 "프레임 크기" 로 믿으면, 64 바이트짜리 한 장이
        // 힌트를 GB 로 부풀리고 그 뒤의 풀 버퍼가 전부 그 크기로 잡힌다. 크기는 리더만 정할 수 있다.
        await using var rig = new StreamRig(SmallBufferOpt());
        await rig.StartAsync();

        var normal = rig.Sender.BuildFrame(1, 64, 32, Mono8);
        rig.Sender.SendFrame(normal);
        using (var first = await rig.ReceiveAsync()) Assert.True(first.IsComplete);
        var bufferBytesBefore = rig.Stream.PoolBufferBytes;

        // 리더 없는 블록의 큰 패킷 id — 프레임당 상한(2^18) 아래라 걸러지지 않고, 오프셋은 100 MB 쯤 된다.
        // 일부러 MaxPayloadBytes(256 MiB) 아래로 잡는다: 그 상한이 대신 막아 주면 여기서 검사하려는
        // "리더 없는 프레임에서는 크기를 배우지 않는다" 가 가려진다.
        // 두 장이 필요하다: 첫 장이 "배운 크기" 를 부풀리고, 둘째 장이 그 크기로 버퍼를 빌린다.
        const uint HugeId = 68_000;
        rig.Sender.SendPayloadWithArbitraryId(2, HugeId, 64);
        rig.Sender.SendPayloadWithArbitraryId(3, HugeId, 64);

        // 리더 없는 두 블록은 버퍼를 하나씩 붙들다 버려진다. 버퍼가 돌아온 것만 보면 슬롯이 아직 살아 있는 순간을 통과할 수 있으므로,
        // 두 블록이 실제로 버려졌다는 통지를 기다린 뒤에 이어서 본다 — 그것이 조립이 끝났다는 확실한 신호다.
        var dropA = await rig.WaitDroppedAsync();
        var dropB = await rig.WaitDroppedAsync();
        Assert.Equal(new[] { 2ul, 3ul }, new[] { dropA.FrameId, dropB.FrameId }.OrderBy(id => id).ToArray());
        await rig.WaitUntilAsync(() => rig.Stream.PoolFreeBuffers == rig.Opt.BufferCount);

        // 그 뒤 정상 프레임이 계속 흘러야 하고, 버퍼는 커지지 않아야 한다.
        var next = rig.Sender.BuildFrame(4, 64, 32, Mono8);
        rig.Sender.SendFrame(next);
        using var delivered = await rig.ReceiveAsync();
        Assert.Equal(4ul, delivered.FrameId);
        Assert.True(delivered.IsComplete);
        Assert.Equal(bufferBytesBefore, rig.Stream.PoolBufferBytes);
    }

    [Fact]
    public async Task LeaderClaimingMoreThanTheLimitIsDroppedInsteadOfAllocated()
    {
        // 리더가 알린 기하가 상한을 넘으면 프레임을 버린다. 상한이 없으면 리더 한 장이 풀 버퍼 전부를 GB 로 잡는다.
        var opt = SmallBufferOpt();
        opt.MaxPayloadBytes = 1 << 20;      // 1 MiB
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 4096 x 4096 Mono8 = 16 MiB > 1 MiB 상한.
        var huge = rig.Sender.BuildFrame(1, 4096, 4096, Mono8);
        rig.Sender.SendPacket(huge, 0, GvspConst.StatusSuccess);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1ul, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Error, diag.Reason);

        // 상한 안의 프레임은 그대로 흐른다.
        var ok = rig.Sender.BuildFrame(2, 64, 32, Mono8);
        rig.Sender.SendFrame(ok);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2ul, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.True(rig.Stream.PoolBufferBytes <= opt.MaxPayloadBytes);
    }

    [Fact]
    public async Task ErrorPacketWithAnImpossiblePacketIdStopsTheFrameInsteadOfAllocating()
    {
        // 오류 패킷이 실어 온 id 는 장치가 준 값이다. 그 값을 믿고 배열을 잡거나 그만큼 훑으면
        // 패킷 한 장이 수 MB 할당과 수십만 회 루프를 수신 스레드에 떠안긴다.
        // 이 프레임이 담을 수 없는 id 는 "어느 구멍인지 모른다" 는 뜻이므로, id 0 이나 범위 밖 id 와 똑같이
        // 이 프레임의 리센드를 끄는 것이 맞다 — 없는 구멍을 표시해 두고 진짜 구멍을 계속 묻는 것이 아니라.
        await using var rig = new StreamRig(SmallBufferOpt());
        await rig.StartAsync();

        var frame = rig.Sender.BuildFrame(1, 64, 32, Mono8);
        rig.Sender.Drop.Add((1ul, 2u));
        rig.Sender.DropOnResend.Add((1ul, 2u));          // 리센드에도 답하지 않아 구멍이 남는다
        rig.Sender.SendFrame(frame);                     // 구멍 하나를 남긴 채 트레일러까지
        await rig.WaitUntilAsync(() => rig.Resend.RequestCount >= 1);

        // 이 프레임이 가질 수 있는 패킷 수(2)를 한참 넘는 id 로 답한다.
        rig.Sender.SendError(1, 200_000, GvcpConst.StatusPacketUnavailable);
        var requestsAfterError = rig.Resend.RequestCount;

        // 더는 묻지 않는다 — 재요청 간격을 여러 번 지나도 요청 수가 늘지 않아야 한다.
        await Task.Delay(4 * rig.Opt.PacketTimeoutMs + 200);
        Assert.Equal(requestsAfterError, rig.Resend.RequestCount);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1ul, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);

        // 수신기는 살아 있고 다음 프레임을 정상으로 받는다.
        var next = rig.Sender.BuildFrame(2, 64, 32, Mono8);
        rig.Sender.SendFrame(next);
        using var delivered = await rig.ReceiveAsync();
        Assert.Equal(2ul, delivered.FrameId);
        Assert.True(delivered.IsComplete);
    }

    [Fact]
    public async Task StopReturnsQueuedFramesToThePoolEvenWhenTheReceiverClosedTheQueueFirst()
    {
        // 수신 스레드가 먼저(소켓이 죽어) 큐를 닫아도, 정지는 큐에 남은 프레임을 반드시 반납해야 한다.
        // 그러지 않으면 완성된 프레임이 든 풀 버퍼가 영영 돌아오지 않는다.
        var opt = SmallBufferOpt();
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 소비자가 가져가지 않은 완성 프레임을 큐에 쌓아 둔다.
        rig.Sender.SendFrame(rig.Sender.BuildFrame(1, 64, 32, Mono8));
        rig.Sender.SendFrame(rig.Sender.BuildFrame(2, 64, 32, Mono8));
        await rig.WaitUntilAsync(() => rig.Stream.Stats.FramesCompleted >= 2);

        // 수신 스레드 쪽에서 큐를 닫은 상태를 만든다(소켓 사망과 같은 경로).
        rig.Stream.SimulateReceiverQueueCompletion(new GevStreamClosedException("socket died"));

        await rig.Stream.StopAsync();

        Assert.Equal(opt.BufferCount, rig.Stream.PoolFreeBuffers);
    }

    [Fact]
    public async Task TinyPayloadPacketCannotMakeAFrameNeedHundredsOfThousandsOfPackets()
    {
        // 패킷 하나의 길이는 "패킷당 데이터 바이트" 의 근거가 된다. 1 바이트짜리 한 장이 256 KiB 프레임을 26 만 패킷으로 만들면
        // 도착 비트·마감 배열이 그만큼 잡히고 구멍 훑기가 매번 그만큼 돈다 — 상한을 넘는 계산은 프레임을 버리는 것으로 끝내야 한다.
        await using var rig = new StreamRig(SmallBufferOpt());
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        // 512 x 512 Mono8 = 262144 바이트. 패킷당 1 바이트로 배우면 필요한 패킷 수가 정확히 상한(2^18)에 닿는다.
        var huge = rig.Sender.BuildFrame(1, 512, 512, Mono8);
        rig.Sender.SendPacket(huge, 0, GvspConst.StatusSuccess);
        rig.Sender.SendPayloadWithArbitraryId(1, packetId: 2, dataLength: 1);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1ul, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Error, diag.Reason);
        Assert.Equal(GvcpConst.StatusInvalidParameter, diag.Code);
        Assert.Equal(1, rig.Stream.Stats.FramesDroppedError);

        // 수신기는 그대로 살아 다음 프레임을 받는다.
        var next = rig.Sender.BuildFrame(2, 64, 32, Mono8);
        rig.Sender.SendFrame(next);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2ul, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(next.Data));
    }
}
