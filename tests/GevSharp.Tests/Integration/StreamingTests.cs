using System.Diagnostics;
using System.Net;
using GevSharp.Gvcp;
using GevSharp.Gvsp;
using GevSharp.Sim;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Integration;

/// <summary>
/// 시뮬레이터 대향 스트리밍: 채널 레지스터 설정 순서, 패킷 크기 협상, 프레임 내용·순서, 풀 반납, 손실 주입과 리센드,
/// 불완전 프레임 정책, 풀 고갈, 확장/표준 블록 ID, PENDING_ACK, 정지, 동시 스트림.
/// 획득은 GenApi 없이 <see cref="SimFeatureAddr"/> 레지스터로 켜고 끈다. 순서·개수·드롭 수를 정확히 단정하는 테스트는 소프트웨어 트리거로
/// 프레임을 하나씩 내보내(<see cref="SimRig.TriggerAsync"/>) 러너 속도에 기대지 않는다.
/// </summary>
public class StreamingTests
{
    private const uint Mono8 = 0x0108_0001;
    private const int FrameBytes = 128 * 64;
    /// <summary>표준 8바이트 헤더, SCPS 1500 → 페이로드 패킷당 1464 바이트 → 8192 바이트는 6 패킷.</summary>
    private const int DataBytes1500 = 1500 - GvspConst.IpUdpOverhead - GvspConst.HeaderSize;
    private const int PacketsPerFrame1500 = (FrameBytes + DataBytes1500 - 1) / DataBytes1500;

    private static GevStreamOpt AutoOpt()
    {
        var opt = SimRig.DefaultStreamOpt();
        opt.PacketSizeMode = PacketSizeMode.Auto;
        return opt;
    }

    /// <summary>
    /// <see cref="SimRig.ReceiveManyAsync"/> 로 frames 개를 들고 있는 동안에도 조립 중인 프레임(최대 4)과 큐가 버퍼를 쓴다 —
    /// 풀이 그보다 작으면 소비자가 든 프레임 수만큼 받은 뒤 나머지는 전부 NoBuffer 로 버려진다(설계대로). 그만큼 여유를 둔다.
    /// </summary>
    private static GevStreamOpt PoolFor(int frames, Action<GevStreamOpt>? tweak = null)
    {
        var opt = SimRig.DefaultStreamOpt();
        opt.BufferCount = frames + 8;
        tweak?.Invoke(opt);
        return opt;
    }

    /// <summary>프레임 전체를 시뮬레이터 패턴과 대조한다(Span 은 비동기 메서드 밖에서 다룬다).</summary>
    private static void AssertPattern(SimRig rig, GevFrame frame)
    {
        var expected = rig.ExpectedFrame(frame.FrameId);
        Assert.Equal(expected.Length, frame.PayloadSize);
        Assert.True(frame.Data.Span.SequenceEqual(expected), $"frame {frame.FrameId}: pixel content differs from the simulator pattern");
    }

    /// <summary>몇 바이트만 직접 계산해 본다 — Mono8 DiagonalRamp 는 (x + y + frameId) &amp; 0xFF.</summary>
    private static void AssertSampledPixels(GevFrame frame)
    {
        var data = frame.Data.Span;
        foreach (var (x, y) in new[] { (0, 0), (frame.Width - 1, 0), (0, frame.Height - 1), (frame.Width - 1, frame.Height - 1), (17, 29) })
        {
            var expected = (byte)(x + y + (int)frame.FrameId);
            var actual = data[y * frame.Stride + x];
            Assert.True(expected == actual, $"frame {frame.FrameId} pixel ({x},{y}) = {actual}, expected {expected}");
        }
    }

    private static void AssertGeometry(GevFrame frame, int width, int height)
    {
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.Equal(Mono8, frame.PixelFormatCode);
        Assert.Equal(width, frame.Stride);
        Assert.Equal(0, frame.PaddingX);
        Assert.Equal(0, frame.PaddingY);
        Assert.Equal(0, frame.OffsetX);
        Assert.Equal(0, frame.OffsetY);
        Assert.Equal(width * height, frame.PayloadSize);
        Assert.Equal(width * height, frame.Data.Length);
        Assert.Equal(GvspConst.PayloadImage, frame.PayloadType);
        Assert.False(frame.HasChunkData);
    }

    private static void DisposeAll(IEnumerable<GevFrame> frames)
    {
        foreach (var f in frames) f.Dispose();
    }

    // ---------------------------------------------------------------- start / registers

