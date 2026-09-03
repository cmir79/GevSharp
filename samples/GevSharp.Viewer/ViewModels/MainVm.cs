using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using GevSharp.Gvcp;

namespace GevSharp.Viewer.ViewModels;

/// <summary>
/// 검색 → 연결 → 라이브 → 속성 편집의 한 화면. 열어 둔 카메라는 여러 대일 수 있고, 영상은 균등한 격자로 나란히 보인다.
/// 속성 트리는 그중 고른 한 대의 것만 보여 준다 — 읽기 하나가 UDP 왕복이라 여러 대를 한꺼번에 읽을 이유가 없다.
/// </summary>
public sealed class MainVm : VmBase
{
    /// <summary>고른 무리를 다시 읽는 주기. 켜고 끄는 설정을 두지 않는다 — 화면의 값이 장치와 맞아야 하는 것은 당연한 동작이다.</summary>
    private const int PollIntervalMs = 100;

    /// <summary>장치를 다시 찾는 주기. 검색은 브로드캐스트 한 번이라 이 정도로는 망에 부담이 되지 않는다.</summary>
    private const int ScanIntervalMs = 1000;

    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _scan;
    private bool _isScanning;
    private bool _autoScan = true;
    private DeviceVm? _selectedDevice;
    private CamVm? _selectedCam;
    private bool _isBusy;
    private bool _isPolling;
    private string _status = "Press Discover to look for cameras.";
    private string? _error;
    private double _valueColumnWidth = 120;

