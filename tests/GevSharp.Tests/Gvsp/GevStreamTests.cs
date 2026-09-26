using System.Net;
using System.Runtime.InteropServices;
using GevSharp.Gvcp;
using GevSharp.Gvsp;
using GevSharp.Tests.GenApi.Model;

namespace GevSharp.Tests.Gvsp;

public class GevStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const uint Mono8 = 0x01080001;
    private const uint Mono12Packed = 0x010C0006;
    private const uint Rgb8 = 0x02180014;

    [Fact]
    public async Task DiscardQueuedFramesDropsWhatIsWaitingAndKeepsTheStreamRunning()
    {
        // 트리거마다 그 트리거의 프레임이어야 하는 자리에서는 큐의 머리를 받으면 지난 장으로 판정하게 된다.
        // 비운 뒤 받으면 그 다음에 온 것이 나와야 하고, 비우는 것으로 스트림이 끝나서는 안 된다.
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 8;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        for (var i = 0; i < 3; i++) rig.Sender.SendFrame(1UL + (ulong)i, 64, 48, Mono8);
        await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames == 3);

        Assert.Equal(3, rig.Stream.DiscardQueuedFrames());
        Assert.Equal(0, rig.Stream.QueuedFrames);

        // 버린 버퍼는 풀로 돌아가야 한다 — 안 돌아가면 비우려던 것이 오히려 취득을 굶긴다.
        var next = rig.Sender.SendFrame(9UL, 64, 48, Mono8);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(next.BlockId, frame.FrameId);
        Assert.Equal(0, rig.Stream.Stats.Snapshot().FramesDroppedNoBuffer);
    }

    [Fact]
    public async Task DiscardQueuedFramesReportsHowFarItSkipped()
    {
        // 번호의 연속을 세는 쪽은 버린 번호까지 기준에 반영해야 자기가 만든 공백을 유실로 세지 않는다.
        // 그러려면 몇 장인지가 아니라 어디까지 버렸는지를 알아야 한다.
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 8;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        Assert.Equal(0, rig.Stream.DiscardQueuedFrames(out var noneDiscarded));
        Assert.Equal(0UL, noneDiscarded);

        for (var i = 0; i < 3; i++) rig.Sender.SendFrame(100UL + (ulong)i, 64, 48, Mono8);
        await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames == 3);

        Assert.Equal(3, rig.Stream.DiscardQueuedFrames(out var lastDiscarded));
        Assert.Equal(102UL, lastDiscarded);

        // 그 번호를 기준으로 삼으면 다음 장이 이어진다 — 배수가 만든 공백이 유실로 보이지 않는다.
        rig.Sender.SendFrame(103UL, 64, 48, Mono8);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(lastDiscarded + 1, frame.FrameId);
    }

    [Fact]
    public async Task FrameReportsWhetherItsBlockIdIsExtended()
    {
        // 16비트 ID 는 65535 다음이 1 이라 번호의 연속을 따지는 쪽이 되돌이를 감안해야 하고, 64비트 확장 ID 는 그럴
        // 필요가 없다. 어느 쪽인지는 패킷 헤더의 EI 비트가 프레임마다 말해 주므로 프레임에 실어 소비자가 고르게 한다.
        await using var rig = new StreamRig(StreamRig.DefaultOpt());
        await rig.StartAsync();

        rig.Sender.SendFrame(1UL, 64, 48, Mono8);
        using (var plain = await rig.ReceiveAsync()) Assert.False(plain.IsExtendedId);

        rig.Sender.ExtendedIds = true;
        rig.Sender.SendFrame(2UL, 64, 48, Mono8);
        using (var extended = await rig.ReceiveAsync()) Assert.True(extended.IsExtendedId);
    }

    [Fact]
    public async Task StopAsyncDrainsTheQueueSoNoFrameSurvivesTheStop()
    {
        // 정지가 큐를 남긴다면 다음에 여는 쪽이 지난 판의 프레임을 받게 된다. 여기서 못 박아 둔다.
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 8;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        for (var i = 0; i < 3; i++) rig.Sender.SendFrame(1UL + (ulong)i, 64, 48, Mono8);
        await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames == 3);

        await rig.Stream.StopAsync(Ct);

        Assert.Equal(0, rig.Stream.QueuedFrames);
        await Assert.ThrowsAsync<GevStreamClosedException>(async () => await rig.Stream.ReceiveAsync(Ct));
    }

    [Fact]
    public async Task QueuedFramesCountsWhatTheConsumerHasNotTakenYet()
    {
        // 받아 가지 않은 완성 프레임이 몇 장 쌓여 있는지가 이 값이다. 프레임이 늦게 보이는데 유실은 0 인
        // 상황에서 취득이 밀리는지 소비가 밀리는지를 가르는 값이라, 소비를 멈춘 채 세어 본다.
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 8;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        Assert.Equal(0, rig.Stream.QueuedFrames);

        for (var i = 0; i < 3; i++) rig.Sender.SendFrame(1UL + (ulong)i, 64, 48, Mono8);
        await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames == 3);

        // 한 장을 가져가면 그만큼 줄어든다 — 누적 계수기가 아니라 그 순간의 점유다.
        using (await rig.ReceiveAsync()) { }
        await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames == 2);

        var snap = rig.Stream.Stats.Snapshot();
        Assert.Equal(3, snap.FramesCompleted);
        Assert.Equal(1, snap.FramesDelivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteFramesAreDeliveredInOrder(bool extendedIds)
    {
        // 다섯 프레임을 받기 전에 다 보내므로 풀은 그보다 커야 한다(작으면 다섯째가 NoBuffer 로 버려진다 — 그건 다른 테스트가 본다).
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 8;
        // 침묵 규칙(재요청 간격만큼 조용하면 아직 안 온 꼬리도 구멍으로 친다)과 보존 시간은 여기서 보는 것이 아니다. 기본값 20 ms 로는
        // 러너가 송신 쪽을 프레임 도중 그만큼만 멈춰도 짐작한 꼬리를 물어 ResendRequests 가 0 이 아니게 된다(프레임마다 30 ms 멈추는 주입으로 재현).
        // 프레임은 마지막 페이로드에서 닫히므로 문턱을 넉넉히 둬도 이 시험은 느려지지 않는다.
        opt.PacketTimeoutMs = 2000;
        opt.FrameRetentionMs = 5000;
        await using var rig = new StreamRig(opt);
        rig.Sender.ExtendedIds = extendedIds;
        await rig.StartAsync();

        var baseId = extendedIds ? 0x1_0000_0000UL : 1UL;
        var sent = new List<GvspTestSender.SynthFrame>();
        for (var i = 0; i < 5; i++)
        {
            sent.Add(rig.Sender.SendFrame(baseId + (ulong)i, 64, 48, Mono8, seed: (byte)(i * 17), offsetX: 8 + i, offsetY: 4));
        }

        for (var i = 0; i < 5; i++)
        {
            using var frame = await rig.ReceiveAsync();
            Assert.Equal(sent[i].BlockId, frame.FrameId);
            Assert.Equal(sent[i].Timestamp, frame.Timestamp);
            Assert.True(frame.IsComplete);
            Assert.Equal(0, frame.MissingPackets);
            Assert.Equal(64, frame.Width);
            Assert.Equal(48, frame.Height);
            Assert.Equal(8 + i, frame.OffsetX);
            Assert.Equal(4, frame.OffsetY);
            Assert.Equal(64, frame.Stride);
            Assert.Equal(Mono8, frame.PixelFormatCode);
            Assert.Equal(sent[i].Data.Length, frame.PayloadSize);
            Assert.Equal(sent[i].PacketCount, frame.ExpectedPackets);
            Assert.True(frame.Data.Span.SequenceEqual(sent[i].Data));
        }

        // 프레임은 마지막 페이로드에서 닫혀 큐에 들므로, 다섯째를 받은 순간 그 블록의 트레일러는 아직 소켓에 있을 수 있다.
        // 계수기는 수신기가 보낸 패킷을 다 센 뒤에 본다 — 그 전에 찍으면 수신 스레드가 잠깐 밀린 것만으로 25 대 24 로 깨진다.
        await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsReceived >= rig.Sender.PacketsSent);
        var snap = rig.Stream.Stats.Snapshot();
        Assert.Equal(5, snap.FramesCompleted);
        Assert.Equal(5, snap.FramesDelivered);
        Assert.Equal(0, snap.FramesIncomplete);
        Assert.Equal(0, snap.PacketsIgnored);
        Assert.Equal(0, snap.ResendRequests);
        Assert.Equal(sent[4].BlockId, snap.LastFrameId);
        Assert.Equal(rig.Sender.PacketsSent, snap.PacketsReceived);
        Assert.Equal(rig.Stream.Stats.PacketsReceived, snap.PacketsReceived);
        Assert.Equal(0, rig.Resend.RequestCount);
    }

    [Theory]
    [InlineData(Mono8, 100, 10, 0, 0, 100)]
    [InlineData(Mono8, 100, 10, 6, 0, 106)]
    [InlineData(Mono12Packed, 101, 10, 4, 0, 157)]     // 홀수 폭 Packed: 51 묶음 × 3 = 153 + padding 4
    [InlineData(Rgb8, 100, 10, 8, 16, 308)]
    public async Task StrideAndPayloadSizeFollowPixelFormatAndPadding(uint pixelFormat, int width, int height, int paddingX, int paddingY, int expectedStride)
    {
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var sent = rig.Sender.SendFrame(1, width, height, pixelFormat, paddingX, paddingY, seed: 5);
        using var frame = await rig.ReceiveAsync();

        var expectedBytes = expectedStride * height + paddingY;
        var dataBytes = GvspConst.DataBytesPerPacket(1500, extendedIds: false);
        Assert.Equal(expectedStride, frame.Stride);
        Assert.Equal(paddingX, frame.PaddingX);
        Assert.Equal(paddingY, frame.PaddingY);
        Assert.Equal(expectedBytes, frame.PayloadSize);
        Assert.Equal(expectedBytes, sent.Data.Length);
        Assert.Equal((expectedBytes + dataBytes - 1) / dataBytes, frame.ExpectedPackets);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
    }

    [Fact]
    public async Task DroppedPayloadPacketsAreRecoveredByResend()
    {
        // 보존 시간은 여기서 보는 것이 아니다 — 러너가 밀려 그 시간이 지나면 복구가 끝나기 전에 프레임이 포기돼 타임아웃으로 둔갑한다.
        var opt = StreamRig.DefaultOpt();
        opt.FrameRetentionMs = 3000;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 20 패킷짜리 프레임에서 3 번과 7..9 번을 빠뜨린다 — 연속 구멍은 요청 하나로 묶여야 한다.
        rig.Sender.Drop.Add((1, 3));
        rig.Sender.Drop.Add((1, 7));
        rig.Sender.Drop.Add((1, 8));
        rig.Sender.Drop.Add((1, 9));
        var sent = rig.Sender.SendFrame(1, 244, 120, Mono8, seed: 9);
        Assert.Equal(20, sent.PacketCount);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.Equal(1UL, frame.FrameId);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));

        var snap = rig.Stream.Stats.Snapshot();
        Assert.Equal(1, snap.FramesCompleted);
        Assert.Equal(0, snap.FramesIncomplete);
        Assert.Equal(4, snap.ResendRecovered);
        Assert.Equal(4, snap.PacketsResent);
        Assert.True(snap.ResendRequests >= 4);

        var requests = rig.Resend.Requests;
        Assert.Contains(requests, r => r.BlockId == 1 && r.First == 3 && r.Last == 3 && !r.ExtendedIds && r.StreamChannel == 0);
        Assert.Contains(requests, r => r.BlockId == 1 && r.First == 7 && r.Last == 9);
    }

    [Fact]
    public async Task ExtendedIdResendRequestsCarryTheFlagAndFullBlockId()
    {
        // 보존 시간은 여기서 보는 것이 아니다 — 러너가 밀려 그 시간이 지나면 요청이 오가기 전에 프레임이 포기된다.
        var opt = StreamRig.DefaultOpt();
        opt.FrameRetentionMs = 3000;
        await using var rig = new StreamRig(opt);
        rig.Sender.ExtendedIds = true;
        await rig.StartAsync();

        const ulong blockId = 0x1_0000_0005UL;
        rig.Sender.Drop.Add((blockId, 2));
        var sent = rig.Sender.SendFrame(blockId, 120, 60, Mono8, seed: 3);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(blockId, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));

        var request = Assert.Single(rig.Resend.Requests);
        Assert.Equal(blockId, request.BlockId);
        Assert.True(request.ExtendedIds);
        Assert.Equal(2u, request.First);
        Assert.Equal(2u, request.Last);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnrecoverableHoleDropsTheFrameWithDiagnostics(bool deviceAnswersUnavailable)
    {
        // 장치가 0x800C 로 답하거나(패킷을 더는 갖고 있지 않다) 아예 답하지 않거나 — 어느 쪽이든 프레임은 진단과 함께 버려진다.
        // 보존 시간은 넉넉히 둔다. 기본값(100 ms)이면 러너가 밀린 사이 첫 리센드 요청이 나가기도 전에 프레임이 포기돼,
        // 여기서 볼 "요청은 했고 그래도 못 메웠다" 가 "요청조차 없었다" 로 바뀐다. 답이 없는 쪽은 이 시간이 지나야 버려지므로 그만큼만 늘린다.
        var opt = StreamRig.DefaultOpt();
        opt.FrameRetentionMs = 1000;
        await using var rig = new StreamRig(opt);
        rig.Resend.Behaviour = deviceAnswersUnavailable ? TestResendPort.Mode.Unavailable : TestResendPort.Mode.Never;
        await rig.StartAsync();

        rig.Sender.Drop.Add((1, 4));
        var lost = rig.Sender.SendFrame(1, 244, 120, Mono8, seed: 1);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1UL, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);
        Assert.Equal(1, diag.MissingPackets);
        Assert.Equal(lost.PacketCount, diag.ExpectedPackets);

        Assert.False(rig.Stream.TryReceive(out var none));
        Assert.Null(none);
        Assert.True(rig.Resend.RequestCount >= 1);

        // 다음 프레임은 막힘 없이 온다.
        var next = rig.Sender.SendFrame(2, 64, 32, Mono8, seed: 2);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2UL, frame.FrameId);
        Assert.True(frame.Data.Span.SequenceEqual(next.Data));

        var snap = rig.Stream.Stats.Snapshot();
        Assert.Equal(1, snap.FramesIncomplete);
        Assert.Equal(1, snap.PacketsMissing);
        Assert.Equal(1, snap.FramesCompleted);
        Assert.Equal(0, snap.ResendRecovered);
        if (deviceAnswersUnavailable) Assert.True(snap.ErrorPackets >= 1);
    }

    [Fact]
    public async Task IncompleteFrameIsDeliveredWhenOptedIn()
    {
        var opt = StreamRig.DefaultOpt();
        opt.DeliverIncompleteFrames = true;
        await using var rig = new StreamRig(opt);
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        rig.Sender.Drop.Add((1, 2));
        var sent = rig.Sender.SendFrame(1, 64, 100, Mono8, seed: 4);
        Assert.Equal(5, sent.PacketCount);

        using var frame = await rig.ReceiveAsync();
        Assert.False(frame.IsComplete);
        Assert.Equal(1, frame.MissingPackets);
        Assert.Equal(5, frame.ExpectedPackets);
        Assert.Equal(sent.Data.Length, frame.PayloadSize);

        // 빠진 패킷 자리는 0, 나머지는 원본과 같다.
        var expected = (byte[])sent.Data.Clone();
        var dataBytes = sent.DataBytesPerPacket;
        Array.Clear(expected, dataBytes, dataBytes);
        Assert.True(frame.Data.Span.SequenceEqual(expected));

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);
        Assert.Equal(1, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(1, rig.Stream.Stats.FramesDelivered);
    }

    [Fact]
    public async Task PoolExhaustionDropsNewFramesWithoutTouchingHeldBuffers()
    {
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 2;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        var sent1 = rig.Sender.SendFrame(1, 64, 32, Mono8, seed: 10);
        var sent2 = rig.Sender.SendFrame(2, 64, 32, Mono8, seed: 20);
        using var held1 = await rig.ReceiveAsync();
        using var held2 = await rig.ReceiveAsync();
        Assert.Equal(1UL, held1.FrameId);
        Assert.Equal(2UL, held2.FrameId);

        // 소비자가 든 버퍼에 표식을 남긴다 — 수신기가 이 버퍼에 쓰면 표식이나 픽셀이 바뀐다.
        Assert.True(MemoryMarshal.TryGetArray(held1.Data, out var seg1));
        Assert.True(MemoryMarshal.TryGetArray(held2.Data, out var seg2));
        seg1.Array![seg1.Offset] = 0xEE;
        seg2.Array![seg2.Offset + seg2.Count - 1] = 0xDD;

        for (ulong id = 3; id <= 5; id++)
        {
            rig.Sender.SendFrame(id, 64, 32, Mono8, seed: (byte)(id * 30));
        }
        await rig.WaitUntilAsync(() => rig.Stream.Stats.FramesDroppedNoBuffer >= 3);

        for (var i = 0; i < 3; i++)
        {
            var diag = await rig.WaitDroppedAsync();
            Assert.Equal(GevFrameDropReason.NoBuffer, diag.Reason);
            Assert.Equal(3UL + (ulong)i, diag.FrameId);
        }
        Assert.False(rig.Stream.TryReceive(out _));

        Assert.Equal(0xEE, held1.Data.Span[0]);
        Assert.True(held1.Data.Span.Slice(1).SequenceEqual(sent1.Data.AsSpan(1)));
        Assert.Equal(0xDD, held2.Data.Span[held2.PayloadSize - 1]);
        Assert.True(held2.Data.Span.Slice(0, held2.PayloadSize - 1).SequenceEqual(sent2.Data.AsSpan(0, sent2.Data.Length - 1)));

        // 돌려주면 다시 받을 수 있다.
        held1.Dispose();
        held2.Dispose();
        var sent6 = rig.Sender.SendFrame(6, 64, 32, Mono8, seed: 60);
        using var frame6 = await rig.ReceiveAsync();
        Assert.Equal(6UL, frame6.FrameId);
        Assert.True(frame6.Data.Span.SequenceEqual(sent6.Data));
        Assert.Equal(3, rig.Stream.Stats.FramesDroppedNoBuffer);
    }

    [Fact]
    public async Task FrameDisposeIsIdempotentAndDataThrowsAfterwards()
    {
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 2;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        var sent = rig.Sender.SendFrame(1, 32, 8, Mono8, seed: 7);
        var frame = await rig.ReceiveAsync();
        Assert.Equal(sent.Data, frame.ToArray());
        Assert.Equal(sent.Data.Length, frame.Data.Length);

        Parallel.For(0, 8, _ => frame.Dispose());
        frame.Dispose();
        Assert.True(frame.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => frame.Data);
        Assert.Throws<ObjectDisposedException>(() => frame.ToArray());

        // 버퍼가 한 번만 반납됐다면 풀 크기보다 많은 프레임도 하나씩 돌려주며 끝없이 받을 수 있다.
        for (ulong id = 2; id <= 6; id++)
        {
            rig.Sender.SendFrame(id, 32, 8, Mono8, seed: (byte)id);
            using var f = await rig.ReceiveAsync();
            Assert.Equal(id, f.FrameId);
        }
        Assert.Equal(0, rig.Stream.Stats.FramesDroppedNoBuffer);
        Assert.Throws<ObjectDisposedException>(() => frame.Data);
    }

    [Fact]
    public async Task StopUnblocksPendingReceive()
    {
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var pending = rig.Stream.ReceiveAsync(Ct).AsTask();
        await Task.Delay(20, Ct);
        Assert.False(pending.IsCompleted);

        await rig.Stream.StopAsync(Ct);

        await Assert.ThrowsAsync<GevStreamClosedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3), Ct));
        await Assert.ThrowsAsync<GevStreamClosedException>(() => rig.Stream.ReceiveAsync(Ct).AsTask());
        Assert.Throws<GevStreamClosedException>(() => rig.Stream.TryReceive(out _));
        Assert.False(rig.Stream.IsStarted);

        // 두 번째 정지는 무해하다.
        await rig.Stream.StopAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Stream.StartAsync(Ct));
    }

    [Fact]
    public async Task StopCancelledMidwayStillTurnsTheDeviceTransmissionOff()
    {
        // 정지 도중 취소가 와도 장치 전송 끄기(SCP = 0, SCDA = 0)는 끝까지 가야 한다. 건너뛰면 장치는 닫힌 포트를 향해
        // 계속 쏘는데 정지는 성공으로 돌아와, 호출자는 그 사실을 알 길이 없다.
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var scp = GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset);
        var scda = GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset);
        using var cts = new CancellationTokenSource();
        rig.Regs.OnWrite = (addr, value) =>
        {
            if (addr == scp && value == 0) cts.Cancel();   // SCP = 0 이 나가는 바로 그때 취소가 도착한다
        };

        await rig.Stream.StopAsync(cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Contains((scda, 0u), rig.Regs.Writes);
        Assert.False(rig.Stream.IsStarted);
        await Assert.ThrowsAsync<GevStreamClosedException>(async () => await rig.Stream.ReceiveAsync(Ct));
    }

    [Fact]
    public async Task StopWithAPreCancelledTokenStillStopsEverything()
    {
        // 셧다운 경로는 시한이 이미 지난 토큰으로 정지를 부르기 쉽다. 그래도 정지는 끝까지 한다 — 장치 전송 끄기,
        // 소켓 닫기, 큐 비우기, 정지 상태. 아무것도 안 하고 취소만 던지면 스트림은 그대로 돌고 큐의 버퍼도 돌아오지 않는다.
        var opt = StreamRig.DefaultOpt();
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        rig.Sender.SendFrame(1UL, 64, 48, Mono8);
        await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames == 1);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await rig.Stream.StopAsync(cts.Token);

        var writes = rig.Regs.Writes;
        Assert.Contains((GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset), 0u), writes);
        Assert.Contains((GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset), 0u), writes);
        Assert.False(rig.Stream.IsStarted);
        Assert.Equal(0, rig.Stream.QueuedFrames);
        Assert.Equal(opt.BufferCount, rig.Stream.PoolFreeBuffers);
        await Assert.ThrowsAsync<GevStreamClosedException>(async () => await rig.Stream.ReceiveAsync(Ct));
    }

    [Fact]
    public async Task ReceiverThreadDyingOnItsOwnClearsIsStarted()
    {
        // 소켓이 죽어 수신 스레드가 스스로 끝나면 받기는 "닫힘" 으로 끝난다. 그때 IsStarted 가 계속 참이면 그 값으로
        // "다시 열기" 와 "이미 멈춤" 을 가르는 쪽이 속는다 — 상태가 사실을 말해야 한다.
        var opt = StreamRig.DefaultOpt();
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        rig.Sender.SendFrame(1UL, 64, 48, Mono8);
        await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames == 1);

        rig.Stream.KillSocketForTest();

        // 이미 큐에 든 장은 그대로 받아 갈 수 있고, 그 다음에 닫힘이 나온다.
        using (var queued = await rig.ReceiveAsync()) Assert.Equal(1UL, queued.FrameId);
        await Assert.ThrowsAsync<GevStreamClosedException>(() => rig.Stream.ReceiveAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(rig.Stream.IsStarted);

        // 스스로 끝난 스트림은 멈춘 것이 아니라 정리를 기다리는 것이다 — 다시 시작할 수는 없고,
        // 정지를 불러야 장치 전송이 꺼지고 버퍼가 돌아온다.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Stream.StartAsync(Ct));
        await rig.Stream.StopAsync(Ct);
        var writes = rig.Regs.Writes;
        Assert.Contains((GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset), 0u), writes);
        Assert.Contains((GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset), 0u), writes);
        Assert.False(rig.Stream.IsStarted);
        Assert.Equal(opt.BufferCount, rig.Stream.PoolFreeBuffers);
    }

    [Fact]
    public void ReceiveBeforeStartThrows()
    {
        var stream = new GevStream(new FakeRegPort(), new TestResendPort(new GvspTestSender()), IPAddress.Loopback, StreamRig.DefaultOpt());
        Assert.Throws<GevStreamClosedException>(() => { _ = stream.ReceiveAsync(Ct); });
        Assert.Throws<GevStreamClosedException>(() => stream.TryReceive(out _));
        Assert.False(stream.IsStarted);
    }

    [Fact]
    public void ConstructorValidatesArguments()
    {
        var regs = new FakeRegPort();
        var resend = new TestResendPort(new GvspTestSender());
        Assert.Throws<ArgumentNullException>(() => new GevStream(null!, resend, IPAddress.Loopback, null));
        Assert.Throws<ArgumentNullException>(() => new GevStream(regs, null!, IPAddress.Loopback, null));
        Assert.Throws<ArgumentException>(() => new GevStream(regs, resend, IPAddress.IPv6Loopback, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GevStream(regs, resend, IPAddress.Loopback, new GevStreamOpt { BufferCount = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GevStream(regs, resend, IPAddress.Loopback, new GevStreamOpt { PacketSize = 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GevStream(regs, resend, IPAddress.Loopback, new GevStreamOpt { PacketRequestRatio = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GevStream(regs, resend, IPAddress.Loopback, null, streamChannel: -1));
    }

    [Fact]
    public async Task PacketDelayLeftOnTheDeviceIsClearedWhenNoneIsRequested()
    {
        // 0 은 "지연 없음" 이지 "그대로 두기" 가 아니다. 앞선 세션이 남긴 지연을 두면 프레임레이트가 조용히 깎인다.
        var opt = StreamRig.DefaultOpt();
        opt.InterPacketDelay = 0;
        await using var rig = new StreamRig(opt, streamChannel: 1);
        rig.Regs.Set(GvbsAddr.StreamChannel(1, GvbsAddr.ScpdOffset), 18_750);

        await rig.StartAsync();

        Assert.Contains((GvbsAddr.StreamChannel(1, GvbsAddr.ScpdOffset), 0u), rig.Regs.Writes);
    }

    [Fact]
    public async Task PacketDelayAlreadyMatchingIsNotWrittenAgain()
    {
        var opt = StreamRig.DefaultOpt();
        opt.InterPacketDelay = 1000;
        await using var rig = new StreamRig(opt, streamChannel: 1);
        rig.Regs.Set(GvbsAddr.StreamChannel(1, GvbsAddr.ScpdOffset), 1000);

        await rig.StartAsync();

        Assert.DoesNotContain(rig.Regs.Writes, w => w.Item1 == GvbsAddr.StreamChannel(1, GvbsAddr.ScpdOffset));
    }

    [Fact]
    public async Task RegisterWritesFollowStartAndStopOrder()
    {
        var opt = StreamRig.DefaultOpt();
        opt.InterPacketDelay = 1000;
        await using var rig = new StreamRig(opt, streamChannel: 1);
        await rig.StartAsync();

        var port = (uint)rig.Stream.LocalPort;
        Assert.True(port > 0);
        Assert.Equal(1500, rig.Stream.PacketSize);

        var expectedStart = new[]
        {
            (GvbsAddr.StreamChannel(1, GvbsAddr.ScdaOffset), 0x7F000001u),
            (GvbsAddr.StreamChannel(1, GvbsAddr.ScpOffset), port),
            (GvbsAddr.StreamChannel(1, GvbsAddr.ScpsOffset), 1500u),
            (GvbsAddr.StreamChannel(1, GvbsAddr.ScpdOffset), 1000u),
        };
        Assert.Equal(expectedStart, rig.Regs.Writes);

        await rig.Stream.StopAsync(Ct);

        var expectedStop = new[]
        {
            (GvbsAddr.StreamChannel(1, GvbsAddr.ScpOffset), 0u),
            (GvbsAddr.StreamChannel(1, GvbsAddr.ScdaOffset), 0u),
        };
        Assert.Equal(expectedStart.Concat(expectedStop), rig.Regs.Writes);
    }

    [Fact]
    public async Task FailedRegisterWriteDuringStartResetsScpAndClosesSocket()
    {
        await using var rig = new StreamRig();
        rig.Regs.OnWrite = (addr, value) =>
        {
            if (addr == GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset)) throw new GevStatusException("WRITEREG", GvcpConst.StatusAccessDenied);
        };

        await Assert.ThrowsAsync<GevStatusException>(() => rig.Stream.StartAsync(Ct));
        Assert.False(rig.Stream.IsStarted);
        var writes = rig.Regs.Writes;
        Assert.Equal((GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset), 0u), writes[writes.Length - 1]);
        Assert.Throws<GevStreamClosedException>(() => rig.Stream.TryReceive(out _));
    }

    [Fact]
    public async Task FailedSocketCreationLeavesTheStreamStopped()
    {
        // 소켓 생성은 핸들·버퍼가 바닥나면 던진다. 그때 스트림이 "시작 중" 에 걸려 있으면 다시 시작하려는 쪽은
        // "이미 시작됨" 이라는 엉뚱한 답을 받고, 정지는 아무것도 쓴 적 없는 장치에 SCP/SCDA = 0 을 보낸다.
        await using var rig = new StreamRig();
        rig.Stream.SocketFactory = () => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NoBufferSpaceAvailable);

        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(() => rig.Stream.StartAsync(Ct));
        Assert.False(rig.Stream.IsStarted);
        Assert.Throws<GevStreamClosedException>(() => rig.Stream.TryReceive(out _));

        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Stream.StartAsync(Ct));
        Assert.Contains("cannot be restarted", retry.Message);

        await rig.Stream.StopAsync(Ct);
        Assert.Empty(rig.Regs.Writes);
    }

    [Fact]
    public async Task ScpWriteFailingAfterSendIsStillReset()
    {
        // SCP 쓰기 자체의 응답이 유실됐다 — 장치는 포트를 받았을 수 있으므로 닫힌 포트로 쏘지 않게 되돌려야 한다
        await using var rig = new StreamRig();
        var scp = GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset);
        rig.Regs.OnWrite = (addr, value) =>
        {
            if (addr == scp && value != 0) throw new GevTimeoutException("WRITEREG reply was lost");
        };

        await Assert.ThrowsAsync<GevTimeoutException>(() => rig.Stream.StartAsync(Ct));
        Assert.False(rig.Stream.IsStarted);
        var writes = rig.Regs.Writes;
        Assert.Equal((scp, 0u), writes[writes.Length - 1]);
    }

    [Fact]
    public async Task SlowSenderDoesNotTriggerSpuriousResends()
    {
        // 침묵 규칙(재요청 간격만큼 조용하면 꼬리를 구멍으로 친다)이 스케줄링 지연에 걸리지 않게 간격을 넉넉히 둔다 — 여기서 보는 것은 유예뿐이다.
        var opt = StreamRig.DefaultOpt();
        // 프레임 전체가 25 ms 안에 나가므로 문턱을 크게 잡아도 "아직 안 온 꼬리는 구멍이 아니다" 라는 성질은 그대로 걸린다.
        // 문턱이 러너의 선점보다 짧으면 이 테스트는 유예가 아니라 러너의 스케줄링을 재게 된다 — 송신을 프레임 도중 1.1 s 멈추면
        // 1 s 문턱의 침묵 규칙이 꼬리를 물어 요청이 1 건 나간다(주입으로 재현). 과부하 러너의 멈춤이 그만큼 길어질 수 있어 문턱을 더 올린다.
        // 프레임은 마지막 페이로드에서 닫히므로 문턱을 더 올려도 이 시험은 느려지지 않는다.
        // (스트레스 실행에서 실제로 잡힌 이 시험의 실패는 이 경로가 아니라 아래에 적은 데이터그램 유실이었다.)
        opt.PacketTimeoutMs = 10_000;
        // 보존 시간도 마찬가지 — 러너가 밀려 프레임이 포기되면 "군더더기 요청이 없다" 대신 타임아웃이 난다.
        opt.FrameRetentionMs = 20_000;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 패킷 사이가 유예(2 ms)보다 길어도 아직 안 온 꼬리는 구멍이 아니다.
        var frame = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 11);
        Assert.Equal(5, frame.PacketCount);
        rig.Sender.SendFrame(frame, interPacketDelayMs: 5);

        using var received = await rig.ReceiveAsync();
        Assert.True(received.IsComplete);
        Assert.True(received.Data.Span.SequenceEqual(frame.Data));
        // 요청 수는 수신기가 보낸 패킷(트레일러까지)을 다 센 뒤에 본다 — 프레임을 받은 순간에는 트레일러가 아직 소켓에 있을 수 있다.
        // 끝내 다 세지 못하면 데이터그램 하나가 스트림 소켓까지 와서 세지기 전에 사라진 것이다. 그때의 요청은 그 유실을 메운 것이라
        // 군더더기는 아니지만, 이 시험은 그것도 실패로 남긴다: 윈도우 루프백에서 짧은 수신 타임아웃이 IOPending 으로 끝날 때
        // 데이터그램이 사라지는 것을 따로 쟀고, 패킷 사이마다 수신 타임아웃이 도는 이 시험이 그 유실이 드러나는 자리다.
        // 기다리는 대신 실패 메시지가 두 경우(수신 쪽 유실 / 군더더기 요청)를 가른다.
        try
        {
            await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsReceived >= rig.Sender.PacketsSent, 2000);
        }
        catch (TimeoutException)
        {
            var s = rig.Stream.Stats.Snapshot();
            var requests = string.Join("; ", rig.Resend.Requests.Select(r => $"{r.First}..{r.Last}"));
            Assert.Fail($"The receiver counted {s.PacketsReceived} of the {rig.Sender.PacketsSent} datagrams sent ({s.PacketsResent} of them resend copies); "
                + $"resend requests [{requests}]. A datagram reached the stream socket and was lost before it was counted, so a request here repairs a real "
                + "loss rather than being spurious. On Windows loopback a blocking receive whose short timeout ends in IOPending has been measured to lose "
                + "the datagram (GevStream.Receiver.cs HandleReceiveError).");
        }
        Assert.Equal(0, rig.Resend.RequestCount);
        Assert.Equal(0, rig.Stream.Stats.ResendRequests);
    }

    [Fact]
    public async Task LostTrailerStillCompletesTheFrame()
    {
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(1, 64, 32, Mono8, seed: 12);
        rig.Sender.Drop.Add((1, sent.TrailerId));
        rig.Sender.SendFrame(sent);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(0, rig.Resend.RequestCount);
    }

    [Fact]
    public async Task LostTailIsRequestedAfterSilence()
    {
        // 여기서 보는 것은 침묵 규칙이지 보존 시간이 아니다 — 러너가 밀리면 꼬리를 요청하기도 전에 프레임이 포기된다.
        var opt = StreamRig.DefaultOpt();
        opt.FrameRetentionMs = 3000;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 마지막 페이로드와 트레일러가 함께 사라지면 "더 높은 id" 가 없다 — 침묵이 재요청 간격만큼 이어진 뒤 꼬리를 요청해야 한다.
        var sent = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 13);
        rig.Sender.Drop.Add((1, (uint)sent.PacketCount));
        rig.Sender.Drop.Add((1, sent.TrailerId));
        rig.Sender.SendFrame(sent);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(1, rig.Stream.Stats.ResendRecovered);
        var request = Assert.Single(rig.Resend.Requests);
        Assert.Equal((uint)sent.PacketCount, request.First);
        Assert.Equal((uint)sent.PacketCount, request.Last);
    }

    [Fact]
    public async Task DeviceThatPausesMidFrameDoesNotLoseTheFrame()
    {
        // 장치가 프레임 도중 재요청 간격보다 오래 쉬면 수신기는 침묵 규칙으로 "꼬리까지 다 보내졌다" 고 짐작한다. 그 짐작만으로
        // 아직 보내지지도 않은 꼬리 전체를 구멍으로 세어 요청 예산을 넘겼다고 프레임을 버리면, 패킷 하나 잃지 않은 프레임이 통째로 사라진다.
        // 짐작한 꼬리는 프레임을 버릴 근거가 못 된다 — 예산 안에서 물어보되 프레임은 보존 시간까지 살아 있어야 한다.
        var opt = StreamRig.DefaultOpt();
        // 쉬는 동안 보존 시간으로 닫히면 여기서 볼 것(예산 판정)이 가려진다.
        opt.FrameRetentionMs = 3000;
        // 프레임이 버려졌을 때 "왜" 가 기다림이 아니라 프레임 내용으로 드러나게.
        opt.DeliverIncompleteFrames = true;
        await using var rig = new StreamRig(opt);
        // 되돌아오는 리센드 사본은 없다 — 프레임을 완성하는 것은 장치가 쉬었다가 이어 보내는 원본뿐이다.
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(1, 244, 120, Mono8, seed: 6);
        Assert.Equal(20, sent.PacketCount);
        for (uint id = 0; id <= 5; id++) rig.Sender.SendPacket(sent, id, GvspConst.StatusSuccess);

        // 재요청 간격(20 ms)의 세 배 — 꼬리 짐작과 그에 이은 판정이 모두 일어날 만큼 쉰다.
        await Task.Delay(60, Ct);
        for (uint id = 6; id <= sent.TrailerId; id++) rig.Sender.SendPacket(sent, id, GvspConst.StatusSuccess);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete,
            $"no packet was lost, yet the frame came out with {frame.MissingPackets} missing after {rig.Resend.RequestCount} resend request(s): "
            + "GevStream.Receiver.cs must not abandon a frame whose tail is only assumed from silence.");
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(0, rig.DroppedCount);

        // 짐작한 꼬리라도 예산을 넘겨 묻지는 않는다 — 요청은 서로 다른 패킷 ceil(20 × 0.25) = 5 개 안에서만 나간다.
        var requested = new HashSet<uint>();
        foreach (var r in rig.Resend.Requests)
        {
            for (var id = r.First; id <= r.Last; id++) requested.Add(id);
        }
        Assert.True(requested.Count <= 5, $"resend requests covered {requested.Count} distinct packets, the budget is 5");
    }

    [Fact]
    public async Task GuessedTailRequestsDoNotSpendTheBudgetForRealHoles()
    {
        // 장치가 프레임 도중 쉬면 수신기는 아직 보내지지도 않은 꼬리를 구멍으로 짐작해 물어본다. 그 짐작한 요청을 진짜 유실과 같은 예산에서
        // 빼면, 장치가 이어 보낸 뒤에 드러나는 진짜 구멍을 메울 여지가 남지 않아 멀쩡히 살릴 수 있는 프레임이 버려진다.
        // 짐작으로 물어본 것은 진짜 유실의 예산을 쓰지 않아야 한다.
        var opt = StreamRig.DefaultOpt();
        // 쉬는 동안 프레임이 보존 시간으로 닫히면 예산 이야기를 꺼내 보지도 못한다.
        opt.FrameRetentionMs = 3000;
        // 버려진 프레임도 받아 봐야 "무엇이 안 메워졌는지" 가 기다림이 아니라 프레임 내용으로 드러난다.
        opt.DeliverIncompleteFrames = true;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 20 패킷, 예산은 ceil(20 × 0.25) = 5. 진짜 유실은 둘(3 과 16)뿐이라 예산 안이다.
        var sent = rig.Sender.BuildFrame(1, 244, 120, Mono8, seed: 8);
        Assert.Equal(20, sent.PacketCount);
        rig.Sender.Drop.Add((1, 3));
        rig.Sender.Drop.Add((1, 16));

        for (uint id = 0; id <= 8; id++) rig.Sender.SendPacket(sent, id, GvspConst.StatusSuccess);

        // 수신기가 침묵을 꼬리로 읽고 아직 보내지지도 않은 9 이후를 물어볼 때까지 기다린다 — 그 요청이 이 테스트의 전제다.
        await rig.WaitUntilAsync(() => rig.Resend.Requests.Any(r => r.BlockId == 1 && r.First >= 9));
        for (uint id = 9; id <= sent.TrailerId; id++) rig.Sender.SendPacket(sent, id, GvspConst.StatusSuccess);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete,
            $"the frame came out with {frame.MissingPackets} missing packet(s) after {rig.Resend.RequestCount} resend request(s): "
            + "GevStream.Receiver.cs must not charge requests for a tail it only guessed at to the budget real holes need.");
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
        // 두 구멍은 리센드로 메워졌다 — 짐작으로 미리 받아 둔 꼬리와 섞이지 않게 요청이 실제로 있었는지도 본다.
        Assert.Contains(rig.Resend.Requests, r => r.BlockId == 1 && r.First == 3 && r.Last == 3);
        Assert.Contains(rig.Resend.Requests, r => r.BlockId == 1 && r.First == 16 && r.Last == 16);
    }

    [Fact]
    public async Task LostLeaderIsRequestedAndRecovered()
    {
        // 보존 시간은 여기서 보는 것이 아니다 — 러너가 밀려 그 시간이 지나면 리더가 돌아오기 전에 프레임이 포기된다.
        var opt = StreamRig.DefaultOpt();
        opt.FrameRetentionMs = 3000;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 첫 프레임으로 버퍼 크기를 알게 한 뒤, 둘째 프레임의 리더를 빠뜨린다.
        var first = rig.Sender.SendFrame(1, 64, 100, Mono8, seed: 1);
        using (var f1 = await rig.ReceiveAsync())
        {
            Assert.True(f1.Data.Span.SequenceEqual(first.Data));
        }

        rig.Sender.Drop.Add((2, 0));
        var second = rig.Sender.SendFrame(2, 64, 100, Mono8, seed: 2);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2UL, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.Equal(second.Timestamp, frame.Timestamp);
        Assert.True(frame.Data.Span.SequenceEqual(second.Data));
        Assert.Contains(rig.Resend.Requests, r => r.BlockId == 2 && r.First == 0 && r.Last == 0);
        Assert.Equal(1, rig.Stream.Stats.ResendRecovered);
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
    }

    [Fact]
    public async Task TooManyFramesInFlightAbandonsTheOldest()
    {
        await using var rig = new StreamRig();
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        rig.Sender.Drop.Add((1, 2));
        rig.Sender.SendFrame(1, 64, 100, Mono8, seed: 1);
        for (ulong id = 2; id <= 5; id++)
        {
            rig.Sender.SendFrame(id, 64, 100, Mono8, seed: (byte)id);
        }

        // 블록 1 은 5 번째 블록이 열릴 때 밀려나고, 2..5 는 순서대로 온다.
        for (ulong id = 2; id <= 5; id++)
        {
            using var frame = await rig.ReceiveAsync();
            Assert.Equal(id, frame.FrameId);
            Assert.True(frame.IsComplete);
        }
        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1UL, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);
        Assert.Equal(1, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(4, rig.Stream.Stats.FramesCompleted);
    }

    [Fact]
    public async Task ResendDisabledDropsIncompleteFramesQuickly()
    {
        var opt = StreamRig.DefaultOpt();
        opt.ResendEnabled = false;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        rig.Sender.Drop.Add((1, 1));
        rig.Sender.SendFrame(1, 64, 100, Mono8, seed: 1);
        var next = rig.Sender.SendFrame(2, 64, 100, Mono8, seed: 2);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2UL, frame.FrameId);
        Assert.True(frame.Data.Span.SequenceEqual(next.Data));
        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1UL, diag.FrameId);
        Assert.Equal(0, rig.Resend.RequestCount);
        Assert.Equal(1, rig.Stream.Stats.FramesIncomplete);
    }

    [Fact]
    public async Task SkippedFrameWithoutATrailerIgnoresRetentionWhenResendIsOff()
    {
        // 버리기로 한 프레임(여기서는 지원하지 않는 payload_type 4 의 12바이트 리더)도 트레일러가 오거나 조용해질 때까지 슬롯을 쥔다.
        // 리센드가 꺼져 있으면 기다릴 리센드가 없으므로 PacketTimeoutMs 에 닫아야 한다 — 시작 로그와 옵션 설명이 "FrameRetentionMs 는
        // 쓰이지 않는다" 고 알리는데 이 자리만 보존 시간을 쓰면, FrameDropped 와 버림 계수기가 그만큼 늦고 그동안 조립 슬롯 하나가 묶인다.
        // 리센드가 켜진 쪽은 같은 시험 안의 대조군이다 — 같은 프레임이 보존 시간까지 기다리는 것을 함께 재서 시계가 살아 있음을 보인다.
        const int packetTimeoutMs = 300;
        const int offRetentionMs = 3000;
        const int onRetentionMs = 1500;

        var off = await MeasureSkippedFrameCloseAsync(resendEnabled: false, packetTimeoutMs, offRetentionMs);
        var on = await MeasureSkippedFrameCloseAsync(resendEnabled: true, packetTimeoutMs, onRetentionMs);

        // 닫는 시각은 "마지막 패킷 + 시한" 이하로 내려가지 않는다(하한은 과부하에도 흔들리지 않는다). 위쪽 상한은 보존 시간의 절반이라
        // 보존 시간을 쓰던 판(≈ 3000 ms)과 한참 떨어져 있다.
        Assert.True(off >= packetTimeoutMs - 10 && off < offRetentionMs / 2,
            $"resend off: the skipped frame closed after {off} ms; expected about PacketTimeoutMs ({packetTimeoutMs} ms), not FrameRetentionMs ({offRetentionMs} ms) "
            + "— GevStream.Receiver.cs CheckCompletion must give up on a skipped frame after PacketTimeoutMs when resend is off");
        Assert.True(on >= onRetentionMs - 10,
            $"resend on (control): the skipped frame closed after {on} ms; it should wait for FrameRetentionMs ({onRetentionMs} ms)");
    }

    /// <summary>지원하지 않는 종류의 리더 한 장만 보내고 FrameDropped 가 올 때까지의 시간을 잰다.</summary>
    private static async Task<long> MeasureSkippedFrameCloseAsync(bool resendEnabled, int packetTimeoutMs, int retentionMs)
    {
        var opt = StreamRig.DefaultOpt();
        opt.ResendEnabled = resendEnabled;
        opt.PacketTimeoutMs = packetTimeoutMs;
        opt.FrameRetentionMs = retentionMs;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        rig.Sender.SendShortLeader(1, GvspConst.PayloadChunkData, dataBytes: 12);   // 트레일러는 끝내 오지 않는다
        var diag = await rig.WaitDroppedAsync();
        sw.Stop();

        Assert.Equal(1UL, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Unsupported, diag.Reason);
        Assert.Equal(1, rig.Stream.Stats.FramesDroppedUnsupported);
        return sw.ElapsedMilliseconds;
    }

    [Fact]
    public async Task LargerLeaderGrowsTheBuffersLazily()
    {
        var opt = StreamRig.DefaultOpt();
        opt.PayloadSize = 512;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        var small = rig.Sender.SendFrame(1, 16, 16, Mono8, seed: 1);
        using (var f = await rig.ReceiveAsync())
        {
            Assert.True(f.Data.Span.SequenceEqual(small.Data));
        }

        var big = rig.Sender.SendFrame(2, 200, 100, Mono8, seed: 2);
        using (var f = await rig.ReceiveAsync())
        {
            Assert.Equal(20000, f.PayloadSize);
            Assert.True(f.Data.Span.SequenceEqual(big.Data));
        }

        var bigger = rig.Sender.SendFrame(3, 300, 100, Rgb8, seed: 3);
        using (var f = await rig.ReceiveAsync())
        {
            Assert.Equal(90000, f.PayloadSize);
            Assert.Equal(900, f.Stride);
            Assert.True(f.Data.Span.SequenceEqual(bigger.Data));
        }
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(0, rig.Stream.Stats.FramesDroppedError);
    }

    [Fact]
    public async Task AllInPacketDeliversTheFrame()
    {
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(9, 32, 8, Mono8, seed: 9);
        rig.Sender.SendAllIn(sent);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(9UL, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.Equal(1, frame.ExpectedPackets);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
    }

    [Fact]
    public async Task UnsupportedPayloadTypeIsDroppedAndCounted()
    {
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var jpeg = rig.Sender.BuildFrame(1, 32, 8, Mono8, seed: 1);
        jpeg.PayloadType = GvspConst.PayloadJpeg;
        rig.Sender.SendFrame(jpeg);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1UL, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Unsupported, diag.Reason);
        Assert.Equal(GvspConst.PayloadJpeg, diag.Code);
        Assert.Equal(1, rig.Stream.Stats.FramesDroppedUnsupported);
        Assert.False(rig.Stream.TryReceive(out _));

        // 그 뒤의 이미지 프레임은 정상.
        var image = rig.Sender.SendFrame(2, 32, 8, Mono8, seed: 2);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2UL, frame.FrameId);
        Assert.True(frame.Data.Span.SequenceEqual(image.Data));
    }

    [Fact]
    public async Task GarbageDatagramsAreIgnoredAndCounted()
    {
        await using var rig = new StreamRig();
        await rig.StartAsync();

        rig.Sender.SendRaw(new byte[] { 1, 2, 3 }, 3);
        var sent = rig.Sender.SendFrame(1, 32, 8, Mono8, seed: 1);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(1, rig.Stream.Stats.PacketsIgnored);
    }

    [Fact]
    public async Task DuplicatePacketsAreCountedNotReapplied()
    {
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 5);
        rig.Sender.SendPacket(sent, 0, GvspConst.StatusSuccess);
        rig.Sender.SendPacket(sent, 1, GvspConst.StatusSuccess);
        rig.Sender.SendPacket(sent, 1, GvspConst.StatusSuccess);
        for (uint id = 2; id <= sent.TrailerId; id++) rig.Sender.SendPacket(sent, id, GvspConst.StatusSuccess);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(1, rig.Stream.Stats.PacketsDuplicated);
    }

    [Fact]
    public async Task StaleLeaderOfClosedBlockIsADuplicateNotANewFrame()
    {
        // 리센드로 되살아난 리더가 원본 프레임이 이미 닫힌 뒤에 오는 경우 — 새 프레임을 열어 버퍼를 붙들고 가짜 불완전 프레임을 만들면 안 되고,
        // 그 리더가 조립 중인 다음 프레임의 꼬리를 "다 보내졌다" 로 확정해 아직 안 온 패킷을 요청하게 해서도 안 된다.
        var opt = StreamRig.DefaultOpt();
        // 침묵 규칙이 끼어들지 않게 — 이 테스트가 보는 것은 늦은 리더의 영향뿐이다. 두 묶음 사이의 정체는 러너에 달렸으므로
        // 문턱을 그보다 훨씬 크게 잡는다(200 ms 로는 밀린 러너에서 정체가 침묵으로 읽혀 안 온 꼬리를 요청하게 된다).
        opt.PacketTimeoutMs = 1000;
        opt.FrameRetentionMs = 3000; // 러너가 밀려 조립 중인 프레임이 포기되면 늦은 리더의 영향 대신 타임아웃을 보게 된다.
        await using var rig = new StreamRig(opt);
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        var first = rig.Sender.SendFrame(1, 64, 100, Mono8, seed: 1);
        using (var f1 = await rig.ReceiveAsync())
        {
            Assert.True(f1.Data.Span.SequenceEqual(first.Data));
        }

        // 둘째 프레임(8 패킷, 요청 예산 2)을 6 번까지 보낸 뒤 닫힌 블록 1 의 리더를 리센드 사본으로 한 번, 늦은 원본(같은 타임스탬프)으로 한 번
        // 다시 보내고, 유예가 지나도록 기다렸다가 나머지를 보낸다.
        var second = rig.Sender.BuildFrame(2, 64, 180, Mono8, seed: 2);
        Assert.Equal(8, second.PacketCount);
        for (uint id = 0; id <= 6; id++) rig.Sender.SendPacket(second, id, GvspConst.StatusSuccess);
        rig.Sender.SendPacket(first, 0, GvspConst.StatusPacketResend);
        rig.Sender.SendPacket(first, 0, GvspConst.StatusSuccess);
        await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsDuplicated >= 2 || rig.Resend.RequestCount >= 1);
        await Task.Delay(10, Ct);
        for (uint id = 7; id <= second.TrailerId; id++) rig.Sender.SendPacket(second, id, GvspConst.StatusSuccess);

        using (var f2 = await rig.ReceiveAsync())
        {
            Assert.Equal(2UL, f2.FrameId);
            Assert.True(f2.IsComplete);
            Assert.True(f2.Data.Span.SequenceEqual(second.Data));
        }
        Assert.Equal(0, rig.Resend.RequestCount);
        Assert.Equal(0, rig.Stream.Stats.ResendRequests);
        Assert.Equal(2, rig.Stream.Stats.PacketsDuplicated);

        // 셋째 프레임은 늦은 리더가 만든 유령 슬롯에 막히지 않고, 유령 슬롯이 불완전 프레임으로 세어지지도 않는다.
        var third = rig.Sender.SendFrame(3, 64, 100, Mono8, seed: 3);
        using (var f3 = await rig.ReceiveAsync())
        {
            Assert.Equal(3UL, f3.FrameId);
            Assert.True(f3.Data.Span.SequenceEqual(third.Data));
        }
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(0, rig.DroppedCount);
        Assert.Equal(3, rig.Stream.Stats.FramesCompleted);
    }

    [Fact]
    public async Task RestartedBlockNumberingOpensNewFrames()
    {
        // 촬영을 다시 시작하면 블록 번호를 1 부터 다시 세는 장치가 있다 — 방금 닫은 블록과 번호가 같아도 타임스탬프가 다르면 새 프레임이다.
        await using var rig = new StreamRig();
        await rig.StartAsync();

        for (ulong id = 1; id <= 2; id++)
        {
            rig.Sender.SendFrame(id, 32, 8, Mono8, seed: (byte)id);
            using var f = await rig.ReceiveAsync();
            Assert.Equal(id, f.FrameId);
        }

        var restarted = rig.Sender.BuildFrame(1, 32, 8, Mono8, seed: 7, timestamp: 777_000);
        rig.Sender.SendFrame(restarted);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(1UL, frame.FrameId);
        Assert.Equal(777_000UL, frame.Timestamp);
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(restarted.Data));
        Assert.Equal(3, rig.Stream.Stats.FramesCompleted);
        Assert.Equal(0, rig.Stream.Stats.PacketsDuplicated);
        Assert.Equal(0, rig.Stream.Stats.PacketsIgnored);
    }

    [Fact]
    public async Task RestartedBlockAfterALeaderOnlyFrameIsAssembledWithItsOwnLeader()
    {
        // 노출이 긴 촬영에서는 리더가 먼저 오므로 리더만 온 가장 새 프레임은 보존 시간이 지나도 기다린다. 그 사이 장치가 그 블록을
        // 버리고(정지) 촬영을 다시 시작해 같은 블록 번호로 새 리더를 보내면, 그 리더를 중복으로 버리고 새 페이로드를 옛 리더의
        // 슬롯에 실어 옛 타임스탬프·기하로 완성 처리하게 된다 — 틀린 값이 정상처럼 보인다.
        var opt = StreamRig.DefaultOpt();
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 옛 리더는 64x100, 새 프레임은 128x50 — 바이트 수가 같아(6400) 옛 기하로 실어도 "다 받았다" 가 된다.
        var aborted = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 0x10, timestamp: 111_000);
        rig.Sender.SendPacket(aborted, 0, GvspConst.StatusSuccess);
        await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsReceived >= 1);
        // 재요청 간격과 보존 시간을 둘 다 넘겨 쉰다 — 리더만 온 프레임은 그래도 붙들려 있다.
        await Task.Delay(opt.FrameRetentionMs + 5 * opt.PacketTimeoutMs, Ct);

        var restarted = rig.Sender.BuildFrame(1, 128, 50, Mono8, seed: 0x20, timestamp: 222_000);
        Assert.Equal(aborted.Data.Length, restarted.Data.Length);
        rig.Sender.SendFrame(restarted);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(1UL, frame.FrameId);
        Assert.Equal(222_000UL, frame.Timestamp);
        Assert.Equal(128, frame.Width);
        Assert.Equal(50, frame.Height);
        Assert.Equal(128, frame.Stride);
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(restarted.Data));
        Assert.Equal(0, rig.Stream.Stats.PacketsDuplicated);

        // 버려진 옛 프레임은 조용히 사라지지 않는다 — 불완전 한 장으로 세고 알린다.
        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1UL, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);
        Assert.Equal(1, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(1, rig.Stream.Stats.FramesCompleted);
        Assert.False(rig.Stream.TryReceive(out _));
    }

    [Fact]
    public async Task LateCopyOfALeaderOnlyFramesLeaderIsStillADuplicate()
    {
        // 위 규칙의 반대편: 리더만 온 프레임에 같은 리더(같은 타임스탬프)가 한참 뒤에 다시 오면 새 촬영이 아니라 늦은 사본이다.
        // 그것으로 프레임을 다시 열면 버리지 말아야 할 프레임을 불완전으로 세게 된다.
        var opt = StreamRig.DefaultOpt();
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 0x30, timestamp: 333_000);
        rig.Sender.SendPacket(sent, 0, GvspConst.StatusSuccess);
        await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsReceived >= 1);
        await Task.Delay(5 * opt.PacketTimeoutMs, Ct);
        rig.Sender.SendPacket(sent, 0, GvspConst.StatusSuccess);
        await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsDuplicated >= 1);
        for (uint id = 1; id <= sent.TrailerId; id++) rig.Sender.SendPacket(sent, id, GvspConst.StatusSuccess);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(333_000UL, frame.Timestamp);
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(1, rig.Stream.Stats.PacketsDuplicated);
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(0, rig.DroppedCount);
    }

    [Fact]
    public async Task DuplicateAllInPacketIsCountedNotReassembled()
    {
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(9, 32, 8, Mono8, seed: 9);
        rig.Sender.SendAllIn(sent);
        using (var f = await rig.ReceiveAsync())
        {
            Assert.Equal(9UL, f.FrameId);
        }

        // 닫힌 블록의 올인 패킷이 다시 오면 중복일 뿐이다 — 같은 프레임이 두 번 전달되면 안 된다.
        rig.Sender.SendAllIn(sent);
        var next = rig.Sender.BuildFrame(10, 32, 8, Mono8, seed: 10);
        rig.Sender.SendAllIn(next);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(10UL, frame.FrameId);
        Assert.True(frame.Data.Span.SequenceEqual(next.Data));
        Assert.Equal(1, rig.Stream.Stats.PacketsDuplicated);
        Assert.Equal(2, rig.Stream.Stats.FramesCompleted);
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
        Assert.False(rig.Stream.TryReceive(out _));
    }

    [Fact]
    public async Task UnavailablePacketAbandonsOnlyThatHole()
    {
        // 장치가 어떤 패킷을 0x800C 로 거절해도 다른 구멍의 리센드는 계속되어야 한다 — 프레임 전체를 포기하면 살릴 수 있는 패킷까지 버린다.
        // 그러려면 거절이 "7 을 요청할지 정하기 전" 에 처리돼야 한다. 그래서 프레임을 두 번에 나눠 보내되, 뒷부분은 장치가 3 을 거절한
        // 직후 리센드 대역이 수신 스레드 안에서 이어 보낸다 — 테스트 스레드가 두 부분 사이에 끼면 그 정체가 곧 이 프레임의 침묵이 되고,
        // 러너가 밀린 만큼 아래 두 가지가 잘못 일어난다: 재요청 간격만큼 조용하면 수신기는 꼬리가 다 왔다고 보아 아직 보내지도 않은
        // 7..20 을 통째로 요청해 예산을 태우고, 보존 시간이 지나면 멀쩡한 프레임을 포기한다. 둘 다 여기서 볼 것이 아니다.
        var opt = StreamRig.DefaultOpt();
        opt.DeliverIncompleteFrames = true;
        // 수신 스레드가 선점당한 침묵을 "장치가 이 프레임을 그만 보냈다" 로 읽지 않을 만큼 넉넉하게(형제 테스트와 같은 값).
        // 보존 시간은 그보다 훨씬 길어야 한다 — 두 부분 사이의 정체가 보존 시간을 넘기면 멀쩡한 프레임이 포기된다.
        opt.PacketTimeoutMs = 200;
        opt.FrameRetentionMs = 3000;
        await using var rig = new StreamRig(opt);
        rig.Resend.UnavailableIds.Add(3);
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(1, 244, 120, Mono8, seed: 9);
        Assert.Equal(20, sent.PacketCount);
        rig.Sender.Drop.Add((1, 3));
        rig.Sender.Drop.Add((1, 7));

        var hasSentRest = 0;
        rig.Resend.AfterRequest = r =>
        {
            if (r.BlockId != 1 || r.First > 3 || r.Last < 3) return;
            if (Interlocked.Exchange(ref hasSentRest, 1) != 0) return;
            for (uint id = 7; id <= sent.TrailerId; id++) rig.Sender.SendPacket(sent, id, GvspConst.StatusSuccess);
        };

        // 3 이 빠진 앞부분. 3 의 리센드 요청에 장치가 0x800C 로 답하고, 그 답 직후 7 이 빠진 나머지가 이어서 나간다.
        for (uint id = 0; id <= 6; id++) rig.Sender.SendPacket(sent, id, GvspConst.StatusSuccess);

        // 정상 동작이면 마지막 패킷 뒤 재요청 간격 하나(200 ms)면 닫힌다. 넉넉한 대기는 거절이 눌어붙지 않는 회귀를
        // 기다림이 아니라 아래 단정으로 드러내기 위한 것이다(그 경우 프레임은 보존 시간을 다 채우고 나온다).
        using var frame = await rig.ReceiveAsync(5000);
        Assert.False(frame.IsComplete);
        Assert.Equal(1, frame.MissingPackets);
        Assert.Equal(20, frame.ExpectedPackets);

        // 7 은 리센드로 메워졌고 3 자리만 0 이다.
        var expected = (byte[])sent.Data.Clone();
        Array.Clear(expected, 2 * sent.DataBytesPerPacket, sent.DataBytesPerPacket);
        Assert.True(frame.Data.Span.SequenceEqual(expected));

        Assert.Equal(1, rig.Stream.Stats.ResendRecovered);
        Assert.Contains(rig.Resend.Requests, r => r.BlockId == 1 && r.First <= 7 && r.Last >= 7);
        // 이 프레임의 구멍은 3 과 7 둘뿐이고 서로 떨어져 있다 — 요청은 전부 한 패킷짜리여야 한다.
        // 받은 적도 없고 보내지지도 않은 id 까지 묶어 묻는 것은 회선과 예산을 태우는 회귀다.
        Assert.All(rig.Resend.Requests.Where(r => r.BlockId == 1), r => Assert.Equal(r.First, r.Last));
        // 장치가 못 준다고 답한 구멍은 다시 묻지 않는다 — 재요청이 되풀이되면 요청 예산과 회선을 태운다.
        Assert.Equal(1, rig.Resend.Requests.Count(r => r.BlockId == 1 && r.First <= 3 && r.Last >= 3));
        Assert.Equal(1, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(1, rig.Stream.Stats.PacketsMissing);
        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);
        Assert.Equal(1, diag.MissingPackets);
    }

    [Fact]
    public async Task RetryAnswersThatNeverFillTheHoleStillLetTheFrameGoAtRetention()
    {
        // "조금 있다 다시 물어라"(0x8014) 로만 답하는 장치 — 그 구멍은 포기 표시가 되지 않으므로 재요청 간격마다 계속 다시 요청된다.
        // 오류 답신이 프레임의 마지막 패킷 시각을 밀어 준다면 보존 시간이 영영 지나지 않아 프레임과 버퍼가 갇힌다.
        // 프레임은 보존 시간 안에 포기되고(진단 하나) 다음 프레임이 막힘 없이 나와야 한다.
        await using var rig = new StreamRig();
        rig.Resend.Behaviour = TestResendPort.Mode.Unavailable;
        rig.Resend.UnavailableStatus = 0x8014;   // PACKET_TEMPORARILY_UNAVAILABLE — 다시 물어야 하는 답이라 구멍을 포기하지 않는다
        await rig.StartAsync();

        rig.Sender.Drop.Add((1, 4));
        var lost = rig.Sender.SendFrame(1, 244, 120, Mono8, seed: 1);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1UL, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);
        Assert.Equal(1, diag.MissingPackets);
        Assert.Equal(lost.PacketCount, diag.ExpectedPackets);
        Assert.True(rig.Stream.Stats.ErrorPackets >= 1);

        // 버퍼가 풀로 돌아왔으니 다음 프레임은 그대로 나온다.
        var next = rig.Sender.SendFrame(2, 64, 32, Mono8, seed: 2);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2UL, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(next.Data));
    }

    [Fact]
    public async Task RepeatedRequestsForTheSameHoleDoNotSpendTheBudgetTwice()
    {
        // 요청 예산(PacketRequestRatio)은 "리센드를 요청해 본 서로 다른 패킷 수" 의 상한이다. 장치의 리센드 응답이 재요청 간격보다
        // 느린 링크에서는 같은 구멍을 다시 묻게 되는데, 그 재요청까지 예산에 얹으면 손실률이 예산 안에 있는 프레임(여기서는 20 %)까지 버려진다.
        var opt = StreamRig.DefaultOpt();
        // 첫 요청에 답이 없는 동안 프레임이 보존 시간으로 닫히면 예산을 보기 전에 끝난다 — 여기서 보는 것은 보존 시간이 아니라 예산이다.
        opt.FrameRetentionMs = 3000;
        // 예산이 터져 포기된 프레임도 받아 봐야 "왜 실패했는지" 가 기다림이 아니라 프레임 내용으로 드러난다.
        opt.DeliverIncompleteFrames = true;
        await using var rig = new StreamRig(opt);
        rig.Resend.Behaviour = TestResendPort.Mode.Never;   // 첫 요청 묶음에는 답하지 않는다 — 응답이 느린 장치.
        await rig.StartAsync();

        // 20 패킷 중 4 개(20 %) 손실 — 서로 다른 패킷 기준으로는 예산 ceil(20 × 0.25) = 5 안이다.
        foreach (var id in new uint[] { 3, 7, 11, 15 }) rig.Sender.Drop.Add((1, id));
        var sent = rig.Sender.SendFrame(1, 244, 120, Mono8, seed: 4);
        Assert.Equal(20, sent.PacketCount);

        // 네 구멍이 한 번씩 요청된 뒤에야 장치가 답하기 시작한다 — 그 다음 재요청 라운드가 네 구멍을 모두 메워야 한다.
        await rig.WaitUntilAsync(() => rig.Resend.RequestCount >= 4);
        rig.Resend.Behaviour = TestResendPort.Mode.Resend;

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete,
            $"frame {frame.FrameId} came out with {frame.MissingPackets} missing packet(s) after {rig.Resend.RequestCount} resend requests: "
            + "GevStream.Receiver.cs SendResend must charge the request budget per distinct packet, not per request.");
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(4, rig.Stream.Stats.ResendRecovered);
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
        // 재요청이 실제로 있었어야 이 테스트가 예산을 확인한 것이다 — 첫 라운드로 끝났다면 아무것도 보지 못했다.
        Assert.True(rig.Resend.RequestCount > 4, $"expected the four holes to be asked again, got {rig.Resend.RequestCount} requests");
    }

    [Fact]
    public async Task MalformedTrailerDoesNotPinTheFrameOpen()
    {
        var opt = StreamRig.DefaultOpt();
        // 리더 없이 페이로드만 받은 프레임은 보존 시간이 지나면 불완전으로 닫힌다 — 시험 스레드가 밀려도 리더를 돌려줄 때까지
        // 열려 있게 넉넉히 둔다. 정상 흐름에서는 리더가 돌아오는 즉시 완성되므로 이 값만큼 기다리지 않는다.
        opt.FrameRetentionMs = 5000;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 첫 프레임으로 버퍼 크기를 알게 한 뒤, 둘째 프레임은 리더 없이 페이로드를 보내고 id 0 짜리 깨진 트레일러를 붙인다.
        // 리더는 리센드로 돌아오며, 깨진 트레일러가 리더의 패킷 수 계산을 막지 않아야 프레임이 닫힌다.
        var first = rig.Sender.SendFrame(1, 64, 100, Mono8, seed: 1);
        using (var f1 = await rig.ReceiveAsync())
        {
            Assert.True(f1.Data.Span.SequenceEqual(first.Data));
        }

        // 깨진 트레일러는 **열려 있는 프레임에** 닿아야 이 시험이 뜻을 가진다. 리센드 답을 붙들지 않으면 송신이 유예(2 ms)보다 늦게
        // 트레일러에 닿는 순간 리더가 먼저 돌아와 프레임이 닫히고, 깨진 트레일러는 닫힌 블록의 늦은 트레일러로 조용히 지나간다
        // (15 ms 멈춤으로 재현). 그래서 리더 요청에는 답하지 않다가, 수신기가 깨진 트레일러를 거른 것을 본 뒤에 답한다.
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        var second = rig.Sender.BuildFrame(2, 64, 100, Mono8, seed: 2);
        rig.Sender.Drop.Add((2, 0));
        for (uint id = 1; id <= (uint)second.PacketCount; id++) rig.Sender.SendPacket(second, id, GvspConst.StatusSuccess);
        rig.Sender.SendTrailer(second, packetId: 0);

        await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsIgnored >= 1);
        Assert.Equal(1, rig.Stream.Stats.FramesCompleted);  // 둘째 프레임은 아직 열려 있다 — 깨진 트레일러를 열린 프레임에서 걸렀다
        rig.Resend.Behaviour = TestResendPort.Mode.Resend;  // 다음 재요청(재요청 간격 뒤)이 리더를 받아 온다

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2UL, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.Equal(second.PacketCount, frame.ExpectedPackets);
        Assert.True(frame.Data.Span.SequenceEqual(second.Data));
        Assert.Equal(1, rig.Stream.Stats.PacketsIgnored);   // 걸러진 것은 깨진 트레일러 하나뿐이다
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
    }

    [Fact]
    public async Task UnsupportedContentTypeIsLoggedOncePerValueEvenAbove31()
    {
        // 콘텐츠 타입은 0..127 — 32 이상의 값이 패킷마다 경고를 남기면 핫패스에서 문자열이 만들어지고 로그가 넘친다.
        var warnings = 0;
        var previousSink = GevLog.Sink;
        GevLog.Sink = (level, source, message, ex) =>
        {
            if (level == GevLogLevel.Warn && message.Contains("content type 100")) Interlocked.Increment(ref warnings);
        };
        try
        {
            await using var rig = new StreamRig();
            await rig.StartAsync();

            for (uint i = 1; i <= 50; i++) rig.Sender.SendHeaderOnly(1, contentType: 100, packetId: i);
            await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsUnsupported >= 50);
            Assert.Equal(1, Volatile.Read(ref warnings));
        }
        finally
        {
            GevLog.Sink = previousSink;
        }
    }

    [Fact]
    public async Task LeaderOfARestartedBlockDoesNotCondemnANewerFrameInFlight()
    {
        // 단일 프레임 촬영을 되풀이하는 장치는 매번 블록 1 부터 다시 센다 — 조립 중인 블록 2 보다 번호가 낮은 리더가 들어온다.
        // 새 블록이 시작됐다는 사실은 "그보다 오래된 블록은 다 보내졌다" 는 근거이지만, 새로 여는 블록이 조립 중인 것보다
        // 오래됐다면 아무것도 확정하지 못한다. 그런데도 꼬리를 확정하면 아직 오지도 않은 패킷이 진짜 유실로 세어져
        // 요청 예산을 태우고, 패킷 하나 잃지 않은 프레임이 통째로 버려진다.
        var opt = StreamRig.DefaultOpt();
        opt.FrameRetentionMs = 3000;   // 이 테스트가 보는 것은 블록 순서 판정이지 보존 시간이 아니다.
        await using var rig = new StreamRig(opt);
        rig.Resend.Behaviour = TestResendPort.Mode.Never;   // 프레임을 완성하는 것은 장치가 이어 보내는 원본뿐이다.
        await rig.StartAsync();

        // 블록 2 를 절반만 보낸 직후(침묵이 끼어들 틈 없이) 블록 1 의 리더를 보낸다.
        var inFlight = rig.Sender.BuildFrame(2, 64, 180, Mono8, seed: 31);
        Assert.Equal(8, inFlight.PacketCount);
        for (uint id = 0; id <= 3; id++) rig.Sender.SendPacket(inFlight, id, GvspConst.StatusSuccess);
        var restarted = rig.Sender.BuildFrame(1, 64, 32, Mono8, seed: 32, timestamp: 555_000);
        rig.Sender.SendPacket(restarted, 0, GvspConst.StatusSuccess);
        await rig.WaitUntilAsync(() => rig.Stream.Stats.PacketsReceived >= 5);

        // 예산(8 × 0.25 = 2)을 넘겨 버려졌다면 재요청 간격 몇 번 안에 드러난다.
        await Task.Delay(6 * opt.PacketTimeoutMs, Ct);
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
        Assert.Equal(0, rig.DroppedCount);

        // 장치가 블록 2 의 나머지를 마저 보내면 그대로 완성된다.
        for (uint id = 4; id <= inFlight.TrailerId; id++) rig.Sender.SendPacket(inFlight, id, GvspConst.StatusSuccess);
        using (var frame = await rig.ReceiveAsync())
        {
            Assert.Equal(2UL, frame.FrameId);
            Assert.True(frame.IsComplete);
            Assert.True(frame.Data.Span.SequenceEqual(inFlight.Data));
        }

        // 다시 시작한 블록 1 도 이어서 정상으로 온다.
        for (uint id = 1; id <= restarted.TrailerId; id++) rig.Sender.SendPacket(restarted, id, GvspConst.StatusSuccess);
        using (var frame = await rig.ReceiveAsync())
        {
            Assert.Equal(1UL, frame.FrameId);
            Assert.Equal(555_000UL, frame.Timestamp);
            Assert.True(frame.IsComplete);
            Assert.True(frame.Data.Span.SequenceEqual(restarted.Data));
        }
        Assert.Equal(2, rig.Stream.Stats.FramesCompleted);
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
    }

    [Fact]
    public async Task TrailerWithASmallerHeightShrinksTheFrame()
    {
        // 가변 높이 촬영: 리더는 최대 줄 수를 알리고 실제 줄 수는 트레일러가 알린다. 리더 값을 그대로 쓰면
        // 오지도 않은 줄까지 유효 바이트로 내보내 소비자가 이전 프레임의 픽셀이 남은 영역을 이미지로 읽는다.
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(1, 64, 50, Mono8, seed: 21);
        sent.LeaderHeight = 100;                 // 리더는 100 줄, 트레일러는 실제 50 줄
        Assert.Equal(3, sent.PacketCount);
        rig.Sender.SendFrame(sent);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.Equal(64, frame.Width);
        Assert.Equal(50, frame.Height);
        Assert.Equal(64, frame.Stride);
        Assert.Equal(sent.Data.Length, frame.PayloadSize);
        Assert.Equal(3, frame.ExpectedPackets);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(0, rig.Stream.Stats.ResendRequests);
    }

    /// <summary>
    /// 풀 버퍼 하나를 정상 프레임으로 한 번 채워 둔 스트림 — 다음 프레임이 같은 버퍼를 받으므로, 덜 온 자리에 이전 프레임의
    /// 바이트가 남아 있으면 눈에 보인다(새 버퍼는 0 이라 그 오염이 가려진다).
    /// </summary>
    private static async Task<(StreamRig Rig, GvspTestSender.SynthFrame Previous)> StartWithDirtyBufferAsync(bool deliverIncomplete, Action<GevStreamOpt>? configure = null)
    {
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 1;
        opt.DeliverIncompleteFrames = deliverIncomplete;
        configure?.Invoke(opt);
        var rig = new StreamRig(opt);
        await rig.StartAsync();
        var previous = rig.Sender.SendFrame(1, 64, 100, Mono8, seed: 0xAA);
        using (var f = await rig.ReceiveAsync()) Assert.True(f.IsComplete);
        return (rig, previous);
    }

    [Fact]
    public async Task BlockCutShortByAnEarlyTrailerIsIncompleteNotStale()
    {
        // 장치가 블록을 중간에 끊고(정지 순간 등) 낮은 id 의 트레일러를 보냈다. 트레일러의 패킷 수만 믿으면 "다 받았다" 가 되어,
        // 리더가 알린 바이트 중 안 온 꼬리에 이전 프레임의 픽셀이 남은 채 완성으로 나간다.
        var (rig, previous) = await StartWithDirtyBufferAsync(deliverIncomplete: true);
        await using (rig)
        {
            var cut = rig.Sender.BuildFrame(2, 64, 100, Mono8, seed: 0x11);
            Assert.Equal(5, cut.PacketCount);
            var d = cut.DataBytesPerPacket;
            rig.Sender.SendPacket(cut, 0, GvspConst.StatusSuccess);
            rig.Sender.SendPacket(cut, 1, GvspConst.StatusSuccess);
            rig.Sender.SendPacket(cut, 2, GvspConst.StatusSuccess);
            rig.Sender.SendTrailer(cut, 3);      // 높이는 리더 그대로 — 가변 높이 축소가 아니다

            using var frame = await rig.ReceiveAsync();
            Assert.Equal(2UL, frame.FrameId);
            Assert.True(frame.Data.Span.Slice(0, 2 * d).SequenceEqual(cut.Data.AsSpan(0, 2 * d)));
            var tail = frame.Data.Span.Slice(2 * d, cut.Data.Length - 2 * d);
            // 검사기가 살아 있는지: 고치기 전에는 이 꼬리가 이전 프레임 바이트다
            Assert.False(tail.SequenceEqual(previous.Data.AsSpan(2 * d, cut.Data.Length - 2 * d)), "tail still holds the previous frame");
            Assert.True(tail.SequenceEqual(new byte[tail.Length]), "unreceived tail must be zeroed");
            Assert.False(frame.IsComplete);
            Assert.Equal(5, frame.ExpectedPackets);
            Assert.Equal(3, frame.MissingPackets);

            var diag = await rig.WaitDroppedAsync();
            Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);
            Assert.Equal(3, diag.MissingPackets);
            Assert.Equal(1, rig.Stream.Stats.FramesCompleted);
            Assert.Equal(1, rig.Stream.Stats.FramesIncomplete);
        }
    }

    [Fact]
    public async Task BlockCutInsideAPacketZeroesTheGapAfterTheShortLastPayload()
    {
        // 끊긴 블록의 마지막 페이로드가 짧다(패킷 가운데서 끊겼다). 그 패킷 자리의 나머지는 장치가 쓰지 않았으므로
        // 트레일러가 약속한 패킷 수 × 패킷 크기가 아니라 실제로 받은 끝부터 비워야 이전 프레임 바이트가 남지 않는다.
        var (rig, previous) = await StartWithDirtyBufferAsync(deliverIncomplete: true);
        await using (rig)
        {
            var cut = rig.Sender.BuildFrame(2, 64, 100, Mono8, seed: 0x11);
            var d = cut.DataBytesPerPacket;
            rig.Sender.SendPacket(cut, 0, GvspConst.StatusSuccess);
            rig.Sender.SendPacket(cut, 1, GvspConst.StatusSuccess);
            rig.Sender.SendPacket(cut, 2, GvspConst.StatusSuccess);
            rig.Sender.SendPayloadWithArbitraryId(2, 3, 100);   // 셋째 패킷은 100 바이트에서 끊겼다
            rig.Sender.SendTrailer(cut, 4);

            using var frame = await rig.ReceiveAsync();
            Assert.False(frame.IsComplete);
            var receivedEnd = 2 * d + 100;
            var gap = frame.Data.Span.Slice(receivedEnd, 3 * d - receivedEnd);
            Assert.False(gap.SequenceEqual(previous.Data.AsSpan(receivedEnd, gap.Length)), "gap still holds the previous frame");
            Assert.True(frame.Data.Span.Slice(receivedEnd, cut.Data.Length - receivedEnd).SequenceEqual(new byte[cut.Data.Length - receivedEnd]));
        }
    }

    [Fact]
    public async Task BlockCutShortIsDroppedWhenIncompleteFramesAreNotDelivered()
    {
        // 기본 설정(불완전 프레임 안 받음)에서는 끊긴 블록이 나가지 않고, 뒤 프레임을 보존 시간만큼 막지도 않는다.
        var (rig, _) = await StartWithDirtyBufferAsync(deliverIncomplete: false);
        await using (rig)
        {
            var cut = rig.Sender.BuildFrame(2, 64, 100, Mono8, seed: 0x11);
            rig.Sender.SendPacket(cut, 0, GvspConst.StatusSuccess);
            rig.Sender.SendPacket(cut, 1, GvspConst.StatusSuccess);
            rig.Sender.SendTrailer(cut, 2);

            var next = rig.Sender.SendFrame(3, 64, 100, Mono8, seed: 0x33);
            using var frame = await rig.ReceiveAsync();
            Assert.Equal(3UL, frame.FrameId);
            Assert.True(frame.IsComplete);
            Assert.True(frame.Data.Span.SequenceEqual(next.Data));
            Assert.Equal(1, rig.Stream.Stats.FramesIncomplete);
        }
    }

    [Fact]
    public async Task BlockCutBeforeItsFirstPayloadClosesAtOnceAsIncomplete()
    {
        // 장치가 리더만 보내고 곧바로 id 1 의 트레일러로 블록을 끊었다(첫 페이로드 전에 멈췄다). 트레일러가 약속한 페이로드는 0 개라
        // 더 올 것이 없다. 패킷 수 0 을 "아직 모름" 으로 읽으면 보존 시간 내내 기다리며 뒤 프레임을 막고, 불완전 프레임을 받겠다고 한
        // 소비자에게도 끝내 나가지 않는다 — 한 패킷이라도 받은 뒤 끊긴 블록과 다르게 다룰 까닭이 없다.
        var (rig, previous) = await StartWithDirtyBufferAsync(deliverIncomplete: true, opt => opt.FrameRetentionMs = 30_000);
        await using (rig)
        {
            var cut = rig.Sender.BuildFrame(2, 64, 100, Mono8, seed: 0x11);
            Assert.Equal(5, cut.PacketCount);
            rig.Sender.SendPacket(cut, 0, GvspConst.StatusSuccess);
            rig.Sender.SendTrailer(cut, 1);

            // 보존 시간(30 초)까지 기다린다면 여기서 시한을 넘긴다.
            using var frame = await rig.ReceiveAsync(3000);
            Assert.Equal(2UL, frame.FrameId);
            Assert.False(frame.IsComplete);
            Assert.Equal(cut.Data.Length, frame.PayloadSize);
            Assert.Equal(5, frame.ExpectedPackets);
            Assert.Equal(5, frame.MissingPackets);
            // 검사기가 살아 있는지: 버퍼는 이전 프레임으로 더럽혀 두었다 — 비우지 않으면 그 바이트가 그대로 나온다.
            Assert.False(frame.Data.Span.SequenceEqual(previous.Data), "the frame still holds the previous frame");
            Assert.True(frame.Data.Span.SequenceEqual(new byte[cut.Data.Length]), "nothing of this block arrived, so the frame must be all zeros");

            var diag = await rig.WaitDroppedAsync();
            Assert.Equal(2UL, diag.FrameId);
            Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);
            Assert.Equal(5, diag.MissingPackets);
            Assert.Equal(1, rig.Stream.Stats.FramesIncomplete);
        }
    }

    [Fact]
    public async Task PacketStrideThatShrinksAfterBytesWereLaidDropsTheFrameAsError()
    {
        // 리더와 첫 페이로드(id 1)가 함께 유실되면 패킷당 바이트를 배울 근거가 없어 협상값(SCPS)에서 구한 간격으로 자리를 정한다.
        // 장치가 그보다 짧은 패킷을 보내면 먼저 온 id 2.. 는 넓은 간격에 실리고, 리센드로 돌아온 id 1 이 진짜 간격을 알려 줄 때는
        // 이미 늦었다. 그 뒤로 받은 패킷 수는 다 차고 받은 끝(가장 먼 끝)도 리더 크기를 넘으므로, 그대로 두면 어긋난 바이트와
        // 그 사이에 남은 이전 프레임 바이트가 완성으로 나간다. 이미 실은 바이트는 옮길 수 없으니 프레임을 오류로 버려야 한다.
        // 버퍼를 넉넉히 잡아 둔다 — 넓은 간격에서 뒤쪽 id 가 버퍼 밖으로 밀려나면 예산 초과로 버려져 이 경로에 오지 않는다.
        var (rig, _) = await StartWithDirtyBufferAsync(deliverIncomplete: false, opt => opt.PayloadSize = 16384);
        await using (rig)
        {
            rig.Sender.PacketSize = 1036;
            var sent = rig.Sender.BuildFrame(2, 64, 100, Mono8, seed: 0x5A);
            Assert.True(sent.DataBytesPerPacket < GvspConst.DataBytesPerPacket(rig.Stream.PacketSize, extendedIds: false));
            Assert.Equal(7, sent.PacketCount);
            rig.Sender.Drop.Add((2, 0));
            rig.Sender.Drop.Add((2, 1));
            rig.Sender.SendFrame(sent);

            await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames > 0 || rig.DroppedCount > 0);
            if (rig.Stream.TryReceive(out var delivered) && delivered is not null)
            {
                using (delivered)
                {
                    Assert.False(delivered.IsComplete && !delivered.Data.Span.SequenceEqual(sent.Data),
                        $"block {delivered.FrameId} was delivered complete with its bytes laid at the wrong packet stride");
                }
            }
            var diag = await rig.WaitDroppedAsync();
            Assert.Equal(2UL, diag.FrameId);
            Assert.Equal(GevFrameDropReason.Error, diag.Reason);
            Assert.Equal(1, rig.Stream.Stats.FramesDroppedError);
            Assert.Equal(1, rig.Stream.Stats.FramesCompleted);   // 더럽히려고 보낸 첫 프레임뿐

            // 리더가 함께 오는 프레임은 실기 전에 id 1 로 간격을 배운다 — 같은 짧은 패킷이어도 그대로 완성되고, 버퍼도 풀로 돌아와 있다.
            var next = rig.Sender.SendFrame(3, 64, 100, Mono8, seed: 0x33);
            using var frame = await rig.ReceiveAsync();
            Assert.Equal(3UL, frame.FrameId);
            Assert.True(frame.IsComplete);
            Assert.True(frame.Data.Span.SequenceEqual(next.Data));
        }
    }

    [Fact]
    public async Task PacketStrideThatGrowsAfterBytesWereLaidDropsTheChunkFrameAsError()
    {
        // 반대 방향: 장치가 협상값보다 긴 패킷을 보내는데 첫 페이로드(id 1)가 유실되고 짧은 마지막 패킷이 먼저 왔다. 마지막 패킷은
        // 협상값 간격에 실리고, 리센드로 돌아온 id 1 이 더 긴 간격을 알려 줄 때는 이미 늦었다. 청크가 붙은 프레임은 리더가 크기를
        // 알려 주지 못해 완성을 패킷 수로만 가리므로, 그대로 두면 마지막 패킷(청크 꼬리)을 잃은 프레임이 완성으로 나간다.
        var (rig, _) = await StartWithDirtyBufferAsync(deliverIncomplete: false);
        await using (rig)
        {
            rig.Sender.PacketSize = 3000;
            var sent = rig.Sender.BuildChunkFrame(2, 64, 50, Mono8, chunkBytes: 400, seed: 0x5A);
            Assert.True(sent.DataBytesPerPacket > GvspConst.DataBytesPerPacket(rig.Stream.PacketSize, extendedIds: false));
            Assert.Equal(2, sent.PacketCount);
            rig.Sender.Drop.Add((2, 1));
            rig.Sender.SendFrame(sent);

            await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames > 0 || rig.DroppedCount > 0);
            if (rig.Stream.TryReceive(out var delivered) && delivered is not null)
            {
                using (delivered)
                {
                    Assert.False(delivered.IsComplete && !delivered.Data.Span.SequenceEqual(sent.Data),
                        $"block {delivered.FrameId} was delivered complete with {delivered.PayloadSize} of {sent.Data.Length} bytes, its last packet laid at the wrong packet stride");
                }
            }
            var diag = await rig.WaitDroppedAsync();
            Assert.Equal(2UL, diag.FrameId);
            Assert.Equal(GevFrameDropReason.Error, diag.Reason);
            Assert.Equal(1, rig.Stream.Stats.FramesDroppedError);
        }
    }

    [Fact]
    public async Task LeaderRecoveredAfterAShorterTrailerStillShrinksTheFrame()
    {
        // 가변 높이 프레임의 리더가 유실돼 리센드로 트레일러 뒤에 왔다. 트레일러가 알린 실제 줄 수를 리더를 적용할 때도 써야
        // 리더의 최대 줄 수로 크기를 잡아 덜 온 것처럼(또는 이전 픽셀이 남은 채) 닫지 않는다.
        var (rig, _) = await StartWithDirtyBufferAsync(deliverIncomplete: false);
        await using (rig)
        {
            var sent = rig.Sender.BuildFrame(2, 64, 50, Mono8, seed: 0x22);
            sent.LeaderHeight = 100;
            rig.Sender.Drop.Add((2, 0));
            rig.Sender.SendFrame(sent);

            using var frame = await rig.ReceiveAsync();
            Assert.Equal(2UL, frame.FrameId);
            Assert.True(frame.IsComplete);
            Assert.Equal(50, frame.Height);
            Assert.Equal(sent.Data.Length, frame.PayloadSize);
            Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
            Assert.True(rig.Stream.Stats.ResendRequests > 0);   // 리더는 정말 리센드로 왔다
        }
    }

    [Fact]
    public async Task VariableHeightOfABitPackedFormatKeepsItsPayloadSize()
    {
        // 줄이 바이트 경계에서 끝나지 않는 패킹(Mono12p 홀수 폭)은 줄 간격이 없어 Stride 가 0 이다 — 줄 간격 × 줄 수로
        // 크기를 다시 계산하면 0 이 된다. 픽셀 포맷 규칙으로 실제 줄 수의 바이트를 구해야 한다.
        const uint Mono12p = 0x010C0047;
        await using var rig = new StreamRig();
        await rig.StartAsync();

        var sent = rig.Sender.BuildFrame(1, 63, 50, Mono12p, seed: 5);
        sent.LeaderHeight = 100;
        rig.Sender.SendFrame(sent);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.Equal(0, frame.Stride);
        Assert.Equal(50, frame.Height);
        Assert.Equal(sent.Data.Length, frame.PayloadSize);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
    }

    [Fact]
    public async Task PayloadLongerThanTheNegotiatedSizeSetsThePacketStride()
    {
        // SCPS 를 무시하고 더 큰 패킷을 보내는 장치가 있다. 리더보다 먼저 온 페이로드는 예상 패킷 수라는 근거가 없어
        // 협상값을 그대로 패킷당 바이트로 쓰기 쉬운데, 그러면 그 패킷이 프레임 안의 엉뚱한 자리에 실려 조용히 어긋난 이미지가 나간다.
        // 협상값보다 긴 길이는 그 자체로 진짜 값이다.
        var opt = StreamRig.DefaultOpt();
        // 이 테스트는 1 번을 비운 채 2 번을 먼저 보낸다 — 그 구멍은 첫 패킷이 닿는 순간부터 존재한다. 기본 유예 2 ms 로는
        // 수신 경로가 아직 덥혀지지 않은 첫 왕복에서 시한을 넘겨 재요청이 한 번 나가고, 재요청 수가 0 이라는 확인이 깨진다.
        // 여기서 재려는 것은 재요청 타이밍이 아니라 패킷 길이로 패킷당 바이트를 배우는지이므로 유예를 넉넉히 두어 순서만 남긴다.
        opt.InitialPacketTimeoutMs = 1000;
        opt.PayloadSize = 8192;      // 리더 전에 온 페이로드도 버퍼를 잡을 수 있게 크기를 알려 둔다.
        await using var rig = new StreamRig(opt);
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        // 협상은 1500 인데 장치는 3000 짜리 패킷을 보낸다.
        rig.Sender.PacketSize = 3000;
        var sent = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 61);
        Assert.Equal(3, sent.PacketCount);
        Assert.True(sent.DataBytesPerPacket > GvspConst.DataBytesPerPacket(rig.Stream.PacketSize, extendedIds: false));

        // 둘째 페이로드가 리더보다 먼저 도착한다 — 이 패킷의 길이 말고는 패킷당 바이트를 알 근거가 없다.
        rig.Sender.SendPacket(sent, 2, GvspConst.StatusSuccess);
        rig.Sender.SendPacket(sent, 0, GvspConst.StatusSuccess);
        rig.Sender.SendPacket(sent, 1, GvspConst.StatusSuccess);
        rig.Sender.SendPacket(sent, 3, GvspConst.StatusSuccess);

        using var frame = await rig.ReceiveAsync(3000);
        Assert.True(frame.IsComplete);
        Assert.Equal(3, frame.ExpectedPackets);
        Assert.Equal(sent.Data.Length, frame.PayloadSize);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data));
        Assert.Equal(0, rig.Stream.Stats.ResendRequests);
    }

    [Fact]
    public async Task AThrowingFrameDroppedHandlerDoesNotKillTheReceiver()
    {
        // FrameDropped 는 수신 스레드에서 불린다 — 소비자 쪽 예외가 그대로 올라오면 스레드가 죽고 스트림 전체가 멈춘다.
        await using var rig = new StreamRig();
        rig.Stream.FrameDropped += _ => throw new InvalidOperationException("handler blew up");
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        var lossy = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 41);
        rig.Sender.Drop.Add((1, 2));
        rig.Sender.SendFrame(lossy);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1UL, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Incomplete, diag.Reason);

        // 다음 프레임이 오는 것이 수신 스레드가 살아 있다는 증거다.
        var next = rig.Sender.SendFrame(2, 64, 32, Mono8, seed: 42);
        using var frame = await rig.ReceiveAsync(3000);
        Assert.Equal(2UL, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.True(frame.Data.Span.SequenceEqual(next.Data));
    }

    [Fact]
    public async Task StoppingReturnsTheBuffersOfFramesStillBeingAssembled()
    {
        // 조립 중이던 프레임의 버퍼는 수신 스레드가 끝날 때 풀로 돌아와야 한다 — 안 그러면 스트림을 멈출 때마다
        // 버퍼가 한 장씩 새어 나가고, 다시 시작한 스트림은 그만큼 적은 버퍼로 돌게 된다.
        var opt = StreamRig.DefaultOpt();
        opt.FrameRetentionMs = 30_000;   // 보존 시간으로 먼저 반납되면 스레드 종료 경로가 가려진다.
        await using var rig = new StreamRig(opt);
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        var partial = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 51);
        rig.Sender.SendPacket(partial, 0, GvspConst.StatusSuccess);
        rig.Sender.SendPacket(partial, 1, GvspConst.StatusSuccess);
        await rig.WaitUntilAsync(() => rig.Stream.PoolFreeBuffers == opt.BufferCount - 1);

        await rig.Stream.StopAsync(Ct);

        Assert.Equal(opt.BufferCount, rig.Stream.PoolFreeBuffers);
        // 중단이지 손실이 아니다 — 통계에는 세지 않는다.
        Assert.Equal(0, rig.Stream.Stats.FramesCompleted);
        Assert.Equal(0, rig.Stream.Stats.FramesIncomplete);
    }
}

