using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using GevSharp.IpConfig.ViewModels;

namespace GevSharp.IpConfig.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainVm? Vm => DataContext as MainVm;

    private void OnScan(object? sender, RoutedEventArgs e) => _ = Vm?.ScanAsync();

    // 어댑터 머리를 누르면 오른쪽이 호스트 쪽 이야기로 바뀐다.
    private void OnAdapterPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NicGroupVm group && Vm is { } vm) vm.SelectedGroup = group;
    }

    private void OnSuggest(object? sender, RoutedEventArgs e) => Vm?.Suggest();

    private void OnForce(object? sender, RoutedEventArgs e) => _ = Vm?.ForceAsync();

    private void OnPersist(object? sender, RoutedEventArgs e) => _ = Vm?.PersistAsync();

    private void OnSetName(object? sender, RoutedEventArgs e) => _ = Vm?.ApplyUserNameAsync();

    private void OnUseDhcp(object? sender, RoutedEventArgs e) => _ = Vm?.UseDhcpAsync();

    private void OnUseStored(object? sender, RoutedEventArgs e) => _ = Vm?.UseStoredAsync();

    private void OnOpenAdapter(object? sender, RoutedEventArgs e) => Vm?.OpenAdapterSettings();

    private void OnRenameAdapter(object? sender, RoutedEventArgs e) => _ = Vm?.RenameAdapterAsync();

    private void OnOpenConnections(object? sender, RoutedEventArgs e) => Vm?.OpenNetworkConnections();

    private void OnOpenFirewall(object? sender, RoutedEventArgs e) => Vm?.OpenFirewallSettings();
}
