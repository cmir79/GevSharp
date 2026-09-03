using GevSharp.Xml;

namespace GevSharp;

public sealed partial class GevDevice
{
    private GevXmlDoc? _xmlDoc;
    private readonly SemaphoreSlim _xmlLock = new(1, 1);

    /// <summary>
    /// 카메라 XML 을 가져온다(First URL → Second URL 폴백, Local:/File:/http 3경로, ZIP 해제).
    /// 한 번 받으면 세션 동안 캐시한다. 디스크 캐시는 <see cref="GevDeviceOpt.XmlCacheDir"/> 가 있을 때만.
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
                _xmlDoc = await GevXmlLoader.LoadAsync(this, _opt.XmlCacheDir, ct).ConfigureAwait(false);
            }
            return _xmlDoc;
        }
        finally
        {
            _xmlLock.Release();
        }
    }
}
