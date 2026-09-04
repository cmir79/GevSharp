using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using System.Runtime.CompilerServices;
using GevSharp.Gvcp;

namespace GevSharp.IpConfig.ViewModels;

public abstract class VmBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>검색으로 찾은 장치 한 대. 답한 내용만 담는다.</summary>
public sealed class DeviceRowVm
{
    public DeviceRowVm(GevDeviceInfo info)
    {
        Info = info;
    }

    public GevDeviceInfo Info { get; }

    public string Title => string.IsNullOrWhiteSpace(Info.UserDefinedName)
        ? $"{Info.Manufacturer} {Info.Model}"
        : $"{Info.UserDefinedName} ({Info.Model})";

    public string Detail => $"{Info.Address}  ·  {Format(Info.Mac)}  ·  SN {Info.SerialNumber}";

    /// <summary>
    /// 이 장치를 들은 인터페이스와 서브넷이 어긋나는 상태. 제어는 오가는데 스트림만 오지 않는 전형적인 원인이라
    /// 목록에서 먼저 눈에 띄어야 한다.
    /// </summary>
    public bool IsStranded => !Info.IsReachableDirectly;

    public string? Warning => IsStranded
        ? $"outside {Info.InterfaceAddress}'s subnet — control may work while streaming never arrives"
        : null;

    public static string Format(PhysicalAddress mac)
        => string.Join(":", mac.GetAddressBytes().Select(b => b.ToString("X2")));
}

/// <summary>
/// 호스트 어댑터 하나. 주소가 없거나 내려가 있어도 담는다 — 카메라가 안 보이는 이유가 대개 그쪽이라,
/// 목록에 없으면 무엇을 확인해야 할지조차 알 수 없다.
/// </summary>
public sealed record HostNic(string Interface, string Hardware, string Id, IPAddress? Address, IPAddress? Mask, bool IsUp)
{
    /// <summary>
    /// 네트워크 연결 창에 그 이름으로 있는 표시 이름. 어댑터 API 가 주는 이름과 다를 수 있다 —
    /// TeamViewer VPN 은 어댑터 이름이 "로컬 영역 연결" 인데 연결 창에는 "TeamViewer VPN" 으로 있다.
    /// 사람이 보고 고르는 것은 이쪽이므로 이것을 쓴다. 못 알아내면 어댑터 이름으로 물러선다.
    /// </summary>
    public string Display { get; init; } = "";

    public string Shown => string.IsNullOrEmpty(Display) ? Interface : Display;

    public bool HasAddress => Address is not null;

    /// <summary>주소가 없는 어댑터에는 카메라가 붙을 수 없다 — 왜 그런지 한 줄로 적는다.</summary>
    public string State => !IsUp
        ? "link down"
        : Address is null
            ? "up, but no IPv4 address"
            : $"{Address} / {Mask}";

    public override string ToString() => $"{Shown}  ·  {Hardware}  ·  {State}";
}

/// <summary>
/// 한 호스트 어댑터와 그 어댑터로 들어온 카메라들. 목록을 어댑터로 묶어야 어느 랜카드가 몇 대를 물고 있는지
/// 한눈에 보인다 — 한 어댑터에 여러 대가 걸리면 대역을 나눠 쓰는 것이라 패킷 간 지연이 필요해진다.
/// </summary>
public sealed class NicGroupVm
{
    public NicGroupVm(HostNic? nic, IPAddress? heardOn, IReadOnlyList<DeviceRowVm> devices)
    {
        Nic = nic;
        Devices = devices;
        // 목록을 다시 그리면 이 객체가 통째로 바뀌므로, 고른 것을 되찾을 열쇠가 따로 있어야 한다.
        // 어댑터는 이름이 바뀌어도 GUID 는 그대로다 — 이름으로 잡으면 이름을 바꾸는 순간 놓친다.
        Key = nic?.Id ?? $"@{heardOn}";
        // 제목은 사용자가 붙일 수 있는 이름이다 — 윈도우 네트워크 연결 창에서 바꾸는 그 이름으로, 설비에서
        // 부르는 대로("카메라1" 같이) 고쳐 두면 그것이 그대로 보인다. 기본값이면 "이더넷" 처럼 밋밋하므로
        // 어느 장치인지는 바로 아래에 함께 적는다. 찾는 것은 어느 이름도 아니고 인터페이스 GUID 다.
        var shown = nic?.Shown ?? "unknown adapter";
        var count = devices.Count switch
        {
            0 => "no camera",
            1 => "1 camera",
            _ => $"{devices.Count} cameras",
        };

        // 대수는 이름 옆 괄호에 — 한 줄로 붙어 있어야 어느 포트에 몇 대인지 훑어보기 좋다.
        Name = $"{shown} ({count})";
        // 장치 이름과 링크 상태는 줄을 나눈다. 한 줄에 몰면 좁은 폭에서 상태가 먼저 잘린다.
        Header = nic?.Hardware ?? "";
        State = nic is null
            ? $"heard on {heardOn}, which is not one of this host's addresses"
            : nic.State;
        Count = count;
    }

    public HostNic? Nic { get; }

    /// <summary>목록을 새로 만들어도 같은 어댑터를 가리키는 값.</summary>
    public string Key { get; }

    public string Kind => Nic is null ? "" : $"connection name: {Nic.Interface}";
    public string Name { get; }
    public string Header { get; }
    public string State { get; }
    public string Count { get; }
    public IReadOnlyList<DeviceRowVm> Devices { get; }

    /// <summary>한 어댑터에 여러 대가 걸린 상태. 대역과 버스트를 나눠 쓰므로 미리 알려 준다.</summary>
    public string? Note => Devices.Count > 1
        ? "sharing one port — cap the frame rates and set an inter-packet delay, or they will collide"
        : null;
}

/// <summary>
/// 카메라의 주소를 잡아 주는 도구. 스트리밍을 하지 않으므로 장치를 열 필요가 없는 일(ForceIP)과
/// 열어야만 되는 일(영구 주소 쓰기)을 나눠 다룬다.
/// </summary>
public sealed class MainVm : VmBase
{
    private DeviceRowVm? _selected;
    private NicGroupVm? _selectedGroup;
    private bool _isBusy;
    private string _status = "Press Scan to look for cameras. Cameras on the wrong subnet answer too.";
    private string? _error;
    private string _ip = "";
    private string _mask = "";
    private string _gateway = "";
    private string _userName = "";
    private string? _firewall;
    private string _adapterName = "";
    private string? _stored;
    private bool _isDhcpSelected;
    private readonly Dictionary<string, string> _connectionNames = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _scan;
    private bool _autoScan = true;
    private bool _isScanning;

    /// <summary>
    /// 주소 설정을 쓸 때 쓰는 접근 방식. 제어 접근으로는 같은 값을 써도 반영하지 않는 카메라가 있다 —
    /// 벤더 도구를 잡아 견줘 보니 쓰는 값은 같고 이것만 달랐다.
    /// </summary>
    private static GevDeviceOpt Exclusive => new() { AccessMode = GevAccessMode.Exclusive };

    public ObservableCollection<DeviceRowVm> Devices { get; } = new();
    public ObservableCollection<HostNic> Nics { get; } = new();

