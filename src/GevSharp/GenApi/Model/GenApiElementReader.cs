using System.Xml.Linq;

namespace GevSharp.GenApi.Model;

/// <summary>
/// 노드 요소 하나의 자식을 로컬 이름으로 꺼내며 어떤 이름을 소비했는지 기록한다.
/// 네임스페이스는 보지 않는다(1.0/1.1/없음 전부 같은 이름). 파싱이 끝난 뒤 남은 자식 이름이 곧 "모르는 자식" 경고 대상이다.
/// 텍스트는 항상 앞뒤 공백을 지운다. 비어 있는 요소(&lt;Unit/&gt;·&lt;ValidValueSet&gt;&lt;/ValidValueSet&gt;)는 없는 것으로 본다 —
/// "" 을 값으로 돌려주면 빈 허용값 집합(아무 값도 못 씀)이나 물려받은 ToolTip 을 지우는 빈 ToolTip 처럼 뜻이 뒤집힌다.
/// 빈 문자열이 정당한 값인 곳(String 의 Value)만 <see cref="TextAllowEmpty"/> 를 쓴다.
/// </summary>
internal sealed class GenApiElementReader
{
    private readonly XElement _el;
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);
    private readonly Action<string>? _warn;

    /// <param name="el">읽을 요소.</param>
    /// <param name="nodeName">오류·경고 메시지에 쓸 노드 이름.</param>
    /// <param name="warn">경고 출구 — 빈 목록 항목처럼 죽일 일은 아니지만 알려야 하는 것. null 이면 조용히 지나간다.</param>
    public GenApiElementReader(XElement el, string nodeName, Action<string>? warn = null)
    {
        _el = el;
        NodeName = nodeName;
        _warn = warn;
    }

    /// <summary>오류 메시지에 쓸 노드 이름(합성 이름일 수도 있다).</summary>
    public string NodeName { get; }

    public string LocalName => _el.Name.LocalName;

    /// <summary>속성 값. 없거나 비어 있으면 null.</summary>
    public string? Attr(string name)
    {
        var v = _el.Attribute(name)?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>첫 번째 자식의 텍스트(공백 제거). 요소가 없거나 비어 있으면 null — 빈 요소는 없는 것과 같다.</summary>
    public string? Text(string localName)
    {
        var t = TextAllowEmpty(localName);
        return string.IsNullOrEmpty(t) ? null : t;
    }

    /// <summary>첫 번째 자식의 텍스트(공백 제거). 요소가 없으면 null, 비어 있으면 "" — 빈 문자열이 값인 곳(String 의 Value)에서만 쓴다.</summary>
    public string? TextAllowEmpty(string localName)
    {
        _consumed.Add(localName);
        foreach (var c in _el.Elements())
        {
            if (c.Name.LocalName == localName) return c.Value.Trim();
        }
        return null;
    }

    /// <summary>노드 이름 참조(p*) — 요소가 없거나 비어 있으면 null. 뜻을 드러내는 이름일 뿐 <see cref="Text"/> 와 같다.</summary>
    public string? Ref(string localName) => Text(localName);

    /// <summary>같은 이름의 자식 전부의 텍스트(문서 순서). 빈 항목은 경고를 남기고 건너뛴다 — 조용히 사라지면 빠진 참조를 아무도 모른다.</summary>
    public IReadOnlyList<string> TextList(string localName)
    {
        _consumed.Add(localName);
        List<string>? list = null;
        foreach (var c in _el.Elements())
        {
            if (c.Name.LocalName != localName) continue;
            var t = c.Value.Trim();
            if (t.Length == 0)
            {
                _warn?.Invoke($"Empty <{localName}> in <{LocalName}> '{NodeName}' was ignored.");
                continue;
            }
            (list ??= new List<string>()).Add(t);
        }
        return list is null ? Array.Empty<string>() : list;
    }

    /// <summary>같은 이름의 자식 요소 전부(문서 순서).</summary>
    public IReadOnlyList<XElement> Elements(string localName)
    {
        _consumed.Add(localName);
        List<XElement>? list = null;
        foreach (var c in _el.Elements())
        {
            if (c.Name.LocalName == localName) (list ??= new List<XElement>()).Add(c);
        }
        return list is null ? Array.Empty<XElement>() : list;
    }

    public long? Int64(string localName)
    {
        var t = Text(localName);
        return t is null ? null : GenApiLiteral.ParseInt64(t, localName, NodeName);
    }

    public int? Int32(string localName)
    {
        var t = Text(localName);
        return t is null ? null : GenApiLiteral.ParseInt32(t, localName, NodeName);
    }

    public double? Double(string localName)
    {
        var t = Text(localName);
        return t is null ? null : GenApiLiteral.ParseDouble(t, localName, NodeName);
    }

    public bool? YesNo(string localName)
    {
        var t = Text(localName);
        return t is null ? null : GenApiLiteral.ParseYesNo(t, localName, NodeName);
    }

    /// <summary>읽지 않고 소비한 것으로 표시한다(예: Extension).</summary>
    public void Consume(string localName) => _consumed.Add(localName);

    /// <summary>아직 소비되지 않은 자식 요소의 로컬 이름들(중복 없이, 문서 순서).</summary>
    public IReadOnlyList<string> UnconsumedNames()
    {
        List<string>? list = null;
        foreach (var c in _el.Elements())
        {
            var n = c.Name.LocalName;
            if (_consumed.Contains(n)) continue;
            if (list is not null && list.Contains(n)) continue;
            (list ??= new List<string>()).Add(n);
        }
        return list is null ? Array.Empty<string>() : list;
    }
}
