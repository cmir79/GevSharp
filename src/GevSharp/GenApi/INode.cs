namespace GevSharp.GenApi;

/// <summary>GenApi 노드 인터페이스 종류 — 소비자가 형 검사 없이 분기할 때 쓴다.</summary>
public enum NodeKind
{
    Category,
    Integer,
    Float,
    String,
    Boolean,
    Enumeration,
    EnumEntry,
    Command,
    Register,
    Port,
    Unknown,
}

/// <summary>GenApi 접근 모드(가용성·구현·잠금 술어를 합친 결과).</summary>
public enum AccessMode
{
    /// <summary>pIsImplemented 가 거짓 — 이 장치에 없다.</summary>
    NotImplemented,
    /// <summary>pIsAvailable 이 거짓 — 지금 상태에서 쓸 수 없다.</summary>
    NotAvailable,
    WriteOnly,
    ReadOnly,
    ReadWrite,
}

public enum Visibility
{
    Beginner,
    Expert,
    Guru,
    Invisible,
}

public enum Representation
{
    Linear,
    Logarithmic,
    Boolean,
    PureNumber,
    HexNumber,
    IPV4Address,
    MACAddress,
}

/// <summary>
/// 모든 GenApi 노드의 공통 면. 값 접근은 하위 인터페이스(<see cref="IInteger"/> 등)로.
/// 모든 값 접근은 비동기다 — 레지스터 한 번이 곧 UDP 왕복이다.
/// </summary>
public interface INode
{
    string Name { get; }
    NodeKind Kind { get; }
    string? DisplayName { get; }
    string? Description { get; }
    string? ToolTip { get; }
    Visibility Visibility { get; }
    /// <summary>XML 의 Streamable 속성 — 레시피 저장 대상 피처인지.</summary>
    bool IsStreamable { get; }

    ValueTask<bool> IsImplementedAsync(CancellationToken ct = default);
    ValueTask<bool> IsAvailableAsync(CancellationToken ct = default);
    ValueTask<bool> IsLockedAsync(CancellationToken ct = default);
    ValueTask<AccessMode> GetAccessModeAsync(CancellationToken ct = default);

    /// <summary>이 노드와 이 노드에 의존하는 노드들의 캐시를 버린다.</summary>
    void Invalidate();
}

public interface IInteger : INode
{
    ValueTask<long> GetAsync(CancellationToken ct = default);
    ValueTask SetAsync(long value, CancellationToken ct = default);
    ValueTask<long> GetMinAsync(CancellationToken ct = default);
    ValueTask<long> GetMaxAsync(CancellationToken ct = default);
    ValueTask<long> GetIncAsync(CancellationToken ct = default);
    Representation Representation { get; }
    string? Unit { get; }
}

public interface IFloat : INode
{
    ValueTask<double> GetAsync(CancellationToken ct = default);
    ValueTask SetAsync(double value, CancellationToken ct = default);
    ValueTask<double> GetMinAsync(CancellationToken ct = default);
    ValueTask<double> GetMaxAsync(CancellationToken ct = default);
    /// <summary>Inc 가 정의되지 않았으면 null.</summary>
    ValueTask<double?> GetIncAsync(CancellationToken ct = default);
    Representation Representation { get; }
    string? Unit { get; }

    /// <summary>표시 표기법 — XML 에 없으면 Automatic.</summary>
    Model.DisplayNotation DisplayNotation { get; }

    /// <summary>표시 자릿수 — XML 에 없으면 null.</summary>
    int? DisplayPrecision { get; }
}

public interface IString : INode
{
    ValueTask<string> GetAsync(CancellationToken ct = default);
    ValueTask SetAsync(string value, CancellationToken ct = default);
    ValueTask<long> GetMaxLengthAsync(CancellationToken ct = default);
}

public interface IBoolean : INode
{
    ValueTask<bool> GetAsync(CancellationToken ct = default);
    ValueTask SetAsync(bool value, CancellationToken ct = default);
}

public interface IEnumEntry : INode
{
    /// <summary>XML 의 Symbolic — 사용자에게 보이는 이름. Name 은 노드 이름(보통 EnumEntry_피처_심볼).</summary>
    string Symbolic { get; }
    long Value { get; }
    /// <summary>NumericValue — 없으면 null. 물리량이 있는 열거(예: 프레임레이트 프리셋)에 쓰인다.</summary>
    double? NumericValue { get; }
}

public interface IEnumeration : INode
{
    /// <summary>현재 값의 Symbolic. 레지스터 값이 어느 엔트리에도 안 맞으면 <see cref="GenApiException"/>.</summary>
    ValueTask<string> GetAsync(CancellationToken ct = default);
    ValueTask SetAsync(string symbolic, CancellationToken ct = default);
    ValueTask<long> GetIntValueAsync(CancellationToken ct = default);
    ValueTask SetIntValueAsync(long value, CancellationToken ct = default);
    /// <summary>XML 에 선언된 전체 엔트리(구현·가용 여부 무관).</summary>
    IReadOnlyList<IEnumEntry> Entries { get; }
    /// <summary>지금 구현·가용한 엔트리만.</summary>
    ValueTask<IReadOnlyList<IEnumEntry>> GetAvailableEntriesAsync(CancellationToken ct = default);
    IEnumEntry? GetEntry(string symbolic);
}

public interface ICommand : INode
{
    ValueTask ExecuteAsync(CancellationToken ct = default);
    /// <summary>실행이 끝났는지(레지스터가 CommandValue 에서 돌아왔는지). 폴링 정보가 없으면 항상 true.</summary>
    ValueTask<bool> IsDoneAsync(CancellationToken ct = default);
}

public interface IRegister : INode
{
    ValueTask<ulong> GetAddressAsync(CancellationToken ct = default);
    ValueTask<long> GetLengthAsync(CancellationToken ct = default);
    ValueTask GetAsync(Memory<byte> buffer, CancellationToken ct = default);
    ValueTask SetAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);
}

public interface ICategory : INode
{
    /// <summary>pFeature 순서 그대로. 구현되지 않은 노드도 포함된다 — 걸러 쓰려면 <see cref="INode.IsImplementedAsync"/>.</summary>
    IReadOnlyList<INode> Features { get; }
}

/// <summary>Port 노드 — 레지스터 노드가 붙는 전송 경계. 실제 I/O 는 <see cref="IGevPort"/> 로 나간다.</summary>
public interface IPortNode : INode
{
    IGevPort Port { get; }
}
