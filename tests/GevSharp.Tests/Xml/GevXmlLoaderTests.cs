using System.IO.Compression;
using System.Reflection;
using System.Text;
using GevSharp.Gvcp;
using GevSharp.Tests.Gvcp;
using GevSharp.Xml;

namespace GevSharp.Tests.Xml;

public class GevXmlLoaderTests
{
    private const ulong XmlAddr = 0x20000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- 도우미 ----

    private static byte[] FixtureBytes()
    {
        var asm = typeof(GevXmlLoaderTests).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("XmlLoaderMinimal.xml", StringComparison.Ordinal));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string FixtureText() => Encoding.UTF8.GetString(FixtureBytes()).Trim();

    private static byte[] MakeZip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var es = entry.Open();
                es.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    // 장치 메모리는 XML 뒤에도 이어진다 — 4 경계까지 넓혀 읽어도 실패하지 않게 여유를 붙인다.
    private static byte[] Pad(byte[] data, int extra)
    {
        var padded = new byte[data.Length + extra];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        return padded;
    }

    private static FakeMemPort PortWithLocal(byte[] payload, string fileName, ulong addr = XmlAddr, int? declaredLen = null)
    {
        var port = new FakeMemPort();
        port.AddRegion(addr, Pad(payload, 16));
        port.SetFirstUrl($"Local:{fileName};{addr:X};{declaredLen ?? payload.Length:X}");
        return port;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GevSharpTests_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 임시 디렉터리 정리 실패는 테스트 결과와 무관하다.
            }
        }
    }

    // 어느 오버로드로 읽기가 내려왔는지 기록하는 스트림 — 배열 경로와 span 경로를 갈라 센다.
    private sealed class OverloadRecordingStream : Stream
    {
        private readonly MemoryStream _inner;

        public OverloadRecordingStream(byte[] data) => _inner = new MemoryStream(data);

        public int ArrayReads { get; private set; }
        public int SpanReads { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArrayReads++;
            return _inner.Read(buffer, offset, count);
        }

#if !NETFRAMEWORK
        // .NET Framework 의 Stream 에는 이 오버로드가 없다 — ns2.0 자산이 도는 자산에서는 재정의할 것이 없고,
        // span 읽기가 배열 경로로 내려간다(아래 CappedReadStreamCountsWhatTheSpanPathReads 가 그 갈림을 본다).
        public override int Read(Span<byte> buffer)
        {
            SpanReads++;
            return _inner.Read(buffer);
        }
#endif

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- Local: 장치 메모리 ----

    [Fact]
    public async Task LocalPlainXmlIsReadInMaxPayloadChunksRoundedUpToFour()
    {
        var xml = "<A>" + new string('x', 1200) + "</A>";
        var bytes = Encoding.UTF8.GetBytes(xml);
        Assert.Equal(3, bytes.Length % 4);
        var port = PortWithLocal(bytes, "cam.xml");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal(xml, doc.Xml);
        Assert.Equal("cam.xml", doc.FileName);
        Assert.Equal($"Local:cam.xml;20000;{bytes.Length:X}", doc.Url);
        Assert.Null(doc.SchemaVersion);

        var reads = port.Reads.Where(r => r.Addr >= XmlAddr).ToList();
        Assert.Equal(new[] { 512, 512, 184 }, reads.Select(r => r.Len));
        Assert.Equal(new[] { XmlAddr, XmlAddr + 0x200, XmlAddr + 0x400 }, reads.Select(r => r.Addr));
        Assert.All(reads, r => Assert.True(r.Len <= GvcpConst.MaxMemPayload));
    }

    [Fact]
    public async Task LocalUnalignedAddressIsWidenedToFourByteBoundary()
    {
        var xml = "<A>" + new string('y', 1200) + "</A>";
        var bytes = Encoding.UTF8.GetBytes(xml);
        var region = new byte[2 + bytes.Length + 16];
        region[0] = (byte)'!';
        region[1] = (byte)'!';
        Buffer.BlockCopy(bytes, 0, region, 2, bytes.Length);

        var port = new FakeMemPort();
        port.AddRegion(XmlAddr, region);
        port.SetFirstUrl($"Local:cam.xml;{XmlAddr + 2:X};{bytes.Length:X}");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal(xml, doc.Xml);
        var reads = port.Reads.Where(r => r.Addr >= XmlAddr).ToList();
        Assert.Equal(XmlAddr, reads[0].Addr);
        Assert.Equal((2 + bytes.Length + 3) & ~3, reads.Sum(r => r.Len));
    }

    [Fact]
    public async Task LocalZipYieldsFirstXmlEntry()
    {
        var fixture = FixtureBytes();
        var zip = MakeZip(("readme.txt", Encoding.ASCII.GetBytes("not xml")), ("XmlLoaderMinimal.xml", fixture), ("other.xml", Encoding.ASCII.GetBytes("<other/>")));
        var port = PortWithLocal(zip, "XmlLoaderMinimal.zip");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal(FixtureText(), doc.Xml);
        Assert.StartsWith("<?xml", doc.Xml);
        Assert.Contains("ModelName=\"XmlLoaderMinimal\"", doc.Xml);
        Assert.Equal("XmlLoaderMinimal.zip", doc.FileName);
    }

    [Fact]
    public async Task LocalXmlWithBomAndNulPaddingIsStripped()
    {
        var xml = "<Root><Child/></Root>";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).Concat(new byte[] { 0, 0, 0, 0, 0 }).ToArray();
        var port = PortWithLocal(bytes, "cam.xml");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal(xml, doc.Xml);
    }

    [Fact]
    public async Task LocalSchemaVersionIsCarriedIntoTheDoc()
    {
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        var port = new FakeMemPort();
        port.AddRegion(XmlAddr, Pad(bytes, 16));
        port.SetFirstUrl($"Local:cam.xml;{XmlAddr:X};{bytes.Length:X}?SchemaVersion=1.1.0");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal("1.1.0", doc.SchemaVersion);
        Assert.Equal("<Root/>", doc.Xml);
    }

    [Fact]
    public async Task TruncatedMemoryThrowsGevException()
    {
        var bytes = Encoding.UTF8.GetBytes("<A>" + new string('z', 700) + "</A>");
        var port = new FakeMemPort();
        port.AddRegion(XmlAddr, bytes);
        port.SetFirstUrl($"Local:cam.xml;{XmlAddr:X};{bytes.Length + 100:X}");

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("cam.xml", ex.Message);
        Assert.Contains("First URL", ex.Message);
        Assert.Contains("Second URL", ex.Message);
    }

    [Fact]
    public async Task NonXmlContentThrowsGevException()
    {
        var port = PortWithLocal(Encoding.ASCII.GetBytes("hello world!"), "cam.xml");

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("does not look like XML", ex.Message);
    }

    [Fact]
    public async Task DeclaredLengthAboveLimitIsRejectedBeforeReading()
    {
        var port = new FakeMemPort();
        port.SetFirstUrl($"Local:cam.xml;{XmlAddr:X};7FFFFFFF");

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("limit", ex.Message);
        Assert.Equal(0, port.ReadCountAtOrAbove(XmlAddr));
    }

    [Fact]
    public async Task PreCancelledTokenPropagatesCancellation()
    {
        var port = PortWithLocal(Encoding.UTF8.GetBytes("<Root/>"), "cam.xml");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GevXmlLoader.LoadAsync(port, null, cts.Token));
    }

    [Fact]
    public async Task CancellationDuringTheChunkedMemoryReadPropagatesAsCancellation()
    {
        var bytes = Encoding.UTF8.GetBytes("<A>" + new string('c', 1500) + "</A>");
        var port = PortWithLocal(bytes, "cam.xml");
        using var cts = new CancellationTokenSource();
        port.OnRead = (addr, _) =>
        {
            if (addr >= XmlAddr) cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GevXmlLoader.LoadAsync(port, null, cts.Token));

        Assert.Equal(1, port.ReadCountAtOrAbove(XmlAddr));
    }

    [Fact]
    public async Task CancellationAfterTheUrlReadPropagatesThroughTheFilePath()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);
        var path = Path.Combine(tmp.Path, "cam.xml");
        File.WriteAllText(path, "<Root/>", new UTF8Encoding(false));
        var port = new FakeMemPort();
        port.SetFirstUrl("file:" + path);
        using var cts = new CancellationTokenSource();
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.FirstUrl) cts.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GevXmlLoader.LoadAsync(port, null, cts.Token));
    }

    // ---- First/Second URL 폴백 ----

    [Fact]
    public async Task EmptyFirstUrlFallsBackToSecond()
    {
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        var port = new FakeMemPort();
        port.AddRegion(XmlAddr, Pad(bytes, 16));
        port.SetSecondUrl($"Local:second.xml;{XmlAddr:X};{bytes.Length:X}");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal("<Root/>", doc.Xml);
        Assert.Equal("second.xml", doc.FileName);
        Assert.StartsWith("Local:second.xml", doc.Url);
    }

    [Fact]
    public async Task MalformedFirstUrlFallsBackToSecond()
    {
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        var port = new FakeMemPort();
        port.AddRegion(XmlAddr, Pad(bytes, 16));
        port.SetFirstUrl("Local:broken");
        port.SetSecondUrl($"Local:second.xml;{XmlAddr:X};{bytes.Length:X}");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal("second.xml", doc.FileName);
    }

    [Fact]
    public async Task UnreadableFirstUrlTargetFallsBackToSecond()
    {
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        var port = new FakeMemPort();
        port.AddRegion(XmlAddr, Pad(bytes, 16));
        port.SetFirstUrl("Local:first.xml;F0000000;100");
        port.SetSecondUrl($"Local:second.xml;{XmlAddr:X};{bytes.Length:X}");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal("second.xml", doc.FileName);
    }

    [Fact]
    public async Task BothUrlsFailingReportsBothReasons()
    {
        var port = new FakeMemPort();
        port.SetFirstUrl("Local:first.xml;F0000000;100");
        port.SetSecondUrl("garbage");

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("First URL", ex.Message);
        Assert.Contains("first.xml", ex.Message);
        Assert.Contains("Second URL", ex.Message);
        Assert.Contains("garbage", ex.Message);
    }

    [Fact]
    public async Task IdenticalSecondUrlIsNotRetried()
    {
        var port = new FakeMemPort();
        port.SetFirstUrl("Local:first.xml;F0000000;100");
        port.SetSecondUrl("local:FIRST.xml;F0000000;100");

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("identical to the First URL", ex.Message);
        Assert.Equal(1, port.Reads.Count(r => r.Addr == 0xF0000000UL));
    }

    [Fact]
    public async Task BothUrlsEmptyThrows()
    {
        var port = new FakeMemPort();

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("register is empty", ex.Message);
    }

    // ---- 장치 상실은 감싸지 않는다 ----

    // 두 URL 이 모두 읽을 수 있는 XML 을 가리키는 포트 — 첫 시도가 장치 상실로 끝나면 둘째로 넘어가는지 볼 수 있게.
    private static FakeMemPort PortWithTwoLocalUrls()
    {
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        var port = new FakeMemPort();
        port.AddRegion(XmlAddr, Pad(bytes, 16));
        port.AddRegion(XmlAddr + 0x10000, Pad(bytes, 16));
        port.SetFirstUrl($"Local:first.xml;{XmlAddr:X};{bytes.Length:X}");
        port.SetSecondUrl($"Local:second.xml;{XmlAddr + 0x10000:X};{bytes.Length:X}");
        return port;
    }

    // 채널이 PENDING_ACK 연장을 다 쓴 요청에 내는 것과 같은 모양의 시한 초과 — 장치가 답했다는 표식이 붙어 있다.
    private static GevTimeoutException PendingAckExpired()
    {
        var ex = new GevTimeoutException("READMEM was answered with PENDING_ACK but never completed");
        ex.Data[GvcpChannel.PendingAckExpiredKey] = true;
        return ex;
    }

    [Fact]
    public async Task ControlLossOnTheFirstUrlRegisterIsRethrownWithoutTryingTheSecond()
    {
        // 장치를 잃었으면 Second URL 도 같은 포트를 거친다 — 재시도 예산만 한 번 더 쓰고 같은 이유로 실패한다.
        // 호출자는 형으로 "다시 연결" 과 "XML 이 틀렸다" 를 가르므로, 제어 상실이 일반 GevException 으로 뭉개지면 안 된다.
        var port = PortWithTwoLocalUrls();
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.FirstUrl) throw new GevControlLostException("heartbeat failed");
        };

        var ex = await Assert.ThrowsAsync<GevControlLostException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Equal("heartbeat failed", ex.Message);
        Assert.Equal(0, port.Reads.Count(r => r.Addr == GvbsAddr.SecondUrl));
    }

    [Fact]
    public async Task DisposedPortOnTheFirstUrlRegisterIsRethrownAsDisposed()
    {
        var port = PortWithTwoLocalUrls();
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.FirstUrl) throw new ObjectDisposedException("GevDevice");
        };

        await Assert.ThrowsAsync<ObjectDisposedException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Equal(0, port.Reads.Count(r => r.Addr == GvbsAddr.SecondUrl));
    }

    [Fact]
    public async Task TimeoutWhileReadingLocalXmlMemoryIsNotWrappedAndSkipsTheSecondUrl()
    {
        // 장치 메모리 읽기는 실패를 GevException 으로 감싸 파일 이름을 붙인다 — 장치 상실만은 그 포장 밖으로 그대로 나와야 한다.
        var port = PortWithTwoLocalUrls();
        port.OnRead = (addr, _) =>
        {
            if (addr >= XmlAddr) throw new GevTimeoutException("READMEM got no reply");
        };

        var ex = await Assert.ThrowsAsync<GevTimeoutException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Equal("READMEM got no reply", ex.Message);
        Assert.Equal(0, port.Reads.Count(r => r.Addr == GvbsAddr.SecondUrl));
        Assert.Equal(1, port.ReadCountAtOrAbove(XmlAddr));
    }

    [Fact]
    public async Task DeviceLossOnTheSecondAttemptIsRethrownUnwrapped()
    {
        // 첫 URL 이 내용 문제로 실패하고 둘째를 읽다가 장치를 잃었다 — 지금 할 일은 다시 연결이다(다시 연결하면 둘째가 될 수 있다).
        // 첫 실패의 사유는 경고 로그에 남는다.
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        var port = new FakeMemPort();
        port.AddRegion(XmlAddr, Pad(bytes, 16));
        port.SetFirstUrl("Local:broken");
        port.SetSecondUrl($"Local:second.xml;{XmlAddr:X};{bytes.Length:X}");
        port.OnRead = (addr, _) =>
        {
            if (addr >= XmlAddr) throw new GevControlLostException("control taken over");
        };

        await Assert.ThrowsAsync<GevControlLostException>(() => GevXmlLoader.LoadAsync(port, null, Ct));
    }

    [Fact]
    public async Task SameStatusFailureOnBothUrlRegistersKeepsTheStatusType()
    {
        // 두 URL 이 같은 종류로 실패했으면 그 종류가 곧 답이다 — 상태 코드까지 호출자에게 그대로 간다.
        var port = PortWithTwoLocalUrls();
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.FirstUrl || addr == GvbsAddr.SecondUrl)
                throw new GevStatusException("READMEM", GvcpConst.StatusAccessDenied);
        };

        var ex = await Assert.ThrowsAsync<GevStatusException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Equal(GvcpConst.StatusAccessDenied, ex.Status);
        Assert.Equal(1, port.Reads.Count(r => r.Addr == GvbsAddr.SecondUrl));   // 장치 상실이 아니니 둘째도 시도했다
    }

    [Fact]
    public void HttpTimeoutIsNotDeviceLossSoTheOtherUrlIsStillTried()
    {
        // http 내려받기의 시한 초과는 서버 쪽 사정이다 — 다른 URL(대개 Local:)로 넘어갈 이유가 남아 있다.
        // 끝에서 끝까지 밟으려면 내려받기 시한(10 초)을 실제로 기다려야 해서(GevXmlLoaderHttpTimeoutTests 가 한 번 밟는다),
        // 여기서는 가르는 판정 자체를 본다. 판정은 URL 종류가 아니라 예외에 붙은 표식으로 한다 — http URL 을 적재하는
        // 중에도 캐시 키는 장치에서 읽으므로, 같은 단계에서 난 시한 초과라도 어느 쪽이 답하지 않았는지는 표식만 안다.
        var httpTimeout = new GevTimeoutException("Downloading camera XML timed out");
        httpTimeout.Data[GevXmlLoader.HttpTimeoutKey] = true;

        Assert.False(GevXmlLoader.IsDeviceLoss(httpTimeout));
        Assert.True(GevXmlLoader.IsDeviceLoss(new GevTimeoutException("no reply")));   // 표식 없는 시한 초과 = 응답 없는 GVCP 요청
        // 장치가 PENDING_ACK 로 답한 뒤 연장을 다 쓴 시한 초과 — 장치는 살아 있다.
        Assert.False(GevXmlLoader.IsDeviceLoss(PendingAckExpired()));
        Assert.True(GevXmlLoader.IsDeviceLoss(new GevControlLostException("lost")));
        Assert.True(GevXmlLoader.IsDeviceLoss(new ObjectDisposedException("GevDevice")));
        Assert.False(GevXmlLoader.IsDeviceLoss(new GevStatusException("READMEM", GvcpConst.StatusInvalidAddress)));
        Assert.False(GevXmlLoader.IsDeviceLoss(new GevException("bad XML")));
    }

    [Fact]
    public async Task FailuresOfDifferentKindsAreStillAggregated()
    {
        // 대조군: 첫 URL 은 장치 거절, 둘째는 형식 오류 — 종류가 다르면 두 사유를 모두 실은 GevException 이다.
        var port = new FakeMemPort();
        port.SetSecondUrl("garbage");
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.FirstUrl) throw new GevStatusException("READMEM", GvcpConst.StatusAccessDenied);
        };

        var ex = await Assert.ThrowsAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("First URL", ex.Message);
        Assert.Contains("Second URL", ex.Message);
        Assert.Contains("garbage", ex.Message);
    }

    [Fact]
    public async Task APendingAckThatNeverCompletesOnTheXmlReadFallsBackToTheSecondUrl()
    {
        // 장치가 PENDING_ACK 로 "받아서 실행 중" 이라고 답했다 — 살아 있는 장치다. 연장 안에 못 끝낸 읽기는 상실이 아니라
        // 이 URL 의 실패이므로 Second URL 로 넘어간다(다시 연결해도 같은 First URL 이 같은 자리에서 멈출 뿐이다).
        // 실제 채널이 던지는 예외를 그대로 받아야 두 시한 초과를 가르는 표식까지 밟으므로 루프백 장치로 연다.
        var first = Encoding.ASCII.GetBytes("<First/>");
        var second = Encoding.ASCII.GetBytes("<Second/>");
        using var r = new GvcpTestResponder();
        first.CopyTo(r.Memory, 0x8000);
        second.CopyTo(r.Memory, 0x9000);
        Encoding.ASCII.GetBytes($"Local:first.xml;8000;{first.Length:X}").CopyTo(r.Memory, (int)GvbsAddr.FirstUrl);
        Encoding.ASCII.GetBytes($"Local:second.xml;9000;{second.Length:X}").CopyTo(r.Memory, (int)GvbsAddr.SecondUrl);
        await using var dev = await GevDevice.OpenAsync(r.EndPoint, new GevDeviceOpt
        {
            GvcpTimeoutMs = 300,
            GvcpRetries = 1,
            MaxPendingAckWaitMs = 200,
            // 하트비트는 시험 동안 돌지 않게 멀리 둔다 — 멈춘 요청 뒤에 줄을 서서 제어권을 흔들지 않게.
            HeartbeatTimeoutMs = 120_000,
            HeartbeatPeriodMs = 60_000,
        }, Ct);
        r.PendingAckStallAddr = 0x8000;

        var doc = await dev.GetXmlAsync(Ct);

        Assert.Equal("<Second/>", doc.Xml);
        Assert.Equal("second.xml", doc.FileName);
        Assert.Equal(1, r.CountOfReg(GvcpConst.ReadMemCmd, 0x8000));   // PENDING_ACK 를 받은 읽기는 다시 보내지 않았다
        Assert.Equal(1, dev.Gvcp.PendingAckCount);                      // 멈춘 것이 무응답이 아니라 PENDING_ACK 였다
    }

    [Fact]
    public async Task APendingAckExpiredUrlRegisterReadFallsBackToTheSecondUrl()
    {
        // URL 레지스터 읽기에서도 같다 — 장치가 답했으면 상실이 아니고, 다른 URL 은 끝날 수 있다.
        var port = PortWithTwoLocalUrls();
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.FirstUrl) throw PendingAckExpired();
        };

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal("second.xml", doc.FileName);
    }

    [Fact]
    public async Task TimeoutsFromALiveDeviceOnBothUrlsStayWrapped()
    {
        // 두 URL 이 모두 "장치가 답한" 시한 초과로 실패했다 — 형은 같지만 맨 GevTimeoutException 으로 내면 호출자는
        // 장치 상실로 읽고 멀쩡한 장치를 다시 연결한다. 모은 GevException 안에 실어야 한다.
        var port = PortWithTwoLocalUrls();
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.FirstUrl || addr == GvbsAddr.SecondUrl) throw PendingAckExpired();
        };

        var ex = await Assert.ThrowsAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.IsType<GevTimeoutException>(ex.InnerException);
        Assert.Contains("First URL", ex.Message);
        Assert.Contains("Second URL", ex.Message);
        Assert.Equal(1, port.Reads.Count(r => r.Addr == GvbsAddr.SecondUrl));   // 상실이 아니니 둘째도 시도했다
    }

    // ---- ExtractXml ----

    [Fact]
    public void ExtractXmlZipWithoutXmlEntryThrows()
    {
        var zip = MakeZip(("readme.txt", Encoding.ASCII.GetBytes("no xml here")), ("data.bin", new byte[] { 1, 2, 3 }));

        var ex = Assert.Throws<GevException>(() => GevXmlLoader.ExtractXml(zip, "cam.zip"));

        Assert.Contains("no .xml entry", ex.Message);
        Assert.Contains("readme.txt", ex.Message);
    }

    [Fact]
    public void ExtractXmlCorruptZipThrows()
    {
        var bytes = Encoding.ASCII.GetBytes("this is definitely not a zip archive, just text padded out");

        var ex = Assert.Throws<GevException>(() => GevXmlLoader.ExtractXml(bytes, "cam.zip"));

        Assert.Contains("cam.zip", ex.Message);
    }

    [Fact]
    public void ExtractXmlDetectsZipBySignatureWhenNameDoesNotSayZip()
    {
        var zip = MakeZip(("cam.xml", Encoding.UTF8.GetBytes("<Root/>")));

        Assert.Equal("<Root/>", GevXmlLoader.ExtractXml(zip, "cam.bin"));
    }

    [Fact]
    public void ExtractXmlRejectsZipEntryDeclaredAboveTheLimit()
    {
        var xml = "<A>" + new string('x', 2000) + "</A>";
        var zip = MakeZip(("cam.xml", Encoding.UTF8.GetBytes(xml)));

        var ex = Assert.Throws<GevException>(() => GevXmlLoader.ExtractXml(zip, "cam.zip", 1024));

        Assert.Contains("limit", ex.Message);
        Assert.Contains("cam.xml", ex.Message);
        Assert.Equal(xml, GevXmlLoader.ExtractXml(zip, "cam.zip", xml.Length));
    }

    [Fact]
    public void CappedReadStreamStopsAtTheLimitRegardlessOfWhatTheSourceDeclares()
    {
        var data = new byte[3000];
        using (var exact = new CappedReadStream(new MemoryStream(data), 3000, "entry"))
        {
            var ms = new MemoryStream();
            exact.CopyTo(ms);
            Assert.Equal(3000, ms.Length);
        }

        using var capped = new CappedReadStream(new MemoryStream(data), 2999, "entry");
        var buf = new byte[1000];
        Assert.Equal(1000, capped.Read(buf, 0, buf.Length));
        Assert.Equal(1000, capped.Read(buf, 0, buf.Length));
        var ex = Assert.Throws<GevException>(() => capped.Read(buf, 0, buf.Length));
        Assert.Contains("limit", ex.Message);
        Assert.Contains("entry", ex.Message);
    }

