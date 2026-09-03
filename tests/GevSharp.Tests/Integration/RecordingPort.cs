using System.Buffers.Binary;

namespace GevSharp.Tests.Integration;

/// <summary>레지스터 접근 한 건. 4바이트 접근은 값도 남기고, 다른 길이는 값 0 으로 둔다.</summary>
internal readonly record struct PortAccess(bool IsWrite, uint Addr, uint Value, int Length)
{
    public override string ToString() => $"{(IsWrite ? "W" : "R")} 0x{Addr:X4} = 0x{Value:X8} ({Length} B)";
}

/// <summary>
/// 장치의 <see cref="IGevPort"/> 를 감싸 접근 순서를 기록한다. 스트림이 채널 레지스터를 어떤 순서로 만지는지(SCDA → SCP → SCPS 읽기 →
/// 파이어테스트 → 최종 SCPS → SCPD, 정지 시 SCP = 0 → SCDA = 0)는 시뮬레이터의 최종 레지스터 값만으로는 알 수 없어서 둔다.
/// 기록은 실제 접근이 성공한 뒤(읽기) 또는 보내기 직전(쓰기)에 남긴다.
/// </summary>
internal sealed class RecordingPort : IGevPort
{
    private readonly IGevPort _inner;
    private readonly List<PortAccess> _log = new();

    public RecordingPort(IGevPort inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>지금까지의 접근 기록 스냅숏(시간순).</summary>
    public IReadOnlyList<PortAccess> Log
    {
        get { lock (_log) return _log.ToArray(); }
    }

    public async ValueTask ReadAsync(ulong address, Memory<byte> buffer, CancellationToken ct = default)
    {
        await _inner.ReadAsync(address, buffer, ct);
        var value = buffer.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(buffer.Span) : 0u;
        lock (_log) _log.Add(new PortAccess(false, (uint)address, value, buffer.Length));
    }

    public async ValueTask WriteAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var value = data.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(data.Span) : 0u;
        lock (_log) _log.Add(new PortAccess(true, (uint)address, value, data.Length));
        await _inner.WriteAsync(address, data, ct);
    }
}
