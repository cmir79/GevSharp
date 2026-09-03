namespace GevSharp.Cli.Commands;

/// <summary>프로세스 종료 코드. 스크립트가 결과를 분기할 수 있게 네 갈래로 고정한다.</summary>
public static class CliExitCode
{
    /// <summary>정상 종료. Ctrl+C 로 멈춘 경우도 정리가 끝났으면 여기에 든다.</summary>
    public const int Ok = 0;

    /// <summary>인자·옵션 오류 — 명령이 시작되기 전에 걸린 문제.</summary>
    public const int Usage = 1;

    /// <summary>장치 오류 — 열기·레지스터 접근·노드맵·시뮬레이터 기동 실패.</summary>
    public const int Device = 2;

    /// <summary>스트림 오류 — 스트림 채널을 연 뒤 수신 단계에서 난 문제.</summary>
    public const int Stream = 3;
}
