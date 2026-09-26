using System.Globalization;
using System.Text;

namespace GevSharp.GenApi;

/// <summary>
/// 수식 오류의 자리 — 수식 본문과 0 기준 문자 인덱스. 본문이 null 이면(값 연산자를 직접 부른 경로) 위치 없는 메시지를 만든다.
/// </summary>
internal readonly struct FormulaErrSite
{
    public readonly string? Text;
    public readonly int Pos;

    public FormulaErrSite(string? text, int pos)
    {
        Text = text;
        Pos = pos;
    }

    public GenApiException Fail(string message) => new(FormulaOps.Describe(message, Text, Pos));
}

/// <summary>
/// 수식 평가 규칙 — 어느 노드의 수식이냐가 정한다. 값의 종류(정수/실수)는 두 규칙 모두 피연산자에서 오고, 갈리는 것은
/// 정수끼리의 연산이 정수 결과를 고집하느냐다.
/// </summary>
internal enum FormulaMode
{
    /// <summary>
    /// 정수 노드(IntSwissKnife·IntConverter·주소 수식)와 공개 <see cref="Formula.Evaluate(Func{string, GenApiValue})"/>:
    /// 정수끼리는 정수(<c>/</c> 는 0 방향 절삭, 넘침은 예외), 실수가 섞이면 실수, 실수의 비트 연산은 예외.
    /// </summary>
    Integer,

    /// <summary>
    /// 실수 노드(SwissKnife·Converter): 값이 실수이므로 <c>/</c> 는 정수끼리여도 실수 결과를 낸다(10/4 = 2.5).
    /// 정수끼리의 <c>+ - * **</c>(0 이상 지수)는 정확하므로 정수로 두되 넘치면 예외 대신 실수로 계속한다. 비트·시프트·나머지(<c>%</c>)는
    /// 정수 연산이라 실수 피연산자를 0 방향으로 잘라 정수로 바꾼다 — 나눗셈이 실수가 되면서 <c>(N / 2) &amp; 1</c>·<c>(N / 2) % 2</c> 같은 식이
    /// 예외로 깨지거나 분수가 되지 않게 하려는 것이고, 정수 나눗셈 뒤 그 연산을 하던 결과와 같다(부호와 무관하게, 피연산자가 2^53 이하일 때 —
    /// 그보다 크면 실수 변환에서 이미 반올림된다).
    /// </summary>
    Real,
}

/// <summary>
/// 수식 연산의 실제 의미 — 정수/실수 승격 규칙과 오류 판정을 한 곳에 모은다.
/// <see cref="GenApiValue"/> 의 연산자와 수식 트리 평가가 모두 여기를 거친다.
/// 기본(<see cref="FormulaMode.Integer"/>)은 정수끼리는 정수(오버플로는 예외), 한쪽이라도 실수면 실수. 0 나눗셈은 정수·실수 모두 예외.
/// <c>isReal</c> 이 참이면 <see cref="FormulaMode.Real"/> 규칙이다.
/// 초월함수의 정의역 밖 인자(SQRT(-1), LN(0), ACOS(2) 등)도 예외 — NaN·무한대를 값으로 흘리지 않는다.
/// </summary>
internal static class FormulaOps
{
    // ---- 오류 메시지 ----

    public static string Describe(string message, string? text, int pos)
    {
        if (text is null) return message + ".";
        return message + " at position " + pos.ToString(CultureInfo.InvariantCulture) + " in formula \"" + Excerpt(text) + "\".";
    }

    /// <summary>메시지에 싣는 수식 발췌 — 공백 연속을 한 칸으로 접고 길이를 제한한다(위치 인덱스는 원문 기준).</summary>
    private static string Excerpt(string text)
    {
        const int MaxLen = 120;
        var sb = new StringBuilder(Math.Min(text.Length, MaxLen + 3));
        bool wasSpace = false;
        foreach (char c in text)
        {
            if (sb.Length >= MaxLen)
            {
                sb.Append("...");
                break;
            }
            if (char.IsWhiteSpace(c))
            {
                if (!wasSpace) sb.Append(' ');
                wasSpace = true;
            }
            else
            {
                sb.Append(c);
                wasSpace = false;
            }
        }
        return sb.ToString();
    }

