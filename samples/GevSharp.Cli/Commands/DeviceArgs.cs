namespace GevSharp.Cli.Commands;

/// <summary>장치를 여는 명령들이 공유하는 인자 해석 — &lt;ip[:port]&gt; 위치 인자와 전역 <c>--access</c> 옵션.</summary>
public static class DeviceArgs
{
    /// <summary>전역 옵션 이름. <see cref="CliApp"/> 이 모든 명령 사양에 얹는다.</summary>
    public const string AccessOption = "access";

    public static DeviceTarget Target(CliArgs args, int index = 0) => DeviceTarget.Parse(args.Positional(index, "ip[:port]"));

    /// <summary>--access 가 없으면 명령별 기본 접근 모드. 읽기만 하는 명령은 ReadOnly 로 열어 다른 애플리케이션의 제어권을 건드리지 않는다.</summary>
    public static GevDeviceOpt BuildOpt(CliArgs args, GevAccessMode fallback)
    {
        var text = args.Get(AccessOption);
        var mode = fallback;
        if (text is not null)
        {
            mode = text.Trim().ToLowerInvariant() switch
            {
                "control" => GevAccessMode.Control,
                "exclusive" => GevAccessMode.Exclusive,
                "readonly" or "read-only" or "ro" => GevAccessMode.ReadOnly,
                _ => throw new CliUsageException($"option --{AccessOption} expects control | exclusive | readonly, got '{text}'"),
            };
        }
        return new GevDeviceOpt { AccessMode = mode };
    }

    public static string AccessName(GevAccessMode mode) => mode switch
    {
        GevAccessMode.Control => "control",
        GevAccessMode.Exclusive => "exclusive",
        GevAccessMode.ReadOnly => "read-only",
        _ => mode.ToString(),
    };
}
