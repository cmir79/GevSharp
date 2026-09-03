using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GevSharp.Gvsp;

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 합성 프레임을 GVSP 패킷으로 쪼개 루프백으로 쏘는 테스트용 송신기. 패킷 조립은 프로토콜 노트의 레이아웃대로 독립 구현한다
/// (수신기의 파서를 빌려 쓰면 양쪽이 같은 오해를 해도 통과해 버린다). 보낸 프레임을 기억해 두었다가 리센드 요청에 답할 수 있다.
/// 표준 헤더(8바이트, 16비트 블록·24비트 패킷 id)와 확장 헤더(20바이트, 64비트 블록·32비트 패킷 id) 둘 다 만든다.
/// </summary>
internal sealed class GvspTestSender : IDisposable
{
    /// <summary>합성 프레임 하나 — 리더 필드와 이미지 바이트.</summary>
    public sealed class SynthFrame
    {
        public ulong BlockId;
        public ulong Timestamp;
        public uint PixelFormat;
        public int Width;
        public int Height;
        /// <summary>리더가 알리는 줄 수. 기본은 <see cref="Height"/> 와 같고, 가변 높이 촬영을 흉내 낼 때만 크게 둔다(트레일러가 실제 줄 수를 알린다).</summary>
        public int LeaderHeight;
        public int OffsetX;
        public int OffsetY;
        public int PaddingX;
        public int PaddingY;
        public int Stride;
        public byte[] Data = Array.Empty<byte>();
        public bool ExtendedIds;
        public int DataBytesPerPacket;
        public ushort PayloadType = GvspConst.PayloadImage;

        /// <summary>페이로드 패킷 수 N (리더 0, 페이로드 1..N, 트레일러 N+1).</summary>
        public int PacketCount => (Data.Length + DataBytesPerPacket - 1) / DataBytesPerPacket;
        public uint TrailerId => (uint)PacketCount + 1;
    }

    private readonly object _lock = new();
    private readonly Socket _socket;
    private readonly Dictionary<ulong, SynthFrame> _frames = new();
    private readonly byte[] _packet = new byte[GevStream.MaxPacketSize];

    public GvspTestSender()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    /// <summary>스트림 소켓 주소 — <see cref="GevStream.StartAsync"/> 뒤에 채운다.</summary>
    public IPEndPoint? Target { get; set; }

    public bool ExtendedIds { get; set; }

    /// <summary>SCPS 에 해당하는 값 — 페이로드 패킷의 데이터 길이는 여기서 IP/UDP/GVSP 헤더를 뺀 값이다.</summary>
    public int PacketSize { get; set; } = 1500;

    /// <summary>처음 보낼 때 빠뜨릴 (블록, 패킷 id) — 리센드에는 적용되지 않는다.</summary>
    public HashSet<(ulong BlockId, uint PacketId)> Drop { get; } = new();

    /// <summary>리센드 요청에도 다시 빠뜨릴 (블록, 패킷 id).</summary>
    public HashSet<(ulong BlockId, uint PacketId)> DropOnResend { get; } = new();

    public int DataBytesPerPacket => GvspConst.DataBytesPerPacket(PacketSize, ExtendedIds);

    public int PacketsSent { get; private set; }

    /// <summary>패턴이 채워진 합성 프레임을 만든다(보내지는 않는다). seed 가 다르면 바이트가 다르다.</summary>
    public SynthFrame BuildFrame(ulong blockId, int width, int height, uint pixelFormat, int paddingX = 0, int paddingY = 0, byte seed = 0, ulong timestamp = 0, int offsetX = 0, int offsetY = 0)
    {
        // 줄 패딩이 있으면 줄이 저마다 따로 시작하므로 줄마다 마지막 묶음을 채운다. 없으면 데이터가 줄에서 끊기지 않고
        // 이어 붙으므로 전체 픽셀 수로 한 번만 올린다 — 홀수 폭 packed 이 두 계산이 갈리는 자리다.
        var stride = LineBytes(pixelFormat, width) + paddingX;
        var bytes = (paddingX == 0 ? LineBytes(pixelFormat, width * height) : stride * height) + paddingY;
        var data = new byte[bytes];
        for (var i = 0; i < bytes; i++)
        {
            data[i] = (byte)(seed + i * 7 + (i >> 9));
        }

        var frame = new SynthFrame
        {
            BlockId = blockId,
            Timestamp = timestamp == 0 ? 1000UL * blockId : timestamp,
            PixelFormat = pixelFormat,
            Width = width,
            Height = height,
            LeaderHeight = height,
            OffsetX = offsetX,
            OffsetY = offsetY,
            PaddingX = paddingX,
            PaddingY = paddingY,
            Stride = stride,
            Data = data,
            ExtendedIds = ExtendedIds,
            DataBytesPerPacket = DataBytesPerPacket,
        };
        lock (_lock) _frames[blockId] = frame;
        return frame;
    }

