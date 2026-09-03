using GevSharp.GenApi;

namespace GevSharp.Cli.Commands;

/// <summary>노드 하나에 값을 쓰고 다시 읽어 보여 준다. Command 노드는 값 없이 실행한다.</summary>
public sealed class SetCmd : ICliCommand
{
    public string Name => "set";

    public string Summary => "write one feature, then read it back";

    public string Usage =>
        "set <ip[:port]> <node> [<value>]\n" +
        "  Value syntax per kind: Integer decimal or 0x hex; Float decimal; Boolean true/false (1/0, on/off); Enumeration by\n" +
        "  symbolic name (or its integer value); String as-is; Register as hex bytes (0A0B0C0D); Command needs no value.\n" +
        "  Takes control of the device (--access control by default; exclusive also works). Prints 'Name = value' after\n" +
        "  reading the node back, or '(write-only)' when it cannot be read.";

    public CliOptSpec Spec { get; } = new CliOptSpec();

    public async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var target = DeviceArgs.Target(args);
        var nodeName = args.Positional(1, "node");
        var value = args.PositionalOrNull(2);
        args.RejectExtraPositionals(3);
        var opt = DeviceArgs.BuildOpt(args, GevAccessMode.Control);
        if (opt.AccessMode == GevAccessMode.ReadOnly)
            throw new CliUsageException("set needs control of the device; --access readonly cannot write");

        await using var dev = await target.OpenAsync(opt, ct);
        var nodes = await NodeMapAccess.TryGetAsync(dev, ct);
        if (nodes is null) return CliExitCode.Device;

        var node = NodeMapAccess.Require(nodes, nodeName);
        if (node is not ICommand && value is null)
            throw new CliUsageException($"missing <value> for {node.Name} ({node.Kind})");

        await NodeText.WriteValueAsync(node, value, ct);

        var access = await node.GetAccessModeAsync(ct);
        var readBack = NodeText.HasValue(node) && !NodeText.IsReadable(access)
            ? "(write-only)"
            : await NodeText.ReadValueAsync(node, ct);
        Console.WriteLine(node is ICommand ? $"{node.Name} executed; {readBack}" : $"{node.Name} = {readBack}");
        return CliExitCode.Ok;
    }
}
