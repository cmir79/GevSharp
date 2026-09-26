using GevSharp.GenApi;

namespace GevSharp;

public sealed partial class GevDevice
{
    private GenApiNodeMap? _nodeMap;
    private readonly SemaphoreSlim _nodeMapLock = new(1, 1);

    /// <summary>
    /// 카메라 XML 을 받아(<see cref="GetXmlAsync"/>) 이 장치를 포트로 바인딩한 노드맵을 만든다. 세션 동안 한 번만 만들고 캐시한다.
    /// 레지스터를 노드맵 밖에서 직접 썼다면 <see cref="GenApiNodeMap.InvalidateAll"/> 로 캐시를 버린다.
    /// XML 적재 실패는 <see cref="GetXmlAsync"/> 와 같은 예외다(장치 상실은 그 형 그대로), XML 해석·바인딩 실패는 <see cref="GenApiException"/>.
    /// </summary>
    public async Task<GenApiNodeMap> GetNodeMapAsync(CancellationToken ct = default)
    {
        var cached = _nodeMap;
        if (cached is not null) return cached;

        await _nodeMapLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_nodeMap is null)
            {
                var xml = await GetXmlAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                _nodeMap = GenApiNodeMap.Parse(xml.Xml, this);
            }
            return _nodeMap;
        }
        finally
        {
            _nodeMapLock.Release();
        }
    }

    /// <summary>
    /// 노드맵의 <c>TLParamsLocked</c> 에 전송 계층 구성이 끝났음(1)·풀렸음(0)을 알린다. 이 노드는 장치 레지스터가 아니라
    /// 노드맵 안에만 사는 호스트 측 값이고, 벤더 XML 은 이 값으로 획득 커맨드와 포맷 파라미터의 잠금을 표현한다 —
    /// 예를 들어 <c>AcquisitionStart</c> 의 pIsLocked 가 <c>TLParamsLocked = 0</c> 인 장치에서는 1 을 쓰기 전까지
    /// 그 커맨드가 잠긴 WO, 즉 접근 불가(NA)로 보여 실행할 수 없다.
    /// 순서: 스트림 <c>StartAsync</c> → 이 메서드에 true → AcquisitionStart … AcquisitionStop → 이 메서드에 false → 스트림 <c>StopAsync</c>.
    /// 값을 썼으면 true. false 는 아무것도 쓰지 않았다는 뜻이고 두 경우다 — 노드가 없는 장치(그런 장치는 이 잠금을 쓰지 않는다, Debug 로그)와,
    /// 같은 이름의 노드가 정수 노드가 아닌 기술(쓸 방법을 모른다 — 그 노드에 걸린 잠금은 그대로 남으므로 Warn 로그에 노드 종류를 적는다).
    /// 예외로 올리지 않는 것은 앞의 경우가 정상이라서다. 뒤의 경우를 가려야 하면 <see cref="GenApiNodeMap.GetNode"/> 로 종류를 본다.
    /// </summary>
    public async Task<bool> SetTlParamsLockedAsync(bool locked, CancellationToken ct = default)
    {
        var nodes = await GetNodeMapAsync(ct).ConfigureAwait(false);
        var found = nodes.GetNode(TlParamsLockedNode);
        if (found is not GenApi.IInteger node)
        {
            // 두 경우를 한 문구로 적지 않는다 — 노드가 있는데 "없다" 고 적히면, 획득 커맨드가 잠긴 채 남은 이유를 로그에서 찾을 수 없다.
            if (found is null)
                GevLog.Debug(LogSrc, $"{TlParamsLockedNode} is not in the node map of {Address}; transport-layer locking is not used by this device");
            else
                GevLog.Warn(LogSrc, $"{TlParamsLockedNode} on {Address} is a {found.Kind} node, not an integer; nothing was written, so features the description gates on it keep their current lock state");
            return false;
        }
        await node.SetAsync(locked ? 1 : 0, ct).ConfigureAwait(false);
        GevLog.Debug(LogSrc, $"{TlParamsLockedNode} = {(locked ? 1 : 0)} on {Address}");
        return true;
    }

    private const string TlParamsLockedNode = "TLParamsLocked";
}
