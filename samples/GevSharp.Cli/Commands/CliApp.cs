using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GevSharp.Cli.Commands;

/// <summary>
/// 진입점 뒤의 얇은 조정자: 로그 싱크 설치 → 명령 이름 찾기 → 사양으로 파싱 → 전역 옵션 적용 → 실행 → 예외를 종료 코드로.
/// 라이브러리 로그는 언제나 stderr 로 가고 stdout 은 명령의 결과만 싣는다 — 파이프로 받는 쪽이 섞이지 않게.
/// </summary>
public static class CliApp
{
    public const string ToolName = "gevsharp-cli";
    private const string LogSrc = "GevSharp.Cli";

    private static readonly ICliCommand[] Commands =
    {
        new DiscoverCmd(),
        new InfoCmd(),
        new FeaturesCmd(),
        new GetCmd(),
        new SetCmd(),
        new GrabCmd(),
        new RegTestCmd(),
        new SimCmd(),
    };

    /// <summary>전역 옵션 — 모든 명령 사양에 얹는다.</summary>
    private static CliOptSpec GlobalSpec() => new CliOptSpec()
        .Flag("help", 'h')
        .Flag("version")
        .Flag("verbose")
        .Flag("quiet")
        .Value(DeviceArgs.AccessOption);

    public static IReadOnlyList<ICliCommand> AllCommands => Commands;

    public static ICliCommand? Find(string name)
    {
        foreach (var c in Commands)
        {
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return null;
    }

    public static async Task<int> RunAsync(string[] rawArgs, CancellationToken ct)
    {
        InstallLogSink();
        var tokens = rawArgs ?? Array.Empty<string>();

        // 명령 이름 = 옵션이 아닌 첫 토큰. 그 앞에 온 전역 옵션(--version, --help, --verbose)도 그대로 존중한다.
        // 값을 받는 전역 옵션(--access readonly)은 값 토큰까지 건너뛴다 — 그 값이 명령 이름으로 읽히면 안 된다.
        var globalSpec = GlobalSpec();
        var commandIndex = -1;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!tokens[i].StartsWith("-", StringComparison.Ordinal))
            {
                commandIndex = i;
                break;
            }
            if (ConsumesNextToken(tokens[i], globalSpec)) i++;
        }

        if (commandIndex < 0)
        {
            CliArgs globals;
            try
            {
                globals = CliArgs.Parse(tokens, GlobalSpec());
            }
            catch (CliUsageException ex)
            {
                return UsageError(ex.Message, null);
            }
            if (globals.Has("version"))
            {
                PrintVersion();
                return CliExitCode.Ok;
            }
            if (globals.Has("help"))
            {
                WriteUsage(Console.Out);
                return CliExitCode.Ok;
            }
            WriteUsage(Console.Error);
            return CliExitCode.Usage;
        }

        var command = Find(tokens[commandIndex]);
        if (command is null) return UsageError($"unknown command '{tokens[commandIndex]}'", null);

