using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GevSharp.Viewer.ViewModels;
using GevSharp.Viewer.Views;

namespace GevSharp.Viewer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainVm();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            // 창이 닫히면 스트림과 장치를 놓아 준다 — 카메라를 물고 있으면 다음 실행이 열지 못한다.
            desktop.ShutdownRequested += (_, _) => vm.ShutdownAsync().GetAwaiter().GetResult();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
