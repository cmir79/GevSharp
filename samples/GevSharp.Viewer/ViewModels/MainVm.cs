using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GevSharp.Gvcp;
using GevSharp.Gvsp;
using GevSharp.Viewer.Imaging;

namespace GevSharp.Viewer.ViewModels;

/// <summary>
/// 검색 → 연결 → 라이브 → 속성 편집의 한 화면. 어느 단계에서도 벤더를 구분하지 않는다 —
/// 목록은 검색 응답이, 속성 트리는 카메라가 준 XML 이 그대로 만든다.
/// </summary>
public sealed class MainVm : VmBase
{
    private readonly FrameRender _render = new();
    private GevDevice? _device;
    private GevStream? _stream;
    private CancellationTokenSource? _liveCts;
    private Task? _liveTask;

    private DeviceVm? _selectedDevice;
    private bool _isBusy;
    private bool _isConnected;
    private bool _isLive;
    private string _status = "Press Discover to look for cameras.";
    private string? _error;
    private Bitmap? _image;
    private string _frameInfo = "";
    private string _streamInfo = "";
    private int _packetDelayUs;
    private string? _selectedTitle;
    private string? _selectedHint;
    private NodeVm? _selectedNode;
    private bool _autoRefresh = true;
    private int _refreshIntervalMs = 100;
    private double _valueColumnWidth = 120;
    private bool _isPolling;
    private readonly DispatcherTimer _poll;

