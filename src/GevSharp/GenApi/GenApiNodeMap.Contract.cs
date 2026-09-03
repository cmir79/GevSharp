namespace GevSharp.GenApi;

/// <summary>RegisterDescription 루트 속성.</summary>
public sealed record RegisterDescriptionInfo(
    string ModelName,
    string VendorName,
    string? ToolTip,
    string? StandardNameSpace,
    int SchemaMajorVersion,
    int SchemaMinorVersion,
    int SchemaSubMinorVersion,
    int MajorVersion,
    int MinorVersion,
    int SubMinorVersion,
    string? ProductGuid,
    string? VersionGuid);

/// <summary>
/// 노드맵 공개 면 — 구현은 GenApi 런타임 모듈(NodeMap 파티션)에 있다.
/// 이름 조회는 XML 의 Name 속성 그대로(대소문자 구분). 표준 피처 이름은 SFNC 를 따른다(예: "ExposureTime").
/// </summary>
public partial class GenApiNodeMap
{
    public RegisterDescriptionInfo Info => _info;
    public ICategory Root => _root;
    public IReadOnlyList<INode> Nodes => _nodes;

    /// <summary>이름으로 노드를 찾는다. 없으면 null.</summary>
    public INode? GetNode(string name) => _byName.TryGetValue(name, out var n) ? n : null;

    /// <summary>이름으로 노드를 찾는다. 없거나 형이 다르면 <see cref="GenApiException"/>.</summary>
    public T GetNode<T>(string name) where T : class, INode
    {
        var n = GetNode(name) ?? throw new GenApiException($"Node '{name}' not found.", name);
        return n as T ?? throw new GenApiException($"Node '{name}' is {n.Kind}, not {typeof(T).Name}.", name);
    }

    public IInteger GetInteger(string name) => GetNode<IInteger>(name);
    public IFloat GetFloat(string name) => GetNode<IFloat>(name);
    public IString GetString(string name) => GetNode<IString>(name);
    public IBoolean GetBoolean(string name) => GetNode<IBoolean>(name);
    public IEnumeration GetEnumeration(string name) => GetNode<IEnumeration>(name);
    public ICommand GetCommand(string name) => GetNode<ICommand>(name);
    public IRegister GetRegister(string name) => GetNode<IRegister>(name);
    public ICategory GetCategory(string name) => GetNode<ICategory>(name);

    /// <summary>모든 노드의 캐시를 버린다 — 외부에서 레지스터를 직접 건드린 뒤에 부른다.</summary>
    public void InvalidateAll()
    {
        foreach (var n in _nodes) n.Invalidate();
    }

    // 런타임 모듈이 채우는 필드 — 생성자는 그쪽 파티션에 있다.
    private readonly RegisterDescriptionInfo _info = null!;
    private readonly ICategory _root = null!;
    private readonly IReadOnlyList<INode> _nodes = Array.Empty<INode>();
    private readonly Dictionary<string, INode> _byName = new(StringComparer.Ordinal);
}
