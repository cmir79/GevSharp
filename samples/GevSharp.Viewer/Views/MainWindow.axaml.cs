using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    // 커서가 들어온 칸은 주기 갱신이 건드리지 않는다.
    private void OnValueGotFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NodeVm node) node.BeginEdit();
    }

    // 칸을 벗어날 때도 쓴다 — Enter 를 안 치고 다른 곳을 눌러도 고친 값이 사라지지 않게.
    private void OnValueLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not NodeVm node) return;
        node.EndEdit();
        if (node.IsDirty) _ = node.CommitTextAsync();
    }

    private void OnExecute(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NodeVm node) _ = node.ExecuteAsync();
    }

    // 이름칸과 값칸의 경계를 끄는 동작. 오른쪽이 값칸이므로 오른쪽으로 끌면 값칸이 좁아진다.
    private void OnValueColumnDrag(object? sender, VectorEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.ValueColumnWidth = Math.Clamp(vm.ValueColumnWidth - e.Vector.X, 70, 400);
    }

    // 값 읽기는 UDP 왕복이라 트리를 통째로 읽지 않는다. 고른 것만 읽고, 카테고리를 고르면 그 안을 읽는다.
    private void OnNodeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is NodeVm node) _ = Vm?.SelectNodeAsync(node);
    }
}
