using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;

namespace GevSharp.Gvcp;

/// <summary>
/// CMD 헤더(8바이트): [type u8 = 0x42][flags u8][command u16][length u16][req_id u16]. 전부 빅엔디언.
/// </summary>
public readonly struct GvcpCmdHeader
{
    public byte PacketType { get; }
    public byte Flags { get; }
    public ushort Command { get; }
    /// <summary>헤더 뒤 페이로드 바이트 수.</summary>
    public ushort Length { get; }
    public ushort ReqId { get; }

    public GvcpCmdHeader(ushort command, ushort length, ushort reqId, byte flags = GvcpConst.FlagAckRequired, byte packetType = GvcpConst.PacketTypeCmd)
    {
        PacketType = packetType;
        Flags = flags;
        Command = command;
        Length = length;
        ReqId = reqId;
    }

    public bool IsAckRequired => (Flags & GvcpConst.FlagAckRequired) != 0;

    /// <summary>헤더를 해석하고 검증한다. 8바이트 미만·type ≠ 0x42·페이로드가 length 보다 짧으면 <see cref="GevException"/>.</summary>
    public static GvcpCmdHeader Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < GvcpConst.HeaderSize)
            throw new GevException($"GVCP command packet too short: {packet.Length} bytes (header needs {GvcpConst.HeaderSize})");
        var header = ReadUnchecked(packet);
        if (header.PacketType != GvcpConst.PacketTypeCmd)
            throw new GevException($"GVCP command packet has type 0x{header.PacketType:X2}, expected 0x{GvcpConst.PacketTypeCmd:X2}");
        if (packet.Length < GvcpConst.HeaderSize + header.Length)
            throw new GevException($"GVCP command payload truncated: header declares {header.Length} bytes, packet carries {packet.Length - GvcpConst.HeaderSize}");
        return header;
    }

    /// <summary>예외 없이 검증한다 — 수신 루프용.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out GvcpCmdHeader header)
    {
        if (packet.Length < GvcpConst.HeaderSize)
        {
            header = default;
            return false;
        }
        header = ReadUnchecked(packet);
        return header.PacketType == GvcpConst.PacketTypeCmd && packet.Length >= GvcpConst.HeaderSize + header.Length;
    }

    private static GvcpCmdHeader ReadUnchecked(ReadOnlySpan<byte> p) => new(
        BinaryPrimitives.ReadUInt16BigEndian(p.Slice(2)),
        BinaryPrimitives.ReadUInt16BigEndian(p.Slice(4)),
        BinaryPrimitives.ReadUInt16BigEndian(p.Slice(6)),
        p[1],
        p[0]);

    public void Write(Span<byte> dst)
    {
        if (dst.Length < GvcpConst.HeaderSize)
            throw new ArgumentException("destination too small for a GVCP header", nameof(dst));
        dst[0] = PacketType;
        dst[1] = Flags;
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2), Command);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4), Length);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(6), ReqId);
    }
}

/// <summary>
/// ACK 헤더(8바이트): [status u16][ack command u16][length u16][req_id u16].
/// 오류 응답은 첫 바이트가 0x80 으로 올 수 있다 — 앞 두 바이트를 통째로 status 로 읽고 bit15 로 오류를 판정한다.
/// </summary>
public readonly struct GvcpAckHeader
{
    public ushort Status { get; }
    public ushort Command { get; }
    public ushort Length { get; }
    public ushort ReqId { get; }

    public GvcpAckHeader(ushort status, ushort command, ushort length, ushort reqId)
    {
        Status = status;
        Command = command;
        Length = length;
        ReqId = reqId;
    }

    public bool IsError => GvcpConst.IsError(Status);

    /// <summary>헤더를 해석하고 검증한다. 8바이트 미만이거나 페이로드가 length 보다 짧으면 <see cref="GevException"/>.</summary>
    public static GvcpAckHeader Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < GvcpConst.HeaderSize)
            throw new GevException($"GVCP ack packet too short: {packet.Length} bytes (header needs {GvcpConst.HeaderSize})");
        var header = ReadUnchecked(packet);
        if (packet.Length < GvcpConst.HeaderSize + header.Length)
            throw new GevException($"GVCP ack payload truncated: header declares {header.Length} bytes, packet carries {packet.Length - GvcpConst.HeaderSize}");
        return header;
    }

    /// <summary>예외 없이 검증한다 — 수신 루프용.</summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out GvcpAckHeader header)
    {
        if (packet.Length < GvcpConst.HeaderSize)
        {
            header = default;
            return false;
        }
        header = ReadUnchecked(packet);
        return packet.Length >= GvcpConst.HeaderSize + header.Length;
    }

    private static GvcpAckHeader ReadUnchecked(ReadOnlySpan<byte> p) => new(
        BinaryPrimitives.ReadUInt16BigEndian(p),
        BinaryPrimitives.ReadUInt16BigEndian(p.Slice(2)),
        BinaryPrimitives.ReadUInt16BigEndian(p.Slice(4)),
        BinaryPrimitives.ReadUInt16BigEndian(p.Slice(6)));

    public void Write(Span<byte> dst)
    {
        if (dst.Length < GvcpConst.HeaderSize)
            throw new ArgumentException("destination too small for a GVCP header", nameof(dst));
        BinaryPrimitives.WriteUInt16BigEndian(dst, Status);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(2), Command);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(4), Length);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(6), ReqId);
    }
}

