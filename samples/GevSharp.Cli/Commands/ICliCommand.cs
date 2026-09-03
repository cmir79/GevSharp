namespace GevSharp.Cli.Commands;

/// <summary>하위 명령 하나. 파싱은 <see cref="CliApp"/> 이 <see cref="Spec"/> 으로 하고, 명령은 결과만 받아 실행한다.</summary>
public interface ICliCommand
{
    string Name { get; }

    /// <summary>최상위 사용법 표에 나오는 한 줄 설명.</summary>
    string Summary { get; }

    /// <summary>명령 사용법 본문 — 첫 줄은 문법, 이어서 옵션·동작 설명.</summary>
    string Usage { get; }

    CliOptSpec Spec { get; }

    /// <summary>실행 후 종료 코드를 돌려준다. 예외는 <see cref="CliApp"/> 이 종료 코드로 바꾼다.</summary>
    Task<int> RunAsync(CliArgs args, CancellationToken ct);
}
