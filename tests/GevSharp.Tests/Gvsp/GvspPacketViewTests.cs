using System.Buffers.Binary;
using GevSharp.Gvsp;
using GevSharp.Pfnc;

namespace GevSharp.Tests.Gvsp;

public class GvspPacketViewTests
{
    private const uint Mono8 = 0x01080001;
    private const uint Mono12Packed = 0x010C0006;
    private const uint Mono10Packed = 0x010C0004;
    private const uint Mono10p = 0x010A0046;
    private const uint Rgb8 = 0x02180014;
    private const uint Yuv411 = 0x020C001E;
    private const uint VendorMono12 = 0x810C0001;

    private static byte[] StdPacket(ushort status, ushort blockId, byte contentType, uint packetId, int dataLength)
    {
        var p = new byte[GvspConst.HeaderSize + dataLength];
        BinaryPrimitives.WriteUInt16BigEndian(p, status);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), blockId);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), ((uint)contentType << 24) | (packetId & 0x00FF_FFFF));
        return p;
    }

    private static byte[] ExtPacket(ushort status, ulong blockId, byte contentType, uint packetId, int dataLength)
    {
        var p = new byte[GvspConst.ExtendedHeaderSize + dataLength];
        BinaryPrimitives.WriteUInt16BigEndian(p, status);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), 0x8000_0000u | ((uint)contentType << 24));
        BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(8), blockId);
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), packetId);
        return p;
    }

    [Fact]
    public void ParsesStandardHeader()
    {
        var p = StdPacket(GvspConst.StatusPacketResend, 0x1234, GvspConst.ContentPayload, 0x00ABCDEF, 5);

        Assert.True(GvspPacketView.TryParse(p, p.Length, out var v));
        Assert.Equal(GvspConst.StatusPacketResend, v.Status);
        Assert.True(v.IsResent);
        Assert.False(v.IsError);
        Assert.False(v.IsExtendedId);
        Assert.Equal(GvspConst.ContentPayload, v.ContentType);
        Assert.Equal(0x1234UL, v.BlockId);
        Assert.Equal(0x00ABCDEFu, v.PacketId);
        Assert.Equal(GvspConst.HeaderSize, v.HeaderSize);
        Assert.Equal(GvspConst.HeaderSize, v.DataOffset);
        Assert.Equal(5, v.DataLength);
        Assert.Equal(5, v.Data.Length);
    }

    [Fact]
    public void ParsesExtendedHeader()
    {
        var p = ExtPacket(GvspConst.StatusSuccess, 0x0102030405060708UL, GvspConst.ContentTrailer, 0x01020304u, 8);

        Assert.True(GvspPacketView.TryParse(p, p.Length, out var v));
        Assert.True(v.IsExtendedId);
        Assert.Equal(GvspConst.ContentTrailer, v.ContentType);
        Assert.Equal(0x0102030405060708UL, v.BlockId);
        Assert.Equal(0x01020304u, v.PacketId);
        Assert.Equal(GvspConst.ExtendedHeaderSize, v.HeaderSize);
        Assert.Equal(8, v.DataLength);
    }

    [Fact]
    public void ContentTypeIgnoresExtendedIdBit()
    {
        // EI 비트가 켜져도 content type 은 [30:24] 만 본다 — 0x83 이 아니라 3.
        var p = ExtPacket(0, 7, GvspConst.ContentPayload, 9, 0);
        Assert.True(GvspPacketView.TryParse(p, p.Length, out var v));
        Assert.Equal(GvspConst.ContentPayload, v.ContentType);
    }

    [Fact]
    public void ErrorStatusIsRecognised()
    {
        var p = StdPacket(GvspConst.StatusPacketUnavailable, 1, GvspConst.ContentPayload, 3, 0);
        Assert.True(GvspPacketView.TryParse(p, p.Length, out var v));
        Assert.True(v.IsError);
        Assert.False(v.IsResent);
    }

    [Fact]
    public void RejectsPacketsShorterThanHeader()
    {
        var std = StdPacket(0, 1, GvspConst.ContentLeader, 0, 0);
        Assert.False(GvspPacketView.TryParse(std, GvspConst.HeaderSize - 1, out _));
        Assert.True(GvspPacketView.TryParse(std, GvspConst.HeaderSize, out _));

        var ext = ExtPacket(0, 1, GvspConst.ContentLeader, 0, 0);
        Assert.False(GvspPacketView.TryParse(ext, GvspConst.ExtendedHeaderSize - 1, out _));
        Assert.True(GvspPacketView.TryParse(ext, GvspConst.ExtendedHeaderSize, out _));

        Assert.False(GvspPacketView.TryParse(std, std.Length + 1, out _));
        Assert.False(GvspPacketView.TryParse(std, -1, out _));
        Assert.Throws<GevException>(() => GvspPacketView.Parse(new byte[3]));
    }

    [Fact]
    public void ReadsImageLeaderFields()
    {
        var p = StdPacket(0, 1, GvspConst.ContentLeader, 0, GvspConst.ImageLeaderDataSize);
        var d = p.AsSpan(GvspConst.HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(d, 0);
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(2), (ushort)(GvspConst.PayloadImage | GvspConst.PayloadChunkFlag));
        BinaryPrimitives.WriteUInt64BigEndian(d.Slice(4), 0x1122334455667788UL);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(12), Mono12Packed);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(16), 640);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(20), 480);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(24), 16);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(28), 8);
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(32), 4);
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(34), 2);

        Assert.True(GvspPacketView.TryParse(p, p.Length, out var v));
        Assert.True(v.TryReadImageLeader(out var leader));
        Assert.Equal(GvspConst.PayloadImage, leader.PayloadTypeBase);
        Assert.True(leader.HasChunkData);
        Assert.Equal(0x1122334455667788UL, leader.Timestamp);
        Assert.Equal(Mono12Packed, leader.PixelFormat);
        Assert.Equal(640u, leader.SizeX);
        Assert.Equal(480u, leader.SizeY);
        Assert.Equal(16u, leader.OffsetX);
        Assert.Equal(8u, leader.OffsetY);
        Assert.Equal(4, leader.PaddingX);
        Assert.Equal(2, leader.PaddingY);
        Assert.Equal(12, leader.BitsPerPixel);
        Assert.Equal(964, leader.LineBytes);
        Assert.Equal(964L * 480 + 2, leader.ImageBytes);

        // 리더 데이터가 짧으면 실패한다.
        Assert.True(GvspPacketView.TryParse(p, p.Length - 1, out var shortView));
        Assert.False(shortView.TryReadImageLeader(out _));
        Assert.Throws<GevException>(() => ReadLeaderOfTruncated(p));
    }

    private static GvspImageLeader ReadLeaderOfTruncated(byte[] packet)
        => GvspPacketView.Parse(packet.AsSpan(0, packet.Length - 1)).ReadImageLeader();

    [Fact]
    public void ExtendedChunkLeaderCountsAsChunkData()
    {
        var leader = new GvspImageLeader(0, GvspConst.PayloadExtendedChunkData, 0, Mono8, 10, 10, 0, 0, 0, 0);
        Assert.True(leader.HasChunkData);
        Assert.Equal(GvspConst.PayloadExtendedChunkData, leader.PayloadTypeBase);

        var plain = new GvspImageLeader(0, GvspConst.PayloadImage, 0, Mono8, 10, 10, 0, 0, 0, 0);
        Assert.False(plain.HasChunkData);
    }

    [Fact]
    public void ReadsTrailerFields()
    {
        var p = StdPacket(0, 1, GvspConst.ContentTrailer, 12, GvspConst.TrailerDataSize);
        var d = p.AsSpan(GvspConst.HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(2), GvspConst.PayloadImage);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(4), 300);

        Assert.True(GvspPacketView.TryParse(p, p.Length, out var v));
        Assert.True(v.TryReadTrailer(out var trailer));
        Assert.Equal(GvspConst.PayloadImage, trailer.PayloadType);
        Assert.Equal(300u, trailer.SizeY);

        Assert.True(GvspPacketView.TryParse(p, p.Length - 1, out var shortView));
        Assert.False(shortView.TryReadTrailer(out _));
    }

    [Theory]
    [InlineData(Mono8, 640u, 480u, 0, 0, 640, 307200)]
    [InlineData(Mono12Packed, 640u, 480u, 4, 0, 964, 462720)]
    [InlineData(Mono12Packed, 641u, 480u, 4, 0, 967, 464160)]      // 홀수 폭: 마지막 2픽셀 묶음 3바이트를 다 센다(963) + padding 4
    [InlineData(Mono10Packed, 3u, 2u, 0, 0, 6, 9)]                  // 줄 패딩이 없으면 묶음이 줄에서 끊기지 않는다 — 6픽셀 = 3묶음 = 9바이트(줄마다 올리면 12)
    [InlineData(Mono12Packed, 2591u, 64u, 0, 0, 3888, 248736)]      // 실측 기하 — 줄마다 올리면 248,832 로 96 바이트를 더 센다
    [InlineData(Rgb8, 640u, 10u, 8, 16, 1928, 19296)]
    [InlineData(Mono10p, 7u, 3u, 0, 0, 9, 27)]
    [InlineData(Mono10p, 5u, 1u, 0, 0, 7, 7)]                       // PFNC p 는 바이트 경계까지만
    [InlineData(Yuv411, 5u, 1u, 0, 0, 12, 12)]                      // 4:1:1 은 4픽셀 = 6바이트 묶음
    [InlineData(VendorMono12, 3u, 1u, 0, 0, 5, 5)]                  // 표에 없는 코드: ceil(width × bpp / 8)
    public void LineAndImageBytesFollowPixelFormatAndPadding(uint pixelFormat, uint width, uint height, int paddingX, int paddingY, long lineBytes, long imageBytes)
    {
        var leader = new GvspImageLeader(0, GvspConst.PayloadImage, 0, pixelFormat, width, height, 0, 0, (ushort)paddingX, (ushort)paddingY);
        Assert.Equal(lineBytes, leader.LineBytes);
        Assert.Equal(imageBytes, leader.ImageBytes);
    }

    [Fact]
    public void LineBytesIsTheOneDefinitionSharedWithPixelFormatInfo()
    {
        // 수신기가 GevFrame.Stride 로 내보내는 값이 표의 줄 길이와 같아야 언팩·접기 루틴이 그 stride 를 그대로 받는다 — 홀수 폭 Packed 가 갈림길.
        foreach (PixelFormat f in Enum.GetValues(typeof(PixelFormat)))
        {
            if (!PixelFormatInfo.IsKnown(f)) continue;
            foreach (var width in new[] { 1, 3, 5, 641 })
            {
                var leader = new GvspImageLeader(0, GvspConst.PayloadImage, 0, (uint)f, (uint)width, 2, 0, 0, 4, 0);
                Assert.Equal(PixelFormatInfo.LineBytes(f, width) + 4L, leader.LineBytes);
            }
        }

        // 리더 치수는 검증 전이라 큰 값도 예외 없이 long 으로 나온다(수신기가 int 한계를 따로 본다).
        var huge = new GvspImageLeader(0, GvspConst.PayloadImage, 0, Mono12Packed, uint.MaxValue, 1, 0, 0, 0, 0);
        Assert.Equal(((long)uint.MaxValue + 1) / 2 * 3, huge.LineBytes);
        var noBits = new GvspImageLeader(0, GvspConst.PayloadImage, 0, 0u, 10, 1, 0, 0, 2, 0);
        Assert.Equal(2L, noBits.LineBytes);
    }
}
