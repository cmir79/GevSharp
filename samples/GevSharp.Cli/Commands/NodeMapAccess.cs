using GevSharp.GenApi;

namespace GevSharp.Cli.Commands;

/// <summary>features/get/set 가 공유하는 노드맵 획득 — 런타임이 없는 빌드를 한 곳에서 같은 말로 알린다.</summary>
public static class NodeMapAccess
{
    /// <summary>노드맵을 만든다. 런타임이 없으면 이유를 stderr 에 쓰고 null.</summary>
    public static async Task<GenApiNodeMap?> TryGetAsync(GevDevice dev, CancellationToken ct)
    {
        try
        {
            return await dev.GetNodeMapAsync(ct);
        }
        catch (NotImplementedException ex)
        {
            Console.Error.WriteLine($"error: the GenApi node map runtime is not available in this build of GevSharp ({ex.Message})");
            return null;
        }
    }

    /// <summary>이름으로 노드를 찾는다. 없으면 사용법 오류 — 이름은 사용자가 준 것이다.</summary>
    public static INode Require(GenApiNodeMap nodes, string name)
        => nodes.GetNode(name) ?? throw new CliUsageException($"node '{name}' not found in the node map ({nodes.Nodes.Count} nodes; names are case-sensitive)");
}
