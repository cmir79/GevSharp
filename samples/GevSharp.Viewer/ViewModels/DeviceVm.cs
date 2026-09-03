using GevSharp.Gvcp;

namespace GevSharp.Viewer.ViewModels;

/// <summary>검색 목록의 한 줄. 검색 응답이 알려 주는 것만 담는다 — 여는 것은 이 단계가 아니다.</summary>
public sealed class DeviceVm : VmBase
{
    private bool _isOpen;

    public DeviceVm(GevDeviceInfo info)
    {
        Info = info;
    }

    public GevDeviceInfo Info { get; }

    public string Title => string.IsNullOrWhiteSpace(Info.UserDefinedName)
        ? $"{Info.Manufacturer} {Info.Model}"
        : $"{Info.UserDefinedName} ({Info.Model})";

    public string Address => Info.Address.ToString();

    public string Detail => $"{Info.Address}  ·  SN {Info.SerialNumber}  ·  {Info.Manufacturer}";

    /// <summary>이 장치를 이미 열어 두었는지. 목록에서 굵게 보이고, 같은 장치를 두 번 열지 않게 하는 데 쓴다.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set => Set(ref _isOpen, value);
    }

    /// <summary>호스트 인터페이스와 서브넷이 어긋나면 제어는 되어도 스트림이 오지 않을 수 있다 — 목록에서 미리 알린다.</summary>
    public string? Warning => Info.IsReachableDirectly
        ? null
        : $"different subnet from {Info.InterfaceAddress} — streaming may not arrive";
}
