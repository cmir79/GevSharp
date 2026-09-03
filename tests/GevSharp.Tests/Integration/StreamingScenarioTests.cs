using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;
using GevSharp.Gvsp;
using GevSharp.Pfnc;
using GevSharp.Sim;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Integration;

/// <summary>
/// 시뮬레이터 대향 스트리밍 2부: 채널 레지스터 접근 순서, 스트리밍 중 해상도 변경(버퍼 자람), 픽셀 포맷별 스트라이드, 큰 프레임의 흩어진 손실 복구,
/// 리센드 예산 소진, 블록 ID 가 1 로 되돌아가는 단일 프레임 촬영, 고정 로컬 포트 재사용, 실제보다 작은 PayloadSize 힌트.
/// 획득은 GenApi 없이 <see cref="SimFeatureAddr"/> 레지스터로 켜고 끈다.
/// </summary>
public class StreamingScenarioTests
{
    private const uint Mono8 = 0x0108_0001;
    private const int DataBytes1500 = 1500 - GvspConst.IpUdpOverhead - GvspConst.HeaderSize;

    private static uint ChannelReg(uint offset) => GvbsAddr.StreamChannel(0, offset);

    /// <summary>조립 중인 프레임(최대 4)과 큐가 버퍼를 쓰는 만큼 여유를 둔 풀.</summary>
    private static GevStreamOpt PoolFor(int frames, Action<GevStreamOpt>? tweak = null)
    {
        var opt = SimRig.DefaultStreamOpt();
        opt.BufferCount = frames + 8;
        tweak?.Invoke(opt);
        return opt;
    }

    /// <summary>프레임이 스스로 알리는 크기·포맷으로 기대 패턴을 만든다 — 스트리밍 중 레지스터가 바뀌어도 프레임 자신과만 대조한다.</summary>
    private static byte[] ExpectedOf(GevFrame frame)
        => SimDevice.BuildPatternFrame(frame.Width, frame.Height, frame.PixelFormatCode, frame.FrameId);

    private static void AssertPatternOfFrame(GevFrame frame)
    {
        var expected = ExpectedOf(frame);
        Assert.Equal(expected.Length, frame.PayloadSize);
        Assert.Equal(expected.Length, frame.Data.Length);
        Assert.True(frame.Data.Span.SequenceEqual(expected),
            $"frame {frame.FrameId} ({frame.Width}x{frame.Height}, 0x{frame.PixelFormatCode:X8}): pixel content differs from the simulator pattern");
    }

    /// <summary>완전한 프레임이고 크기·패턴이 맞는지.</summary>
    private static void AssertCompleteFrameOf(GevFrame frame, int width, int height)
    {
        Assert.True(frame.IsComplete, $"frame {frame.FrameId} ({frame.Width}x{frame.Height}) incomplete: {frame.MissingPackets} of {frame.ExpectedPackets} packets missing");
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        AssertPatternOfFrame(frame);
    }

    // ---------------------------------------------------------------- register access order

