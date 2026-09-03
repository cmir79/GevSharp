using GevSharp.GenApi.Model;

namespace GevSharp.Tests.GenApi.Model;

/// <summary>
/// <see cref="GevLog.Sink"/>/<see cref="GevLog.MinLevel"/> 은 프로세스 전역이다. 이 컬렉션은 다른 어떤 컬렉션과도 나란히 돌지 않아,
/// 싱크를 바꿔 끼는 동안 다른 테스트의 로그가 섞여 들거나 삼켜지지 않는다.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GevLogSinkCollection
{
    public const string Name = "GevLogSink";
}

/// <summary>파서 경고가 <see cref="GevLog"/> 로도 나가는지 — 전역 싱크를 바꾸므로 격리 컬렉션에서만 돈다.</summary>
[Collection(GevLogSinkCollection.Name)]
public class GenApiXmlParserLogTests
{
    [Fact]
    public void WarningsAreAlsoLogged()
    {
        var logged = new List<(GevLogLevel Level, string Source, string Message)>();
        var prevSink = GevLog.Sink;
        var prevLevel = GevLog.MinLevel;
        try
        {
            GevLog.Sink = (lvl, src, msg, _) => logged.Add((lvl, src, msg));
            GevLog.MinLevel = GevLogLevel.Trace;
            GenApiXmlParser.Parse(GenApiFixtures.Wrap("<Odd Name=\"O\"/>"));
        }
        finally
        {
            GevLog.Sink = prevSink;
            GevLog.MinLevel = prevLevel;
        }
        var entry = Assert.Single(logged);
        Assert.Equal(GevLogLevel.Warn, entry.Level);
        Assert.Equal("GenApi.Model", entry.Source);
        Assert.Contains("Odd", entry.Message);
    }

    [Fact]
    public void NothingIsLoggedBelowMinLevelOrWithoutSink()
    {
        var logged = 0;
        var prevSink = GevLog.Sink;
        var prevLevel = GevLog.MinLevel;
        try
        {
            GevLog.Sink = (_, _, _, _) => logged++;
            GevLog.MinLevel = GevLogLevel.Error;
            var m = GenApiXmlParser.Parse(GenApiFixtures.Wrap("<Odd Name=\"O\"/>"));
            Assert.Single(m.Warnings);   // the model keeps the warning even when the log drops it
            Assert.Equal(0, logged);

            GevLog.Sink = null;
            GevLog.MinLevel = GevLogLevel.Trace;
            Assert.Single(GenApiXmlParser.Parse(GenApiFixtures.Wrap("<Odd Name=\"O\"/>")).Warnings);
        }
        finally
        {
            GevLog.Sink = prevSink;
            GevLog.MinLevel = prevLevel;
        }
    }
}
