using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GevSharp.Gvcp;
using GevSharp.Sim;

namespace GevSharp.Tests.Simulator;

/// <summary>시뮬레이터가 돌려준 GVCP ACK 한 개 — 헤더 필드와 페이로드, 보낸 쪽 엔드포인트.</summary>
internal sealed record RawGvcpAck(ushort Status, ushort Command, ushort ReqId, byte[] Payload, IPEndPoint From)
{
    public bool IsError => (Status & 0x8000) != 0;
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Payload.AsSpan(offset, 4));
    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Payload.AsSpan(offset, 2));

    /// <summary>NUL 종료 UTF-8 문자열을 페이로드 오프셋에서 읽는다.</summary>
    public string Str(int offset, int length)
    {
        int n = Array.IndexOf(Payload, (byte)0, offset, length) - offset;
        if (n < 0) n = length;
        return Encoding.UTF8.GetString(Payload, offset, n);
    }
}

/// <summary>
/// 시뮬레이터 테스트 전용 GVCP 클라이언트. 패킷 조립·해석을 라이브러리와 무관하게 여기서 직접 한다 —
/// 라이브러리 쪽 버그와 시뮬레이터 쪽 버그가 서로 상쇄되어 테스트가 통과하는 일을 막는다. 공유하는 것은 명령·상태 상수뿐이다.
/// 한 인스턴스가 곧 한 "애플리케이션"이다(CCP 소유 판정은 소켓 엔드포인트로 이뤄진다).
/// </summary>
internal sealed class RawGvcpClient : IDisposable
{
    private readonly Socket _sock;
    private ushort _reqId;

    /// <summary>
    /// ACK 를 기다리는 상한. 장치가 한 명령 안에서 스스로 기다릴 수 있는 최대 시간(<see cref="SimDevice.SenderJoinTimeoutMs"/> —
    /// AcquisitionStart/Stop 이 송신 스레드를 거두는 상한)에 굶주린 러너의 스케줄링 여유를 더한 값이다. 이보다 짧게 잡으면
    /// 규정대로 늦게 답한 장치를, 또 과부하에서는 멀쩡한 장치를 무응답으로 오판한다.
    /// 상한일 뿐 재시도가 아니다 — 명령을 잃거나 무시한 장치는 아예 답하지 않으므로 어떤 유한한 상한에서도 그대로 걸린다.
    /// **이 값은 응답성을 재는 잣대가 아니다.** 응답기가 명령을 붙들고 있는지는 호스트 왕복이 아니라 장치 쪽에서
    /// <see cref="SimDevice.MaxCommandHandleMs"/> 로 좁게 재고, 그 값을 스트리밍 테스트가 단언한다 — 그래서 여기서는
    /// 여유를 넉넉히 줘도 잃는 검출력이 없다.
    /// </summary>
    public const int DefaultTimeoutMs = SimDevice.SenderJoinTimeoutMs + 7000;

    public RawGvcpClient(IPEndPoint device, int timeoutMs = DefaultTimeoutMs)
    {
        Device = device;
        TimeoutMs = timeoutMs;
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        LocalEndPoint = (IPEndPoint)_sock.LocalEndPoint!;
    }

    public IPEndPoint Device { get; }
    public IPEndPoint LocalEndPoint { get; }
    public int TimeoutMs { get; set; }
    /// <summary>마지막 <see cref="Request"/> 가 쓴 req_id.</summary>
    public ushort LastReqId { get; private set; }

    public ushort NextReqId()
    {
        _reqId++;
        if (_reqId == 0) _reqId = 1;
        return _reqId;
    }

    // ---- 패킷 조립 ----