    // ---- 변환 ----

    /// <summary>실수 → 정수. 0 방향 절삭. NaN·무한대·범위 밖은 예외.</summary>
    public static long ToInt64(double value, FormulaErrSite site)
    {
        if (double.IsNaN(value)) throw site.Fail("Cannot convert NaN to an integer");
        double t = Math.Truncate(value);
        // 2^63 은 double 로 정확히 표현되므로 경계 비교가 정확하다. 무한대도 여기서 걸린다.
        if (t >= 9223372036854775808.0 || t < -9223372036854775808.0)
            throw site.Fail("Value " + value.ToString("R", CultureInfo.InvariantCulture) + " is outside the 64-bit integer range");
        return (long)t;
    }

    // ---- 산술 — 정수 넘침은 정수 규칙에서 예외, 실수 규칙에서는 실수로 계속한다 ----

    public static GenApiValue Add(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        if (a.IsInteger && b.IsInteger)
        {
            try { return checked(a.AsInt64 + b.AsInt64); }
            catch (OverflowException) when (isReal) { }
            catch (OverflowException) { throw site.Fail("Integer overflow in '+'"); }
        }
        return a.AsDouble + b.AsDouble;
    }

    public static GenApiValue Subtract(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        if (a.IsInteger && b.IsInteger)
        {
            try { return checked(a.AsInt64 - b.AsInt64); }
            catch (OverflowException) when (isReal) { }
            catch (OverflowException) { throw site.Fail("Integer overflow in '-'"); }
        }
        return a.AsDouble - b.AsDouble;
    }

    public static GenApiValue Multiply(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        if (a.IsInteger && b.IsInteger)
        {
            try { return checked(a.AsInt64 * b.AsInt64); }
            catch (OverflowException) when (isReal) { }
            catch (OverflowException) { throw site.Fail("Integer overflow in '*'"); }
        }
        return a.AsDouble * b.AsDouble;
    }

    /// <summary>나눗셈 — 정수 규칙에서 정수끼리는 0 방향 절삭, 실수 규칙에서는 항상 실수 나눗셈. 0 으로 나누면 어느 쪽이든 예외.</summary>
    public static GenApiValue Divide(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        if (a.IsInteger && b.IsInteger && !isReal)
        {
            long x = a.AsInt64, y = b.AsInt64;
            if (y == 0) throw site.Fail("Division by zero");
            if (x == long.MinValue && y == -1) throw site.Fail("Integer overflow in '/'");
            return x / y;
        }
        double d = b.AsDouble;
        if (d == 0.0) throw site.Fail("Division by zero");
        return a.AsDouble / d;
    }

    /// <summary>
    /// 나머지 — 정수는 피제수 부호를 따른다. 실수 피연산자는 정수 규칙에서 절삭 나눗셈 기반 나머지(C 의 fmod), 실수 규칙에서는
    /// 비트 연산처럼 0 방향으로 잘라 정수 나머지를 낸다 — 나눗셈이 실수가 되면서 <c>(N / 2) % 2</c> 가 1.5 같은 값으로 바뀌지 않게 한다
    /// (정수 나눗셈 뒤 나머지와 같은 답). NaN·64비트 범위 밖은 자르지 않고 예외.
    /// </summary>
    public static GenApiValue Modulo(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        if (a.IsInteger && b.IsInteger) return IntRemainder(a.AsInt64, b.AsInt64, site);
        if (isReal) return IntRemainder(BitOperand(a, "%", true, site), BitOperand(b, "%", true, site), site);
        double d = b.AsDouble;
        if (d == 0.0) throw site.Fail("Division by zero in '%'");
        return a.AsDouble % d;
    }

