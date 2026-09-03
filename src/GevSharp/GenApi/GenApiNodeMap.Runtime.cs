using GevSharp.GenApi.Model;
using GevSharp.GenApi.Runtime;

namespace GevSharp.GenApi;

/// <summary>
/// 노드맵 런타임 파티션 — 모델(<see cref="GenApiXmlModel"/>)의 정의를 포트에 바인딩된 노드 객체로 만들고, 쓰기 뒤의 캐시 무효화를 전파한다.
/// <para>
/// 바인딩(<see cref="NodeBinder"/>): 모든 노드를 먼저 만들고 p* 이름 참조를 잇는다(빠진 이름·틀린 종류·값 참조 순환은 <see cref="GenApiException"/>,
/// 술어 간선을 지나야 닫히는 순환은 경고), 그 간선에서 역방향 색인(의존 노드·pInvalidator 청취자·pSelected)을 만든다. 이 색인은 바인딩 뒤 바뀌지 않는다.
/// </para>
/// <para>
/// 무효화: 노드 X 가 쓰이면 X 를 p* 로 참조하는 노드, X 를 pInvalidator 로 지목한 노드, X 가 셀렉터일 때 pSelected 대상 — 그리고 그들에게서
/// 같은 규칙으로 닿는 노드 전부 — 의 캐시를 버린다(값 사슬 아래의 레지스터 캐시까지, pIndex 슬롯 전부 포함). 쓰인 레지스터 자신의 캐시는
/// Cachable 정책이 정한다(WriteThrough 는 쓴 값을 남긴다). <see cref="INode.Invalidate"/> 는 쓰기 없이 같은 전파를 하되 자기 자신도 버린다.
/// </para>
/// <para>
/// 쓰기 그림자(<see cref="WriteShadow"/>): 포트에 쓴 바이트를 주소별로 기억해 쓰기 전용 레지스터의 읽기-수정-쓰기 바탕값으로 쓴다.
/// 무효화는 그림자를 건드리지 않는다 — 쓰기 전용 레지스터의 내용을 호스트가 아는 길은 마지막 쓰기뿐이다.
/// </para>
/// 스레드: 동시 읽기는 안전하다(레지스터 캐시·그림자는 잠금 아래, 색인은 불변). 쓰기끼리는 동시에 하지 않는다 — 읽기-수정-쓰기(MaskedIntReg)와
/// pValueCopy 가 서로 끼어들 수 있으므로 호출자가 순서를 맞춘다.
/// </summary>
public partial class GenApiNodeMap
{
    private const string LogSrc = "GenApi.Runtime";

    private readonly IGevPort _port = null!;
    private readonly RegisterCore[] _cores = Array.Empty<RegisterCore>();
    private readonly WriteShadow _shadow = new();

    private GenApiNodeMap(GenApiXmlModel model, IGevPort port)
    {
        _info = model.Info;
        _port = port;

        var binder = new NodeBinder(this, model);
        binder.BindAll();

        var nodes = binder.PublicNodes;
        var list = new INode[nodes.Count];
        for (var i = 0; i < list.Length; i++)
        {
            list[i] = nodes[i];
            _byName[nodes[i].Name] = nodes[i];
        }
        _nodes = list;
        _cores = binder.Cores.ToArray();

        _root = _byName.TryGetValue("Root", out var root) && root is ICategory category
            ? category
            : throw new GenApiException("The GenApi XML has no 'Root' category.", "Root");

        if (GevLog.IsEnabled(GevLogLevel.Debug))
            GevLog.Debug(LogSrc, $"bound {list.Length} node(s) of {_info.VendorName} {_info.ModelName} ({_cores.Length} register(s))");
    }

    /// <summary>카메라 XML 을 파싱해 포트에 바인딩된 노드맵을 만든다. 잘못된 XML·빠진 참조·순환은 <see cref="GenApiException"/>.</summary>
    public static GenApiNodeMap Parse(string xml, IGevPort port)
    {
        if (xml is null) throw new ArgumentNullException(nameof(xml));
        if (port is null) throw new ArgumentNullException(nameof(port));
        return Parse(GenApiXmlParser.Parse(xml), port);
    }

    /// <summary>이미 파싱된 모델을 포트에 바인딩한다.</summary>
    public static GenApiNodeMap Parse(GenApiXmlModel model, IGevPort port)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));
        if (port is null) throw new ArgumentNullException(nameof(port));
        return new GenApiNodeMap(model, port);
    }

    /// <summary>모든 레지스터 노드가 I/O 에 쓰는 포트 — Port 노드는 전부 이 하나로 간다(청크 어댑터는 다루지 않는다).</summary>
    internal IGevPort Port => _port;

    /// <summary>이 노드맵이 포트에 마지막으로 쓴 바이트 — 쓰기 전용 레지스터의 읽기-수정-쓰기 바탕값.</summary>
    internal WriteShadow Shadow => _shadow;

    /// <summary>노드 값이 쓰였다. 쓰인 노드 자신은 제외하고(그 캐시는 Cachable 정책이 이미 처리했다) 의존 닫힘 안의 캐시를 버린다.</summary>
    internal void OnWritten(NodeBase node)
    {
        var closure = Closure(node);
        foreach (var n in closure)
        {
            if (!ReferenceEquals(n, node)) NodeBase.DropCacheChain(n, node);
        }
    }

    /// <summary>
    /// 레지스터 바이트가 쓰였다 — 그림자에 남기고, 같은 주소를 나눠 쓰는 다른 노드(StructReg 항목, 별칭 레지스터)의 캐시가 겹치면 버린다.
    /// 노드 그래프로는 이어지지 않는 관계라 주소로 찾는다.
    /// </summary>
    internal void OnRegisterWritten(RegisterCore core, ulong address, byte[] data)
    {
        _shadow.Store(address, data);
        foreach (var c in _cores)
        {
            if (!ReferenceEquals(c, core)) c.DropIfOverlaps(address, data.Length);
        }
    }

    /// <summary><see cref="INode.Invalidate"/> — 노드 자신과 값 사슬, 그리고 의존 닫힘 전체의 캐시를 버린다.</summary>
    internal void InvalidateNode(NodeBase node)
    {
        foreach (var n in Closure(node)) NodeBase.DropCacheChain(n);
    }

    /// <summary>
    /// 무효화 닫힘: 시작 노드에서 의존 노드(p* 참조의 역방향)·pInvalidator 청취자·pSelected 대상을 따라 닿는 모든 노드(시작 노드 포함).
    /// 셀렉터의 역방향(pSelecting)은 따르지 않는다 — 선택된 피처를 써도 셀렉터는 그대로다.
    /// </summary>
    private static List<NodeBase> Closure(NodeBase start)
    {
        var visited = new HashSet<NodeBase>(NodeReferenceComparer.Instance) { start };
        var result = new List<NodeBase> { start };
        for (var i = 0; i < result.Count; i++)
        {
            var n = result[i];
            foreach (var d in n.Dependents)
            {
                if (visited.Add(d)) result.Add(d);
            }
            foreach (var l in n.InvalidatorListeners)
            {
                if (visited.Add(l)) result.Add(l);
            }
            foreach (var s in n.Selected)
            {
                if (visited.Add(s)) result.Add(s);
            }
        }
        return result;
    }
}