    public MainVm()
    {
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _poll.Tick += (_, _) => _ = PollAsync();
        _poll.Start();

        _scan = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScanIntervalMs) };
        _scan.Tick += (_, _) => _ = ScanAsync();
        _scan.Start();
        _ = ScanAsync();

        Cams.CollectionChanged += (_, _) => Raise(nameof(TileColumns));
    }

    /// <summary>
    /// 영상 격자의 열 수. 균등 격자에 맡기면 항목 수의 제곱근으로 행·열을 함께 잡아 두 대에 2x2 를 만들고
    /// 절반이 빈칸이 된다. 열만 정해 주면 행은 필요한 만큼만 생긴다 — 두 대는 한 줄에 둘이다.
    /// </summary>
    public int TileColumns => Cams.Count <= 1 ? 1 : (int)Math.Ceiling(Math.Sqrt(Cams.Count));

    /// <summary>
    /// 장치를 주기적으로 다시 찾을지. 기본은 켬 — 케이블을 꽂으면 목록에 나타나는 것이 당연한 동작이다.
    /// 검색은 브로드캐스트라, 망을 조용히 두어야 하는 자리에서는 끌 수 있어야 한다.
    /// </summary>
    public bool AutoScan
    {
        get => _autoScan;
        set => Set(ref _autoScan, value);
    }

    public ObservableCollection<DeviceVm> Devices { get; } = new();

    /// <summary>열어 둔 카메라들. 영상은 이 순서대로 격자에 깔린다.</summary>
    public ObservableCollection<CamVm> Cams { get; } = new();

    public DeviceVm? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            Raise(nameof(CanConnect));
            // 목록 선택이 유일한 기준이다. 고른 장치가 열려 있으면 그 세션을, 아니면 아무것도 가리키지 않는다 —
            // 그래야 연결·해제·라이브가 전부 같은 대상을 보고, 목록에서 고른 것과 단추가 미는 것이 어긋나지 않는다.
            SelectedCam = value is null ? null : Cams.FirstOrDefault(c => c.Address == value.Address);
        }
    }

    /// <summary>속성 트리와 라이브 단추가 가리키는 카메라. 격자에서 영상을 누르면 바뀐다.</summary>
    public CamVm? SelectedCam
    {
        get => _selectedCam;
        set
        {
            var previous = _selectedCam;
            if (!Set(ref _selectedCam, value)) return;
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;
            Raise(nameof(HasSelectedCam));

            // 목록의 선택도 따라간다. 양쪽 설정자가 서로를 부르지만 값이 같아지는 순간 멈춘다.
            if (value is null) return;
            var row = Devices.FirstOrDefault(d => d.Address == value.Address);
            if (row is not null) SelectedDevice = row;
        }
    }

    public bool HasSelectedCam => _selectedCam is not null;

    /// <summary>같은 장치를 두 번 열지 않는다 — 이미 열려 있으면 연결 단추가 죽는다.</summary>
    public bool CanConnect => IsIdle && SelectedDevice is { IsOpen: false };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(IsIdle));
            Raise(nameof(CanConnect));
        }
    }

    public bool IsIdle => !_isBusy;

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

    /// <summary>값 편집 칸의 폭. 피처 이름도 값도 장치마다 길이가 달라 사람이 맞추는 편이 낫다.</summary>
    public double ValueColumnWidth
    {
        get => _valueColumnWidth;
        set => Set(ref _valueColumnWidth, value);
    }

    public Task DiscoverAsync() => RunAsync("Discovering", async () =>
    {
        var found = await GevDiscovery.DiscoverAsync().ConfigureAwait(true);
        Merge(found);
        Status = found.Count == 0
            ? "No camera answered. Check the cable, the subnet and the host firewall."
            : $"{found.Count} camera(s) found.";
    });

    /// <summary>
    /// 주기적으로 다시 찾는다. 목록을 비우지 않고 합치므로 고른 장치도 열어 둔 장치도 그대로 남는다 —
    /// 매번 비우면 초마다 선택이 튀어 쓸 수가 없다. 상태줄도 건드리지 않는다.
    /// </summary>
    private async Task ScanAsync()
    {
        if (!AutoScan || _isScanning || IsBusy) return;
        _isScanning = true;
        try
        {
            var found = await GevDiscovery.DiscoverAsync().ConfigureAwait(true);
            Merge(found);
        }
        catch
        {
            // 검색 실패는 다음 차례에 다시 시도하면 된다 — 화면을 어지럽히지 않는다.
        }
        finally
        {
            _isScanning = false;
        }
    }

    /// <summary>찾은 것을 목록에 합친다. 새로 보이면 넣고, 사라지면 뺀다 — 열어 둔 장치는 응답이 없어도 빼지 않는다.</summary>
    private void Merge(IReadOnlyList<GevDeviceInfo> found)
    {
        foreach (var info in found.OrderBy(d => d.Address.ToString()))
        {
            var address = info.Address.ToString();
            if (Devices.Any(d => d.Address == address)) continue;
            Devices.Add(new DeviceVm(info) { IsOpen = Cams.Any(c => c.Address == address) });
        }

        var seen = found.Select(f => f.Address.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in Devices.Where(d => !seen.Contains(d.Address) && !d.IsOpen).ToList())
        {
            if (ReferenceEquals(gone, SelectedDevice)) SelectedDevice = null;
            Devices.Remove(gone);
        }

        SelectedDevice ??= Devices.FirstOrDefault();
        Raise(nameof(CanConnect));
    }

    public Task ConnectAsync()
    {
        var target = SelectedDevice;
        if (target is null || target.IsOpen) return Task.CompletedTask;

        return RunAsync($"Opening {target.Address}", async () =>
        {
            var cam = await CamVm.OpenAsync(target.Info, Report).ConfigureAwait(true);
            Cams.Add(cam);
            SelectedCam = cam;
            target.IsOpen = true;
            Raise(nameof(CanConnect));
            Status = $"{cam.Title} open — {cam.NodeCount} nodes, heartbeat {cam.Device.HeartbeatPeriodMs} ms.";
        });
    }

    public Task CloseSelectedAsync()
    {
        var cam = SelectedCam;
        if (cam is null) return Task.CompletedTask;

        return RunAsync($"Closing {cam.Address}", async () =>
        {
            Cams.Remove(cam);
            foreach (var d in Devices.Where(d => d.Address == cam.Address)) d.IsOpen = false;
            // 목록의 선택은 그대로 두고 세션만 놓는다 — 방금 닫은 장치가 목록에서 계속 골라져 있어야
            // 바로 다시 열 수 있고, 연결 단추도 그 장치를 가리킨 채로 살아난다.
            SelectedCam = null;
            Raise(nameof(CanConnect));
            await cam.DisposeAsync().ConfigureAwait(true);
            Status = "Closed.";
        });
    }

    public Task StartLiveAsync()
    {
        var cam = SelectedCam;
        return cam is null ? Task.CompletedTask : RunAsync($"Starting {cam.Address}", async () =>
        {
            await cam.StartLiveAsync().ConfigureAwait(true);
            Status = "Live.";
        });
    }

    public Task StopLiveAsync()
    {
        var cam = SelectedCam;
        return cam is null ? Task.CompletedTask : RunAsync($"Stopping {cam.Address}", async () =>
        {
            await cam.StopLiveAsync().ConfigureAwait(true);
            Status = "Stopped.";
        });
    }

    public Task SelectNodeAsync(NodeVm node) => SelectedCam?.SelectNodeAsync(node) ?? Task.CompletedTask;

    /// <summary>지금 화면에 걸린 그림을 파일로 쓴다. 실패해도 창을 내리지 않고 이유만 남긴다.</summary>
    public void SaveFrame(CamVm cam, string path)
    {
        try
        {
            cam.SaveCurrent(path);
            Error = null;
            Status = $"Saved {path}.";
        }
        catch (Exception ex)
        {
            Error = $"could not save the frame: {ex.Message}";
        }
    }

    /// <summary>창이 닫힐 때 부르는 정리. 여기서 놓지 않으면 카메라가 하트비트 시간만큼 제어권을 물고 있다.</summary>
    public async Task ShutdownAsync()
    {
        _poll.Stop();
        _scan.Stop();
        foreach (var cam in Cams.ToList())
        {
            try { await cam.DisposeAsync().ConfigureAwait(true); }
            catch { /* 종료 경로에서는 더 할 수 있는 것이 없다 */ }
        }

        Cams.Clear();
    }

    /// <summary>노드가 쓰기에 성공하거나 실패한 것을 아래 상태줄로 올린다 — 트리 안 작은 글씨는 눈에 안 띈다.</summary>
    private void Report(string text, bool isError)
    {
        if (isError) { Error = text; }
        else { Error = null; Status = text; }
    }

    /// <summary>화면에 보이는 것만 다시 읽는다. 앞선 갱신이 아직 돌고 있으면 이번 차례는 건너뛴다.</summary>
    private async Task PollAsync()
    {
        if (_isPolling || IsBusy || SelectedCam is null) return;

        _isPolling = true;
        try { await SelectedCam.PollAsync().ConfigureAwait(true); }
        catch { /* 읽기 실패는 노드 옆에 이미 적힌다 */ }
        finally { _isPolling = false; }
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
