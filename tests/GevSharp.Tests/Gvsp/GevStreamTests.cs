using System.Net;
using System.Runtime.InteropServices;
using GevSharp.Gvcp;
using GevSharp.Gvsp;

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
    public async Task StopAsyncDrainsTheQueueSoNoFrameSurvivesTheStop()
    {
        // 정지가 큐를 남긴다면 다음에 여는 쪽이 지난 판의 프레임을 받게 된다. 여기서 못 박아 둔다.
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 8;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        for (var i = 0; i < 3; i++) rig.Sender.SendFrame(1UL + (ulong)i, 64, 48, Mono8);
        await rig.WaitUntilAsync(() => rig.Stream.QueuedFrames == 3);

        await rig.Stream.StopAsync();

        Assert.Equal(0, rig.Stream.QueuedFrames);
        await Assert.ThrowsAsync<GevStreamClosedException>(async () => await rig.Stream.ReceiveAsync());
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
    public async Task SlowSenderDoesNotTriggerSpuriousResends()
    {
        // 침묵 규칙(재요청 간격만큼 조용하면 꼬리를 구멍으로 친다)이 스케줄링 지연에 걸리지 않게 간격을 넉넉히 둔다 — 여기서 보는 것은 유예뿐이다.
        var opt = StreamRig.DefaultOpt();
        // 프레임 전체가 25 ms 안에 나가므로 문턱을 크게 잡아도 "아직 안 온 꼬리는 구멍이 아니다" 라는 성질은 그대로 걸린다.
        // 문턱이 러너의 선점보다 짧으면 이 테스트는 유예가 아니라 러너의 스케줄링을 재게 된다.
        opt.PacketTimeoutMs = 1000;
        // 보존 시간도 마찬가지 — 러너가 밀려 프레임이 포기되면 "군더더기 요청이 없다" 대신 타임아웃이 난다.
        opt.FrameRetentionMs = 3000;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        // 패킷 사이가 유예(2 ms)보다 길어도 아직 안 온 꼬리는 구멍이 아니다.
        var frame = rig.Sender.BuildFrame(1, 64, 100, Mono8, seed: 11);
        Assert.Equal(5, frame.PacketCount);
        rig.Sender.SendFrame(frame, interPacketDelayMs: 5);

        using var received = await rig.ReceiveAsync();
        Assert.True(received.IsComplete);
        Assert.True(received.Data.Span.SequenceEqual(frame.Data));
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
        await using var rig = new StreamRig();
        await rig.StartAsync();

        // 첫 프레임으로 버퍼 크기를 알게 한 뒤, 둘째 프레임은 리더 없이 페이로드를 보내고 id 0 짜리 깨진 트레일러를 붙인다.
        // 리더는 리센드로 돌아오며, 깨진 트레일러가 리더의 패킷 수 계산을 막지 않아야 프레임이 닫힌다.
        var first = rig.Sender.SendFrame(1, 64, 100, Mono8, seed: 1);
        using (var f1 = await rig.ReceiveAsync())
        {
            Assert.True(f1.Data.Span.SequenceEqual(first.Data));
        }

        var second = rig.Sender.BuildFrame(2, 64, 100, Mono8, seed: 2);
        rig.Sender.Drop.Add((2, 0));
        for (uint id = 1; id <= (uint)second.PacketCount; id++) rig.Sender.SendPacket(second, id, GvspConst.StatusSuccess);
        rig.Sender.SendTrailer(second, packetId: 0);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2UL, frame.FrameId);
        Assert.True(frame.IsComplete);
        Assert.Equal(second.PacketCount, frame.ExpectedPackets);
        Assert.True(frame.Data.Span.SequenceEqual(second.Data));
        Assert.True(rig.Stream.Stats.PacketsIgnored >= 1);
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