    [Fact]
    public async Task Start_AccessesChannelRegistersInTheDocumentedOrder_AndStopReversesIt()
    {
        await using var rig = await SimRig.StartAsync(sim: o => o.MaxPacketSize = 4000);
        var port = new RecordingPort(rig.Device);
        var opt = SimRig.DefaultStreamOpt();
        opt.PacketSizeMode = PacketSizeMode.Auto;
        opt.InterPacketDelay = 1_000;
        // 장치의 포트 대신 기록 포트를 끼운다 — 리센드 출구는 그대로 제어 채널이다. MTU 는 9000 으로 고정해 이분 탐색 경로를 태운다.
        await using var stream = new GevStream(port, rig.Device.Gvcp, IPAddress.Loopback, opt) { MtuResolver = _ => 9000 };

        await stream.StartAsync();

        var scda = ChannelReg(GvbsAddr.ScdaOffset);
        var scp = ChannelReg(GvbsAddr.ScpOffset);
        var scps = ChannelReg(GvbsAddr.ScpsOffset);
        var scpd = ChannelReg(GvbsAddr.ScpdOffset);
        var startLog = port.Log.ToList();
        var writes = startLog.Where(a => a.IsWrite).ToList();
        var trace = string.Join(" | ", startLog);

        // 스트림 채널 0 밖의 레지스터는 건드리지 않는다.
        Assert.All(startLog, a => Assert.InRange(a.Addr, ChannelReg(0), ChannelReg(0x3C)));
        Assert.All(startLog, a => Assert.Equal(4, a.Length));

        // 1. SCDA, 2. SCP — 테스트 패킷의 목적지가 먼저다.
        Assert.True(writes.Count >= 4, $"expected SCDA, SCP, fire tests, SCPS and SCPD writes; saw: {trace}");
        Assert.Equal(new PortAccess(true, scda, 0x7F00_0001, 4), writes[0]);
        Assert.Equal(new PortAccess(true, scp, (uint)stream.LocalPort, 4), writes[1]);

        // 3. SCPS 를 한 번 읽어 장치 플래그를 보존한다 — SCP 뒤, 첫 SCPS 쓰기 앞.
        var scpWrite = startLog.FindIndex(a => a.IsWrite && a.Addr == scp);
        var firstScpsRead = startLog.FindIndex(a => !a.IsWrite && a.Addr == scps);
        var firstScpsWrite = startLog.FindIndex(a => a.IsWrite && a.Addr == scps);
        Assert.True(firstScpsRead >= 0, $"SCPS was never read before it was written: {trace}");
        Assert.True(scpWrite < firstScpsRead && firstScpsRead < firstScpsWrite, $"SCPS read must sit between the SCP write and the first SCPS write: {trace}");

        // 4. 파이어테스트(fire + 단편화 금지) 쓰기들, 5. 최종 SCPS(fire 없음, 단편화 금지, 협상 크기), 6. SCPD.
        var scpsWrites = writes.Where(w => w.Addr == scps).ToList();
        Assert.True(scpsWrites.Count >= 3, $"expected at least two probes (9000 ignored by the capped device, then 1500) before the final SCPS write: {trace}");
        foreach (var probe in scpsWrites.Take(scpsWrites.Count - 1))
        {
            Assert.NotEqual(0u, probe.Value & GvbsAddr.ScpsFireTest);
            Assert.NotEqual(0u, probe.Value & GvbsAddr.ScpsDoNotFragment);
        }
        var final = scpsWrites[scpsWrites.Count - 1];
        Assert.Equal(0u, final.Value & GvbsAddr.ScpsFireTest);
        Assert.NotEqual(0u, final.Value & GvbsAddr.ScpsDoNotFragment);
        Assert.Equal((uint)stream.PacketSize, final.Value & GvbsAddr.ScpsSizeMask);
        Assert.InRange(stream.PacketSize, 3984, 4000);
        Assert.Equal(final, writes[writes.Count - 2]);
        Assert.Equal(new PortAccess(true, scpd, 1_000, 4), writes[writes.Count - 1]);
        // SCP 다음부터 SCPD 앞까지는 SCPS 쓰기뿐이다.
        Assert.All(writes.Skip(2).Take(writes.Count - 3), w => Assert.Equal(scps, w.Addr));
        Assert.Equal((uint)stream.PacketSize, rig.ReadStreamReg(GvbsAddr.ScpsOffset) & GvbsAddr.ScpsSizeMask);

        // 정지: SCP = 0 다음 SCDA = 0, 그 밖의 접근 없음.
        await stream.StopAsync();
        var stopLog = port.Log.Skip(startLog.Count).ToList();
        Assert.Equal(new[] { new PortAccess(true, scp, 0, 4), new PortAccess(true, scda, 0, 4) }, stopLog);
        Assert.Equal(0u, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        Assert.Equal(0u, rig.ReadStreamReg(GvbsAddr.ScdaOffset));
    }

    // ---------------------------------------------------------------- geometry change

    [Fact]
    public async Task GeometryChange_WhileStreaming_NextFramesCarryTheNewSize_AndBuffersGrow()
    {
        await using var rig = await SimRig.StartAsync();
        await using var stream = await rig.OpenStreamAsync();
        await rig.StartTriggeredAcquisitionAsync();
        using (var first = await rig.TriggerAndReceiveAsync(stream))
        {
            AssertCompleteFrameOf(first, 128, 64);
            Assert.Equal(128, first.Stride);
        }

        // 키운다: 풀은 첫 리더 크기(8192)로 잡혀 있으므로 32768 바이트 프레임은 버퍼를 자라게 한다.
        // 시뮬레이터는 프레임마다 레지스터를 다시 읽으므로 쓰기 ACK 뒤의 첫 트리거부터 새 크기다.
        await rig.Device.WriteRegsAsync(new[]
        {
            new KeyValuePair<uint, uint>(SimFeatureAddr.Width, 256),
            new KeyValuePair<uint, uint>(SimFeatureAddr.Height, 128),
        });
        using (var big = await rig.TriggerAndReceiveAsync(stream))
        {
            AssertCompleteFrameOf(big, 256, 128);
            Assert.Equal(256, big.Stride);
            Assert.Equal(256 * 128, big.PayloadSize);
            Assert.Equal(256 * 128, big.ToArray().Length);
        }
        for (var i = 0; i < 3; i++)
        {
            using var f = await rig.TriggerAndReceiveAsync(stream);
            AssertCompleteFrameOf(f, 256, 128);
        }

        // 줄인다: 큰 버퍼 위에 작은 프레임이 실린다 — Data/ToArray 는 유효 바이트만 보여야 한다.
        await rig.Device.WriteRegsAsync(new[]
        {
            new KeyValuePair<uint, uint>(SimFeatureAddr.Width, 96),
            new KeyValuePair<uint, uint>(SimFeatureAddr.Height, 32),
        });
        using (var small = await rig.TriggerAndReceiveAsync(stream))
        {
            AssertCompleteFrameOf(small, 96, 32);
            Assert.Equal(96, small.Stride);
            Assert.Equal(96 * 32, small.PayloadSize);
            Assert.Equal(96 * 32, small.Data.Length);
            Assert.Equal(96 * 32, small.ToArray().Length);
        }
        await rig.StopAcquisitionAsync();

        var s = stream.Stats.Snapshot();
        Assert.Equal(6, s.FramesDelivered);
        Assert.Equal(0, s.FramesIncomplete);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
        Assert.Equal(0, s.FramesDroppedError);
        Assert.Equal(0, s.ResendRequests);
    }

    // ---------------------------------------------------------------- pixel formats

    /// <summary>GVSP 의 2 픽셀-3 바이트 묶음 포맷 가운데 이 이론이 다루는 것(Mono10Packed/Mono12Packed). PFNC lsb 묶음(Mono10p 등)은 일반 올림을 따른다.</summary>
    private static bool IsGvspPacked(uint code) => code is 0x010C_0004u or 0x010C_0006u;

    /// <summary>테스트 자체의 줄 크기 규칙 — 라이브러리와 시뮬레이터 어느 쪽도 빌리지 않는다.</summary>
    private static int ExpectedLineBytes(uint code, int bitsPerPixel, int width)
        => IsGvspPacked(code) ? (width + 1) / 2 * 3 : (width * bitsPerPixel + 7) / 8;

    /// <summary>폭 width 한 줄이 바이트 경계에서 끝나는지. 거짓이고 줄 패딩도 없으면 줄 간격이라는 것이 없어 수신기가 Stride 0 을 내보낸다.</summary>
    private static bool IsLineByteAligned(uint code, int bitsPerPixel, int width)
        => IsGvspPacked(code) ? width % 2 == 0 : (long)width * bitsPerPixel % 8 == 0;

    /// <summary>
    /// 줄 패딩이 없는 이미지 전체 바이트 수 — 데이터가 줄에서 끊기지 않으므로 줄마다가 아니라 전체 픽셀 수로 한 번만 올린다.
    /// 홀수 폭에서 줄 단위 계산(줄 길이 × 높이)과 갈린다.
    /// </summary>
    private static int ExpectedImageBytes(uint code, int bitsPerPixel, int width, int height)
        => ExpectedLineBytes(code, bitsPerPixel, width * height);

    /// <summary>수신기가 GevFrame.Stride 로 내보낼 값 — 줄 패딩이 없는 장치이므로 줄이 바이트 경계에서 끝나지 않으면 0.</summary>
    private static int ExpectedStride(uint code, int bitsPerPixel, int width)
        => IsLineByteAligned(code, bitsPerPixel, width) ? ExpectedLineBytes(code, bitsPerPixel, width) : 0;

    [Theory]
    [InlineData(0x0110_0007u, 16, 122)]   // Mono16
    [InlineData(0x0110_0007u, 16, 121)]
    [InlineData(0x0218_0014u, 24, 122)]   // RGB8
    [InlineData(0x0218_0014u, 24, 121)]
    [InlineData(0x010C_0006u, 12, 122)]   // Mono12Packed — 짝수 폭에서는 묶음 규칙(61 × 3)과 일반 올림(1464 / 8)이 같은 183 이다
    [InlineData(0x010C_0004u, 12, 122)]   // Mono10Packed — 같은 묶음 규칙
    [InlineData(0x010A_0046u, 10, 122)]   // Mono10p — 122 px × 10 bit = 152.5 바이트 → 153 (올림)
    [InlineData(0x010A_0046u, 10, 121)]   // Mono10p — 1210 bit = 151.25 바이트 → 152
    [InlineData(0x0108_0009u, 8, 122)]    // BayerRG8
    [InlineData(0x0108_0009u, 8, 121)]
    public async Task PixelFormat_StrideAndPayloadFollowThePfncBitsPerPixel(uint code, int bitsPerPixel, int width)
    {
        const int height = 40;
        var expectedStride = ExpectedStride(code, bitsPerPixel, width);
        var expectedBytes = ExpectedImageBytes(code, bitsPerPixel, width, height);
        Assert.Equal(bitsPerPixel, PixelFormatInfo.BitsPerPixel(code));
        Assert.Equal(ExpectedLineBytes(code, bitsPerPixel, width), PixelFormatInfo.LineBytes(code, width));
        Assert.Equal(expectedBytes, PixelFormatInfo.FrameBytes(code, width, height));
        // 한 픽셀 좁은 폭에서도 규칙이 맞아야 한다 — 묶음 포맷은 홀수 폭에서 일반 올림과 갈린다(121 px: 183 vs 182).
        Assert.Equal(ExpectedLineBytes(code, bitsPerPixel, width - 1), PixelFormatInfo.LineBytes(code, width - 1));

        await using var rig = await SimRig.StartAsync(sim: o => { o.PixelFormat = code; o.Width = width; o.Height = height; });
        await using var stream = await rig.OpenStreamAsync();
        await rig.StartTriggeredAcquisitionAsync();

        for (var i = 0; i < 2; i++)
        {
            using var f = await rig.TriggerAndReceiveAsync(stream);
            Assert.True(f.IsComplete, $"frame {f.FrameId} (0x{code:X8}) incomplete");
            Assert.Equal(code, f.PixelFormatCode);
            Assert.Equal(width, f.Width);
            Assert.Equal(height, f.Height);
            Assert.Equal(expectedStride, f.Stride);
            Assert.Equal(expectedBytes, f.PayloadSize);
            Assert.Equal(0, f.PaddingX);
            AssertPatternOfFrame(f);
        }
        await rig.StopAcquisitionAsync();
        Assert.Equal(0, stream.Stats.FramesIncomplete);
        Assert.Equal(0, stream.Stats.FramesDroppedError);
    }

    [Theory]
    [InlineData(0x010C_0006u)]   // Mono12Packed
    [InlineData(0x010C_0004u)]   // Mono10Packed
    public async Task PixelFormat_GvspPackedFormats_AtOddWidth_CarryTwoPixelsInThreeBytes(uint code)
    {
        // 홀수 폭에서만 묶음 규칙이 일반 올림과 갈린다: 121 px → ceil(121 / 2) × 3 = 183, 일반 올림은 (121 × 12 + 7) / 8 = 182.
        const int width = 121;
        const int height = 40;
        var lineBytes = ExpectedLineBytes(code, 12, width);
        Assert.Equal(183, lineBytes);
        Assert.NotEqual((width * 12 + 7) / 8, lineBytes);
        Assert.Equal(lineBytes, PixelFormatInfo.LineBytes(code, width));        // 라이브러리 쪽은 맞다 — 항상 엄격히 본다

        // 그 줄 길이는 줄 사이에 패딩이 있을 때의 값이다. 패딩이 없으면 묶음이 줄에서 끊기지 않고 이어 붙으므로
        // 전체 픽셀 수로 한 번만 올린다 — 줄마다 올리면 높이만큼 더 세게 된다.
        var expectedBytes = ExpectedImageBytes(code, 12, width, height);
        Assert.Equal(7260, expectedBytes);                                      // 4840 px = 2420 묶음 × 3
        Assert.NotEqual(lineBytes * height, expectedBytes);                     // 183 × 40 = 7320 — 60 바이트 더 센다
        Assert.Equal(expectedBytes, PixelFormatInfo.FrameBytes(code, width, height));
        // 줄이 바이트 경계에서 끝나지 않고 줄 패딩도 없다 — 줄 간격이라는 것이 없다.
        Assert.False(PixelFormatInfo.IsLineByteAligned(code, width));

        // 장치 쪽(시뮬레이터)도 같은 규칙이어야 프레임 크기가 어긋나지 않는다.
        var simFrame = SimDevice.BuildPatternFrame(width, height, code, 1);
        Assert.True(simFrame.Length == expectedBytes,
            "SimDevice must size a GVSP packed image with no line padding as ceil(width * height / 2) * 3 "
            + "(tests/GevSharp.Sim/SimDevice.Gvsp.cs, SimImageBytes): "
            + $"at {width}x{height} it builds {simFrame.Length} bytes for 0x{code:X8} while the receiver expects {expectedBytes}, "
            + $"so the two would disagree by {Math.Abs(simFrame.Length - expectedBytes)} bytes per frame.");

        // 실제 스트림도 같은 크기와 내용이어야 한다.
        await using var rig = await SimRig.StartAsync(sim: o => { o.PixelFormat = code; o.Width = width; o.Height = height; });
        await using var stream = await rig.OpenStreamAsync();
        await rig.StartTriggeredAcquisitionAsync();
        using var frame = await rig.TriggerAndReceiveAsync(stream);
        Assert.True(frame.IsComplete, $"frame {frame.FrameId} (0x{code:X8}) incomplete");
        Assert.Equal(0, frame.Stride);
        Assert.Equal(expectedBytes, frame.PayloadSize);
        AssertPatternOfFrame(frame);
        await rig.StopAcquisitionAsync();
    }

    // ---------------------------------------------------------------- large frames, scattered loss

    private const int LargeWidth = 1024;
    private const int LargeHeight = 768;
    /// <summary>1024×768 Mono8 = 786432 바이트 → SCPS 1500 에서 538 페이로드 패킷(마지막은 짧다).</summary>
    private const int LargePackets = (LargeWidth * LargeHeight + DataBytes1500 - 1) / DataBytes1500;

    /// <summary>첫 페이로드, 워드 경계 안팎의 몇 개, 마지막(짧은) 페이로드 — 서로 떨어진 구멍 다섯.</summary>
    private static readonly uint[] s_scatteredHoles = { 1, 7, 100, 300, LargePackets };

    [Fact]
    public async Task LargeFrames_WithScatteredDrops_AreRecoveredByResend_OneRequestPerHole()
    {
        // 트리거마다 한 프레임: 리센드 이력(최근 8 프레임)이 러너 속도 때문에 소진되지 않고, 블록 1..6 이 그대로 온다.
        await using var rig = await SimRig.StartAsync(sim: o =>
        {
            o.Width = LargeWidth;
            o.Height = LargeHeight;
            o.DropPacket = (frame, packet) => frame % 2 == 0 && Array.IndexOf(s_scatteredHoles, packet) >= 0;
        });
        await using var stream = await rig.OpenStreamAsync();
        await rig.StartTriggeredAcquisitionAsync();

        var injected = new List<ulong>();
        for (var i = 0; i < 6; i++)
        {
            var id = await rig.TriggerAsync();
            using var f = await SimRig.ReceiveAsync(stream, 5000);
            Assert.Equal(id, f.FrameId);
            Assert.True(f.IsComplete, $"frame {f.FrameId} incomplete after resend: {f.MissingPackets} of {f.ExpectedPackets} packets missing");
            Assert.Equal(LargePackets, f.ExpectedPackets);
            Assert.Equal(LargeWidth, f.Stride);
            AssertPatternOfFrame(f);
            if (id % 2 == 0) injected.Add(id);
        }
        await rig.StopAcquisitionAsync();

        Assert.Equal(3, injected.Count);
        var s = stream.Stats.Snapshot();
        Assert.Equal(0, s.FramesIncomplete);
        Assert.Equal(0, s.PacketsMissing);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
        Assert.True(s.ResendRequests >= injected.Count * s_scatteredHoles.Length, $"resend requests {s.ResendRequests} < {injected.Count} frames × {s_scatteredHoles.Length} holes");
        Assert.True(s.ResendRecovered >= injected.Count * s_scatteredHoles.Length, $"resend recovered {s.ResendRecovered} < {injected.Count} frames × {s_scatteredHoles.Length} holes");
        Assert.True(rig.Sim.PacketsDropped >= injected.Count * s_scatteredHoles.Length);

        var requests = rig.Sim.ResendRequests;
        Assert.NotEmpty(requests);
        Assert.All(requests, r => Assert.True(r.IsAccepted, $"resend for block {r.BlockId} was not accepted (sender {r.Sender})"));
        // 구멍마다 정확히 그 id 하나를 요청한다 — 받은 패킷이나 아직 오지 않은 꼬리를 함께 달라고 하지 않는다.
        Assert.All(requests, r => Assert.True(r.FirstPacketId == r.LastPacketId && Array.IndexOf(s_scatteredHoles, r.FirstPacketId) >= 0,
            $"resend request {r.FirstPacketId}..{r.LastPacketId} for block {r.BlockId} covers more than one injected hole"));
        foreach (var block in injected)
        {
            foreach (var hole in s_scatteredHoles)
            {
                Assert.True(requests.Any(r => r.BlockId == block && r.FirstPacketId <= hole && hole <= r.LastPacketId),
                    $"hole {hole} of block {block} was never requested (requests: {string.Join(", ", requests.Where(r => r.BlockId == block).Select(r => r.FirstPacketId))})");
            }
        }
    }

    [Fact]
    public async Task ResendBudget_Exhausted_AbandonsTheFrame_AfterTheRequestedHolesWereFilled()
    {
        // 538 패킷 × 0.005 = 2.69 → 프레임당 최대 3 패킷만 요청한다. 구멍 다섯 중 예산 안의 것만 되살아나고 나머지는 비어 남는다.
        // 어느 구멍을 고르는지는 구현 세부(훑는 순서)라 못 박지 않는다 — 요청이 예산 안이고, 요청한 구멍은 채워지고, 요청하지 않은 구멍만 0 인지를 본다.
        await using var rig = await SimRig.StartAsync(sim: o =>
        {
            o.Width = LargeWidth;
            o.Height = LargeHeight;
            o.DropPacket = (frame, packet) => frame % 2 == 0 && Array.IndexOf(s_scatteredHoles, packet) >= 0;
        });
        const double ratio = 0.005;
        var budget = (int)Math.Ceiling(LargePackets * ratio);
        Assert.Equal(3, budget);
        var opt = SimRig.DefaultStreamOpt();
        opt.PacketRequestRatio = ratio;
        opt.DeliverIncompleteFrames = true;
        var dropped = new List<GevFrameDiag>();
        await using var stream = await rig.OpenStreamAsync(opt, st => st.FrameDropped += d => { lock (dropped) dropped.Add(d); });
        await rig.StartTriggeredAcquisitionAsync();

        var missingByBlock = new Dictionary<ulong, int>();
        for (var i = 0; i < 6; i++)
        {
            var id = await rig.TriggerAsync();
            using var f = await SimRig.ReceiveAsync(stream, 5000);
            Assert.Equal(id, f.FrameId);
            Assert.Equal(LargePackets, f.ExpectedPackets);
            if (id % 2 != 0)
            {
                Assert.True(f.IsComplete, $"block {id} had no loss injected but came out incomplete ({f.MissingPackets} missing)");
                AssertPatternOfFrame(f);
                continue;
            }

            Assert.False(f.IsComplete, $"block {id} lost {s_scatteredHoles.Length} packets with a budget of {budget} but came out complete");
            var requests = rig.Sim.ResendRequests.Where(r => r.BlockId == id).ToList();
            Assert.NotEmpty(requests);
            Assert.All(requests, r => Assert.True(r.IsAccepted, $"resend for block {r.BlockId} was not accepted (sender {r.Sender})"));
            var requestedPackets = requests.Sum(r => (long)(r.LastPacketId - r.FirstPacketId + 1));
            Assert.True(requestedPackets <= budget,
                $"block {id}: {requestedPackets} packets requested ({string.Join(", ", requests.Select(r => $"{r.FirstPacketId}..{r.LastPacketId}"))}) exceed the budget of {budget}");
            var filled = s_scatteredHoles.Where(h => requests.Any(r => r.FirstPacketId <= h && h <= r.LastPacketId)).ToArray();
            Assert.NotEmpty(filled);
            var missing = s_scatteredHoles.Length - filled.Length;
            Assert.Equal(missing, f.MissingPackets);
            missingByBlock[id] = missing;

            // 요청한 구멍은 리센드로 채워지고, 요청하지 않은 구멍만 0 이다.
            var expected = ExpectedOf(f);
            foreach (var hole in s_scatteredHoles.Except(filled))
            {
                var offset = (int)(hole - 1) * DataBytes1500;
                Array.Clear(expected, offset, Math.Min(DataBytes1500, expected.Length - offset));
            }
            Assert.True(f.Data.Span.SequenceEqual(expected),
                $"block {id}: holes {string.Join(", ", filled)} should be filled by resend and only {string.Join(", ", s_scatteredHoles.Except(filled))} zeroed");
        }
        await rig.StopAcquisitionAsync();

        Assert.Equal(3, missingByBlock.Count);
        lock (dropped)
        {
            Assert.NotEmpty(dropped);
            Assert.All(dropped, d =>
            {
                Assert.Equal(GevFrameDropReason.Incomplete, d.Reason);
                Assert.True(missingByBlock.TryGetValue(d.FrameId, out var missing), $"FrameDropped for block {d.FrameId}, which had no loss injected");
                Assert.Equal(missing, d.MissingPackets);
            });
        }
        var s = stream.Stats.Snapshot();
        Assert.Equal(3, s.FramesIncomplete);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
        Assert.Equal(6, s.FramesDelivered);
    }

    // ---------------------------------------------------------------- block id restart

    [Fact]
    public async Task SingleFrameAcquisitions_RestartingBlockIdsAt1_AreNotMistakenForDuplicates()
    {
        await using var rig = await SimRig.StartAsync();
        await using var stream = await rig.OpenStreamAsync(PoolFor(3));

        for (var round = 1; round <= 3; round++)
        {
            // 단일 프레임 촬영을 반복하는 장치는 매번 블록 1 부터 보낸다.
            rig.Sim.SeedBlockId(0);
            await rig.StartAcquisitionAsync(SimFeatureAddr.AcquisitionModeSingleFrame);
            using (var frame = await SimRig.ReceiveAsync(stream))
            {
                Assert.Equal(1ul, frame.FrameId);
                Assert.True(frame.IsComplete, $"round {round}: block 1 incomplete ({frame.MissingPackets} missing)");
                AssertPatternOfFrame(frame);
            }
            await SimRig.WaitUntilAsync(() => !rig.Sim.IsAcquiring, 2000, $"round {round}: single-frame acquisition to finish");
            await Task.Delay(30);
            Assert.False(stream.TryReceive(out var extra), $"round {round}: an extra frame {extra?.FrameId} was delivered after the single frame");
            Assert.Equal(round, rig.Sim.FramesSent);
        }

        var s = stream.Stats.Snapshot();
        Assert.Equal(3, s.FramesDelivered);
        Assert.Equal(3, s.FramesCompleted);
        Assert.Equal(0, s.FramesIncomplete);
        Assert.Equal(0, s.PacketsDuplicated);
        Assert.Equal(0, s.PacketsIgnored);
        Assert.Equal(1ul, s.LastFrameId);
    }

    // ---------------------------------------------------------------- fixed local port

    [Fact]
    public async Task FixedLocalPort_IsHonoured_AndCanBeReusedByTheNextStreamAfterStop()
    {
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        await using var rig = await SimRig.StartAsync();
        var opt1 = SimRig.DefaultStreamOpt();
        opt1.LocalPort = port;
        await using var first = await rig.OpenStreamAsync(opt1);   // 중간에 명시적으로 닫지만, 단정이 실패해도 포트가 잡힌 채 남지 않게 한다
        Assert.Equal(port, first.LocalPort);
        Assert.Equal((uint)port, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        await rig.StartAcquisitionAsync();
        using (var f = await SimRig.ReceiveAsync(first)) Assert.True(f.IsComplete);
        await first.DisposeAsync();
        Assert.Equal(0u, rig.ReadStreamReg(GvbsAddr.ScpOffset));

        // 같은 포트로 두 번째 스트림 — 획득은 켜져 있으므로 채널을 다시 열면 바로 흐른다.
        var opt2 = SimRig.DefaultStreamOpt();
        opt2.LocalPort = port;
        await using var second = await rig.OpenStreamAsync(opt2);
        Assert.Equal(port, second.LocalPort);
        Assert.Equal((uint)port, rig.ReadStreamReg(GvbsAddr.ScpOffset));
        using (var f = await SimRig.ReceiveAsync(second))
        {
            Assert.True(f.IsComplete);
            AssertPatternOfFrame(f);
        }
        await rig.StopAcquisitionAsync();

        // 포트가 아직 잡혀 있는 동안 같은 포트를 요구하는 세 번째 스트림은 시작에 실패하고 장치 채널을 되돌린다.
        var opt3 = SimRig.DefaultStreamOpt();
        opt3.LocalPort = port;
        await using var third = await rig.Device.OpenStreamAsync(opt3);
        await Assert.ThrowsAsync<SocketException>(() => third.StartAsync());
        Assert.False(third.IsStarted);
        Assert.True(second.IsStarted);
        Assert.Equal((uint)port, rig.ReadStreamReg(GvbsAddr.ScpOffset));
    }

    // ---------------------------------------------------------------- payload size hint

    [Fact]
    public async Task PayloadSizeHint_SmallerThanTheActualFrame_StillDeliversCompleteFrames()
    {
        await using var rig = await SimRig.StartAsync();
        var opt = SimRig.DefaultStreamOpt();
        opt.PayloadSize = 1024;   // 실제 프레임은 8192 바이트
        await using var stream = await rig.OpenStreamAsync(opt);
        await rig.StartTriggeredAcquisitionAsync();

        for (var i = 0; i < 5; i++)
        {
            using var f = await rig.TriggerAndReceiveAsync(stream);
            Assert.True(f.IsComplete, $"frame {f.FrameId} incomplete with a small PayloadSize hint");
            Assert.Equal(128 * 64, f.PayloadSize);
            AssertPatternOfFrame(f);
        }
        await rig.StopAcquisitionAsync();

        var s = stream.Stats.Snapshot();
        Assert.Equal(0, s.FramesIncomplete);
        Assert.Equal(0, s.FramesDroppedError);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
        Assert.Equal(0, s.PacketsIgnored);
    }
}