    /// <summary>
    /// 줄 바이트 — 프로토콜 노트의 규칙을 라이브러리와 독립으로 구현: GVSP Packed(Mono/Bayer 10Packed·12Packed, 2픽셀 = 3바이트)는 홀수 폭도
    /// 마지막 묶음을 다 채우고, 그 밖은 ceil(width × bpp / 8). 4:1:1 은 이 송신기가 다루지 않는다.
    /// </summary>
    private static int LineBytes(uint pixelFormat, int width)
    {
        var isGvspPacked = pixelFormat == 0x010C0004u || pixelFormat == 0x010C0006u || (pixelFormat >= 0x010C0026u && pixelFormat <= 0x010C002Du);
        if (isGvspPacked) return (width + 1) / 2 * 3;
        var bpp = (int)((pixelFormat >> 16) & 0xFF);
        return (width * bpp + 7) / 8;
    }

    /// <summary>
    /// 이미지 뒤에 청크 바이트가 붙은 프레임. 리더는 이 프레임의 크기를 알려 주지 못한다 —
    /// payload_type 의 bit14(또는 확장 청크 타입)가 "이미지 뒤에 더 있다" 고 알릴 뿐이고, 실제 끝은 트레일러까지 받아 봐야 안다.
    /// 청크 바이트는 이미지 패턴과 겹치지 않는 값으로 채워 어디까지가 이미지인지 눈으로도 갈린다.
    /// </summary>
    public SynthFrame BuildChunkFrame(ulong blockId, int width, int height, uint pixelFormat, int chunkBytes, bool extendedType = false, byte seed = 0)
    {
        if (chunkBytes < 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
        var frame = BuildFrame(blockId, width, height, pixelFormat, seed: seed);
        var image = frame.Data;
        var data = new byte[image.Length + chunkBytes];
        Array.Copy(image, data, image.Length);
        for (var i = 0; i < chunkBytes; i++) data[image.Length + i] = (byte)(0xC0 + (i % 0x30));
        frame.Data = data;
        frame.PayloadType = extendedType
            ? GvspConst.PayloadExtendedChunkData
            : (ushort)(GvspConst.PayloadImage | GvspConst.PayloadChunkFlag);
        return frame;
    }

    /// <summary>프레임 하나를 리더·페이로드·트레일러로 보낸다. <see cref="Drop"/> 에 든 패킷은 건너뛴다.</summary>
    public SynthFrame SendFrame(ulong blockId, int width, int height, uint pixelFormat, int paddingX = 0, int paddingY = 0, byte seed = 0, int offsetX = 0, int offsetY = 0)
    {
        var frame = BuildFrame(blockId, width, height, pixelFormat, paddingX, paddingY, seed, offsetX: offsetX, offsetY: offsetY);
        SendFrame(frame);
        return frame;
    }

    /// <summary>이미 만든 프레임을 보낸다. 패킷 사이에 interPacketDelayMs 만큼 쉴 수 있다(느린 장치 흉내).</summary>
    public void SendFrame(SynthFrame frame, int interPacketDelayMs = 0)
    {
        SendPacket(frame, 0, GvspConst.StatusSuccess);
        for (uint id = 1; id <= frame.TrailerId; id++)
        {
            if (interPacketDelayMs > 0) Thread.Sleep(interPacketDelayMs);
            SendPacket(frame, id, GvspConst.StatusSuccess);
        }
    }

    /// <summary>블록의 패킷 id 하나를 보낸다(0 리더, 1..N 페이로드, N+1 트레일러). <see cref="Drop"/> 에 들어 있으면 보내지 않는다.</summary>
    public void SendPacket(SynthFrame frame, uint packetId, ushort status)
    {
        if (status != GvspConst.StatusPacketResend && Drop.Contains((frame.BlockId, packetId))) return;
        if (status == GvspConst.StatusPacketResend && DropOnResend.Contains((frame.BlockId, packetId))) return;

        lock (_lock)
        {
            int length;
            if (packetId == 0) length = WriteLeader(frame, status);
            else if (packetId == frame.TrailerId) length = WriteTrailer(frame, status);
            else length = WritePayload(frame, packetId, status);
            Send(length);
        }
    }

    /// <summary>패킷 하나를 보내지 않고 바이트로만 만든다 — 수신기에 직접 먹여 볼 때 쓴다.</summary>
    public byte[] BuildPacketBytes(SynthFrame frame, uint packetId, ushort status = GvspConst.StatusSuccess)
    {
        lock (_lock)
        {
            int length;
            if (packetId == 0) length = WriteLeader(frame, status);
            else if (packetId == frame.TrailerId) length = WriteTrailer(frame, status);
            else length = WritePayload(frame, packetId, status);
            return _packet.AsSpan(0, length).ToArray();
        }
    }

    /// <summary>리센드 요청에 답한다 — [first, last] 를 상태 0x0100 으로 다시 보낸다. 범위 밖 id 는 무시한다.</summary>
    public void Resend(ulong blockId, uint first, uint last)
    {
        SynthFrame? frame;
        lock (_lock) _frames.TryGetValue(blockId, out frame);
        if (frame is null) return;
        for (var id = first; id <= last; id++)
        {
            if (id > frame.TrailerId) break;
            SendPacket(frame, id, GvspConst.StatusPacketResend);
        }
    }

    /// <summary>오류 상태 패킷(헤더만)을 보낸다 — 예: 0x800C 패킷 없음.</summary>
    public void SendError(ulong blockId, uint packetId, ushort status)
    {
        lock (_lock)
        {
            var length = WriteHeader(status, blockId, GvspConst.ContentPayload, packetId);
            Send(length);
        }
    }

    /// <summary>임의 콘텐츠 타입의 헤더만 있는 패킷을 보낸다 — 지원하지 않거나 예약된 콘텐츠 타입 흉내.</summary>
    public void SendHeaderOnly(ulong blockId, byte contentType, uint packetId)
    {
        lock (_lock)
        {
            var length = WriteHeader(GvspConst.StatusSuccess, blockId, contentType, packetId);
            Send(length);
        }
    }

    /// <summary>
    /// 리더를 짧은 데이터로 보낸다 — 종류마다 리더 모양이 다르다는 사실을 흉내 낸다.
    /// 청크 모드를 켠 실장치는 payload_type 4(chunk data)의 12바이트 리더를 보낸다: flags·payload_type·timestamp 뿐이고 기하가 없다.
    /// </summary>
    public void SendShortLeader(ulong blockId, ushort payloadType, int dataBytes)
    {
        lock (_lock)
        {
            var offset = WriteHeader(GvspConst.StatusSuccess, blockId, GvspConst.ContentLeader, 0);
            var d = _packet.AsSpan(offset, dataBytes);
            d.Clear();
            if (dataBytes >= 2) BinaryPrimitives.WriteUInt16BigEndian(d, 0);                    // flags
            if (dataBytes >= 4) BinaryPrimitives.WriteUInt16BigEndian(d.Slice(2), payloadType);  // payload_type
            Send(offset + dataBytes);
        }
    }

    /// <summary>트레일러를 임의 패킷 id 로 보낸다 — id 0 같은 깨진 트레일러 흉내.</summary>
    public void SendTrailer(SynthFrame frame, uint packetId)
    {
        lock (_lock)
        {
            var offset = WriteHeader(GvspConst.StatusSuccess, frame.BlockId, GvspConst.ContentTrailer, packetId);
            offset += WriteTrailerData(_packet.AsSpan(offset), frame);
            Send(offset);
        }
    }

    /// <summary>올인 패킷: 리더 36 바이트 + 이미지 + 트레일러 8 바이트를 한 데이터그램에 싣는다.</summary>
    public void SendAllIn(SynthFrame frame)
    {
        lock (_lock)
        {
            var offset = WriteHeader(GvspConst.StatusSuccess, frame.BlockId, GvspConst.ContentAllIn, 0);
            offset += WriteLeaderData(_packet.AsSpan(offset), frame);
            Buffer.BlockCopy(frame.Data, 0, _packet, offset, frame.Data.Length);
            offset += frame.Data.Length;
            offset += WriteTrailerData(_packet.AsSpan(offset), frame);
            Send(offset);
        }
    }

    /// <summary>
    /// 프레임이 실제로 가진 패킷 수와 무관하게 페이로드 패킷 하나를 보낸다 — 손상되었거나 남이 보낸 패킷을 흉내 낸다.
    /// <see cref="SendPacket"/> 은 id 가 프레임 범위 안인지 검사하므로 그쪽으로는 만들 수 없는 상황이다.
    /// </summary>
    public void SendPayloadWithArbitraryId(ulong blockId, uint packetId, int dataLength)
    {
        lock (_lock)
        {
            var offset = WriteHeader(GvspConst.StatusSuccess, blockId, GvspConst.ContentPayload, packetId);
            for (var i = 0; i < dataLength; i++) _packet[offset + i] = (byte)i;
            Send(offset + dataLength);
        }
    }

    /// <summary>임의 바이트를 그대로 보낸다(깨진 패킷·헤더 미달 등).</summary>
    public void SendRaw(byte[] bytes, int length)
    {
        lock (_lock)
        {
            Buffer.BlockCopy(bytes, 0, _packet, 0, length);
            Send(length);
        }
    }

    public void Dispose() => _socket.Close();

    private void Send(int length)
    {
        var target = Target ?? throw new InvalidOperationException("Sender target is not set.");
        _socket.SendTo(_packet, 0, length, SocketFlags.None, target);
        PacketsSent++;
    }

    private int WriteLeader(SynthFrame frame, ushort status)
    {
        var offset = WriteHeader(status, frame.BlockId, GvspConst.ContentLeader, 0);
        return offset + WriteLeaderData(_packet.AsSpan(offset), frame);
    }

    private int WriteTrailer(SynthFrame frame, ushort status)
    {
        var offset = WriteHeader(status, frame.BlockId, GvspConst.ContentTrailer, frame.TrailerId);
        return offset + WriteTrailerData(_packet.AsSpan(offset), frame);
    }

    private int WritePayload(SynthFrame frame, uint packetId, ushort status)
    {
        var offset = WriteHeader(status, frame.BlockId, GvspConst.ContentPayload, packetId);
        var dataOffset = (int)(packetId - 1) * frame.DataBytesPerPacket;
        var length = Math.Min(frame.DataBytesPerPacket, frame.Data.Length - dataOffset);
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(packetId), $"Packet {packetId} is beyond the frame ({frame.PacketCount} payload packets).");
        Buffer.BlockCopy(frame.Data, dataOffset, _packet, offset, length);
        return offset + length;
    }

