using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GevSharp.Gvcp;
using GevSharp.Gvsp;
using GevSharp.Viewer.Imaging;

namespace GevSharp.Viewer.ViewModels;

/// <summary>
/// 열려 있는 카메라 한 대. 장치·스트림·노드 트리·화면을 한 덩이로 들고 있어서 여러 대를 동시에 열어도
/// 서로 섞이지 않는다. 어느 것도 벤더를 구분하지 않는다 — 트리는 카메라가 준 XML 이 그대로 만든다.
/// </summary>
public sealed class CamVm : VmBase, IAsyncDisposable
{
    private readonly FrameRender _render = new();
    private readonly Action<string, bool> _report;
    private GevStream? _stream;
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;

    private bool _isLive;
    private bool _isSelected;
    private Bitmap? _image;
    private string _frameInfo = "";
    private string _streamInfo = "";
    private string? _selectedTitle;
    private string? _selectedHint;
    private NodeVm? _selectedNode;
    private double _zoom = 1;
    private double _offsetX;
    private double _offsetY;
    private string _cursorInfo = "";

    private CamVm(GevDevice device, GevDeviceInfo info, Action<string, bool> report)
    {
        Device = device;
        Info = info;
        _report = report;
    }

    public static async Task<CamVm> OpenAsync(GevDeviceInfo info, Action<string, bool> report)
    {
        var device = await GevDevice.OpenAsync(info).ConfigureAwait(true);
        var cam = new CamVm(device, info, report);
        var map = await device.GetNodeMapAsync().ConfigureAwait(true);
        // 루트는 그리지 않는다 — 이름이 장치마다 제각각이고 한 겹 더 펼치게 만들 뿐이다.
        foreach (var feature in map.Root.Features) cam.Nodes.Add(new NodeVm(feature, null, report));
        cam.NodeCount = map.Nodes.Count;
        return cam;
    }

    public GevDevice Device { get; }
    public GevDeviceInfo Info { get; }
    public int NodeCount { get; private set; }

    public ObservableCollection<NodeVm> Nodes { get; } = new();

    public string Title => string.IsNullOrWhiteSpace(Info.UserDefinedName)
        ? $"{Info.Manufacturer} {Info.Model}"
        : $"{Info.UserDefinedName} ({Info.Model})";

    public string Address => Info.Address.ToString();

    /// <summary>격자에서 고른 타일인지. 속성 트리와 조작 단추가 가리키는 카메라를 눈으로 알 수 있어야 한다.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    public bool IsLive
    {
        get => _isLive;
        private set { if (Set(ref _isLive, value)) Raise(nameof(IsNotLive)); }
    }

    public bool IsNotLive => !_isLive;

    public Bitmap? Image
    {
        get => _image;
        private set => Set(ref _image, value);
    }

    public string FrameInfo
    {
        get => _frameInfo;
        private set => Set(ref _frameInfo, value);
    }

    public string StreamInfo
    {
        get => _streamInfo;
        private set => Set(ref _streamInfo, value);
    }

    public string? SelectedTitle
    {
        get => _selectedTitle;
        private set => Set(ref _selectedTitle, value);
    }

    public string? SelectedHint
    {
        get => _selectedHint;
        private set => Set(ref _selectedHint, value);
    }

    /// <summary>
    /// 화면 배율. 1 이 창에 맞춘 크기다 — 원본 배율이 아니라 맞춤 배율 기준이라, 창 크기가 달라져도 보던 만큼이 유지된다.
    /// </summary>
    public double Zoom
    {
        get => _zoom;
        private set
        {
            if (!Set(ref _zoom, value)) return;
            Raise(nameof(ViewTransform));
            Raise(nameof(ZoomText));
        }
    }

    public string ZoomText => $"{Zoom * 100:F0}%";

    /// <summary>확대한 그림을 끌어 옮긴 양(화면 좌표).</summary>
    public double OffsetX
    {
        get => _offsetX;
        private set { if (Set(ref _offsetX, value)) Raise(nameof(ViewTransform)); }
    }

    public double OffsetY
    {
        get => _offsetY;
        private set { if (Set(ref _offsetY, value)) Raise(nameof(ViewTransform)); }
    }

