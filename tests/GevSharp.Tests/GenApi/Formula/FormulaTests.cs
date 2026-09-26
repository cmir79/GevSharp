using GevSharp.GenApi;

namespace GevSharp.Tests.GenApi;

public class FormulaTests
{
    private static readonly IReadOnlyDictionary<string, GenApiValue> NoVars = new Dictionary<string, GenApiValue>();

    private static GenApiValue Eval(string text) => Formula.Parse(text).Evaluate(NoVars);

    private static GenApiValue Eval(string text, params (string Name, GenApiValue Value)[] vars)
    {
        var dict = new Dictionary<string, GenApiValue>(StringComparer.Ordinal);
        foreach (var (name, value) in vars) dict[name] = value;
        return Formula.Parse(text).Evaluate(dict);
    }

    /// <summary>실수 노드(SwissKnife·Converter)가 쓰는 실수 규칙으로 평가한다.</summary>
    private static GenApiValue EvalReal(string text, params (string Name, GenApiValue Value)[] vars)
    {
        var dict = new Dictionary<string, GenApiValue>(StringComparer.Ordinal);
        foreach (var (name, value) in vars) dict[name] = value;
        return Formula.Parse(text).Evaluate(dict, FormulaMode.Real);
    }

    // ---- 정수 결과: 연산자·우선순위·결합·리터럴·함수 ----

    [Theory]
    [InlineData("1 + 2", 3L)]
    [InlineData("7 - 10", -3L)]
    [InlineData("6 * 7", 42L)]
    [InlineData("7 / 2", 3L)]                    // 0 방향 절삭
    [InlineData("-7 / 2", -3L)]
    [InlineData("7 % 3", 1L)]
    [InlineData("-7 % 3", -1L)]                  // 피제수 부호
    [InlineData("0x8000000000000000 % -1", 0L)]
    [InlineData("2 ** 10", 1024L)]
    [InlineData("2 ** 0", 1L)]
    [InlineData("0 ** 0", 1L)]
    [InlineData("(-3) ** 3", -27L)]
    [InlineData("2 ** 3 ** 2", 512L)]            // ** 우결합
    [InlineData("-2 ** 2", 4L)]                  // 단항이 ** 보다 세다: (-2)**2
    [InlineData("-(2 ** 2)", -4L)]
    [InlineData("~2 ** 2", 9L)]                  // (~2)**2 = (-3)**2
    [InlineData("2 ** -2 ** 2", 16L)]            // 지수 자리의 부호: 2**((-2)**2)
    [InlineData("- -2 ** 2", 4L)]
    [InlineData("0xFF & 0x0F", 15L)]
    [InlineData("0xF0 | 0x0F", 255L)]
    [InlineData("0xFF ^ 0x0F", 240L)]
    [InlineData("1 << 4", 16L)]
    [InlineData("256 >> 4", 16L)]
    [InlineData("-16 >> 2", -4L)]                // 산술 시프트
    [InlineData("1 << 63", long.MinValue)]
    [InlineData("~0", -1L)]
    [InlineData("~~5", 5L)]
    [InlineData("!0", 1L)]
    [InlineData("!5", 0L)]
    [InlineData("!!7", 1L)]
    [InlineData("+5", 5L)]
    [InlineData("- -5", 5L)]
    [InlineData("1 - -1", 2L)]
    [InlineData("1 && 0", 0L)]
    [InlineData("1 && 2", 1L)]
    [InlineData("0 || 3", 1L)]
    [InlineData("0 || 0", 0L)]
    [InlineData("3 < 5", 1L)]
    [InlineData("5 <= 5", 1L)]
    [InlineData("3 > 5", 0L)]
    [InlineData("5 >= 6", 0L)]
    [InlineData("5 = 5", 1L)]
    [InlineData("5 == 4", 0L)]
    [InlineData("5 <> 4", 1L)]
    [InlineData("5 != 5", 0L)]
    [InlineData("1 + 2 * 3", 7L)]
    [InlineData("(1 + 2) * 3", 9L)]
    [InlineData("10 - 4 - 3", 3L)]               // 좌결합
    [InlineData("100 / 10 / 2", 5L)]
    [InlineData("2 * 3 % 4", 2L)]
    [InlineData("1 << 2 + 1", 8L)]               // + 가 << 보다 세다
    [InlineData("1 + 1 << 2", 8L)]
    [InlineData("3 & 1 == 1", 1L)]               // == 가 & 보다 세다
    [InlineData("1 | 2 ^ 3 & 4", 3L)]            // & → ^ → |
    [InlineData("1 < 2 == 1", 1L)]               // 관계가 동등보다 세다
    [InlineData("1 | 0 && 0", 0L)]               // | 가 && 보다 세다
    [InlineData("0 && 0 || 1", 1L)]              // && 가 || 보다 세다
    [InlineData("1 ? 2 : 3", 2L)]
    [InlineData("0 ? 2 : 3", 3L)]
    [InlineData("1 ? 0 ? 5 : 6 : 7", 6L)]
    [InlineData("0 ? 1 : 0 ? 2 : 3", 3L)]        // 우결합
    [InlineData("1 + 1 ? 10 : 20", 10L)]         // ?: 가 가장 약하다
    [InlineData("1 ? 2 : 1 / 0", 2L)]            // 택하지 않은 가지의 0 나눗셈은 평가되지 않는다
    [InlineData("0 ? 1 / 0 : 4", 4L)]
    [InlineData("0 && 1 / 0", 0L)]               // 단락 평가
    [InlineData("1 || 1 / 0", 1L)]
    [InlineData("0x10", 16L)]
    [InlineData("0XfF", 255L)]
    [InlineData("0x7FFFFFFFFFFFFFFF", long.MaxValue)]
    [InlineData("0xFFFFFFFFFFFFFFFF", -1L)]
    [InlineData("0x8000000000000000", long.MinValue)]
    [InlineData("ABS(-5)", 5L)]
    [InlineData("abs(-5)", 5L)]                  // 함수 이름은 대소문자 무관
    [InlineData("Abs(5)", 5L)]
    [InlineData("SGN(-9)", -1L)]
    [InlineData("SGN(0)", 0L)]
    [InlineData("SGN(42)", 1L)]
    [InlineData("SGN(-2.5)", -1L)]               // SGN 은 항상 정수
    [InlineData("NEG(4)", -4L)]
    [InlineData("TRUNC(7)", 7L)]
    [InlineData("FLOOR(7)", 7L)]
    [InlineData("CEIL(7)", 7L)]
    [InlineData("ROUND(7)", 7L)]
    [InlineData("ROUND(7, 0)", 7L)]              // 정수는 자릿수와 무관하게 정수 그대로
    [InlineData("ROUND(-7, 3)", -7L)]
    [InlineData("round(7, 2.9)", 7L)]            // 실수 자릿수는 0 방향 절삭
    [InlineData("  1 +\n\t2  ", 3L)]
    [InlineData("1+2", 3L)]
    [InlineData("(((((1)))))", 1L)]
    public void IntegerFormulasEvaluateToInteger(string text, long expected)
    {
        var v = Eval(text);
        Assert.True(v.IsInteger, $"expected an integer result for '{text}', got {v}");
        Assert.Equal(expected, v.AsInt64);
    }

