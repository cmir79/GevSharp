using GevSharp.GenApi;

namespace GevSharp.Cli.Commands;

/// <summary>features/get/set 가 공유하는 노드맵 획득 — 실패 사유를 한 곳에서 같은 말로 알린다.</summary>
public static class NodeMapAccess
{
    /// <summary>
    /// 노드맵을 만든다. 실패하면 이유를 stderr 에 쓰고 null.
    /// 갈래는 둘이다 — XML 을 못 가져오면 <see cref="GevException"/>, 가져온 XML 을 못 묶으면 <see cref="GenApiException"/>.
    /// (<see cref="GenApiException"/> 이 <see cref="GevException"/> 을 상속하므로 한 번에 잡는다.)
    /// </summary>
    public static async Task<GenApiNodeMap?> TryGetAsync(GevDevice dev, CancellationToken ct)
    {
        try
        {
            return await dev.GetNodeMapAsync(ct);
        }
        catch (GevException ex)
        {
            Console.Error.WriteLine($"error: could not build the GenApi node map ({ex.Message})");
            return null;
        }
    }

    /// <summary>이름으로 노드를 찾는다. 없으면 사용법 오류 — 이름은 사용자가 준 것이다.</summary>
    public static INode Require(GenApiNodeMap nodes, string name)
        => nodes.GetNode(name) ?? throw new CliUsageException($"node '{name}' not found in the node map ({nodes.Nodes.Count} nodes; names are case-sensitive)");
}
