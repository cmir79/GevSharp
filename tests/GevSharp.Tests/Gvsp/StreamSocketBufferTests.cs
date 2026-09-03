using System.Net;
using System.Net.Sockets;
using GevSharp.Tests.GenApi.Model;

namespace GevSharp.Tests.Gvsp;

/// <summary>
/// 스트림 수신 소켓 버퍼(R12) — 크기는 옵션으로 정하고, OS 가 실제로 내준 값을 로그로 남긴다.
/// 요청이 통째로 빠져도 루프백 테스트는 전부 통과하므로, 확인할 수 있는 것은 내준 값과 그것을 알리는 로그 두 가지뿐이다.
/// <see cref="GevLog.Sink"/> 는 프로세스 전역이라 싱크를 바꿔 끼는 동안 다른 테스트와 나란히 돌지 않는 컬렉션에 둔다.
/// </summary>
[Collection(GevLogSinkCollection.Name)]
public class StreamSocketBufferTests
{
    [Fact]
    public async Task ReceiveBufferIsEnlargedAndTheGrantedSizeIsLogged()
    {
        const int Requested = 4 * 1024 * 1024;

        // 같은 OS 가 같은 요청에 무엇을 내주는지 먼저 잰다. 기본값과 다르지 않으면 이 확인 자체가 성립하지 않으므로 그것도 함께 본다.
        int osDefault;
        int osGranted;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            osDefault = probe.ReceiveBufferSize;
            probe.ReceiveBufferSize = Requested;
            osGranted = probe.ReceiveBufferSize;
        }
        Assert.SkipWhen(osGranted == osDefault, $"this host grants {osGranted} bytes by default, so the request cannot be observed");

        var messages = new List<string>();
        var previousSink = GevLog.Sink;
        var previousLevel = GevLog.MinLevel;
        GevLog.MinLevel = GevLogLevel.Info;
        GevLog.Sink = (_, _, message, _) =>
        {
            lock (messages) messages.Add(message);
        };
        try
        {
            var opt = StreamRig.DefaultOpt();
            opt.SocketBufferBytes = Requested;
            await using var rig = new StreamRig(opt);
            await rig.StartAsync();

            Assert.Equal(osGranted, rig.Stream.SocketReceiveBufferBytes);

            string[] logged;
            lock (messages) logged = messages.ToArray();
            Assert.Contains(logged, m => m.Contains($"receive buffer requested {Requested} bytes, granted {osGranted} bytes"));
        }
        finally
        {
            GevLog.Sink = previousSink;
            GevLog.MinLevel = previousLevel;
        }
    }
}
