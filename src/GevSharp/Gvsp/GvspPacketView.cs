using System.Buffers.Binary;
using GevSharp.Pfnc;

namespace GevSharp.Gvsp;

/// <summary>
/// GVSP 패킷 한 개를 무할당으로 읽는 뷰. 수신 스크래치 버퍼 위에 얹어 헤더 필드와 데이터 영역을 돌려준다.
/// 표준 헤더(8바이트)와 확장 헤더(20바이트)는 packet_infos 의 EI 비트로 패킷마다 구분한다.
/// content type 은 [30:24] 를 마스크한 값이라 예약 비트가 섞여 있어도 분류가 어긋나지 않는다.
/// </summary>
public readonly ref struct GvspPacketView
{
    private readonly ReadOnlySpan<byte> _packet;

    private GvspPacketView(ReadOnlySpan<byte> packet, ushort status, bool isExtendedId, byte contentType, ulong blockId, uint packetId, int headerSize)
    {
        _packet = packet;
        Status = status;
        IsExtendedId = isExtendedId;
        ContentType = contentType;
        BlockId = blockId;
        PacketId = packetId;
        HeaderSize = headerSize;
    }

    /// <summary>상태 코드 — 0 정상, 0x0100 리센드된 패킷, 0x8xxx 오류.</summary>
    public ushort Status { get; }

    /// <summary>확장 ID 헤더(64비트 블록·32비트 패킷 ID)인지.</summary>
    public bool IsExtendedId { get; }

    /// <summary>마스크된 content type — <see cref="GvspConst.ContentLeader"/> 등.</summary>
    public byte ContentType { get; }

    public ulong BlockId { get; }

    /// <summary>리더 0, 페이로드 1..N, 트레일러 N+1.</summary>
    public uint PacketId { get; }

    public int HeaderSize { get; }

    public int PacketLength => _packet.Length;

    /// <summary>패킷 안에서 데이터가 시작하는 오프셋(= 헤더 길이).</summary>
    public int DataOffset => HeaderSize;

    public int DataLength => _packet.Length - HeaderSize;

    /// <summary>헤더 뒤의 데이터 영역 — 리더 필드, 이미지 바이트, 트레일러 필드 중 하나.</summary>
    public ReadOnlySpan<byte> Data => _packet.Slice(HeaderSize);

    public bool IsError => GvspConst.IsError(Status);

    public bool IsResent => Status == GvspConst.StatusPacketResend;

    /// <summary>buffer 의 앞 length 바이트를 패킷으로 읽는다. 헤더보다 짧으면 false.</summary>
    public static bool TryParse(byte[] buffer, int length, out GvspPacketView view)
    {
        if (buffer is null || length < 0 || length > buffer.Length)
        {
            view = default;
            return false;
        }
        return TryParse(new ReadOnlySpan<byte>(buffer, 0, length), out view);
    }

    /// <summary>패킷을 읽는다. 헤더(표준 8 / 확장 20)보다 짧으면 false.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out GvspPacketView view)
    {
        if (packet.Length < GvspConst.HeaderSize)
        {
            view = default;
            return false;
        }

        var status = BinaryPrimitives.ReadUInt16BigEndian(packet);
        var infos = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4));
        var isExtended = (infos & GvspConst.ExtendedIdMask) != 0;
        var contentType = (byte)((infos & GvspConst.ContentTypeMask) >> GvspConst.ContentTypeShift);

        if (!isExtended)
        {
            var blockId16 = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2));
            view = new GvspPacketView(packet, status, false, contentType, blockId16, infos & GvspConst.PacketIdMask, GvspConst.HeaderSize);
            return true;
        }

        if (packet.Length < GvspConst.ExtendedHeaderSize)
        {
            view = default;
            return false;
        }

        var blockId64 = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(8));
        var packetId32 = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(16));
        view = new GvspPacketView(packet, status, true, contentType, blockId64, packetId32, GvspConst.ExtendedHeaderSize);
        return true;
    }

    /// <summary>패킷을 읽는다. 헤더보다 짧으면 <see cref="GevException"/>.</summary>
    public static GvspPacketView Parse(ReadOnlySpan<byte> packet)
    {
        if (!TryParse(packet, out var view))
        {
            throw new GevException($"GVSP packet too short: {packet.Length} bytes (header needs {GvspConst.HeaderSize} or {GvspConst.ExtendedHeaderSize}).");
        }
        return view;
    }

    /// <summary>리더 데이터 36바이트를 읽는다. 데이터가 짧으면 false.</summary>
    public bool TryReadImageLeader(out GvspImageLeader leader) => GvspImageLeader.TryRead(Data, out leader);

    /// <summary>
    /// 리더가 스스로 밝힌 페이로드 종류(청크 비트를 뗀 값). 종류는 어느 리더에나 같은 자리에 있고 리더의 나머지 모양은 그 종류가 정하므로,
    /// 이미지 리더로 읽기 전에 이것부터 본다 — 다루지 않는 종류를 "헤더가 깨졌다" 로 오인하지 않기 위해서다.
    /// 4바이트도 안 되면 종류조차 알 수 없어 false.
    /// </summary>
    public bool TryReadLeaderPayloadType(out ushort payloadType)
    {
        var d = Data;
        if (d.Length < 4)
        {
            payloadType = 0;
            return false;
        }
        payloadType = (ushort)(((d[2] << 8) | d[3]) & GvspConst.PayloadTypeMask);
        return true;
    }

    /// <summary>리더 데이터 36바이트를 읽는다. 데이터가 짧으면 <see cref="GevException"/>.</summary>
    public GvspImageLeader ReadImageLeader() => GvspImageLeader.Read(Data);

    /// <summary>트레일러 데이터 8바이트를 읽는다. 데이터가 짧으면 false.</summary>
    public bool TryReadTrailer(out GvspTrailer trailer) => GvspTrailer.TryRead(Data, out trailer);

    /// <summary>트레일러 데이터 8바이트를 읽는다. 데이터가 짧으면 <see cref="GevException"/>.</summary>
    public GvspTrailer ReadTrailer() => GvspTrailer.Read(Data);
}