    // ---- 실수 결과: 승격·리터럴·상수·함수 ----

    [Theory]
    [InlineData("1.5 + 2", 3.5)]
    [InlineData("7 / 2.0", 3.5)]
    [InlineData("7.5 % 2", 1.5)]
    [InlineData("-2.5", -2.5)]
    [InlineData("1e3", 1000.0)]
    [InlineData("1.5e-3", 0.0015)]
    [InlineData("2.5E2", 250.0)]
    [InlineData(".5", 0.5)]
    [InlineData("2 ** -1", 0.5)]                 // 음수 지수는 실수
    [InlineData("2 ** 0.5", 1.4142135623730951)]
    [InlineData("4 ** 0.5", 2.0)]
    [InlineData("4.0 ** 2", 16.0)]
    [InlineData("PI", Math.PI)]
    [InlineData("E", Math.E)]
    [InlineData("2 * PI", 2 * Math.PI)]
    [InlineData("SIN(0)", 0.0)]
    [InlineData("COS(0)", 1.0)]
    [InlineData("TAN(0)", 0.0)]
    [InlineData("ASIN(1)", Math.PI / 2)]
    [InlineData("ACOS(1)", 0.0)]
    [InlineData("ATAN(1)", Math.PI / 4)]
    [InlineData("EXP(1)", Math.E)]
    [InlineData("LN(E)", 1.0)]
    [InlineData("LG(1000)", 3.0)]
    [InlineData("SQRT(16)", 4.0)]                // 정수 입력이라도 실수
    [InlineData("TRUNC(-2.7)", -2.0)]
    [InlineData("FLOOR(-2.5)", -3.0)]
    [InlineData("CEIL(2.1)", 3.0)]
    [InlineData("ROUND(2.5)", 3.0)]              // 0 에서 먼 쪽
    [InlineData("ROUND(-2.5)", -3.0)]
    [InlineData("ROUND(2.4)", 2.0)]
    [InlineData("ROUND(2.5, 0)", 3.0)]           // 둘째 인자 = 소수 자릿수
    [InlineData("ROUND(1.2345, 2)", 1.23)]
    [InlineData("ROUND(2.375, 2)", 2.38)]        // 자릿수 반올림도 0 에서 먼 쪽(2.375 는 이진수로 정확)
    [InlineData("ROUND(-2.375, 2)", -2.38)]
    [InlineData("ROUND(0.125, 2)", 0.13)]
    [InlineData("ROUND(1.5, 15)", 1.5)]
    [InlineData("ROUND(200 * LG(2.0), 0)", 60.0)]
    [InlineData("ABS(-2.5)", 2.5)]
    [InlineData("NEG(2.5)", -2.5)]
    [InlineData("10 / 4.0 * 2", 5.0)]
    [InlineData("1 + 2.0 * 3", 7.0)]
    [InlineData("1.0 ? 2.5 : 3", 2.5)]
    public void DoubleFormulasEvaluateToDouble(string text, double expected)
    {
        var v = Eval(text);
        Assert.True(v.IsDouble, $"expected a double result for '{text}', got {v}");
        Assert.Equal(expected, v.AsDouble, 1e-12);
    }

