using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;

namespace GevSharp.Gvcp;

/// <summary>
/// 호스트가 보내는 GVCP 명령 한 개 — 헤더+페이로드가 완성된 불변 템플릿이다. req_id 자리는 0 으로 비워 두고
/// 채널이 전송 직전 자기 송신 버퍼에 복사하면서 채운다(<see cref="WriteTo"/>). 그래서 같은 인스턴스를 재사용해도 안전하다.
/// 정적 팩토리가 길이·정렬·개수 제한을 검증한다 — 장치가 BAD_ALIGNMENT 로 거절할 패킷은 만들지 않는다.
/// </summary>
public sealed class GvcpCmd
{
    private readonly byte[] _packet;

    private GvcpCmd(byte[] packet, ushort expectedAck)
    {
        _packet = packet;
        ExpectedAck = expectedAck;
    }

    public ushort Command => BinaryPrimitives.ReadUInt16BigEndian(_packet.AsSpan(2));
    public byte Flags => _packet[1];
    /// <summary>이 명령에 대한 정상 응답의 ack command 값(보통 command + 1).</summary>
    public ushort ExpectedAck { get; }
    public bool IsAckRequired => (Flags & GvcpConst.FlagAckRequired) != 0;
    public string Name => GvcpPacket.CommandName(Command);
    /// <summary>헤더 포함 전체 길이.</summary>
    public int Length => _packet.Length;
    public int PayloadLength => _packet.Length - GvcpConst.HeaderSize;
    /// <summary>req_id 가 0 인 템플릿 바이트.</summary>
    public ReadOnlyMemory<byte> Packet => _packet;
    public ReadOnlySpan<byte> Payload => _packet.AsSpan(GvcpConst.HeaderSize);

    /// <summary>패킷을 dst 에 복사하고 req_id 를 채운다. dst 는 <see cref="Length"/> 이상이어야 한다.</summary>
    public void WriteTo(Span<byte> dst, ushort reqId)
    {
        if (dst.Length < _packet.Length)
            throw new ArgumentException($"destination too small for {Name} ({_packet.Length} bytes)", nameof(dst));
        _packet.AsSpan().CopyTo(dst);
        BinaryPrimitives.WriteUInt16BigEndian(dst.Slice(6), reqId);
    }

    public byte[] ToArray(ushort reqId)
    {
        var copy = new byte[_packet.Length];
        WriteTo(copy, reqId);
        return copy;
    }

    // ------------------------------------------------------------------ factories

    /// <summary>DISCOVERY_CMD — 페이로드 없음. 브로드캐스트 탐색은 allow-broadcast-ack 를 켠다(flags 0x11).</summary>
    public static GvcpCmd Discovery(bool allowBroadcastAck = true)
    {
        var flags = allowBroadcastAck ? (byte)(GvcpConst.FlagAckRequired | GvcpConst.FlagAllowBroadcastAck) : GvcpConst.FlagAckRequired;
        return new GvcpCmd(NewPacket(GvcpConst.DiscoveryCmd, flags, 0), GvcpConst.DiscoveryAck);
    }

    /// <summary>FORCEIP_CMD — MAC 으로 지목한 장치의 IP/서브넷/게이트웨이를 바꾼다. 장치는 보통 응답하지 않으므로 ack 는 기대하지 않는다.</summary>
    public static GvcpCmd ForceIp(PhysicalAddress mac, IPAddress ip, IPAddress subnet, IPAddress gateway, bool allowBroadcastAck = true)
    {
        var flags = allowBroadcastAck ? (byte)(GvcpConst.FlagAckRequired | GvcpConst.FlagAllowBroadcastAck) : GvcpConst.FlagAckRequired;
        var packet = NewPacket(GvcpConst.ForceIpCmd, flags, GvcpPacket.ForceIpPayloadSize);
        GvcpPacket.WriteForceIp(packet.AsSpan(GvcpConst.HeaderSize), mac, ip, subnet, gateway);
        return new GvcpCmd(packet, GvcpConst.ForceIpAck);
    }

    public static GvcpCmd ReadReg(uint address)
    {
        ThrowIfMisaligned("READREG", address);
        var packet = NewPacket(GvcpConst.ReadRegCmd, GvcpConst.FlagAckRequired, 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(GvcpConst.HeaderSize), address);
        return new GvcpCmd(packet, GvcpConst.ReadRegAck);
    }

