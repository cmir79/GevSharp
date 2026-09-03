using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;
using GevSharp.GenApi;
using GevSharp.Gvcp;

// 테스트마다 자체 타임아웃을 두므로 xunit 취소 토큰 전달 권고(xUnit1051)는 끈다.
#pragma warning disable xUnit1051

namespace GevSharp.Tests.Integration;

/// <summary>
/// 외부 가상 카메라 대향 테스트 — 우리 시뮬레이터가 아니라 남이 만든 장치를 상대로 같은 코드가 도는지 본다.
/// 환경변수 <c>GEVSHARP_VIRTUAL_CAMERA</c> 에 IPv4 주소가 있을 때만 돌고,
/// 없으면 각 테스트가 <see cref="Assert.Skip(string)"/> 으로 건너뛴다(CI 의 Linux 잡이 설정한다).
///
/// 로컬에서 돌리려면 루프백에서 GVCP 에 응답하는 가상 카메라를 하나 띄우고 그 주소를 환경변수에 준다
/// (CI 가 쓰는 것은 .github/workflows/ci.yml 의 설치 단계에 적혀 있다):
/// <code>
///   GEVSHARP_VIRTUAL_CAMERA=127.0.0.1 dotnet test tests/GevSharp.Tests --filter-trait "Category=VirtualCamera"
/// </code>
/// Windows PowerShell 에서는 <c>$env:GEVSHARP_VIRTUAL_CAMERA = "127.0.0.1"</c> 로 변수를 두고 같은 <c>dotnet test</c> 를 부른다.
/// 가짜 카메라는 표준 GVCP 포트 3956 에서 듣고, 지정한 인터페이스의 주소로 응답한다.
///
/// ⚠ 이 대향 장치는 실장치가 아니라 **부트스트랩을 부분적으로만 채운다** — 예를 들어 숫자 스펙 버전 레지스터(0x0000)를
/// 아예 쓰지 않아 0 으로 남는다. 여기서 단언하는 것은 "우리 스택이 장치와 규약대로 대화한다"이지 "장치가 규격을 다 채운다"가 아니다.
/// 값이 비어 있어 실패하는 단언을 만나면 라이브러리를 고치기 전에 그 레지스터를 이 장치가 실제로 채우는지부터 확인한다.
/// </summary>
[Trait("Category", "VirtualCamera")]
public class VirtualCameraTests
{
    private const string EnvVar = "GEVSHARP_VIRTUAL_CAMERA";
    private const int ProbeTimeoutMs = 2000;

