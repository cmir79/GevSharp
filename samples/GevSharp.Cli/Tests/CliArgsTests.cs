using GevSharp.Cli.Commands;
using GevSharp.GenApi;

namespace GevSharp.Tests.Cli;

/// <summary>손으로 만든 인자 파서 — 문법(긴/짧은 옵션, 인라인 값, 음수, "--")과 형 변환·범위 검사가 사용법 오류로 이어지는지.</summary>
public class CliArgsTests
{
    private static CliOptSpec Spec() => new CliOptSpec()
        .Flag("no-resend")
        .Flag("all", 'a')
        .Value("count", 'n')
        .Value("timeout")
        .Value("interface")
        .Value("socket-buffer")
        .Value("addr")
        .Value("visibility");

    [Fact]
    public void LongOptionsSeparateAndInlineValues()
    {
        var args = CliArgs.Parse(new[] { "192.168.1.10", "--count", "5", "--timeout=250" }, Spec());

        Assert.Equal(new[] { "192.168.1.10" }, args.Positionals);
        Assert.Equal("5", args.Get("count"));
        Assert.Equal(250, args.GetInt("timeout", 0));
        Assert.True(args.Has("count"));
        Assert.False(args.Has("no-resend"));
    }

    [Fact]
    public void ShortAliasSeparateAndAttached()
    {
        Assert.Equal(5, CliArgs.Parse(new[] { "-n", "5" }, Spec()).GetInt("count", 0));
        Assert.Equal(7, CliArgs.Parse(new[] { "-n7" }, Spec()).GetInt("count", 0));
        Assert.True(CliArgs.Parse(new[] { "-a" }, Spec()).Has("all"));
    }

    [Fact]
    public void FlagsDoNotConsumeTheNextToken()
    {
        var args = CliArgs.Parse(new[] { "--no-resend", "10.0.0.1" }, Spec());

        Assert.True(args.Has("no-resend"));
        Assert.Equal(new[] { "10.0.0.1" }, args.Positionals);
    }

    [Fact]
    public void NegativeNumbersArePositionals()
    {
        var args = CliArgs.Parse(new[] { "-1", "-0.5" }, Spec());

        Assert.Equal(new[] { "-1", "-0.5" }, args.Positionals);
    }

    [Fact]
    public void DoubleDashEndsOptionParsing()
    {
        var args = CliArgs.Parse(new[] { "--", "--count", "-n" }, Spec());

        Assert.Equal(new[] { "--count", "-n" }, args.Positionals);
        Assert.False(args.Has("count"));
    }