#if !NETFRAMEWORK
    [Fact]
    public void CappedReadStreamCountsWhatTheSpanPathReads()
    {
        var inner = new OverloadRecordingStream(new byte[3000]);
        using var capped = new CappedReadStream(inner, 2000, "entry");
        var buf = new byte[1000];

        Assert.Equal(1000, capped.Read(buf.AsSpan()));
        Assert.Equal(1000, capped.Read(buf.AsSpan()));
        var ex = Assert.Throws<GevException>(() => capped.Read(buf.AsSpan()));

        Assert.Contains("limit", ex.Message);
        // span 읽기가 내부 스트림까지 그대로 내려갔다 — 기반 클래스가 배열을 빌려 우회하지 않았다.
        Assert.Equal(3, inner.SpanReads);
        Assert.Equal(0, inner.ArrayReads);
    }
#else
    [Fact]
    public void CappedReadStreamEnforcesTheCapOnTheArrayPath()
    {
        // ns2.0 자산에는 Read(Span) 이 없어 span 경로 자체가 없다. 한도는 배열 경로에서 그대로 지켜져야 한다.
        var inner = new OverloadRecordingStream(new byte[3000]);
        using var capped = new CappedReadStream(inner, 2000, "entry");
        var buf = new byte[1000];

        Assert.Equal(1000, capped.Read(buf, 0, buf.Length));
        Assert.Equal(1000, capped.Read(buf, 0, buf.Length));
        var ex = Assert.Throws<GevException>(() => capped.Read(buf, 0, buf.Length));

        Assert.Contains("limit", ex.Message);
        Assert.Equal(3, inner.ArrayReads);
    }
