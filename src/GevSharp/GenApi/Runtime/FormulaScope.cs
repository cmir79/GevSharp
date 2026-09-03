using GevSharp.GenApi.Model;

namespace GevSharp.GenApi.Runtime;

/// <summary>
/// 수식 노드(SwissKnife/IntSwissKnife/Converter/IntConverter)의 변수 범위 — pVariable·Constant·Expression 을 이름으로 묶고
/// 수식을 비동기로 평가한다. 수식은 바인딩 시점에 한 번만 파싱한다.
/// <para>
/// 평가는 <see cref="Formula.EvaluateAsync"/> 로 한다: 수식이 나열한 변수를 전부(택하지 않은 삼항 가지의 것까지) 먼저 읽고
/// 동기로 계산한다. 변수 하나의 읽기 실패가 곧 수식 실패라 결과가 결정적이고, 레지스터 왕복이 변수 순서대로 한 번씩만 일어난다.
/// </para>
/// <para>
/// pVariable 의 Name 은 수식 안의 변수 이름 그대로이며, 점 접미사로 무엇을 읽을지 정한다:
/// 접미사 없음 또는 <c>.Value</c> = 값, <c>.Min</c>/<c>.Max</c>/<c>.Inc</c> = 한계값, <c>.Entry.〈Symbolic〉</c> = Enumeration 항목의 정수 값(상수).
/// 모르는 접미사는 값으로 보고 경고를 남긴다.
/// </para>
/// </summary>
internal sealed class FormulaScope
{
    private const int MaxExpressionDepth = 32;

    private enum VarSuffix
    {
        Value,
        Min,
        Max,
        Inc,
        Entry,
    }

    private sealed class VarRef
    {
        public VarRef(NodeBase node, VarSuffix suffix, GenApiValue constant)
        {
            Node = node;
            Suffix = suffix;
            Constant = constant;
        }

        public NodeBase Node { get; }
        public VarSuffix Suffix { get; }
        public GenApiValue Constant { get; }
    }

    private readonly NodeBase _owner;
    private readonly Dictionary<string, GenApiValue> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Formula> _expressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VarRef> _variables = new(StringComparer.Ordinal);

    public FormulaScope(NodeBase owner, IFormulaNodeDef def, NodeBinder binder)
    {
        _owner = owner;
        foreach (var c in def.Constants)
            _constants[c.Name] = c.IntValue is { } iv ? new GenApiValue(iv) : new GenApiValue(c.DoubleValue);
        foreach (var v in def.Variables)
            _variables[v.Name] = BindVariable(v, binder);
        foreach (var e in def.Expressions)
            _expressions[e.Name] = Parse(e.Expression, $"Expression '{e.Name}'");
        CheckExpressionCycles();
    }

    /// <summary>수식을 파싱한다. 오류에는 노드 이름과 어느 수식인지 붙인다.</summary>
    public Formula Parse(string text, string label)
    {
        try
        {
            return Formula.Parse(text);
        }
        catch (GenApiException ex)
        {
            throw new GenApiException($"{label} of node '{_owner.Name}' cannot be parsed: {ex.Message}", _owner.Name, ex);
        }
    }

    /// <summary>수식을 평가한다. extraName 은 Converter 의 FROM/TO 처럼 호출자가 값을 주는 변수.</summary>
    public ValueTask<GenApiValue> EvaluateAsync(Formula formula, string? extraName, GenApiValue extraValue, CancellationToken ct)
        => formula.EvaluateAsync(name => ResolveAsync(name, extraName, extraValue, 0, ct), ct);

    private async ValueTask<GenApiValue> ResolveAsync(string name, string? extraName, GenApiValue extraValue, int depth, CancellationToken ct)
    {
        if (extraName is not null && string.Equals(name, extraName, StringComparison.Ordinal)) return extraValue;
        if (_constants.TryGetValue(name, out var constant)) return constant;
        if (_expressions.TryGetValue(name, out var expression))
        {
            if (depth >= MaxExpressionDepth)
                throw new GenApiException($"Expression '{name}' of node '{_owner.Name}' nests too deeply.", _owner.Name);
            return await expression.EvaluateAsync(n => ResolveAsync(n, extraName, extraValue, depth + 1, ct), ct).ConfigureAwait(false);
        }
        if (_variables.TryGetValue(name, out var variable)) return await ReadVariableAsync(variable, ct).ConfigureAwait(false);
        throw new GenApiException($"Formula variable '{name}' is not defined in node '{_owner.Name}'.", _owner.Name);
    }

