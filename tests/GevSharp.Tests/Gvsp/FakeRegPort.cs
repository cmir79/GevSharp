using System.Buffers.Binary;

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 레지스터 쓰기를 순서대로 기록하는 메모리 포트. 스트림은 4 바이트 빅엔디언 값만 쓰므로 그 형태만 받는다.
/// 읽기는 <see cref="Set"/> 으로 미리 넣어 둔 값(또는 마지막 쓰기 값)을 돌려주고, 모르는 주소는 0 이다.
/// <see cref="OnWrite"/> 훅으로 장치 반응(파이어테스트 패킷 송신 등)을 흉내 낸다 — 훅은 쓰기 호출 스레드에서 동기로 불린다.
/// </summary>
internal sealed class FakeRegPort : IGevPort
{
    private readonly object _lock = new();
    private readonly List<(uint Addr, uint Value)> _writes = new();
    private readonly Dictionary<uint, uint> _regs = new();

    /// <summary>쓰기마다 (주소, 값) 으로 불린다. 여기서 예외를 던지면 쓰기 실패를 흉내 낸다.</summary>
    public Action<uint, uint>? OnWrite { get; set; }

    /// <summary>읽기에 답할 레지스터 값을 미리 넣는다 — 장치가 켜 둔 SCPS 플래그 같은 초기 상태.</summary>
    public void Set(uint addr, uint value)
    {
        lock (_lock) _regs[addr] = value;
    }

    /// <summary>지금까지의 쓰기 (주소, 값) 사본 — 호출 순서 그대로.</summary>
    public (uint Addr, uint Value)[] Writes
    {
        get { lock (_lock) return _writes.ToArray(); }
    }

    public uint? LastValue(uint addr)
    {
        lock (_lock)
        {
            for (var i = _writes.Count - 1; i >= 0; i--)
            {
                if (_writes[i].Addr == addr) return _writes[i].Value;
            }
        }
        return null;
    }

    public ValueTask ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        buffer.Span.Clear();
        if (buffer.Length == 4 && address <= uint.MaxValue)
        {
            bool found;
            uint value;
            lock (_lock) found = _regs.TryGetValue((uint)address, out value);
            if (found) BinaryPrimitives.WriteUInt32BigEndian(buffer.Span, value);
        }
        return default;
    }

    public ValueTask WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (data.Length != 4) throw new GevException($"Stream register writes must be 4 bytes, got {data.Length}.");
        if (address > uint.MaxValue) throw new GevException($"Address 0x{address:X} exceeds the 32-bit GVCP space.");

        var value = BinaryPrimitives.ReadUInt32BigEndian(data.Span);
        lock (_lock)
        {
            _writes.Add(((uint)address, value));
            _regs[(uint)address] = value;
        }
        OnWrite?.Invoke((uint)address, value);
        return default;
    }
}
