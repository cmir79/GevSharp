using System.Net;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 브로드캐스트 탐색(모든 인터페이스) 또는 주소 하나에 대한 유니캐스트 프로브. 결과는 표 하나.
/// 표는 DISCOVERY_ACK 가 실어 온 식별 필드를 빠짐없이 보인다 — 장치 버전(펌웨어)도 그 응답에 이미 들어 있으므로,
/// 그 값 하나를 보려고 세션을 열 필요가 없다. 받아 온 것과 보여 주는 것이 어긋나면 "열어야 알 수 있다" 는 오해를 만든다.
/// </summary>
public sealed class DiscoverCmd : ICliCommand
{
    public string Name => "discover";

    public string Summary => "list devices answering broadcast discovery, or probe one address";

    public string Usage =>
        "discover [--timeout ms] [--interface ip]... [--probe ip[:port]]\n" +
        "  --timeout ms        reply collection window in milliseconds (default 1000)\n" +
        "  --interface ip      host interface to scan; repeatable (default: every IPv4 interface that is up, loopback excluded)\n" +
        "  --probe ip[:port]   send one unicast DISCOVERY_CMD to that address instead of broadcasting. Reaches devices behind\n" +
        "                      a router and loopback simulators, which never see a broadcast. Exit code 2 when nothing answers.\n" +
        "  Columns: IP, MAC, manufacturer, model, device version, serial number, user-defined name, interface that heard\n" +
        "  the reply. Everything shown comes from the discovery reply itself; no session is opened.";

    public CliOptSpec Spec { get; } = new CliOptSpec().Value("timeout").Value("interface").Value("probe");

    public async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        args.RejectExtraPositionals(0);
        var timeoutMs = args.GetInt("timeout", 1000, 1, 600_000);

        IReadOnlyList<GevDeviceInfo> devices;
        var probe = args.Get("probe");
        if (probe is not null)
        {
            var target = DeviceTarget.Parse(probe);
            var info = await target.ProbeAsync(timeoutMs, ct);
            if (info is null)
            {
                Console.Error.WriteLine($"no reply from {target} within {timeoutMs} ms");
                return CliExitCode.Device;
            }
            devices = new[] { info };
        }
        else
        {
            var opt = new GevDiscoveryOpt { TimeoutMs = timeoutMs };
            var interfaces = args.GetAll("interface");
            if (interfaces.Count > 0) opt.Interfaces = interfaces.Select(ParseInterface).ToArray();
            devices = await GevDiscovery.DiscoverAsync(opt, ct);
        }

        var table = new TextTable("IP", "MAC", "Manufacturer", "Model", "Version", "Serial", "User name", "Interface");
        foreach (var d in devices)
        {
            table.AddRow(d.Address.ToString(), NetText.Mac(d.Mac), d.Manufacturer, d.Model, d.DeviceVersion, d.SerialNumber, d.UserDefinedName,
                d.InterfaceAddress.ToString());
        }
        if (devices.Count == 0)
        {
            Console.WriteLine("no devices found");
        }
        else
        {
            table.Write(Console.Out);
            Console.WriteLine($"{devices.Count} device(s)");
        }
        return CliExitCode.Ok;
    }

    private static IPAddress ParseInterface(string text)
    {
        if (IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return address;
        throw new CliUsageException($"option --interface expects an IPv4 address, got '{text}'");
    }
}
