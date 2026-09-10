using System.ComponentModel;
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
        DataContextChanged += (_, _) => HookSelection();
        HookSelection();
    }

    private MainVm? _hooked;

    private void HookSelection()
    {
        if (_hooked is not null) _hooked.PropertyChanged -= OnVmPropertyChanged;
        _hooked = Vm;
        if (_hooked is null) return;
        _hooked.PropertyChanged += OnVmPropertyChanged;
        SyncSelectedRow();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainVm.SelectedRow)) SyncSelectedRow();
    }

    /// <summary>
    /// VM 이 고른 행을 목록에 그대로 옮긴다. SelectedItem 의 두 방향 바인딩은 목록→VM 은 되는데 VM→목록이 오지 않았다 —
    /// 실측으로 목록이 그 항목을 갖고 VM 도 골랐는데 SelectedIndex 가 -1 에 머물렀고, 직접 넣으면 바로 선택됐다. 그래서
    /// 바인딩은 목록→VM 한 방향만 두고 이 방향은 여기서 직접 한다. 같은 행이면 손대지 않아 되돌이가 없다.
    /// <para>
    /// 목록을 방금 새로 채운 같은 호출 안에서는 넣은 값이 붙지 않는다(첫 화면의 자동 선택이 그 경우다). 붙지 않았으면 한 차례
    /// 뒤에 한 번 더 넣는다 — 그때는 목록이 항목을 다 받아들인 뒤라 붙는다.
    /// </para>
    /// </summary>
    private void SyncSelectedRow()
    {
        if (Vm is not { } vm || this.FindControl<ListBox>("DeviceList") is not { } list) return;
        var row = vm.SelectedRow;
        if (ReferenceEquals(list.SelectedItem, row)) return;
        list.SelectedItem = row;
        if (!ReferenceEquals(list.SelectedItem, row))
            Avalonia.Threading.Dispatcher.UIThread.Post(SyncSelectedRow, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainVm? Vm => DataContext as MainVm;

    private void OnScan(object? sender, RoutedEventArgs e) => _ = Vm?.ScanAsync();

    private void OnSuggest(object? sender, RoutedEventArgs e) => Vm?.Suggest();

    private void OnSetName(object? sender, RoutedEventArgs e) => _ = Vm?.ApplyUserNameAsync();

    private void OnApply(object? sender, RoutedEventArgs e) => _ = Vm?.ApplyAsync();

    private void OnOpenAdapter(object? sender, RoutedEventArgs e) => Vm?.OpenAdapterSettings();

    private void OnRenameAdapter(object? sender, RoutedEventArgs e) => _ = Vm?.RenameAdapterAsync();

    private void OnOpenConnections(object? sender, RoutedEventArgs e) => Vm?.OpenNetworkConnections();

    private void OnOpenFirewall(object? sender, RoutedEventArgs e) => Vm?.OpenFirewallSettings();
}