    [Theory]
    [InlineData("1.0 = 1", 1L)]
    [InlineData("0.5 < 1", 1L)]
    [InlineData("2.5 > 2.5", 0L)]
    [InlineData("1.5 != 1.5", 0L)]
    [InlineData("1.5 <> 2", 1L)]
    [InlineData("2.0 >= 2", 1L)]
    [InlineData("1.0 && 0.0", 0L)]
    [InlineData("!0.0", 1L)]
    [InlineData("0.0 || 0.5", 1L)]
    [InlineData("10 / 4 * 2", 4L)]               // 정수끼리는 끝까지 정수
    public void ComparisonsAndLogicYieldIntegerRegardlessOfOperandType(string text, long expected)
    {
        var v = Eval(text);
        Assert.True(v.IsInteger, $"expected an integer result for '{text}', got {v}");
        Assert.Equal(expected, v.AsInt64);
    }

    // ---- 오류 ----

    [Theory]
    [InlineData("", "Formula is empty")]
    [InlineData("   \n ", "Formula is empty")]
    [InlineData("1 +", "Unexpected end of formula")]
    [InlineData("(1 + 2", "Expected ')'")]
    [InlineData("1 + 2)", "Unexpected token ')'")]
    [InlineData("1 ? 2", "Expected ':'")]
    [InlineData("FOO(1)", "Unknown function 'FOO'")]
    [InlineData("ABS(1, 2)", "expects 1 argument, got 2")]
    [InlineData("ABS()", "expects 1 argument, got 0")]
    [InlineData("ROUND(1, 2, 3)", "expects 1 or 2 arguments, got 3")]
    [InlineData("ROUND()", "expects 1 or 2 arguments, got 0")]
    [InlineData("1e400", "outside the range of a double")]
    [InlineData("-1e400", "outside the range of a double")]
    [InlineData("1.5e309", "outside the range of a double")]
    [InlineData("SIN 1", "Unexpected token '1'")]
    [InlineData("1 # 2", "Unexpected character '#'")]
    [InlineData("0x", "Hexadecimal literal has no digits")]
    [InlineData("0x1FFFFFFFFFFFFFFFF", "exceeds 64 bits")]
    [InlineData("99999999999999999999", "outside the 64-bit range")]
    [InlineData("1 2", "Unexpected token '2'")]
    [InlineData("* 3", "Unexpected token '*'")]
    [InlineData("1 +* 2", "Unexpected token '*'")]
    [InlineData("12abc", "Invalid number literal")]
    [InlineData("1e", "Invalid number literal")]
    [InlineData("1e+", "Invalid number literal")]
    [InlineData("ABS(1", "Expected ')'")]
    [InlineData("()", "Unexpected token ')'")]
    public void ParseErrorsThrowGenApiException(string text, string expectedFragment)
    {
        var ex = Assert.Throws<GenApiException>(() => Formula.Parse(text));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Theory]
    [InlineData("1 / 0", "Division by zero")]
    [InlineData("1.0 / 0", "Division by zero")]
    [InlineData("1 / 0.0", "Division by zero")]
    [InlineData("5 % 0", "Division by zero")]
    [InlineData("5.5 % 0", "Division by zero")]
    [InlineData("0 ** -1", "Division by zero")]
    [InlineData("1.5 & 1", "requires integer operands")]
    [InlineData("1 | 2.0", "requires integer operands")]
    [InlineData("1 ^ 0.5", "requires integer operands")]
    [InlineData("~1.5", "requires an integer operand")]
    [InlineData("1.5 << 1", "requires integer operands")]
    [InlineData("1 >> 1.0", "requires integer operands")]
    [InlineData("1 << 64", "Shift count 64")]
    [InlineData("1 << -1", "Shift count -1")]
    [InlineData("1 >> 100", "Shift count 100")]
    [InlineData("0x7FFFFFFFFFFFFFFF + 1", "Integer overflow in '+'")]
    [InlineData("-0x7FFFFFFFFFFFFFFF - 2", "Integer overflow in '-'")]
    [InlineData("0x7FFFFFFFFFFFFFFF * 2", "Integer overflow in '*'")]
    [InlineData("2 ** 64", "Integer overflow in '**'")]
    [InlineData("-0x8000000000000000", "Integer overflow in unary '-'")]
    [InlineData("ABS(0x8000000000000000)", "Integer overflow in ABS")]
    [InlineData("NEG(0x8000000000000000)", "Integer overflow in unary '-'")]
    [InlineData("0x8000000000000000 / -1", "Integer overflow in '/'")]
    [InlineData("SQRT(-1)", "SQRT is undefined for the negative argument -1")]
    [InlineData("SQRT(-0.5)", "SQRT is undefined")]
    [InlineData("LN(0)", "LN is undefined for the non-positive argument 0")]
    [InlineData("LN(-1.5)", "LN is undefined")]
    [InlineData("LG(0.0)", "LG is undefined")]
    [InlineData("LG(-10)", "LG is undefined")]
    [InlineData("ASIN(2)", "ASIN is undefined for the argument 2 outside -1..1")]
    [InlineData("ASIN(-1.0001)", "ASIN is undefined")]
    [InlineData("ACOS(-1.5)", "ACOS is undefined")]
    [InlineData("ACOS(1.5)", "ACOS is undefined")]
    [InlineData("ROUND(1.5, 16)", "ROUND precision 16 is outside 0..15")]
    [InlineData("ROUND(1.5, -1)", "ROUND precision -1 is outside 0..15")]
    [InlineData("ROUND(7, 16)", "ROUND precision 16 is outside 0..15")]   // 정수 입력도 자릿수는 검사한다
    public void RuntimeErrorsThrowGenApiException(string text, string expectedFragment)
    {
        var f = Formula.Parse(text);   // 파싱은 통과하고 평가에서 실패해야 한다
        var ex = Assert.Throws<GenApiException>(() => f.Evaluate(NoVars));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void RuntimeErrorCarriesOperatorPositionAndExcerpt()
    {
        var ex = Assert.Throws<GenApiException>(() => Eval("1 + (2 / 0)"));
        Assert.Contains("Division by zero", ex.Message);
        Assert.Contains("position 7", ex.Message);
        Assert.Contains("in formula \"1 + (2 / 0)\"", ex.Message);
    }

    [Fact]
    public void DomainErrorCarriesFunctionPositionAndIsSkippedInUntakenBranch()
    {
        var ex = Assert.Throws<GenApiException>(() => Eval("1 + SQRT(-4)"));
        Assert.Contains("position 4", ex.Message);
        Assert.Equal(2L, Eval("1 ? 2 : SQRT(-4)").AsInt64);
        Assert.Equal(1L, Eval("1 || LN(0)").AsInt64);
    }

    [Theory]
    [InlineData("SQRT(0)", 0.0)]
    [InlineData("SQRT(-0.0)", 0.0)]              // 음의 0 은 음수가 아니다
    [InlineData("LN(1e-300)", -690.7755278982137)]
    [InlineData("ASIN(-1)", -Math.PI / 2)]
    [InlineData("ACOS(-1)", Math.PI)]
    public void DomainBoundariesAreInsideTheDomain(string text, double expected)
    {
        Assert.Equal(expected, Eval(text).AsDouble, 1e-9);
    }

    [Fact]
    public void ParseErrorCarriesPosition()
    {
        var ex = Assert.Throws<GenApiException>(() => Formula.Parse("(1 + 2"));
        Assert.Contains("position 6", ex.Message);
        Assert.Contains("'(' at position 0", ex.Message);
    }

    [Fact]
    public void UnknownVariableInDictionaryThrowsWithPosition()
    {
        var f = Formula.Parse("1 + Foo");
        var ex = Assert.Throws<GenApiException>(() => f.Evaluate(NoVars));
        Assert.Contains("Unknown variable 'Foo'", ex.Message);
        Assert.Contains("position 4", ex.Message);
    }

    [Fact]
    public void MissingVariableInUntakenBranchIsNotAnError()
    {
        Assert.Equal(5L, Formula.Parse("1 ? 5 : Missing").Evaluate(NoVars).AsInt64);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Formula.Parse(null!));
        var f = Formula.Parse("1");
        Assert.Throws<ArgumentNullException>(() => f.Evaluate((Func<string, GenApiValue>)null!));
        Assert.Throws<ArgumentNullException>(() => f.Evaluate((IReadOnlyDictionary<string, GenApiValue>)null!));
        Assert.Throws<ArgumentNullException>(() => { _ = f.EvaluateAsync(null!, TestContext.Current.CancellationToken); });
    }

