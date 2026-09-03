using GevSharp.GenApi.Model;

namespace GevSharp.Tests.GenApi.Model;

/// <summary>텍스트 리터럴 해석 규칙 — 정수(10진/16진/부호), 실수, Yes/No, 접두 없는 16진.</summary>
public class GenApiLiteralTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("-42", -42L)]
    [InlineData("+7", 7L)]
    [InlineData(" 0x10 ", 16L)]
    [InlineData("0X1f", 31L)]
    [InlineData("-0x10", -16L)]
    [InlineData("0xFFFFFFFFFFFFFFFF", -1L)]
    [InlineData("0x7FFFFFFFFFFFFFFF", long.MaxValue)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void Int64Parses(string text, long expected)
    {
        Assert.True(GenApiLiteral.TryParseInt64(text, out var v));
        Assert.Equal(expected, v);
        Assert.Equal(expected, GenApiLiteral.ParseInt64(text, "test", "N"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x")]
    [InlineData("0xG1")]
    [InlineData("1.5")]
    [InlineData("12abc")]
    [InlineData("1 2")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("0x10000000000000000")]
    public void Int64Rejects(string text)
    {
        Assert.False(GenApiLiteral.TryParseInt64(text, out _));
        var ex = Assert.Throws<GenApiException>(() => GenApiLiteral.ParseInt64(text, "Value", "N"));
        Assert.Equal("N", ex.NodeName);
        Assert.Contains("Value", ex.Message);
    }

    [Fact]
    public void NullRejects()
    {
        Assert.False(GenApiLiteral.TryParseInt64(null, out _));
        Assert.False(GenApiLiteral.TryParseDouble(null, out _));
        Assert.Throws<GenApiException>(() => GenApiLiteral.ParseYesNo(null, "x", "N"));
        Assert.Throws<GenApiException>(() => GenApiLiteral.ParseHex(null, "x", "N"));
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("-2.25", -2.25)]
    [InlineData("1e3", 1000.0)]
    [InlineData("1.5E-3", 0.0015)]
    [InlineData("7", 7.0)]
    [InlineData("0x10", 16.0)]
    [InlineData(" .5 ", 0.5)]
    public void DoubleParses(string text, double expected)
    {
        Assert.True(GenApiLiteral.TryParseDouble(text, out var v));
        Assert.Equal(expected, v);
        Assert.Equal(expected, GenApiLiteral.ParseDouble(text, "test", "N"));
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("abc")]
    [InlineData("")]
    public void DoubleRejects(string text)
    {
        Assert.False(GenApiLiteral.TryParseDouble(text, out _));
        Assert.Throws<GenApiException>(() => GenApiLiteral.ParseDouble(text, "Min", "N"));
    }

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("yes", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("No", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData(" No ", false)]
    public void YesNoParses(string text, bool expected)
        => Assert.Equal(expected, GenApiLiteral.ParseYesNo(text, "Streamable", "N"));

    [Theory]
    [InlineData("Maybe")]
    [InlineData("")]
    [InlineData("2")]
    public void YesNoRejects(string text)
        => Assert.Throws<GenApiException>(() => GenApiLiteral.ParseYesNo(text, "Streamable", "N"));

    [Theory]
    [InlineData("9001", 0x9001UL)]
    [InlineData("0x9001", 0x9001UL)]
    [InlineData("0A", 10UL)]
    [InlineData(" ff ", 255UL)]
    [InlineData("FFFFFFFFFFFFFFFF", ulong.MaxValue)]
    public void HexParses(string text, ulong expected)
        => Assert.Equal(expected, GenApiLiteral.ParseHex(text, "EventID", "N"));

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("xyz")]
    [InlineData("-1")]
    public void HexRejects(string text)
        => Assert.Throws<GenApiException>(() => GenApiLiteral.ParseHex(text, "EventID", "N"));

    [Fact]
    public void Int32RangeIsChecked()
    {
        Assert.Equal(31, GenApiLiteral.ParseInt32("31", "Bit", "N"));
        Assert.Equal(-1, GenApiLiteral.ParseInt32("-1", "Bit", "N"));
        Assert.Throws<GenApiException>(() => GenApiLiteral.ParseInt32("0x100000000", "Bit", "N"));
    }
}
