using System.Text.RegularExpressions;
using GevSharp.Cli.Commands;

namespace GevSharp.Tests.Cli;

/// <summary>명령 목록·사용법 텍스트 — 전역 상태(콘솔·로그 싱크)를 건드리지 않는 부분만.</summary>
public class CliAppTests
{
    [Theory]
    [InlineData("discover")]
    [InlineData("info")]
    [InlineData("features")]
    [InlineData("get")]
    [InlineData("set")]
    [InlineData("grab")]
    [InlineData("regtest")]
    [InlineData("sim")]
    public void EveryCommandIsRegisteredAndDocumented(string name)
    {
        var command = CliApp.Find(name);

        Assert.NotNull(command);
        Assert.Equal(name, command!.Name);
        Assert.False(string.IsNullOrWhiteSpace(command.Summary));
        Assert.StartsWith(name, command.Usage, StringComparison.Ordinal);
        Assert.NotNull(command.Spec);
    }

    [Fact]
    public void FindIgnoresCaseAndRejectsUnknownNames()
    {
        Assert.NotNull(CliApp.Find("GRAB"));
        Assert.Null(CliApp.Find("nope"));
        Assert.Equal(8, CliApp.AllCommands.Count);
    }

    [Fact]
    public void TopLevelUsageListsCommandsExitCodesAndPortSuffix()
    {
        var w = new StringWriter();

        CliApp.WriteUsage(w);

        var text = w.ToString();
        foreach (var c in CliApp.AllCommands) Assert.Contains($"  {c.Name,-10} {c.Summary}", text);
        Assert.Contains("exit codes: 0 ok, 1 usage error, 2 device error, 3 stream error", text);
        Assert.Contains(":port suffix (default 3956)", text);
        Assert.Contains("--verbose", text);
        Assert.Contains("--version", text);
    }

    [Fact]
    public void CommandUsageStartsWithTheSyntaxLine()
    {
        var w = new StringWriter();

        CliApp.WriteCommandUsage(CliApp.Find("grab")!, w);

        var lines = w.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        Assert.StartsWith($"usage: {CliApp.ToolName} grab <ip[:port]>", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("--acq-start-addr"));
        Assert.Contains(lines, l => l.StartsWith("global options:", StringComparison.Ordinal));
    }

    /// <summary>전역 옵션 — 명령마다 선언하지 않고 CliApp 이 먼저 걷어 간다.</summary>
    private static readonly string[] GlobalOptions = { "verbose", "quiet", "access", "help" };

    [Fact]
    public void EveryOptionInAUsageTextIsActuallyAccepted()
    {
        // 사용법 문구와 옵션 선언은 서로 다른 자리에 있어 조용히 어긋난다 — 실제로 그랬다:
        // grab 의 --packet-timeout·--frame-retention 이 문서에는 있고 파서에는 없어 "unknown option" 으로 거절됐다.
        // 문구에 적힌 것은 전부 받아야 하고, 받는 것은 전부 문구에 있어야 한다.
        var problems = new List<string>();
        foreach (var c in CliApp.AllCommands)
        {
            var documented = Regex.Matches(c.Usage, @"--([a-z][a-z0-9-]*)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(n => !GlobalOptions.Contains(n, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var declared = c.Spec.Names.ToList();

            foreach (var name in documented.Where(n => !declared.Contains(n, StringComparer.Ordinal)))
                problems.Add($"{c.Name}: usage documents --{name} but the parser rejects it");
            foreach (var name in declared.Where(n => !documented.Contains(n, StringComparer.Ordinal)))
                problems.Add($"{c.Name}: --{name} is accepted but appears nowhere in the usage text");
        }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    [Fact]
    public void UsageTextNeverUsesTheTrademarkedName()
    {
        var w = new StringWriter();
        CliApp.WriteUsage(w);
        foreach (var c in CliApp.AllCommands) CliApp.WriteCommandUsage(c, w);

        Assert.DoesNotContain("GigE " + "Vision", w.ToString());
    }
}
