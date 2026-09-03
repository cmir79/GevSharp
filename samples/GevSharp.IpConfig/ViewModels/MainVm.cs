using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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

/// <summary>호스트의 IPv4 주소 하나. 어느 대역으로 맞춰야 하는지 고르는 근거가 된다.</summary>
public sealed record HostNic(string Interface, IPAddress Address, IPAddress Mask)
{
    public override string ToString() => $"{Interface} — {Address} / {Mask}";
}

/// <summary>
/// 카메라의 주소를 잡아 주는 도구. 스트리밍을 하지 않으므로 장치를 열 필요가 없는 일(ForceIP)과
/// 열어야만 되는 일(영구 주소 쓰기)을 나눠 다룬다.
/// </summary>
public sealed class MainVm : VmBase
{
    private DeviceRowVm? _selected;
    private HostNic? _selectedNic;
    private bool _isBusy;
    private string _status = "Press Scan to look for cameras. Cameras on the wrong subnet answer too.";
    private string? _error;
    private string _ip = "";
    private string _mask = "";
    private string _gateway = "";

    public ObservableCollection<DeviceRowVm> Devices { get; } = new();
    public ObservableCollection<HostNic> Nics { get; } = new();

    public DeviceRowVm? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedSummary));
            if (value is not null) LoadFields(value);
        }
    }

    public HostNic? SelectedNic
    {
        get => _selectedNic;
        set { if (Set(ref _selectedNic, value)) Raise(nameof(CanSuggest)); }
    }

    public bool HasSelection => _selected is not null;

    public bool CanSuggest => _selectedNic is not null && _selected is not null;

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
    }

    public Task ScanAsync() => RunAsync("Scanning", async () =>
    {
        LoadNics();
        var found = await GevDiscovery.DiscoverAsync().ConfigureAwait(true);
        var keep = Selected?.Info.Address.ToString();

        Devices.Clear();
        foreach (var info in found.OrderBy(d => d.Address.ToString())) Devices.Add(new DeviceRowVm(info));
        Selected = Devices.FirstOrDefault(d => d.Info.Address.ToString() == keep) ?? Devices.FirstOrDefault();

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
        if (SelectedNic is not { } nic || Selected is null) return;

        var host = ToUInt32(nic.Address);
        var mask = ToUInt32(nic.Mask);
        var network = host & mask;
        var broadcast = network | ~mask;
        var taken = Devices.Select(d => ToUInt32(d.Info.Address)).Append(host).ToHashSet();

        for (var candidate = network + 1; candidate < broadcast; candidate++)
        {
            if (taken.Contains(candidate)) continue;
            Ip = ToAddress(candidate).ToString();
            Mask = nic.Mask.ToString();
            Gateway = "0.0.0.0";
            Status = $"Proposed {Ip} — free in {ToAddress(network)}/{nic.Mask}.";
            Error = null;
            return;
        }

        Error = "no free address in that subnet";
    }

    /// <summary>
    /// 지금 당장 주소를 바꾼다. 브로드캐스트로 나가므로 서브넷이 어긋나 열 수 없는 장치에도 닿는다.
    /// 전원을 껐다 켜면 사라지는 임시 설정이다 — 되살려 놓고 영구 주소를 쓰는 순서로 쓴다.
    /// </summary>
    public Task ForceAsync()
    {
        if (Selected is not { } target) return Task.CompletedTask;
        if (!TryReadFields(out var ip, out var mask, out var gateway)) return Task.CompletedTask;

        return RunAsync($"Forcing {ip}", async () =>
        {
            await GevDiscovery.ForceIpAsync(target.Info.Mac, ip, mask, gateway).ConfigureAwait(true);
            Status = $"{ip} applied to {DeviceRowVm.Format(target.Info.Mac)}. This lasts until the camera is power-cycled.";
            await Task.Delay(700).ConfigureAwait(true);
            await ScanCoreAsync().ConfigureAwait(true);
        });
    }

    /// <summary>
    /// 전원을 껐다 켜도 남는 주소를 쓴다. 장치를 열어야 하므로 지금 닿는 주소여야 한다 —
    /// 닿지 않는 장치는 먼저 임시로 되살린 뒤에 이것을 쓴다.
    /// </summary>
    public Task PersistAsync()
    {
        if (Selected is not { } target) return Task.CompletedTask;
        if (!TryReadFields(out var ip, out var mask, out var gateway)) return Task.CompletedTask;

        return RunAsync($"Writing {ip} persistently", async () =>
        {
            if ((target.Info.SupportedIpCfg & GvbsAddr.IpCfgPersistent) == 0)
            {
                Error = "this camera does not support a persistent address; use Force IP or DHCP instead";
                return;
            }

            await using var device = await GevDevice.OpenAsync(target.Info).ConfigureAwait(true);
            await device.WriteRegAsync(GvbsAddr.PersistentIp0, ToUInt32(ip)).ConfigureAwait(true);
            await device.WriteRegAsync(GvbsAddr.PersistentSubnet0, ToUInt32(mask)).ConfigureAwait(true);
            await device.WriteRegAsync(GvbsAddr.PersistentGateway0, ToUInt32(gateway)).ConfigureAwait(true);

            // 주소만 써 두면 장치는 여전히 DHCP 나 LLA 로 뜬다. 쓰겠다는 표시까지 켜야 다음 기동에 그 주소로 온다.
            var cfg = await device.ReadRegAsync(GvbsAddr.CurrentIpCfg).ConfigureAwait(true);
            await device.WriteRegAsync(GvbsAddr.CurrentIpCfg, cfg | GvbsAddr.IpCfgPersistent).ConfigureAwait(true);

            Status = $"{ip} stored. It takes effect the next time the camera powers up.";
        });
    }

    private async Task ScanCoreAsync()
    {
        var found = await GevDiscovery.DiscoverAsync().ConfigureAwait(true);
        var keep = Selected?.Info.Mac;
        Devices.Clear();
        foreach (var info in found.OrderBy(d => d.Address.ToString())) Devices.Add(new DeviceRowVm(info));
        Selected = Devices.FirstOrDefault(d => keep is not null && d.Info.Mac.Equals(keep)) ?? Devices.FirstOrDefault();
    }

    private void LoadFields(DeviceRowVm row)
    {
        Ip = row.Info.Address.ToString();
        Mask = row.Info.Subnet.ToString();
        Gateway = row.Info.Gateway.ToString();
    }

    private void LoadNics()
    {
        var keep = SelectedNic?.Address.ToString();
        Nics.Clear();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                Nics.Add(new HostNic(nic.Name, addr.Address, addr.IPv4Mask));
            }
        }

        SelectedNic = Nics.FirstOrDefault(n => n.Address.ToString() == keep) ?? Nics.FirstOrDefault();
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
