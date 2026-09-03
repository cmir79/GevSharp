using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GevSharp.Viewer.ViewModels;

namespace GevSharp.Viewer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainVm? Vm => DataContext as MainVm;

    private void OnDiscover(object? sender, RoutedEventArgs e) => _ = Vm?.DiscoverAsync();

    private void OnConnect(object? sender, RoutedEventArgs e) => _ = Vm?.ConnectAsync();

    private void OnDisconnect(object? sender, RoutedEventArgs e) => _ = Vm?.DisconnectAsync();

    private void OnStartLive(object? sender, RoutedEventArgs e) => _ = Vm?.StartLiveAsync();

    private void OnStopLive(object? sender, RoutedEventArgs e) => _ = Vm?.StopLiveAsync();

    // 값은 Enter 로만 쓴다 — 글자를 칠 때마다 쓰면 중간 상태가 장치로 나간다.
    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (sender as Control)?.DataContext is not NodeVm node) return;
        e.Handled = true;
        _ = node.CommitTextAsync();
    }

    private void OnExecute(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NodeVm node) _ = node.ExecuteAsync();
    }
}