    // ---- 깊이 제한 ----

    [Fact]
    public void DeepParenthesesAreRejectedWithoutStackOverflow()
    {
        var ok = new string('(', 150) + "1" + new string(')', 150);
        Assert.Equal(1L, Eval(ok).AsInt64);

        var tooDeep = new string('(', 300) + "1" + new string(')', 300);
        var ex = Assert.Throws<GenApiException>(() => Formula.Parse(tooDeep));
        Assert.Contains("nesting exceeds the limit", ex.Message);
    }

    [Fact]
    public void DeepUnaryFunctionTernaryAndPowerChainsAreRejected()
    {
        Assert.Throws<GenApiException>(() => Formula.Parse(new string('-', 300) + "1"));
        Assert.Throws<GenApiException>(() => Formula.Parse(string.Concat(Enumerable.Repeat("ABS(", 300)) + "1" + new string(')', 300)));
        Assert.Throws<GenApiException>(() => Formula.Parse(string.Concat(Enumerable.Repeat("1 ? ", 300)) + "1" + string.Concat(Enumerable.Repeat(" : 0", 300))));
        Assert.Throws<GenApiException>(() => Formula.Parse(string.Join(" ** ", Enumerable.Repeat("1", 300))));
    }

    [Fact]
    public void LongLeftAssociativeChainIsBoundedByTreeDepth()
    {
        Assert.Equal(150L, Eval(string.Join(" + ", Enumerable.Repeat("1", 150))).AsInt64);
        var ex = Assert.Throws<GenApiException>(() => Formula.Parse(string.Join(" + ", Enumerable.Repeat("1", 300))));
        Assert.Contains("nesting exceeds the limit", ex.Message);
    }

