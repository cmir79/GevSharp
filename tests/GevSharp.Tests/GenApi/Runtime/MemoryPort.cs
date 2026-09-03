using System.Buffers.Binary;
using System.Text;

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>
/// 테스트용 레지스터 공간 — 희소 바이트 메모리 위의 <see cref="IGevPort"/>. 모든 읽기·쓰기를 주소·길이와 함께 기록하므로
/// 캐시 정책(읽기 횟수)과 쓰기 바이트를 그대로 검증할 수 있다. 읽지 않은 주소는 0 이다.
/// <see cref="AfterWrite"/> 로 장치 쪽 부작용(자기 소거 비트 등)을 흉내 낸다.
/// </summary>
internal sealed class MemoryPort : IGevPort
{
    private readonly Dictionary<ulong, byte> _mem = new();
    private readonly object _lock = new();

    public sealed record Access(ulong Address, byte[] Data);

    public List<Access> Reads { get; } = new();
    public List<Access> Writes { get; } = new();

    public int ReadCount => Reads.Count;
    public int WriteCount => Writes.Count;

    /// <summary>쓰기가 메모리에 반영된 직후 불린다 — 장치 부작용을 흉내 낼 때.</summary>
    public Action<ulong, byte[]>? AfterWrite { get; set; }

    /// <summary>읽기 직전에 불린다 — 장치가 스스로 바꾸는 레지스터(폴링 대상)를 흉내 낼 때.</summary>
    public Action<ulong, int>? BeforeRead { get; set; }

    /// <summary>주소 범위에 대한 읽기 횟수.</summary>
    public int ReadsAt(ulong address)
    {
        var n = 0;
        lock (_lock)
        {
            foreach (var r in Reads)
            {
                if (address >= r.Address && address < r.Address + (ulong)r.Data.Length) n++;
            }
        }
        return n;
    }

    public int WritesAt(ulong address)
    {
        var n = 0;
        lock (_lock)
        {
            foreach (var w in Writes)
            {
                if (address >= w.Address && address < w.Address + (ulong)w.Data.Length) n++;
            }
        }
        return n;
    }

    public void ClearLog()
    {
        lock (_lock)
        {
            Reads.Clear();
            Writes.Clear();
        }
    }

    public ValueTask ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct = default)
    {
        BeforeRead?.Invoke(address, buffer.Length);
        var data = new byte[buffer.Length];
        lock (_lock)
        {
            for (var i = 0; i < data.Length; i++)
                data[i] = _mem.TryGetValue(address + (ulong)i, out var b) ? b : (byte)0;
            Reads.Add(new Access(address, (byte[])data.Clone()));
        }
        data.AsMemory().CopyTo(buffer);
        return default;
    }

    public ValueTask WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var copy = data.ToArray();
        lock (_lock)
        {
            for (var i = 0; i < copy.Length; i++) _mem[address + (ulong)i] = copy[i];
            Writes.Add(new Access(address, (byte[])copy.Clone()));
        }
        AfterWrite?.Invoke(address, copy);
        return default;
    }

    // ---- 직접 접근(기록에 남지 않는다) ----

    public byte[] Peek(ulong address, int length)
    {
        var data = new byte[length];
        lock (_lock)
        {
            for (var i = 0; i < length; i++)
                data[i] = _mem.TryGetValue(address + (ulong)i, out var b) ? b : (byte)0;
        }
        return data;
    }

    public void Poke(ulong address, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            for (var i = 0; i < data.Length; i++) _mem[address + (ulong)i] = data[i];
        }
    }

    public uint U32(ulong address) => BinaryPrimitives.ReadUInt32BigEndian(Peek(address, 4));

    public void U32(ulong address, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        Poke(address, b);
    }

    public uint U32Le(ulong address) => BinaryPrimitives.ReadUInt32LittleEndian(Peek(address, 4));

    public void U32Le(ulong address, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        Poke(address, b);
    }

    public ulong U64(ulong address) => BinaryPrimitives.ReadUInt64BigEndian(Peek(address, 8));

    public void U64(ulong address, ulong value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        Poke(address, b);
    }

    public void F32(ulong address, float value) => U32(address, (uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
    public float F32(ulong address) => BitConverter.ToSingle(BitConverter.GetBytes((int)U32(address)), 0);
    public void F64(ulong address, double value) => U64(address, (ulong)BitConverter.DoubleToInt64Bits(value));
    public double F64(ulong address) => BitConverter.Int64BitsToDouble((long)U64(address));

    public void Str(ulong address, int length, string value)
    {
        var buf = new byte[length];
        Encoding.UTF8.GetBytes(value, 0, value.Length, buf, 0);
        Poke(address, buf);
    }

    public string Str(ulong address, int length)
    {
        var b = Peek(address, length);
        var n = Array.IndexOf(b, (byte)0);
        return Encoding.UTF8.GetString(b, 0, n < 0 ? length : n);
    }
}
