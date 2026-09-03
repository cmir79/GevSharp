namespace GevSharp.Tests;

/// <summary>
/// 테스트가 기다릴 때 쓰는 시간 제한. <c>Task.WaitAsync</c> 는 net6 이상에만 있어, netstandard2.0 자산을 net48 로 돌리는
/// 다리(tests/GevSharp.Net48)에서는 없다. 확장 메서드는 인스턴스 메서드보다 뒤에 고려되므로, 있는 자산에서는 런타임 것이
/// 그대로 쓰이고 없는 자산에서만 이것이 쓰인다 — 두 자산의 테스트 소스가 갈리지 않게 하는 것이 유일한 목적이다.
/// </summary>
internal static class TestAsync
{
    /// <summary>
    /// <paramref name="task"/> 가 시간 안에 끝나면 그 결과, 아니면 <see cref="TimeoutException"/>.
    /// 토큰이 취소되면 취소로 끝난다. 기다리는 쪽만 풀어 줄 뿐 원래 작업을 멈추지는 않는다(런타임 것과 같다).
    /// </summary>
    public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout, CancellationToken ct = default)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));
        if (task.IsCompleted) return await task.ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, cts.Token);
        var first = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (!ReferenceEquals(first, task))
        {
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"The task did not complete within {timeout.TotalMilliseconds} ms.");
        }
        cts.Cancel();               // 지연 타이머를 거둔다
        return await task.ConfigureAwait(false);
    }
}
