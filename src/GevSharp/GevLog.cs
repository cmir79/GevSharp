namespace GevSharp;

/// <summary>로그 레벨 — 호스트 로거의 레벨로 매핑해 쓴다.</summary>
public enum GevLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// 라이브러리 로그 출구. 라이브러리는 스스로 아무 데도 쓰지 않는다 — 호스트가 기동 시 <see cref="Sink"/> 를 한 번 붙인다.
/// 메시지는 항상 영어다(검색·비교·이관을 위해 한 언어로 고정). 장치가 보낸 문자열은 받은 그대로 싣는다.
/// 핫패스(GVSP 수신 스레드)에서는 <see cref="IsEnabled"/> 로 먼저 걸러 문자열 조립 비용을 피한다.
/// </summary>
public static class GevLog
{
    /// <summary>(level, source, message, exception) — null 이면 로그는 버려진다.</summary>
    public static Action<GevLogLevel, string, string, Exception?>? Sink { get; set; }

    /// <summary>이 레벨 미만은 Sink 호출 전에 걸러진다. 기본 Info.</summary>
    public static GevLogLevel MinLevel { get; set; } = GevLogLevel.Info;

    public static bool IsEnabled(GevLogLevel level) => Sink is not null && level >= MinLevel;

    public static void Write(GevLogLevel level, string source, string message, Exception? ex = null)
    {
        var sink = Sink;
        if (sink is null || level < MinLevel) return;
        try { sink(level, source, message, ex); }
        catch { /* 로그 실패가 라이브러리를 죽이면 안 된다 */ }
    }

    public static void Trace(string source, string message) => Write(GevLogLevel.Trace, source, message);
    public static void Debug(string source, string message) => Write(GevLogLevel.Debug, source, message);
    public static void Info(string source, string message) => Write(GevLogLevel.Info, source, message);
    public static void Warn(string source, string message, Exception? ex = null) => Write(GevLogLevel.Warn, source, message, ex);
    public static void Error(string source, string message, Exception? ex = null) => Write(GevLogLevel.Error, source, message, ex);
}
