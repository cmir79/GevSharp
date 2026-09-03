using System.Runtime.CompilerServices;
using GevSharp.GenApi.Model;

namespace GevSharp.GenApi.Runtime;

/// <summary>
/// 모델의 정의를 런타임 노드로 만들고 p* 이름 참조를 노드로 잇는다. 순서:
/// 1) 문서 순서대로 모든 노드 객체를 만든다(앞뒤 어디를 가리키든 해석되게 전부 먼저 만든다),
/// 2) 노드마다 Bind 로 참조를 해석하며 간선을 기록한다(빠진 이름·틀린 종류는 참조하는 노드 이름을 담은 <see cref="GenApiException"/>),
/// 3) 간선에서 역방향 색인(의존 노드·pInvalidator 청취자·pSelected)을 만들고,
/// 4) 순환을 찾는다(반복형 DFS — 큰 XML 에서도 스택을 키우지 않는다): 값 참조 간선만의 순환은 오류, 술어 간선을 지나야 닫히는 순환은 경고.
/// </summary>
internal sealed class NodeBinder
{
    private readonly GenApiNodeMap _map;
    private readonly Dictionary<string, NodeBase> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<NodeDef, NodeBase> _byDef = new(ReferenceComparer.Instance);
    private readonly List<NodeBase> _public = new();
    private readonly List<NodeBase> _private = new();
    private readonly List<Edge> _edges = new();
    private readonly List<RegisterCore> _cores = new();
    private NodeBase? _current;

    private readonly struct Edge
    {
        public Edge(NodeBase from, NodeBase to, RefKind kind)
        {
            From = from;
            To = to;
            Kind = kind;
        }

        public NodeBase From { get; }
        public NodeBase To { get; }
        public RefKind Kind { get; }
    }

    /// <summary>정의 레코드는 값 동등성이라 같은 내용의 두 정의가 겹칠 수 있다 — 사전은 참조 동일성으로 키를 잡는다.</summary>
    private sealed class ReferenceComparer : IEqualityComparer<NodeDef>
    {
        public static readonly ReferenceComparer Instance = new();
        public bool Equals(NodeDef? x, NodeDef? y) => ReferenceEquals(x, y);
        public int GetHashCode(NodeDef obj) => RuntimeHelpers.GetHashCode(obj);
    }

    public static readonly Func<NodeBase, bool> Any = _ => true;

    /// <summary>수치로 읽고 쓸 수 있는 노드 — 정수·실수 계열, Boolean(1/0), Enumeration(항목 값).</summary>
    public static readonly Func<NodeBase, bool> Numeric = n => n is IntegerNodeBase || n is FloatNodeBase || n is BooleanNode || n is EnumerationNode;

    public static readonly Func<NodeBase, bool> Stringy = n => n is IString;

    public NodeBinder(GenApiNodeMap map, GenApiXmlModel model)
    {
        _map = map;
        foreach (var def in model.NodeList)
        {
            var node = Create(def, map);
            _byName.Add(def.Name, node);
            _byDef.Add(def, node);
            _public.Add(node);
        }
    }

    /// <summary>모델의 노드 목록 순서 그대로(EnumEntry 와 이름 있는 인라인 IntSwissKnife 포함).</summary>
    public IReadOnlyList<NodeBase> PublicNodes => _public;

    /// <summary>레지스터 노드가 가진 모든 레지스터 부품 — 겹치는 캐시 무효화에 쓴다.</summary>
    public List<RegisterCore> Cores => _cores;

    public void BindAll()
    {
        for (var i = 0; i < _public.Count; i++) BindNode(_public[i]);
        BuildReverseIndex();
        DetectCycles(includeGuards: false);
        DetectCycles(includeGuards: true);
    }

    public void BindNode(NodeBase node)
    {
        var prev = _current;
        _current = node;
        try
        {
            node.Bind(this);
        }
        finally
        {
            _current = prev;
        }
    }

