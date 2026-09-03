using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GevSharp.Viewer.ViewModels;

namespace GevSharp.Viewer.Views;

public partial class MainWindow : Window
{
    private CamVm? _panning;
    private CamVm? _swapFrom;
    private Point _panFrom;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainVm? Vm => DataContext as MainVm;

    private void OnDiscover(object? sender, RoutedEventArgs e) => _ = Vm?.DiscoverAsync();

    private void OnConnect(object? sender, RoutedEventArgs e) => _ = Vm?.ConnectAsync();

    private void OnDisconnect(object? sender, RoutedEventArgs e) => _ = Vm?.CloseSelectedAsync();

    private void OnStartLive(object? sender, RoutedEventArgs e) => _ = Vm?.StartLiveAsync();

    private void OnStopLive(object? sender, RoutedEventArgs e) => _ = Vm?.StopLiveAsync();

    private void OnFit(object? sender, RoutedEventArgs e) => Vm?.SelectedCam?.Fit();

    private void OnToggleLayout(object? sender, RoutedEventArgs e)
    {
        if (Vm is { CanChangeLayout: true } vm) vm.IsFocusLayout = !vm.IsFocusLayout;
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedCam: { } cam } vm) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the current frame",
            SuggestedFileName = $"{cam.Address}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } },
        });

        if (file?.TryGetLocalPath() is not { } path) return;
        vm.SaveFrame(cam, path);
    }

    // 격자에서 타일을 누르면 속성 트리와 조작 단추가 그 카메라를 가리킨다. 누른 채로 끌면 그림이 따라 움직인다.
    private void OnImagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not CamVm cam) return;
        vm.SelectedCam = cam;

        // 오른쪽 단추로 끌면 타일끼리 자리를 맞바꾼다. 그림을 옮기는 것과 섞이지 않게 단추를 나눠 둔다.
        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsRightButtonPressed)
        {
            _swapFrom = cam;
            return;
        }

        if (!point.IsLeftButtonPressed) return;
        _panning = cam;
        _panFrom = e.GetPosition(this);
        if (sender is Control c) c.Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void OnImageMoved(object? sender, PointerEventArgs e)
    {
        if ((sender as Control) is not { } control || control.DataContext is not CamVm cam) return;

        if (ReferenceEquals(_panning, cam))
        {
            var now = e.GetPosition(this);
            cam.PanBy(now.X - _panFrom.X, now.Y - _panFrom.Y);
            _panFrom = now;
            return;
        }

        // 끌고 있지 않을 때는 커서 아래 화소를 알려 준다.
        var p = e.GetPosition(control);
        if (ToPixel(control, cam, p) is { } px) cam.ReportCursorPixel(px.X, px.Y);
        else cam.ClearCursor();
    }

    private void OnImageReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panning = null;
        if (sender is Control c) c.Cursor = Cursor.Default;

        if (_swapFrom is not { } from) return;
        _swapFrom = null;
        if (CamUnder(e.GetPosition(this)) is { } onto && Vm is { } vm) vm.SwapCams(from, onto);
    }

    /// <summary>그 지점에 있는 타일의 카메라. 놓은 자리가 어느 타일인지 알아야 자리를 바꿀 수 있다.</summary>
    private CamVm? CamUnder(Point at)
        => (this.InputHitTest(at) as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(c => c.DataContext)
            .OfType<CamVm>()
            .FirstOrDefault();

    private void OnImageExited(object? sender, PointerEventArgs e)
    {
        if ((sender as Control)?.DataContext is CamVm cam) cam.ClearCursor();
    }

    // 휠은 커서를 기준으로 확대한다 — 보던 지점이 제자리에 남아야 확대가 쓸모 있다.
    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((sender as Control) is not { } control || control.DataContext is not CamVm cam) return;
        var at = e.GetPosition(control);
        cam.ZoomAt(e.Delta.Y > 0 ? 1.25 : 1 / 1.25, at.X, at.Y);
        e.Handled = true;
    }

    /// <summary>
    /// 화면 위 한 점을 그림의 화소 좌표로 푼다. 그림은 창에 맞춰(Uniform) 그린 뒤 확대·이동 변환을 얹으므로,
    /// 그 두 단계를 거꾸로 되짚는다. 그림 밖이면 null.
    /// </summary>
    private static PixelPoint? ToPixel(Control control, CamVm cam, Point at)
    {
        if (cam.PixelSize is not { } size || size.Width <= 0 || size.Height <= 0) return null;

        // 확대·이동 되돌리기.
        var x = (at.X - cam.OffsetX) / cam.Zoom;
        var y = (at.Y - cam.OffsetY) / cam.Zoom;

        // 창에 맞춘 배치 되돌리기 — 남는 쪽은 가운데 정렬로 여백이 된다.
        var cw = control.Bounds.Width;
        var ch = control.Bounds.Height;
        if (cw <= 0 || ch <= 0) return null;
        var fit = Math.Min(cw / size.Width, ch / size.Height);
        if (fit <= 0) return null;

        var px = (int)((x - (cw - size.Width * fit) / 2) / fit);
        var py = (int)((y - (ch - size.Height * fit) / 2) / fit);
        return px < 0 || py < 0 || px >= size.Width || py >= size.Height ? null : new PixelPoint(px, py);
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
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not NodeVm node) return;
        _ = Vm?.SelectNodeAsync(node);

        // 줄을 고르면 커서가 값 편집기로 바로 들어가게 한다 — 피처 이름에 포커스가 머무르면 한 번 더 눌러야 한다.
        // 컨테이너는 고른 직후에야 만들어지므로 한 박자 뒤에 찾는다.
        if (sender is TreeView tree) Dispatcher.UIThread.Post(() => FocusEditor(tree, node), DispatcherPriority.Background);
    }

    /// <summary>그 노드의 값 편집기를 찾아 커서를 준다. 글 칸이면 내용을 통째로 잡아 두어 바로 덮어쓸 수 있게 한다.</summary>
    private static void FocusEditor(TreeView tree, NodeVm node)
    {
        var editor = tree.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => c is TextBox or ComboBox && c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, node));

        if (editor is TextBox { IsReadOnly: false } box)
        {
            box.Focus();
            box.SelectAll();
        }
        else if (editor is ComboBox { IsEffectivelyEnabled: true } combo)
        {
            combo.Focus();
        }
    }

    // 값은 Enter 로 쓴다 — 글자를 칠 때마다 쓰면 중간 상태가 장치로 나간다.
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
}
