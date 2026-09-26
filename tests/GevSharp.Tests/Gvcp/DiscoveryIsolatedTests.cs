using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using GevSharp.Tests.GenApi.Model;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Gvcp;

/// <summary>
/// 프로세스 전역(열린 핸들 수)을 재는 탐색 테스트 — 다른 테스트가 나란히 돌며 소켓·스레드를 여닫으면 수가 흔들리므로
/// 다른 어떤 컬렉션과도 나란히 돌지 않는 격리 컬렉션에 둔다. 여기 있는 탐색은 전부 창을 열기 전에 끝나는 길만 탄다.
/// </summary>
[Collection(GevLogSinkCollection.Name)]
public class DiscoveryIsolatedTests
{
    /// <summary>
    /// 이 호스트의 인터페이스 주소일 수 없는 주소(문서용 예약 대역 TEST-NET-1) — 여기에 묶으면 바인드가 곧바로 실패한다.
    /// 설령 묶이더라도 아래 옵션은 브로드캐스트를 끄고 루프백의 닫힌 포트로만 보내므로 호스트 밖으로 아무것도 나가지 않는다.
    /// </summary>
    private static readonly IPAddress UnbindableAddress = IPAddress.Parse("192.0.2.1");

    private static GevDiscoveryOpt UnbindableOpt(int interfaceCount) => new()
    {
        Interfaces = Enumerable.Repeat(UnbindableAddress, interfaceCount).ToArray(),
        LimitedBroadcast = false,
        DirectedBroadcast = false,
        UnicastTargets = new[] { new IPEndPoint(IPAddress.Loopback, 9) },
        TimeoutMs = 5000,
    };

    /// <summary>이 프로세스가 연 핸들(리눅스는 파일 기술자) 수. 셀 방법이 없는 플랫폼이면 null.</summary>
    private static int? OpenHandleCount()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var self = Process.GetCurrentProcess();
            return self.HandleCount;
        }
        const string fdDir = "/proc/self/fd";
        return Directory.Exists(fdDir) ? Directory.GetFileSystemEntries(fdDir).Length : null;
    }

    [Fact]
    public async Task ASocketWhoseBindFailsIsClosedAtOnce_NotLeftToTheFinalizer()
    {
        // 인터페이스 하나마다 소켓을 하나 만들고, 바인드가 실패하면 그 인터페이스를 건너뛴다. 그 소켓을 닫지 않으면
        // 핸들은 GC 종료자가 돌 때까지 남는다 — 탐색을 부를 때마다 실패한 인터페이스 수만큼 쌓인다.
        // 같은 주소를 여러 번 주면 한 번의 탐색(인터페이스 열거 한 번)으로 실패를 여러 번 만든다.
        const int interfaces = 500;
        var before = OpenHandleCount();
        if (before is null) Assert.Skip("this platform offers no way to count open handles");

        // 재는 동안 GC 를 막는다 — 도중에 GC 가 돌면 종료자가 버려진 소켓을 닫아 새는 판도 수가 작게 나온다(실측: 막지 않으면
        // 새는 판이 +143 으로 문턱 아래에 들어왔다). 예산을 넘겨 할당하면 런타임이 스스로 구역을 끝내므로 끝낼 때는 아직 구역
        // 안인지 보고 끝낸다. 예산이 그 런타임의 한도를 넘으면(32비트 등) 막지 못한 채 잰다 — 그때 이 테스트는 새는 판을 놓칠 수
        // 있을 뿐 닫는 판을 떨어뜨리지는 않는다.
        bool noGc;
        try
        {
            noGc = GC.TryStartNoGCRegion(32L * 1024 * 1024);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            noGc = false;
        }
        IReadOnlyList<GevDeviceInfo> result;
        int after;
        try
        {
            result = await GevDiscovery.DiscoverAsync(UnbindableOpt(interfaces));
            after = OpenHandleCount()!.Value;
        }
        finally
        {
            if (noGc && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion)
                GC.EndNoGCRegion();
        }

        // 대조군: 전체 GC 로 종료자를 돌린 뒤의 수. 새는 판에서는 여기서 수가 도로 내려가 "차이가 버려진 소켓이었다" 를 보인다.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var afterGc = OpenHandleCount()!.Value;

        Assert.Empty(result);
        // 문턱을 실패 수의 절반으로 둔다 — 격리 컬렉션이어도 런타임 자신이 스레드·이벤트 핸들을 조금씩 여닫는 흔들림은 남는다.
        // 실측(Windows, net8.0): 소켓을 닫지 않던 판 +533(전체 GC 뒤 +35), 닫는 판 +33(전체 GC 뒤 그대로).
        Assert.True(after - before.Value < interfaces / 2,
            $"{after - before.Value} handle(s) stayed open after {interfaces} failed binds (before {before}, after {after}, after a full GC {afterGc}, GC held off: {noGc})");
    }
}
