using GevSharp.Gvcp;
using GevSharp.Gvsp;

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 청크가 붙은 프레임의 조립. 이런 프레임은 리더만 보고 크기를 알 수 없다 — payload_type 의 bit14 나 확장 청크 타입이
/// "이미지 뒤에 더 있다" 고 알릴 뿐이고, 실제 끝은 트레일러까지 받아 봐야 정해진다. 그래서 이미지 프레임과 달리
/// ① 리더 크기로 잘라 내지 않고 ② 버퍼를 넘치면 그 프레임은 버리되 배운 크기로 다음 버퍼를 키운다.
/// 이 파일은 그 두 가지와, 청크 표시가 없을 때는 예전대로 리더 크기로 잘린다는 것을 함께 못 박는다.
/// </summary>
public class ChunkStreamingTests
{
    private const uint Mono8 = 0x0108_0001;
    private const int Width = 64;
    private const int Height = 32;
    private const int ImageBytes = Width * Height;
    private const int ChunkBytes = 300;

    private static GevStreamOpt OptWithRoomForChunks()
    {
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 2;
        // 장치의 PayloadSize 노드 값을 넣어 준 상황 — 청크까지 들어가는 버퍼로 시작한다.
        opt.PayloadSize = ImageBytes + ChunkBytes;
        return opt;
    }

