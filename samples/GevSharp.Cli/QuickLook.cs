using GevSharp.Pfnc;

namespace GevSharp.Cli;

/// <summary>
/// README 의 "Quick look" 예제와 같은 코드. 실행되지 않지만 **컴파일된다** —
/// 공개 API 가 바뀌면 여기서 빌드가 깨지므로, 사용자가 처음 복사해 가는 그 예제가 조용히 낡지 않는다.
/// (NuGet 페이지에 실리는 것도 이 예제다: GevSharp.csproj 의 PackageReadmeFile.)
/// README 를 고치면 이 파일도 같이 고친다.
/// </summary>
internal static class QuickLook
{
    public static async Task RunAsync()
    {
        var devices = await GevDiscovery.DiscoverAsync();           // all interfaces, broadcast + subnet-directed
        await using var dev = await GevDevice.OpenAsync(devices[0]); // takes control, starts heartbeat
        var nodes = await dev.GetNodeMapAsync();                     // fetches and parses the camera XML

        await nodes.GetFloat("ExposureTime").SetAsync(2000);
        await nodes.GetEnumeration("PixelFormat").SetAsync("Mono8");

        await using var stream = await dev.OpenStreamAsync();
        await stream.StartAsync();

        // Tell the node map the transport layer is configured. Vendor descriptions gate the acquisition
        // commands on this (AcquisitionStart is a locked write-only node until it is set), and lock the
        // format parameters while it holds.
        await dev.SetTlParamsLockedAsync(true);
        await nodes.GetCommand("AcquisitionStart").ExecuteAsync();

        for (var i = 0; i < 10; i++)
        {
            using var frame = await stream.ReceiveAsync();           // complete frames only
            Console.WriteLine($"{frame.FrameId}: {frame.Width}x{frame.Height} "
                + $"{PixelFormatInfo.Name(frame.PixelFormatCode)} stride={frame.Stride}");
        }

        await nodes.GetCommand("AcquisitionStop").ExecuteAsync();
        await dev.SetTlParamsLockedAsync(false);
    }
}
