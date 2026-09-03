using System.Buffers.Binary;

namespace GevSharp.Gvcp;

/// <summary>
/// 장치가 보낸 ACK 한 개. 수신 스레드의 재사용 버퍼에서 페이로드를 복사해 두므로 요청자가 나중에 읽어도 안전하다.
/// 접근자는 ack command 종류를 확인하고 길이를 검증한다 — 다른 종류의 ACK 에서 값을 꺼내려 하면 <see cref="GevException"/>.
/// </summary>
public sealed class GvcpAck
{
    private readonly byte[] _payload;

    public GvcpAck(ushort status, ushort command, ushort reqId, byte[] payload)
    {
        Status = status;
        Command = command;
        ReqId = reqId;
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public ushort Status { get; }
    /// <summary>ack command (예: READREG_ACK = 0x0081).</summary>
    public ushort Command { get; }
    public ushort ReqId { get; }
    public bool IsError => GvcpConst.IsError(Status);
    public string Name => GvcpPacket.CommandName(Command);
    public ReadOnlyMemory<byte> Payload => _payload;
    public int PayloadLength => _payload.Length;

    /// <summary>헤더를 검증하고 페이로드를 복사한다. 8바이트 미만·페이로드 부족은 <see cref="GevException"/>.</summary>
    public static GvcpAck Parse(ReadOnlySpan<byte> packet)
    {
        var header = GvcpAckHeader.Parse(packet);
        return new GvcpAck(header.Status, header.Command, header.ReqId, packet.Slice(GvcpConst.HeaderSize, header.Length).ToArray());
    }

    // ------------------------------------------------------------------ READREG_ACK

    /// <summary>READREG_ACK 가 나른 값의 개수.</summary>
    public int RegCount
    {
        get
        {
            Require(GvcpConst.ReadRegAck);
            if ((_payload.Length & 3) != 0)
                throw new GevException($"READREG_ACK payload length {_payload.Length} is not a multiple of 4");
            return _payload.Length / 4;
        }
    }

    public uint GetRegValue(int index)
    {
        var n = RegCount;
        if ((uint)index >= (uint)n)
            throw new GevException($"READREG_ACK has {n} value(s); index {index} is out of range");
        return BinaryPrimitives.ReadUInt32BigEndian(_payload.AsSpan(index * 4));
    }

    public uint[] GetRegValues()
    {
        var n = RegCount;
        var values = new uint[n];
        for (var i = 0; i < n; i++)
            values[i] = BinaryPrimitives.ReadUInt32BigEndian(_payload.AsSpan(i * 4));
        return values;
    }

    // ------------------------------------------------------------------ WRITEREG_ACK / WRITEMEM_ACK

    /// <summary>WRITEREG_ACK 의 쓴 레지스터 수, WRITEMEM_ACK 의 쓴 바이트 수. 페이로드가 4바이트 미만이면 <see cref="GevException"/>.</summary>
    public ushort WriteIndex
    {
        get
        {
            if (Command != GvcpConst.WriteRegAck && Command != GvcpConst.WriteMemAck)
                throw new GevException($"{Name}_ACK carries no write index");
            if (_payload.Length < GvcpPacket.WriteAckPayloadSize)
                throw new GevException($"{Name}_ACK payload too short for an index: {_payload.Length} bytes");
            return BinaryPrimitives.ReadUInt16BigEndian(_payload.AsSpan(2));
        }
    }

    /// <summary>index 를 예외 없이 읽는다 — 일부 장치는 빈 페이로드로 응답하므로 로그 용도로만 쓴다.</summary>
    public bool TryGetWriteIndex(out ushort index)
    {
        if ((Command == GvcpConst.WriteRegAck || Command == GvcpConst.WriteMemAck) && _payload.Length >= GvcpPacket.WriteAckPayloadSize)
        {
            index = BinaryPrimitives.ReadUInt16BigEndian(_payload.AsSpan(2));
            return true;
        }
        index = 0;
        return false;
    }

    // ------------------------------------------------------------------ READMEM_ACK

    /// <summary>READMEM_ACK 가 되돌려 준 시작 주소.</summary>
    public uint MemAddress
    {
        get
        {
            Require(GvcpConst.ReadMemAck);
            if (_payload.Length < 4)
                throw new GevException($"READMEM_ACK payload too short for an address: {_payload.Length} bytes");
            return BinaryPrimitives.ReadUInt32BigEndian(_payload);
        }
    }

    /// <summary>READMEM_ACK 의 데이터(주소 4바이트 뒤).</summary>
    public ReadOnlySpan<byte> MemData
    {
        get
        {
            Require(GvcpConst.ReadMemAck);
            if (_payload.Length < 4)
                throw new GevException($"READMEM_ACK payload too short for an address: {_payload.Length} bytes");
            return _payload.AsSpan(4);
        }
    }

    // ------------------------------------------------------------------ PENDING_ACK

    /// <summary>PENDING_ACK 의 time-to-completion (ms).</summary>
    public int PendingAckTimeMs
    {
        get
        {
            Require(GvcpConst.PendingAck);
            if (_payload.Length < GvcpPacket.PendingAckPayloadSize)
                throw new GevException($"PENDING_ACK payload too short: {_payload.Length} bytes");
            return BinaryPrimitives.ReadUInt16BigEndian(_payload.AsSpan(2));
        }
    }

    // ------------------------------------------------------------------ DISCOVERY_ACK

    /// <summary>DISCOVERY_ACK 의 부트스트랩 블록 248바이트. 짧으면 <see cref="GevException"/>.</summary>
    public ReadOnlySpan<byte> DiscoveryData
    {
        get
        {
            Require(GvcpConst.DiscoveryAck);
            if (_payload.Length < GvbsAddr.DiscoveryDataLen)
                throw new GevException($"DISCOVERY_ACK payload too short: {_payload.Length} bytes (expected {GvbsAddr.DiscoveryDataLen})");
            return _payload.AsSpan(0, GvbsAddr.DiscoveryDataLen);
        }
    }

    private void Require(ushort ackCommand)
    {
        if (Command != ackCommand)
            throw new GevException($"expected {GvcpPacket.CommandName(ackCommand)}_ACK (0x{ackCommand:X4}) but this is {Name} (0x{Command:X4})");
    }

    public override string ToString() => $"{Name}_ACK status=0x{Status:X4} req_id={ReqId} payload={_payload.Length}B";
}