    // ---- 변수 ----

    [Theory]
    [InlineData("A + B * A - C", "A,B,C")]
    [InlineData("a + A", "a,A")]                             // 대소문자 구분
    [InlineData("Cam.Width * 2", "Cam.Width")]               // '.' 허용
    [InlineData("_x + x_1", "_x,x_1")]
    [InlineData("PI * R", "R")]                              // 상수는 변수가 아니다
    [InlineData("E + e", "e")]
    [InlineData("ABS(X) + sin(Y)", "X,Y")]                   // 함수 이름은 변수가 아니다
    [InlineData("42", "")]
    [InlineData("FROM > MAX ? MAX : FROM < MIN ? MIN : FROM", "FROM,MAX,MIN")]
    public void VariablesAreDistinctInOrderOfFirstAppearance(string text, string expectedCsv)
    {
        var expected = expectedCsv.Length == 0 ? Array.Empty<string>() : expectedCsv.Split(',');
        var f = Formula.Parse(text);
        Assert.Equal(expected, f.Variables);
        Assert.Equal(expected.Length > 0, f.HasVariables);
    }

    [Fact]
    public void TextIsPreservedVerbatim()
    {
        const string text = "  ( A\r\n\t+ 1 ) ";
        var f = Formula.Parse(text);
        Assert.Equal(text, f.Text);
        Assert.Equal(text, f.ToString());
    }

    [Fact]
    public void VariablesRoundTripThroughResolver()
    {
        var f = Formula.Parse("(W * H + PAD) / DIV");
        Assert.Equal(new[] { "W", "H", "PAD", "DIV" }, f.Variables);

        var values = new Dictionary<string, GenApiValue>(StringComparer.Ordinal);
        long next = 2;
        foreach (var name in f.Variables) values[name] = next++;   // W=2, H=3, PAD=4, DIV=5 → (6 + 4) / 5

        Assert.Equal(2L, f.Evaluate(values).AsInt64);
        Assert.Equal(2L, f.Evaluate(name => values[name]).AsInt64);
    }

    [Fact]
    public void SyncEvaluateResolvesEachVariableOnceAndOnlyTheTakenBranch()
    {
        var f = Formula.Parse("SEL ? A + A * A : B");
        var calls = new List<string>();
        GenApiValue Resolve(string name)
        {
            calls.Add(name);
            return name switch
            {
                "SEL" => 1,
                "A" => 3,
                _ => throw new InvalidOperationException("B must not be resolved"),
            };
        }

        Assert.Equal(12L, f.Evaluate(Resolve).AsInt64);
        Assert.Equal(new[] { "SEL", "A" }, calls);
    }

