namespace GevSharp.Cli.Commands;

/// <summary>
/// Ctrl+C 를 취소 토큰으로 바꾼다. 첫 번째 Ctrl+C 는 프로세스를 살려 둔 채 토큰만 취소해 명령이 정리(획득 정지·CCP 해제·최종 통계)를 마치게 하고,
/// 두 번째 Ctrl+C 는 기본 동작(즉시 종료)에 맡긴다 — 정리가 멈춰 있을 때 빠져나갈 길.
/// 취소 소스는 끝까지 버리지 않는다 — 명령이 제 이유로 끝나는 순간에 눌린 Ctrl+C 의 취소가 스레드풀에서 아직 돌고 있을 수 있는데,
/// 그때 Dispose 가 앞서면 그 취소가 예외로 터져 정상 종료를 크래시로 바꾼다. 타이머가 없는 취소 소스는 놓아 줄 자원이 없고 프로세스도 곧 끝난다.
/// </summary>
public sealed class ConsoleCancel : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _presses;

    public ConsoleCancel()
    {
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    public CancellationToken Token => _cts.Token;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        var count = Interlocked.Increment(ref _presses);
        if (count > 1)
        {
            Console.Error.WriteLine("second Ctrl+C: terminating without cleanup");
            return;   // e.Cancel 이 false 이므로 프로세스가 끝난다
        }
        e.Cancel = true;
        Console.Error.WriteLine("Ctrl+C: stopping (press again to terminate immediately)");
        // 취소 콜백을 이 핸들러 스레드에서 동기 실행하지 않는다 — 콜백이 콘솔·소켓을 잠그면 핸들러가 막힌다.
        ThreadPool.QueueUserWorkItem(_ => _cts.Cancel());
    }

    /// <summary>핸들러만 뗀다. 취소 소스는 위 설명대로 살려 둔다.</summary>
    public void Dispose()
    {
        Console.CancelKeyPress -= OnCancelKeyPress;
    }
}
