using System.Net;
using System.Net.Sockets;
using GevSharp.Xml;

namespace GevSharp.Tests.Xml;

/// <summary>
/// http 내려받기의 시한 초과를 실제 시한(<see cref="GevXmlLoader.HttpTimeoutMs"/>)만큼 기다려 밟는다.
/// 한 번에 10 초가 들어, 다른 적재 시험 뒤에 줄 서지 않고 나란히 돌도록 클래스를 따로 둔다.
/// </summary>
public class GevXmlLoaderHttpTimeoutTests
{
    /// <summary>연결은 받아 두고 아무것도 답하지 않는 서버. 받은 연결은 해제할 때까지 쥐고 있는다.</summary>
    private sealed class SilentTcpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = new();
        private int _accepted;

        public SilentTcpServer()
        {
            _listener.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _ = AcceptLoopAsync();
        }

        public Uri BaseUri { get; }

        /// <summary>받아 둔 연결 수 — 시한 초과가 정말로 서버 쪽에서 났는지(연결은 됐는지) 보는 데 쓴다.</summary>
        public int Accepted => Volatile.Read(ref _accepted);

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                lock (_clients) _clients.Add(client);
                Interlocked.Increment(ref _accepted);
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_clients)
            {
                foreach (var c in _clients) c.Dispose();
                _clients.Clear();
            }
        }
    }

    [Fact]
    public async Task AnHttpTimeoutStaysWrappedSoItIsNotReadAsDeviceLoss()
    {
        // 감싸지 않은 GevTimeoutException 은 호출자에게 "장치를 잃었다, 다시 연결하라" 는 뜻이다. 서버가 답하지 않은 것은
        // 장치와 무관하다 — 다시 연결해도 같은 서버에서 같은 시한 초과를 다시 겪는다. 그래서 이 형이 맨몸으로 나가면 안 되고,
        // 두 URL 의 실패를 모은 GevException 안에 실려야 한다. Second URL 이 First URL 과 같아 시도한 URL 이 하나뿐인 경우가
        // 그 규칙이 가장 쉽게 새는 자리다(실패가 하나뿐이면 "모두 같은 형" 이 곧바로 참이 된다).
        using var server = new SilentTcpServer();
        var port = new FakeMemPort();
        var url = server.BaseUri + "cam.xml";
        port.SetFirstUrl(url);
        port.SetSecondUrl(url);

        var ex = await Assert.ThrowsAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, TestContext.Current.CancellationToken));

        var inner = Assert.IsType<GevTimeoutException>(ex.InnerException);
        Assert.Contains("Downloading camera XML", inner.Message);
        Assert.Equal(true, inner.Data[GevXmlLoader.HttpTimeoutKey]);   // 실제로 던지는 자리가 "서버 쪽 시한 초과" 표식을 단다
        Assert.Contains("identical to the First URL", ex.Message);
        Assert.Equal(1, server.Accepted);   // 연결은 됐다 — 시한 초과는 답하지 않은 서버에서 났다
    }
}