    private static ValueTask<GenApiValue> ReadVariableAsync(VarRef v, CancellationToken ct) => v.Suffix switch
    {
        VarSuffix.Value => v.Node.ReadValueAsync(ct),
        VarSuffix.Min => v.Node.ReadLimitAsync(LimitKind.Min, ct),
        VarSuffix.Max => v.Node.ReadLimitAsync(LimitKind.Max, ct),
        VarSuffix.Inc => v.Node.ReadLimitAsync(LimitKind.Inc, ct),
        _ => new ValueTask<GenApiValue>(v.Constant),
    };

    private VarRef BindVariable(FormulaVariableDef v, NodeBinder binder)
    {
        var dot = v.Name.IndexOf('.');
        var suffix = dot < 0 ? "" : v.Name.Substring(dot + 1);
        var what = $"pVariable '{v.Name}'";

        if (suffix.StartsWith("Entry.", StringComparison.Ordinal))
        {
            var symbolic = suffix.Substring("Entry.".Length);
            // 항목 값은 바인딩에서 상수로 굳는다 — 실행 중에 열거를 읽지 않으므로 값 간선을 남기지 않는다
            // (남기면 열거의 술어가 이 수식을 가리키는 흔한 XML 이 순환으로 거부되고, 열거를 쓸 때마다 이 수식이 헛되이 무효화된다).
            var node = binder.Resolve(v.PNode, RefKind.Constant, what, NodeBinder.Any);
            if (node is not EnumerationNode enumeration)
                throw new GenApiException($"Node '{_owner.Name}' uses '{v.Name}' on node '{v.PNode}', which is a {node.Def.Kind}, not an Enumeration.", _owner.Name);
            // 열거가 문서에서 이 노드보다 뒤에 있으면 아직 항목이 채워지지 않았다 — 먼저 바인딩한다(한 번만 실행되므로 안전하다).
            binder.BindNode(enumeration);
            var entry = enumeration.FindEntry(symbolic)
                ?? throw new GenApiException($"Node '{_owner.Name}' uses '{v.Name}', but enumeration '{v.PNode}' has no entry '{symbolic}'.", _owner.Name);
            return new VarRef(node, VarSuffix.Entry, new GenApiValue(entry.Value));
        }

        var target = binder.Resolve(v.PNode, RefKind.Value, what, NodeBinder.Numeric);
        switch (suffix)
        {
            case "":
            case "Value":
                return new VarRef(target, VarSuffix.Value, default);
            case "Min":
                return new VarRef(target, VarSuffix.Min, default);
            case "Max":
                return new VarRef(target, VarSuffix.Max, default);
            case "Inc":
                return new VarRef(target, VarSuffix.Inc, default);
            default:
                GevLog.Warn("GenApi.Runtime", $"Node '{_owner.Name}': unknown pVariable suffix '.{suffix}' in '{v.Name}' is treated as the node value.");
                return new VarRef(target, VarSuffix.Value, default);
        }
    }

