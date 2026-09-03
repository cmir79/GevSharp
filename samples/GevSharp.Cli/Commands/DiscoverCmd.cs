using System.Net;

namespace GevSharp.Cli.Commands;

/// <summary>브로드캐스트 탐색(모든 인터페이스) 또는 주소 하나에 대한 유니캐스트 프로브. 결과는 표 하나.</summary>
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
        "  Columns: IP, MAC, manufacturer, model, serial number, user-defined name, interface that heard the reply.";

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

        var table = new TextTable("IP", "MAC", "Manufacturer", "Model", "Serial", "User name", "Interface");
        foreach (var d in devices)
        {
            table.AddRow(d.Address.ToString(), NetText.Mac(d.Mac), d.Manufacturer, d.Model, d.SerialNumber, d.UserDefinedName, d.InterfaceAddress.ToString());
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