/// <summary>
/// 이미지 리더(패킷 id 0)의 데이터 필드. 확장 청크 리더도 같은 앞부분을 가진다.
/// 프레임 바이트 수는 리더만으로 계산한다: bpp 는 PFNC 코드의 [23:16], 크기 규칙은 <see cref="PixelFormatInfo"/> 한 곳에 있다
/// (기본 ceil(픽셀 수 × bpp / 8); GVSP Packed 는 2픽셀 = 3바이트, 4:1:1 은 4픽셀 = 6바이트 묶음 단위로 올림).
/// padding_x 가 있으면 줄이 저마다 따로 시작하므로 줄마다 올린 뒤 패딩을 더하고, 없으면 데이터가 줄에서 끊기지 않으므로 전체 픽셀 수로 한 번만 올린다.
/// 수신기는 <see cref="GvspImageLeader.LineBytes"/> 를 <c>GevFrame.Stride</c> 로 내보내되, 줄이 바이트 경계에서 끝나지 않고 padding_x 도 없으면
/// 줄 간격이 없다는 뜻으로 0 을 내보낸다.
/// </summary>
public readonly struct GvspImageLeader
{
    public GvspImageLeader(ushort flags, ushort payloadType, ulong timestamp, uint pixelFormat, uint sizeX, uint sizeY, uint offsetX, uint offsetY, ushort paddingX, ushort paddingY)
    {
        Flags = flags;
        PayloadType = payloadType;
        Timestamp = timestamp;
        PixelFormat = pixelFormat;
        SizeX = sizeX;
        SizeY = sizeY;
        OffsetX = offsetX;
        OffsetY = offsetY;
        PaddingX = paddingX;
        PaddingY = paddingY;
    }

    public ushort Flags { get; }

    /// <summary>원시 payload_type — [13:0] 종류, bit14 청크 부가 여부.</summary>
    public ushort PayloadType { get; }

    public ulong Timestamp { get; }

    /// <summary>PFNC 픽셀 포맷 코드.</summary>
    public uint PixelFormat { get; }

    public uint SizeX { get; }
    public uint SizeY { get; }
    public uint OffsetX { get; }
    public uint OffsetY { get; }
    public ushort PaddingX { get; }
    public ushort PaddingY { get; }

    /// <summary>청크 비트를 뗀 페이로드 종류(1 = image, 5 = extended chunk, …).</summary>
    public ushort PayloadTypeBase => (ushort)(PayloadType & GvspConst.PayloadTypeMask);

    /// <summary>이미지 뒤에 청크 데이터가 붙는지 — bit14 또는 extended chunk 타입.</summary>
    public bool HasChunkData => (PayloadType & GvspConst.PayloadChunkFlag) != 0 || PayloadTypeBase == GvspConst.PayloadExtendedChunkData;

    public int BitsPerPixel => (int)((PixelFormat >> 16) & 0xFF);

    /// <summary>
    /// 한 줄의 바이트 수(패딩 포함) — <see cref="PixelFormatInfo.LineBytes(uint, int)"/> 와 같은 규칙, 모르는 코드는 ceil(width × bpp / 8). 예외를 내지 않는다.
    /// <see cref="IsLineByteAligned"/> 가 거짓이고 padding_x 도 0 이면 줄이 바이트 경계에서 끝나지 않아 이 값은 실제 줄 간격이 아니다 —
    /// 그때 이미지는 줄 단위가 아니라 이어진 한 덩어리다.
    /// </summary>
    public long LineBytes => PixelFormatInfo.LineBytesLong(PixelFormat, SizeX) + PaddingX;

    /// <summary>줄이 바이트 경계에서 끝나는지 — 거짓이면 padding_x 가 없는 한 <see cref="LineBytes"/> 로 줄을 건너뛸 수 없다.</summary>
    public bool IsLineByteAligned => SizeX <= int.MaxValue && PixelFormatInfo.IsLineByteAligned(PixelFormat, (int)SizeX);

    /// <summary>
    /// 이미지 영역 바이트 수(청크 제외). padding_x 가 있으면 줄마다 마지막 묶음을 채워 줄 길이 × 높이,
    /// 없으면 데이터가 줄에서 끊기지 않으므로 전체 픽셀 수로 한 번만 올린다. 마지막에 padding_y 를 더한다.
    /// </summary>
    public long ImageBytes => PixelFormatInfo.ImageBytesLong(PixelFormat, SizeX, SizeY, PaddingX, PaddingY);

    public static bool TryRead(ReadOnlySpan<byte> data, out GvspImageLeader leader)
    {
        if (data.Length < GvspConst.ImageLeaderDataSize)
        {
            leader = default;
            return false;
        }

        leader = new GvspImageLeader(
            flags: BinaryPrimitives.ReadUInt16BigEndian(data),
            payloadType: BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2)),
            timestamp: BinaryPrimitives.ReadUInt64BigEndian(data.Slice(4)),
            pixelFormat: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12)),
            sizeX: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16)),
            sizeY: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20)),
            offsetX: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(24)),
            offsetY: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(28)),
            paddingX: BinaryPrimitives.ReadUInt16BigEndian(data.Slice(32)),
            paddingY: BinaryPrimitives.ReadUInt16BigEndian(data.Slice(34)));
        return true;
    }

    public static GvspImageLeader Read(ReadOnlySpan<byte> data)
    {
        if (!TryRead(data, out var leader))
        {
            throw new GevException($"GVSP image leader too short: {data.Length} bytes (needs {GvspConst.ImageLeaderDataSize}).");
        }
        return leader;
    }
}

/// <summary>트레일러(패킷 id N+1)의 데이터 필드. size_y 는 가변 높이 촬영에서 실제 줄 수를 알려 준다.</summary>
public readonly struct GvspTrailer
{
    public GvspTrailer(ushort payloadType, uint sizeY)
    {
        PayloadType = payloadType;
        SizeY = sizeY;
    }

    public ushort PayloadType { get; }
    public uint SizeY { get; }

    public static bool TryRead(ReadOnlySpan<byte> data, out GvspTrailer trailer)
    {
        if (data.Length < GvspConst.TrailerDataSize)
        {
            trailer = default;
            return false;
        }

        trailer = new GvspTrailer(
            payloadType: BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2)),
            sizeY: BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4)));
        return true;
    }

    public static GvspTrailer Read(ReadOnlySpan<byte> data)
    {
        if (!TryRead(data, out var trailer))
        {
            throw new GevException($"GVSP trailer too short: {data.Length} bytes (needs {GvspConst.TrailerDataSize}).");
        }
        return trailer;
    }
}
