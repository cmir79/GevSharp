using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GevSharp.Viewer.ViewModels;

/// <summary>변경 통지만 하는 최소 기반 — 이 앱에 MVVM 프레임워크를 끌어들이지 않는다.</summary>
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