/// <summary>
/// 스트림이 남기는 로그 줄 — <see cref="GevLog.Sink"/> 는 프로세스 전역이라 싱크를 바꿔 끼는 동안 다른 테스트와 나란히 돌지 않는 컬렉션에 둔다.
/// </summary>
[Collection(GevLogSinkCollection.Name)]
public class GevStreamLogTests
{
    private const uint Mono8 = 0x01080001;

    /// <summary>싱크를 바꿔 끼운 채 본문을 돌리고, 그동안 남은 (레벨, 메시지) 를 돌려준다.</summary>
    private static async Task<(GevLogLevel Level, string Message)[]> CaptureAsync(Func<Task> body)
    {
        var logged = new List<(GevLogLevel, string)>();
        var previousSink = GevLog.Sink;
        var previousLevel = GevLog.MinLevel;
        GevLog.MinLevel = GevLogLevel.Debug;
        GevLog.Sink = (level, _, message, _) =>
        {
            lock (logged) logged.Add((level, message));
        };
        try
        {
            await body();
        }
        finally
        {
            GevLog.Sink = previousSink;
            GevLog.MinLevel = previousLevel;
        }
        lock (logged) return logged.ToArray();
    }

    [Fact]
    public async Task BlockCutBeforeItsFirstPayloadIsReportedLikeAnyCutBlock()
    {
        // 첫 페이로드 전에 끊긴 블록도 끊긴 블록이다 — 같은 경고가 한 번 나가야 "장치가 블록을 끊는다" 가 현장 로그에 보인다.
        var logged = await CaptureAsync(async () =>
        {
            var opt = StreamRig.DefaultOpt();
            opt.FrameRetentionMs = 30_000;
            await using var rig = new StreamRig(opt);
            await rig.StartAsync();

            var cut = rig.Sender.BuildFrame(2, 64, 100, Mono8, seed: 0x11);
            rig.Sender.SendPacket(cut, 0, GvspConst.StatusSuccess);
            rig.Sender.SendTrailer(cut, 1);
            await rig.WaitDroppedAsync(3000);
        });

        Assert.Contains(logged, l => l.Level == GevLogLevel.Warn && l.Message.Contains("the trailer ended the block after 0 payload packet(s)"));
    }

