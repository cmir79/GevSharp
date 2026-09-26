using GevSharp.GenApi;
using GevSharp.Tests.GenApi.Model;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Integration;

/// <summary>
/// <see cref="GevDevice.SetTlParamsLockedAsync"/> 가 false 를 돌려주는 두 경우 — 노드가 없을 때와, 같은 이름의 노드가 정수 노드가 아닐 때 —
/// 가 로그에서 서로 구분되는지. 뒤엣것은 그 노드에 걸린 잠금이 풀리지 않은 채 남는 이상 상황이라 "없다" 로 적히면 안 된다.
/// 전역 싱크를 바꾸므로 격리 컬렉션에서 돌고, 싱크는 네트워크 왕복이 없는 호출(노드맵을 미리 받아 둔 뒤의 조회) 하나만 감싼다.
/// </summary>
[Collection(GevLogSinkCollection.Name)]
public class TlParamsLockedTests
{
    private const string Header =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<RegisterDescription xmlns=\"http://www.genicam.org/GenApi/Version_1_1\" ModelName=\"TlLockProbe\" VendorName=\"GevSharp\""
        + " SchemaMajorVersion=\"1\" SchemaMinorVersion=\"1\" SchemaSubMinorVersion=\"0\" MajorVersion=\"1\" MinorVersion=\"0\" SubMinorVersion=\"0\""
        + " ProductGuid=\"a\" VersionGuid=\"b\">"
        + "<Port Name=\"Device\" NameSpace=\"Standard\"/>";

    /// <summary>TLParamsLocked 를 정수가 아닌 Boolean 으로 선언한 기술.</summary>
    private const string BooleanTlParamsLockedXml = Header
        + "<Category Name=\"Root\" NameSpace=\"Standard\"><pFeature>TLParamsLocked</pFeature></Category>"
        + "<Boolean Name=\"TLParamsLocked\" NameSpace=\"Standard\"><Value>0</Value></Boolean>"
        + "</RegisterDescription>";

    /// <summary>TLParamsLocked 가 아예 없는 기술.</summary>
    private const string NoTlParamsLockedXml = Header
        + "<Category Name=\"Root\" NameSpace=\"Standard\"/>"
        + "</RegisterDescription>";

    [Fact]
    public async Task NonIntegerNode_ReturnsFalse_AndWarnsWithItsKindInsteadOfCallingItMissing()
    {
        await using var rig = await SimRig.StartAsync(sim: o => o.GenApiXml = BooleanTlParamsLockedXml);
        var nodes = await rig.Device.GetNodeMapAsync();
        Assert.Equal(NodeKind.Boolean, nodes.GetNode("TLParamsLocked")!.Kind);

        var (result, logged) = await CaptureAsync(() => rig.Device.SetTlParamsLockedAsync(true));

        Assert.False(result);
        var entry = Assert.Single(logged, e => e.Message.Contains("TLParamsLocked"));
        Assert.DoesNotContain("not in the node map", entry.Message);
        Assert.Contains("Boolean", entry.Message);
        Assert.Equal(GevLogLevel.Warn, entry.Level);
    }

    [Fact]
    public async Task MissingNode_ReturnsFalse_AndSaysSoAtDebug()
    {
        await using var rig = await SimRig.StartAsync(sim: o => o.GenApiXml = NoTlParamsLockedXml);
        var nodes = await rig.Device.GetNodeMapAsync();
        Assert.Null(nodes.GetNode("TLParamsLocked"));

        var (result, logged) = await CaptureAsync(() => rig.Device.SetTlParamsLockedAsync(false));

        Assert.False(result);
        var entry = Assert.Single(logged, e => e.Message.Contains("TLParamsLocked"));
        Assert.Contains("not in the node map", entry.Message);
        Assert.Equal(GevLogLevel.Debug, entry.Level);
    }

    /// <summary>호출 하나를 Debug 싱크로 감싼다. 노드맵은 이미 받아 두었으므로 이 호출은 장치와 왕복하지 않는다.</summary>
    private static async Task<(bool Result, List<(GevLogLevel Level, string Message)> Logged)> CaptureAsync(Func<Task<bool>> call)
    {
        var logged = new List<(GevLogLevel Level, string Message)>();
        var prevSink = GevLog.Sink;
        var prevLevel = GevLog.MinLevel;
        bool result;
        try
        {
            GevLog.Sink = (lvl, _, msg, _) => { lock (logged) logged.Add((lvl, msg)); };
            GevLog.MinLevel = GevLogLevel.Debug;
            result = await call();
        }
        finally
        {
            GevLog.Sink = prevSink;
            GevLog.MinLevel = prevLevel;
        }
        lock (logged) return (result, logged.ToList());
    }
}
