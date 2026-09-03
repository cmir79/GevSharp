using GevSharp.Gvcp;
using GevSharp.Xml;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 장치 하나의 부트스트랩 블록·GVCP 능력·하트비트·틱 주파수·스트림 채널 0·XML URL 을 보여 준다.
/// 기본은 읽기 전용 세션(CCP 를 건드리지 않고 하트비트도 없다). 장치가 거절한 레지스터는 그 줄에만 표시하고 계속 진행한다.
/// </summary>
public sealed class InfoCmd : ICliCommand
{
    private const int KeyWidth = 22;

    public string Name => "info";

    public string Summary => "bootstrap registers, GVCP capabilities, stream channel and XML URLs of one device";

    public string Usage =>
        "info <ip[:port]> [--save-xml file]\n" +
        "  --save-xml file     also fetch the camera XML and write it there as UTF-8 (a ZIP is unpacked first)\n" +
        "  Opens the device read-only (no control, no heartbeat) unless --access says otherwise, then prints the bootstrap\n" +
        "  block, GVCP capability bits, heartbeat timeout, timestamp tick frequency, stream channel 0 registers and the\n" +
        "  XML URL registers. A register the device refuses is shown inline as unreadable; the command still completes.";

    public CliOptSpec Spec { get; } = new CliOptSpec().Value("save-xml");

    public async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var target = DeviceArgs.Target(args);
        args.RejectExtraPositionals(1);
        var opt = DeviceArgs.BuildOpt(args, GevAccessMode.ReadOnly);

        await using var dev = await target.OpenAsync(opt, ct);
        var info = dev.Info;
        var w = Console.Out;

        w.WriteLine($"Device {target} via {dev.LocalAddress} ({DeviceArgs.AccessName(dev.AccessMode)} session)");

        Section(w, "Bootstrap");
        Line(w, "Version", $"{info.SpecMajor}.{info.SpecMinor}");
        Line(w, "Device mode", $"0x{info.DeviceMode:X8} ({(info.IsBigEndianDevice ? "big-endian" : "little-endian")} device, character set {NetText.CharacterSet(info.CharacterSet)})");
        Line(w, "MAC", NetText.Mac(info.Mac));
        Line(w, "IP configuration", $"supported: {NetText.IpCfg(info.SupportedIpCfg)}; current: {NetText.IpCfg(info.CurrentIpCfg)}");
        Line(w, "Current IP", $"{info.Address} / {info.Subnet}, gateway {info.Gateway}");
        Line(w, "Manufacturer", NetText.Text(info.Manufacturer));
        Line(w, "Model", NetText.Text(info.Model));
        Line(w, "Device version", NetText.Text(info.DeviceVersion));
        Line(w, "Manufacturer info", NetText.Text(info.ManufacturerInfo));
        Line(w, "Serial number", NetText.Text(info.SerialNumber));
        Line(w, "User-defined name", NetText.Text(info.UserDefinedName));
        Line(w, "Network interfaces", await RegAsync(dev, GvbsAddr.NumNetworkInterfaces, v => v.ToString(), ct)
                                      + ", link speed " + await RegAsync(dev, GvbsAddr.LinkSpeed0, v => $"{v} Mbit/s", ct));
        Line(w, "Channels", $"stream {await RegAsync(dev, GvbsAddr.NumStreamChannels, v => v.ToString(), ct)}, "
                            + $"message {await RegAsync(dev, GvbsAddr.NumMessageChannels, v => v.ToString(), ct)}, "
                            + $"action signals {await RegAsync(dev, GvbsAddr.NumActionSignals, v => v.ToString(), ct)}, "
                            + $"active links {await RegAsync(dev, GvbsAddr.NumActiveLinks, v => v.ToString(), ct)}");

        Section(w, "GVCP");
        Line(w, "Capability", $"0x{dev.GvcpCapability:X8}: {NetText.GvcpCap(dev.GvcpCapability)}");
        var heartbeat = $"{dev.DeviceHeartbeatTimeoutMs} ms";
        if (dev.HeartbeatPeriodMs > 0) heartbeat += $" (this session sends a heartbeat every {dev.HeartbeatPeriodMs} ms)";
        Line(w, "Heartbeat timeout", heartbeat);
        Line(w, "Timestamp tick freq", dev.TimestampTickFrequency == 0 ? "0 (not readable or not supported)" : $"{dev.TimestampTickFrequency} Hz");
        if ((dev.GvcpCapability & GvbsAddr.GvcpCapPendingAck) != 0)
            Line(w, "Pending timeout", await RegAsync(dev, GvbsAddr.PendingTimeout, v => $"{v} ms", ct));
        Line(w, "GVSP capability", await RegAsync(dev, GvbsAddr.GvspCapability, v => $"0x{v:X8}", ct));
        Line(w, "CCP", await RegAsync(dev, GvbsAddr.Ccp, v => $"0x{v:X8} ({NetText.Ccp(v)})", ct));
        Line(w, "Primary application", await RegAsync(dev, GvbsAddr.PrimaryAppIp, v => NetText.Ipv4(v).ToString(), ct)
                                       + ":" + await RegAsync(dev, GvbsAddr.PrimaryAppPort, v => (v & 0xFFFF).ToString(), ct));