    /// <summary>Expression 끼리의 순환 참조는 평가 시 끝없이 재귀하므로 바인딩에서 잡는다.</summary>
    private void CheckExpressionCycles()
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in _expressions.Keys) Visit(name, state, new List<string>());
    }

    private void Visit(string name, Dictionary<string, int> state, List<string> path)
    {
        if (state.TryGetValue(name, out var s))
        {
            if (s == 1)
            {
                var start = path.IndexOf(name);
                var cycle = string.Join(" -> ", path.GetRange(start, path.Count - start)) + " -> " + name;
                throw new GenApiException($"Expressions of node '{_owner.Name}' reference each other in a cycle: {cycle}.", _owner.Name);
            }
            return;
        }
        state[name] = 1;
        path.Add(name);
        foreach (var v in _expressions[name].Variables)
        {
            if (_expressions.ContainsKey(v)) Visit(v, state, path);
        }
        path.RemoveAt(path.Count - 1);
        state[name] = 2;
    }

    /// <summary>
    /// Converter 의 Min/Max: 대상 노드의 한계값을 FormulaFrom 으로 넘긴다. Increasing 은 (From(min), From(max)),
    /// Decreasing 은 그 반대, Automatic 은 둘 다 계산해 작은 쪽을 Min 으로 둔다.
    /// <para>
    /// <b>Varying 은 한계를 짓지 않는다</b> — 수식이 단조롭지 않다고 XML 이 스스로 밝힌 것이므로, 대상의 양 끝이 이 노드의 양 끝이라는
    /// 근거가 사라진다. 그래도 계산하면 값을 넓게 잡는 정도로 끝나지 않고 노드를 아예 못 쓰게 만든다: 룩업 테이블형 변환
    /// (조건 사슬 끝에 "해당 없음" 상수를 두는 흔한 모양)은 양 끝이 모두 그 상수 분기로 떨어져 Min 과 Max 가 같은 값이 되고,
    /// 그러면 정작 표에 있는 값을 포함해 모든 쓰기가 범위 밖으로 거절된다(실측: 픽셀 포맷을 바꿀 수 없었다).
    /// 한계를 모르면 모른다고 두고 장치가 판단하게 한다 — 우리가 없는 범위를 지어내지 않는다.
    /// </para>
    /// <para>
    /// 대상이 그쪽 한계를 선언하지 않았으면(<see cref="IsOpenEnd"/> 의 센티널) 그 끝은 null — "열린 끝" 이다. 센티널을 그대로
    /// 수식에 넣으면 곱셈·덧셈 한 번에 넘쳐 한계값 조회가 실패하고, 쓰기마다 한계를 묻는 탓에 노드 전체를 못 쓰게 된다.
    /// 열린 끝은 호출자가 자기 값 종류의 극단으로 채운다.
    /// </para>
    /// </summary>
    public async ValueTask<(GenApiValue? Min, GenApiValue? Max)> ConverterLimitsAsync(NodeBase target, Formula formulaFrom, Slope slope, CancellationToken ct)
    {
        // 단조롭지 않은 변환은 대상 한계를 읽어 볼 것도 없다 — 읽어도 쓸 수 없고, 왕복만 는다.
        if (slope == Slope.Varying) return (null, null);

        var a = await EndpointAsync(target, LimitKind.Min, formulaFrom, ct).ConfigureAwait(false);
        var b = await EndpointAsync(target, LimitKind.Max, formulaFrom, ct).ConfigureAwait(false);
        switch (slope)
        {
            case Slope.Increasing:
                return (a, b);
            case Slope.Decreasing:
                return (b, a);
            default:
                // 기울기를 모르는데 한쪽 끝이 열려 있으면 남은 끝이 Min 인지 Max 인지도 알 수 없다 — 양쪽을 열어 둔다.
                if (a is null || b is null) return (null, null);
                return a.Value.AsDouble <= b.Value.AsDouble ? (a, b) : (b, a);
        }
    }

    /// <summary>대상 한계값 한쪽을 FormulaFrom 으로 옮긴 값. 선언되지 않은 한계면 수식을 평가하지 않고 열린 끝(null)으로 둔다.</summary>
    private async ValueTask<GenApiValue?> EndpointAsync(NodeBase target, LimitKind kind, Formula formulaFrom, CancellationToken ct)
    {
        var limit = await target.ReadLimitAsync(kind, ct).ConfigureAwait(false);
        if (IsOpenEnd(limit, kind)) return null;
        return await EvaluateAsync(formulaFrom, "TO", limit, ct).ConfigureAwait(false);
    }

    /// <summary>한계값이 "선언 안 됨" 을 뜻하는 극단인지 — 정수 노드는 long 의 양끝, 실수 노드는 double 의 양끝(무한대 포함).</summary>
    private static bool IsOpenEnd(GenApiValue limit, LimitKind kind)
        => kind == LimitKind.Min
            ? (limit.IsInteger ? limit.AsInt64 == long.MinValue : limit.AsDouble <= -double.MaxValue)
            : (limit.IsInteger ? limit.AsInt64 == long.MaxValue : limit.AsDouble >= double.MaxValue);
}