    private static GenApiValue IntRemainder(long x, long y, FormulaErrSite site)
    {
        if (y == 0) throw site.Fail("Division by zero in '%'");
        if (y == -1) return 0L;   // MinValue % -1 의 오버플로 회피 — 수학적으로 0
        return x % y;
    }

    /// <summary>
    /// 거듭제곱. 정수 밑·0 이상 정수 지수는 정확한 정수(넘침은 정수 규칙에서 예외, 실수 규칙에서 실수); 음수 지수나 실수 피연산자는 실수.
    /// 정수 결과는 같은 값의 실수 결과와 같으므로(넘치지 않는 한) 실수 규칙에서도 정확한 정수를 먼저 쓴다.
    /// 0 의 음수 거듭제곱은 정수·실수 모두 0 나눗셈 예외, 실수 결과가 정의되지 않으면(음수의 분수 거듭제곱) NaN 대신 예외 —
    /// 실수 규칙에서는 지수가 나눗셈에서 실수로 오므로 정수 지수만 막아서는 새어 나간다. 실수 범위를 넘는 크기는 무한대로 남는다
    /// (다른 실수 산술과 같다; Converter 한계 계산은 그 끝을 "한계 없음" 으로 읽는다).
    /// </summary>
    public static GenApiValue Pow(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        if (a.IsInteger && b.IsInteger)
        {
            long x = a.AsInt64, y = b.AsInt64;
            if (y < 0)
            {
                if (x == 0) throw site.Fail("Division by zero in '**'");
                return Math.Pow(x, y);
            }
            try { return IntPow(x, y); }
            catch (OverflowException) when (isReal) { return Math.Pow(x, y); }
            catch (OverflowException) { throw site.Fail("Integer overflow in '**'"); }
        }
        double xb = a.AsDouble, yb = b.AsDouble;
        if (xb == 0.0 && yb < 0) throw site.Fail("Division by zero in '**'");
        var r = Math.Pow(xb, yb);
        if (double.IsNaN(r) && !double.IsNaN(xb) && !double.IsNaN(yb))
            throw site.Fail("'**' is undefined for the base " + Fmt(xb) + " and the exponent " + Fmt(yb));
        return r;
    }

    /// <summary>제곱 반복으로 정확한 정수 거듭제곱. 오버플로는 OverflowException 으로 나온다.</summary>
    private static long IntPow(long b, long e)
    {
        long result = 1;
        while (e > 0)
        {
            if ((e & 1) != 0) result = checked(result * b);
            e >>= 1;
            if (e > 0) b = checked(b * b);
        }
        return result;
    }

    public static GenApiValue Negate(GenApiValue a, FormulaErrSite site, bool isReal = false)
    {
        if (a.IsInteger)
        {
            long x = a.AsInt64;
            if (x != long.MinValue) return -x;
            if (!isReal) throw site.Fail("Integer overflow in unary '-'");
        }
        return -a.AsDouble;
    }

    // ---- 비트 연산: 정수 연산 — 정수 규칙은 실수 피연산자를 거절하고, 실수 규칙은 0 방향으로 잘라 정수로 바꾼다 ----

    /// <summary>
    /// 비트 연산의 피연산자를 정수로. 정수는 그대로, 실수는 정수 규칙이면 예외, 실수 규칙이면 0 방향 절삭
    /// (NaN·무한대·64비트 범위 밖은 예외 — 자를 수 없는 값을 0 으로 흘리지 않는다).
    /// </summary>
    private static long BitOperand(GenApiValue v, string op, bool isReal, FormulaErrSite site)
    {
        if (v.IsInteger) return v.AsInt64;
        if (!isReal) throw site.Fail("Operator '" + op + "' requires integer operands");
        return ToInt64(v.AsDouble, site);
    }

    public static GenApiValue BitAnd(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        long x = BitOperand(a, "&", isReal, site), y = BitOperand(b, "&", isReal, site);
        return x & y;
    }

    public static GenApiValue BitOr(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        long x = BitOperand(a, "|", isReal, site), y = BitOperand(b, "|", isReal, site);
        return x | y;
    }