    /// <summary>어댑터별로 묶은 장치 목록. 화면의 왼쪽이 이것을 그린다.</summary>
    public ObservableCollection<NicGroupVm> Groups { get; } = new();

    public DeviceRowVm? Selected
    {
        get => _selected;
        set => Select(value, loadFields: true);
    }

    /// <summary>
    /// 고른 카메라를 바꾼다. <paramref name="loadFields"/> 가 거짓이면 화면에 적힌 것을 그대로 둔다 —
    /// 검색이 목록을 새로 만들면서 같은 카메라를 다시 고르게 될 때, 사람이 방금 고른 주소 방식과 타이핑한
    /// 주소를 장치 값으로 되돌리지 않기 위한 것이다. 되돌리면 적용을 누르기도 전에 고른 것이 사라진다.
    /// </summary>
    private void Select(DeviceRowVm? value, bool loadFields)
    {
        {
            if (!Set(ref _selected, value, nameof(Selected))) return;
            Raise(nameof(HasSelection));
            Raise(nameof(HasAnySelection));
            Raise(nameof(SelectedSummary));
            Raise(nameof(SupportsDhcp));
            Raise(nameof(SupportsPersistent));
            Raise(nameof(MatchingNic));
            Raise(nameof(CanSuggest));
            if (value is null) { Firewall = null; return; }
            _selectedGroup = null;
            Raise(nameof(SelectedGroup));
            Raise(nameof(HasGroupSelection));
            Raise(nameof(HasAnySelection));
            if (loadFields) LoadFields(value);
            // 방화벽은 어댑터 쪽 설정이라 어댑터를 골랐을 때만 읽는다 — 카메라마다 다시 물을 이유가 없다.
            Firewall = null;

            // 저장 주소는 사람이 카메라를 고를 때만 읽는다. 검색이 목록을 새로 만들며 같은 카메라를 다시
            // 고르는 자리에서까지 열면, 적용 중에 그 검색이 돌 때 우리 세션 둘이 같은 카메라를 동시에 열게 된다 —
            // 뒤에 닫히는 쪽이 CCP 를 0 으로 쓰면서 앞 세션의 제어권을 걷어 가고, 적용은 아무것도 못 쓴 채 끝난다.
            if (loadFields) _ = RefreshStoredAsync(value);
        }
    }

    /// <summary>
    /// 고른 카메라를 들은 호스트 어댑터. 고르는 것이 아니라 <b>따라오는 것</b>이다 — 포트에 직결한 카메라는
    /// 어느 어댑터로 들어오는지가 이미 정해져 있어, 사람이 고를 여지가 없다.
    /// </summary>
    public HostNic? MatchingNic => _selected is null
        ? null
        : Nics.FirstOrDefault(n => n.Address is not null && n.Address.Equals(_selected.Info.InterfaceAddress));

