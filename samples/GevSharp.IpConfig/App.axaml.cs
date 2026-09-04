using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GevSharp.IpConfig.ViewModels;
using GevSharp.IpConfig.Views;

namespace GevSharp.IpConfig;

public partial class App : Application
{
    public override void Initialize()
    {
        AttachLog();
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 환경변수 GEVSHARP_IPCONFIG_LOG 가 가리키는 파일로 라이브러리 로그를 받는다. 눌렀는데 아무 일도
    /// 일어나지 않는 경우를 잡으려면 무엇을 보냈는지가 남아 있어야 한다 — 화면 문구만으로는 알 수 없다.
    /// </summary>
    private static void AttachLog()
    {
        var path = Environment.GetEnvironmentVariable("GEVSHARP_IPCONFIG_LOG");
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
            var gate = new object();
            GevLog.MinLevel = GevLogLevel.Debug;
            GevLog.Sink = (level, source, message, ex) =>
            {
                lock (gate)
                {
                    writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level,-5} {source}: {message}");
                    if (ex is not null) writer.WriteLine($"    {ex.GetType().Name}: {ex.Message}");
                }
            };
        }
        catch
        {
            // 로그를 못 열어도 도구는 그대로 쓴다.
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = new MainVm() };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
