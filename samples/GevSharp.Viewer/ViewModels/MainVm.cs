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
    private int _packetDelay;

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
    /// 패킷 간 지연(장치 틱). 한 포트에 카메라를 여럿 물리면 프레임레이트를 낮춰도 버스트가 겹쳐 유실되므로
    /// 이 값으로 버스트를 펴 준다. 한 대만 쓰면 0 으로 둔다.
    /// </summary>
    public int PacketDelay
    {
        get => _packetDelay;
        set => Set(ref _packetDelay, value);
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
            Nodes.Add(new NodeVm(map.Root) { IsExpanded = true });
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
            var opt = new GevStreamOpt { InterPacketDelay = PacketDelay };
            _stream = await _device.OpenStreamAsync(opt).ConfigureAwait(true);
            await _stream.StartAsync().ConfigureAwait(true);
            StreamInfo = $"packet size {_stream.PacketSize} bytes, local port {_stream.LocalPort}"
                       + (PacketDelay > 0 ? $", packet delay {PacketDelay} ticks" : "");

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

                var bitmap = await Dispatcher.UIThread.InvokeAsync(() => _render.Render(frame));

                var elapsed = clock.Elapsed;
                if (elapsed - lastReport < TimeSpan.FromMilliseconds(250)) continue;

                var fps = frames / elapsed.TotalSeconds;
                var format = Pfnc.PixelFormatInfo.ToPixelFormat(frame.PixelFormatCode);
                var info = $"frame {frame.FrameId} · {frame.Width}x{frame.Height} · {format} · {fps:F1} fps"
                         + (frame.IsComplete ? "" : $" · incomplete, {frame.MissingPackets} packets missing");
                var unsupported = _render.Unsupported;
                lastReport = elapsed;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Image = bitmap;
                    FrameInfo = info;
                    Error = unsupported;
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