    [Fact]
    public void ResolverExceptionPropagatesUnchanged()
    {
        var f = Formula.Parse("X + 1");
        var ex = Assert.Throws<InvalidOperationException>(() => f.Evaluate(_ => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", ex.Message);
    }

    // ---- 실제 XML 에 나오는 모양의 수식 ----

    [Theory]
    [InlineData(125L, 1000L, 125L)]
    [InlineData(125L, 3L, 41666L)]
    public void TickConversionFormula(long from, long tickFreq, long expected)
    {
        var v = Eval("(FROM * 1000) / TickFreq", ("FROM", from), ("TickFreq", tickFreq));
        Assert.True(v.IsInteger);
        Assert.Equal(expected, v.AsInt64);
    }

    [Fact]
    public void RegisterPackingFormula()
    {
        var v = Eval("(VAR1 & 0xFF) << 8 | VAR2", ("VAR1", 0x1234L), ("VAR2", 0x56L));
        Assert.Equal(0x3456L, v.AsInt64);
    }

    [Theory]
    [InlineData(50L, 40L)]
    [InlineData(5L, 10L)]
    [InlineData(20L, 20L)]
    public void ClampFormula(long from, long expected)
    {
        var v = Eval("FROM > MAX ? MAX : FROM < MIN ? MIN : FROM", ("FROM", from), ("MAX", 40L), ("MIN", 10L));
        Assert.Equal(expected, v.AsInt64);
    }

    // 벤더 XML 의 Gain 변환기 모양 — LG 와 두 인자 ROUND(x, 0) 를 함께 쓴다. FROM 은 Float 노드 값이라 실수.
    [Theory]
    [InlineData(0L, 10.0, 200.0)]                // 0.1 dB 단위: 200 * log10(10) = 200
    [InlineData(0L, 2.0, 60.0)]                  // 200 * log10(2) = 60.2… → 60
    [InlineData(0L, 4.0, 120.0)]                 // 120.41… → 120
    [InlineData(1L, 2.0, 128.0)]                 // 다른 셀렉터: (FROM - 1) * 128
    [InlineData(5L, 3.0, 300.0)]                 // 셀렉터 5: FROM * 100
    public void GainConverterFormulaToWithTwoArgumentRound(long selector, double from, double expected)
    {
        const string text = "(GAIN_SELECTOR=0) ? (ROUND(200 * LG(FROM), 0)) : ((GAIN_SELECTOR<>5)? (FROM - 1) * 128: FROM * 100)";
        var f = Formula.Parse(text);
        Assert.Equal(new[] { "GAIN_SELECTOR", "FROM" }, f.Variables);

        var v = Eval(text, ("GAIN_SELECTOR", selector), ("FROM", from));
        Assert.True(v.IsDouble, $"expected a double result, got {v}");
        Assert.Equal(expected, v.AsDouble, 1e-9);
    }

    [Fact]
    public void GainConverterRoundTripsThroughFormulaToAndFormulaFrom()
    {
        var reg = Eval("(ROUND(200 * LG(FROM), 0))", ("FROM", 4.0));
        Assert.Equal(120.0, reg.AsDouble, 1e-9);

        var back = Eval("(10 ** (TO / 200))", ("TO", reg));   // 10 ** 0.6 = 3.98…
        Assert.True(back.IsDouble);
        Assert.Equal(Math.Pow(10, 0.6), back.AsDouble, 1e-12);

        // 이득 0 은 로그의 정의역 밖 — NaN·-무한대가 아니라 위치를 담은 예외
        var ex = Assert.Throws<GenApiException>(() => Eval("(ROUND(200 * LG(FROM), 0))", ("FROM", 0.0)));
        Assert.Contains("LG is undefined", ex.Message);
        Assert.Contains("position 13", ex.Message);
    }

    [Fact]
    public void GainConverterFormulaFromWithIntegerRegister()
    {
        // 위 왕복의 TO 는 실수 120.0 이었다 — 실제로는 정수 레지스터 값이 온다. 정수 규칙이면 120/200 = 0 → 1.
        Assert.Equal(1L, Eval("(10 ** (TO / 200))", ("TO", 120L)).AsInt64);
        var back = EvalReal("(10 ** (TO / 200))", ("TO", 120L));
        Assert.Equal(Math.Pow(10, 0.6), back.AsDouble, 1e-12);
    }

    // ---- 실수 규칙(실수 노드의 수식) ----

    [Theory]
    [InlineData("7 / 2", 3.5)]
    [InlineData("-7 / 2", -3.5)]
    [InlineData("1 / 3 * 3", 1.0)]
    [InlineData("10 ** ((60 / 10) / 20)", 1.9952623149688795)]
    [InlineData("2 ** -1", 0.5)]
    public void RealModeDividesIntegersAsReals(string text, double expected)
    {
        var v = EvalReal(text);
        Assert.True(v.IsDouble || v.AsDouble == expected, $"{text} = {v}");
        Assert.Equal(expected, v.AsDouble, 15);
    }

    [Fact]
    public void RealModeKeepsExactIntegerArithmeticUntilItOverflows()
    {
        // 정수끼리의 + - * ** 는 정확하므로 정수로 남는다 — 비교·비트 연산이 뒤따라도 값이 흐트러지지 않는다
        var exact = EvalReal("A * B + 1", ("A", 3L), ("B", 4L));
        Assert.True(exact.IsInteger);
        Assert.Equal(13L, exact.AsInt64);

        // 넘치면 예외 대신 실수로 계속한다. 대조군: 정수 규칙은 같은 식에서 넘침 예외.
        Assert.Equal(Math.Pow(2, 70), EvalReal("2 ** 70").AsDouble);
        Assert.Equal(4294967296.0 * 4294967296.0, EvalReal("A * A", ("A", 4294967296L)).AsDouble);
        Assert.Equal(9223372036854775807.0 + 1.0, EvalReal("A + 1", ("A", long.MaxValue)).AsDouble);
        Assert.Equal(9223372036854775808.0, EvalReal("-A", ("A", long.MinValue)).AsDouble);
        Assert.Equal(9223372036854775808.0, EvalReal("ABS(A)", ("A", long.MinValue)).AsDouble);
        Assert.Contains("overflow", Assert.Throws<GenApiException>(() => Eval("2 ** 70")).Message);
        Assert.Contains("overflow", Assert.Throws<GenApiException>(() => Eval("A * A", ("A", 4294967296L))).Message);
    }

    [Theory]
    [InlineData("(N / 2) & 1", 6L, 1L)]                 // 3.0 → 3
    [InlineData("(N / 2) & 1", 5L, 0L)]                 // 2.5 → 0 방향 절삭 2 — 정수 나눗셈 뒤 비트 연산과 같은 결과
    [InlineData("(N / 2) | 0", 7L, 3L)]
    [InlineData("(N / 4) << 1", 10L, 4L)]               // 2.5 → 2 → 4
    [InlineData("1 << (N / 2)", 9L, 16L)]               // 시프트 수도 같은 규칙: 4.5 → 4
    [InlineData("~(N / 2)", 4L, -3L)]
    [InlineData("(N / 2) & 1", -7L, 1L)]                // 음수도 같다: -3.5 → -3
    public void RealModeBitOperatorsTruncateRealOperands(string text, long n, long expected)
    {
        var v = EvalReal(text, ("N", n));
        Assert.True(v.IsInteger);
        Assert.Equal(expected, v.AsInt64);
        // 대조군: 정수 규칙은 나눗셈이 이미 정수라 같은 답이 나온다(두 규칙 모두 0 방향 절삭이라 부호와 무관 — 2^53 이하에서)
        Assert.Equal(expected, Eval(text, ("N", n)).AsInt64);
    }

    [Fact]
    public void RealModeBitOperatorRejectsValuesThatCannotBeIntegers()
    {
        var nan = Assert.Throws<GenApiException>(() => EvalReal("A & 1", ("A", double.NaN)));
        Assert.Contains("NaN", nan.Message);
        var big = Assert.Throws<GenApiException>(() => EvalReal("A | 0", ("A", 1e30)));
        Assert.Contains("64-bit", big.Message);
        // 정수 규칙은 실수 피연산자를 비트 연산에 받지 않는다(그대로)
        Assert.Contains("requires integer", Assert.Throws<GenApiException>(() => Eval("A & 1", ("A", 2.0))).Message);
    }

    [Theory]
    [InlineData("(N / 2) % 2", 7L, 1L)]                 // 3.5 → 3, 3 % 2 = 1 — 정수 나눗셈 뒤 나머지와 같다
    [InlineData("(N / 2) % 2", -7L, -1L)]
    [InlineData("N % (7 / 2)", 10L, 1L)]                // 제수도 같은 규칙: 3.5 → 3
    public void RealModeRemainderTruncatesRealOperandsLikeBitOperators(string text, long n, long expected)
    {
        var v = EvalReal(text, ("N", n));
        Assert.True(v.IsInteger, $"{text} = {v}");
        Assert.Equal(expected, v.AsInt64);
        Assert.Equal(expected, Eval(text, ("N", n)).AsInt64);   // 대조군: 정수 규칙과 같은 답
    }

    [Fact]
    public void IntegerModeRemainderOfRealsStaysFmod()
    {
        // 공개 규칙(정수 규칙)의 실수 나머지는 그대로다
        Assert.Equal(1.5, Eval("7.5 % 2").AsDouble);
        Assert.Contains("NaN", Assert.Throws<GenApiException>(() => EvalReal("A % 2", ("A", double.NaN))).Message);
    }

    [Theory]
    [InlineData("B ** (X / 2)", 0L, -2L, "zero")]       // 0 ** -1.0 — 지수가 나눗셈에서 실수로 와도 0 나눗셈이다
    [InlineData("B ** (1 / X)", -8L, 3L, "undefined")]  // (-8) ** (1/3) — 음수의 분수 거듭제곱은 실수가 아니다
    public void RealModePowerRejectsZeroBaseNegativeExponentAndNaN(string text, long b, long x, string expected)
    {
        // (변수 이름에 E 를 쓰지 않는다 — E 는 상수 e 다)
        var ex = Assert.Throws<GenApiException>(() => EvalReal(text, ("B", b), ("X", x)));
        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerOfRealOperandsNeverReturnsNaNOrDivisionByZero()
    {
        // 정수 규칙에서도 실수 피연산자의 거듭제곱은 무한대·NaN 을 값으로 흘리지 않는다
        Assert.Contains("zero", Assert.Throws<GenApiException>(() => Eval("0.0 ** -1")).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("undefined", Assert.Throws<GenApiException>(() => Eval("(-4.0) ** 0.5")).Message);
        Assert.Equal(0.25, Eval("0.5 ** 2").AsDouble);  // 대조군: 정상 값은 그대로
        Assert.Equal(1.0, Eval("0.0 ** 0").AsDouble);
    }

    [Fact]
    public void RealModeStillRejectsDivisionByZero()
    {
        Assert.Contains("zero", Assert.Throws<GenApiException>(() => EvalReal("1 / A", ("A", 0L))).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zero", Assert.Throws<GenApiException>(() => EvalReal("0 ** -1")).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConverterFormulasPromoteAndTruncate()
    {
        var toHost = Eval("FROM / 1000000.0", ("FROM", 20000L));
        Assert.True(toHost.IsDouble);
        Assert.Equal(0.02, toHost.AsDouble, 1e-12);

        var toDevice = Eval("TRUNC(TO * 1000000)", ("TO", 0.02));
        Assert.True(toDevice.IsDouble);
        Assert.Equal(20000L, toDevice.AsInt64);
    }

    [Fact]
    public void WhitespaceAndNewlinesAreIgnored()
    {
        var v = Eval("(\r\n\tA\r\n\t+ B\r\n)\n* 2", ("A", 1L), ("B", 2L));
        Assert.Equal(6L, v.AsInt64);
    }

    // ---- 비동기 ----

    [Fact]
    public async Task EvaluateAsyncResolvesEveryVariableOnceInOrderThenEvaluates()
    {
        var f = Formula.Parse("B ? A : C + A");
        var calls = new List<string>();
        ValueTask<GenApiValue> Resolve(string name)
        {
            calls.Add(name);
            GenApiValue value = name switch
            {
                "A" => 7,
                "B" => 1,
                "C" => 100,
                _ => throw new InvalidOperationException(name),
            };
            return new ValueTask<GenApiValue>(value);
        }

        var v = await f.EvaluateAsync(Resolve, TestContext.Current.CancellationToken);
        Assert.Equal(7L, v.AsInt64);
        Assert.Equal(new[] { "B", "A", "C" }, calls);   // 비동기는 택하지 않은 가지의 변수도 미리 한 번씩 해석한다
    }

    [Fact]
    public async Task EvaluateAsyncWorksWithTrulyAsynchronousResolver()
    {
        var f = Formula.Parse("X * 2");
        async ValueTask<GenApiValue> Resolve(string name)
        {
            await Task.Yield();
            return 21;
        }

        Assert.Equal(42L, (await f.EvaluateAsync(Resolve, TestContext.Current.CancellationToken)).AsInt64);
    }

    [Fact]
    public async Task EvaluateAsyncHonoursCancellation()
    {
        var f = Formula.Parse("X + 1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await f.EvaluateAsync(_ => { calls++; return new ValueTask<GenApiValue>(GenApiValue.One); }, cts.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EvaluateAsyncPropagatesResolverAndFormulaErrors()
    {
        var f = Formula.Parse("X / 0");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await f.EvaluateAsync(_ => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<GenApiException>(async () =>
            await f.EvaluateAsync(_ => new ValueTask<GenApiValue>(GenApiValue.One), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EvaluateAsyncWithoutVariablesCompletesSynchronously()
    {
        var task = Formula.Parse("6 * 7").EvaluateAsync(_ => throw new InvalidOperationException("must not be called"), TestContext.Current.CancellationToken);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(42L, (await task).AsInt64);
    }
}