    /// <summary>다중 레지스터 READREG — 1..135 개, 각 주소 4바이트 정렬.</summary>
    public static GvcpCmd ReadRegs(ReadOnlySpan<uint> addresses)
    {
        if (addresses.Length == 0)
            throw new GevException("READREG needs at least one address");
        if (addresses.Length > GvcpConst.MaxRegsPerPacket)
            throw new GevException($"READREG can carry at most {GvcpConst.MaxRegsPerPacket} addresses, got {addresses.Length}");
        var packet = NewPacket(GvcpConst.ReadRegCmd, GvcpConst.FlagAckRequired, addresses.Length * 4);
        for (var i = 0; i < addresses.Length; i++)
        {
            ThrowIfMisaligned("READREG", addresses[i]);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(GvcpConst.HeaderSize + i * 4), addresses[i]);
        }
        return new GvcpCmd(packet, GvcpConst.ReadRegAck);
    }

    public static GvcpCmd WriteReg(uint address, uint value)
    {
        ThrowIfMisaligned("WRITEREG", address);
        var packet = NewPacket(GvcpConst.WriteRegCmd, GvcpConst.FlagAckRequired, 8);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(GvcpConst.HeaderSize), address);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(GvcpConst.HeaderSize + 4), value);
        return new GvcpCmd(packet, GvcpConst.WriteRegAck);
    }

    /// <summary>다중 레지스터 WRITEREG — 1..67 쌍. 장치가 concatenation 을 지원할 때만 2개 이상을 보낸다.</summary>
    public static GvcpCmd WriteRegs(ReadOnlySpan<KeyValuePair<uint, uint>> writes)
    {
        if (writes.Length == 0)
            throw new GevException("WRITEREG needs at least one entry");
        if (writes.Length > GvcpPacket.MaxWriteRegsPerPacket)
            throw new GevException($"WRITEREG can carry at most {GvcpPacket.MaxWriteRegsPerPacket} entries, got {writes.Length}");
        var packet = NewPacket(GvcpConst.WriteRegCmd, GvcpConst.FlagAckRequired, writes.Length * 8);
        for (var i = 0; i < writes.Length; i++)
        {
            ThrowIfMisaligned("WRITEREG", writes[i].Key);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(GvcpConst.HeaderSize + i * 8), writes[i].Key);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(GvcpConst.HeaderSize + i * 8 + 4), writes[i].Value);
        }
        return new GvcpCmd(packet, GvcpConst.WriteRegAck);
    }

    /// <summary>READMEM_CMD — count 는 4 의 배수, 4..512. 주소 4바이트 정렬.</summary>
    public static GvcpCmd ReadMem(uint address, int count)
    {
        ThrowIfMisaligned("READMEM", address);
        ThrowIfBadMemLength("READMEM", count);
        if ((ulong)address + (ulong)count > 0x1_0000_0000UL)
            throw new GevException($"READMEM range 0x{address:X8}+{count} exceeds the 32-bit address space");
        var packet = NewPacket(GvcpConst.ReadMemCmd, GvcpConst.FlagAckRequired, GvcpPacket.ReadMemCmdPayloadSize);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(GvcpConst.HeaderSize), address);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(GvcpConst.HeaderSize + 4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(GvcpConst.HeaderSize + 6), (ushort)count);
        return new GvcpCmd(packet, GvcpConst.ReadMemAck);
    }

    /// <summary>WRITEMEM_CMD — data 길이는 4 의 배수, 4..512. 주소 4바이트 정렬.</summary>
    public static GvcpCmd WriteMem(uint address, ReadOnlySpan<byte> data)
    {
        ThrowIfMisaligned("WRITEMEM", address);
        ThrowIfBadMemLength("WRITEMEM", data.Length);
        if ((ulong)address + (ulong)data.Length > 0x1_0000_0000UL)
            throw new GevException($"WRITEMEM range 0x{address:X8}+{data.Length} exceeds the 32-bit address space");
        var packet = NewPacket(GvcpConst.WriteMemCmd, GvcpConst.FlagAckRequired, 4 + data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(GvcpConst.HeaderSize), address);
        data.CopyTo(packet.AsSpan(GvcpConst.HeaderSize + 4));
        return new GvcpCmd(packet, GvcpConst.WriteMemAck);
    }

    /// <summary>PACKETRESEND_CMD (ack 없음). 핫패스에서는 <see cref="GvcpChannel.SendPacketResend"/> 가 스택 버퍼로 직접 쓴다 — 이 팩토리는 편의용.</summary>
    public static GvcpCmd PacketResend(ulong blockId, uint firstPacketId, uint lastPacketId, bool extendedIds, int streamChannel = 0)
    {
        var packet = new byte[GvcpConst.HeaderSize + (extendedIds ? GvcpPacket.PacketResendExtPayloadSize : GvcpPacket.PacketResendStdPayloadSize)];
        GvcpPacket.WritePacketResend(packet, 0, blockId, firstPacketId, lastPacketId, extendedIds, streamChannel);
        return new GvcpCmd(packet, GvcpConst.PacketResendAck);
    }

    /// <summary>임의 명령(EVENT ack, ACTION 등) — 페이로드를 그대로 싣는다.</summary>
    public static GvcpCmd Raw(ushort command, byte flags, ReadOnlySpan<byte> payload, ushort expectedAck)
    {
        if (payload.Length > GvcpPacket.MaxPayload)
            throw new GevException($"GVCP payload {payload.Length} bytes exceeds the {GvcpPacket.MaxPayload} byte limit");
        var packet = NewPacket(command, flags, payload.Length);
        payload.CopyTo(packet.AsSpan(GvcpConst.HeaderSize));
        return new GvcpCmd(packet, expectedAck);
    }

    private static byte[] NewPacket(ushort command, byte flags, int payloadLen)
    {
        var packet = new byte[GvcpConst.HeaderSize + payloadLen];
        new GvcpCmdHeader(command, (ushort)payloadLen, 0, flags).Write(packet);
        return packet;
    }

    private static void ThrowIfMisaligned(string op, uint address)
    {
        if ((address & 3) != 0)
            throw new GevException($"{op} address 0x{address:X8} is not 4-byte aligned");
    }

    private static void ThrowIfBadMemLength(string op, int length)
    {
        if (length <= 0 || (length & 3) != 0 || length > GvcpConst.MaxMemPayload)
            throw new GevException($"{op} length {length} must be a multiple of 4 between 4 and {GvcpConst.MaxMemPayload}");
    }
}
