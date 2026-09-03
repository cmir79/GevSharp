using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GevSharp.Gvcp;

namespace GevSharp.Tests.Gvcp;

/// <summary>
/// 루프백 최소 응답기 — 64 KiB 메모리 이미지로 DISCOVERY/READREG/WRITEREG/READMEM/WRITEMEM 에 답한다.
/// 패킷은 라이브러리의 작성기를 쓰지 않고 손으로 조립한다(대칭 오류 상쇄 방지).
/// 시나리오 노브: 지연, 틀린 req_id 선행, PENDING_ACK 선행(전체 또는 한 주소만), N 회 드롭, 침묵, 오류 상태, CCP 점유, 잘린 DISCOVERY_ACK,
/// 잘못된 ack command, 잘린 응답, READMEM_ACK 길이 어긋남, PENDING_ACK 만 보내고 멈추는 주소.
/// 받은 요청은 도착 시각(<see cref="ElapsedMs"/> 기준)과 함께 기록한다 — 요청 사이의 간격을 재는 시험이 쓴다.
/// </summary>
internal sealed class GvcpTestResponder : IDisposable
{
    public const int MemorySize = 0x10000;

    private readonly Socket _socket;
    private readonly Thread _thread;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly ConcurrentQueue<RequestRecord> _requests = new();
    private volatile bool _isDisposed;
    private int _dropNext;
    private int _wrongReqIdNext;
    private int _wrongCommandNext;
    private int _truncateReplyNext;