    public MainVm()
    {
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_refreshIntervalMs) };
        _poll.Tick += (_, _) => _ = PollAsync();
        _poll.Start();
    }

    public ObservableCollection<DeviceVm> Devices { get; } = new();
    public ObservableCollection<NodeVm> Nodes { get; } = new();

    public DeviceVm? SelectedDevice
    {
        get => _selectedDevice;
        set => Set(ref _selectedDevice, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isBusy;

    public bool IsConnected
    {
        get => _isConnected;
        private set => Set(ref _isConnected, value);
    }

    public bool IsLive
    {
        get => _isLive;
        private set { if (Set(ref _isLive, value)) Raise(nameof(IsNotLive)); }
    }

    public bool IsNotLive => !_isLive;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>마지막으로 실패한 동작의 이유. 성공하면 지운다 — 옛 오류를 화면에 남겨 두지 않는다.</summary>
    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

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

    /// <summary>
    /// 트리에서 고른 노드의 설명. 트리 안에 띄우면 그만큼 칸이 벌어져 값 편집이 좁아지므로 영상 아래에 적는다.
    /// </summary>
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
    /// 고른 노드를 주기적으로 다시 읽을지. 장치가 스스로 바꾸는 값과, 옆 노드를 써서 풀린 잠금을 따라가려면 필요하다.
    /// </summary>
    public bool AutoRefresh
    {
        get => _autoRefresh;
        set => Set(ref _autoRefresh, value);
    }

    public int RefreshIntervalMs
    {
        get => _refreshIntervalMs;
        set
        {
            if (!Set(ref _refreshIntervalMs, value) || value <= 0) return;
            _poll.Interval = TimeSpan.FromMilliseconds(value);
        }
    }

    /// <summary>값 편집 칸의 폭. 피처 이름도 값도 장치마다 길이가 달라 사람이 맞추는 편이 낫다.</summary>
    public double ValueColumnWidth
    {
        get => _valueColumnWidth;
        set => Set(ref _valueColumnWidth, value);
    }

    /// <summary>노드가 쓰기에 성공하거나 실패한 것을 아래 상태줄로 올린다 — 트리 안 작은 글씨는 눈에 안 띈다.</summary>
    private void ReportFromNode(string text, bool isError)
    {
        if (isError) { Error = text; }
        else { Error = null; Status = text; }
    }

    /// <summary>트리 선택이 바뀌면 그 노드를 읽고 설명을 아래에 건다.</summary>
    public Task SelectNodeAsync(NodeVm node)
    {
        _selectedNode = node;
        SelectedTitle = node.Unit is { Length: > 0 } unit ? $"{node.Label}  ({node.Name}, {unit})" : $"{node.Label}  ({node.Name})";
        SelectedHint = node.Hint;
        return LoadAndDescribeAsync(node);
    }

    private async Task LoadAndDescribeAsync(NodeVm node)
    {
        await node.LoadAsync().ConfigureAwait(true);
        if (!ReferenceEquals(node, _selectedNode) || node.IsCategory) return;
        SelectedTitle += $"   —   {node.Access}";
    }

    /// <summary>
    /// 화면에 보이는 것만 다시 읽는다. 읽기마다 왕복이라 트리 전체를 훑지 않고, 앞선 갱신이 아직 돌고 있으면 이번 차례는 건너뛴다.
    /// 고른 것이 노드 하나면 같은 칸의 형제까지 본다 — 잠금은 대개 옆 노드가 푼다.
    /// </summary>
    private async Task PollAsync()
    {
        if (!AutoRefresh || _isPolling || IsBusy || !IsConnected) return;
        var target = _selectedNode?.IsCategory == true ? _selectedNode : _selectedNode?.Parent ?? _selectedNode;
        if (target is null) return;

        _isPolling = true;
        try { await target.PollAsync().ConfigureAwait(true); }
        catch { /* 읽기 실패는 노드 옆에 이미 적힌다 */ }
        finally { _isPolling = false; }
    }

    /// <summary>
    /// 패킷 간 지연(마이크로초). 한 포트에 카메라를 여럿 물리면 프레임레이트를 낮춰도 버스트가 겹쳐 유실되므로
    /// 이 값으로 버스트를 펴 준다. 한 대만 쓰면 0 으로 둔다.
    /// <para>
    /// 장치는 이 값을 자기 타임스탬프 틱으로 받는다. 틱 주파수는 장치마다 달라(실측 125 MHz 와 66.67 MHz)
    /// 같은 지연이 다른 숫자가 되므로, 사람에게는 시간으로 받고 환산은 여기서 한다.
    /// </para>
    /// </summary>
    public int PacketDelayUs
    {
        get => _packetDelayUs;
        set => Set(ref _packetDelayUs, value);
    }

    /// <summary>마이크로초를 이 장치의 틱으로 환산한다. 틱 주파수를 모르면 적용하지 않는다 — 0 은 "지연 없음" 이라 조용한 미적용이 된다.</summary>
    private int PacketDelayTicks(GevDevice device)
    {
        if (PacketDelayUs <= 0) return 0;
        if (device.TimestampTickFrequency == 0)
        {
            Error = "the device does not report a timestamp tick frequency; the inter-packet delay was not applied";
            return 0;
        }

        var ticks = (double)PacketDelayUs * device.TimestampTickFrequency / 1_000_000d;
        return ticks >= int.MaxValue ? int.MaxValue : (int)Math.Round(ticks);
    }

    public Task DiscoverAsync() => RunAsync("Discovering", async () =>
    {
        var found = await GevDiscovery.DiscoverAsync().ConfigureAwait(true);
        Devices.Clear();
        foreach (var info in found.OrderBy(d => d.Address.ToString())) Devices.Add(new DeviceVm(info));
        SelectedDevice ??= Devices.FirstOrDefault();
        Status = found.Count == 0
            ? "No camera answered. Check the cable, the subnet and the host firewall."
            : $"{found.Count} camera(s) found.";
    });

    public Task ConnectAsync()
    {
        var target = SelectedDevice;
        if (target is null) return Task.CompletedTask;

        return RunAsync($"Opening {target.Address}", async () =>
        {
            await CloseAsync().ConfigureAwait(true);
            _device = await GevDevice.OpenAsync(target.Info).ConfigureAwait(true);
            var map = await _device.GetNodeMapAsync().ConfigureAwait(true);

            Nodes.Clear();
            // 루트는 그리지 않는다 — 이름이 장치마다 제각각이고 한 겹 더 펼치게 만들 뿐이다.
            foreach (var feature in map.Root.Features) Nodes.Add(new NodeVm(feature, null, ReportFromNode));
            IsConnected = true;
            Status = $"{target.Title} open — {map.Nodes.Count} nodes, heartbeat {_device.HeartbeatPeriodMs} ms.";
        });
    }

    public Task DisconnectAsync() => RunAsync("Closing", async () =>
    {
        await CloseAsync().ConfigureAwait(true);
        Status = "Closed.";
    });

    public Task StartLiveAsync()
    {
        if (_device is null || IsLive) return Task.CompletedTask;

        return RunAsync("Starting the stream", async () =>
        {
            var ticks = PacketDelayTicks(_device);
            var opt = new GevStreamOpt { InterPacketDelay = ticks };
            _stream = await _device.OpenStreamAsync(opt).ConfigureAwait(true);
            await _stream.StartAsync().ConfigureAwait(true);
            StreamInfo = $"packet size {_stream.PacketSize} bytes, local port {_stream.LocalPort}"
                       + (ticks > 0 ? $", packet delay {PacketDelayUs} us ({ticks} ticks)" : "");

            // 스트림 채널을 다 세운 뒤에 취득을 건다 — 순서가 바뀌면 첫 프레임이 갈 곳이 없다.
            await _device.SetTlParamsLockedAsync(true).ConfigureAwait(true);
            var map = await _device.GetNodeMapAsync().ConfigureAwait(true);
            await map.GetCommand("AcquisitionStart").ExecuteAsync().ConfigureAwait(true);

            _liveCts = new CancellationTokenSource();
            _liveTask = Task.Run(() => PumpAsync(_stream, _liveCts.Token));
            IsLive = true;
            Status = "Live.";
        });
    }

    public Task StopLiveAsync()
    {
        if (!IsLive) return Task.CompletedTask;
        return RunAsync("Stopping the stream", async () =>
        {
            await StopLiveCoreAsync().ConfigureAwait(true);
            Status = "Stopped.";
        });
    }

    /// <summary>창이 닫힐 때 부르는 정리. 여기서 놓지 않으면 카메라가 하트비트 시간만큼 제어권을 물고 있다.</summary>
    public async Task ShutdownAsync()
    {
        try { await CloseAsync().ConfigureAwait(false); }
        catch { /* 종료 경로에서는 더 할 수 있는 것이 없다 */ }
    }

    /// <summary>
    /// 수신 루프. 프레임 하나를 그린 즉시 반납한다 — 버퍼는 풀의 것이고, 오래 들고 있으면 수신이 굶는다.
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

                // 그림은 매 장 갱신하고 글자만 솎는다. 예전에는 글자와 함께 솎았는데, 그 사이에 버퍼가
                // 짝수 번 바뀌면 같은 참조가 다시 들어가 바인딩이 갱신을 알아채지 못했다.
                var elapsed = clock.Elapsed;
                var showStats = elapsed - lastReport >= TimeSpan.FromMilliseconds(250);
                if (showStats) lastReport = elapsed;
                var fps = frames / elapsed.TotalSeconds;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Image = _render.Render(frame);
                    if (!showStats) return;

                    var format = Pfnc.PixelFormatInfo.ToPixelFormat(frame.PixelFormatCode);
                    FrameInfo = $"frame {frame.FrameId} · {frame.Width}x{frame.Height} · {format} · {fps:F1} fps"
                              + (frame.IsComplete ? "" : $" · incomplete, {frame.MissingPackets} packets missing");
                    Error = _render.Unsupported;
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
                Error = ex.Message;
                Status = "Streaming stopped after an error.";
                IsLive = false;
            });
        }
    }

    private async Task StopLiveCoreAsync()
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

        if (_device is not null)
        {
            try
            {
                var map = await _device.GetNodeMapAsync().ConfigureAwait(true);
                await map.GetCommand("AcquisitionStop").ExecuteAsync().ConfigureAwait(true);
                await _device.SetTlParamsLockedAsync(false).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Error = $"could not stop acquisition cleanly: {ex.Message}";
            }
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(true);
            _stream = null;
        }

        IsLive = false;
        StreamInfo = "";
    }

    private async Task CloseAsync()
    {
        await StopLiveCoreAsync().ConfigureAwait(true);
        if (_device is not null)
        {
            await _device.DisposeAsync().ConfigureAwait(true);
            _device = null;
        }

        Nodes.Clear();
        Image = null;
        FrameInfo = "";
        SelectedTitle = null;
        SelectedHint = null;
        _selectedNode = null;
        IsConnected = false;
    }

    /// <summary>한 동작을 감싼다 — 진행 중 표시, 실패 이유 보존, 그리고 어떤 실패도 창을 내리지 않게.</summary>
    private async Task RunAsync(string what, Func<Task> body)
    {
        if (IsBusy) return;
        IsBusy = true;
        Error = null;
        Status = what + "...";
        try
        {
            await body().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Status = what + " failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
