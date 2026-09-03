namespace GevSharp.Cli.Commands;

/// <summary>노드 하나의 값을 종류에 맞게 찍는다. stdout 에는 값만 — 스크립트가 그대로 받아 쓸 수 있게.</summary>
public sealed class GetCmd : ICliCommand
{
    public string Name => "get";

    public string Summary => "read one feature and print its value";

    public string Usage =>
        "get <ip[:port]> <node> [--detail]\n" +
        "  --detail   also print kind, visibility, range/increment/unit, enumeration entries or category features\n" +
        "  Prints the value typed per node kind: integers as decimal (hex / IPv4 / MAC per the representation), floats with\n" +
        "  their unit, strings quoted, booleans true/false, enumerations by symbolic name, commands as idle/executing,\n" +
        "  registers as address, length and the first bytes. Opens the device read-only unless --access says otherwise.";

    public CliOptSpec Spec { get; } = new CliOptSpec().Flag("detail");

    public async Task<int> RunAsync(CliArgs args, CancellationToken ct)
    {
        var target = DeviceArgs.Target(args);
        var nodeName = args.Positional(1, "node");
        args.RejectExtraPositionals(2);
        var opt = DeviceArgs.BuildOpt(args, GevAccessMode.ReadOnly);

        await using var dev = await target.OpenAsync(opt, ct);
        var nodes = await NodeMapAccess.TryGetAsync(dev, ct);
        if (nodes is null) return CliExitCode.Device;

        var node = NodeMapAccess.Require(nodes, nodeName);
        var access = await node.GetAccessModeAsync(ct);
        if (NodeText.HasValue(node) && !NodeText.IsReadable(access))
        {
            Console.Error.WriteLine($"error: {node.Name} is {access} ({NodeText.AccessTag(access)}) and cannot be read");
            return CliExitCode.Device;
        }

        Console.WriteLine(await NodeText.ReadValueAsync(node, ct));
        if (args.Has("detail"))
        {
            foreach (var line in await NodeText.DescribeAsync(node, ct)) Console.WriteLine("  " + line);
        }
        return CliExitCode.Ok;
    }
}
