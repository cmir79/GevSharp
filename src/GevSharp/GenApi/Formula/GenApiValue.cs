using System.Globalization;

namespace GevSharp.GenApi;

/// <summary>
/// GenApi 수식이 다루는 값 — 64비트 정수 또는 배정도 실수 중 하나를 담는 불변 합집합.
/// 정수끼리의 산술은 정수로 남고(나눗셈은 0 방향 절삭), 한쪽이라도 실수면 실수로 승격된다.
/// 비교·논리 연산의 결과는 정수 1/0 이다. 0 나눗셈·정수 오버플로·실수 피연산자의 비트 연산은
/// <see cref="GenApiException"/> 을 던진다 — 오류를 0 으로 흘리지 않는다. <c>default</c> 는 정수 0 이다.
/// </summary>
public readonly struct GenApiValue : IEquatable<GenApiValue>
{
    private readonly long _int;
    private readonly double _dbl;
    private readonly bool _isDouble;

    public static readonly GenApiValue Zero = new(0L);
    public static readonly GenApiValue One = new(1L);

    public GenApiValue(long value)
    {
        _int = value;
        _dbl = 0;
        _isDouble = false;
    }

    public GenApiValue(double value)
    {
        _int = 0;
        _dbl = value;
        _isDouble = true;
    }

    public static GenApiValue FromInt64(long value) => new(value);
    public static GenApiValue FromDouble(double value) => new(value);

    /// <summary>참이면 정수 1, 거짓이면 정수 0 — 비교·논리 연산 결과의 규약.</summary>
    public static GenApiValue FromBoolean(bool value) => value ? One : Zero;

    public bool IsInteger => !_isDouble;
    public bool IsDouble => _isDouble;

    /// <summary>정수 값. 실수는 0 방향으로 잘라 정수화한다 — NaN·무한대·64비트 범위 밖은 <see cref="GenApiException"/>.</summary>
    public long AsInt64 => _isDouble ? FormulaOps.ToInt64(_dbl, default) : _int;

    /// <summary>실수 값. 정수는 그대로 변환한다(2^53 을 넘는 정수는 정밀도를 잃을 수 있다).</summary>
    public double AsDouble => _isDouble ? _dbl : _int;

    /// <summary>0 이 아니면 참 — 논리 연산과 삼항 조건의 진릿값. NaN 은 0 과 다르므로 참이다.</summary>
    public bool IsNonZero => _isDouble ? _dbl != 0.0 : _int != 0;

    public static implicit operator GenApiValue(long value) => new(value);
    public static implicit operator GenApiValue(int value) => new((long)value);
    public static implicit operator GenApiValue(double value) => new(value);

    // ---- 산술 — 수식 엔진과 같은 승격·오류 규칙(위치 정보 없는 메시지) ----

    public static GenApiValue operator +(GenApiValue a, GenApiValue b) => FormulaOps.Add(a, b, default);
    public static GenApiValue operator -(GenApiValue a, GenApiValue b) => FormulaOps.Subtract(a, b, default);
    public static GenApiValue operator *(GenApiValue a, GenApiValue b) => FormulaOps.Multiply(a, b, default);
    public static GenApiValue operator /(GenApiValue a, GenApiValue b) => FormulaOps.Divide(a, b, default);
    public static GenApiValue operator %(GenApiValue a, GenApiValue b) => FormulaOps.Modulo(a, b, default);
    public static GenApiValue operator -(GenApiValue a) => FormulaOps.Negate(a, default);

    /// <summary>거듭제곱. 정수 밑·0 이상 정수 지수는 정수(오버플로는 예외), 음수 지수나 실수 피연산자는 실수.</summary>
    public static GenApiValue Pow(GenApiValue a, GenApiValue b) => FormulaOps.Pow(a, b, default);

    /// <summary>비트 AND — 두 피연산자 모두 정수여야 한다.</summary>
    public static GenApiValue BitAnd(GenApiValue a, GenApiValue b) => FormulaOps.BitAnd(a, b, default);
    public static GenApiValue BitOr(GenApiValue a, GenApiValue b) => FormulaOps.BitOr(a, b, default);
    public static GenApiValue BitXor(GenApiValue a, GenApiValue b) => FormulaOps.BitXor(a, b, default);
    public static GenApiValue BitNot(GenApiValue a) => FormulaOps.BitNot(a, default);

    /// <summary>산술 시프트 — 정수 전용, 시프트 수는 0..63.</summary>
    public static GenApiValue ShiftLeft(GenApiValue a, GenApiValue count) => FormulaOps.ShiftLeft(a, count, default);
    public static GenApiValue ShiftRight(GenApiValue a, GenApiValue count) => FormulaOps.ShiftRight(a, count, default);

    /// <summary>논리 부정 — 0 이면 1, 아니면 0.</summary>
    public static GenApiValue LogicalNot(GenApiValue a) => FromBoolean(!a.IsNonZero);

    // ---- 동등성: 종류와 값이 모두 같아야 한다(정수 1 과 실수 1.0 은 다르다). 수식의 '=' 는 수치 비교라 별개다. ----

    public bool Equals(GenApiValue other)
        => _isDouble == other._isDouble && (_isDouble ? _dbl.Equals(other._dbl) : _int == other._int);

    public override bool Equals(object? obj) => obj is GenApiValue other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return _isDouble ? (_dbl.GetHashCode() * 397) ^ 1 : _int.GetHashCode() * 397;
        }
    }

    public static bool operator ==(GenApiValue a, GenApiValue b) => a.Equals(b);
    public static bool operator !=(GenApiValue a, GenApiValue b) => !a.Equals(b);

    /// <summary>
    /// 불변 문화권 표기 — 정수는 10진, 실수는 "R" 형식. 사람이 읽는 자리(로그·예외 메시지·진단)를 위한 표기다.
    /// 왕복(다시 파싱해 같은 값)을 약속하지 않는다: 같은 실수라도 런타임에 따라 자릿수가 다르고(최단 표기 대 17자리),
    /// 되읽으면 이웃 값이 되는 실수도 있다. 값을 보존해야 하는 자리에서는 문자열이 아니라 값 자체를 넘긴다.
    /// 음의 0 만은 부호를 직접 적는다 — "R" 이 부호를 잃는 런타임이 있어, 그대로 두면 자산마다 다른 글자가 나온다.
    /// (실측 — .NET Framework 4.8: 음의 0 은 "0", 1/3 은 "0.33333333333333331", 0.84551240822557006 은 되읽으면
    /// 이웃 값이 되는 "0.84551240822557". 같은 값이 .NET 8 에서는 "-0", "0.3333333333333333", "0.8455124082255701" 이다.)
    /// </summary>
    public override string ToString()
        => _isDouble
            ? FormatDouble(_dbl, _dbl.ToString("R", CultureInfo.InvariantCulture))
            : _int.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 런타임이 만든 "R" 문자열을 받아 음의 0 의 부호만 바로잡는다 — 그 외에는 받은 문자열 그대로다.
    /// 부호를 붙이는 자리가 런타임에 따라 갈리기 때문에 보정이 필요하다: 음의 0 을 "-0" 으로 내는 런타임도,
    /// 부호를 잃고 "0" 으로 내는 런타임도 있다. 자산마다 다른 글자가 나오지 않게 여기서 하나로 맞춘다.
    /// 보정을 실제로 밟아 보려면 부호를 잃은 출력이 필요하므로, 만들어진 문자열을 인자로 받는다.
    /// </summary>
    /// <param name="value">표기 대상 값 — 음의 0 인지는 비교가 아니라 부호 비트로 가른다.</param>
    /// <param name="raw">그 값을 "R" 로 만든 문자열.</param>
    internal static string FormatDouble(double value, string raw)
    {
        // 0 이면서 부호 비트가 서 있는 값 = 음의 0. 0 과 구분되는 것은 비트뿐이라 비교가 아니라 비트로 가른다.
        if (value == 0.0 && BitConverter.DoubleToInt64Bits(value) != 0L
            && (raw.Length == 0 || raw[0] != '-'))
            return "-0";

        return raw;
    }
}