    public static GenApiValue BitXor(GenApiValue a, GenApiValue b, FormulaErrSite site, bool isReal = false)
    {
        long x = BitOperand(a, "^", isReal, site), y = BitOperand(b, "^", isReal, site);
        return x ^ y;
    }

    public static GenApiValue BitNot(GenApiValue a, FormulaErrSite site, bool isReal = false)
    {
        if (!a.IsInteger && !isReal) throw site.Fail("Operator '~' requires an integer operand");
        return ~BitOperand(a, "~", isReal, site);
    }

    private static int ShiftCount(long n, string op, FormulaErrSite site)
    {
        if (n < 0 || n > 63)
            throw site.Fail("Shift count " + n.ToString(CultureInfo.InvariantCulture) + " in '" + op + "' is outside 0..63");
        return (int)n;
    }

    public static GenApiValue ShiftLeft(GenApiValue a, GenApiValue count, FormulaErrSite site, bool isReal = false)
    {
        long x = BitOperand(a, "<<", isReal, site), n = BitOperand(count, "<<", isReal, site);
        return x << ShiftCount(n, "<<", site);
    }

    /// <summary>산술(부호 유지) 오른쪽 시프트.</summary>
    public static GenApiValue ShiftRight(GenApiValue a, GenApiValue count, FormulaErrSite site, bool isReal = false)
    {
        long x = BitOperand(a, ">>", isReal, site), n = BitOperand(count, ">>", isReal, site);
        return x >> ShiftCount(n, ">>", site);
    }

    // ---- 비교: 정수끼리는 정수 비교, 아니면 실수 비교(IEEE — NaN 은 모든 순서 비교에서 거짓). 결과는 정수 1/0. ----

