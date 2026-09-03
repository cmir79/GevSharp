using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GevSharp.Tests.Xml;

/// <summary>
/// 127.0.0.1 의 임시 포트에서 GET 요청 하나에 응답하는 최소 HTTP 서버.
/// 경로를 받아 (상태 코드, 본문) 을 돌려주는 핸들러로 동작하며, 요청마다 연결을 닫는다.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<string, (int Status, byte[] Body)> _handler;
    private readonly CancellationTokenSource _cts = new();

    public Uri BaseUri { get; }

    /// <summary>응답 헤더에 실을 Content-Length — null 이면 본문 길이. 본문과 다른 값을 선언해 클라이언트의 선언 길이 검사를 시험한다.</summary>
    public long? DeclaredContentLength { get; set; }

    public LoopbackHttpServer(Func<string, (int Status, byte[] Body)> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
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

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = await ReadHeaderAsync(stream);
                var path = ParsePath(header);
                var (status, body) = _handler(path);
                var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Error" };
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/octet-stream\r\nContent-Length: {DeclaredContentLength ?? body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, 0, head.Length);
                await stream.WriteAsync(body, 0, body.Length);
                await stream.FlushAsync();
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // 테스트 클라이언트가 먼저 끊은 경우 등 — 서버 쪽 오류는 테스트 실패로 드러난다.
            }
        }
    }

    private static async Task<string> ReadHeaderAsync(NetworkStream stream)
    {
        var buf = new byte[1024];
        var all = new MemoryStream();
        while (all.Length < 64 * 1024)
        {
            var n = await stream.ReadAsync(buf, 0, buf.Length);
            if (n <= 0) break;
            all.Write(buf, 0, n);
            var text = Encoding.ASCII.GetString(all.GetBuffer(), 0, (int)all.Length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }

        return Encoding.ASCII.GetString(all.GetBuffer(), 0, (int)all.Length);
    }

    private static string ParsePath(string header)
    {
        var line = header.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 ? parts[1] : "/";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
