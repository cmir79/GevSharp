using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GevSharp.Viewer.ViewModels;
using GevSharp.Viewer.Views;

namespace GevSharp.Viewer;

public partial class App : Application
{
    private MainVm? _vm;
    private bool _hasReleased;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _vm = new MainVm();
            desktop.MainWindow = new MainWindow { DataContext = _vm };
            desktop.ShutdownRequested += OnShutdownRequested;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 창이 닫힐 때 카메라를 놓는다. 놓지 않고 프로세스가 사라지면 장치는 하트비트가 끊길 때까지 제어권을 쥐고 있어
    /// 다음 실행이 열지 못한다.
    /// <para>
    /// 정리는 반드시 <b>기다리지 않고</b> 해야 한다. 여기서 UI 스레드를 막고 결과를 기다리면, 정리 안의 이어지는 일들이
    /// 그 UI 스레드로 돌아오지 못해 서로를 기다리며 멈춘다 — 창은 닫혔는데 프로세스가 남아 카메라를 계속 물고 있는
    /// 상태가 되고, 놓는 코드가 있어도 끝까지 가지 못하므로 아예 없는 것보다 나쁘다.
    /// 그래서 종료를 한 번 미루고, 정리가 끝난 뒤에 스스로 종료한다.
    /// </para>
    /// </summary>
    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_hasReleased || _vm is null) return;

        e.Cancel = true;
        try
        {
            await _vm.ShutdownAsync();
        }
        catch
        {
            // 종료 경로에서는 더 할 수 있는 것이 없다. 그래도 종료는 계속한다.
        }

        _hasReleased = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