#endif

    [Fact]
    public void ExtractXmlPlainPassthroughTrimsAndStripsBom()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("  \r\n<Root/>\r\n\0\0")).ToArray();

        Assert.Equal("<Root/>", GevXmlLoader.ExtractXml(bytes, "cam.xml"));
    }

    [Fact]
    public void ExtractXmlUtf16WithBomIsDecoded()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<Root/>")).ToArray();

        Assert.Equal("<Root/>", GevXmlLoader.ExtractXml(bytes, "cam.xml"));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0, 0, 0, 0 })]
    [InlineData(new byte[] { (byte)'h', (byte)'i' })]
    public void ExtractXmlRejectsContentNotStartingWithAngleBracket(byte[] bytes)
    {
        var ex = Assert.Throws<GevException>(() => GevXmlLoader.ExtractXml(bytes, "cam.xml"));
        Assert.Contains("does not look like XML", ex.Message);
    }

    // ---- 캐시 ----

    [Fact]
    public void CacheFileNameIsSanitizedAndStable()
    {
        Assert.Equal("Acme_Vision_Cam_2000_1.2.3_cam.zip", GevXmlLoader.CacheFileName("Acme Vision", "Cam/2000", "1.2.3", "cam.zip"));
        Assert.Equal("A_B_C_cam.xml", GevXmlLoader.CacheFileName("A", "B", "C", "cam.xml"));
        Assert.Equal("A_B_C_cam", GevXmlLoader.CacheFileName("A", "B", "C", "cam"));
        Assert.Equal("unknown_B_C_x.xml", GevXmlLoader.CacheFileName("", "B", "C", "x.xml"));

        var name = GevXmlLoader.CacheFileName("<>:\"/\\|?*", "M\tM", "V\nV", "c a m.zip");
        Assert.Equal("_________" + "_M_M_V_V_c_a_m.zip", name);
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), c => name.IndexOf(c) >= 0);
    }

    [Fact]
    public void CacheFileNameKeepsZipAndXmlSourcesApart()
    {
        var zip = GevXmlLoader.CacheFileName("A", "B", "C", "cam.zip");
        var xml = GevXmlLoader.CacheFileName("A", "B", "C", "cam.xml");

        Assert.NotEqual(zip, xml);
        Assert.EndsWith("cam.zip", zip);
        Assert.EndsWith("cam.xml", xml);
    }

    [Fact]
    public async Task ZipAndXmlUrlsOfOneDeviceDoNotShareACacheEntry()
    {
        using var tmp = new TempDir();
        var port = new FakeMemPort("V", "M", "1");
        var zip = MakeZip(("cam.xml", Encoding.UTF8.GetBytes("<FromZip/>")));
        var plain = Encoding.UTF8.GetBytes("<FromPlain/>");
        port.AddRegion(XmlAddr, Pad(zip, 16));
        port.AddRegion(XmlAddr + 0x10000, Pad(plain, 16));

        port.SetFirstUrl($"Local:cam.zip;{XmlAddr:X};{zip.Length:X}");
        var doc1 = await GevXmlLoader.LoadAsync(port, tmp.Path, Ct);
        port.SetFirstUrl($"Local:cam.xml;{XmlAddr + 0x10000:X};{plain.Length:X}");
        var doc2 = await GevXmlLoader.LoadAsync(port, tmp.Path, Ct);

        Assert.Equal("<FromZip/>", doc1.Xml);
        Assert.Equal("<FromPlain/>", doc2.Xml);
        Assert.Equal(2, Directory.GetFiles(tmp.Path).Length);
    }

    [Fact]
    public async Task CacheMissWritesFileAndHitSkipsDeviceXmlRead()
    {
        using var tmp = new TempDir();
        var fixture = FixtureBytes();
        var zip = MakeZip(("XmlLoaderMinimal.xml", fixture));
        var port = new FakeMemPort("Acme Vision", "Cam/2000", "1.2.3");
        port.AddRegion(XmlAddr, Pad(zip, 16));
        port.SetFirstUrl($"Local:cam.zip;{XmlAddr:X};{zip.Length:X}");

        var doc1 = await GevXmlLoader.LoadAsync(port, tmp.Path, Ct);

        var expectedPath = Path.Combine(tmp.Path, "Acme_Vision_Cam_2000_1.2.3_cam.zip");
        Assert.True(File.Exists(expectedPath), "cache file was not written");
        Assert.Equal(doc1.Xml, File.ReadAllText(expectedPath, Encoding.UTF8));
        Assert.True(port.ReadCountAtOrAbove(XmlAddr) > 0);
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));

        port.Reads.Clear();
        var doc2 = await GevXmlLoader.LoadAsync(port, tmp.Path, Ct);

        Assert.Equal(doc1, doc2);
        Assert.Equal(FixtureText(), doc2.Xml);
        Assert.Equal(0, port.ReadCountAtOrAbove(XmlAddr));
        Assert.Contains(port.Reads, r => r.Addr == GvbsAddr.FirstUrl);
    }

    [Fact]
    public async Task CorruptCacheFileIsIgnoredAndRewritten()
    {
        using var tmp = new TempDir();
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        var port = new FakeMemPort("V", "M", "1");
        port.AddRegion(XmlAddr, Pad(bytes, 16));
        port.SetFirstUrl($"Local:cam.xml;{XmlAddr:X};{bytes.Length:X}");
        Directory.CreateDirectory(tmp.Path);
        var cachePath = Path.Combine(tmp.Path, GevXmlLoader.CacheFileName("V", "M", "1", "cam.xml"));
        File.WriteAllText(cachePath, "garbage, not xml");

        var doc = await GevXmlLoader.LoadAsync(port, tmp.Path, Ct);

        Assert.Equal("<Root/>", doc.Xml);
        Assert.True(port.ReadCountAtOrAbove(XmlAddr) > 0);
        Assert.Equal("<Root/>", File.ReadAllText(cachePath, Encoding.UTF8));
    }

    [Fact]
    public void CacheRewriteNeverLeavesThePathMissing()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "cache.xml");
        var name = Path.GetFileName(path);
        File.WriteAllText(path, "<Seed/>");

        // 같은 캐시를 지켜보는 다른 프로세스 역 — 디렉터리 나열이라 파일을 열지 않는다(파일을 열어 보면
        // 공유 위반을 "없음" 으로 잘못 센다).
        //
        // 한 번에 갈아끼우는 Move 를 쓸 수 있는 자산에서는 캐시 이름이 비는 순간이 아예 없어야 한다 —
        // 지웠다 옮기는 방식으로 바꾸면 여기서 붙잡힌다. 그 Move 가 없는 런타임(.NET Framework 에서 도는
        // netstandard2.0 자산)은 File.Replace 로 내려가고, 거기에는 이름이 잠깐 비는 구간이 있어 없앨 수 없다.
        // 그때도 지켜져야 하는 것은 따로 있다: 읽는 쪽은 "없음" 아니면 완성된 내용을 보고, 반쯤 쓰인 것은 보지 않는다.
        var missing = 0;
        var partial = new List<string>();
        var stop = false;
        var watcher = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (Directory.GetFiles(dir.Path, name).Length == 0)
                {
                    missing++;
                    continue;
                }
                // 있으면 내용을 훔쳐본다 — 쓰는 쪽과 겹쳐 열지 못하는 것은 표본이 아니므로 넘긴다.
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    var seen = sr.ReadToEnd();
                    if (seen.Length > 0 && !seen.EndsWith("/>", StringComparison.Ordinal)) partial.Add(seen);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                // 다른 테스트와 코어를 다투지 않게 매번 양보한다 — 실측상 표본이 줄지 않아 검출력은 그대로다.
                Thread.Yield();
            }
        })
        {
            IsBackground = true,
        };
        watcher.Start();

        for (var i = 0; i < 40; i++) GevXmlLoader.WriteCache(path, $"<Rewrite{i}/>");

        Volatile.Write(ref stop, true);
        watcher.Join();

        // 어느 자산에서든: 반쯤 쓰인 내용은 결코 보이지 않고, 끝난 뒤에는 완성된 캐시 하나만 남는다.
        Assert.Empty(partial);
        Assert.StartsWith("<Rewrite", File.ReadAllText(path, Encoding.UTF8), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        // 한 번에 갈아끼우는 Move 를 가진 자산에서만: 이름이 비는 순간도 없다.
        if (GevXmlLoader.OverwriteMove is not null)
            Assert.Equal(0, missing);
    }

    [Fact]
    public void SwapByReplaceLeavesTheOldCacheInPlaceWhenTheNewContentIsGone()
    {
        // 덮어쓰기 Move 가 없는 자산이 타는 경로를 직접 두드린다 — 이 스위트가 도는 자산은 WriteCache 로는
        // 그 경로를 밟지 않는다(그쪽은 한 번에 갈아끼우는 Move 를 쓴다).
        // 새 내용이 사라진 채로 교체를 부르면 실패해야 하고, 그때 옛 캐시는 자리에 남아 있어야 한다.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "cache.xml");
        File.WriteAllText(path, "<Old/>");

        Assert.ThrowsAny<IOException>(() => GevXmlLoader.SwapByReplace(path + ".gone.tmp", path));

        Assert.True(File.Exists(path));
        Assert.Equal("<Old/>", File.ReadAllText(path, Encoding.UTF8));
    }


    [Fact]
    public void SwapByReplacePutsTheFileInPlaceWithAndWithoutAnExistingTarget()
    {
        // 덮어쓰기 Move 가 없는 자산이 타는 경로 — 이 스위트는 net8.0 으로 돌아도 그 경로를 그대로 밟는다.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "cache.xml");

        var first = path + ".1.tmp";
        File.WriteAllText(first, "first");
        GevXmlLoader.SwapByReplace(first, path);

        Assert.Equal("first", File.ReadAllText(path));
        Assert.False(File.Exists(first));

        var second = path + ".2.tmp";
        File.WriteAllText(second, "second");
        GevXmlLoader.SwapByReplace(second, path);

        Assert.Equal("second", File.ReadAllText(path));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void OverwriteMoveIsResolvedOnRuntimesThatCarryIt()
    {
        // netstandard 자산은 3인자 Move 를 컴파일로 불러 쓰지 못하지만, 그 자산을 실어 돌리는 런타임에는 대개 있다
        // (.NET Core 3.0 이상). 지연 조회가 그것을 실제로 찾아내는지, 찾은 것이 동작하는지를 본다.
        var move = GevXmlLoader.OverwriteMove;
#if NETFRAMEWORK
        // .NET Framework 에는 이 오버로드가 아예 없다 — 조회는 null 을 내야 하고(예외로 죽지 않아야 하고),
        // 캐시 교체는 File.Replace 경로로 내려간다. 그 경로는 SwapByReplace* 테스트들이 따로 본다.
        Assert.Null(move);
        return;
#else
        Assert.NotNull(move);

        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "cache.xml");
        File.WriteAllText(path, "<Old/>");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, "<New/>");

        move!(tmp, path, true);

        Assert.Equal("<New/>", File.ReadAllText(path, Encoding.UTF8));
        Assert.False(File.Exists(tmp));