/// <summary>
/// GVCP 패킷 조립·해석의 무할당 정적 함수. CMD 페이로드 읽기(시뮬레이터·응답기용), ACK 쓰기, PACKETRESEND 쓰기.
/// 호스트가 보내는 CMD 는 <see cref="GvcpCmd"/>, 받은 ACK 는 <see cref="GvcpAck"/> 로 다룬다.
/// 길이가 맞지 않는 입력은 전부 <see cref="GevException"/> — 잘린 패킷을 0 으로 읽어 넘기지 않는다.
/// </summary>
public static class GvcpPacket
{
    /// <summary>READREG 135 × 4 = 540 바이트가 가장 큰 CMD 페이로드다.</summary>
    public const int MaxPayload = GvcpConst.MaxRegsPerPacket * 4;
    public const int MaxCmdSize = GvcpConst.HeaderSize + MaxPayload;
    public const int MaxAckSize = GvcpConst.HeaderSize + MaxPayload;
    /// <summary>WRITEREG 한 패킷의 최대 (주소, 값) 쌍 수 — 540 / 8.</summary>
    public const int MaxWriteRegsPerPacket = MaxPayload / 8;
    public const int ForceIpPayloadSize = 56;
    public const int PacketResendStdPayloadSize = 12;
    public const int PacketResendExtPayloadSize = 20;
    /// <summary>PACKETRESEND 패킷 최대 크기 — 스택 버퍼 크기로 쓴다.</summary>
    public const int PacketResendMaxSize = GvcpConst.HeaderSize + PacketResendExtPayloadSize;
    public const int PendingAckPayloadSize = 4;
    public const int WriteAckPayloadSize = 4;
    public const int ReadMemCmdPayloadSize = 8;

    public static string CommandName(ushort command) => command switch
    {
        GvcpConst.DiscoveryCmd or GvcpConst.DiscoveryAck => "DISCOVERY",
        GvcpConst.ForceIpCmd or GvcpConst.ForceIpAck => "FORCEIP",
        GvcpConst.PacketResendCmd or GvcpConst.PacketResendAck => "PACKETRESEND",
        GvcpConst.ReadRegCmd or GvcpConst.ReadRegAck => "READREG",
        GvcpConst.WriteRegCmd or GvcpConst.WriteRegAck => "WRITEREG",
        GvcpConst.ReadMemCmd or GvcpConst.ReadMemAck => "READMEM",
        GvcpConst.WriteMemCmd or GvcpConst.WriteMemAck => "WRITEMEM",
        GvcpConst.PendingAck => "PENDING_ACK",
        GvcpConst.EventCmd or GvcpConst.EventAck => "EVENT",
        GvcpConst.EventDataCmd or GvcpConst.EventDataAck => "EVENTDATA",
        GvcpConst.ActionCmd or GvcpConst.ActionAck => "ACTION",
        _ => $"CMD_0x{command:X4}",
    };

    // ------------------------------------------------------------------ PACKETRESEND