    public GvcpTestResponder()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        FillBootstrap();
        _thread = new Thread(Loop) { IsBackground = true, Name = "GvcpTestResponder" };
        _thread.Start();
    }

    public IPEndPoint EndPoint { get; }
    public byte[] Memory { get; } = new byte[MemorySize];
    /// <summary>응답기가 만들어진 뒤 흐른 시간(ms) — <see cref="RequestRecord.AtMs"/> 와 같은 기준이다.</summary>
    public long ElapsedMs => _clock.ElapsedMilliseconds;

    // ---- 시나리오 노브(테스트 스레드가 쓰고 응답기 스레드가 읽는다) ----
    private volatile int _replyDelayMs;
    private volatile bool _isSilent;
    private volatile int _pendingAckMs;
    private volatile int _pendingAckDelayMs;
    private long _pendingAckAddr = -1;
    private volatile bool _isCcpHeldByOther;
    private volatile int _truncateDiscoveryTo;
    private long _errorAddr = -1;
    private volatile int _errorStatus = GvcpConst.StatusWriteProtect;
    private volatile bool _isAckEmptyForWrites;
    private volatile int _readMemLengthDelta;
    private long _pendingAckStallAddr = -1;
    private volatile int _pendingAckStallMs = 60_000;

    /// <summary>모든 응답을 이만큼 늦춘다.</summary>
    public int ReplyDelayMs { get => _replyDelayMs; set => _replyDelayMs = value; }
    /// <summary>요청은 기록하되 아무 응답도 하지 않는다.</summary>
    public bool IsSilent { get => _isSilent; set => _isSilent = value; }
    /// <summary>0 보다 크면 본 응답 전에 이 time-to-completion 을 실은 PENDING_ACK 를 먼저 보낸다.</summary>
    public int PendingAckMs { get => _pendingAckMs; set => _pendingAckMs = value; }
    /// <summary>PENDING_ACK 와 본 응답 사이의 간격.</summary>
    public int PendingAckDelayMs { get => _pendingAckDelayMs; set => _pendingAckDelayMs = value; }
    /// <summary><see cref="PendingAckMs"/> 를 이 주소를 건드리는 요청에만 적용한다. null = 모든 요청.</summary>
    public uint? PendingAckAddr
    {
        get { var v = Interlocked.Read(ref _pendingAckAddr); return v < 0 ? null : (uint)v; }
        set => Interlocked.Exchange(ref _pendingAckAddr, value.HasValue ? value.Value : -1L);
    }
    /// <summary>다른 호스트가 CCP 를 쥐고 있다 — 0 이 아닌 CCP 쓰기에 ACCESS_DENIED.</summary>
    public bool IsCcpHeldByOther { get => _isCcpHeldByOther; set => _isCcpHeldByOther = value; }
    /// <summary>0 보다 크면 DISCOVERY_ACK 페이로드를 이 길이로 자른다.</summary>
    public int TruncateDiscoveryTo { get => _truncateDiscoveryTo; set => _truncateDiscoveryTo = value; }
    /// <summary>이 주소를 건드리는 요청에 <see cref="ErrorStatus"/> 로 답한다. null = 없음.</summary>
    public uint? ErrorAddr
    {
        get { var v = Interlocked.Read(ref _errorAddr); return v < 0 ? null : (uint)v; }
        set => Interlocked.Exchange(ref _errorAddr, value.HasValue ? value.Value : -1L);
    }
    public ushort ErrorStatus { get => (ushort)_errorStatus; set => _errorStatus = value; }
    /// <summary>WRITEREG_ACK 를 빈 페이로드로 보낸다(index 를 안 채우는 장치 흉내).</summary>
    public bool IsAckEmptyForWrites { get => _isAckEmptyForWrites; set => _isAckEmptyForWrites = value; }
    /// <summary>READMEM_ACK 에 실어 보내는 데이터 길이를 요청보다 이만큼 늘린다(음수면 줄인다).</summary>
    public int ReadMemLengthDelta { get => _readMemLengthDelta; set => _readMemLengthDelta = value; }
    /// <summary>이 주소를 건드리는 요청에는 <see cref="PendingAckStallMs"/> 를 예고한 PENDING_ACK 만 보내고 본 응답은 보내지 않는다. null = 없음.</summary>
    public uint? PendingAckStallAddr
    {
        get { var v = Interlocked.Read(ref _pendingAckStallAddr); return v < 0 ? null : (uint)v; }
        set => Interlocked.Exchange(ref _pendingAckStallAddr, value.HasValue ? value.Value : -1L);
    }
    /// <summary><see cref="PendingAckStallAddr"/> 의 PENDING_ACK 가 예고하는 완료 시간.</summary>
    public int PendingAckStallMs { get => _pendingAckStallMs; set => _pendingAckStallMs = value; }

    public void DropNext(int count) => Interlocked.Exchange(ref _dropNext, count);
    public void WrongReqIdNext(int count) => Interlocked.Exchange(ref _wrongReqIdNext, count);
    public void WrongCommandNext(int count) => Interlocked.Exchange(ref _wrongCommandNext, count);
    public void TruncateReplyNext(int count) => Interlocked.Exchange(ref _truncateReplyNext, count);

    public IReadOnlyList<RequestRecord> Requests => _requests.ToArray();
    public int CountOf(ushort command) => _requests.Count(r => r.Command == command);
    public int CountOfReg(ushort command, uint addr) => _requests.Count(r => r.Command == command && r.Addresses.Contains(addr));

    public uint ReadU32(uint addr) => BinaryPrimitives.ReadUInt32BigEndian(Memory.AsSpan((int)addr));
    public void WriteU32(uint addr, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Memory.AsSpan((int)addr), value);

    /// <param name="AtMs">응답기가 이 요청을 받은 시각 — <see cref="ElapsedMs"/> 와 같은 기준.</param>
    public sealed record RequestRecord(ushort Command, ushort ReqId, byte Flags, byte[] Payload, uint[] Addresses, long AtMs)
    {
        public uint[] Values => Command == GvcpConst.WriteRegCmd
            ? Enumerable.Range(0, Payload.Length / 8).Select(i => BinaryPrimitives.ReadUInt32BigEndian(Payload.AsSpan(i * 8 + 4))).ToArray()
            : Array.Empty<uint>();
    }

    private void FillBootstrap()
    {
        WriteU32(GvbsAddr.Version, 0x0001_0002);
        WriteU32(GvbsAddr.DeviceMode, 0x8000_0001);
        WriteU32(GvbsAddr.MacHigh, 0x0000_0011);
        WriteU32(GvbsAddr.MacLow, 0x2233_4455);
        WriteU32(GvbsAddr.SupportedIpCfg, 0x8000_0007);
        WriteU32(GvbsAddr.CurrentIpCfg, 0x0000_0001);
        WriteU32(GvbsAddr.CurrentIp, 0x7F00_0001);
        WriteU32(GvbsAddr.CurrentSubnet, 0xFF00_0000);
        WriteU32(GvbsAddr.CurrentGateway, 0x0000_0000);
        WriteString(GvbsAddr.ManufacturerName, "GevSharp Test");
        WriteString(GvbsAddr.ModelName, "Responder");
        WriteString(GvbsAddr.DeviceVersion, "1.0");
        WriteString(GvbsAddr.ManufacturerInfo, "loopback");
        WriteString(GvbsAddr.SerialNumber, "SN0001");
        WriteString(GvbsAddr.UserDefinedName, "unit");
        WriteU32(GvbsAddr.GvcpCapability, GvbsAddr.GvcpCapConcatenation | GvbsAddr.GvcpCapWriteMem | GvbsAddr.GvcpCapPacketResend | GvbsAddr.GvcpCapPendingAck);
        WriteU32(GvbsAddr.HeartbeatTimeout, 3000);
        WriteU32(GvbsAddr.TimestampTickFreqHigh, 0x0000_0000);
        WriteU32(GvbsAddr.TimestampTickFreqLow, 125_000_000);
        WriteU32(GvbsAddr.Ccp, 0);
    }

    private void WriteString(uint addr, string s)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(s);
        bytes.CopyTo(Memory, (int)addr);
    }

    private void Loop()
    {
        var buf = new byte[2048];
        var reply = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_isDisposed)
        {
            int n;
            try
            {
                n = _socket.ReceiveFrom(buf, ref from);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            catch (Exception)
            {
                break;
            }

            try
            {
                Handle(buf, n, reply, from);
            }
            catch (Exception)
            {
                // 테스트 응답기는 어떤 입력에도 죽지 않는다.
            }
        }
    }

    private void Handle(byte[] buf, int n, byte[] reply, EndPoint from)
    {
        if (n < 8 || buf[0] != 0x42) return;
        var command = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4));
        var reqId = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
        var flags = buf[1];
        var payload = buf.AsSpan(8, Math.Min(length, n - 8)).ToArray();
        var addresses = ExtractAddresses(command, payload);
        _requests.Enqueue(new RequestRecord(command, reqId, flags, payload, addresses, _clock.ElapsedMilliseconds));

        if (IsSilent) return;
        if (DecrementIfPositive(ref _dropNext)) return;
        if ((flags & GvcpConst.FlagAckRequired) == 0) return;

        // 완료를 한참 뒤로 예고해 놓고 본 응답을 보내지 않는 장치 — 요청 쪽이 상한 없이 기다리면 채널이 그만큼 붙들린다.
        if (PendingAckStallAddr is uint stallAddr && Array.IndexOf(addresses, stallAddr) >= 0)
        {
            SendPendingAck(reqId, from, PendingAckStallMs);
            return;
        }

        var replyLen = BuildReply(command, reqId, payload, reply);
        if (replyLen == 0) return;

        if (DecrementIfPositive(ref _wrongReqIdNext))
        {
            var wrong = (byte[])reply.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(wrong.AsSpan(6), (ushort)(reqId ^ 0x5555));
            _socket.SendTo(wrong, 0, replyLen, SocketFlags.None, from);
        }
        if (DecrementIfPositive(ref _wrongCommandNext))
        {
            var wrong = (byte[])reply.Clone();
            BinaryPrimitives.WriteUInt16BigEndian(wrong.AsSpan(2), GvcpConst.EventAck);
            _socket.SendTo(wrong, 0, replyLen, SocketFlags.None, from);
            return;
        }
        if (PendingAckMs > 0 && (PendingAckAddr is not uint pendingAddr || Array.IndexOf(addresses, pendingAddr) >= 0))
        {
            SendPendingAck(reqId, from, PendingAckMs);
            Thread.Sleep(PendingAckDelayMs);
        }
        if (ReplyDelayMs > 0) Thread.Sleep(ReplyDelayMs);
        if (DecrementIfPositive(ref _truncateReplyNext))
        {
            _socket.SendTo(reply, 0, Math.Max(8, replyLen - 2), SocketFlags.None, from);
            return;
        }
        _socket.SendTo(reply, 0, replyLen, SocketFlags.None, from);
    }

    private void SendPendingAck(ushort reqId, EndPoint to, int timeToCompletionMs)
    {
        var pending = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(pending.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16BigEndian(pending.AsSpan(2), GvcpConst.PendingAck);
        BinaryPrimitives.WriteUInt16BigEndian(pending.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt16BigEndian(pending.AsSpan(6), reqId);
        BinaryPrimitives.WriteUInt16BigEndian(pending.AsSpan(10), (ushort)timeToCompletionMs);
        _socket.SendTo(pending, 0, pending.Length, SocketFlags.None, to);
    }

    private static uint[] ExtractAddresses(ushort command, byte[] payload)
    {
        switch (command)
        {
            case GvcpConst.ReadRegCmd:
                return Enumerable.Range(0, payload.Length / 4).Select(i => BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i * 4))).ToArray();
            case GvcpConst.WriteRegCmd:
                return Enumerable.Range(0, payload.Length / 8).Select(i => BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i * 8))).ToArray();
            case GvcpConst.ReadMemCmd:
            case GvcpConst.WriteMemCmd:
                return payload.Length >= 4 ? new[] { BinaryPrimitives.ReadUInt32BigEndian(payload) } : Array.Empty<uint>();
            default:
                return Array.Empty<uint>();
        }
    }

    private int BuildReply(ushort command, ushort reqId, byte[] payload, byte[] reply)
    {
        switch (command)
        {
            case GvcpConst.DiscoveryCmd:
            {
                var len = TruncateDiscoveryTo > 0 ? TruncateDiscoveryTo : GvbsAddr.DiscoveryDataLen;
                Header(reply, GvcpConst.StatusSuccess, GvcpConst.DiscoveryAck, (ushort)len, reqId);
                Memory.AsSpan(0, len).CopyTo(reply.AsSpan(8));
                return 8 + len;
            }
            case GvcpConst.ReadRegCmd:
            {
                var count = payload.Length / 4;
                for (var i = 0; i < count; i++)
                {
                    var addr = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i * 4));
                    if (ErrorAddr == addr)
                        return Error(reply, GvcpConst.ReadRegAck, reqId, ErrorStatus);
                    if (addr + 4 > MemorySize)
                        return Error(reply, GvcpConst.ReadRegAck, reqId, GvcpConst.StatusInvalidAddress);
                    Memory.AsSpan((int)addr, 4).CopyTo(reply.AsSpan(8 + i * 4));
                }
                Header(reply, GvcpConst.StatusSuccess, GvcpConst.ReadRegAck, (ushort)(count * 4), reqId);
                return 8 + count * 4;
            }
            case GvcpConst.WriteRegCmd:
            {
                var count = payload.Length / 8;
                for (var i = 0; i < count; i++)
                {
                    var addr = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i * 8));
                    var value = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i * 8 + 4));
                    if (ErrorAddr == addr)
                        return IndexAck(reply, GvcpConst.WriteRegAck, reqId, (ushort)i, ErrorStatus);
                    if (addr == GvbsAddr.Ccp && IsCcpHeldByOther && value != 0)
                        return IndexAck(reply, GvcpConst.WriteRegAck, reqId, (ushort)i, GvcpConst.StatusAccessDenied);
                    if (addr + 4 > MemorySize)
                        return IndexAck(reply, GvcpConst.WriteRegAck, reqId, (ushort)i, GvcpConst.StatusInvalidAddress);
                    payload.AsSpan(i * 8 + 4, 4).CopyTo(Memory.AsSpan((int)addr));
                }
                if (IsAckEmptyForWrites)
                {
                    Header(reply, GvcpConst.StatusSuccess, GvcpConst.WriteRegAck, 0, reqId);
                    return 8;
                }
                return IndexAck(reply, GvcpConst.WriteRegAck, reqId, (ushort)count, GvcpConst.StatusSuccess);
            }
            case GvcpConst.ReadMemCmd:
            {
                var addr = BinaryPrimitives.ReadUInt32BigEndian(payload);
                var count = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(6));
                if (ErrorAddr is not null && addr <= ErrorAddr && ErrorAddr < addr + count)
                    return Error(reply, GvcpConst.ReadMemAck, reqId, ErrorStatus);
                if ((long)addr + count > MemorySize)
                    return Error(reply, GvcpConst.ReadMemAck, reqId, GvcpConst.StatusInvalidAddress);
                // 요청 길이와 어긋나게 답하는 장치 흉내 — 늘리면 뒤에 표식 바이트를 붙이고, 줄이면 앞에서 그만큼만 보낸다.
                var sent = Math.Max(0, Math.Min(count + ReadMemLengthDelta, reply.Length - 12));
                Header(reply, GvcpConst.StatusSuccess, GvcpConst.ReadMemAck, (ushort)(4 + sent), reqId);
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8), addr);
                Memory.AsSpan((int)addr, Math.Min(count, sent)).CopyTo(reply.AsSpan(12));
                if (sent > count) reply.AsSpan(12 + count, sent - count).Fill(0xEE);
                return 12 + sent;
            }
            case GvcpConst.WriteMemCmd:
            {
                var addr = BinaryPrimitives.ReadUInt32BigEndian(payload);
                var data = payload.AsSpan(4);
                if (ErrorAddr is not null && addr <= ErrorAddr && ErrorAddr < addr + data.Length)
                    return IndexAck(reply, GvcpConst.WriteMemAck, reqId, 0, ErrorStatus);
                if ((long)addr + data.Length > MemorySize)
                    return IndexAck(reply, GvcpConst.WriteMemAck, reqId, 0, GvcpConst.StatusInvalidAddress);
                data.CopyTo(Memory.AsSpan((int)addr));
                return IndexAck(reply, GvcpConst.WriteMemAck, reqId, (ushort)data.Length, GvcpConst.StatusSuccess);
            }
            case GvcpConst.ForceIpCmd:
                Header(reply, GvcpConst.StatusSuccess, GvcpConst.ForceIpAck, 0, reqId);
                return 8;
            default:
                return Error(reply, (ushort)(command + 1), reqId, GvcpConst.StatusNotImplemented);
        }
    }

    private static void Header(byte[] reply, ushort status, ushort ack, ushort length, ushort reqId)
    {
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(0), status);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), ack);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), length);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), reqId);
    }

    private static int Error(byte[] reply, ushort ack, ushort reqId, ushort status)
    {
        Header(reply, status, ack, 0, reqId);
        return 8;
    }

    private static int IndexAck(byte[] reply, ushort ack, ushort reqId, ushort index, ushort status)
    {
        Header(reply, status, ack, 4, reqId);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10), index);
        return 12;
    }

    private static bool DecrementIfPositive(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current) return true;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _socket.Dispose();
        _thread.Join(2000);
    }
}
