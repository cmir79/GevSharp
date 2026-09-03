using System.Diagnostics;
using GevSharp.GenApi;
using GevSharp.Gvcp;
using GevSharp.Sim;

#pragma warning disable xUnit1051

namespace GevSharp.Tests.GenApi.Runtime;

/// <summary>
/// 루프백 시뮬레이터에 <see cref="GevDevice"/> 로 붙어 XML 을 받고 노드맵으로 피처를 읽고 쓴다 —
/// 레지스터 값은 시뮬레이터의 레지스터 맵에서 직접 확인한다(docs/sim-register-map.md).
/// </summary>
public class SimNodeMapTests
{
    // GVCP 타임아웃은 재시도 예산이지 기대 응답 시간이 아니다 — 시뮬레이터는 즉시 답하므로 정상 경로는 이 값과 무관하고,
    // 굶주린 러너에서 500 ms 를 넘긴 왕복을 무응답으로 오판해 같은 명령을 다시 보내는 일만 막는다.
    private static GevDeviceOpt FastOpt() => new() { GvcpTimeoutMs = 2000, GvcpRetries = 2, HeartbeatTimeoutMs = 10_000, HeartbeatPeriodMs = 500 };

    private sealed class Session : IAsyncDisposable
    {
        public SimDevice Sim { get; }
        public GevDevice Device { get; }
        public GenApiNodeMap Map { get; }

        private Session(SimDevice sim, GevDevice device, GenApiNodeMap map)
        {
            Sim = sim;
            Device = device;
            Map = map;
        }

        public static async Task<Session> OpenAsync(Action<SimDeviceOpt>? configure = null)
        {
            var opt = new SimDeviceOpt();
            configure?.Invoke(opt);
            var sim = new SimDevice(opt);
            sim.Start();
            GevDevice? device = null;
            try
            {
                device = await GevDevice.OpenAsync(sim.GvcpEndPoint, FastOpt());
                var map = await device.GetNodeMapAsync();
                return new Session(sim, device, map);
            }
            catch
            {
                if (device is not null) await device.DisposeAsync();
                sim.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Device.DisposeAsync();
            Sim.Dispose();
        }
    }

    /// <summary>조건이 설 때까지 기다린다. 시간 제한은 "멈춰 버린 시험을 끝낸다" 는 뜻뿐이라, 굶주린 러너를 재지 않도록 넉넉히 둔다.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task NodeMap_IsBuiltFromDeviceXmlAndCached()
    {
        await using var s = await Session.OpenAsync();

        Assert.Equal("SimCamera", s.Map.Info.ModelName);
        Assert.Equal("GevSharp", s.Map.Info.VendorName);
        Assert.Equal(6, s.Map.Root.Features.Count);
        Assert.Same(s.Map, await s.Device.GetNodeMapAsync());
        Assert.Same(s.Device, s.Map.GetNode<IPortNode>("Device").Port);
    }