    private NodeBase Current => _current ?? throw new InvalidOperationException("No node is being bound.");

    /// <summary>이름을 노드로 해석하고 간선을 기록한다. 없거나 accept 를 통과하지 못하면 참조하는 노드 이름을 담아 던진다.</summary>
    public NodeBase Resolve(string name, RefKind kind, string what, Func<NodeBase, bool> accept)
    {
        var from = Current;
        if (!_byName.TryGetValue(name, out var to))
            throw new GenApiException($"Node '{from.Name}' references missing node '{name}' in {what}.", from.Name);
        if (!accept(to))
            throw new GenApiException($"Node '{from.Name}' references node '{name}' in {what}, but a {to.Def.Kind} node cannot be used there.", from.Name);
        Link(from, to, kind);
        return to;
    }

    public T Resolve<T>(string name, RefKind kind, string what) where T : NodeBase
        => (T)Resolve(name, kind, what, n => n is T);

    public NodeBase? ResolveOptional(string? name, RefKind kind, string what, Func<NodeBase, bool> accept)
        => name is null ? null : Resolve(name, kind, what, accept);

    public void Link(NodeBase from, NodeBase to, RefKind kind)
    {
        if (kind == RefKind.Feature || kind == RefKind.Port || kind == RefKind.Constant) return;
        _edges.Add(new Edge(from, to, kind));
    }

    /// <summary>정의 인스턴스에 대응하는 노드(EnumEntry 처럼 소유 노드가 자기 자식을 찾을 때).</summary>
    public NodeBase NodeOf(NodeDef def)
        => _byDef.TryGetValue(def, out var n) ? n : throw new GenApiException($"Node '{def.Name}' has no runtime node.", def.Name);

    /// <summary>
    /// 레지스터의 인라인 주소 IntSwissKnife. Name 이 있으면 모델에 등록된 같은 노드를 쓰고, 없으면 레지스터 전용 노드를 만든다.
    /// 소유 레지스터 → 수식 간선을 값 참조로 남겨 순환 검사와 무효화가 수식의 변수까지 이어지게 한다.
    /// </summary>
    public IntSwissKnifeNode AddressKnife(IntSwissKnifeDef def, NodeBase owner)
    {
        if (!_byDef.TryGetValue(def, out var node))
        {
            node = new IntSwissKnifeNode(def, _map);
            _byDef.Add(def, node);
            _private.Add(node);
        }
        Link(owner, node, RefKind.Value);
        BindNode(node);
        return (IntSwissKnifeNode)node;
    }

    public void AddCore(RegisterCore core) => _cores.Add(core);

    private void BuildReverseIndex()
    {
        foreach (var e in _edges)
        {
            switch (e.Kind)
            {
                case RefKind.Value:
                case RefKind.Guard:
                case RefKind.Limit:
                    AddUnique(e.To.Dependents, e.From);
                    break;
                case RefKind.Invalidator:
                    AddUnique(e.To.InvalidatorListeners, e.From);
                    break;
                case RefKind.Selected:
                    AddUnique(e.From.Selected, e.To);
                    AddUnique(e.To.Selectors, e.From);
                    break;
            }
        }
    }

    private static void AddUnique(List<NodeBase> list, NodeBase node)
    {
        foreach (var n in list)
        {
            if (ReferenceEquals(n, node)) return;
        }
        list.Add(node);
    }