    /// <summary>
    /// 어댑터를 골랐을 때. 카메라 선택과 배타다 — 오른쪽에는 지금 보고 있는 것 하나만 나와야 헷갈리지 않는다.
    /// </summary>
    public NicGroupVm? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!Set(ref _selectedGroup, value)) return;
            Raise(nameof(HasGroupSelection));
            Raise(nameof(HasAnySelection));
            if (value is null) return;
            _selected = null;
            Raise(nameof(Selected));
            Raise(nameof(HasSelection));
            Raise(nameof(HasAnySelection));
            AdapterName = value.Nic?.Shown ?? "";
            _ = RefreshFirewallAsync(value.Nic);
        }
    }

    public bool HasGroupSelection => _selectedGroup is not null;

    /// <summary>어느 쪽이든 하나는 골랐는지 — 아무것도 안 골랐을 때만 안내를 띄운다.</summary>
    public bool HasAnySelection => _selected is not null || _selectedGroup is not null;

    public bool HasSelection => _selected is not null;

    public bool CanSuggest => MatchingNic is not null;

    /// <summary>고른 장치가 지금 무엇으로 설정돼 있는지. 무엇을 바꾸는지 알고 눌러야 한다.</summary>
    public string? SelectedSummary
    {
        get
        {
            if (_selected is not { } d) return null;
            var current = Describe(d.Info.CurrentIpCfg);
            var supported = Describe(d.Info.SupportedIpCfg);
            return $"MAC {DeviceRowVm.Format(d.Info.Mac)}   ·   heard on {d.Info.InterfaceAddress}\n"
                 + $"now: {d.Info.Address} / {d.Info.Subnet}   gateway {d.Info.Gateway}\n"
                 + $"addressing in use: {current}   ·   supported: {supported}";
        }
    }

    public string Ip
    {
        get => _ip;
        set => Set(ref _ip, value);
    }

    public string Mask
    {
        get => _mask;
        set => Set(ref _mask, value);
    }

    public string Gateway
    {
        get => _gateway;
        set => Set(ref _gateway, value);
    }

    /// <summary>
    /// 장치에 붙이는 사용자 이름. 부트스트랩의 16바이트 자리에 그대로 쓴다 — 벤더마다 다른 노드 이름에
    /// 기대지 않으려는 것이다. 여러 대를 같은 모델로 깔면 시리얼만으로는 어느 자리인지 알 수 없으므로,
    /// 설비에서 부르는 이름을 여기에 적어 둔다.
    /// </summary>
    public string UserName
    {
        get => _userName;
        set => Set(ref _userName, value);
    }

    public Task ApplyUserNameAsync()
    {
        if (Selected is not { } target) return Task.CompletedTask;

        var text = UserName.Trim();
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        if (bytes.Length > GvbsAddr.UserDefinedNameLen)
        {
            Error = $"the name must fit in {GvbsAddr.UserDefinedNameLen} ASCII characters";
            return Task.CompletedTask;
        }

        return RunAsync($"Naming {target.Info.Address}", async () =>
        {
            // 자리를 통째로 채운다 — 짧게 쓰면 앞선 이름의 꼬리가 남는다.
            var buffer = new byte[GvbsAddr.UserDefinedNameLen];
            bytes.CopyTo(buffer, 0);

            await using var device = await GevDevice.OpenAsync(target.Info).ConfigureAwait(true);
            await device.WriteMemAsync(GvbsAddr.UserDefinedName, buffer).ConfigureAwait(true);
            Status = text.Length == 0 ? "Name cleared." : $"Named \"{text}\".";
            await ScanCoreAsync().ConfigureAwait(true);
        });
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) Raise(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isBusy;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public MainVm()
    {
        LoadNics();
        _ = LoadConnectionNamesAsync();
        _scan = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _scan.Tick += (_, _) => _ = AutoScanAsync();
        _scan.Start();
        _ = AutoScanAsync();
    }

    /// <summary>
    /// 계속 다시 찾을지. 기본은 켬 — 주소를 바꾸면 장치가 새 주소로 다시 나타나는 것을 눈으로 봐야 한다.
    /// </summary>
    public bool AutoScan
    {
        get => _autoScan;
        set => Set(ref _autoScan, value);
    }

    /// <summary>이 도구가 무엇을 열어 줄 수 있는지는 운영체제마다 다르다. 없는 곳에서는 단추를 감춘다.</summary>
    public bool CanOpenSystemSettings => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>고른 장치가 DHCP 를 지원하는지. 지원하지 않는 장치에 그 단추를 살려 둘 이유가 없다.</summary>
    public bool SupportsDhcp => (_selected?.Info.SupportedIpCfg & GvbsAddr.IpCfgDhcp) != 0;

    public bool SupportsPersistent => (_selected?.Info.SupportedIpCfg & GvbsAddr.IpCfgPersistent) != 0;

    private async Task AutoScanAsync()
    {
        if (!AutoScan || _isScanning || IsBusy) return;
        _isScanning = true;
        try { await ScanCoreAsync().ConfigureAwait(true); }
        catch { /* 다음 차례에 다시 시도하면 된다 */ }
        finally { _isScanning = false; }
    }

    /// <summary>
    /// 주소를 어떻게 얻을지 바꾼다. 주소값이 아니라 방식을 바꾸는 것이라, DHCP 로 돌리면 저장해 둔 주소는 남아 있어도 쓰이지 않는다.
    /// 링크로컬 비트는 건드리지 않는다 — 다른 방식이 모두 실패했을 때의 마지막 수단이라 꺼 두면 장치를 잃을 수 있다.
    /// </summary>
    /// <summary>
    /// 화면에서 고른 주소 방식. 장치에 바로 쓰이지 않는다 — 고르고 나서 적용을 눌러야 장치가 바뀐다.
    /// 고른 방식이 DHCP 면 주소 칸은 잠긴다. 그 칸들이 정하는 것은 고정 주소이고, DHCP 를 쓸 때는
    /// 주소를 서버가 정하므로 사람이 적을 자리가 없다.
    /// </summary>
    public bool IsDhcpSelected
    {
        get => _isDhcpSelected;
        set
        {
            if (!Set(ref _isDhcpSelected, value)) return;
            Raise(nameof(IsStaticSelected));
            if (value)
            {
                // DHCP 를 고르면 주소 칸을 비운다. 서버가 정할 값이라 남겨 두면 무엇을 쓰는지 착각하게 된다.
                Ip = "";
                Mask = "";
                Gateway = "";
                return;
            }

            // 고정으로 돌아와도 칸은 채우지 않는다. 어떤 주소를 쓸지는 사람이 정할 일이고, 비어 있으면
            // 어댑터에 맞춰 빈 주소를 제안하는 단추로 한 번에 채운다. 여기서 장치 값을 되읽으면 방금 고른
            // 모드까지 되돌려 놓아 고정이 곧바로 DHCP 로 튕겨 돌아갔다.
        }
    }

    public bool IsStaticSelected
    {
        get => !_isDhcpSelected;
        set => IsDhcpSelected = !value;
    }

    /// <summary>
    /// 화면에 적힌 대로 카메라를 맞춘다. 단추를 하나로 둔 이유는, 쓰는 사람이 정하고 싶은 것은
    /// "이 카메라를 이렇게 쓰겠다" 하나이지 그것을 이루는 절차가 아니기 때문이다.
    /// <para>
    /// 고정 주소면 지금 바로 그 주소로 바꾸고(브로드캐스트라 못 여는 장치에도 닿는다) 같은 주소를 저장한 뒤
    /// 고정 방식으로 표시해, 지금도 다음 기동에도 같은 자리에 있게 한다. DHCP 면 방식만 바꾸고 저장된
    /// 주소를 지운다 — 남겨 두면 그 주소로 올라오는 카메라가 있어 DHCP 로 바꿨다는 말이 거짓이 된다.
    /// </para>
    /// </summary>
    public Task ApplyAsync()
    {
        // 되돌아가는 자리마다 이유를 남긴다. 단추가 조용히 아무 일도 하지 않으면 왜 그런지 알 방법이 없고,
        // 실제로 그 상태를 한참 들여다본 적이 있다.
        if (Selected is not { } target)
        {
            Error = "no camera is selected — pick one on the left";
            return Task.CompletedTask;
        }

        if (IsBusy)
        {
            Error = "another operation is still running";
            return Task.CompletedTask;
        }

        if (IsDhcpSelected) return SetAddressingAsync(dhcp: true);
        if (!TryReadFields(out var ip, out var mask, out var gateway)) return Task.CompletedTask;

        return RunAsync($"Applying {ip}", async () =>
        {
            // 강제 주소는 열 수 없는 장치를 데려올 때만 쓴다. 열 수 있으면 쓸 이유가 없다 —
            // 레지스터에 적고 다시 시작시키면 그 주소로 올라오고, 임시 상태를 거치지 않아 지금 보이는 것이
            // 다음 기동에 보일 것과 같아진다.
            Status = $"Applying {ip} / {mask} to {DeviceRowVm.Format(target.Info.Mac)}...";
            GevLog.Info("IpConfig", $"apply fixed {ip}/{mask} gw {gateway} to {DeviceRowVm.Format(target.Info.Mac)} at {target.Info.Address}, stranded={target.IsStranded}");
            var info = target.Info;
            if (target.IsStranded)
            {
                // 여기서만 훑는다. 레지스터를 쓰려면 장치를 열어야 하고, 열려면 강제 주소로 옮겨 간 뒤의
                // 주소를 알아야 한다 — 확인하려는 검색이 아니라 다음 단계에 필요한 검색이다.
                await GevDiscovery.ForceIpAsync(info.Mac, ip, mask, gateway).ConfigureAwait(true);
                GevDeviceInfo? moved = null;
                for (var i = 0; i < 6; i++)
                {
                    await ScanCoreAsync().ConfigureAwait(true);
                    moved = Devices.FirstOrDefault(d => d.Info.Mac.Equals(info.Mac))?.Info;
                    if (moved is not null && moved.IsReachableDirectly) break;
                    await Task.Delay(300).ConfigureAwait(true);
                }

                if (moved is null)
                {
                    Status = $"{ip} was sent to bring the camera back in range; it has not answered yet.";
                    return;
                }

                info = moved;
            }

            if ((info.SupportedIpCfg & GvbsAddr.IpCfgPersistent) == 0)
            {
                Error = "this camera cannot store a fixed address";
                return;
            }

            bool stuck, persistent, confirmed;

            // 장치를 여는 구간을 따로 묶는다. 강제 주소는 이 구간이 끝나고 장치를 놓은 뒤에 보내야 한다 —
            // 제 세션이 장치를 쥐고 있는 동안에는 그 명령이 무시된다. 아무것도 열지 않고 보내면 1초 만에
            // 먹는 것을 확인했고, 연 채로 보내면 아무 일도 일어나지 않았다.
            await using (var device = await GevDevice.OpenAsync(info, Exclusive).ConfigureAwait(true))
            {
                // 모드를 먼저 켠다. 영구 모드가 꺼져 있으면 주소 레지스터가 잠겨 있어 쓰기가 거부된다 —
                // 순서를 반대로 하면 주소는 안 들어가고 모드만 바뀌어, 저장한 적 없는 주소로 올라오게 된다.
                await device.WriteRegAsync(GvbsAddr.CurrentIpCfg, WantedCfg(info, dhcp: false)).ConfigureAwait(true);

                await device.WriteRegAsync(GvbsAddr.PersistentIp0, ToUInt32(ip)).ConfigureAwait(true);
                await device.WriteRegAsync(GvbsAddr.PersistentSubnet0, ToUInt32(mask)).ConfigureAwait(true);
                await device.WriteRegAsync(GvbsAddr.PersistentGateway0, ToUInt32(gateway)).ConfigureAwait(true);

                // 되읽어 확인한다. 쓰기가 성공해도 장치가 그 비트를 그대로 받아들이지 않을 수 있고, 그러면
                // 고정 주소로 바꿨다고 말해 놓고 다음 기동에 DHCP 로 올라온다.
                var after = await device.ReadRegAsync(GvbsAddr.CurrentIpCfg).ConfigureAwait(true);
                stuck = (after & GvbsAddr.IpCfgDhcp) != 0;
                persistent = (after & GvbsAddr.IpCfgPersistent) != 0;
                GevLog.Info("IpConfig", $"current ip configuration reads 0x{after:X} after the write");

                // 주소가 실제로 남았는지 보고 나서 리셋을 건다. 쓰자마자 걸면 장치가 앞의 쓰기를 붙들고 있어
                // 리셋을 받지 않는다 — 확인하는 시간이 그 틈을 겸한다.
                confirmed = await ConfirmAsync(device, ip, mask, gateway).ConfigureAwait(true);
            }

            GevLog.Info("IpConfig", $"registers written; persistent={persistent}, dhcp still set={stuck}, stored confirmed={confirmed}");

            // 제어권을 놓은 뒤에 쓸 주소를 그대로 앉힌다. 여기서 0.0.0.0 을 먼저 보낼 이유가 없다 —
            // 주소가 있는 쪽으로 옮기는 것이 곧 인터페이스를 다시 세우는 일이고, 갈 곳까지 함께 알려 준다.
            await RestartAddressingAsync(info.Mac, ip, mask, gateway).ConfigureAwait(true);

            var note = !persistent
                ? $"{ip} was written but the camera did not accept the fixed-address mode."
                : stuck
                    ? $"{ip} is stored, but this camera keeps DHCP enabled as well — it may still take a "
                      + "server's address if one answers first."
                    : $"{ip} is stored and set as the fixed address.";

            // 돌아오기를 기다리며 훑지 않는다. 카메라는 잠깐 사라졌다가 새 주소로 올라오는데, 그동안 붙들고
            // 있어 봐야 화면만 멈춘다 — 목록에서 내리고 검색에 맡긴다. 검색이 켜져 있으면 알아서 다시 잡히고,
            // 꺼져 있으면 사람이 누른다.
            Devices.Remove(Devices.FirstOrDefault(d => d.Info.Mac.Equals(info.Mac)) ?? target);
            Regroup();
            Selected = null;
            Stored = null;
            Error = null;
            Status = $"{note} Its addressing was restarted; it will reappear on {ip} at the next scan.";
        });
    }

    private Task SetAddressingAsync(bool dhcp)
    {
        if (Selected is not { } target) return Task.CompletedTask;

        return RunAsync(dhcp ? "Switching to DHCP" : "Switching to the stored address", async () =>
        {
            var want = dhcp ? GvbsAddr.IpCfgDhcp : GvbsAddr.IpCfgPersistent;
            if ((target.Info.SupportedIpCfg & want) == 0)
            {
                Error = dhcp
                    ? "this camera does not support DHCP"
                    : "this camera does not support a stored address";
                return;
            }

            var info = target.Info;
            bool took;

            // 장치를 여는 구간을 따로 묶는다 — 제어 접근을 놓았다 다시 잡는 것이 리셋을 대신하는 장치가 있어,
            // 그 일은 이 구간이 끝난 뒤라야 할 수 있다.
            await using (var device = await GevDevice.OpenAsync(info, Exclusive).ConfigureAwait(true))
            {
                // 배타 접근으로 연다. 제어 접근으로 같은 값을 써도 이 카메라는 반영하지 않는다 — 벤더 도구가
                // 보내는 것을 그대로 잡아 견줘 보니 쓰는 값은 같고 접근 방식만 달랐다(CCP 1 대 2).
                // 재부팅을 동반하는 설정이라 소유자가 하나임을 요구하는 것으로 보인다.
                await device.WriteRegAsync(GvbsAddr.CurrentIpCfg, WantedCfg(info, dhcp)).ConfigureAwait(true);

                if (dhcp)
                {
                    // 저장된 주소도 비운다. 남겨 두면 그 주소로 올라오는 장치가 있어, DHCP 로 바꿨다는 말이
                    // 거짓이 된다. 이 자리는 32비트 주소값이라 "빈 값" 은 0.0.0.0 이다.
                    await device.WriteRegAsync(GvbsAddr.PersistentIp0, 0).ConfigureAwait(true);
                    await device.WriteRegAsync(GvbsAddr.PersistentSubnet0, 0).ConfigureAwait(true);
                    await device.WriteRegAsync(GvbsAddr.PersistentGateway0, 0).ConfigureAwait(true);
                }

                // 바뀐 것을 확인하고 나서 리셋을 건다. 쓰자마자 걸면 장치가 앞의 쓰기를 아직 붙들고 있어 받지 않는다.
                var after = await device.ReadRegAsync(GvbsAddr.CurrentIpCfg).ConfigureAwait(true);
                took = (after & want) != 0;
            }

            // 앉힐 주소가 없다. DHCP 는 카메라가 스스로 서버에 물어야 하는 일이라, 우리가 할 수 있는 것은
            // 주소를 거둬 인터페이스를 다시 세우게 하고 어디로 올라오는지 지켜보는 것뿐이다.
            await RestartAddressingAsync(info.Mac, IPAddress.Any, IPAddress.Any, IPAddress.Any).ConfigureAwait(true);
            GevLog.Info("IpConfig", $"addressing set to {(dhcp ? "dhcp" : "persistent")}; took={took}");

            Devices.Remove(target);
            Regroup();
            Selected = null;
            Stored = null;
            Error = took ? null : "the camera did not accept that addressing mode";

            var what = dhcp ? "Set to DHCP." : "Set to the stored address.";
            Status = $"{what} Its addressing was restarted; the camera will come back on whatever address that gives it.";
        });
    }

    /// <summary>
    /// 고른 카메라가 들어오는 어댑터의 방화벽 상태. 창을 열어 확인하기 전에 여기서 먼저 보여 준다 —
    /// 제어는 오가는데 스트림만 오지 않는 증상의 첫 번째 원인이고, 그때 봐야 할 것이 정확히 이 두 가지다.
    /// </summary>
    public string? Firewall
    {
        get => _firewall;
        private set => Set(ref _firewall, value);
    }

    /// <summary>
    /// 어댑터가 어느 프로필에 있고 그 프로필의 방화벽이 켜져 있는지 묻는다. 관리자 권한 없이 읽을 수 있고,
    /// 운영체제 도구에 물어보므로 방화벽 API 를 직접 붙들지 않는다. 윈도우가 아니면 아무것도 하지 않는다.
    /// </summary>
    private async Task RefreshFirewallAsync(HostNic? nic)
    {
        if (!CanOpenSystemSettings || nic is null)
        {
            Firewall = null;
            return;
        }

        Firewall = "checking...";
        // 장치 자체(인터페이스 GUID)로 찾는다. 주소로 찾으면 주소가 없는 어댑터 — 링크가 내려갔거나 DHCP 임대
        // 전인 상태 — 를 아예 조회할 수 없는데, 카메라가 안 보일 때 확인해야 할 것이 바로 그런 어댑터다.
        // GUID 는 주소가 붙기 전에도 있고, 이름과 달리 중복되지도 바뀌지도 않으며 언제나 ASCII 다.
        var guid = nic.Id.Replace("'", "''");
        var script = $"$a = Get-NetAdapter -IncludeHidden | Where-Object InterfaceGuid -eq '{guid}'; "
                   + "if ($null -eq $a) { 'noadapter' } else { "
                   + "$p = (Get-NetConnectionProfile -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue).NetworkCategory; "
                   + "if ($null -eq $p) { \"noprofile|$($a.Status)\" } else { $f = Get-NetFirewallProfile -Name $p; "
                   + "\"$p|$($f.Enabled)|$($f.DefaultInboundAction)\" } }";

        try
        {
            var psi = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                Firewall = null;
                return;
            }

            var text = (await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(true)).Trim();
            proc.WaitForExit(5000);
            Firewall = DescribeFirewall(text);
        }
        catch (Exception ex)
        {
            Firewall = $"could not be read ({ex.Message})";
        }
    }

    /// <summary>
    /// 화면에서 고른 방식을 그대로 레지스터 값으로 만든다. <b>장치가 지금 무엇으로 설정돼 있는지는 보지 않는다</b> —
    /// 사람이 고른 것이 곧 의도인데, 장치 값에서 비트를 빼고 더하는 식으로 만들면 그 사이에 장치가 바뀐 것에
    /// 끌려가 고른 적 없는 조합이 나간다.
    /// <para>
    /// 링크로컬은 늘 켠다. 다른 방식이 모두 실패했을 때의 마지막 수단이라 꺼 두면 장치를 잃을 수 있다.
    /// 장치가 지원한다고 알린 것만 남긴다 — 지원 여부는 상태가 아니라 능력이라 되읽을 필요가 없다.
    /// </para>
    /// </summary>
    private static uint WantedCfg(GevDeviceInfo info, bool dhcp)
    {
        var wanted = (dhcp ? GvbsAddr.IpCfgDhcp : GvbsAddr.IpCfgPersistent) | GvbsAddr.IpCfgLla;
        return wanted & info.SupportedIpCfg;
    }

    /// <summary>쓰기와 재시작 사이에 두는 틈. 장치가 방금 받은 값을 자기 것으로 삼기 전에 걸면 받지 않는다.</summary>
    private const int RestartSettleMs = 800;

    /// <summary>
    /// 레지스터에 써 둔 설정을 지금 적용시킨다. 주소 설정은 인터페이스를 세울 때만 읽히므로, 이것이 없으면
    /// 써 놓고도 전원을 뽑았다 꽂기 전까지 아무것도 달라지지 않는다.
    /// <para>
    /// 고정 주소면 쓸 주소를 그대로 보낸다. DHCP 면 앉힐 주소가 없으므로 0.0.0.0 을 보낸다 — 주소를 잃는
    /// 것 자체가 인터페이스를 다시 세우라는 신호가 되고, 그러면 방금 켜 둔 DHCP 로 올라온다.
    /// </para>
    /// <para>
    /// 장치 리셋 명령을 쓰지 않는다. 그 명령이 있는 카메라도 이 방법으로 반영되고, 이쪽이 훨씬 빠르며
    /// (측정: 0.5~2.3초), 다른 설정을 건드리지 않는다. 무엇보다 명령이 없는 카메라와 같은 길로 갈 수 있어
    /// 장치마다 다르게 동작할 여지가 사라진다.
    /// </para>
    /// <para>
    /// 제어권을 놓은 뒤에 보내야 한다 — 우리가 쥐고 있는 동안에는 이 명령이 무시된다. 놓자마자 보내지 않고
    /// 잠깐 두는 것도 필요하다. 브로드캐스트라 열 수 없는 장치에도 닿는다.
    /// </para>
    /// </summary>
    private static async Task RestartAddressingAsync(PhysicalAddress mac, IPAddress ip, IPAddress mask, IPAddress gateway)
    {
        await Task.Delay(RestartSettleMs).ConfigureAwait(true);
        await GevDiscovery.ForceIpAsync(mac, ip, mask, gateway).ConfigureAwait(true);
        GevLog.Info("IpConfig", $"force-ip {ip} sent to {DeviceRowVm.Format(mac)} to apply what was written");
    }

    /// <summary>
    /// 쓴 주소가 장치에 남았는지 되읽어 확인한다. 리셋은 이것이 끝난 뒤에 건다 — 쓰자마자 리셋을 걸면
    /// 장치가 앞의 쓰기를 아직 붙들고 있어 리셋을 받지 않고, 무엇이 저장됐는지 모르는 채로 재우게 된다.
    /// </summary>
    private static async Task<bool> ConfirmAsync(GevDevice device, IPAddress ip, IPAddress mask, IPAddress gateway)
    {
        for (var i = 0; i < 5; i++)
        {
            var a = await device.ReadRegAsync(GvbsAddr.PersistentIp0).ConfigureAwait(true);
            var m = await device.ReadRegAsync(GvbsAddr.PersistentSubnet0).ConfigureAwait(true);
            var g = await device.ReadRegAsync(GvbsAddr.PersistentGateway0).ConfigureAwait(true);
            if (a == ToUInt32(ip) && m == ToUInt32(mask) && g == ToUInt32(gateway)) return true;
            await Task.Delay(200).ConfigureAwait(true);
        }

        return false;
    }

    /// <summary>
    /// 고른 어댑터의 속성 창을 바로 연다. 특정 어댑터의 속성을 여는 명령은 따로 없어서, 네트워크 연결 폴더에서
    /// 그 항목을 찾아 "속성" 동작을 부른다. 어느 단계든 실패하면 목록 창이라도 연다.
    /// </summary>
    public void OpenAdapterSettings()
    {
        var nic = SelectedGroup?.Nic ?? MatchingNic;
        if (nic is null)
        {
            Launch("ncpa.cpl");
            return;
        }

        // 셸이 그 항목을 부르는 이름으로 찾는다 — 어댑터 API 가 주는 이름과 다른 장치가 있어서
        // (TeamViewer VPN 은 어댑터로는 "로컬 영역 연결" 인데 연결 창에는 "TeamViewer VPN" 으로 있다)
        // 어댑터 이름으로 찾으면 그런 장치의 속성이 열리지 않는다.
        var wanted = nic.Shown.Replace("'", "''");
        // 속성 동사의 가속키는 한국어판과 영문판이 모두 R 이다(속성(&R) / P&roperties). 못 찾으면 마지막 동사를
        // 쓴다 — 셸이 속성을 맨 끝에 두기 때문이다.
        var script = "$f = (New-Object -ComObject Shell.Application).NameSpace(0x31); "
                   + $"$i = $f.Items() | Where-Object {{ $_.Name -eq '{wanted}' }}; "
                   + "if (-not $i) { exit 1 }; "
                   + "$v = $i.Verbs() | Where-Object { $_.Name -like '*&R*' }; "
                   + "if (-not $v) { $v = @($i.Verbs())[-1] }; "
                   // 창은 셸이 띄우지만 호출한 프로세스가 곧바로 사라지면 그 호출이 끝나기 전에 끊긴다.
                   + "$v.DoIt(); Start-Sleep -Seconds 3";

        if (!RunHidden("powershell", script))
        {
            Launch("ncpa.cpl");
            return;
        }

        // 창은 셸이 띄우므로 우리 창 뒤에 열린다. 앞으로 올릴 권한은 지금 전경에 있는 이 앱에 있으니 여기서 올린다.
        _ = BringToFrontAsync(nic.Shown);
    }

    /// <summary>
    /// 방금 열린 속성 창을 앞으로 가져온다. 창 제목이 연결 이름으로 시작한다("이더넷 속성" / "Ethernet Properties").
    /// 셸이 띄우는 데 시간이 걸리므로 잠깐 기다리며 찾는다 — 못 찾으면 조용히 포기한다. 창은 이미 열려 있다.
    /// </summary>
    private static async Task BringToFrontAsync(string connectionName)
    {
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(200).ConfigureAwait(true);
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;
                    if (!proc.MainWindowTitle.StartsWith(connectionName, StringComparison.Ordinal)) continue;
                    SetForegroundWindow(proc.MainWindowHandle);
                    return;
                }
                catch
                {
                    // 이미 사라진 프로세스 — 다음 것을 본다.
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>창을 띄우지 않고 스크립트를 돌린다. 성공하면 true.</summary>
    private bool RunHidden(string program, string script)
    {
        try
        {
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            var proc = Process.Start(psi);
            // 기다리지 않는다 — 창은 셸이 띄우고 이 프로세스는 그 뒤에 스스로 사라진다. 여기서 붙잡고 있으면
            // 창이 열리는 동안 화면이 멈춘 것처럼 보인다.
            return proc is not null;
        }
        catch (Exception ex)
        {
            Error = $"could not open the adapter properties: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 어댑터에 붙일 이름. 네트워크 연결 창에서 바꾸는 그 이름이라, 설비에서 부르는 대로("상단검사" 같이)
    /// 고쳐 두면 이 도구에도 윈도우에도 같은 이름으로 보인다.
    /// </summary>
    public string AdapterName
    {
        get => _adapterName;
        set => Set(ref _adapterName, value);
    }

    /// <summary>어댑터 이름을 바꾼다. 셸이 그 항목을 부르는 이름으로 찾아 이름 바꾸기를 시킨다.</summary>
    public Task RenameAdapterAsync()
    {
        if (SelectedGroup?.Nic is not { } nic) return Task.CompletedTask;

        var wanted = AdapterName.Trim();
        if (wanted.Length == 0)
        {
            Error = "the name cannot be empty";
            return Task.CompletedTask;
        }

        if (string.Equals(wanted, nic.Shown, StringComparison.Ordinal))
        {
            Status = "That is already its name.";
            return Task.CompletedTask;
        }

        // 윈도우도 같은 이름을 거부하지만 그 거부는 조용히 지나가므로 여기서 먼저 막는다.
        if (Nics.Any(n => !ReferenceEquals(n, nic) && string.Equals(n.Shown, wanted, StringComparison.OrdinalIgnoreCase)))
        {
            Error = $"another adapter is already called \"{wanted}\"";
            return Task.CompletedTask;
        }

        return RunAsync($"Renaming {nic.Shown}", async () =>
        {
            var from = nic.Shown.Replace("'", "''");
            var to = wanted.Replace("'", "''");
            var script = "$f = (New-Object -ComObject Shell.Application).NameSpace(0x31); "
                       + $"$i = $f.Items() | Where-Object {{ $_.Name -eq '{from}' }}; "
                       + "if (-not $i) { exit 1 }; "
                       + $"$i.Name = '{to}'";

            if (!RunHidden("powershell", script))
            {
                Error = "the adapter could not be renamed";
                return;
            }

            // 셸이 이름을 바꾸는 데 잠깐 걸린다 — 바뀐 뒤에 다시 읽어야 목록에 새 이름이 뜬다.
            await Task.Delay(800).ConfigureAwait(true);
            await LoadConnectionNamesAsync().ConfigureAwait(true);

            // 정말 바뀌었는지 확인한다. 셸이 거부해도 프로세스는 멀쩡히 끝나므로, 확인하지 않으면
            // 바뀌지 않은 것을 바뀌었다고 말하게 된다.
            var now = Nics.FirstOrDefault(n => n.Id == nic.Id)?.Shown;
            if (string.Equals(now, wanted, StringComparison.Ordinal))
            {
                Status = $"Renamed to \"{wanted}\".";
            }
            else
            {
                Error = $"Windows did not accept that name; it is still \"{now}\"";
            }
        });
    }

    /// <summary>네트워크 연결 목록을 연다 — 어댑터를 켜고 끄거나 이름을 바꾸는 자리다.</summary>
    /// <summary>
    /// 카메라에 저장돼 있는 영구 주소. 지금 쓰는 주소와 다를 수 있다 — DHCP 로 올라온 카메라는 현재 주소가
    /// 서버가 준 것이고 저장된 주소는 그대로 남아 있다. 무엇이 남아 있는지 보이지 않으면 다음 기동에 어디로
    /// 올라올지 알 수 없으므로 따로 읽어 적는다.
    /// </summary>
    public string? Stored
    {
        get => _stored;
        private set => Set(ref _stored, value);
    }

    private async Task RefreshStoredAsync(DeviceRowVm row)
    {
        // 무언가 진행 중이면 열지 않는다. 이 읽기는 화면을 채우자는 것뿐이라 미뤄도 잃을 것이 없고,
        // 겹쳐 열면 진행 중인 작업이 제어권을 잃는다.
        if (IsBusy) return;

        if ((row.Info.SupportedIpCfg & GvbsAddr.IpCfgPersistent) == 0)
        {
            Stored = "this camera has no stored address";
            return;
        }

        Stored = "reading...";
        try
        {
            await using var device = await GevDevice.OpenAsync(row.Info).ConfigureAwait(true);
            var ip = await device.ReadRegAsync(GvbsAddr.PersistentIp0).ConfigureAwait(true);
            var mask = await device.ReadRegAsync(GvbsAddr.PersistentSubnet0).ConfigureAwait(true);
            var gw = await device.ReadRegAsync(GvbsAddr.PersistentGateway0).ConfigureAwait(true);

            var uses = (row.Info.CurrentIpCfg & GvbsAddr.IpCfgPersistent) != 0;
            Stored = ip == 0
                ? "nothing stored — this camera will not come up on a fixed address"
                : $"stored: {ToAddress(ip)} / {ToAddress(mask)}   gateway {ToAddress(gw)}"
                  + (uses ? "   (this is what it comes up on)" : "   (kept, but not used while another mode is selected)");
        }
        catch (Exception ex)
        {
            Stored = $"the stored address could not be read ({ex.Message})";
        }
    }

    public void OpenNetworkConnections() => Launch("ncpa.cpl");

    /// <summary>방화벽 설정을 연다. 제어는 오가는데 스트림만 오지 않으면 대개 이쪽이다.</summary>
    public void OpenFirewallSettings() => Launch("wf.msc");

    private void Launch(string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(what) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error = $"could not open {what}: {ex.Message}";
        }
    }

    public Task ScanAsync() => RunAsync("Scanning", async () =>
    {
        LoadNics();
        var found = await GevDiscovery.DiscoverAsync().ConfigureAwait(true);
        Apply(found);

        var stranded = Devices.Count(d => d.IsStranded);
        Status = found.Count == 0
            ? "No camera answered. Check the cable and that the adapter has an IPv4 address."
            : stranded == 0
                ? $"{found.Count} camera(s)."
                : $"{found.Count} camera(s); {stranded} outside the host subnet.";
    });

    /// <summary>
    /// 빈 주소를 하나 제안한다. 고른 인터페이스와 같은 대역에서, 호스트 자신과 이미 답한 카메라들을 피해 고른다.
    /// 서브넷이 어긋난 카메라를 되살리는 것이 이 도구의 주된 쓸모라 그 자리를 자동으로 잡아 준다.
    /// </summary>
    public void Suggest()
    {
        if (MatchingNic is not { } nic || nic.Address is null || nic.Mask is null || Selected is null) return;

        var address = nic.Address.GetAddressBytes();
        var mask = nic.Mask.GetAddressBytes();

        // 어댑터가 정하는 것은 대역까지다. 마스크가 통째로 덮는 옥텟만 적고 나머지는 비워 둔다 —
        // 그 뒤는 사람이 정할 자리이고, 우리가 아무 숫자나 채우면 뜻이 있는 값처럼 보인 채로 그대로 나간다.
        var fixedOctets = 0;
        while (fixedOctets < 4 && mask[fixedOctets] == 255) fixedOctets++;

        // 온전히 한 자리만 남는 마스크(/32)면 적을 것이 없다. 마지막 칸은 언제나 사람 몫으로 남긴다.
        if (fixedOctets == 4) fixedOctets = 3;

        Ip = string.Join(".", address.Take(fixedOctets)) + ".";
        Mask = nic.Mask.ToString();
        Status = $"Filled in what this adapter fixes ({Ip}); type the rest.";
        Error = null;
    }

    private async Task ScanCoreAsync()
    {
        var found = await GevDiscovery.DiscoverAsync().ConfigureAwait(true);
        Apply(found);
    }

    /// <summary>
    /// 찾은 것을 목록에 반영한다. <b>바뀐 것이 없으면 아무것도 하지 않는다</b> — 매초 목록을 새로 만들면
    /// 고른 항목의 객체가 바뀌어 편집 중인 칸이 장치 값으로 덮인다. 이름을 치는 도중에 글자가 사라지면 못 쓴다.
    /// </summary>
    private void Apply(IReadOnlyList<GevDeviceInfo> found)
    {
        var ordered = found.OrderBy(d => d.Address.ToString()).ToList();
        if (IsSameAsShown(ordered)) return;

        var keep = Selected?.Info.Mac;
        Devices.Clear();
        foreach (var info in ordered) Devices.Add(new DeviceRowVm(info));
        Regroup();

        // 고르고 있던 카메라가 그대로 있으면 화면은 손대지 않고 객체만 갈아 끼운다. 여기서 장치 값을 다시
        // 읽어 넣으면, 방식을 DHCP 에서 고정으로 막 바꿔 놓은 사람의 선택이 다음 검색 한 번에 지워진다.
        var again = keep is null ? null : Devices.FirstOrDefault(d => d.Info.Mac.Equals(keep));
        if (again is not null) Select(again, loadFields: false);
        else Selected = Devices.FirstOrDefault();
    }

    /// <summary>지금 그려 둔 것과 같은지. 장치를 가리는 것은 MAC 이고, 화면에 보이는 것은 주소와 이름이다.</summary>
    private bool IsSameAsShown(IReadOnlyList<GevDeviceInfo> found)
    {
        if (found.Count != Devices.Count) return false;
        for (var i = 0; i < found.Count; i++)
        {
            var a = found[i];
            var b = Devices[i].Info;
            if (!a.Mac.Equals(b.Mac)
                || !a.Address.Equals(b.Address)
                || !a.Subnet.Equals(b.Subnet)
                || !a.Gateway.Equals(b.Gateway)
                || !a.InterfaceAddress.Equals(b.InterfaceAddress)
                || !string.Equals(a.UserDefinedName, b.UserDefinedName, StringComparison.Ordinal)
                || a.CurrentIpCfg != b.CurrentIpCfg)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 장치를 들어온 어댑터로 묶는다. 카메라가 붙지 않은 어댑터도 빈 채로 보여 준다 —
    /// 어느 포트가 비어 있는지, 케이블을 꽂았는데 왜 안 보이는지를 아는 것도 이 도구의 일이다.
    /// </summary>
    private void Regroup()
    {
        // 고른 어댑터와, 이름 칸에 치고 있던 것을 기억해 둔다. 목록이 새로 만들어지면 고른 객체가 사라져
        // 오른쪽 패널이 통째로 비는데, 이름을 바꾼 직후에 그렇게 되면 방금 무엇을 했는지 확인할 자리가 없어진다.
        var keepGroup = _selectedGroup?.Key;
        var typed = _adapterName;

        Groups.Clear();
        var byInterface = Devices.ToLookup(d => d.Info.InterfaceAddress.ToString());

        foreach (var nic in Nics)
        {
            var mine = nic.Address is null ? new List<DeviceRowVm>() : byInterface[nic.Address.ToString()].ToList();
            Groups.Add(new NicGroupVm(nic, nic.Address, mine));
        }

        // 어느 어댑터에도 맞지 않는 주소로 들어온 장치 — 어댑터가 그새 사라졌거나 주소가 바뀐 경우다.
        var known = Nics.Where(n => n.Address is not null).Select(n => n.Address!.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var g in byInterface.Where(g => !known.Contains(g.Key)).OrderBy(g => g.Key))
        {
            Groups.Add(new NicGroupVm(null, g.First().Info.InterfaceAddress, g.ToList()));
        }

        if (keepGroup is null) return;

        var again = Groups.FirstOrDefault(g => string.Equals(g.Key, keepGroup, StringComparison.Ordinal));
        if (again is null) return;

        SelectedGroup = again;

        // 이름 칸은 고른 어댑터의 지금 이름으로 채워지는데, 사람이 치던 중이었다면 그것을 되돌려 준다 —
        // 목록이 새로 그려졌다는 이유로 입력을 뺏지 않는다. 방금 이름을 바꾼 경우에는 친 것과 지금 이름이
        // 같으므로 아무 일도 일어나지 않는다.
        if (!string.Equals(typed, again.Nic?.Shown, StringComparison.Ordinal)) AdapterName = typed;
    }

    private void LoadFields(DeviceRowVm row)
    {
        UserName = row.Info.UserDefinedName;
        IsDhcpSelected = (row.Info.CurrentIpCfg & GvbsAddr.IpCfgDhcp) != 0
                         && (row.Info.CurrentIpCfg & GvbsAddr.IpCfgPersistent) == 0;
        Ip = row.Info.Address.ToString();
        Mask = row.Info.Subnet.ToString();
        Gateway = row.Info.Gateway.ToString();
    }

    /// <summary>
    /// 호스트 어댑터를 훑는다. 올라와 있고 주소가 있는 것만 고르지 않는다 — 꺼진 포트와 주소를 못 받은 포트도
    /// 함께 보여야 "케이블은 꽂았는데 카메라가 안 보인다" 의 답을 여기서 찾을 수 있다. 되돌이만 뺀다.
    /// </summary>
    /// <summary>
    /// 사람이 아는 어댑터만 훑는다. 되돌이·터널·없는 장치는 빼고, 무엇보다 NDIS 필터가 만든 가상
    /// 인터페이스를 뺀다 — 필터 하나마다 어댑터 사본이 하나씩 생겨서, 이 PC 에서는 진짜 셋에 사본 마흔 넘게
    /// 딸려 나온다. 사본은 설명이 예외 없이 필터 이름과 -0000 으로 끝나므로 그것으로 가른다.
    /// 꺼져 있거나 주소가 없는 것은 남긴다 — 카메라가 안 보이는 이유가 대개 그쪽이다.
    /// </summary>
    private void LoadNics()
    {
        var before = Nics.Select(n => n.ToString()).ToList();
        Nics.Clear();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsRealAdapter(nic)) continue;

            var isUp = nic.OperationalStatus == OperationalStatus.Up;
            var v4 = isUp
                ? nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .ToList()
                : new List<UnicastIPAddressInformation>();

            if (v4.Count == 0)
            {
                Nics.Add(new HostNic(nic.Name, nic.Description, nic.Id, null, null, isUp) { Display = Shown(nic.Description) });
                continue;
            }

            foreach (var addr in v4) Nics.Add(new HostNic(nic.Name, nic.Description, nic.Id, addr.Address, addr.IPv4Mask, true) { Display = Shown(nic.Description) });
        }

        Raise(nameof(MatchingNic));
        Raise(nameof(CanSuggest));

        // 어댑터가 달라졌으면 장치가 그대로여도 묶음을 다시 만든다.
        if (!before.SequenceEqual(Nics.Select(n => n.ToString()))) Regroup();
    }

    /// <summary>
    /// 네트워크 연결 창이 항목마다 갖고 있는 표시 이름을 장치 이름에 이어 붙여 읽어 둔다.
    /// 셸이 쓰는 이름과 어댑터 API 가 주는 이름이 어긋나는 장치가 있어서, 사람에게 보여 줄 이름도
    /// 속성 창을 열 때 찾을 이름도 이쪽이 기준이다.
    /// </summary>
    private async Task LoadConnectionNamesAsync()
    {
        if (!CanOpenSystemSettings) return;

        const string script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; "
                            + "$f=(New-Object -ComObject Shell.Application).NameSpace(0x31); "
                            + "$f.Items() | ForEach-Object { $f.GetDetailsOf($_,2) + [char]9 + $_.Name }";
        try
        {
            var psi = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var proc = Process.Start(psi);
            if (proc is null) return;
            var text = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(true);
            proc.WaitForExit(5000);

            _connectionNames.Clear();
            foreach (var line in text.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) continue;
                _connectionNames[parts[0]] = parts[1];
            }

            LoadNics();
        }
        catch
        {
            // 못 읽으면 어댑터 이름으로 간다 — 목록은 그대로 쓸 수 있다.
        }
    }

    private string Shown(string description) => _connectionNames.TryGetValue(description, out var n) ? n : "";

    /// <summary>카메라를 물릴 수 있는 진짜 어댑터인지.</summary>
    private static bool IsRealAdapter(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return false;
        if (nic.OperationalStatus == OperationalStatus.NotPresent) return false;

        var d = nic.Description;
        // 필터가 만든 사본 — 원래 어댑터 설명 뒤에 필터 이름과 일련번호가 붙는다.
        if (d.EndsWith("-0000", StringComparison.Ordinal)) return false;
        // 전화 접속·터널 미니포트와, 무선 직결용 가상 어댑터, 커널 디버깅용 어댑터.
        if (d.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)) return false;
        if (d.Contains("Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase)) return false;
        if (d.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private bool TryReadFields(out IPAddress ip, out IPAddress mask, out IPAddress gateway)
    {
        ip = IPAddress.Any;
        mask = IPAddress.Any;
        gateway = IPAddress.Any;

        if (!TryParse(Ip, "address", out ip)) return false;
        if (!TryParse(Mask, "subnet mask", out mask)) return false;
        if (!TryParse(Gateway, "gateway", out gateway)) return false;

        // 잘못된 주소는 장치를 아예 못 찾게 만든다. 명백히 못 쓰는 것만이라도 미리 막는다.
        var host = ToUInt32(ip);
        var m = ToUInt32(mask);
        if (m != 0 && ((host & ~m) == 0 || (host | m) == uint.MaxValue))
        {
            Error = "that address is the network or broadcast address of its subnet";
            return false;
        }

        Error = null;
        return true;
    }

    private bool TryParse(string text, string what, out IPAddress address)
    {
        if (IPAddress.TryParse(text.Trim(), out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            address = parsed;
            return true;
        }

        address = IPAddress.Any;
        Error = $"{text} is not a valid IPv4 {what}";
        return false;
    }

    /// <summary>방화벽 조회 결과를 사람이 읽을 문장으로. 무엇을 못 읽었는지도 이유를 적는다.</summary>
    private static string DescribeFirewall(string text)
    {
        var parts = text.Split('|');
        if (parts.Length == 0 || parts[0].Length == 0) return "the firewall state could not be read";

        if (parts[0] == "noadapter") return "this adapter was not found by the system; it may have just been removed";

        if (parts[0] == "noprofile")
        {
            // 프로필은 연결된 망에만 붙는다. 내려간 어댑터에 프로필이 없는 것은 정상이고, 그 자체가 답이다.
            var status = parts.Length > 1 ? parts[1] : "not connected";
            return $"no network profile yet ({status}). Windows assigns one when the link comes up and carries traffic, "
                 + "and the firewall then follows whichever profile it lands in — so this cannot be judged until the "
                 + "cable is live.";
        }

        if (parts.Length < 2) return "the firewall state could not be read";

        var profile = parts[0];
        var isOn = string.Equals(parts[1], "True", StringComparison.OrdinalIgnoreCase);
        var inbound = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : "not configured";

        return isOn
            ? $"{profile} profile, firewall ON (inbound default: {inbound}). Streaming needs the return path open — "
              + "this library punches it by sending to the port the camera streams from, but a rule that blocks the "
              + "program outright will still drop every packet."
            : $"{profile} profile, firewall off. Nothing here will block the stream.";
    }

    private static string Describe(uint cfg)
    {
        var parts = new List<string>();
        if ((cfg & GvbsAddr.IpCfgPersistent) != 0) parts.Add("persistent");
        if ((cfg & GvbsAddr.IpCfgDhcp) != 0) parts.Add("DHCP");
        if ((cfg & GvbsAddr.IpCfgLla) != 0) parts.Add("link-local");
        return parts.Count == 0 ? "none reported" : string.Join(", ", parts);
    }

    private static uint ToUInt32(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static IPAddress ToAddress(uint value)
        => new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });

    private async Task RunAsync(string what, Func<Task> body)
    {
        if (IsBusy)
        {
            Error = $"{what} was not started — another operation is still running";
            return;
        }

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