    [Fact]
    public async Task DeviceStrings_ReadAndUserIdWrites()
    {
        await using var s = await Session.OpenAsync(o => o.SerialNumber = "SIM4242");

        Assert.Equal("SimCamera", await s.Map.GetString("DeviceModelName").GetAsync());
        Assert.Equal("GevSharp", await s.Map.GetString("DeviceVendorName").GetAsync());
        Assert.Equal("SIM4242", await s.Map.GetString("DeviceSerialNumber").GetAsync());
        Assert.Equal("", await s.Map.GetString("DeviceUserID").GetAsync());

        var userId = s.Map.GetString("DeviceUserID");
        await userId.SetAsync("bench7");
        Assert.Equal("bench7", s.Sim.Registers.ReadString(GvbsAddr.UserDefinedName, GvbsAddr.UserDefinedNameLen));
        Assert.Equal("bench7", await userId.GetAsync());
        Assert.Equal(16, await userId.GetMaxLengthAsync());

        var ex = await Assert.ThrowsAsync<GenApiException>(() => s.Map.GetString("DeviceModelName").SetAsync("x").AsTask());
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task ExposureTime_ConverterWritesRawTicks()
    {
        await using var s = await Session.OpenAsync();
        var exposure = s.Map.GetFloat("ExposureTime");

        Assert.Equal(10_000.0, await exposure.GetAsync());
        Assert.Equal(1_000_000_000, await s.Map.GetInteger("TimestampTickFrequency").GetAsync());

        await exposure.SetAsync(2000.0);
        Assert.Equal(2_000_000u, s.Sim.Registers.ReadU32(SimFeatureAddr.ExposureTimeRaw));
        Assert.Equal(2000.0, await exposure.GetAsync());
        Assert.Equal(2_000_000, await s.Map.GetInteger("ExposureTimeRaw").GetAsync());
        Assert.Equal(1.0, await exposure.GetMinAsync());
        Assert.Equal(2_000_000.0, await exposure.GetMaxAsync());
        Assert.Equal("us", exposure.Unit);

        await Assert.ThrowsAsync<GenApiException>(() => exposure.SetAsync(0.1).AsTask());
        Assert.Equal(2_000_000u, s.Sim.Registers.ReadU32(SimFeatureAddr.ExposureTimeRaw));
    }

    [Fact]
    public async Task Gain_ViaSelector_AddressesThreeIndependentChannels()
    {
        await using var s = await Session.OpenAsync();
        var selector = s.Map.GetEnumeration("GainSelector");
        var gain = s.Map.GetFloat("Gain");
        var channels = new[] { "AnalogAll", "DigitalAll", "DigitalRed" };

        for (var i = 0; i < channels.Length; i++)
        {
            await selector.SetAsync(channels[i]);
            await gain.SetAsync(1.5 + i);
        }
        for (var i = 0; i < channels.Length; i++)
            Assert.Equal(15u + 10u * (uint)i, s.Sim.Registers.ReadU32(SimFeatureAddr.GainRaw0 + 4u * (uint)i));

        for (var i = channels.Length - 1; i >= 0; i--)
        {
            await selector.SetAsync(channels[i]);
            Assert.Equal(channels[i], await selector.GetAsync());
            Assert.Equal(1.5 + i, await gain.GetAsync());
            Assert.Equal(15 + 10 * i, await s.Map.GetInteger("GainRaw").GetAsync());
        }
        Assert.Equal(0.0, await gain.GetMinAsync());
        Assert.Equal(102.3, await gain.GetMaxAsync(), 10);
    }

    [Fact]
    public async Task PixelFormat_EnumerationSetAndGet()
    {
        await using var s = await Session.OpenAsync();
        var pf = s.Map.GetEnumeration("PixelFormat");

        Assert.Equal("Mono8", await pf.GetAsync());
        Assert.Equal(6, pf.Entries.Count);
        Assert.Equal(6, (await pf.GetAvailableEntriesAsync()).Count);

        await pf.SetAsync("Mono12");
        Assert.Equal(0x01100005u, s.Sim.Registers.ReadU32(SimFeatureAddr.PixelFormat));
        Assert.Equal("Mono12", await pf.GetAsync());
        Assert.Equal(0x01100005, await pf.GetIntValueAsync());

        await Assert.ThrowsAsync<GenApiException>(() => pf.SetAsync("Mono99").AsTask());
        Assert.Equal(0x01100005u, s.Sim.Registers.ReadU32(SimFeatureAddr.PixelFormat));
    }

    [Fact]
    public async Task WidthHeightPixelFormat_DrivePayloadSizeSwissKnife()
    {
        await using var s = await Session.OpenAsync();
        var payload = s.Map.GetInteger("PayloadSize");
        var width = s.Map.GetInteger("Width");
        var height = s.Map.GetInteger("Height");

        Assert.Equal(640L * 480, await payload.GetAsync());
        Assert.Equal(4096, await width.GetMaxAsync());
        Assert.Equal(8, await width.GetMinAsync());
        Assert.Equal(4, await width.GetIncAsync());

        await width.SetAsync(320);
        await height.SetAsync(240);
        Assert.Equal(320u, s.Sim.Registers.ReadU32(SimFeatureAddr.Width));
        Assert.Equal(240u, s.Sim.Registers.ReadU32(SimFeatureAddr.Height));
        Assert.Equal(320L * 240, await payload.GetAsync());

        await s.Map.GetEnumeration("PixelFormat").SetAsync("Mono16");
        Assert.Equal(320L * 240 * 2, await payload.GetAsync());
        await s.Map.GetEnumeration("PixelFormat").SetAsync("RGB8");
        Assert.Equal(320L * 240 * 3, await payload.GetAsync());

        var ex = await Assert.ThrowsAsync<GenApiException>(() => width.SetAsync(322).AsTask());
        Assert.Contains("increment", ex.Message);
        await Assert.ThrowsAsync<GenApiException>(() => width.SetAsync(5000).AsTask());
        Assert.Equal(320u, s.Sim.Registers.ReadU32(SimFeatureAddr.Width));
    }

    [Fact]
    public async Task TriggerMode_On_MakesTriggerSourceAvailable()
    {
        await using var s = await Session.OpenAsync();
        var mode = s.Map.GetEnumeration("TriggerMode");
        var source = s.Map.GetEnumeration("TriggerSource");
        var software = s.Map.GetCommand("TriggerSoftware");

        Assert.Equal("Off", await mode.GetAsync());
        Assert.False(await source.IsAvailableAsync());
        Assert.Equal(AccessMode.NotAvailable, await source.GetAccessModeAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => source.SetAsync("Line0").AsTask());
        Assert.Contains("not available", ex.Message);
        Assert.False(await software.IsAvailableAsync());

        await mode.SetAsync("On");
        Assert.Equal(0x01u, s.Sim.Registers.ReadU32(SimFeatureAddr.TriggerControl));
        Assert.True(await source.IsAvailableAsync());
        Assert.Equal("Software", await source.GetAsync());
        Assert.True(await software.IsAvailableAsync());

        await source.SetAsync("Line1");
        Assert.Equal(0x21u, s.Sim.Registers.ReadU32(SimFeatureAddr.TriggerControl));
        Assert.Equal("Line1", await source.GetAsync());
        Assert.Equal("On", await mode.GetAsync());
        Assert.False(await software.IsAvailableAsync());

        await mode.SetAsync("Off");
        Assert.Equal(0x20u, s.Sim.Registers.ReadU32(SimFeatureAddr.TriggerControl));   // 소스 비트는 보존
        Assert.False(await source.IsAvailableAsync());
    }

    [Fact]
    public async Task AcquisitionStart_LocksWidthUntilStop()
    {
        await using var s = await Session.OpenAsync();
        var width = s.Map.GetInteger("Width");
        var start = s.Map.GetCommand("AcquisitionStart");
        var stop = s.Map.GetCommand("AcquisitionStop");

        Assert.False(await width.IsLockedAsync());
        await width.SetAsync(64);

        // 전송 계층을 잠그기 전에는 획득 커맨드가 잠긴 WO — 즉 접근 불가라 실행할 수 없다(실장치와 같은 규약).
        Assert.Equal(AccessMode.NotAvailable, await start.GetAccessModeAsync());
        var locked = await Assert.ThrowsAsync<GenApiException>(() => start.ExecuteAsync().AsTask());
        Assert.Contains("locked", locked.Message);

        Assert.True(await s.Device.SetTlParamsLockedAsync(true));
        Assert.Equal(AccessMode.WriteOnly, await start.GetAccessModeAsync());

        await start.ExecuteAsync();
        await WaitUntilAsync(() => s.Sim.IsAcquiring);
        Assert.True(await start.IsDoneAsync());                     // 자기 소거 비트는 0 으로 돌아왔다
        Assert.True(await width.IsLockedAsync());
        Assert.Equal(AccessMode.ReadOnly, await width.GetAccessModeAsync());
        Assert.Equal(64, await width.GetAsync());
        var ex = await Assert.ThrowsAsync<GenApiException>(() => width.SetAsync(128).AsTask());
        Assert.Contains("locked", ex.Message);
        Assert.Equal(64u, s.Sim.Registers.ReadU32(SimFeatureAddr.Width));
        Assert.True(await s.Map.GetEnumeration("PixelFormat").IsLockedAsync());

        await stop.ExecuteAsync();
        await WaitUntilAsync(() => !s.Sim.IsAcquiring);
        Assert.True(await stop.IsDoneAsync());
        Assert.True(await s.Device.SetTlParamsLockedAsync(false));
        Assert.Equal(AccessMode.NotAvailable, await start.GetAccessModeAsync());   // 다시 잠긴다
        Assert.False(await width.IsLockedAsync());
        await width.SetAsync(128);
        Assert.Equal(128u, s.Sim.Registers.ReadU32(SimFeatureAddr.Width));
    }

    [Fact]
    public async Task GevSCPSPacketSize_MaskedWritePreservesFlagBits()
    {
        await using var s = await Session.OpenAsync();
        var scpsAddr = GvbsAddr.StreamChannel(0, GvbsAddr.ScpsOffset);
        s.Sim.Registers.WriteU32(scpsAddr, 0x4000_05DC);              // do-not-fragment + 1500
        var size = s.Map.GetInteger("GevSCPSPacketSize");

        Assert.Equal(1500, await size.GetAsync());
        await size.SetAsync(8000);
        Assert.Equal(0x4000_1F40u, s.Sim.Registers.ReadU32(scpsAddr));
        Assert.Equal(8000, await size.GetAsync());
        await Assert.ThrowsAsync<GenApiException>(() => size.SetAsync(70000).AsTask());
        Assert.Equal(0x4000_1F40u, s.Sim.Registers.ReadU32(scpsAddr));
    }

    [Fact]
    public async Task TimestampLatch_CommandInvalidatesLatchedValue()
    {
        await using var s = await Session.OpenAsync();
        var latched = s.Map.GetInteger("TimestampLatchValue");
        var latch = s.Map.GetCommand("TimestampLatch");

        Assert.Equal(0, await latched.GetAsync());
        await latch.ExecuteAsync();
        var first = await latched.GetAsync();
        Assert.True(first > 0, "latch must capture a running timestamp");
        await Task.Delay(5);
        await latch.ExecuteAsync();
        Assert.True(await latched.GetAsync() > first);
    }

    [Fact]
    public async Task BooleanAndFloatFeatures_RoundTripThroughDevice()
    {
        await using var s = await Session.OpenAsync();
        var reverse = s.Map.GetBoolean("ReverseX");
        var rate = s.Map.GetFloat("AcquisitionFrameRate");

        Assert.False(await reverse.GetAsync());
        await reverse.SetAsync(true);
        Assert.Equal(1u, s.Sim.Registers.ReadU32(SimFeatureAddr.ReverseX));
        Assert.True(await reverse.GetAsync());

        Assert.Equal(30.0, await rate.GetAsync());
        await rate.SetAsync(12.5);
        Assert.Equal(12.5f, s.Sim.Registers.ReadF32(SimFeatureAddr.AcquisitionFrameRate));
        Assert.Equal(12.5, await rate.GetAsync());
        await Assert.ThrowsAsync<GenApiException>(() => rate.SetAsync(0.5).AsTask());

        var count = s.Map.GetInteger("AcquisitionFrameCount");
        Assert.False(await count.IsAvailableAsync());
        await s.Map.GetEnumeration("AcquisitionMode").SetAsync("MultiFrame");
        Assert.True(await count.IsAvailableAsync());
        await count.SetAsync(3);
        Assert.Equal(3u, s.Sim.Registers.ReadU32(SimFeatureAddr.AcquisitionFrameCount));
    }

    [Fact]
    public async Task UserSetLoad_RestoresDefaultsAndMapInvalidateAllRefreshes()
    {
        await using var s = await Session.OpenAsync();
        var width = s.Map.GetInteger("Width");

        await width.SetAsync(64);
        await s.Map.GetEnumeration("UserSetSelector").SetAsync("UserSet1");
        await s.Map.GetCommand("UserSetLoad").ExecuteAsync();
        await WaitUntilAsync(() => s.Sim.Registers.ReadU32(SimFeatureAddr.Width) == 640);

        // 장치가 스스로 바꾼 값 — UserSetLoad 는 Width 를 무효화하지 않으므로 캐시가 낡았을 수 있다; InvalidateAll 로 다시 읽는다
        s.Map.InvalidateAll();
        Assert.Equal(640, await width.GetAsync());
    }
}
