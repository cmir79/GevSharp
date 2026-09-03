namespace GevSharp.Cli.Commands;

/// <summary>인자·옵션이 잘못됐다. 메시지는 사용자에게 그대로 보이고 종료 코드는 <see cref="CliExitCode.Usage"/>.</summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}