    public static byte[] BuildCmd(ushort cmd, byte flags, ushort reqId, ReadOnlySpan<byte> payload)
    {
        var pkt = new byte[8 + payload.Length];
        pkt[0] = 0x42;
        pkt[1] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), cmd);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(4), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(6), reqId);
        payload.CopyTo(pkt.AsSpan(8));
        return pkt;
    }

    public static byte[] ReadRegPayload(params uint[] addrs)
    {
        var p = new byte[addrs.Length * 4];
        for (int i = 0; i < addrs.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(i * 4), addrs[i]);
        return p;
    }

    public static byte[] WriteRegPayload(params (uint Addr, uint Value)[] regs)
    {
        var p = new byte[regs.Length * 8];
        for (int i = 0; i < regs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(i * 8), regs[i].Addr);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(i * 8 + 4), regs[i].Value);
        }
        return p;
    }

    public static byte[] ReadMemPayload(uint addr, ushort count)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0), addr);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(6), count);
        return p;
    }

    public static byte[] WriteMemPayload(uint addr, ReadOnlySpan<byte> data)
    {
        var p = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(0), addr);
        data.CopyTo(p.AsSpan(4));
        return p;
    }

    /// <summary>표준(12바이트) 또는 확장(20바이트) PACKETRESEND 페이로드.</summary>
    public static byte[] PacketResendPayload(ulong blockId, uint first, uint last, bool extended, ushort channel = 0)
    {
        if (!extended)
        {
            var p = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(0), channel);
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), (ushort)blockId);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), first & 0x00FF_FFFF);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8), last & 0x00FF_FFFF);
            return p;
        }
        var e = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(e.AsSpan(0), channel);
        BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(4), first);
        BinaryPrimitives.WriteUInt32BigEndian(e.AsSpan(8), last);
        BinaryPrimitives.WriteUInt64BigEndian(e.AsSpan(12), blockId);
        return e;
    }

    // ---- 송수신 ----

    public void SendRaw(byte[] pkt) => _sock.SendTo(pkt, Device);

    /// <summary>ACK 하나를 기다린다. 시간 안에 오지 않으면 null.</summary>
    public RawGvcpAck? Receive(int? timeoutMs = null)
    {
        int t = timeoutMs ?? TimeoutMs;
        var buf = new byte[4096];
        EndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            if (!_sock.Poll(t * 1000, SelectMode.SelectRead)) return null;
            int n = _sock.ReceiveFrom(buf, ref ep);
            if (n < 8) throw new InvalidOperationException($"short GVCP reply of {n} bytes");
            ushort status = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0));
            ushort cmd = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2));
            int len = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4));
            ushort req = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
            if (8 + len != n) throw new InvalidOperationException($"GVCP reply length field {len} does not match datagram of {n} bytes");
            var payload = new byte[len];
            Buffer.BlockCopy(buf, 8, payload, 0, len);
            return new RawGvcpAck(status, cmd, req, payload, (IPEndPoint)ep);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return null;
        }
    }

    /// <summary>명령을 보내고 첫 응답을 돌려준다. 응답이 없으면 <see cref="TimeoutException"/>.</summary>
    public RawGvcpAck Request(ushort cmd, ReadOnlySpan<byte> payload, byte flags = GvcpConst.FlagAckRequired)
    {
        ushort id = NextReqId();
        LastReqId = id;
        SendRaw(BuildCmd(cmd, flags, id, payload));
        return Receive() ?? throw new TimeoutException($"no reply to GVCP command 0x{cmd:X4} (req_id {id}) within {TimeoutMs} ms");
    }

    // ---- 명령별 편의 메서드 ----

    public RawGvcpAck Discovery() => Request(GvcpConst.DiscoveryCmd, ReadOnlySpan<byte>.Empty, GvcpConst.FlagAckRequired | GvcpConst.FlagAllowBroadcastAck);

    public (ushort Status, uint[] Values) ReadRegs(params uint[] addrs)
    {
        var ack = Request(GvcpConst.ReadRegCmd, ReadRegPayload(addrs));
        var values = new uint[ack.Payload.Length / 4];
        for (int i = 0; i < values.Length; i++) values[i] = ack.U32(i * 4);
        return (ack.Status, values);
    }

    /// <summary>레지스터 하나를 읽는다. 오류 status 면 예외.</summary>
    public uint ReadReg(uint addr)
    {
        var (status, values) = ReadRegs(addr);
        if (status != GvcpConst.StatusSuccess) throw new InvalidOperationException($"READREG 0x{addr:X8} failed with 0x{status:X4}");
        return values[0];
    }

    public (ushort Status, ushort Index) WriteRegs(params (uint Addr, uint Value)[] regs)
    {
        var ack = Request(GvcpConst.WriteRegCmd, WriteRegPayload(regs));
        // PENDING_ACK 는 실제 ACK 가 뒤따른다 — 편의 메서드는 그것까지 기다린다
        while (ack.Command == GvcpConst.PendingAck)
        {
            int wait = Math.Max(ack.U16(2) * 2, TimeoutMs);
            ack = Receive(wait) ?? throw new TimeoutException("no WRITEREG_ACK after PENDING_ACK");
        }
        return (ack.Status, ack.U16(2));
    }

    public (ushort Status, ushort Index) WriteReg(uint addr, uint value) => WriteRegs((addr, value));

    /// <summary>레지스터 하나를 쓰고 성공을 확인한다.</summary>
    public void WriteRegOk(uint addr, uint value)
    {
        var (status, _) = WriteReg(addr, value);
        if (status != GvcpConst.StatusSuccess) throw new InvalidOperationException($"WRITEREG 0x{addr:X8} failed with 0x{status:X4}");
    }

    public (ushort Status, byte[] Data) ReadMem(uint addr, ushort count)
    {
        var ack = Request(GvcpConst.ReadMemCmd, ReadMemPayload(addr, count));
        if (ack.Payload.Length < 4) return (ack.Status, Array.Empty<byte>());
        var data = new byte[ack.Payload.Length - 4];
        Buffer.BlockCopy(ack.Payload, 4, data, 0, data.Length);
        return (ack.Status, data);
    }

    public (ushort Status, ushort Index) WriteMem(uint addr, ReadOnlySpan<byte> data)
    {
        var ack = Request(GvcpConst.WriteMemCmd, WriteMemPayload(addr, data));
        while (ack.Command == GvcpConst.PendingAck)
            ack = Receive() ?? throw new TimeoutException("no WRITEMEM_ACK after PENDING_ACK");
        return (ack.Status, ack.U16(2));
    }

    /// <summary>응답 없는 PACKETRESEND(ack_required = 0).</summary>
    public void SendPacketResend(ulong blockId, uint first, uint last, bool extended = false, ushort channel = 0)
    {
        byte flags = extended ? GvcpConst.FlagExtendedIds : (byte)0;
        SendRaw(BuildCmd(GvcpConst.PacketResendCmd, flags, NextReqId(), PacketResendPayload(blockId, first, last, extended, channel)));
    }

    public void Dispose() => _sock.Dispose();
}