#endif
    }

    [Fact]
    public void SwapByMoveAsidePutsTheNewContentInPlaceAndLeavesNothingBehind()
    {
        // 교체도 덮어쓰기 Move 도 안 되는 자리의 마지막 수단 — 비켜 둔 사본이 남아 돌아다니면 안 된다.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "cache.xml");
        File.WriteAllText(path, "<Old/>");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, "<New/>");

        GevXmlLoader.SwapByMoveAside(tmp, path);

        Assert.Equal("<New/>", File.ReadAllText(path, Encoding.UTF8));
        Assert.False(File.Exists(tmp));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.old"));
    }

    [Fact]
    public void SwapByMoveAsidePutsTheOldCacheBackWhenTheNewContentIsGone()
    {
        // 비켜 두는 데까지 성공하고 새 파일 넣기에서 실패하는 경우. 예외는 그대로 올라가되,
        // 옇 캐시는 백업 이름에 버려지지 않고 제 자리로 돌아와 있어야 한다.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "cache.xml");
        File.WriteAllText(path, "<Old/>");

        Assert.ThrowsAny<IOException>(() => GevXmlLoader.SwapByMoveAside(path + ".gone.tmp", path));

        Assert.True(File.Exists(path));
        Assert.Equal("<Old/>", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.old"));
    }

    [Fact]
    public void SwapByReplaceFallsBackToMovingAsideWhenReplaceIsRefused()
    {
        // 교체가 거부되는 자리를 재현한다: 대상이 읽기 전용이면 File.Replace 는 UnauthorizedAccessException 으로
        // 거부하지만 이름 바꾸기는 그대로 된다(실측). 거부 신호를 예외 종류 하나로만 가리면 마지막 수단이
        // 아예 닿지 않아 캐시가 다시는 갱신되지 않는다 — 그러면 이 테스트가 예외로 넘어진다.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "cache.xml");
        File.WriteAllText(path, "<Old/>");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, "<New/>");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        GevXmlLoader.SwapByReplace(tmp, path);

        Assert.Equal("<New/>", File.ReadAllText(path, Encoding.UTF8));
        Assert.False(File.Exists(tmp));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.old"));
    }

    [Fact]
    public void SwapByReplaceKeepsTheOldCacheWhenTheTargetIsHeldOpen()
    {
        // 열려 있는 대상을 교체할 수 있는지는 OS 가 정한다 — 파일 잠금은 이식 가능한 개념이 아니다.
        // 윈도우는 공유 규칙이 강제라 읽는 손이 있으면 교체가 거부되고, POSIX 는 이름만 바꾸므로 그냥 성공한다
        // (열어 둔 쪽은 옛 내용을 계속 본다). 어느 쪽이든 이 경로가 약속하는 것은 하나다:
        // **옛 캐시가 사라진 채로 끝나지 않는다.** 거부당하면 옛 내용이 그대로, 성공하면 새 내용이 그 자리에 있다.
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = Path.Combine(dir.Path, "cache.xml");
        File.WriteAllText(path, "<Old/>");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, "<New/>");

        var refused = false;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                GevXmlLoader.SwapByReplace(tmp, path);
            }
            catch (Exception)
            {
                refused = true;
            }

            // 거부당했으면 옛 내용이, 통과했으면 새 내용이 자리에 있다. 어느 쪽도 "없음" 은 아니다.
            Assert.Equal(refused ? "<Old/>" : "<New/>", File.ReadAllText(path, Encoding.UTF8));
            Assert.Equal(refused, File.Exists(tmp));
        }

        // 손을 놓으면 다음 쓰기가 그대로 들어간다(거부됐던 자산에서는 여기서 처음 들어간다).
        if (refused) GevXmlLoader.SwapByReplace(tmp, path);

        Assert.Equal("<New/>", File.ReadAllText(path, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.old"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public async Task NoCacheDirMeansNothingIsWritten()
    {
        using var tmp = new TempDir();
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        var port = PortWithLocal(bytes, "cam.xml");

        await GevXmlLoader.LoadAsync(port, null, Ct);
        await GevXmlLoader.LoadAsync(port, "", Ct);

        Assert.False(Directory.Exists(tmp.Path));
        Assert.DoesNotContain(port.Reads, r => r.Addr == GvbsAddr.ManufacturerName);
    }

    [Fact]
    public async Task DeviceLossWhileReadingTheCacheKeyIsRethrownBeforeTheXmlIsRead()
    {
        // 캐시 키(GVBS 의 제조사·모델·버전)를 읽다가 장치를 잃었으면 그 자리에서 멈춘다. 삼키고 캐시 없이 넘어가면
        // XML 영역을 읽느라 재시도 예산을 한 번 더 다 쓰고 나서야 같은 상실을 알게 된다.
        using var tmp = new TempDir();
        var port = PortWithTwoLocalUrls();
        var timeouts = 0;
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.ManufacturerName || addr >= XmlAddr)
            {
                timeouts++;
                throw new GevTimeoutException("READMEM got no reply");
            }
        };

        var ex = await Assert.ThrowsAsync<GevTimeoutException>(() => GevXmlLoader.LoadAsync(port, tmp.Path, Ct));

        Assert.Equal("READMEM got no reply", ex.Message);
        Assert.Equal(1, timeouts);   // 무응답을 한 번만 겪었다 — 재시도 예산을 두 번 쓰지 않았다
        Assert.Equal(new ulong[] { GvbsAddr.FirstUrl, GvbsAddr.ManufacturerName }, port.Reads.Select(r => r.Addr));
        Assert.False(Directory.Exists(tmp.Path));
    }

    [Fact]
    public async Task DeviceLossWhileReadingTheCacheKeyStopsAnHttpUrlToo()
    {
        // http URL 이어도 캐시 키는 장치에서 읽는다 — 거기서 난 무응답은 서버가 아니라 장치의 상실이다.
        // URL 종류로 시한 초과를 가르면 이 상실이 "서버 쪽 사정" 으로 읽혀 무시되거나 다른 URL 로 넘어간다.
        using var tmp = new TempDir();
        var hits = 0;
        using var server = new LoopbackHttpServer(_ =>
        {
            Interlocked.Increment(ref hits);
            return (200, Encoding.ASCII.GetBytes("<Root/>"));
        });
        var port = new FakeMemPort();
        port.SetFirstUrl(server.BaseUri + "cam.xml");
        port.OnRead = (addr, _) =>
        {
            if (addr == GvbsAddr.ManufacturerName) throw new GevTimeoutException("READMEM got no reply");
        };

        await Assert.ThrowsAsync<GevTimeoutException>(() => GevXmlLoader.LoadAsync(port, tmp.Path, Ct));

        Assert.Equal(0, Volatile.Read(ref hits));
        Assert.Equal(0, port.Reads.Count(r => r.Addr == GvbsAddr.SecondUrl));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACacheKeyReadFailureFromALiveDeviceStillLoadsWithoutTheCache(bool isPendingAckExpired)
    {
        // 대조군: 장치가 살아서 답한 실패(상태 오류, PENDING_ACK 연장 소진)는 상실이 아니다 — 캐시 없이 이어 가는 원래 동작 그대로다.
        using var tmp = new TempDir();
        var port = PortWithLocal(Encoding.UTF8.GetBytes("<Root/>"), "cam.xml");
        port.OnRead = (addr, _) =>
        {
            if (addr != GvbsAddr.ManufacturerName) return;
            if (isPendingAckExpired) throw PendingAckExpired();
            throw new GevStatusException("READMEM", GvcpConst.StatusAccessDenied);
        };

        var doc = await GevXmlLoader.LoadAsync(port, tmp.Path, Ct);

        Assert.Equal("<Root/>", doc.Xml);
        Assert.True(port.ReadCountAtOrAbove(XmlAddr) > 0);
        Assert.False(Directory.Exists(tmp.Path));   // 캐시 키가 없으니 쓰지도 않았다
    }

    // ---- File: ----

    [Fact]
    public async Task FileUrlReadsHostFile()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);
        var path = Path.Combine(tmp.Path, "cam.xml");
        File.WriteAllText(path, "<Root/>", new UTF8Encoding(false));
        var port = new FakeMemPort();
        // 드라이브 경로(C:/…)는 "File:///" 뒤에, 루트 경로(/tmp/…)는 "File://" 뒤에 붙어야 슬래시가 셋이 된다.
        var slashPath = path.Replace('\\', '/');
        port.SetFirstUrl((slashPath.StartsWith("/", StringComparison.Ordinal) ? "File://" : "File:///") + slashPath + "?SchemaVersion=1.0.0");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal("<Root/>", doc.Xml);
        Assert.Equal("cam.xml", doc.FileName);
        Assert.Equal("1.0.0", doc.SchemaVersion);
    }

    [Fact]
    public async Task FileUrlWithZipReadsFirstXmlEntry()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);
        var path = Path.Combine(tmp.Path, "cam.zip");
        File.WriteAllBytes(path, MakeZip(("cam.xml", Encoding.UTF8.GetBytes("<Root/>"))));
        var port = new FakeMemPort();
        port.SetFirstUrl("file:" + path);

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal("<Root/>", doc.Xml);
        Assert.Equal("cam.zip", doc.FileName);
    }

    [Fact]
    public async Task MissingFileThrowsGevException()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "missing.xml");
        var port = new FakeMemPort();
        port.SetFirstUrl("file:" + path);

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("missing.xml", ex.Message);
    }

    // ---- http ----

    [Fact]
    public async Task HttpUrlDownloadsZipFromLoopbackServer()
    {
        var fixture = FixtureBytes();
        var zip = MakeZip(("XmlLoaderMinimal.xml", fixture));
        using var server = new LoopbackHttpServer(path => path == "/xml/cam.zip" ? (200, zip) : (404, Array.Empty<byte>()));
        var port = new FakeMemPort();
        port.SetFirstUrl(server.BaseUri + "xml/cam.zip?SchemaVersion=1.1.0");

        var doc = await GevXmlLoader.LoadAsync(port, null, Ct);

        Assert.Equal(FixtureText(), doc.Xml);
        Assert.Equal("cam.zip", doc.FileName);
        Assert.Equal("1.1.0", doc.SchemaVersion);
    }

    [Fact]
    public async Task HttpNotFoundThrowsGevExceptionWithStatus()
    {
        using var server = new LoopbackHttpServer(_ => (404, Encoding.ASCII.GetBytes("nope")));
        var port = new FakeMemPort();
        port.SetFirstUrl(server.BaseUri + "cam.xml");

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task HttpDeclaredContentLengthAboveTheLimitIsRejectedWithoutBufferingTheBody()
    {
        using var server = new LoopbackHttpServer(_ => (200, Encoding.ASCII.GetBytes("<Root/>")))
        {
            DeclaredContentLength = (long)GevXmlLoader.MaxXmlBytes + 1,
        };
        var port = new FakeMemPort();
        port.SetFirstUrl(server.BaseUri + "cam.xml");

        var ex = await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadAsync(port, null, Ct));

        // 어느 자산에서든: 본문을 버퍼링하지 않고 거절하며, 시한 초과가 아니라 내용 오류로 알린다.
        Assert.IsNotType<GevTimeoutException>(ex);
        Assert.Contains("cam.xml", ex.Message, StringComparison.Ordinal);
#if !NETFRAMEWORK
        // 상한을 넘겨 끊겼다는 것을 런타임이 구분해 알려 주는 자산에서는 우리 말로 상한과 그 값까지 싣는다.
        // ns2.0 자산에는 그 구분이 없어 클라이언트가 낸 메시지가 그대로 실린다 — 거절한다는 사실은 같다.
        Assert.Contains("limit", ex.Message);
        Assert.Contains(GevXmlLoader.MaxXmlBytes.ToString(), ex.Message);
#endif
    }

    [Fact]
    public async Task CancellationDuringTheHttpDownloadIsCancellationNotTimeout()
    {
        using var gate = new ManualResetEventSlim(false);
        using var server = new LoopbackHttpServer(_ =>
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return (200, Encoding.ASCII.GetBytes("<Root/>"));
        });
        var port = new FakeMemPort();
        port.SetFirstUrl(server.BaseUri + "cam.xml");
        using var cts = new CancellationTokenSource();
        try
        {
            cts.CancelAfter(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GevXmlLoader.LoadAsync(port, null, cts.Token));
        }
        finally
        {
            gate.Set();
        }
    }

    // ---- LoadFromUrlAsync 직접 호출 ----

    [Fact]
    public async Task LoadFromUrlAsyncDoesNotFallBack()
    {
        var port = new FakeMemPort();
        var bytes = Encoding.UTF8.GetBytes("<Root/>");
        port.AddRegion(XmlAddr, Pad(bytes, 16));
        port.SetSecondUrl($"Local:second.xml;{XmlAddr:X};{bytes.Length:X}");
        var url = GevXmlUrl.Parse("Local:first.xml;F0000000;100");

        await Assert.ThrowsAnyAsync<GevException>(() => GevXmlLoader.LoadFromUrlAsync(port, url, null, Ct));

        var doc = await GevXmlLoader.LoadFromUrlAsync(port, GevXmlUrl.Parse($"Local:second.xml;{XmlAddr:X};{bytes.Length:X}"), null, Ct);
        Assert.Equal("<Root/>", doc.Xml);
        Assert.DoesNotContain(port.Reads, r => r.Addr == GvbsAddr.FirstUrl || r.Addr == GvbsAddr.SecondUrl);
    }

    [Fact]
    public void DocToStringDoesNotDumpTheXml()
    {
        var doc = new GevXmlDoc("<Root>" + new string('x', 500) + "</Root>", "Local:cam.xml;20000;10", "cam.xml", null);

        var s = doc.ToString();

        Assert.Contains("cam.xml", s);
        Assert.True(s.Length < 200, s);
    }
}
