using GevSharp.GenApi;

namespace GevSharp.Tests.GenApi;

public class GenApiValueTests
{
    [Fact]
    public void DefaultIsIntegerZero()
    {
        GenApiValue v = default;
        Assert.True(v.IsInteger);
        Assert.False(v.IsDouble);
        Assert.Equal(0L, v.AsInt64);
        Assert.Equal(0.0, v.AsDouble);
        Assert.False(v.IsNonZero);
        Assert.Equal(GenApiValue.Zero, v);
    }

    [Theory]
    [InlineData(2.9, 2L)]
    [InlineData(-2.9, -2L)]
    [InlineData(0.0, 0L)]
    [InlineData(-9223372036854775808.0, long.MinValue)]
    [InlineData(1e15, 1000000000000000L)]
    public void AsInt64TruncatesTowardZero(double d, long expected)
        => Assert.Equal(expected, GenApiValue.FromDouble(d).AsInt64);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(9223372036854775808.0)]
    [InlineData(-1e19)]
    public void AsInt64RejectsUnrepresentableDoubles(double d)
    {
        var ex = Assert.Throws<GenApiException>(() => GenApiValue.FromDouble(d).AsInt64);
        Assert.EndsWith(".", ex.Message);
    }

    [Fact]
    public void IntegerConvertsToDouble()
    {
        var v = GenApiValue.FromInt64(42);
        Assert.True(v.IsInteger);
        Assert.Equal(42.0, v.AsDouble);
        Assert.Equal(42L, v.AsInt64);
    }

    [Theory]
    [InlineData(42L, "42")]
    [InlineData(-1L, "-1")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void IntegerToStringIsInvariant(long v, string expected)
        => Assert.Equal(expected, GenApiValue.FromInt64(v).ToString());

    [Theory]
    [InlineData(2.5, "2.5")]
    [InlineData(1.0, "1")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e21, "1E+21")]
    [InlineData(double.NaN, "NaN")]
    public void DoubleToStringIsInvariant(double v, string expected)
        => Assert.Equal(expected, GenApiValue.FromDouble(v).ToString());

    [Fact]
    public void NegativeZeroKeepsItsSignInText()
    {
        // 이 런타임에서 실제로 나오는 글자를 고정한다 — 부호를 잃는 런타임 쪽은 아래 FormatDouble 테스트가 본다.
        Assert.Equal("-0", GenApiValue.FromDouble(-0.0).ToString());
        Assert.Equal("-0", GenApiValue.FromDouble(-1.0 * 0.0).ToString());
        Assert.Equal("0", GenApiValue.FromDouble(0.0).ToString());
        Assert.Equal("0", GenApiValue.FromInt64(0).ToString());
    }

    [Theory]
    // 음의 0 을 부호 없이 내는 런타임의 출력을 그대로 넣는다 — 보정이 빠지면 "0" 이 그대로 새어 나간다.
    [InlineData(-0.0, "0", "-0")]
    // 이미 부호가 붙어 나오는 런타임의 출력은 손대지 않는다(부호가 겹치지 않는다).
    [InlineData(-0.0, "-0", "-0")]
    // 양의 0 과 보통 값은 어느 런타임이든 받은 글자 그대로다.
    [InlineData(0.0, "0", "0")]
    [InlineData(2.5, "2.5", "2.5")]
    [InlineData(-2.5, "-2.5", "-2.5")]
    [InlineData(double.NaN, "NaN", "NaN")]
    public void FormatDoubleRestoresTheSignRuntimesDropFromNegativeZero(double value, string raw, string expected)
        => Assert.Equal(expected, GenApiValue.FormatDouble(value, raw));

    [Fact]
    public void EqualityIsStructural()
    {
        Assert.Equal(GenApiValue.FromInt64(1), GenApiValue.FromInt64(1));
        Assert.NotEqual(GenApiValue.FromInt64(1), GenApiValue.FromDouble(1.0));   // 종류가 다르면 다르다
        Assert.True(GenApiValue.FromDouble(2.5) == 2.5);
        Assert.True(GenApiValue.FromInt64(3) != 4);
        Assert.Equal(GenApiValue.FromDouble(double.NaN), GenApiValue.FromDouble(double.NaN));
        Assert.Equal(GenApiValue.FromInt64(7).GetHashCode(), GenApiValue.FromInt64(7).GetHashCode());
    }

    [Fact]
    public void ImplicitConversionsPickTheRightKind()
    {
        GenApiValue i = 5;
        GenApiValue l = 5L;
        GenApiValue d = 5.0;
        Assert.True(i.IsInteger);
        Assert.True(l.IsInteger);
        Assert.True(d.IsDouble);
        Assert.Equal(i, l);
        Assert.NotEqual(i, d);
    }

    [Fact]
    public void FromBooleanYieldsIntegerOneOrZero()
    {
        Assert.Equal(GenApiValue.One, GenApiValue.FromBoolean(true));
        Assert.Equal(GenApiValue.Zero, GenApiValue.FromBoolean(false));
        Assert.True(GenApiValue.One.IsNonZero);
        Assert.True(GenApiValue.FromDouble(0.5).IsNonZero);
        Assert.True(GenApiValue.FromDouble(double.NaN).IsNonZero);
        Assert.False(GenApiValue.FromDouble(0.0).IsNonZero);
    }

    [Fact]
    public void OperatorsFollowGenApiTyping()
    {
        GenApiValue a = 7, b = 2, d = 2.0;
        Assert.Equal(GenApiValue.FromInt64(9), a + b);
        Assert.Equal(GenApiValue.FromInt64(5), a - b);
        Assert.Equal(GenApiValue.FromInt64(14), a * b);
        Assert.Equal(GenApiValue.FromInt64(3), a / b);
        Assert.Equal(GenApiValue.FromInt64(1), a % b);
        Assert.Equal(GenApiValue.FromDouble(3.5), a / d);
        Assert.Equal(GenApiValue.FromDouble(9.0), a + d);
        Assert.Equal(GenApiValue.FromInt64(-7), -a);
        Assert.Equal(GenApiValue.FromInt64(49), GenApiValue.Pow(a, b));
        Assert.Equal(GenApiValue.FromDouble(0.5), GenApiValue.Pow(b, -1));
        Assert.Equal(GenApiValue.FromInt64(2), GenApiValue.BitAnd(a, b));
        Assert.Equal(GenApiValue.FromInt64(7), GenApiValue.BitOr(a, b));
        Assert.Equal(GenApiValue.FromInt64(5), GenApiValue.BitXor(a, b));
        Assert.Equal(GenApiValue.FromInt64(-8), GenApiValue.BitNot(a));
        Assert.Equal(GenApiValue.FromInt64(28), GenApiValue.ShiftLeft(a, b));
        Assert.Equal(GenApiValue.FromInt64(1), GenApiValue.ShiftRight(a, b));
        Assert.Equal(GenApiValue.Zero, GenApiValue.LogicalNot(a));
        Assert.Equal(GenApiValue.One, GenApiValue.LogicalNot(GenApiValue.Zero));
    }

    [Fact]
    public void OperatorErrorsThrowWithoutPositionSuffix()
    {
        GenApiValue one = 1, zero = 0, half = 0.5;
        var div = Assert.Throws<GenApiException>(() => one / zero);
        Assert.Equal("Division by zero.", div.Message);
        Assert.Throws<GenApiException>(() => one % zero);
        Assert.Throws<GenApiException>(() => GenApiValue.BitAnd(one, half));
        Assert.Throws<GenApiException>(() => GenApiValue.ShiftLeft(one, 64));
        Assert.Throws<GenApiException>(() => GenApiValue.FromInt64(long.MaxValue) + one);
        Assert.Throws<GenApiException>(() => -GenApiValue.FromInt64(long.MinValue));
    }
}
