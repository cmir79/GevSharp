using System.Collections.ObjectModel;

namespace GevSharp.GenApi;

/// <summary>
/// SwissKnife / Converter 수식 — 한 번 파싱한 불변 트리를 여러 번 평가한다. 평가 상태는 호출마다 따로 만들어 스레드 안전하다.
/// <para>
/// 문법: 이항 <c>+ - * / % ** &amp; | ^ &lt;&lt; &gt;&gt; &amp;&amp; || &lt; &gt; &lt;= &gt;= = == &lt;&gt; !=</c>, 단항 <c>+ - ! ~</c>,
/// 삼항 <c>?:</c>(우결합, 택한 가지만 평가), 괄호, 정수(10진·<c>0x</c> 16진)·실수(소수점·지수) 리터럴, 상수 <c>PI</c>·<c>E</c>,
/// 함수 SIN COS TAN ASIN ACOS ATAN ABS EXP LN LG SQRT TRUNC FLOOR CEIL ROUND SGN NEG(인자 하나, 이름은 대소문자 무관) —
/// ROUND 는 소수 자릿수 0..15 를 둘째 인자로 더 받는다(ROUND(x, 2)).
/// </para>
/// <para>
/// 우선순위(센 쪽부터): 단항 → <c>**</c>(우결합, -2**2 = 4) → <c>* / %</c> → <c>+ -</c> → <c>&lt;&lt; &gt;&gt;</c> → <c>&lt; &lt;= &gt; &gt;=</c>
/// → <c>= == &lt;&gt; !=</c> → <c>&amp;</c> → <c>^</c> → <c>|</c> → <c>&amp;&amp;</c> → <c>||</c> → <c>?:</c>.
/// </para>
/// <para>
/// 형: 정수끼리는 정수(<c>/</c> 는 0 방향 절삭, 음수 지수의 <c>**</c> 는 실수, 시프트는 64비트), 한쪽이라도 실수면 실수,
/// 비교·논리는 정수 1/0, 실수의 비트 연산은 오류. 0 나눗셈·오버플로·초월함수의 정의역 밖(SQRT(-1), LN(0))·모르는 변수/함수·
/// 문법 오류는 위치(0 기준 문자 인덱스)를 담은 <see cref="GenApiException"/> — 결과를 0 이나 NaN 으로 흘리지 않는다.
/// </para>
/// 변수 이름은 대소문자를 구분하며 글자·숫자·'_'·'.' 로 이루어진다. 값은 호출자가 이름으로 공급한다(pVariable → 노드 매핑은 노드 계층 몫).
/// </summary>
public sealed class Formula
{
    /// <summary>괄호·단항·함수·삼항·지수 중첩과 트리 깊이의 상한 — 넘으면 파싱 단계에서 <see cref="GenApiException"/>.</summary>
    public const int MaxDepth = 200;

    private readonly FormulaNode _root;
    private readonly string[] _vars;

    private Formula(string text, FormulaNode root, string[] vars)
    {
        Text = text;
        _root = root;
        _vars = vars;
        Variables = Array.AsReadOnly(vars);
    }

    /// <summary>원문 그대로(공백 포함).</summary>
    public string Text { get; }

    /// <summary>수식이 참조하는 변수 이름 — 서로 다른 것만, 첫 등장 순서. 상수 PI/E 와 함수 이름은 들어가지 않는다.</summary>
    public IReadOnlyList<string> Variables { get; }

    public bool HasVariables => _vars.Length > 0;

    /// <summary>수식을 파싱한다. 문법 오류·모르는 함수·인자 수 오류·중첩 초과는 위치를 담은 <see cref="GenApiException"/>.</summary>
    public static Formula Parse(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var root = FormulaParser.Parse(text, out var vars);
        return new Formula(text, root, vars);
    }

    /// <summary>
    /// 동기 평가. 변수는 처음 쓰일 때 한 번만 resolve 를 부르며, 택하지 않은 삼항 가지의 변수는 해석하지 않는다.
    /// resolve 가 던진 예외는 그대로 전파된다.
    /// </summary>
    public GenApiValue Evaluate(Func<string, GenApiValue> resolve)
    {
        if (resolve is null) throw new ArgumentNullException(nameof(resolve));
        return _root.Eval(new FormulaEvalCtx(Text, _vars, resolve));
    }

    /// <summary>사전으로 평가. 실제로 쓰이는 변수가 사전에 없으면 위치를 담은 <see cref="GenApiException"/>.</summary>
    public GenApiValue Evaluate(IReadOnlyDictionary<string, GenApiValue> variables)
    {
        if (variables is null) throw new ArgumentNullException(nameof(variables));
        var ctx = new FormulaEvalCtx(Text, _vars, null);
        for (int i = 0; i < _vars.Length; i++)
        {
            if (variables.TryGetValue(_vars[i], out var v)) ctx.SetVariable(i, v);
        }
        return _root.Eval(ctx);
    }

    /// <summary>
    /// 비동기 평가. 모든 변수를 <see cref="Variables"/> 순서로 각각 한 번씩 먼저 해석한 뒤(레지스터 왕복이 여기서 일어난다)
    /// 동기로 평가한다. 삼항의 택하지 않은 가지 변수도 해석된다는 점이 동기 평가와 다르다.
    /// </summary>
    public ValueTask<GenApiValue> EvaluateAsync(Func<string, ValueTask<GenApiValue>> resolve, CancellationToken ct = default)
    {
        if (resolve is null) throw new ArgumentNullException(nameof(resolve));
        return EvaluateAsyncCore(resolve, ct);
    }

    private async ValueTask<GenApiValue> EvaluateAsyncCore(Func<string, ValueTask<GenApiValue>> resolve, CancellationToken ct)
    {
        var ctx = new FormulaEvalCtx(Text, _vars, null);
        for (int i = 0; i < _vars.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            ctx.SetVariable(i, await resolve(_vars[i]).ConfigureAwait(false));
        }
        return _root.Eval(ctx);
    }

    public override string ToString() => Text;
}