    [Theory]
    [InlineData(false)]     // payload_type = image | bit14
    [InlineData(true)]      // payload_type = extended chunk data
    public async Task ChunkBearingFrame_IsDeliveredWholeWithItsChunkBytes(bool extendedType)
    {
        await using var rig = new StreamRig(OptWithRoomForChunks());
        await rig.StartAsync();

        var sent = rig.Sender.BuildChunkFrame(1, Width, Height, Mono8, ChunkBytes, extendedType);
        Assert.Equal(ImageBytes + ChunkBytes, sent.Data.Length);
        rig.Sender.SendFrame(sent);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete, $"frame {frame.FrameId} incomplete: {frame.MissingPackets} of {frame.ExpectedPackets} packets missing");
        Assert.True(frame.HasChunkData);
        // 유효 바이트는 이미지가 아니라 실제로 받은 끝까지다 — 리더 크기로 잘리면 청크가 통째로 사라진다.
        Assert.Equal(ImageBytes + ChunkBytes, frame.PayloadSize);
        Assert.Equal(ImageBytes + ChunkBytes, frame.Data.Length);
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data), "delivered bytes differ from what the device sent");
        // 그래서 이미지가 어디서 끝나는지는 따로 알려 줘야 한다 — PayloadSize 를 이미지 크기로 알면 청크가 화소로 셈된다.
        Assert.Equal(ImageBytes, frame.ImageSize);
        Assert.True(frame.Data.Span.Slice(0, frame.ImageSize).SequenceEqual(sent.Data.AsSpan(0, ImageBytes)));
        // 이미지 기하는 그대로 나온다 — 청크가 붙었다고 리더의 나머지를 못 쓰는 것은 아니다.
        Assert.Equal(Width, frame.Width);
        Assert.Equal(Height, frame.Height);
        Assert.Equal(Width, frame.Stride);
        Assert.Equal(Mono8, frame.PixelFormatCode);
        Assert.Equal(0, stat(rig).FramesDroppedError);
    }

    [Fact]
    public async Task WithoutTheChunkFlag_TheSameBytesAreCutToTheLeaderSize()
    {
        // 대조군: 바이트는 같고 payload_type 만 평범한 이미지다. 그러면 리더가 크기의 전부이므로 뒤쪽은 버려진다.
        // 위 테스트와 이 테스트의 차이는 오직 그 표시 하나이고, 표시를 무시하면 위가 이것과 같아진다.
        await using var rig = new StreamRig(OptWithRoomForChunks());
        await rig.StartAsync();

        var sent = rig.Sender.BuildChunkFrame(1, Width, Height, Mono8, ChunkBytes);
        sent.PayloadType = GvspConst.PayloadImage;
        rig.Sender.SendFrame(sent);

        using var frame = await rig.ReceiveAsync();
        Assert.True(frame.IsComplete);
        Assert.False(frame.HasChunkData);
        Assert.Equal(ImageBytes, frame.PayloadSize);
        Assert.Equal(ImageBytes, frame.ImageSize);            // 청크가 없으면 둘이 같다
        Assert.True(frame.Data.Span.SequenceEqual(sent.Data.AsSpan(0, ImageBytes)));
    }

    [Fact]
    public async Task ChunkFrameBiggerThanTheBuffer_IsDropped_ThenTheBuffersGrowAndTheNextOneFits()
    {
        // PayloadSize 힌트를 주지 않으면 풀은 첫 리더가 알린 이미지 크기로 잡힌다 — 청크는 그 밖이다.
        // 넘친 프레임은 버리되 끝까지 크기를 배워, 다음 프레임은 한 번에 담긴다.
        var opt = StreamRig.DefaultOpt();
        opt.BufferCount = 2;
        await using var rig = new StreamRig(opt);
        await rig.StartAsync();

        rig.Sender.SendFrame(rig.Sender.BuildChunkFrame(1, Width, Height, Mono8, ChunkBytes));

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1ul, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Error, diag.Reason);
        Assert.Equal(GvcpConst.StatusOverflow, diag.Code);
        Assert.Equal(1, stat(rig).FramesDroppedError);

        // 두 번째 프레임은 배운 크기로 잡힌 버퍼에 그대로 들어간다.
        var second = rig.Sender.BuildChunkFrame(2, Width, Height, Mono8, ChunkBytes, seed: 9);
        rig.Sender.SendFrame(second);

        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2ul, frame.FrameId);
        Assert.True(frame.IsComplete, $"frame {frame.FrameId} incomplete after the pool grew");
        Assert.True(frame.HasChunkData);
        Assert.Equal(ImageBytes + ChunkBytes, frame.PayloadSize);
        Assert.True(frame.Data.Span.SequenceEqual(second.Data));
        Assert.True(rig.Stream.PoolBufferBytes >= ImageBytes + ChunkBytes,
            $"pool buffers are {rig.Stream.PoolBufferBytes} bytes, too small for the {ImageBytes + ChunkBytes}-byte frame it just delivered");
    }

    [Fact]
    public async Task IncompleteChunkFrame_StillReportsTheBytesItReceived()
    {
        // 청크 프레임은 기대 크기가 없으므로 "몇 바이트가 유효한가" 를 받은 끝으로 정한다. 구멍이 뚫려도 그 규칙은 같고,
        // 못 받은 자리는 0 이어야 한다(이전 프레임의 픽셀이 새어 보이면 안 된다).
        var opt = OptWithRoomForChunks();
        opt.DeliverIncompleteFrames = true;
        await using var rig = new StreamRig(opt);
        rig.Resend.Behaviour = TestResendPort.Mode.Never;
        await rig.StartAsync();

        var sent = rig.Sender.BuildChunkFrame(1, Width, Height, Mono8, ChunkBytes);
        rig.Sender.Drop.Add((1ul, 1u));
        rig.Sender.SendFrame(sent);

        using var frame = await rig.ReceiveAsync();
        Assert.False(frame.IsComplete);
        Assert.Equal(1, frame.MissingPackets);
        Assert.True(frame.HasChunkData);
        Assert.Equal(ImageBytes + ChunkBytes, frame.PayloadSize);
        Assert.Equal(ImageBytes, frame.ImageSize);

        var dataBytes = GvspConst.DataBytesPerPacket(rig.Sender.PacketSize, rig.Sender.ExtendedIds);
        var hole = Math.Min(dataBytes, frame.PayloadSize);
        Assert.All(frame.Data.Span.Slice(0, hole).ToArray(), b => Assert.Equal(0, b));
        Assert.True(frame.Data.Span.Slice(hole).SequenceEqual(sent.Data.AsSpan(hole)));
    }

    [Fact]
    public async Task ChunkDataPayloadType_IsReportedAsUnsupported_NotAsABrokenHeader()
    {
        // 실기에서 나온 모양이다. 청크 모드를 켜면 이 장치는 payload_type 을 4(chunk data)로 바꾸고 리더를 12바이트만 보낸다 —
        // flags·payload_type·timestamp 뿐이고 기하가 없다(이미지 자체가 청크 스트림 안에 들어간다).
        // 그것을 36바이트 이미지 리더로 먼저 읽으려 들면 "헤더가 깨졌다" 가 되어, 다루지 않는 종류라는 사실이 사유에서 사라진다.
        // 종류를 먼저 보아야 정직한 사유가 나온다: 우리가 조립하지 않는 종류다.
        await using var rig = new StreamRig(OptWithRoomForChunks());
        await rig.StartAsync();

        rig.Sender.SendShortLeader(1, GvspConst.PayloadChunkData, dataBytes: 12);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1ul, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Unsupported, diag.Reason);
        Assert.Equal(GvspConst.PayloadChunkData, diag.Code);
        var s = stat(rig);
        Assert.Equal(1, s.FramesDroppedUnsupported);
        Assert.Equal(0, s.FramesDroppedError);

        // 수신기는 살아 있고 다음 이미지 프레임을 정상으로 받는다.
        var next = rig.Sender.BuildFrame(2, Width, Height, Mono8);
        rig.Sender.SendFrame(next);
        using var frame = await rig.ReceiveAsync();
        Assert.Equal(2ul, frame.FrameId);
        Assert.True(frame.IsComplete);
    }

    [Fact]
    public async Task ALeaderTooShortToNameItsTypeIsStillAnError()
    {
        // 반대쪽 경계 — 종류조차 읽을 수 없을 만큼 짧으면 그것은 깨진 헤더가 맞다.
        await using var rig = new StreamRig(OptWithRoomForChunks());
        await rig.StartAsync();

        rig.Sender.SendShortLeader(1, GvspConst.PayloadImage, dataBytes: 2);

        var diag = await rig.WaitDroppedAsync();
        Assert.Equal(1ul, diag.FrameId);
        Assert.Equal(GevFrameDropReason.Error, diag.Reason);
        Assert.Equal(1, stat(rig).FramesDroppedError);
    }

    private static GevStreamStatsSnap stat(StreamRig rig) => rig.Stream.Stats.Snapshot();
}