    [Fact]
    public void UnknownOptionsAreRejected()
    {
        var ex = Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--bogus" }, Spec()));
        Assert.Contains("--bogus", ex.Message);
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "-z" }, Spec()));
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--" + string.Empty + "=x" }, Spec()));
    }

    [Fact]
    public void MissingValueAndValueOnFlagAreUsageErrors()
    {
        var missing = Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--count" }, Spec()));
        Assert.Contains("requires a value", missing.Message);
        var onFlag = Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--no-resend=1" }, Spec()));
        Assert.Contains("does not take a value", onFlag.Message);
    }

    [Fact]
    public void RepeatedValuesKeepAllAndLastWins()
    {
        var args = CliArgs.Parse(new[] { "--interface", "10.0.0.1", "--interface", "10.0.1.1" }, Spec());

        Assert.Equal(new[] { "10.0.0.1", "10.0.1.1" }, args.GetAll("interface"));
        Assert.Equal("10.0.1.1", args.Get("interface"));
        Assert.Empty(args.GetAll("timeout"));
        Assert.Null(args.Get("timeout"));
    }

    [Fact]
    public void TypedGettersFallBackAndValidateRange()
    {
        var absent = CliArgs.Parse(Array.Empty<string>(), Spec());
        Assert.Equal(1000, absent.GetInt("timeout", 1000));
        Assert.Equal(2.5, absent.GetDouble("timeout", 2.5));
        Assert.Equal(9L, absent.GetLong("count", 9));

        var zero = CliArgs.Parse(new[] { "--count", "0" }, Spec());
        var ex = Assert.Throws<CliUsageException>(() => zero.GetInt("count", 1, min: 1));
        Assert.Contains("--count", ex.Message);

        var text = CliArgs.Parse(new[] { "--count", "five" }, Spec());
        Assert.Throws<CliUsageException>(() => text.GetInt("count", 1));
        Assert.Throws<CliUsageException>(() => text.GetDouble("count", 1));
    }

    [Fact]
    public void HexOptionsAcceptPrefixedAndBareDigits()
    {
        Assert.Equal(0x10030u, CliArgs.Parse(new[] { "--addr", "0x10030" }, Spec()).GetHex("addr"));
        Assert.Equal(0x10030u, CliArgs.Parse(new[] { "--addr", "10030" }, Spec()).GetHex("addr"));
        Assert.Null(CliArgs.Parse(Array.Empty<string>(), Spec()).GetHex("addr"));
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--addr", "0xZZ" }, Spec()).GetHex("addr"));
    }

    [Fact]
    public void ByteCountsAcceptSuffixes()
    {
        Assert.Equal(256L * 1024, CliArgs.Parse(new[] { "--socket-buffer", "256k" }, Spec()).GetBytes("socket-buffer", 0));
        Assert.Equal(32L * 1024 * 1024, CliArgs.Parse(new[] { "--socket-buffer", "32M" }, Spec()).GetBytes("socket-buffer", 0));
        Assert.Equal(4096L, CliArgs.Parse(new[] { "--socket-buffer", "4096" }, Spec()).GetBytes("socket-buffer", 0));
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--socket-buffer", "-1" }, Spec()).GetBytes("socket-buffer", 0));
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--socket-buffer", "big" }, Spec()).GetBytes("socket-buffer", 0));
        // 접미사를 곱해 long 을 넘는 값도 사용법 오류다 — OverflowException 이 새어 나가면 장치 오류(2)로 잘못 보고된다.
        var tooLarge = Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--socket-buffer", "99999999999g" }, Spec()).GetBytes("socket-buffer", 0));
        Assert.Contains("too large", tooLarge.Message);
        Assert.Throws<CliUsageException>(() => CliArgs.ParseBytes("9223372036854775807k", "--socket-buffer"));
        Assert.Equal(long.MaxValue, CliArgs.ParseBytes("9223372036854775807", "--socket-buffer"));
    }

    [Fact]
    public void EnumOptionsIgnoreCaseAndListAllowedValues()
    {
        Assert.Equal(Visibility.Expert, CliArgs.Parse(new[] { "--visibility", "Expert" }, Spec()).GetEnum("visibility", Visibility.Beginner));
        Assert.Equal(Visibility.Guru, CliArgs.Parse(new[] { "--visibility", "guru" }, Spec()).GetEnum("visibility", Visibility.Beginner));
        Assert.Equal(Visibility.Beginner, CliArgs.Parse(Array.Empty<string>(), Spec()).GetEnum("visibility", Visibility.Beginner));

        var ex = Assert.Throws<CliUsageException>(() => CliArgs.Parse(new[] { "--visibility", "nope" }, Spec()).GetEnum("visibility", Visibility.Beginner));
        Assert.Contains("beginner", ex.Message);
        Assert.Contains("guru", ex.Message);
    }

    [Fact]
    public void PositionalHelpersReportMissingAndExtraArguments()
    {
        var one = CliArgs.Parse(new[] { "10.0.0.1" }, Spec());
        Assert.Equal("10.0.0.1", one.Positional(0, "ip"));
        Assert.Null(one.PositionalOrNull(1));
        var missing = Assert.Throws<CliUsageException>(() => one.Positional(1, "node"));
        Assert.Contains("<node>", missing.Message);

        var two = CliArgs.Parse(new[] { "10.0.0.1", "extra" }, Spec());
        var extra = Assert.Throws<CliUsageException>(() => two.RejectExtraPositionals(1));
        Assert.Contains("extra", extra.Message);
        two.RejectExtraPositionals(2);
    }

    [Fact]
    public void MergeAddsOptionsWithoutOverridingShortAliases()
    {
        var spec = new CliOptSpec().Value("count", 'n').Merge(new CliOptSpec().Flag("help", 'h').Value("other", 'n'));

        Assert.True(spec.IsFlag("help"));
        Assert.True(spec.IsValued("other"));
        Assert.True(spec.TryResolveShort('n', out var name));
        Assert.Equal("count", name);
        Assert.True(spec.TryResolveShort('h', out _));
        Assert.False(spec.IsKnown("nope"));
    }

    [Fact]
    public void StaticParsersShareTheSameRules()
    {
        Assert.Equal(255L, CliArgs.ParseLong("0xFF", "value"));
        Assert.Equal(-12L, CliArgs.ParseLong("-12", "value"));
        Assert.Equal(1.5, CliArgs.ParseDouble("1.5", "value"));
        Assert.Equal(0xD00u, CliArgs.ParseHex("D00", "--addr"));
        Assert.Throws<CliUsageException>(() => CliArgs.ParseInt("1.5", "--count"));
        Assert.Throws<CliUsageException>(() => CliArgs.ParseDouble("NaN", "value"));
        Assert.Throws<CliUsageException>(() => CliArgs.ParseHex("", "--addr"));
    }
}