    /// <summary>
    /// PACKETRESEND_CMD 전체 패킷(헤더 포함)을 dst 에 쓴다. ack-required 는 0 — 장치는 응답하지 않는다.
    /// 표준: [channel u16][block u16][first u32 (24비트)][last u32 (24비트)] 12바이트.
    /// 확장(flags 0x10): [channel u16][reserved u16][first u32][last u32][block u64] 20바이트.
    /// </summary>
    /// <returns>쓴 바이트 수(20 또는 28).</returns>
    public static int WritePacketResend(Span<byte> dst, ushort reqId, ulong blockId, uint firstPacketId, uint lastPacketId, bool extendedIds, int streamChannel = 0)
    {
        if (streamChannel < 0 || streamChannel > ushort.MaxValue)
            throw new GevException($"stream channel {streamChannel} is out of range 0..65535");
        if (firstPacketId > lastPacketId)
            throw new GevException($"PACKETRESEND first packet id {firstPacketId} is greater than last {lastPacketId}");
        if (!extendedIds)
        {
            if (blockId > ushort.MaxValue)
                throw new GevException($"block id {blockId} does not fit the 16-bit standard PACKETRESEND; use extended ids");
            if (lastPacketId > Gvsp.GvspConst.PacketIdMask)
                throw new GevException($"packet id {lastPacketId} does not fit the 24-bit standard PACKETRESEND; use extended ids");
        }

        var payloadLen = extendedIds ? PacketResendExtPayloadSize : PacketResendStdPayloadSize;
        var total = GvcpConst.HeaderSize + payloadLen;
        if (dst.Length < total)
            throw new ArgumentException($"destination too small for PACKETRESEND ({total} bytes)", nameof(dst));

        var flags = extendedIds ? GvcpConst.FlagExtendedIds : (byte)0;
        new GvcpCmdHeader(GvcpConst.PacketResendCmd, (ushort)payloadLen, reqId, flags).Write(dst);
        var p = dst.Slice(GvcpConst.HeaderSize);
        BinaryPrimitives.WriteUInt16BigEndian(p, (ushort)streamChannel);
        if (extendedIds)
        {
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2), 0);
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), firstPacketId);
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(8), lastPacketId);
            BinaryPrimitives.WriteUInt64BigEndian(p.Slice(12), blockId);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(p.Slice(2), (ushort)blockId);
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(4), firstPacketId & Gvsp.GvspConst.PacketIdMask);
            BinaryPrimitives.WriteUInt32BigEndian(p.Slice(8), lastPacketId & Gvsp.GvspConst.PacketIdMask);
        }
        return total;
    }

    /// <summary>PACKETRESEND_CMD 페이로드를 읽는다. extendedIds 는 헤더 flags 의 0x10 비트로 판정해 넘긴다.</summary>
    public static void ReadPacketResend(ReadOnlySpan<byte> payload, bool extendedIds, out int streamChannel, out ulong blockId, out uint firstPacketId, out uint lastPacketId)
    {
        var need = extendedIds ? PacketResendExtPayloadSize : PacketResendStdPayloadSize;
        if (payload.Length < need)
            throw new GevException($"PACKETRESEND payload too short: {payload.Length} bytes (expected {need})");
        streamChannel = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (extendedIds)
        {
            firstPacketId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4));
            lastPacketId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8));
            blockId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(12));
        }
        else
        {
            blockId = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(2));
            firstPacketId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4)) & Gvsp.GvspConst.PacketIdMask;
            lastPacketId = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8)) & Gvsp.GvspConst.PacketIdMask;
        }
    }

    // ------------------------------------------------------------------ CMD payload readers (장치 쪽)

    /// <summary>READREG_CMD 페이로드의 주소 개수. 4의 배수가 아니거나 0 이거나 135 를 넘으면 <see cref="GevException"/>.</summary>
    public static int ReadRegCount(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || (payload.Length & 3) != 0)
            throw new GevException($"READREG payload length {payload.Length} is not a positive multiple of 4");
        var n = payload.Length / 4;
        if (n > GvcpConst.MaxRegsPerPacket)
            throw new GevException($"READREG carries {n} addresses, more than the {GvcpConst.MaxRegsPerPacket} allowed");
        return n;
    }

    public static uint ReadRegAddress(ReadOnlySpan<byte> payload, int index)
    {
        var n = ReadRegCount(payload);
        if ((uint)index >= (uint)n) throw new GevException($"READREG address index {index} out of range 0..{n - 1}");
        return BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(index * 4));
    }

    /// <summary>WRITEREG_CMD 페이로드의 (주소, 값) 쌍 개수.</summary>
    public static int WriteRegCount(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0 || (payload.Length & 7) != 0)
            throw new GevException($"WRITEREG payload length {payload.Length} is not a positive multiple of 8");
        var n = payload.Length / 8;
        if (n > MaxWriteRegsPerPacket)
            throw new GevException($"WRITEREG carries {n} entries, more than the {MaxWriteRegsPerPacket} allowed");
        return n;
    }

    public static void WriteRegEntry(ReadOnlySpan<byte> payload, int index, out uint address, out uint value)
    {
        var n = WriteRegCount(payload);
        if ((uint)index >= (uint)n) throw new GevException($"WRITEREG entry index {index} out of range 0..{n - 1}");
        address = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(index * 8));
        value = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(index * 8 + 4));
    }

    /// <summary>READMEM_CMD 페이로드: [address u32][reserved u16][count u16].</summary>
    public static void ReadMemFields(ReadOnlySpan<byte> payload, out uint address, out int count)
    {
        if (payload.Length < ReadMemCmdPayloadSize)
            throw new GevException($"READMEM payload too short: {payload.Length} bytes (expected {ReadMemCmdPayloadSize})");
        address = BinaryPrimitives.ReadUInt32BigEndian(payload);
        count = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6));
    }

    /// <summary>WRITEMEM_CMD 페이로드: [address u32][data …].</summary>
    public static uint WriteMemAddress(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new GevException($"WRITEMEM payload too short: {payload.Length} bytes");
        return BinaryPrimitives.ReadUInt32BigEndian(payload);
    }

    public static ReadOnlySpan<byte> WriteMemData(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            throw new GevException($"WRITEMEM payload too short: {payload.Length} bytes");
        return payload.Slice(4);
    }

    /// <summary>FORCEIP_CMD 페이로드(56바이트)를 쓴다: [res u16][mac-high u16][mac-low u32][res 12][ip u32][res 12][subnet u32][res 12][gateway u32].</summary>
    public static void WriteForceIp(Span<byte> payload, PhysicalAddress mac, IPAddress ip, IPAddress subnet, IPAddress gateway)
    {
        if (payload.Length < ForceIpPayloadSize)
            throw new ArgumentException($"destination too small for a FORCEIP payload ({ForceIpPayloadSize} bytes)", nameof(payload));
        var macBytes = (mac ?? throw new ArgumentNullException(nameof(mac))).GetAddressBytes();
        if (macBytes.Length != 6)
            throw new GevException($"MAC address must be 6 bytes, got {macBytes.Length}");
        payload.Slice(0, ForceIpPayloadSize).Clear();
        macBytes.AsSpan().CopyTo(payload.Slice(2));
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(20), Ipv4ToUInt32(ip));
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(36), Ipv4ToUInt32(subnet));
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(52), Ipv4ToUInt32(gateway));
    }

    public static void ReadForceIp(ReadOnlySpan<byte> payload, out PhysicalAddress mac, out IPAddress ip, out IPAddress subnet, out IPAddress gateway)
    {
        if (payload.Length < ForceIpPayloadSize)
            throw new GevException($"FORCEIP payload too short: {payload.Length} bytes (expected {ForceIpPayloadSize})");
        mac = new PhysicalAddress(payload.Slice(2, 6).ToArray());
        ip = Ipv4FromBytes(payload.Slice(20, 4));
        subnet = Ipv4FromBytes(payload.Slice(36, 4));
        gateway = Ipv4FromBytes(payload.Slice(52, 4));
    }

    // ------------------------------------------------------------------ ACK writers (장치 쪽)

    /// <summary>임의 ACK 를 쓴다. 반환값은 전체 길이.</summary>
    public static int WriteAck(Span<byte> dst, ushort status, ushort ackCommand, ushort reqId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new GevException($"ack payload {payload.Length} bytes exceeds the {MaxPayload} byte limit");
        var total = GvcpConst.HeaderSize + payload.Length;
        if (dst.Length < total)
            throw new ArgumentException($"destination too small for the ack ({total} bytes)", nameof(dst));
        new GvcpAckHeader(status, ackCommand, (ushort)payload.Length, reqId).Write(dst);
        payload.CopyTo(dst.Slice(GvcpConst.HeaderSize));
        return total;
    }

    public static int WriteReadRegAck(Span<byte> dst, ushort reqId, ReadOnlySpan<uint> values, ushort status = GvcpConst.StatusSuccess)
    {
        if (values.Length > GvcpConst.MaxRegsPerPacket)
            throw new GevException($"READREG_ACK carries {values.Length} values, more than the {GvcpConst.MaxRegsPerPacket} allowed");
        var total = GvcpConst.HeaderSize + values.Length * 4;
        if (dst.Length < total)
            throw new ArgumentException($"destination too small for READREG_ACK ({total} bytes)", nameof(dst));
        new GvcpAckHeader(status, GvcpConst.ReadRegAck, (ushort)(values.Length * 4), reqId).Write(dst);
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(GvcpConst.HeaderSize + i * 4), values[i]);
        return total;
    }

    /// <summary>WRITEREG_ACK: [reserved u16][index u16] — index 는 쓴 레지스터 수(오류 시 실패한 항목 번호).</summary>
    public static int WriteWriteRegAck(Span<byte> dst, ushort reqId, ushort index, ushort status = GvcpConst.StatusSuccess)
        => WriteIndexAck(dst, GvcpConst.WriteRegAck, reqId, index, status);

    public static int WriteReadMemAck(Span<byte> dst, ushort reqId, uint address, ReadOnlySpan<byte> data, ushort status = GvcpConst.StatusSuccess)
    {
        if (data.Length > GvcpConst.MaxMemPayload)
            throw new GevException($"READMEM_ACK data {data.Length} bytes exceeds the {GvcpConst.MaxMemPayload} byte limit");
        var total = GvcpConst.HeaderSize + 4 + data.Length;
        if (dst.Length < total)
            throw new ArgumentException($"destination too small for READMEM_ACK ({total} bytes)", nameof(dst));
        new GvcpAckHeader(status, GvcpConst.ReadMemAck, (ushort)(4 + data.Length), reqId).Write(dst);
        BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(GvcpConst.HeaderSize), address);
        data.CopyTo(dst.Slice(GvcpConst.HeaderSize + 4));
        return total;
    }

    /// <summary>WRITEMEM_ACK: [reserved u16][index u16] — index 는 쓴 바이트 수.</summary>
    public static int WriteWriteMemAck(Span<byte> dst, ushort reqId, ushort index, ushort status = GvcpConst.StatusSuccess)
        => WriteIndexAck(dst, GvcpConst.WriteMemAck, reqId, index, status);

    /// <summary>PENDING_ACK: [reserved u16][time-to-completion ms u16].</summary>
    public static int WritePendingAck(Span<byte> dst, ushort reqId, ushort timeToCompletionMs)
    {
        const int total = GvcpConst.HeaderSize + PendingAckPayloadSize;
        if (dst.Length < total)
            throw new ArgumentException($"destination too small for PENDING_ACK ({total} bytes)", nameof(dst));
        new GvcpAckHeader(GvcpConst.StatusSuccess, GvcpConst.PendingAck, PendingAckPayloadSize, reqId).Write(dst);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(GvcpConst.HeaderSize), 0);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(GvcpConst.HeaderSize + 2), timeToCompletionMs);
        return total;
    }

    /// <summary>DISCOVERY_ACK: 부트스트랩 0x0000..0x00F7 (248바이트) 그대로.</summary>
    public static int WriteDiscoveryAck(Span<byte> dst, ushort reqId, ReadOnlySpan<byte> bootstrap)
    {
        if (bootstrap.Length < GvbsAddr.DiscoveryDataLen)
            throw new GevException($"DISCOVERY_ACK needs {GvbsAddr.DiscoveryDataLen} bootstrap bytes, got {bootstrap.Length}");
        return WriteAck(dst, GvcpConst.StatusSuccess, GvcpConst.DiscoveryAck, reqId, bootstrap.Slice(0, GvbsAddr.DiscoveryDataLen));
    }

    public static int WriteForceIpAck(Span<byte> dst, ushort reqId, ushort status = GvcpConst.StatusSuccess)
        => WriteAck(dst, status, GvcpConst.ForceIpAck, reqId, ReadOnlySpan<byte>.Empty);

    private static int WriteIndexAck(Span<byte> dst, ushort ackCommand, ushort reqId, ushort index, ushort status)
    {
        const int total = GvcpConst.HeaderSize + WriteAckPayloadSize;
        if (dst.Length < total)
            throw new ArgumentException($"destination too small for {CommandName(ackCommand)}_ACK ({total} bytes)", nameof(dst));
        new GvcpAckHeader(status, ackCommand, WriteAckPayloadSize, reqId).Write(dst);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(GvcpConst.HeaderSize), 0);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(GvcpConst.HeaderSize + 2), index);
        return total;
    }

    // ------------------------------------------------------------------ IPv4 변환

    /// <summary>IPv4 주소를 빅엔디언 u32 로. IPv6 이면 <see cref="GevException"/>.</summary>
    public static uint Ipv4ToUInt32(IPAddress address)
    {
        if (address is null) throw new ArgumentNullException(nameof(address));
        var b = address.GetAddressBytes();
        if (b.Length != 4)
            throw new GevException($"{address} is not an IPv4 address");
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    public static IPAddress Ipv4FromUInt32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return new IPAddress(b);
    }

    public static IPAddress Ipv4FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
            throw new GevException($"IPv4 address needs 4 bytes, got {bytes.Length}");
        return new IPAddress(bytes.Slice(0, 4).ToArray());
    }
}
