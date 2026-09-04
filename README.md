# GevSharp

Vendor-free, pure managed C# library for industrial GigE cameras:
GVCP device control, GVSP streaming with packet resend, and GenICam GenApi XML feature access.

- **No native dependencies.** No vendor SDK, no GenTL producer (`.cti`), no filter driver — a tuned UDP
  socket is all it needs. Runs on Windows, Linux and macOS.
- **Full-rate 1 GbE streaming.** Dedicated receive thread, large socket buffers, jumbo-frame packet size
  negotiation, offset-based frame reassembly, and GVSP packet resend to recover drops.
- **Complete GenApi node map.** Category / Group recursion, Integer / Float / String / Boolean /
  Enumeration / Command / Register nodes, `pValue` / `pAddress` / `pIndex` indirection, selectors,
  availability / lock / implementation guards, Converter and SwissKnife formulas — evaluated by an
  in-house formula engine with no third-party dependency.
- **Explicit buffer ownership.** Frames are leased from a pool and returned on `Dispose`; the receiver
  never overwrites a buffer the consumer still holds. Incomplete frames are dropped and counted by
  default; delivering them is an explicit opt-in.
- **Zero-dependency `netstandard2.1` and `net8.0` assets.** The `netstandard2.0` asset (for
  .NET Framework 4.6.2+ and older Unity) additionally depends on `System.Memory`,
  `System.Threading.Tasks.Extensions` and `Microsoft.Bcl.AsyncInterfaces` — the three .NET Foundation
  packages that back `Span<T>`, `ValueTask` and `IAsyncDisposable` there. That asset is not only
  compiled but executed: the suite is re-run against it on .NET Framework 4.8 in CI.

## Status

Early development — see `docs/` for the design and the milestone plan. The library is exercised
against an in-process device simulator (`tests/GevSharp.Sim`) and a third-party virtual camera in CI,
on Linux and Windows, plus a .NET Framework 4.8 run of the same tests against the `netstandard2.0` asset.
macOS is compiled in CI for all three target frameworks but the suite is not run there yet — see
`docs/architecture.md` for what that does and does not mean. Real-camera validation is tracked in `docs/evaluation.md`, which records what
the hardware settled that no public source did.

## Quick look

```csharp
using GevSharp;
using GevSharp.Pfnc;

var devices = await GevDiscovery.DiscoverAsync();           // all interfaces, broadcast + subnet-directed
await using var dev = await GevDevice.OpenAsync(devices[0]); // takes control, starts heartbeat
var nodes = await dev.GetNodeMapAsync();                     // fetches and parses the camera XML

await nodes.GetFloat("ExposureTime").SetAsync(2000);
await nodes.GetEnumeration("PixelFormat").SetAsync("Mono8");

await using var stream = await dev.OpenStreamAsync();
await stream.StartAsync();

// Tell the node map the transport layer is configured. Vendor descriptions gate the acquisition
// commands on this (AcquisitionStart is a locked write-only node until it is set), and lock the
// format parameters while it holds.
await dev.SetTlParamsLockedAsync(true);
await nodes.GetCommand("AcquisitionStart").ExecuteAsync();

for (var i = 0; i < 10; i++)
{
    using var frame = await stream.ReceiveAsync();           // complete frames only
    Console.WriteLine($"{frame.FrameId}: {frame.Width}x{frame.Height} "
        + $"{PixelFormatInfo.Name(frame.PixelFormatCode)} stride={frame.Stride}");
}

await nodes.GetCommand("AcquisitionStop").ExecuteAsync();
await dev.SetTlParamsLockedAsync(false);
```

## Install

```
dotnet add package GevSharp
```

## Tools

Two desktop tools and a command line live in `samples/`. They are built on the library itself, so they
also serve as worked examples of the API.

| | What it does | Download |
|---|---|---|
| **Viewer** | Finds cameras, opens several at once, and shows them live — an even grid or one large view with the rest in a strip, tiles you can reorder by dragging. Zoom, pan, save a frame, read a pixel, and edit the full GenApi feature tree while the camera streams. | [gevsharp-viewer.exe](https://github.com/cmir79/GevSharp/releases/latest/download/gevsharp-viewer.exe) |
| **IP configurator** | Puts a camera on the address you want, including one stranded on a subnet you cannot reach, which no ordinary tool can open at all. Fixed or DHCP, in effect at once rather than at the next power-up. It also shows the host adapters, which cameras came in on which one, and whether that adapter's firewall is what keeps the images out. | [gevsharp-ipconfig.exe](https://github.com/cmir79/GevSharp/releases/latest/download/gevsharp-ipconfig.exe) |
| **CLI** | `discover`, `info`, `features`, `get`, `set`, `grab`, `regtest`, and `sim` — an in-process device simulator to develop against with no hardware. Run it from the source tree: `dotnet run --project samples/GevSharp.Cli -- discover` | — |

The two downloads are self-contained Windows x64 builds: one file, nothing to install, no .NET runtime
needed. Both are Avalonia applications and build for Linux and macOS from the same source.

## Logging

The library never writes logs itself. Attach a sink once at startup:

```csharp
GevLog.Sink = (level, source, message, ex) => myLogger.Log(level, source, message, ex);
```

## Integration with CvInspect

GevSharp has no OpenCV dependency by design. A thin adapter materializes `GevFrame` into a GC-owned
`CamFrame` for [CvInspect.Imaging](https://github.com/cmir79/CvInspect); the adapter lives on the CvInspect side.

## License

Apache-2.0. See `THIRD-PARTY-NOTICES.md` for attribution of adapted code.

"GigE Vision" is a registered trademark of the Association for Advancing Automation (A3). GevSharp is an
independent project, is not affiliated with A3, and makes no compliance claim. "GenICam" is a trademark of the
European Machine Vision Association (EMVA); GevSharp is not affiliated with EMVA. "GigE", "GVCP", "GVSP" and
"GenICam" are used descriptively, to name the protocols and the description format this library speaks.
