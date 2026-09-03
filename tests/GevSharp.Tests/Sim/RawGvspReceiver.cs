using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace GevSharp.Tests.Simulator;

/// <summary>수신한 GVSP 패킷 하나를 테스트 쪽 규칙으로 해석한 것. <see cref="Data"/> 는 헤더 뒤의 바이트.</summary>
internal sealed record RawGvspPacket(ushort Status, ulong BlockId, byte ContentType, uint PacketId, bool IsExtended, byte[] Data, int TotalLength)
{
    public const byte Leader = 1, Trailer = 2, Payload = 3;

    public uint DataU32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset, 4));
    public ushort DataU16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset, 2));
    public ulong DataU64(int offset) => BinaryPrimitives.ReadUInt64BigEndian(Data.AsSpan(offset, 8));

    /// <summary>바이트 배열에서 8/20바이트 헤더를 해석한다. 헤더보다 짧으면 null.</summary>
    public static RawGvspPacket? Parse(byte[] buf, int n)
    {
        if (n < 8) return null;
        ushort status = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0));
        uint infos = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4));
        bool extended = (infos & 0x8000_0000) != 0;
        byte content = (byte)((infos >> 24) & 0x7F);
        ulong block;
        uint pid;
        int header;
        if (!extended)
        {
            block = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2));
            pid = infos & 0x00FF_FFFF;
            header = 8;
        }
        else
        {
            if (n < 20) return null;
            block = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(8));
            pid = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16));
            header = 20;
        }
        var data = new byte[n - header];
        Buffer.BlockCopy(buf, header, data, 0, data.Length);
        return new RawGvspPacket(status, block, content, pid, extended, data, n);
    }
}

/// <summary>한 프레임의 패킷을 모은 것 — 리더·페이로드(패킷 id 순)·트레일러.</summary>
internal sealed class RawGvspFrame
{
    public ulong BlockId;
    public RawGvspPacket? Leader;
    public RawGvspPacket? Trailer;
    public SortedDictionary<uint, RawGvspPacket> Payloads = new();

    public bool IsComplete => Leader is not null && Trailer is not null && Payloads.Count == (int)Trailer.PacketId - 1;

    /// <summary>페이로드를 (id − 1) × dataBytes 오프셋 규칙으로 잇는다(dataBytes = 첫 패킷 길이).</summary>
    public byte[] Assemble()
    {
        if (Payloads.Count == 0) return Array.Empty<byte>();
        int dataBytes = Payloads.Values.First().Data.Length;
        int total = Payloads.Sum(p => p.Value.Data.Length);
        var img = new byte[total];
        foreach (var (id, p) in Payloads) Buffer.BlockCopy(p.Data, 0, img, (int)(id - 1) * dataBytes, p.Data.Length);
        return img;
    }
}

/// <summary>스트림 채널 목적지 역할의 UDP 소켓. SCDA:SCP 에 쓸 주소·포트를 내주고 패킷을 해석해 돌려준다.</summary>
internal sealed class RawGvspReceiver : IDisposable
{
    private readonly Socket _sock;
    private readonly byte[] _buf = new byte[65536];

    public RawGvspReceiver()
    {
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sock.ReceiveBufferSize = 4 << 20;
        _sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_sock.LocalEndPoint!).Port;
    }

    public int Port { get; }
    public uint AddressU32 => 0x7F00_0001;   // 127.0.0.1

    /// <summary>데이터그램 하나(원시 바이트). 시간 안에 없으면 null.</summary>
    public byte[]? ReceiveRaw(int timeoutMs)
    {
        try
        {
            if (!_sock.Poll(timeoutMs * 1000, SelectMode.SelectRead)) return null;
            int n = _sock.Receive(_buf);
            var copy = new byte[n];
            Buffer.BlockCopy(_buf, 0, copy, 0, n);
            return copy;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return null;
        }
    }

    public RawGvspPacket? Receive(int timeoutMs)
    {
        var raw = ReceiveRaw(timeoutMs);
        return raw is null ? null : RawGvspPacket.Parse(raw, raw.Length);
    }

    /// <summary>
    /// 정상 status(0x0000) 패킷을 프레임별로 모은다. 트레일러가 count 개 도착하거나 idleTimeoutMs 동안 아무것도 오지 않으면 끝난다.
    /// status 가 0 이 아닌 패킷(리센드·오류)은 <paramref name="others"/> 로 따로 모은다.
    /// </summary>
    public Dictionary<ulong, RawGvspFrame> CollectFrames(int count, int idleTimeoutMs, List<RawGvspPacket>? others = null)
    {
        var frames = new Dictionary<ulong, RawGvspFrame>();
        int trailers = 0;
        while (trailers < count)
        {
            var p = Receive(idleTimeoutMs);
            if (p is null) break;
            if (p.Status != 0)
            {
                others?.Add(p);
                continue;
            }
            if (!frames.TryGetValue(p.BlockId, out var f)) frames[p.BlockId] = f = new RawGvspFrame { BlockId = p.BlockId };
            switch (p.ContentType)
            {
                case RawGvspPacket.Leader: f.Leader = p; break;
                case RawGvspPacket.Trailer: f.Trailer = p; trailers++; break;
                case RawGvspPacket.Payload: f.Payloads[p.PacketId] = p; break;
            }
        }
        return frames;
    }

    /// <summary>주어진 시간 동안 도착하는 패킷을 전부 읽어 버린다. 읽은 수를 돌려준다.</summary>
    public int Drain(int timeoutMs)
    {
        int n = 0;
        while (ReceiveRaw(timeoutMs) is not null) n++;
        return n;
    }

    public void Dispose() => _sock.Dispose();
}