    /// <summary>표준 8 바이트 또는 확장 20 바이트 헤더를 쓰고 헤더 길이를 돌려준다.</summary>
    private int WriteHeader(ushort status, ulong blockId, byte contentType, uint packetId)
    {
        var p = _packet.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(p, status);
        if (!ExtendedIds)
        {
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2), (ushort)blockId);
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), ((uint)contentType << 24) | (packetId & 0x00FF_FFFF));
            return GvspConst.HeaderSize;
        }

        BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), 0x8000_0000u | ((uint)contentType << 24));
        BinaryPrimitives.WriteUInt64BigEndian(p.Slice(8), blockId);
        BinaryPrimitives.WriteUInt32BigEndian(p.Slice(16), packetId);
        return GvspConst.ExtendedHeaderSize;
    }

    private static int WriteLeaderData(Span<byte> d, SynthFrame frame)
    {
        BinaryPrimitives.WriteUInt16BigEndian(d, 0);
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(2), frame.PayloadType);
        BinaryPrimitives.WriteUInt64BigEndian(d.Slice(4), frame.Timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(12), frame.PixelFormat);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(16), (uint)frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(20), (uint)(frame.LeaderHeight > 0 ? frame.LeaderHeight : frame.Height));
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(24), (uint)frame.OffsetX);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(28), (uint)frame.OffsetY);
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(32), (ushort)frame.PaddingX);
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(34), (ushort)frame.PaddingY);
        return GvspConst.ImageLeaderDataSize;
    }

    private static int WriteTrailerData(Span<byte> d, SynthFrame frame)
    {
        BinaryPrimitives.WriteUInt16BigEndian(d, 0);
        BinaryPrimitives.WriteUInt16BigEndian(d.Slice(2), frame.PayloadType);
        BinaryPrimitives.WriteUInt32BigEndian(d.Slice(4), (uint)frame.Height);
        return GvspConst.TrailerDataSize;
    }
}