        var rest = new List<string>(tokens.Length);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (i != commandIndex) rest.Add(tokens[i]);
        }

        CliArgs args;
        try
        {
            args = CliArgs.Parse(rest, new CliOptSpec().Merge(command.Spec).Merge(GlobalSpec()));
        }
        catch (CliUsageException ex)
        {
            return UsageError(ex.Message, command);
        }

        if (args.Has("version"))
        {
            PrintVersion();
            return CliExitCode.Ok;
        }
        if (args.Has("help"))
        {
            WriteCommandUsage(command, Console.Out);
            return CliExitCode.Ok;
        }
        if (args.Has("verbose")) GevLog.MinLevel = GevLogLevel.Debug;
        else if (args.Has("quiet")) GevLog.MinLevel = GevLogLevel.Warn;

        try
        {
            return await command.RunAsync(args, ct);
        }
        catch (CliUsageException ex)
        {
            return UsageError(ex.Message, command);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("cancelled");
            return CliExitCode.Ok;
        }
        catch (GevStreamClosedException ex)
        {
            Console.Error.WriteLine($"stream error: {ex.Message}");
            return CliExitCode.Stream;
        }
        catch (ArgumentException ex)
        {
            // 라이브러리의 값 범위 검사(패킷 크기·버퍼 수 등)는 사용자가 준 값의 문제다.
            return UsageError(ex.Message, command);
        }
        catch (Exception ex) when (IsDeviceError(ex))
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (GevLog.MinLevel <= GevLogLevel.Debug) Console.Error.WriteLine(ex.ToString());
            return CliExitCode.Device;
        }
        catch (Exception ex)
        {
            // 예상 밖의 예외는 감추지 않고 전부 보여 준다 — 버그 보고에 그대로 쓸 수 있게.
            Console.Error.WriteLine($"unexpected error: {ex}");
            return CliExitCode.Device;
        }
    }

    /// <summary>값을 받는 전역 옵션이 값을 붙여 쓰지 않은 꼴(--access readonly, -x value)이면 다음 토큰이 그 값이다. 모르는 옵션은 파서가 나중에 거절한다.</summary>
    private static bool ConsumesNextToken(string token, CliOptSpec spec)
    {
        if (token == "--") return false;
        string name;
        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            name = token.Substring(2);
            if (name.IndexOf('=') >= 0) return false;
        }
        else if (token.Length != 2 || !spec.TryResolveShort(token[1], out name))
        {
            return false;
        }
        return spec.IsValued(name);
    }

    private static bool IsDeviceError(Exception ex) => ex is GevException
        or SocketException
        or IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or NotImplementedException
        or NotSupportedException
        or ObjectDisposedException
        or OperationCanceledException;

    private static int UsageError(string message, ICliCommand? command)
    {
        Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine();
        if (command is null) WriteUsage(Console.Error);
        else WriteCommandUsage(command, Console.Error);
        return CliExitCode.Usage;
    }

    // ------------------------------------------------------------------ usage / version

    public static void WriteUsage(TextWriter w)
    {
        w.WriteLine($"{ToolName} - evaluation console for GevSharp (GigE cameras: GVCP control, GVSP streaming, GenICam GenApi)");
        w.WriteLine();
        w.WriteLine($"usage: {ToolName} <command> [options]");
        w.WriteLine($"       {ToolName} <command> --help");
        w.WriteLine($"       {ToolName} --version | --help");
        w.WriteLine();
        w.WriteLine("commands:");
        foreach (var c in Commands) w.WriteLine($"  {c.Name,-10} {c.Summary}");
        w.WriteLine();
        w.WriteLine("<ip> accepts an optional :port suffix (default 3956) for simulators listening on another port.");
        w.WriteLine();
        w.WriteLine("global options:");
        w.WriteLine("  --verbose            library log level Debug (library logs always go to stderr; default level Info)");
        w.WriteLine("  --quiet              library log level Warn");
        w.WriteLine("  --access <mode>      control | exclusive | readonly (default: readonly for info/features/get, control otherwise)");
        w.WriteLine("  -h, --help           show this text, or a command's usage when placed after the command");
        w.WriteLine("  --version            print the version");
        w.WriteLine();
        w.WriteLine("exit codes: 0 ok, 1 usage error, 2 device error, 3 stream error");
    }

    public static void WriteCommandUsage(ICliCommand command, TextWriter w)
    {
        var lines = command.Usage.Split('\n');
        w.WriteLine($"usage: {ToolName} {lines[0].TrimEnd('\r')}");
        for (var i = 1; i < lines.Length; i++) w.WriteLine(lines[i].TrimEnd('\r'));
        w.WriteLine();
        w.WriteLine("global options: --verbose | --quiet | --access control|exclusive|readonly | --help");
    }

    private static void PrintVersion()
    {
        Console.WriteLine($"{ToolName} {VersionOf(typeof(CliApp).Assembly)} (GevSharp {VersionOf(typeof(GevDevice).Assembly)}, {RuntimeInformation.FrameworkDescription})");
    }

    private static string VersionOf(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? assembly.GetName().Version?.ToString()
           ?? "unknown";

    // ------------------------------------------------------------------ logging

    /// <summary>라이브러리 로그를 stderr 로. 기본 Info; --verbose 가 Debug 로, --quiet 가 Warn 으로 올린다.</summary>
    private static void InstallLogSink()
    {
        GevLog.Sink = (level, source, message, ex) =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {LevelTag(level)} {source}: {message}";
            if (ex is not null) line += $" | {ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(line);
        };
        GevLog.MinLevel = GevLogLevel.Info;
    }

    private static string LevelTag(GevLogLevel level) => level switch
    {
        GevLogLevel.Trace => "TRACE",
        GevLogLevel.Debug => "DEBUG",
        GevLogLevel.Info => "INFO ",
        GevLogLevel.Warn => "WARN ",
        GevLogLevel.Error => "ERROR",
        _ => level.ToString().ToUpperInvariant(),
    };

    /// <summary>명령 구현이 남기는 로그 — 라이브러리와 같은 싱크로 간다.</summary>
    public static void Log(GevLogLevel level, string message) => GevLog.Write(level, LogSrc, message);
}
