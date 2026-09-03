using System.Collections.ObjectModel;
using System.Globalization;
using GevSharp.GenApi;

namespace GevSharp.Viewer.ViewModels;

/// <summary>
/// 속성 트리의 한 노드. 카메라가 XML 로 스스로 설명한 것만 그린다 — 이름·표시 이름·설명·단위·종류·접근 권한.
/// 어떤 피처가 무엇을 뜻하는지 아는 표를 두지 않으므로 벤더가 무엇이든 같은 코드로 그려진다.
/// <para>
/// 값 하나를 읽는 것이 곧 UDP 왕복이라 트리를 통째로 읽지 않는다. 카테고리를 펼칠 때 그 자식들만 읽는다.
/// </para>
/// </summary>
public sealed class NodeVm : VmBase
{
    private readonly INode _node;
    private bool _isExpanded;
    private bool _hasLoadedChildren;
    private string _text = "";
    private bool? _boolValue;
    private string? _enumValue;
    private bool _isReadOnly = true;
    private bool _isAvailable = true;
    private string? _status;
    private bool _isBusy;

    public NodeVm(INode node)
    {
        _node = node;
        if (node is ICategory category)
        {
            foreach (var feature in category.Features) Children.Add(new NodeVm(feature));
        }
    }

    public string Name => _node.Name;
    public string Label => string.IsNullOrWhiteSpace(_node.DisplayName) ? _node.Name : _node.DisplayName!;
    public NodeKind Kind => _node.Kind;
    public Visibility Visibility => _node.Visibility;
    public string? Unit => (_node as IInteger)?.Unit ?? (_node as IFloat)?.Unit;

    /// <summary>툴팁이 없으면 설명으로 대신한다 — 둘 다 없는 노드도 있다.</summary>
    public string? Hint => string.IsNullOrWhiteSpace(_node.ToolTip) ? _node.Description : _node.ToolTip;

    public ObservableCollection<NodeVm> Children { get; } = new();

    public bool IsCategory => _node.Kind == NodeKind.Category;
    public bool IsBoolean => _node.Kind == NodeKind.Boolean;
    public bool IsEnumeration => _node.Kind == NodeKind.Enumeration;
    public bool IsCommand => _node.Kind == NodeKind.Command;

    /// <summary>정수·실수·문자열은 한 칸에 글로 적는다 — 종류마다 편집기를 따로 두지 않아도 XML 이 범위를 알려 준다.</summary>
    public bool IsTextual => _node.Kind is NodeKind.Integer or NodeKind.Float or NodeKind.String;

    public ObservableCollection<string> EnumOptions { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value) || !value || _hasLoadedChildren) return;
            _hasLoadedChildren = true;
            _ = LoadChildrenAsync();
        }
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    public bool? BoolValue
    {
        get => _boolValue;
        set
        {
            if (!Set(ref _boolValue, value) || value is null || _isBusy) return;
            _ = ApplyAsync(() => ((IBoolean)_node).SetAsync(value.Value).AsTask());
        }
    }

    public string? EnumValue
    {
        get => _enumValue;
        set
        {
            if (!Set(ref _enumValue, value) || value is null || _isBusy) return;
            _ = ApplyAsync(() => ((IEnumeration)_node).SetAsync(value).AsTask());
        }
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set => Set(ref _isReadOnly, value);
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => Set(ref _isAvailable, value);
    }

    /// <summary>읽기·쓰기가 거부되면 그 이유를 그대로 적는다 — 조용히 빈칸으로 두지 않는다.</summary>
    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>글로 적은 값을 장치에 쓴다. 종류에 맞게 해석하고, 해석에 실패하면 쓰지 않는다.</summary>
    public Task CommitTextAsync() => ApplyAsync(() => _node switch
    {
        IInteger i => i.SetAsync(ParseInteger(Text)).AsTask(),
        IFloat f => f.SetAsync(double.Parse(Text, CultureInfo.InvariantCulture)).AsTask(),
        IString s => s.SetAsync(Text).AsTask(),
        _ => Task.CompletedTask,
    });

    public Task ExecuteAsync() => ApplyAsync(() => ((ICommand)_node).ExecuteAsync().AsTask());

    /// <summary>접근 권한과 현재 값을 다시 읽는다. 값이 없거나 거부되면 <see cref="Status"/> 에 남긴다.</summary>
    public async Task RefreshAsync()
    {
        if (IsCategory) return;
        _isBusy = true;
        try
        {
            var access = await _node.GetAccessModeAsync().ConfigureAwait(true);
            IsAvailable = access is AccessMode.ReadOnly or AccessMode.ReadWrite or AccessMode.WriteOnly;
            IsReadOnly = access is not (AccessMode.ReadWrite or AccessMode.WriteOnly);
            if (!IsAvailable)
            {
                Status = access == AccessMode.NotImplemented ? "not implemented" : "not available";
                Text = "";
                return;
            }

            if (access == AccessMode.WriteOnly)
            {
                Status = "write-only";
                return;
            }

            Status = null;
            switch (_node)
            {
                case IInteger i:
                    Text = (await i.GetAsync().ConfigureAwait(true)).ToString(CultureInfo.InvariantCulture);
                    break;
                case IFloat f:
                    Text = (await f.GetAsync().ConfigureAwait(true)).ToString("G6", CultureInfo.InvariantCulture);
                    break;
                case IString s:
                    Text = await s.GetAsync().ConfigureAwait(true);
                    break;
                case IBoolean b:
                    _boolValue = await b.GetAsync().ConfigureAwait(true);
                    Raise(nameof(BoolValue));
                    break;
                case IEnumeration e:
                    var entries = await e.GetAvailableEntriesAsync().ConfigureAwait(true);
                    EnumOptions.Clear();
                    foreach (var entry in entries) EnumOptions.Add(entry.Symbolic);
                    _enumValue = await e.GetAsync().ConfigureAwait(true);
                    Raise(nameof(EnumValue));
                    break;
            }
        }
        catch (Exception ex)
        {
            Status = Describe(ex);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task LoadChildrenAsync()
    {
        foreach (var child in Children)
        {
            await child.RefreshAsync().ConfigureAwait(true);
        }
    }

    private async Task ApplyAsync(Func<Task> write)
    {
        _isBusy = true;
        try
        {
            Status = null;
            await write().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = Describe(ex);
        }
        finally
        {
            _isBusy = false;
        }

        // 쓰기 하나가 다른 노드의 범위·가용성을 바꿀 수 있다. 적어도 자기 자신은 다시 읽는다.
        await RefreshAsync().ConfigureAwait(true);
    }

    private static long ParseInteger(string text)
    {
        var t = text.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(t, CultureInfo.InvariantCulture);
    }

    private static string Describe(Exception ex) => ex switch
    {
        FormatException => "not a number",
        OverflowException => "out of range",
        _ => ex.Message,
    };
}