    public static GenApiValue Compare(FormulaBinOp op, GenApiValue a, GenApiValue b)
    {
        bool r;
        if (a.IsInteger && b.IsInteger)
        {
            long x = a.AsInt64, y = b.AsInt64;
            r = op switch
            {
                FormulaBinOp.Eq => x == y,
                FormulaBinOp.Ne => x != y,
                FormulaBinOp.Lt => x < y,
                FormulaBinOp.Le => x <= y,
                FormulaBinOp.Gt => x > y,
                FormulaBinOp.Ge => x >= y,
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
        }
        else
        {
            double x = a.AsDouble, y = b.AsDouble;
            r = op switch
            {
                FormulaBinOp.Eq => x == y,
                FormulaBinOp.Ne => x != y,
                FormulaBinOp.Lt => x < y,
                FormulaBinOp.Le => x <= y,
                FormulaBinOp.Gt => x > y,
                FormulaBinOp.Ge => x >= y,
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
        }
        return GenApiValue.FromBoolean(r);
    }

    // ---- 함수 ----

    /// <summary>
    /// 단항 함수. ABS·NEG·TRUNC·FLOOR·CEIL·ROUND 는 정수 입력을 정수로 돌려주고, SGN 은 항상 정수 -1/0/1,
    /// 나머지(삼각·지수·로그·제곱근)는 항상 실수. 정의역 밖 인자(SQRT 의 음수, LN·LG 의 0 이하, ASIN·ACOS 의 -1..1 밖)는
    /// NaN·무한대 대신 위치를 담은 예외.
    /// </summary>
    public static GenApiValue Call(FormulaFunc func, GenApiValue x, FormulaErrSite site, bool isReal = false)
    {
        switch (func)
        {
            case FormulaFunc.Sin: return Math.Sin(x.AsDouble);
            case FormulaFunc.Cos: return Math.Cos(x.AsDouble);
            case FormulaFunc.Tan: return Math.Tan(x.AsDouble);
            case FormulaFunc.Asin: return Math.Asin(RequireUnitRange(x, "ASIN", site));
            case FormulaFunc.Acos: return Math.Acos(RequireUnitRange(x, "ACOS", site));
            case FormulaFunc.Atan: return Math.Atan(x.AsDouble);
            case FormulaFunc.Exp: return Math.Exp(x.AsDouble);
            case FormulaFunc.Ln: return Math.Log(RequirePositive(x, "LN", site));
            case FormulaFunc.Lg: return Math.Log10(RequirePositive(x, "LG", site));
            case FormulaFunc.Sqrt:
            {
                double d = x.AsDouble;
                if (d < 0) throw site.Fail("SQRT is undefined for the negative argument " + Fmt(d));
                return Math.Sqrt(d);
            }

            case FormulaFunc.Abs:
                if (x.IsInteger)
                {
                    long v = x.AsInt64;
                    if (v != long.MinValue) return v < 0 ? -v : v;
                    if (!isReal) throw site.Fail("Integer overflow in ABS");
                }
                return Math.Abs(x.AsDouble);

            case FormulaFunc.Neg:
                return Negate(x, site, isReal);

            case FormulaFunc.Sgn:
                if (x.IsInteger) return (long)Math.Sign(x.AsInt64);
                {
                    double d = x.AsDouble;
                    if (double.IsNaN(d)) throw site.Fail("SGN is undefined for NaN");
                    return d < 0 ? -1L : d > 0 ? 1L : 0L;
                }

            case FormulaFunc.Trunc: return x.IsInteger ? x : Math.Truncate(x.AsDouble);
            case FormulaFunc.Floor: return x.IsInteger ? x : Math.Floor(x.AsDouble);
            case FormulaFunc.Ceil: return x.IsInteger ? x : Math.Ceiling(x.AsDouble);
            // 반올림은 0 에서 먼 쪽(2.5 → 3, -2.5 → -3)
            case FormulaFunc.Round: return x.IsInteger ? x : Math.Round(x.AsDouble, MidpointRounding.AwayFromZero);

            default:
                throw new ArgumentOutOfRangeException(nameof(func));
        }
    }

    /// <summary>
    /// 두 인자 함수 — ROUND(x, digits) 만 해당. digits 는 소수 자릿수 0..15(실수면 0 방향 절삭, 범위 밖은 예외).
    /// 정수 x 는 그대로(소수 자리를 늘려도 정수는 변하지 않는다), 실수 x 는 그 자릿수에서 0 에서 먼 쪽으로 반올림한 실수.
    /// </summary>
    public static GenApiValue Call(FormulaFunc func, GenApiValue x, GenApiValue y, FormulaErrSite site)
    {
        if (func != FormulaFunc.Round) throw new ArgumentOutOfRangeException(nameof(func));

        long digits = y.IsInteger ? y.AsInt64 : ToInt64(y.AsDouble, site);
        if (digits < 0 || digits > 15)
            throw site.Fail("ROUND precision " + digits.ToString(CultureInfo.InvariantCulture) + " is outside 0..15");
        if (x.IsInteger) return x;
        return Math.Round(x.AsDouble, (int)digits, MidpointRounding.AwayFromZero);
    }

    // ---- 정의역 검사: 밖이면 NaN·무한대 대신 위치를 담은 예외. NaN 입력은 검사에 걸리지 않고 그대로 흘러간다. ----

    private static string Fmt(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>로그의 인자는 양수여야 한다 — 0 은 -무한대, 음수는 NaN 이 되므로 둘 다 예외.</summary>
    private static double RequirePositive(GenApiValue x, string func, FormulaErrSite site)
    {
        double d = x.AsDouble;
        if (d <= 0) throw site.Fail(func + " is undefined for the non-positive argument " + Fmt(d));
        return d;
    }

    /// <summary>역삼각함수(ASIN·ACOS)의 인자는 -1..1 이어야 한다 — 밖은 NaN 이 되므로 예외.</summary>
    private static double RequireUnitRange(GenApiValue x, string func, FormulaErrSite site)
    {
        double d = x.AsDouble;
        if (d < -1 || d > 1) throw site.Fail(func + " is undefined for the argument " + Fmt(d) + " outside -1..1");
        return d;
    }
}
