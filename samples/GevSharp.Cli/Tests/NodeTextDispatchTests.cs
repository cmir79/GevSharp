using GevSharp.Cli.Commands;
using GevSharp.GenApi;

namespace GevSharp.Tests.Cli;

/// <summary>
/// 노드 종류 분기 — 런타임 노드 클래스가 인터페이스를 여럿 겸해도(정수 값을 노출하는 열거, 레지스터 기반 정수) 더 구체적인 쪽으로 다뤄야 한다.
/// 그렇지 않으면 PixelFormat 이 숫자로 찍히고 심볼로 쓰는 set 이 막힌다.
/// </summary>
public class NodeTextDispatchTests
{
    [Fact]
    public async Task EnumerationThatAlsoExposesAnIntegerIsReadAndWrittenAsAnEnumeration()
    {
        var node = new IntegerBackedEnumeration();

        Assert.Equal("Mono8", await NodeText.ReadValueAsync(node, CancellationToken.None));
        Assert.Equal("Mono8", await NodeText.ReadValueOrErrorAsync(node, AccessMode.ReadWrite, CancellationToken.None));

        await NodeText.WriteValueAsync(node, "Mono12", CancellationToken.None);
        Assert.Equal("Mono12", node.LastSymbolic);
        Assert.Null(node.LastInt);

        await NodeText.WriteValueAsync(node, "0x2", CancellationToken.None);
        Assert.Equal(2, node.LastInt);

        var ex = await Assert.ThrowsAsync<CliUsageException>(() => NodeText.WriteValueAsync(node, "Mono99", CancellationToken.None));
        Assert.Contains("entries: Mono8, Mono12", ex.Message);

        var lines = await NodeText.DescribeAsync(node, CancellationToken.None);
        Assert.Contains(lines, l => l.StartsWith("entry: Mono8 = 1", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("min:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IntegerThatAlsoExposesItsRegisterIsReadAsAnInteger()
    {
        var node = new RegisterBackedInteger();

        Assert.Equal("0x2A", await NodeText.ReadValueAsync(node, CancellationToken.None));
        await NodeText.WriteValueAsync(node, "43", CancellationToken.None);
        Assert.Equal(43, node.LastValue);
        Assert.Null(node.LastBytes);

        var lines = await NodeText.DescribeAsync(node, CancellationToken.None);
        Assert.Contains(lines, l => l.StartsWith("min: 0x0, max: 0xFF", StringComparison.Ordinal));
    }

    private abstract class FakeNode : INode
    {
        public abstract string Name { get; }
        public abstract NodeKind Kind { get; }
        public string? DisplayName => null;
        public string? Description => null;
        public string? ToolTip => null;
        public Visibility Visibility => Visibility.Beginner;
        public bool IsStreamable => false;
        public ValueTask<bool> IsImplementedAsync(CancellationToken ct = default) => new(true);
        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default) => new(true);
        public ValueTask<bool> IsLockedAsync(CancellationToken ct = default) => new(false);
        public ValueTask<AccessMode> GetAccessModeAsync(CancellationToken ct = default) => new(AccessMode.ReadWrite);
        public void Invalidate() { }
    }

    private sealed class FakeEntry : FakeNode, IEnumEntry
    {
        public FakeEntry(string symbolic, long value)
        {
            Symbolic = symbolic;
            Value = value;
        }

        public override string Name => "EnumEntry_PixelFormat_" + Symbolic;
        public override NodeKind Kind => NodeKind.EnumEntry;
        public string Symbolic { get; }
        public long Value { get; }
        public double? NumericValue => null;
    }

    /// <summary>정수 값도 노출하는 열거 노드.</summary>
    private sealed class IntegerBackedEnumeration : FakeNode, IEnumeration, IInteger
    {
        private readonly FakeEntry[] _entries = { new("Mono8", 1), new("Mono12", 2) };

        public string? LastSymbolic { get; private set; }
        public long? LastInt { get; private set; }

        public override string Name => "PixelFormat";
        public override NodeKind Kind => NodeKind.Enumeration;

        ValueTask<string> IEnumeration.GetAsync(CancellationToken ct) => new("Mono8");
        public ValueTask SetAsync(string symbolic, CancellationToken ct = default)
        {
            LastSymbolic = symbolic;
            return default;
        }
        public ValueTask<long> GetIntValueAsync(CancellationToken ct = default) => new(1);
        public ValueTask SetIntValueAsync(long value, CancellationToken ct = default)
        {
            LastInt = value;
            return default;
        }
        public IReadOnlyList<IEnumEntry> Entries => _entries;
        public ValueTask<IReadOnlyList<IEnumEntry>> GetAvailableEntriesAsync(CancellationToken ct = default) => new(Entries);
        public IEnumEntry? GetEntry(string symbolic) => _entries.FirstOrDefault(e => e.Symbolic == symbolic);

        ValueTask<long> IInteger.GetAsync(CancellationToken ct) => new(1);
        public ValueTask SetAsync(long value, CancellationToken ct = default)
        {
            LastInt = value;
            return default;
        }
        public ValueTask<long> GetMinAsync(CancellationToken ct = default) => new(1);
        public ValueTask<long> GetMaxAsync(CancellationToken ct = default) => new(2);
        public ValueTask<long> GetIncAsync(CancellationToken ct = default) => new(1);
        public Representation Representation => Representation.PureNumber;
        public string? Unit => null;
    }

    /// <summary>자기 레지스터도 노출하는 정수 노드.</summary>
    private sealed class RegisterBackedInteger : FakeNode, IInteger, IRegister
    {
        public long? LastValue { get; private set; }
        public byte[]? LastBytes { get; private set; }

        public override string Name => "Gain";
        public override NodeKind Kind => NodeKind.Integer;

        public ValueTask<long> GetAsync(CancellationToken ct = default) => new(42);
        public ValueTask SetAsync(long value, CancellationToken ct = default)
        {
            LastValue = value;
            return default;
        }
        public ValueTask<long> GetMinAsync(CancellationToken ct = default) => new(0);
        public ValueTask<long> GetMaxAsync(CancellationToken ct = default) => new(255);
        public ValueTask<long> GetIncAsync(CancellationToken ct = default) => new(1);
        public Representation Representation => Representation.HexNumber;
        public string? Unit => null;

        public ValueTask<ulong> GetAddressAsync(CancellationToken ct = default) => new(0x1000ul);
        public ValueTask<long> GetLengthAsync(CancellationToken ct = default) => new(4);
        public ValueTask GetAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            buffer.Span.Fill(0);
            return default;
        }
        public ValueTask SetAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            LastBytes = data.ToArray();
            return default;
        }
    }
}