    [Fact]
    public async Task Start_Fixed1500_WritesScdaScpScpsAsIs()
    {
        await using var rig = await SimRig.StartAsync();
        Assert.Equal(0u, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        Assert.Equal(0u, rig.ReadStreamReg(GvbsAddr.ScdaOffset));
        var opt = SimRig.DefaultStreamOpt();
        opt.InterPacketDelay = 5_000;   // 5 µs — 경로만 태운다

        await using var stream = await rig.OpenStreamAsync(opt);

        Assert.True(stream.IsStarted);
        Assert.NotEqual(0, stream.LocalPort);
        Assert.Equal(1500, stream.PacketSize);
        Assert.Equal(0x7F00_0001u, rig.ReadStreamReg(GvbsAddr.ScdaOffset));
        Assert.Equal((uint)stream.LocalPort, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        var scps = rig.ReadStreamReg(GvbsAddr.ScpsOffset);
        Assert.Equal(1500u, scps & GvbsAddr.ScpsSizeMask);
        Assert.Equal(0u, scps & GvbsAddr.ScpsFireTest);
        Assert.Equal(5_000u, rig.ReadStreamReg(GvbsAddr.ScpdOffset));
        // Fixed 는 협상하지 않는다 — 테스트 패킷이 하나도 없다.
        Assert.Equal(0, rig.Sim.TestPacketsSent);
        Assert.Equal(0, rig.Sim.TestPacketsIgnored);
        Assert.Equal(0, rig.Sim.MalformedCount);

        // 이미 시작한 스트림을 다시 시작할 수는 없다.
        await Assert.ThrowsAsync<InvalidOperationException>(() => stream.StartAsync());
    }

    [Fact]
    public async Task Start_Auto_NegotiatesUpToTheDeviceCap_UsingTheLoopbackMtu()
    {
        await using var rig = await SimRig.StartAsync(sim: o => o.MaxPacketSize = 4000);
        var opt = AutoOpt();

        await using var stream = await rig.OpenStreamAsync(opt);

        // 계약만 본다: 결과는 장치 상한(4000) 이하, 4 의 배수, 최소 크기 이상이고, 루프백 MTU(모르면 9000 에서 시작)는 1500 을 넘으므로 1500 보다 커야 한다.
        // 탐색 시작점·이분 탐색 단계 같은 구현 세부는 Gvsp/PacketSizeNegotiationTests 몫이다.
        var mtu = GevStream.InterfaceMtu(IPAddress.Loopback);   // 진단 메시지에만 쓴다
        Assert.True(stream.PacketSize <= 4000, $"negotiated {stream.PacketSize} above the device cap 4000 (MTU {mtu})");
        Assert.Equal(0, stream.PacketSize % 4);
        Assert.True(stream.PacketSize >= GevStream.MinPacketSize);
        Assert.True(stream.PacketSize > 1500, $"loopback (MTU {mtu}) allows more than 1500 but {stream.PacketSize} was negotiated");
        Assert.True(rig.Sim.TestPacketsSent > 0, "no fire-test packet was ever answered");
        Assert.Equal(stream.PacketSize, opt.PacketSize);

        var scps = rig.ReadStreamReg(GvbsAddr.ScpsOffset);
        Assert.Equal((uint)stream.PacketSize, scps & GvbsAddr.ScpsSizeMask);
        Assert.Equal(0u, scps & GvbsAddr.ScpsFireTest);
        Assert.NotEqual(0u, scps & GvbsAddr.ScpsDoNotFragment);
        Assert.Equal((uint)stream.LocalPort, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        Assert.Equal(0x7F00_0001u, rig.ReadStreamReg(GvbsAddr.ScdaOffset));
    }

    [Fact]
    public async Task Start_Auto_BinarySearchStopsAtTheDeviceCap_AndFramesUseThatSize()
    {
        await using var rig = await SimRig.StartAsync(sim: o => o.MaxPacketSize = 4000);

        // MTU 조회를 9000 으로 고정해 플랫폼과 무관하게 이분 탐색 경로를 태운다.
        await using var stream = await rig.OpenStreamAsync(AutoOpt(), s => s.MtuResolver = _ => 9000);

        Assert.InRange(stream.PacketSize, 3984, 4000);
        Assert.Equal(0, stream.PacketSize % 4);
        Assert.True(rig.Sim.TestPacketsIgnored >= 1, "the 9000 probe must have been ignored by the capped device");
        Assert.True(rig.Sim.TestPacketsSent >= 2, "1500 and at least one mid-point probe must have been answered");
        Assert.Equal((uint)stream.PacketSize, rig.ReadStreamReg(GvbsAddr.ScpsOffset) & GvbsAddr.ScpsSizeMask);

        await rig.StartAcquisitionAsync();
        using var frame = await SimRig.ReceiveAsync(stream);
        Assert.True(frame.IsComplete);
        var dataBytes = stream.PacketSize - GvspConst.IpUdpOverhead - GvspConst.HeaderSize;
        Assert.Equal((FrameBytes + dataBytes - 1) / dataBytes, frame.ExpectedPackets);
        AssertGeometry(frame, 128, 64);
        AssertPattern(rig, frame);
    }

    // ---------------------------------------------------------------- frames

    [Fact]
    public async Task Acquisition_Delivers20CompleteFramesInOrderWithPattern_AndReturnsBuffersToThePool()
    {
        await using var rig = await SimRig.StartAsync();
        var opt = SimRig.DefaultStreamOpt();
        opt.BufferCount = 4;   // 20 프레임 > 4 버퍼 — Dispose 가 풀에 돌려주지 않으면 드롭이 난다
        await using var stream = await rig.OpenStreamAsync(opt);
        var dropped = new List<GevFrameDiag>();
        stream.FrameDropped += d => { lock (dropped) dropped.Add(d); };

        // 트리거 모드: 프레임은 테스트가 트리거할 때만 나간다 — 20 프레임 > 4 버퍼라도 순서·드롭 0 은 러너 속도와 무관하다.
        await rig.StartTriggeredAcquisitionAsync();
        Assert.True(rig.Sim.IsAcquiring);

        ulong previous = 0;
        for (var i = 0; i < 20; i++)
        {
            using var frame = await rig.TriggerAndReceiveAsync(stream);
            Assert.True(frame.IsComplete, $"frame {frame.FrameId} incomplete: {frame.MissingPackets} of {frame.ExpectedPackets} packets missing");
            Assert.Equal(0, frame.MissingPackets);
            Assert.Equal(PacketsPerFrame1500, frame.ExpectedPackets);
            if (i == 0) Assert.Equal(1ul, frame.FrameId);   // 첫 획득의 첫 블록은 1
            Assert.True(frame.FrameId > previous, $"frame id {frame.FrameId} after {previous} is not increasing");
            Assert.Equal(previous + 1, frame.FrameId);      // 트리거마다 한 프레임 — 하나도 빠지지 않는다
            previous = frame.FrameId;
            Assert.True(frame.Timestamp > 0);
            AssertGeometry(frame, 128, 64);
            AssertSampledPixels(frame);
            AssertPattern(rig, frame);
            Assert.False(frame.IsDisposed);
        }

        await rig.StopAcquisitionAsync();
        Assert.False(rig.Sim.IsAcquiring);

        var s = stream.Stats.Snapshot();
        Assert.Equal(20, s.FramesCompleted);
        Assert.Equal(20, s.FramesDelivered);
        Assert.Equal(0, s.FramesIncomplete);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
        Assert.Equal(0, s.FramesDroppedError);
        Assert.Equal(0, s.FramesDroppedUnsupported);
        Assert.Equal(0, s.ResendRequests);
        Assert.Equal(0, s.PacketsMissing);
        Assert.Equal(0, s.ErrorPackets);
        Assert.True(s.LastFrameId >= previous);
        Assert.Equal(20 * (PacketsPerFrame1500 + 2), s.PacketsReceived);   // 프레임마다 리더 + 페이로드 + 트레일러, 리센드 없음
        lock (dropped) Assert.Empty(dropped);
        Assert.Empty(rig.Sim.ResendRequests);
    }

    [Fact]
    public async Task Frame_DataIsInvalidAfterDispose_AndToArrayOutlivesIt()
    {
        await using var rig = await SimRig.StartAsync();
        await using var stream = await rig.OpenStreamAsync();
        await rig.StartAcquisitionAsync();

        var frame = await SimRig.ReceiveAsync(stream);
        var copy = frame.ToArray();
        Assert.Equal(rig.ExpectedFrame(frame.FrameId), copy);
        frame.Dispose();
        Assert.True(frame.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => frame.Data);
        Assert.Throws<ObjectDisposedException>(() => frame.ToArray());
        frame.Dispose();   // 멱등
        Assert.Equal(rig.ExpectedFrame(frame.FrameId), copy);   // 사본은 살아 있다
        Assert.Equal(1ul, frame.FrameId);                       // 메타데이터는 Dispose 뒤에도 읽힌다
    }

    // ---------------------------------------------------------------- packet loss

    [Fact]
    public async Task DroppedPackets_AreRecoveredByResend_AllFramesComplete()
    {
        // 매 3 번째 프레임의 페이로드 2 번을 첫 전송에서 버린다. 트리거마다 한 프레임 — 리센드 이력(최근 8 프레임)이 러너 속도 때문에 소진되지 않는다.
        await using var rig = await SimRig.StartAsync(sim: o => o.DropPacket = (frame, packet) => frame % 3 == 0 && packet == 2);
        await using var stream = await rig.OpenStreamAsync();
        await rig.StartTriggeredAcquisitionAsync();

        var injected = 0;
        for (var i = 0; i < 20; i++)
        {
            var id = await rig.TriggerAsync();
            using var f = await SimRig.ReceiveAsync(stream);
            Assert.Equal(id, f.FrameId);
            Assert.True(f.IsComplete, $"frame {f.FrameId} incomplete after resend: {f.MissingPackets} missing");
            AssertPattern(rig, f);
            if (id % 3 == 0) injected++;
        }
        Assert.Equal(6, injected);   // 블록 3, 6, …, 18 에 손실이 주입됐다
        await rig.StopAcquisitionAsync();

        var s = stream.Stats.Snapshot();
        Assert.Equal(0, s.FramesIncomplete);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
        Assert.Equal(0, s.PacketsMissing);
        Assert.True(s.ResendRequests >= injected, $"resend requests {s.ResendRequests} < injected frames {injected}");
        Assert.True(s.ResendRecovered >= injected, $"resend recovered {s.ResendRecovered} < injected frames {injected}");
        Assert.True(s.PacketsResent >= injected);
        Assert.True(rig.Sim.PacketsDropped >= injected);
        Assert.True(rig.Sim.PacketsResent >= injected);

        var requests = rig.Sim.ResendRequests;
        Assert.NotEmpty(requests);
        Assert.True(requests.Count >= injected);
        Assert.All(requests, r =>
        {
            Assert.True(r.IsAccepted, $"resend for block {r.BlockId} was not accepted by the device (sender {r.Sender})");
            Assert.Equal(rig.Device.Gvcp.LocalEndPoint, r.Sender);   // 제어 소켓에서 나가야 장치가 받아 준다
            Assert.Equal(0ul, r.BlockId % 3);
            Assert.Equal(2u, r.FirstPacketId);
            Assert.Equal(2u, r.LastPacketId);
        });
    }

    [Fact]
    public async Task DroppedPackets_WithoutResend_IncompleteFramesAreCountedAndNotDelivered()
    {
        await using var rig = await SimRig.StartAsync(sim: o => o.DropPacket = (frame, packet) => frame % 3 == 0 && packet == 2);
        var opt = PoolFor(12, o => o.ResendEnabled = false);
        var dropped = new List<GevFrameDiag>();
        await using var stream = await rig.OpenStreamAsync(opt, s => s.FrameDropped += d => { lock (dropped) dropped.Add(d); });
        await rig.StartTriggeredAcquisitionAsync();

        // 18 프레임을 트리거한다: 3 의 배수 여섯은 불완전이라 전달되지 않고 나머지 12 개만 온다. 프레임은 블록 순서로 닫히므로
        // 손실 프레임 다음의 완전한 프레임은 손실 프레임이 불완전으로 닫힌 뒤에 전달된다.
        var frames = new List<GevFrame>();
        try
        {
            for (var i = 0; i < 18; i++)
            {
                var id = await rig.TriggerAsync();
                if (id % 3 != 0) frames.Add(await SimRig.ReceiveAsync(stream));
            }
            Assert.Equal(12, frames.Count);
            Assert.All(frames, f => Assert.True(f.IsComplete, $"incomplete frame {f.FrameId} was delivered although DeliverIncompleteFrames is off"));
            Assert.Equal(Enumerable.Range(1, 18).Where(n => n % 3 != 0).Select(n => (ulong)n).ToArray(), frames.Select(f => f.FrameId).ToArray());
            foreach (var f in frames) AssertPattern(rig, f);
        }
        finally
        {
            DisposeAll(frames);
        }
        await rig.StopAcquisitionAsync();

        var s = stream.Stats.Snapshot();
        Assert.True(s.FramesIncomplete > 0, "incomplete frames were not counted");
        Assert.True(s.PacketsMissing >= s.FramesIncomplete);
        Assert.Equal(0, s.ResendRequests);
        Assert.Equal(0, s.PacketsResent);
        Assert.Equal(12, s.FramesDelivered);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
        Assert.Empty(rig.Sim.ResendRequests);
        Assert.Equal(0, rig.Sim.PacketsResent);
        lock (dropped)
        {
            Assert.NotEmpty(dropped);
            Assert.All(dropped, d =>
            {
                Assert.Equal(GevFrameDropReason.Incomplete, d.Reason);
                Assert.Equal(0ul, d.FrameId % 3);
                Assert.Equal(1, d.MissingPackets);
                Assert.Equal(PacketsPerFrame1500, d.ExpectedPackets);
            });
        }
    }

    [Fact]
    public async Task DroppedPackets_DeliverIncomplete_FlagsFramesAndZeroesTheHole()
    {
        await using var rig = await SimRig.StartAsync(sim: o => o.DropPacket = (frame, packet) => frame % 3 == 0 && packet == 2);
        var opt = PoolFor(12, o => { o.ResendEnabled = false; o.DeliverIncompleteFrames = true; });
        await using var stream = await rig.OpenStreamAsync(opt);
        await rig.StartTriggeredAcquisitionAsync();

        var frames = new List<GevFrame>();
        try
        {
            for (var i = 0; i < 12; i++)
            {
                await rig.TriggerAsync();
                frames.Add(await SimRig.ReceiveAsync(stream));
            }
            // 아무 프레임도 건너뛰지 않는다 — 불완전 프레임도 제자리에 온다.
            Assert.Equal(Enumerable.Range(1, 12).Select(n => (ulong)n).ToArray(), frames.Select(f => f.FrameId).ToArray());

            var incomplete = frames.Where(f => !f.IsComplete).ToList();
            Assert.NotEmpty(incomplete);
            Assert.All(incomplete, f => Assert.Equal(0ul, f.FrameId % 3));
            Assert.All(frames.Where(f => f.FrameId % 3 == 0), f => Assert.False(f.IsComplete));
            foreach (var f in incomplete)
            {
                Assert.Equal(1, f.MissingPackets);
                Assert.Equal(PacketsPerFrame1500, f.ExpectedPackets);
                AssertGeometry(f, 128, 64);
                // 페이로드 2 번 자리 [1464, 2928) 는 0, 나머지는 패턴 그대로.
                var expected = rig.ExpectedFrame(f.FrameId);
                Array.Clear(expected, DataBytes1500, DataBytes1500);
                Assert.True(f.Data.Span.SequenceEqual(expected), $"incomplete frame {f.FrameId}: hole is not zeroed or the rest differs from the pattern");
            }
            foreach (var f in frames.Where(f => f.IsComplete))
            {
                Assert.Equal(0, f.MissingPackets);
                AssertPattern(rig, f);
            }
        }
        finally
        {
            DisposeAll(frames);
        }
        await rig.StopAcquisitionAsync();

        var s = stream.Stats.Snapshot();
        Assert.True(s.FramesIncomplete > 0);
        Assert.Equal(12, s.FramesDelivered);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
        Assert.Equal(0, s.ResendRequests);
    }

    // ---------------------------------------------------------------- pool exhaustion

    [Fact]
    public async Task PoolExhaustion_DropsNewFrames_AndNeverTouchesHeldBuffers()
    {
        await using var rig = await SimRig.StartAsync();
        var opt = SimRig.DefaultStreamOpt();
        opt.BufferCount = 4;
        var dropped = new List<GevFrameDiag>();
        await using var stream = await rig.OpenStreamAsync(opt, s => s.FrameDropped += d => { lock (dropped) dropped.Add(d); });
        await rig.StartTriggeredAcquisitionAsync();

        // 버퍼 수만큼 들고 놓지 않는다.
        var held = new List<GevFrame>();
        long droppedWhileHeld;
        try
        {
            for (var i = 0; i < opt.BufferCount; i++) held.Add(await rig.TriggerAndReceiveAsync(stream));
            Assert.All(held, f => Assert.True(f.IsComplete));
            var snapshots = held.Select(f => f.ToArray()).ToArray();

            // 버퍼가 하나도 없는 동안 5 프레임을 더 트리거한다 — 전부 NoBuffer 로 버려져야 한다(드롭은 수신 스레드가 세므로 잠깐 기다린다).
            var before = stream.Stats.FramesDroppedNoBuffer;
            for (var i = 0; i < 5; i++) await rig.TriggerAsync();
            await SimRig.WaitUntilAsync(() => stream.Stats.FramesDroppedNoBuffer >= before + 5, 3000, "5 no-buffer drops while every buffer is held");
            droppedWhileHeld = stream.Stats.FramesDroppedNoBuffer;
            Assert.Equal(before + 5, droppedWhileHeld);

            Assert.Equal(opt.BufferCount, stream.Stats.FramesDelivered);
            Assert.False(stream.TryReceive(out var extra), $"a frame {extra?.FrameId} was queued although the consumer holds every buffer");

            // 들고 있는 프레임의 바이트는 그대로다 — 수신 스레드는 빌려 준 버퍼에 쓰지 않는다.
            for (var i = 0; i < held.Count; i++)
            {
                Assert.Equal(snapshots[i], held[i].ToArray());
                AssertPattern(rig, held[i]);
            }
            lock (dropped)
            {
                Assert.NotEmpty(dropped);
                Assert.All(dropped, d => Assert.Equal(GevFrameDropReason.NoBuffer, d.Reason));
            }
        }
        finally
        {
            DisposeAll(held);
        }
        Assert.Throws<ObjectDisposedException>(() => held[0].Data);

        // 반납 뒤에는 다시 흐른다.
        var later = new List<GevFrame>();
        try
        {
            for (var i = 0; i < 4; i++) later.Add(await rig.TriggerAndReceiveAsync(stream));
            Assert.All(later, f => Assert.True(f.IsComplete));
            Assert.All(later, f => Assert.True(f.FrameId > held[held.Count - 1].FrameId));
            foreach (var f in later) AssertPattern(rig, f);
        }
        finally
        {
            DisposeAll(later);
        }
        await rig.StopAcquisitionAsync();

        var s = stream.Stats.Snapshot();
        Assert.Equal(droppedWhileHeld, s.FramesDroppedNoBuffer);
        Assert.Equal(8, s.FramesDelivered);
        Assert.Equal(0, s.FramesIncomplete);
        Assert.Equal(0, s.FramesDroppedError);
    }

    // ---------------------------------------------------------------- block ids

    [Fact]
    public async Task ExtendedIds_FrameIdContinuesPast65535()
    {
        await using var rig = await SimRig.StartAsync(sim: o => o.ExtendedIds = true);
        Assert.Equal(SimStreamBits.SccfgExtendedIds, rig.ReadStreamReg(GvbsAddr.SccfgOffset));
        rig.Sim.SeedBlockId(65535);
        await using var stream = await rig.OpenStreamAsync(PoolFor(5));
        await rig.StartTriggeredAcquisitionAsync();

        var frames = new List<GevFrame>();
        try
        {
            for (var i = 0; i < 5; i++) frames.Add(await rig.TriggerAndReceiveAsync(stream));
            Assert.Equal(new ulong[] { 65536, 65537, 65538, 65539, 65540 }, frames.Select(f => f.FrameId).ToArray());
            var dataBytes = 1500 - GvspConst.IpUdpOverhead - GvspConst.ExtendedHeaderSize;
            foreach (var f in frames)
            {
                Assert.True(f.IsComplete, $"frame {f.FrameId} incomplete in extended-id mode");
                Assert.Equal((FrameBytes + dataBytes - 1) / dataBytes, f.ExpectedPackets);
                AssertGeometry(f, 128, 64);
                AssertPattern(rig, f);
            }
        }
        finally
        {
            DisposeAll(frames);
        }
        await rig.StopAcquisitionAsync();
        Assert.True(stream.Stats.LastFrameId >= 65540ul);
        Assert.Equal(0, stream.Stats.FramesIncomplete);
        Assert.Equal(0, stream.Stats.FramesDroppedNoBuffer);
    }

    [Fact]
    public async Task StandardIds_WrapFrom65535To1_WithoutLosingFrames()
    {
        await using var rig = await SimRig.StartAsync();
        rig.Sim.SeedBlockId(65533);
        await using var stream = await rig.OpenStreamAsync(PoolFor(5));
        await rig.StartTriggeredAcquisitionAsync();

        var frames = new List<GevFrame>();
        try
        {
            for (var i = 0; i < 5; i++) frames.Add(await rig.TriggerAndReceiveAsync(stream));
            Assert.Equal(new ulong[] { 65534, 65535, 1, 2, 3 }, frames.Select(f => f.FrameId).ToArray());
            foreach (var f in frames)
            {
                Assert.True(f.IsComplete, $"frame {f.FrameId} incomplete across the 16-bit wrap");
                AssertPattern(rig, f);
            }
        }
        finally
        {
            DisposeAll(frames);
        }
        await rig.StopAcquisitionAsync();
        Assert.Equal(0, stream.Stats.FramesIncomplete);
        Assert.Equal(0, stream.Stats.FramesDroppedNoBuffer);
        Assert.Equal(0, stream.Stats.PacketsIgnored);
    }

    // ---------------------------------------------------------------- pending ack

    [Fact]
    public async Task PendingAck_RegisterWritesSucceedBeyondTheGvcpTimeout()
    {
        // 장치는 WRITEREG 마다 PENDING_ACK 를 먼저 보내고 예고한 만큼 기다렸다가 진짜 ACK 를 보낸다. 시험의 뼈대는
        // "예고된 지연 > 채널 타임아웃" 이며, 그래야 연장이 없으면 통과할 수 없다.
        // 채널 타임아웃 1 s: PENDING_ACK 자체가 첫 시도 안에 닿을 여유다(300 ms 로는 굶주린 러너에서 못 닿아 네 번 다 타임아웃했다).
        // 시뮬레이터는 PENDING_ACK 뒤의 대기를 처리 스레드에서 잠들어 구현하므로 헛된 재시도가 늘수록 뒤의 요청(하트비트 포함)이 밀린다 —
        // 그래서 준비 단계의 지연은 짧게 두고, 재는 쓰기 하나만 타임아웃을 넘기도록 그때만 늘린다(지연은 WRITEREG 마다 다시 읽힌다).
        await using var rig = await SimRig.StartAsync(
            sim: o => { o.SupportPendingAck = true; o.PendingAckDelayMs = 100; },
            device: o => { o.GvcpTimeoutMs = 1000; o.GvcpRetries = 3; o.HeartbeatTimeoutMs = 30_000; });   // 하트비트가 잠든 처리 스레드 뒤에 밀리지 않게
        Assert.NotEqual(0u, rig.Device.GvcpCapability & GvbsAddr.GvcpCapPendingAck);
        Assert.True(rig.Device.Gvcp.PendingAckCount >= 2, $"CCP and heartbeat-timeout writes should have seen PENDING_ACK, count {rig.Device.Gvcp.PendingAckCount}");

        rig.Sim.Opt.PendingAckDelayMs = 1500;   // 채널 타임아웃(1 s)보다 길게 — 연장이 없으면 이 쓰기는 실패한다
        var sw = Stopwatch.StartNew();
        await rig.Device.WriteRegAsync(SimFeatureAddr.Width, 256);
        rig.Sim.Opt.PendingAckDelayMs = 100;
        // 아래 경계만 뜻이 있다: 채널 타임아웃(1 s)을 넘겨 끝났다 = 연장이 있었다. 부하는 시간을 늘릴 뿐이라 흔들리지 않는다.
        // 위 경계는 두지 않는다 — 굶주린 스케줄러에서 늘어난 시간은 라이브러리의 잘못이 아니고, "끝나기는 했다" 는 바로 아래 값 확인이 지킨다.
        Assert.True(sw.ElapsedMilliseconds >= 1000, $"the write finished in {sw.ElapsedMilliseconds} ms, inside the channel timeout - no PENDING_ACK extension happened");
        Assert.Equal(256u, rig.Sim.Registers.ReadU32(SimFeatureAddr.Width));
        Assert.Equal(256u, await rig.Device.ReadRegAsync(SimFeatureAddr.Width));
        Assert.True(rig.Device.Gvcp.PendingAckCount >= 3);

        // 스트림 설정(SCDA/SCP/SCPS 쓰기)과 획득 시작도 같은 경로를 탄다.
        await using var stream = await rig.OpenStreamAsync();
        Assert.Equal((uint)stream.LocalPort, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        await rig.StartAcquisitionAsync();
        using var frame = await SimRig.ReceiveAsync(stream);
        Assert.True(frame.IsComplete);
        Assert.Equal(256, frame.Width);
        AssertPattern(rig, frame);
        Assert.True(rig.Device.IsOpen);
    }

    [Fact]
    public async Task PendingAck_AckLandingJustAfterTheAnnouncedTime_IsNotATimeout()
    {
        // 장치는 PENDING_ACK 로 200 ms 를 알리고 정확히 그만큼 잔 뒤 ACK 를 보낸다. 타이머 정밀도(Windows 는 최대 ~16 ms) 때문에
        // 실제 ACK 는 알린 시각보다 몇 ms 늦게 도착한다. 채널은 알린 시각 뒤에 보통의 응답 창(TimeoutMs)을 한 번 더 주므로 이런 장치도 통과한다.
        // 재시도 0 으로 두어, 그 여유가 없어지면 GevTimeoutException 으로 바로 드러나게 한다(재시도가 있으면 같은 WRITEREG 가 장치에 두 번 간다).
        // PENDING_ACK 는 열린 뒤에 켠다(시뮬레이터는 쓰기마다 옵션을 읽는다) — 열기 자체가 이 결함에 걸려 테스트가 엉뚱한 곳에서 죽지 않게.
        // 하트비트는 30 s 로 밀어 둔다 — 시뮬레이터는 PENDING_ACK 대기를 처리 스레드에서 잠들어 구현하므로, 쓰기 사이에 끼어든 하트비트 읽기가
        // 그 뒤로 밀려 150 ms 타임아웃에 걸리고, 세 번 연속이면 ControlLost 로 테스트가 엉뚱한 곳에서 죽는다.
        await using var rig = await SimRig.StartAsync(
            sim: o => o.PendingAckDelayMs = 200,
            device: o => { o.GvcpTimeoutMs = 150; o.GvcpRetries = 0; o.HeartbeatTimeoutMs = 30_000; });
        rig.Sim.Opt.SupportPendingAck = true;
        try
        {
            var timeouts = new List<string>();
            const int writes = 10;
            for (var i = 0; i < writes; i++)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    await rig.Device.WriteRegAsync(SimFeatureAddr.Width, 128 + 4u * (uint)i);
                }
                catch (GevTimeoutException ex)
                {
                    timeouts.Add($"write {i} after {sw.ElapsedMilliseconds} ms: {ex.Message}");
                }
            }
            // 여유가 없으면 한 쓰기의 타임아웃 뒤에 다음 쓰기가 곧바로 나가 시뮬레이터가 아직 잠든 채이므로, 과부하에서는 PENDING_ACK 없이
            // 타임아웃되는 쓰기도 생긴다 — 그래서 "쓰기마다 PENDING_ACK 하나" 도 함께 본다(정상이면 겹침이 없어 항상 성립한다).
            var pendingAcks = rig.Device.Gvcp.PendingAckCount;
            Assert.True(timeouts.Count == 0 && pendingAcks >= writes,
                "GvcpChannel treats an ACK that lands a few ms after the PENDING_ACK time-to-completion as a timeout: the extended deadline ends exactly at "
                + "the announced time with no slack (GvcpChannel.WaitForAckAsync), so a device whose completion runs into timer granularity fails with "
                + $"GevTimeoutException (or, with retries, receives the same WRITEREG again). {timeouts.Count} of {writes} writes timed out "
                + $"({pendingAcks} PENDING_ACKs seen for {writes} writes): " + string.Join(" | ", timeouts));
            Assert.Equal(0, rig.Device.Gvcp.StaleAckCount);
        }
        finally
        {
            rig.Sim.Opt.SupportPendingAck = false;   // 닫기(CCP = 0)가 결함에 걸리지 않게
        }
    }

    // ---------------------------------------------------------------- stop

    [Fact]
    public async Task Stop_WritesScpZero_StopsTheDevice_AndFailsAPendingReceive()
    {
        await using var rig = await SimRig.StartAsync();

        // (a) 획득 중 정지: SCP = 0 이면 장치는 더 보내지 않는다(획득 자체는 켜져 있어도).
        await using var first = await rig.OpenStreamAsync();   // 중간에 명시적으로 닫지만, 단정이 실패해도 수신 스레드가 남지 않게 한다
        await rig.StartAcquisitionAsync();
        var frames = await SimRig.ReceiveManyAsync(first, 2);
        DisposeAll(frames);
        await first.StopAsync();
        Assert.False(first.IsStarted);
        Assert.Equal(0u, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        Assert.Equal(0u, rig.ReadStreamReg(GvbsAddr.ScdaOffset));
        await Task.Delay(60);
        var sentAfterStop = rig.Sim.FramesSent;
        await Task.Delay(150);
        Assert.Equal(sentAfterStop, rig.Sim.FramesSent);
        Assert.True(rig.Sim.IsAcquiring);
        await Assert.ThrowsAsync<GevStreamClosedException>(async () => await first.ReceiveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.StartAsync());
        await first.DisposeAsync();

        // (b) 대기 중인 ReceiveAsync 는 정지와 함께 GevStreamClosedException 으로 끝난다.
        await rig.StopAcquisitionAsync();
        await using var second = await rig.OpenStreamAsync();
        Assert.Equal((uint)second.LocalPort, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        var pending = second.ReceiveAsync(CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);

        await second.StopAsync();

        var ex = await Assert.ThrowsAsync<GevStreamClosedException>(() => pending);
        Assert.Contains("stopped", ex.Message);
        Assert.Equal(0u, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        await second.StopAsync();   // 멱등
        await second.DisposeAsync();

        // 장치 세션은 그대로 살아 있다.
        Assert.True(rig.Device.IsOpen);
        Assert.Equal(rig.Device.Gvcp.LocalEndPoint, rig.Sim.ControlOwner);
    }

    [Fact]
    public async Task DeviceDispose_WhileStreaming_ReleasesControl_AndStreamDisposeStillCleansUp()
    {
        var sim = SimRig.StartSim();
        GevDevice? dev = null;
        GevStream? stream = null;
        try
        {
            dev = await GevDevice.OpenAsync(sim.GvcpEndPoint, SimRig.DefaultDeviceOpt());
            stream = await dev.OpenStreamAsync(SimRig.DefaultStreamOpt());
            await stream.StartAsync();
            await dev.WriteRegAsync(SimFeatureAddr.AcquisitionStart, 1);
            using (var f = await SimRig.ReceiveAsync(stream)) Assert.True(f.IsComplete);

            await dev.DisposeAsync();
            Assert.Null(sim.ControlOwner);
            Assert.Equal(0u, sim.Registers.ReadU32(GvbsAddr.Ccp));

            // 채널이 닫힌 뒤의 스트림 정지는 레지스터 쓰기에 실패하지만(로그만) 로컬 정리는 끝까지 간다.
            var sw = Stopwatch.StartNew();
            await stream.DisposeAsync();
            // 상한은 정리가 얼마나 빠른지가 아니라 "죽은 채널에 매달리지 않는다" 를 지킨다 — 닫힌 채널에 정지 쓰기를 걸어 두고
            // 그 예산이나 수신 스레드 조인을 끝없이 기다리면 깨진다. 굶주린 스케줄러의 고정 비용보다는 한참 위에 둔다.
            Assert.True(sw.ElapsedMilliseconds < 10_000, $"stream dispose took {sw.ElapsedMilliseconds} ms after the device was closed");
            Assert.False(stream.IsStarted);
            await Assert.ThrowsAsync<GevStreamClosedException>(async () => await stream.ReceiveAsync());
        }
        finally
        {
            // 단정이 중간에 실패해도 스트림 → 장치 → 시뮬레이터 순으로 전부 정리한다(둘 다 두 번 닫아도 안전하다).
            if (stream is not null) await stream.DisposeAsync();
            if (dev is not null) await dev.DisposeAsync();
            sim.Dispose();
        }
    }

    // ---------------------------------------------------------------- concurrency

    [Fact]
    public async Task TwoSimulators_StreamConcurrently_EachDelivers10CompleteFrames()
    {
        await using var a = await SimRig.StartAsync(sim: o => o.SerialNumber = "SIM-A");
        await using var b = await SimRig.StartAsync(sim: o => { o.SerialNumber = "SIM-B"; o.Width = 64; o.Height = 48; });
        Assert.NotEqual(a.EndPoint.Port, b.EndPoint.Port);
        Assert.NotEqual(a.Device.Gvcp.LocalEndPoint, b.Device.Gvcp.LocalEndPoint);
        Assert.Equal("SIM-A", a.Device.Info.SerialNumber);
        Assert.Equal("SIM-B", b.Device.Info.SerialNumber);

        var ports = await Task.WhenAll(StreamTenAsync(a, 128, 64), StreamTenAsync(b, 64, 48));

        Assert.NotEqual(ports[0], ports[1]);
        Assert.Equal(a.Device.Gvcp.LocalEndPoint, a.Sim.ControlOwner);
        Assert.Equal(b.Device.Gvcp.LocalEndPoint, b.Sim.ControlOwner);
        Assert.Empty(a.Sim.ResendRequests);
        Assert.Empty(b.Sim.ResendRequests);
    }

    private static async Task<int> StreamTenAsync(SimRig rig, int width, int height)
    {
        await using var stream = await rig.OpenStreamAsync();
        await rig.StartTriggeredAcquisitionAsync();
        for (var i = 1; i <= 10; i++)
        {
            var id = await rig.TriggerAsync();
            using var f = await SimRig.ReceiveAsync(stream);
            Assert.True(f.IsComplete, $"[{rig.Sim.Opt.SerialNumber}] frame {f.FrameId} incomplete");
            Assert.Equal((ulong)i, id);
            Assert.Equal(id, f.FrameId);
            AssertGeometry(f, width, height);
            AssertPattern(rig, f);
        }
        await rig.StopAcquisitionAsync();
        Assert.Equal(0, stream.Stats.FramesIncomplete);
        Assert.Equal(0, stream.Stats.FramesDroppedNoBuffer);
        return stream.LocalPort;
    }
}