    /// <summary>
    /// 순환 검사. includeGuards 가 거짓이면 값 참조 간선만으로 찾아 경로를 담은 <see cref="GenApiException"/> 을 던진다 — 평가가 끝없이 재귀할 순환이다.
    /// 참이면 술어 간선(pIsImplemented/pIsAvailable/pIsLocked)까지 넣어 찾되 경고만 남긴다: 술어는 내부 값 경로로 읽혀 자기 접근 검사로
    /// 되돌아오지 않으므로 실행은 끝나고, 그런 XML(술어가 그 노드의 값을 읽는 형태)로도 장치를 쓸 수 있어야 한다. 값 순환은 앞선 호출이
    /// 이미 던졌으므로 여기서 나오는 순환은 전부 술어 간선을 지난다.
    /// pInvalidator·pSelected 는 평가가 따라가지 않는 간선이라 순환이어도 무방하므로 뺀다(셀렉터가 서로를 pSelected 하는 XML 은 실제로 있다).
    /// </summary>
    private void DetectCycles(bool includeGuards)
    {
        var adjacency = new Dictionary<NodeBase, List<NodeBase>>(NodeReferenceComparer.Instance);
        foreach (var e in _edges)
        {
            if (e.Kind != RefKind.Value && (e.Kind != RefKind.Guard || !includeGuards)) continue;
            if (!adjacency.TryGetValue(e.From, out var list))
            {
                list = new List<NodeBase>();
                adjacency.Add(e.From, list);
            }
            list.Add(e.To);
        }

        var state = new Dictionary<NodeBase, int>(NodeReferenceComparer.Instance);   // 1 = 방문 중, 2 = 끝남
        var empty = new List<NodeBase>();
        var path = new List<NodeBase>();
        var stack = new Stack<(NodeBase Node, int Next)>();

        void Visit(NodeBase start)
        {
            if (state.ContainsKey(start)) return;
            state[start] = 1;
            path.Add(start);
            stack.Push((start, 0));
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var adj = adjacency.TryGetValue(node, out var l) ? l : empty;
                if (next < adj.Count)
                {
                    stack.Push((node, next + 1));
                    var m = adj[next];
                    if (!state.TryGetValue(m, out var s))
                    {
                        state[m] = 1;
                        path.Add(m);
                        stack.Push((m, 0));
                    }
                    else if (s == 1)
                    {
                        var startIndex = path.IndexOf(m);
                        var names = new List<string>();
                        for (var i = startIndex; i < path.Count; i++) names.Add(path[i].Name);
                        names.Add(m.Name);
                        var cycle = string.Join(" -> ", names);
                        if (!includeGuards)
                            throw new GenApiException($"Reference cycle in the GenApi XML: {cycle}.", m.Name);
                        GevLog.Warn("GenApi.Runtime", $"Reference cycle through a guard (pIsImplemented/pIsAvailable/pIsLocked) in the GenApi XML: {cycle}. The map is bound; guards are evaluated without recursion.");
                    }
                }
                else
                {
                    state[node] = 2;
                    path.RemoveAt(path.Count - 1);
                }
            }
        }

        foreach (var n in _public) Visit(n);
        foreach (var n in _private) Visit(n);
    }

    private static NodeBase Create(NodeDef def, GenApiNodeMap map) => def switch
    {
        CategoryDef d => new CategoryNode(d, map),
        IntegerDef d => new IntegerNode(d, map),
        IntRegDef d => new IntRegNode(d, map),
        MaskedIntRegDef d => new MaskedIntRegNode(d, map),
        IntSwissKnifeDef d => new IntSwissKnifeNode(d, map),
        IntConverterDef d => new IntConverterNode(d, map),
        FloatDef d => new FloatNode(d, map),
        FloatRegDef d => new FloatRegNode(d, map),
        SwissKnifeDef d => new SwissKnifeNode(d, map),
        ConverterDef d => new ConverterNode(d, map),
        StringDef d => new StringNode(d, map),
        StringRegDef d => new StringRegNode(d, map),
        BooleanDef d => new BooleanNode(d, map),
        EnumerationDef d => new EnumerationNode(d, map),
        EnumEntryDef d => new EnumEntryNode(d, map),
        CommandDef d => new CommandNode(d, map),
        RegisterDef d => new RegisterNode(d, map),
        PortDef d => new PortNode(d, map),
        _ => new GenericNode(def, map),
    };
}
