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
    private readonly Action<string, bool>? _report;
    private bool _isExpanded;
    private bool _hasLoadedChildren;
    private string _text = "";
    private bool? _boolValue;
    private string? _enumValue;
    private bool _isReadOnly = true;
    private bool _isDirty;
    private AccessMode _access = AccessMode.ReadWrite;
    private bool _isAvailable = true;
    private string? _status;
    private bool _isBusy;

    public NodeVm(INode node, NodeVm? parent = null, Action<string, bool>? report = null)
    {
        _node = node;
        Parent = parent;
        _report = report;
        if (node is ICategory category)
        {
            foreach (var feature in category.Features) Children.Add(new NodeVm(feature, this, report));
        }
    }

    /// <summary>담고 있는 카테고리. 한 노드를 쓰면 같은 칸의 다른 노드가 풀리거나 잠기므로 그때 형제를 다시 읽는다.</summary>
    public NodeVm? Parent { get; }

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
            _ = LoadAsync();
        }
    }

    /// <summary>
    /// 편집 중인 글. 사용자가 고치면 <see cref="IsDirty"/> 가 서고, 그동안은 주기 갱신이 이 칸을 덮지 않는다 —
    /// 타이핑하는 도중에 장치 값이 들어와 글자가 사라지면 못 쓴다.
    /// </summary>
    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) SetDirty(true); }
    }

    /// <summary>
    /// 지금 이 칸에 커서가 있는지. 주기 갱신은 여기가 참인 칸만 건너뛴다 —
    /// 고쳐 놓고 나간 칸까지 영영 건너뛰면 그 칸만 옛 값으로 남는다.
    /// </summary>
    public bool IsEditing { get; private set; }

    public void BeginEdit() => IsEditing = true;

    public void EndEdit() => IsEditing = false;

    /// <summary>사용자가 고쳐 놓고 아직 쓰지 않은 상태.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    private void SetDirty(bool value) => IsDirty = value;

    /// <summary>장치에서 읽은 값을 넣는다 — 이것은 사용자의 편집이 아니므로 편집 표시를 세우지 않는다.</summary>
    private void SetTextFromDevice(string value)
    {
        _text = value;
        Raise(nameof(Text));
        SetDirty(false);
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

    /// <summary>마지막으로 읽은 접근 권한. 왜 못 쓰는지를 화면에 적기 위해 남긴다.</summary>
    public AccessMode Access
    {
        get => _access;
        private set => Set(ref _access, value);
    }

    /// <summary>읽기·쓰기가 거부되면 그 이유를 그대로 적는다 — 조용히 빈칸으로 두지 않는다.</summary>
    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>
    /// 글로 적은 값을 장치에 쓴다. 종류에 맞게 해석하고, 해석에 실패하면 쓰지 않는다.
    /// <para>
    /// 값이 증분 격자에 어긋나면 가장 가까운 격자 값으로 맞춰 한 번 더 쓴다. 노출 시간처럼 겉으로는 증분이 없는
    /// 변환 노드도 속에서는 격자 위에 있어(실측: 노출은 35 us 격자) 사람이 고른 값이 그대로는 들어가지 않는다.
    /// 조용히 바꾸지는 않는다 — 무엇을 대신 썼는지 반드시 알린다.
    /// </para>
    /// </summary>
    public Task CommitTextAsync() => ApplyAsync(WriteTextAsync);

    private async Task WriteTextAsync()
    {
        try
        {
            await WriteRawAsync(Text).ConfigureAwait(true);
        }
        catch (GenApiException ex) when (Snap(ex, Text) is { } snapped)
        {
            await WriteRawAsync(snapped.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);
            _report?.Invoke($"{Label}: {Text} is not a step this device accepts; wrote {snapped} instead.", false);
            SetTextFromDevice(snapped.ToString(CultureInfo.InvariantCulture));
        }
    }

    private Task WriteRawAsync(string text) => _node switch
    {
        IInteger i => i.SetAsync(ParseInteger(text)).AsTask(),
        IFloat f => f.SetAsync(double.Parse(text, CultureInfo.InvariantCulture)).AsTask(),
        IString s => s.SetAsync(text).AsTask(),
        _ => Task.CompletedTask,
    };

    /// <summary>격자에 어긋나 거절된 값을 가장 가까운 격자 값으로 옮긴다. 격자를 모르면 null — 그때는 거절을 그대로 올린다.</summary>
    /// <summary>지금 들고 있는 항목이 방금 읽은 것과 같은지. 같으면 목록을 다시 만들지 않는다.</summary>
    private bool SameOptions(IReadOnlyList<IEnumEntry> entries)
    {
        if (EnumOptions.Count != entries.Count) return false;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!string.Equals(EnumOptions[i], entries[i].Symbolic, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static long? Snap(GenApiException ex, string text)
    {
        if (ex.Data[GenApiException.GridAnchorKey] is not long anchor) return null;
        if (ex.Data[GenApiException.GridIncrementKey] is not long inc || inc <= 1) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var wanted)) return null;

        var steps = Math.Round((wanted - anchor) / inc, MidpointRounding.AwayFromZero);
        return anchor + (long)steps * inc;
    }

    public Task ExecuteAsync() => ApplyAsync(() => ((ICommand)_node).ExecuteAsync().AsTask());

    /// <summary>접근 권한과 현재 값을 다시 읽는다. 값이 없거나 거부되면 <see cref="Status"/> 에 남긴다.</summary>
    public async Task RefreshAsync()
    {
        if (IsCategory) return;
        _isBusy = true;
        try
        {
            var access = await _node.GetAccessModeAsync().ConfigureAwait(true);
            Access = access;
            IsAvailable = access is AccessMode.ReadOnly or AccessMode.ReadWrite or AccessMode.WriteOnly;
            IsReadOnly = access is not (AccessMode.ReadWrite or AccessMode.WriteOnly);
            if (!IsAvailable)
            {
                Status = access == AccessMode.NotImplemented ? "not implemented" : "not available";
                SetTextFromDevice("");
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
                    SetTextFromDevice((await i.GetAsync().ConfigureAwait(true)).ToString(CultureInfo.InvariantCulture));
                    break;
                case IFloat f:
                    SetTextFromDevice((await f.GetAsync().ConfigureAwait(true)).ToString("G6", CultureInfo.InvariantCulture));
                    break;
                case IString s:
                    SetTextFromDevice(await s.GetAsync().ConfigureAwait(true));
                    break;
                case IBoolean b:
                    _boolValue = await b.GetAsync().ConfigureAwait(true);
                    Raise(nameof(BoolValue));
                    break;
                case IEnumeration e:
                    // 목록이 그대로면 손대지 않는다. 주기마다 비웠다 채우면 펼쳐 둔 목록이 닫히고 고를 수가 없다.
                    var entries = await e.GetAvailableEntriesAsync().ConfigureAwait(true);
                    if (!SameOptions(entries))
                    {
                        EnumOptions.Clear();
                        foreach (var entry in entries) EnumOptions.Add(entry.Symbolic);
                    }

                    var symbolic = await e.GetAsync().ConfigureAwait(true);
                    if (!string.Equals(_enumValue, symbolic, StringComparison.Ordinal))
                    {
                        _enumValue = symbolic;
                        Raise(nameof(EnumValue));
                    }

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

    /// <summary>
    /// 주기 갱신. 장치가 스스로 바꾸는 값(온도·자동 노출 결과 등)을 따라가려면 다시 읽어야 한다.
    /// 카테고리는 바로 아래 자식까지만 훑는다 — 트리를 깊이 따라가면 읽기 왕복이 걷잡을 수 없이 는다.
    /// </summary>
    public async Task PollAsync()
    {
        if (IsCategory)
        {
            foreach (var child in Children)
            {
                if (!child.IsCategory) await child.PollAsync().ConfigureAwait(true);
            }

            return;
        }

        if (_isBusy || IsEditing) return;
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 이 노드를 화면에 채운다. 카테고리면 그 안의 노드들을, 아니면 자기 값을 읽는다.
    /// 펼치는 것과 고르는 것 양쪽에서 불린다 — 어느 쪽으로 들어와도 값이 뜨게 하려는 것이다.
    /// </summary>
    public async Task LoadAsync()
    {
        if (!IsCategory)
        {
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        _hasLoadedChildren = true;
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
            _report?.Invoke($"{Label} written.", false);
        }
        catch (Exception ex)
        {
            Status = Describe(ex);
            _report?.Invoke($"{Label}: {Describe(ex)}", true);
        }
        finally
        {
            _isBusy = false;
        }

        // 쓰기 하나가 다른 노드의 잠금·범위·가용성을 바꾼다. 자기만 다시 읽으면 옆 노드가 잠긴 채로 남는다 —
        // 자동 노출을 껐는데 노출 시간이 계속 읽기 전용으로 보이는 것이 그 증상이다. 같은 칸을 통째로 다시 읽는다.
        await RefreshAsync().ConfigureAwait(true);
        if (Parent is null) return;
        foreach (var sibling in Parent.Children)
        {
            if (!ReferenceEquals(sibling, this)) await sibling.PollAsync().ConfigureAwait(true);
        }
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