        Section(w, "Stream channel 0");
        Line(w, "SCP host port", await RegAsync(dev, GvbsAddr.StreamChannel(0, GvbsAddr.ScpOffset), v => (v & 0xFFFF) == 0 ? "0 (closed)" : (v & 0xFFFF).ToString(), ct));
        Line(w, "SCPS packet size", await RegAsync(dev, GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset), NetText.Scps, ct));
        Line(w, "SCPD packet delay", await RegAsync(dev, GvbsAddr.StreamChannel(0, GvbsAddr.ScpdOffset), v => $"{v} ticks", ct));
        Line(w, "SCDA destination", await RegAsync(dev, GvbsAddr.StreamChannel(0, GvbsAddr.ScdaOffset), v => NetText.Ipv4(v).ToString(), ct));
        Line(w, "SCSP source port", await RegAsync(dev, GvbsAddr.StreamChannel(0, GvbsAddr.ScspOffset), v => (v & 0xFFFF).ToString(), ct));
        Line(w, "SCC capability", await RegAsync(dev, GvbsAddr.StreamChannel(0, GvbsAddr.SccOffset), v => $"0x{v:X8}", ct));
        Line(w, "SCCFG configuration", await RegAsync(dev, GvbsAddr.StreamChannel(0, GvbsAddr.SccfgOffset), v => $"0x{v:X8}", ct));

        Section(w, "XML");
        Line(w, "First URL", await UrlAsync(dev, GvbsAddr.FirstUrl, ct));
        Line(w, "Second URL", await UrlAsync(dev, GvbsAddr.SecondUrl, ct));

        var savePath = args.Get("save-xml");
        if (savePath is not null) await SaveXmlAsync(dev, savePath, w, ct);
        return CliExitCode.Ok;
    }

    /// <summary>
    /// 카메라 XML 을 파일로 남긴다 — 벤더 XML 을 살펴보려고 매번 임시 도구를 짜는 일을 없앤다.
    /// 받아 온 것이 ZIP 이어도 풀린 XML 텍스트가 나온다. 가져오기가 실패해도 info 의 나머지 출력은 그대로 유효하므로
    /// 이유만 알리고 성공으로 끝낸다 — 레지스터 한 칸을 못 읽었을 때와 같은 취급이다.
    /// </summary>
    private static async Task SaveXmlAsync(GevDevice dev, string path, TextWriter w, CancellationToken ct)
    {
        Section(w, "Camera XML");
        try
        {
            var doc = await dev.GetXmlAsync(ct);
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            // BOM 없는 UTF-8 — 앞 세 바이트를 XML 로 읽으려다 걸리는 도구가 있다.
            File.WriteAllText(full, doc.Xml, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Line(w, "Saved", $"{full} ({doc.Xml.Length} chars)");
            Line(w, "Source", $"{doc.Url} (file name {doc.FileName})");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Line(w, "Saved", $"<not saved: {ex.Message}>");
        }
    }

    private static void Section(TextWriter w, string title)
    {
        w.WriteLine();
        w.WriteLine($"[{title}]");
    }

    private static void Line(TextWriter w, string key, string value) => w.WriteLine($"  {key.PadRight(KeyWidth)} {value}");

    /// <summary>레지스터 하나를 읽어 포맷한다. 거절·타임아웃은 그 이유를 실은 문자열로 — 출력이 중간에 끊기지 않게.</summary>
    private static async Task<string> RegAsync(GevDevice dev, uint addr, Func<uint, string> format, CancellationToken ct)
    {
        try
        {
            return format(await dev.ReadRegAsync(addr, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GevException ex)
        {
            return $"(unreadable: {Reason(ex)})";
        }
    }

    private static string Reason(GevException ex) => ex switch
    {
        GevStatusException status => GvcpConst.StatusName(status.Status),
        GevTimeoutException => "timeout",
        _ => ex.Message,
    };

    private static async Task<string> UrlAsync(GevDevice dev, uint addr, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await dev.ReadStringAsync(addr, GvbsAddr.UrlLen, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (GevException ex)
        {
            return $"(unreadable: {Reason(ex)})";
        }

        if (string.IsNullOrWhiteSpace(raw)) return "(empty)";
        if (!GevXmlUrl.TryParse(raw, out var url) || url is null) return $"{raw}  (not a recognised XML URL)";
        var detail = url.Kind switch
        {
            GevXmlUrlKind.Local => $"device memory, file {url.FileName}, address 0x{url.Address:X8}, length {url.Length} bytes",
            GevXmlUrlKind.File => $"host file {url.FilePath}",
            GevXmlUrlKind.Http => $"http {url.HttpUri}",
            _ => url.Kind.ToString(),
        };
        if (url.IsZip) detail += ", zip";
        if (url.SchemaVersion is not null) detail += $", schema {url.SchemaVersion}";
        return $"{raw}  ({detail})";
    }
}
