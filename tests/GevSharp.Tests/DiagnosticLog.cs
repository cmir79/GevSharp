using System.Runtime.CompilerServices;

namespace GevSharp.Tests;

/// <summary>
/// 환경변수 <c>GEVSHARP_TEST_LOG</c> 에 파일 경로가 있으면 라이브러리 로그를 그 파일로 받는다.
/// <para>
/// 스위트가 어딘가에서 멈췄을 때 "무엇을 하다 멈췄는가" 를 알려면 마지막으로 시도한 동작이 남아 있어야 한다.
/// 테스트 러너의 출력은 버퍼에 있다가 강제 종료와 함께 사라지고 테스트 이름도 기본 리포터는 찍지 않으므로,
/// 소켓 바인드·레지스터 왕복 같은 실제 동작을 남기는 것은 라이브러리 로그뿐이다.
/// </para>
/// <para>
/// 평소에는 <b>붙지 않는다</b> — 환경변수가 없으면 <see cref="GevLog.Sink"/> 는 null 로 남고 로그 호출은
/// 조건 검사 하나로 끝난다. 싱크를 붙이면 문자열을 만들고 파일에 쓰므로 타이밍에 민감한 테스트가 흔들릴 수 있고,
/// 실제로 그런 일이 있었다. 진단할 때만 켠다.
/// </para>
/// </summary>
internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    [ModuleInitializer]
    internal static void Attach()
    {
        var path = Environment.GetEnvironmentVariable("GEVSHARP_TEST_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            // AutoFlush — 강제 종료돼도 마지막 줄까지 디스크에 있어야 쓸모가 있다.
            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GEVSHARP_TEST_LOG is set but could not be opened ({ex.Message}); library logging stays off.");
            return;
        }

        var level = Environment.GetEnvironmentVariable("GEVSHARP_TEST_LOG_LEVEL");
        GevLog.MinLevel = Enum.TryParse<GevLogLevel>(level, ignoreCase: true, out var parsed) ? parsed : GevLogLevel.Debug;
        GevLog.Sink = Write;
        GevLog.Info("TestHost", $"library log attached at {GevLog.MinLevel}; pid {Environment.ProcessId}, {Environment.ProcessorCount} processors");
    }

    private static void Write(GevLogLevel level, string source, string message, Exception? ex)
    {
        var w = _writer;
        if (w is null) return;
        // 여러 스레드가 동시에 쓴다(수신 스레드·하트비트·테스트) — 줄이 섞이면 마지막 줄을 믿을 수 없다.
        lock (Gate)
        {
            w.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {level,-5} {source}: {message}");
            if (ex is not null) w.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
        }
    }
}
