using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GevSharp.Viewer.ViewModels;

/// <summary>화면에만 쓰이는 작은 변환들. 뷰모델이 색이나 굵기를 들고 있지 않게 하려고 여기에 둔다.</summary>
public static class VmConverters
{
    /// <summary>열어 둔 장치를 굵게 — 목록만 보고 무엇이 붙어 있는지 알 수 있어야 한다.</summary>
    public static readonly IValueConverter OpenWeight =
        new FuncValueConverter<bool, FontWeight>(open => open ? FontWeight.Bold : FontWeight.Normal);

    /// <summary>고른 카메라의 타일에만 테두리를 준다.</summary>
    public static readonly IValueConverter SelectedBorder =
        new FuncValueConverter<bool, IBrush>(selected =>
            selected ? new SolidColorBrush(Color.FromRgb(0x61, 0xAF, 0xEF)) : Brushes.Transparent);
}
