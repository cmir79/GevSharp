using GevSharp.Xml;

namespace GevSharp;

public sealed partial class GevDevice
{
    private GevXmlDoc? _xmlDoc;
    private readonly SemaphoreSlim _xmlLock = new(1, 1);

    /// <summary>
    /// 카메라 XML 을 가져온다(First URL → Second URL 폴백, Local:/File:/http 3경로, ZIP 해제).
    /// 한 번 받으면 세션 동안 캐시한다. 디스크 캐시는 <see cref="GevDeviceOpt.XmlCacheDir"/> 가 있을 때만.
    /// <para>
    /// 적재 중 장치를 잃으면(<see cref="GevControlLostException"/>, 응답 없는 <see cref="GevTimeoutException"/>,
    /// 해제된 장치의 <see cref="ObjectDisposedException"/>) Second URL 로 넘어가지 않고 그 예외를 그대로 던진다 — 다시 연결할 일이다.
    /// 여기서 감싸지 않은 채 나오는 <see cref="GevTimeoutException"/> 은 언제나 이 경우다. 상대가 답은 한 시한 초과(http 내려받기의 시한 초과,
    /// 장치가 PENDING_ACK 로 답한 뒤 연장 안에 못 끝낸 읽기)는 Second URL 로 넘어가고, 두 URL 이 모두 그렇게 실패해도
    /// <see cref="GevException"/> 으로 감싸 낸다. 그 밖에 두 URL 이 같은 종류로 실패하면 그 예외를, 다른 종류로 실패하면 두 사유를 담은
    /// <see cref="GevException"/> 을 던진다(규칙 전체는 <see cref="GevXmlLoader.LoadAsync(IGevPort, string, CancellationToken)"/>).
    /// </para>
    /// </summary>
    public async Task<GevXmlDoc> GetXmlAsync(CancellationToken ct = default)
    {
        var cached = _xmlDoc;
        if (cached is not null) return cached;

        await _xmlLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_xmlDoc is null)
            {
                ThrowIfClosed();
                _xmlDoc = await GevXmlLoader.LoadAsync(this, _opt.XmlCacheDir, Address.ToString(), ct).ConfigureAwait(false);
            }
            return _xmlDoc;
        }
        finally
        {
            _xmlLock.Release();
        }
    }
}
