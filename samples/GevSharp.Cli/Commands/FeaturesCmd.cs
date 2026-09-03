using GevSharp.GenApi;

namespace GevSharp.Cli.Commands;

/// <summary>
/// GenApi 노드 트리를 Root(또는 지정 카테고리)부터 순회하며 종류·접근 모드·값을 찍는다.
/// 노드 하나의 읽기 실패는 그 줄에만 남기고 순회는 계속한다. 기본은 Beginner 가시성, --visibility 로 넓히고 --all 은 전부.
/// </summary>
public sealed class FeaturesCmd : ICliCommand
{
    private const int MaxDepth = 32;

    public string Name => "features";

    public string Summary => "walk the GenApi node tree with kind, access mode and value";

    public string Usage =>
        "features <ip[:port]> [--all] [--visibility beginner|expert|guru] [--category name]\n" +
        "  --visibility level  show nodes up to this visibility (default beginner)\n" +
        "  --all               show every node, including Invisible ones and nodes reported as not implemented\n" +
        "  --category name     start at this category instead of Root\n" +
        "  Opens the device read-only unless --access says otherwise. Each line is: Name [Kind] access = value, where\n" +
        "  access is RW / RO / WO / NA (not available) / NI (not implemented). Read errors are shown inline as <error: ...>.";

    public CliOptSpec Spec { get; } = new CliOptSpec().Flag("all").Value("visibility").Value("category");

    public async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var target = DeviceArgs.Target(args);
        args.RejectExtraPositionals(1);
        var showAll = args.Has("all");
        var maxVisibility = showAll ? Visibility.Invisible : args.GetEnum("visibility", Visibility.Beginner);
        if (maxVisibility == Visibility.Invisible && !showAll)
            throw new CliUsageException("option --visibility expects beginner | expert | guru (use --all for invisible nodes)");
        var categoryName = args.Get("category");
        var opt = DeviceArgs.BuildOpt(args, GevAccessMode.ReadOnly);

        await using var dev = await target.OpenAsync(opt, ct);
        var nodes = await NodeMapAccess.TryGetAsync(dev, ct);
        if (nodes is null) return CliExitCode.Device;

        var info = nodes.Info;
        Console.WriteLine($"{info.VendorName} {info.ModelName}: {nodes.Nodes.Count} nodes, schema {info.SchemaMajorVersion}.{info.SchemaMinorVersion}, XML version {info.MajorVersion}.{info.MinorVersion}.{info.SubMinorVersion}");

        ICategory root;
        if (categoryName is null)
        {
            root = nodes.Root;
        }
        else
        {
            var node = NodeMapAccess.Require(nodes, categoryName);
            root = node as ICategory ?? throw new CliUsageException($"node '{categoryName}' is a {node.Kind}, not a category");
        }

        var walker = new TreeWalker(Console.Out, showAll, maxVisibility);
        await walker.WalkAsync(root, 0, ct);
        Console.WriteLine();
        Console.WriteLine($"{walker.Shown} node(s) shown, {walker.Skipped} skipped (visibility above {maxVisibility.ToString().ToLowerInvariant()} or not implemented; --all shows them)");
        return CliExitCode.Ok;
    }

    private sealed class TreeWalker
    {
        private readonly TextWriter _out;
        private readonly bool _showAll;
        private readonly Visibility _maxVisibility;
        private readonly HashSet<string> _visitedCategories = new(StringComparer.Ordinal);

        public TreeWalker(TextWriter output, bool showAll, Visibility maxVisibility)
        {
            _out = output;
            _showAll = showAll;
            _maxVisibility = maxVisibility;
        }

        public int Shown { get; private set; }
        public int Skipped { get; private set; }

        public async Task WalkAsync(INode node, int depth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var indent = new string(' ', depth * 2);

            AccessMode access;
            string accessTag;
            try
            {
                access = await node.GetAccessModeAsync(ct);
                accessTag = NodeText.AccessTag(access);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 접근 모드조차 못 읽는 노드도 목록에는 남긴다 — 값 읽기를 시도해 같은 오류를 인라인으로 보여 준다.
                access = AccessMode.ReadWrite;
                accessTag = $"??({ex.GetType().Name})";
            }

            if (!_showAll && (node.Visibility > _maxVisibility || access == AccessMode.NotImplemented))
            {
                Skipped++;
                return;
            }

            if (node is ICategory category)
            {
                Shown++;
                if (!_visitedCategories.Add(node.Name))
                {
                    _out.WriteLine($"{indent}{node.Name} [Category] (already listed above)");
                    return;
                }
                _out.WriteLine($"{indent}{node.Name} [Category] ({category.Features.Count} feature(s))");
                if (depth >= MaxDepth)
                {
                    _out.WriteLine($"{indent}  ... (depth limit {MaxDepth})");
                    return;
                }
                foreach (var feature in category.Features) await WalkAsync(feature, depth + 1, ct);
                return;
            }

            var value = await NodeText.ReadValueOrErrorAsync(node, access, ct);
            _out.WriteLine($"{indent}{node.Name} [{node.Kind}] {accessTag} = {value}");
            Shown++;
        }
    }
}
