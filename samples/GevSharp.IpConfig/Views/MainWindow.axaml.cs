using Avalonia.Controls;
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

    private void OnSuggest(object? sender, RoutedEventArgs e) => Vm?.Suggest();

    private void OnForce(object? sender, RoutedEventArgs e) => _ = Vm?.ForceAsync();

    private void OnPersist(object? sender, RoutedEventArgs e) => _ = Vm?.PersistAsync();

    private void OnUseDhcp(object? sender, RoutedEventArgs e) => _ = Vm?.UseDhcpAsync();

    private void OnUseStored(object? sender, RoutedEventArgs e) => _ = Vm?.UseStoredAsync();

    private void OnOpenAdapter(object? sender, RoutedEventArgs e) => Vm?.OpenAdapterSettings();

    private void OnOpenFirewall(object? sender, RoutedEventArgs e) => Vm?.OpenFirewallSettings();
}