    /// <summary>확대·이동을 하나로 묶은 변환. 그림 자체는 창에 맞춰 그린 뒤 그 위에 이 변환을 얹는다.</summary>
    public ITransform ViewTransform
    {
        get
        {
            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(Zoom, Zoom));
            group.Children.Add(new TranslateTransform(OffsetX, OffsetY));
            return group;
        }
    }

    /// <summary>커서 아래 화소의 좌표와 값. 원본 화소가 아니라 화면에 그려진 값이다.</summary>
    public string CursorInfo
    {
        get => _cursorInfo;
        private set => Set(ref _cursorInfo, value);
    }

    /// <summary>창에 맞춘 크기로 되돌린다.</summary>
    public void Fit()
    {
        Zoom = 1;
        OffsetX = 0;
        OffsetY = 0;
    }

    /// <summary>커서를 기준으로 확대·축소한다 — 커서 아래의 지점이 제자리에 남도록 이동량을 함께 옮긴다.</summary>
    public void ZoomAt(double factor, double anchorX, double anchorY)
    {
        var next = Math.Clamp(Zoom * factor, 1, 40);
        if (Math.Abs(next - Zoom) < double.Epsilon) return;

        var ratio = next / Zoom;
        OffsetX = anchorX - (anchorX - OffsetX) * ratio;
        OffsetY = anchorY - (anchorY - OffsetY) * ratio;
        Zoom = next;
    }

    public void PanBy(double dx, double dy)
    {
        OffsetX += dx;
        OffsetY += dy;
    }

    /// <summary>그림의 화소 좌표를 받아 그 자리의 값을 적는다. 그림 밖이면 지운다.</summary>
    public void ReportCursorPixel(int px, int py)
    {
        var pixel = _render.ReadDisplayPixel(px, py);
        if (pixel is not { } p)
        {
            CursorInfo = "";
            return;
        }

        CursorInfo = p.R == p.G && p.G == p.B
            ? $"({px}, {py})  gray {p.R}"
            : $"({px}, {py})  R {p.R}  G {p.G}  B {p.B}";
    }

    public void ClearCursor() => CursorInfo = "";

    /// <summary>지금 화면에 걸린 그림의 크기. 화면 좌표를 화소로 풀 때 쓴다.</summary>
    public (int Width, int Height)? PixelSize => Image is { } b ? (b.PixelSize.Width, b.PixelSize.Height) : null;

    /// <summary>지금 화면에 걸린 그림을 파일로 쓴다. 화면에 보이는 그대로이며 확대·이동은 담기지 않는다.</summary>
    public void SaveCurrent(string path)
    {
        var bmp = _render.Current ?? throw new InvalidOperationException("There is no frame to save yet.");
        bmp.Save(path);
    }

    /// <summary>트리 선택이 바뀌면 그 노드를 읽고 설명을 화면 아래에 건다.</summary>
    public async Task SelectNodeAsync(NodeVm node)
    {
        _selectedNode = node;
        SelectedTitle = node.Unit is { Length: > 0 } unit ? $"{node.Label}  ({node.Name}, {unit})" : $"{node.Label}  ({node.Name})";
        SelectedHint = node.Hint;

        await node.LoadAsync().ConfigureAwait(true);
        if (ReferenceEquals(node, _selectedNode) && !node.IsCategory) SelectedTitle += $"   —   {node.Access}";
    }

    /// <summary>고른 무리만 다시 읽는다. 트리 전체를 훑으면 읽기 왕복이 걷잡을 수 없이 는다.</summary>
    public Task PollAsync()
    {
        var target = _selectedNode?.IsCategory == true ? _selectedNode : _selectedNode?.Parent ?? _selectedNode;
        return target is null ? Task.CompletedTask : target.PollAsync();
    }

    public async Task StartLiveAsync()
    {
        if (IsLive) return;

        // 패킷 간 지연은 화면에 따로 칸을 두지 않는다 — 필요한 사람은 속성 트리의 GevSCPD 로 직접 정한다.
        // 다만 장치에 설정된 값을 읽어 그대로 넘겨야 한다. 0 을 넘기면 스트림을 여는 쪽이 남은 값을 지워 버린다.
        var ticks = await ReadPacketDelayAsync().ConfigureAwait(true);
        var opt = new GevStreamOpt { InterPacketDelay = ticks };
        _stream = await Device.OpenStreamAsync(opt).ConfigureAwait(true);
        await _stream.StartAsync().ConfigureAwait(true);
        StreamInfo = $"packet size {_stream.PacketSize} bytes, local port {_stream.LocalPort}"
                   + (ticks > 0 ? $", packet delay {ticks} ticks" : "");

        // 스트림 채널을 다 세운 뒤에 취득을 건다 — 순서가 바뀌면 첫 프레임이 갈 곳이 없다.
        await Device.SetTlParamsLockedAsync(true).ConfigureAwait(true);
        var map = await Device.GetNodeMapAsync().ConfigureAwait(true);
        await map.GetCommand("AcquisitionStart").ExecuteAsync().ConfigureAwait(true);

        _liveCts = new CancellationTokenSource();
        _liveTask = Task.Run(() => PumpAsync(_stream, _liveCts.Token));
        IsLive = true;
    }

    public async Task StopLiveAsync()
    {
        if (_liveCts is not null)
        {
            _liveCts.Cancel();
            if (_liveTask is not null)
            {
                try { await _liveTask.ConfigureAwait(true); } catch { /* 이미 보고된 오류 */ }
            }

            _liveCts.Dispose();
            _liveCts = null;
            _liveTask = null;
        }

        if (IsLive)
        {
            try
            {
                var map = await Device.GetNodeMapAsync().ConfigureAwait(true);
                await map.GetCommand("AcquisitionStop").ExecuteAsync().ConfigureAwait(true);
                await Device.SetTlParamsLockedAsync(false).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _report($"{Title}: could not stop acquisition cleanly — {ex.Message}", true);
            }
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(true);
            _stream = null;
        }

        IsLive = false;
        StreamInfo = "";
        FrameInfo = "";
    }

    public async ValueTask DisposeAsync()
    {
        await StopLiveAsync().ConfigureAwait(true);
        await Device.DisposeAsync().ConfigureAwait(true);
        _render.Dispose();
        Image = null;
    }

    /// <summary>장치에 설정돼 있는 패킷 간 지연을 읽는다. 읽지 못하면 0 — 그때는 지연 없이 간다.</summary>
    private async Task<int> ReadPacketDelayAsync()
    {
        try
        {
            var value = await Device.ReadRegAsync(Gvcp.GvbsAddr.StreamChannel(0, Gvcp.GvbsAddr.ScpdOffset)).ConfigureAwait(true);
            return value > int.MaxValue ? 0 : (int)value;
        }
        catch (GevException)
        {
            return 0;
        }
    }

    /// <summary>
    /// 수신 루프. 프레임 하나를 그린 즉시 반납한다 — 버퍼는 풀의 것이고, 오래 들고 있으면 수신이 굶는다.
    /// 그림은 매 장 갱신하고 글자만 솎는다. 함께 솎으면 버퍼가 짝수 번 바뀌었을 때 같은 참조가 다시 들어가 화면이 멈춘다.
    /// </summary>
    private async Task PumpAsync(GevStream stream, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var frames = 0;
        var lastReport = TimeSpan.Zero;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var frame = await stream.ReceiveAsync(ct).ConfigureAwait(false);
                frames++;

                var elapsed = clock.Elapsed;
                var showStats = elapsed - lastReport >= TimeSpan.FromMilliseconds(250);
                if (showStats) lastReport = elapsed;
                var fps = frames / elapsed.TotalSeconds;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Image = _render.Render(frame);
                    if (!showStats) return;

                    var format = Pfnc.PixelFormatInfo.ToPixelFormat(frame.PixelFormatCode);
                    FrameInfo = $"{frame.Width}x{frame.Height} · {format} · {fps:F1} fps"
                              + (frame.IsComplete ? "" : $" · incomplete, {frame.MissingPackets} packets missing");
                    if (_render.Unsupported is { } why) _report($"{Title}: {why}", true);
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료.
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _report($"{Title}: streaming stopped — {ex.Message}", true);
                IsLive = false;
            });
        }
    }
}
