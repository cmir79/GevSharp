using System.Collections.ObjectModel;

namespace GevSharp.GenApi.Model;

/// <summary>
/// XML 한 장을 읽은 결과 — 루트 속성과 노드 정의의 평면 사전. Group 은 투명하게 풀려 있고 StructReg 는 항목별로 펼쳐져 있다.
/// 불변이며 스레드 간 공유해도 된다. 이름 조회는 대소문자 구분(XML Name 그대로 — 단 EnumEntry 는 "EnumEntry_{열거}_{항목}" 한정 이름).
/// </summary>
public sealed class GenApiXmlModel
{
    private readonly NodeDef[] _nodeList;

    /// <summary>정의 목록으로 모델을 만든다. 이름이 겹치면 <see cref="GenApiException"/>.</summary>
    public GenApiXmlModel(RegisterDescriptionInfo info, IReadOnlyList<NodeDef> nodes, IReadOnlyList<string>? warnings = null)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        if (nodes is null) throw new ArgumentNullException(nameof(nodes));

        var dict = new Dictionary<string, NodeDef>(nodes.Count, StringComparer.Ordinal);
        _nodeList = new NodeDef[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            var def = nodes[i] ?? throw new ArgumentException("Node list contains null.", nameof(nodes));
            if (dict.ContainsKey(def.Name))
                throw new GenApiException($"Duplicate node name '{def.Name}'.", def.Name);
            dict.Add(def.Name, def);
            _nodeList[i] = def;
        }

        Nodes = new ReadOnlyDictionary<string, NodeDef>(dict);
        Warnings = warnings is null ? Array.Empty<string>() : warnings.ToArray();
    }

    /// <summary>RegisterDescription 루트 속성.</summary>
    public RegisterDescriptionInfo Info { get; }

    /// <summary>Name → 정의. 인라인 주소 IntSwissKnife 는 Name 속성이 있을 때만 여기 있다(이름 없는 것은 소유 레지스터 안에만).</summary>
    public IReadOnlyDictionary<string, NodeDef> Nodes { get; }

    /// <summary>문서 순서 그대로의 정의 목록(소유 노드 바로 뒤에 그 중첩 정의 — Enumeration 의 EnumEntry, 레지스터의 이름 있는 IntSwissKnife; StructReg 자리에 펼쳐진 항목들).</summary>
    public IReadOnlyList<NodeDef> NodeList => _nodeList;

    /// <summary>파싱 중 남긴 경고(알 수 없는 요소·자식, 낯선 네임스페이스 등). 같은 내용이 <see cref="GevLog"/> Warn 으로도 나간다.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>이름으로 찾는다. 없으면 null.</summary>
    public NodeDef? Find(string name) => Nodes.TryGetValue(name, out var def) ? def : null;

    /// <summary>이름으로 찾는다. 없으면 <see cref="GenApiException"/>.</summary>
    public NodeDef Get(string name)
        => Find(name) ?? throw new GenApiException($"Node '{name}' not found in the XML model.", name);

    /// <summary>이름과 정의 형으로 찾는다. 없거나 형이 다르면 <see cref="GenApiException"/>.</summary>
    public T Get<T>(string name) where T : NodeDef
    {
        var def = Get(name);
        return def as T ?? throw new GenApiException($"Node '{name}' is {def.Kind}, not {typeof(T).Name}.", name);
    }
}