    /// <summary>환경변수의 IPv4 주소. 없거나 IPv4 가 아니면 테스트를 건너뛴다.</summary>
    private static IPAddress RequireTarget()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        IPAddress? ip = null;
        if (!string.IsNullOrWhiteSpace(value)) IPAddress.TryParse(value!.Trim(), out ip);
        if (ip is null || ip.AddressFamily != AddressFamily.InterNetwork)
            Assert.Skip($"{EnvVar} is not set to an IPv4 address; the virtual-camera tests are skipped.");
        return ip!;
    }

    private static GevDeviceOpt DeviceOpt() => new()
    {
        GvcpTimeoutMs = 500,
        GvcpRetries = 2,
        HeartbeatTimeoutMs = 3000,
    };

    [Fact]
    public async Task Probe_UnicastOnPort3956_FindsTheFakeCamera()
    {
        var target = RequireTarget();

        var info = await GevDiscovery.ProbeAsync(target, ProbeTimeoutMs);

        Assert.True(info is not null, $"no DISCOVERY_ACK from {target}:{GvcpConst.Port} within {ProbeTimeoutMs} ms — is the virtual camera running on that interface?");
        Assert.False(string.IsNullOrWhiteSpace(info!.Manufacturer), "manufacturer string is empty");
        Assert.False(string.IsNullOrWhiteSpace(info.Model), "model string is empty");
        Assert.NotEqual(PhysicalAddress.None, info.Mac);
        Assert.Equal(AddressFamily.InterNetwork, info.Address.AddressFamily);
    }

    [Fact]
    public async Task Open_Control_ReadsTheBootstrapIdentity()
    {
        var target = RequireTarget();

        await using var dev = await GevDevice.OpenAsync(target, DeviceOpt());

        Assert.True(dev.IsOpen);
        Assert.Equal(GevAccessMode.Control, dev.AccessMode);
        Assert.False(string.IsNullOrWhiteSpace(dev.Info.Manufacturer));
        Assert.False(string.IsNullOrWhiteSpace(dev.Info.Model));
        // 숫자 스펙 버전 레지스터(0x0000)는 보지 않는다 — 이 대향 장치는 그 레지스터를 채우지 않고 0 으로 둔다.
        // 대신 버전 **문자열**(0x0088)이 차 있는지로 부트스트랩 읽기가 실제로 값을 가져왔음을 본다.
        Assert.False(string.IsNullOrWhiteSpace(dev.Info.DeviceVersion), "device version string (GVBS 0x0088) is empty");
        Assert.True(dev.DeviceHeartbeatTimeoutMs > 0, "device heartbeat timeout reads 0");
        Assert.True(dev.HeartbeatPeriodMs > 0);
    }

    [Fact]
    public async Task GetXml_RootElementIsRegisterDescription()
    {
        var target = RequireTarget();

        await using var dev = await GevDevice.OpenAsync(target, DeviceOpt());
        var doc = await dev.GetXmlAsync();

        Assert.False(string.IsNullOrEmpty(doc.Xml));
        var root = XDocument.Parse(doc.Xml).Root;
        Assert.NotNull(root);
        Assert.Equal("RegisterDescription", root!.Name.LocalName);
        Assert.Same(doc, await dev.GetXmlAsync());
    }

    [Fact]
    public async Task Stream_AcquisitionStartThroughGenApi_Receives10Frames()
    {
        var target = RequireTarget();

        await using var dev = await GevDevice.OpenAsync(target, DeviceOpt());
        var opt = new GevStreamOpt
        {
            BufferCount = 8,
            PacketSizeMode = PacketSizeMode.Auto,
            SocketBufferBytes = 8 * 1024 * 1024,
            ReceiverPriority = ThreadPriority.Normal,
        };
        await using var stream = await dev.OpenStreamAsync(opt);
        await stream.StartAsync();
        Assert.True(stream.IsStarted);
        Assert.True(stream.PacketSize >= GevStream.MinPacketSize);

        GenApiNodeMap nodeMap;
        try
        {
            nodeMap = await dev.GetNodeMapAsync();
        }
        catch (NotImplementedException ex)
        {
            await stream.StopAsync();
            Assert.Skip("GenApi runtime is not implemented yet, so AcquisitionStart cannot be sent through the node map: " + ex.Message);
            return;
        }

        var start = nodeMap.GetCommand("AcquisitionStart");
        var stop = nodeMap.GetCommand("AcquisitionStop");
        await start.ExecuteAsync();
        try
        {
            ulong previous = 0;
            for (var i = 0; i < 10; i++)
            {
                using var frame = await SimRig.ReceiveAsync(stream, 5000);
                Assert.True(frame.IsComplete, $"frame {frame.FrameId} incomplete: {frame.MissingPackets} of {frame.ExpectedPackets} packets missing");
                Assert.True(frame.Width > 0 && frame.Height > 0);
                Assert.True(frame.Stride >= frame.Width);
                Assert.Equal(frame.PayloadSize, frame.Data.Length);
                Assert.True(frame.FrameId > previous, $"frame id {frame.FrameId} after {previous}");
                previous = frame.FrameId;
            }
        }
        finally
        {
            await stop.ExecuteAsync();
        }

        await stream.StopAsync();
        Assert.False(stream.IsStarted);
        var s = stream.Stats.Snapshot();
        Assert.True(s.FramesDelivered >= 10);
        Assert.Equal(0, s.FramesDroppedNoBuffer);
    }
}