    [Theory]
    [InlineData(true, 0.25, false)]
    [InlineData(false, 0.25, true)]
    [InlineData(true, 0.0, true)]
    public async Task StartSaysWhenFrameRetentionDoesNotApply(bool resendEnabled, double ratio, bool isResendOff)
    {
        // 리센드가 꺼지면 보존 시간은 쓰이지 않는다 — 비율 0 도 그렇다. 옵션만 보고 보존 시간을 늘린 사람이 로그에서 이유를 찾을 수 있어야 하고,
        // 시작 줄의 "resend on/off" 도 옵션 하나가 아니라 실제로 도는 쪽을 말해야 한다.
        var logged = await CaptureAsync(async () =>
        {
            var opt = StreamRig.DefaultOpt();
            opt.ResendEnabled = resendEnabled;
            opt.PacketRequestRatio = ratio;
            await using var rig = new StreamRig(opt);
            await rig.StartAsync();
        });

        var notes = logged.Where(l => l.Message.Contains("FrameRetentionMs") && l.Message.Contains("does not apply")).ToArray();
        Assert.Equal(isResendOff ? 1 : 0, notes.Length);
        if (isResendOff) Assert.Equal(GevLogLevel.Info, notes[0].Level);
        Assert.Contains(logged, l => l.Message.StartsWith("Stream started") && l.Message.EndsWith(isResendOff ? "resend off." : "resend on."));
    }
}
