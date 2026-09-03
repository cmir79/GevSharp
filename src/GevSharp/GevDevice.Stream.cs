namespace GevSharp;

public sealed partial class GevDevice
{
    /// <summary>
    /// 스트림 채널을 연다(기본 채널 0). 소켓 바인드·SCDA/SCP/SCPS 설정은 <see cref="GevStream.StartAsync"/> 에서 한다.
    /// 리센드 요청은 이 장치의 제어 소켓(<see cref="Gvcp"/>)으로 나간다 — 장치는 제어 애플리케이션이 보낸 PACKETRESEND 만 받아 준다.
    /// 제어권 없는(ReadOnly) 세션은 스트림 레지스터를 쓸 수 없으므로 <see cref="GevControlLostException"/>.
    /// </summary>
    public Task<GevStream> OpenStreamAsync(GevStreamOpt? opt = null, CancellationToken ct = default)
        => OpenStreamAsync(0, opt, ct);

    /// <summary>지정한 스트림 채널을 연다. 채널 수는 GVBS 0x0904(NumStreamChannels)로 확인한다.</summary>
    public Task<GevStream> OpenStreamAsync(int streamChannel, GevStreamOpt? opt = null, CancellationToken ct = default)
    {
        ThrowIfClosed();
        if (AccessMode == GevAccessMode.ReadOnly)
        {
            throw new GevControlLostException("a read-only session cannot configure a stream channel; open the device with Control or Exclusive access");
        }
        ct.ThrowIfCancellationRequested();
        var stream = new GevStream(this, Gvcp, LocalAddress, opt, streamChannel, Address);
        return Task.FromResult(stream);
    }
}
