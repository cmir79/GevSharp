# Architecture

This document is the contract between modules. Implement against it; change it before changing the
public surface it describes.

## Module map

```
src/GevSharp/
  GevLog.cs, GevException.cs, IGevPort.cs         shared: logging sink, exception family, register-access boundary
  Gvcp/        GvcpConst, GvbsAddr                  wire constants + bootstrap register map (shared)
               GvcpPacket*, GvcpChannel             request/response channel: ID correlation, retries, PENDING_ACK
               GevDiscovery, GevDeviceInfo          multi-NIC discovery, FORCEIP
               GevDevice (in root namespace)        control session: CCP, heartbeat, reg/mem access, IGevPort impl
  Xml/         GevXmlLoader, GevXmlDoc              First/Second URL → XML text (Local: / File: / http:, ZIP)
  GenApi/      INode family, GenApiNodeMap          public node interfaces (shared)
               Model/  NodeDef records + parser     XML → immutable node definitions (Group recursion, StructReg expansion)
               Formula/ FormulaParser, Formula      SwissKnife / Converter expression engine (no third-party dependency)
               Runtime/ node implementations        pValue chains, address resolution, caching, selectors, guards
  Gvsp/        GvspConst                            wire constants (shared)
               GvspPacket parsing, GevFrame, GevFramePool, GevStream, GevStreamOpt, GevStreamStats
  Pfnc/        PixelFormat, PixelFormatInfo, PixelUnpack   PFNC codes, bits/pixel, packed-format unpackers
tests/GevSharp.Sim/                                 in-process device simulator (GVCP responder + GVSP sender)
tests/GevSharp.Tests/                               xunit v3; unit tests + loopback integration tests via Sim
tests/GevSharp.Net48/                               the same test sources re-compiled for net48 — runs the netstandard2.0 asset
samples/GevSharp.Cli/                               discover / info / features / get / set / grab / regtest / sim — real-camera evaluation tool
```

Dependency direction: `GenApi` depends only on `IGevPort`. `Gvsp` depends on `GevDevice` (for register
writes and the resend command) but not on `GenApi`. `GevDevice.GetNodeMapAsync` is the only place that
wires `Xml` + `GenApi` + `Gvcp` together.

## Target frameworks

`netstandard2.0;netstandard2.1;net8.0`. Write to the netstandard2.0 API surface; add faster paths under
`#if NETSTANDARD2_1_OR_GREATER` / `#if NET8_0_OR_GREATER` only with a fallback. PolySharp supplies the
language-feature polyfills (records, `init`, `required`, `Index`/`Range` types); array range slicing
(`arr[1..]`) still does not compile on 2.0 — use `AsSpan().Slice`.

Not available on netstandard2.0 (use the alternative): `Socket.Receive(Span)` → `byte[]` overloads;
`Math.Clamp` → own helper; `string.Contains(char)` → `IndexOf(char) >= 0`; `string.Split(char, options)` →
`Split(new[] { c }, options)`; `HashCode.Combine` → hand-rolled; `ArgumentNullException.ThrowIfNull` →
explicit check; `PeriodicTimer` / `Task.WaitAsync` / `CancellationTokenSource.CancelAsync` → timer +
`Task.Delay` loops; default interface members → not allowed; `Encoding.Latin1` → `Encoding.ASCII`;
`Convert.ToHexString` → own helper; non-generic `TaskCompletionSource` → `TaskCompletionSource<bool>`.
Always create `TaskCompletionSource` with `TaskCreationOptions.RunContinuationsAsynchronously`.

## What this library does not do

A library's boundaries are part of its contract. Everything below is a deliberate omission, not an oversight:
the constants may exist in `GvcpConst`/`GvbsAddr` because the packet decoder names them, but there is no
implementation and no plan implied. Anyone who needs one of these should know before they wire it up rather
than after the stream stays empty.

| Not implemented | What that means in practice |
|---|---|
| **Message channel and events** (`EVENT_CMD`, `EVENTDATA_CMD`) | A device can be configured to signal events, but GevSharp opens no message channel and raises no event. `Channels: message 1` in `info` reports the device's capability, not ours. |
| **`ACTION_CMD` and scheduled actions** | No way to fire a synchronised trigger across cameras from the host. |
| **Multicast streaming** | One stream, one destination. `SCDA` is always set to this host's address; there is no group join and no read-only "listener" mode alongside another controlling application. |
| **Chunk streams (`payload_type` 4)** | Payload types 1 (image) and 5 (extended chunk data) are assembled — both carry the geometry in the leader. Type 4 makes the whole payload a chunk stream with a 12-byte leader and no geometry, so no frame can be built. Turning a vendor's chunk mode on typically switches to type 4; the receiver reports it as `Unsupported` and says so once in the log (measured — see `docs/evaluation.md`). Registers on a chunk port report `NotAvailable` rather than reading the device address, so no chunk node invents a value. **If this is ever built, read the design note below first.** |
| **Stream channels above 0** | `OpenStreamAsync(int streamChannel, ...)` takes the index and the register maths is per-channel, but nothing has been exercised beyond channel 0. |
| **Persistent IP configuration** | The bootstrap registers are named (`PersistentIp0` and friends) and `info` prints which configuration modes a device supports and uses, but there is no API to *set* a persistent address. `GevDiscovery.ForceIpAsync` sets a **volatile** address only — it is lost on power cycle. |
| **USB3 or any non-GigE transport** | Out of scope by design. |

### Design note: what building chunk support would take

Written after measuring a real device, so the shape is not re-derived from scratch later. The wire facts
are in `docs/protocol-notes.md`; this is the part that constrains *our* structure.

**The layering cannot stay as it is.** To hand back a frame with `Width`/`Height`/`PixelFormatCode` for a
type-4 payload, something must read the image chunk's own header — and which bits hold the width is stated
only by the device's XML (`ChunkWidth` → a `MaskedIntReg` at a vendor-chosen offset on a vendor-chosen
port). Even *which* entry is the image is a vendor id. So interpreting a chunk payload requires the node
map, and today `Gvsp` does not depend on `GenApi` (see the dependency direction above). Hard-coding the
offsets would work on one vendor and fail silently on the next — the opposite of the point of this library.

The split that keeps the direction intact:

- **`Gvsp`** assembles the payload and walks the entries from the end into an `id → (offset, length)` index.
  It never interprets a chunk. The frame says "this is a chunk payload" and exposes the index.
- **`GenApi`** attaches a frame to the node map for a scope; chunk ports then read from that frame's bytes
  instead of the device, so `ChunkWidth` and friends simply work, and an image view can be built on top.

**Why an explicit attach.** A node is one object; a chunk value belongs to one frame. With two frames in
hand, `ChunkExposureTime` has no answer unless the caller says which frame is meant. So the scope is part
of the API, not an implementation detail — and leaving it out is what makes chunk metadata race with the
thing it exists to be synchronous with. Reading a chunk node outside a scope must stay `NotAvailable`,
which is what it already does.

**What already exists.** The "size unknown until the trailer" machinery is in place and tested from the
type-5 work (R19): `ExpectedBytes = -1`, sizing from the `PayloadSize` hint, finalising the payload size
from what actually arrived, and dropping-then-growing when a chunk payload exceeds the buffer. Type 4 needs
the entry walker, geometry from the image chunk, the attach scope, and the chunk routing inside
`RegisterCore` (including `SwapEndianess` and `CacheChunkData`, which are parsed into the model but not
honoured).

**Cost of not having it:** a device with chunk mode on delivers no frames — loudly, with a named reason.
That is a deliberate resting state, not a silent failure.

## Public API (root namespace `GevSharp`)

### Discovery

```csharp
public sealed class GevDiscoveryOpt
{
    public int TimeoutMs { get; set; } = 1000;          // collect replies for this long
    public int Repeat { get; set; } = 2;                // total DISCOVERY_CMD sends per target within the window, including the first (not "retries")
    public IReadOnlyList<IPAddress>? Interfaces { get; set; }   // null = every IPv4 interface that is up
    public bool LimitedBroadcast { get; set; } = true;  // 255.255.255.255
    public bool DirectedBroadcast { get; set; } = true; // subnet broadcast of each interface
}

public static class GevDiscovery
{
    public static Task<IReadOnlyList<GevDeviceInfo>> DiscoverAsync(GevDiscoveryOpt? opt = null, CancellationToken ct = default);
    /// unicast DISCOVERY_CMD to one address (works across subnets and on loopback simulators)
    public static Task<GevDeviceInfo?> ProbeAsync(IPAddress address, int timeoutMs = 1000, CancellationToken ct = default);
    public static Task ForceIpAsync(PhysicalAddress mac, IPAddress ip, IPAddress subnet, IPAddress gateway, GevDiscoveryOpt? opt = null, CancellationToken ct = default);
}

public sealed record GevDeviceInfo
{
    public required PhysicalAddress Mac { get; init; }
    public required IPAddress Address { get; init; }
    public required IPAddress Subnet { get; init; }
    public required IPAddress Gateway { get; init; }
    public required IPAddress InterfaceAddress { get; init; }   // host interface that heard the reply
    public required int SpecMajor { get; init; }
    public required int SpecMinor { get; init; }
    public required uint DeviceMode { get; init; }
    public required uint SupportedIpCfg { get; init; }
    public required uint CurrentIpCfg { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public required string DeviceVersion { get; init; }
    public required string ManufacturerInfo { get; init; }
    public required string SerialNumber { get; init; }
    public required string UserDefinedName { get; init; }
    public bool IsBigEndianDevice => (DeviceMode & GvbsAddr.DeviceModeBigEndian) != 0;
    public static GevDeviceInfo ParseDiscoveryAck(ReadOnlySpan<byte> payload, IPAddress interfaceAddress);  // 248 bytes; shorter → GevException
}
```

Discovery details: one socket per interface bound to `(interfaceIp, 0)` with `EnableBroadcast`; send to
255.255.255.255:3956 and to the interface's directed broadcast; collect ACKs until the timeout; dedupe by
MAC (keep the reply whose interface shares the device subnet if there are several). Truncated ACKs are
logged and skipped, never turned into ghost entries. Flags byte = `FlagAckRequired | FlagAllowBroadcastAck`.

### Device

```csharp
public enum GevAccessMode { Control, Exclusive, ReadOnly }

public sealed class GevDeviceOpt
{
    public GevAccessMode AccessMode { get; set; } = GevAccessMode.Control;
    public int GvcpTimeoutMs { get; set; } = 500;
    public int GvcpRetries { get; set; } = 3;
    public int HeartbeatTimeoutMs { get; set; } = 3000;      // written to GVBS 0x0938 when we control
    public int? HeartbeatPeriodMs { get; set; }              // null = device-accepted timeout / 3; ReadOnly sessions run no heartbeat (GevDevice.HeartbeatPeriodMs = 0)
    public IPAddress? LocalAddress { get; set; }             // null = auto (route lookup / discovery interface)
    public string? XmlCacheDir { get; set; }                 // null = no on-disk cache of the camera XML
    public bool AllowSwitchover { get; set; } = false;       // set CCP switchover-enable bit
}

public sealed class GevDevice : IGevPort, IAsyncDisposable
{
    public static Task<GevDevice> OpenAsync(GevDeviceInfo info, GevDeviceOpt? opt = null, CancellationToken ct = default);
    public static Task<GevDevice> OpenAsync(IPAddress address, GevDeviceOpt? opt = null, CancellationToken ct = default);

    public GevDeviceInfo Info { get; }             // re-read from bootstrap registers after open
    public IPAddress Address { get; }
    public IPAddress LocalAddress { get; }         // host address used for GVCP; also the SCDA for streams
    public GevAccessMode AccessMode { get; }
    public bool IsOpen { get; }
    public uint GvcpCapability { get; }            // GVBS 0x0934
    public ulong TimestampTickFrequency { get; }   // GVBS 0x093C/0x0940 (0 if unreadable)
    public event Action<GevDevice, Exception?>? ControlLost;   // heartbeat failed or CCP taken by someone else

    public Task<uint> ReadRegAsync(uint addr, CancellationToken ct = default);
    public Task<uint[]> ReadRegsAsync(IReadOnlyList<uint> addrs, CancellationToken ct = default);   // batches of 135 only when the GVCP concatenation capability bit is set, else one per packet
    public Task WriteRegAsync(uint addr, uint value, CancellationToken ct = default);
    public Task WriteRegsAsync(IReadOnlyList<KeyValuePair<uint, uint>> writes, CancellationToken ct = default);
    public Task ReadMemAsync(uint addr, Memory<byte> dst, CancellationToken ct = default);          // chunks of MaxMemPayload
    public Task WriteMemAsync(uint addr, ReadOnlyMemory<byte> src, CancellationToken ct = default);
    public Task<string> ReadStringAsync(uint addr, int length, CancellationToken ct = default);    // NUL-terminated ASCII/UTF-8

    public Task<GevXmlDoc> GetXmlAsync(CancellationToken ct = default);                // Xml module
    public Task<GenApiNodeMap> GetNodeMapAsync(CancellationToken ct = default);         // cached after first call
    public Task<GevStream> OpenStreamAsync(GevStreamOpt? opt = null, CancellationToken ct = default);  // Gvsp module; channel 0
    public Task<GevStream> OpenStreamAsync(int streamChannel, GevStreamOpt? opt = null, CancellationToken ct = default);  // channel count from GVBS 0x0904
    // Both overloads need control: a ReadOnly session cannot write the stream-channel registers and is
    // refused with GevControlLostException. Acquisition also needs the transport-layer lock, see GenApi below.

    public GvcpChannel Gvcp { get; }               // low-level access (stream uses it for PACKETRESEND)
    public ValueTask DisposeAsync();               // stop heartbeat, release CCP (write 0), close socket
}
```

Open sequence: create `GvcpChannel` → read the identity block field by field (READREG per 32-bit register,
READMEM per string at its own address — a bulk READMEM of 0x0000..0x00F7 is not a byte image on devices that
leave reserved words unimplemented, see `docs/protocol-notes.md`) → read GVCP capability, tick frequency and
heartbeat timeout → if `AccessMode != ReadOnly`: write CCP (`CcpControl`, plus `CcpExclusive` for Exclusive,
plus `CcpSwitchoverEnable` if allowed); `ACCESS_DENIED` →
`GevControlLostException("device is controlled by another application")` → write heartbeat timeout →
start heartbeat task (read CCP every period; N consecutive failures = control lost → raise `ControlLost`,
mark device closed). Heartbeat shares the channel with user requests — the channel serializes them.

Releasing control: `DisposeAsync` writes CCP = 0 whenever the CCP write was *sent*, not only when its ACK was
seen. A cancelled or timed-out open may already have been applied by the device, and leaving it unreleased
locks the camera for a whole device heartbeat timeout (R21). The only case that must not release is
`ACCESS_DENIED`, where the privilege belongs to another application.

`IGevPort` implementation: `ReadAsync`/`WriteAsync` map to READMEM/WRITEMEM; 4-byte-aligned 4-byte
accesses may use READREG/WRITEREG. An address above `uint.MaxValue` is narrowed to its low 32 bits with a
one-time warning per address — vendor descriptions declare such addresses (see `docs/protocol-notes.md`) and
GVCP has nowhere to put the high bits. An access whose end leaves the 32-bit space is a `GevException`.

### GVCP channel (`GevSharp.Gvcp`)

```csharp
public sealed class GvcpChannelOpt { public int TimeoutMs { get; set; } = 500; public int Retries { get; set; } = 3; public int MaxPendingAckWaitMs { get; set; } = 10000; }
// Retries = resends after the first send (default 3 → 4 sends, ~2 s per silent request). GevDeviceOpt.GvcpRetries has the same meaning.

public sealed class GvcpChannel : IDisposable
{
    public GvcpChannel(IPEndPoint device, IPAddress? localAddress = null, GvcpChannelOpt? opt = null);
    public IPEndPoint LocalEndPoint { get; }
    public IPEndPoint DeviceEndPoint { get; }
    /// serialized request/response: assigns req_id (1..65535, never 0), sends, waits for the ACK with the same
    /// req_id and the expected ACK command; PENDING_ACK extends the wait to the announced time plus one more
    /// TimeoutMs window (capped by MaxPendingAckWaitMs) — ending exactly at the announced time turns a reply that is
    /// late by the timer granularity into a timeout, and the retry makes the device execute the command twice;
    /// retries on timeout.
    public Task<GvcpAck> RequestAsync(GvcpCmd cmd, CancellationToken ct = default);
    /// fire-and-forget command with ack_required = 0 (PACKETRESEND). Thread-safe, no allocation on the hot path.
    public void SendNoAck(ReadOnlySpan<byte> packet);
    public void SendPacketResend(ulong blockId, uint firstPacketId, uint lastPacketId, bool extendedIds, int streamChannel = 0);
}
```

A single receive loop (dedicated thread or one pending async receive) demultiplexes by `req_id`; late
replies for an already-completed request are dropped and counted. Responses are checked for
`command == expected ACK`, `req_id == sent`, and `status`; an error status raises `GevStatusException` — for WRITEREG/WRITEMEM the
ACK index of the failing entry is carried in `GevStatusException.FailedIndex` (rebased to the caller's list
index by `GevDevice.WriteRegsAsync`). Additive diagnostics on the channel: `StaleAckCount`, `ForeignPacketCount`,
`MalformedPacketCount`, `PendingAckCount`, `IsDisposed`, `Opt`; on the device: `DeviceHeartbeatTimeoutMs`,
`HeartbeatPeriodMs`; on `GevDeviceInfo`: `CharacterSet`, `IsReachableDirectly`, `ReadFromDeviceAsync`.

### Stream (public types in `GevSharp`; wire types `GvspPacketView`, `GvspImageLeader`, `GvspTrailer` in `GevSharp.Gvsp`)

The buffer pool is `internal`: rules 1–4 of "Buffer ownership" make it a library concern, and a consumer that
could rent from it could break the lease invariant the receiver depends on.

```csharp
public enum PacketSizeMode { Auto, Fixed }

public sealed class GevStreamOpt
{
    public int BufferCount { get; set; } = 8;                 // frames in the pool
    public PacketSizeMode PacketSizeMode { get; set; } = PacketSizeMode.Auto;   // Auto: probe with SCPS fire-test from the NIC MTU downwards
    public int PacketSize { get; set; } = 1500;               // used when Fixed; Auto stores the negotiated value here after StartAsync
    public int SocketBufferBytes { get; set; } = 32 * 1024 * 1024;
    public bool ResendEnabled { get; set; } = true;
    public int InitialPacketTimeoutMs { get; set; } = 2;      // wait before the first resend request (reordering grace)
    public int PacketTimeoutMs { get; set; } = 20;            // between resend requests for the same hole
    public int FrameRetentionMs { get; set; } = 100;          // give up on a frame this long after its last packet
    public double PacketRequestRatio { get; set; } = 0.25;    // never request more than this fraction of a frame's DISTINCT packets; asking for the same hole again does not spend more budget
    public bool DeliverIncompleteFrames { get; set; } = false;
    public bool FirewallTraversal { get; set; } = true;       // one byte to the device's SCSP port after opening the channel
    public int FirewallTraversalIntervalMs { get; set; } = 15_000;   // re-send that byte after this much silence (0 = never)
    public int? LocalPort { get; set; }                       // null = ephemeral
    public int InterPacketDelay { get; set; } = 0;            // SCPD in timestamp ticks; 0 = leave device value
    public ThreadPriority ReceiverPriority { get; set; } = ThreadPriority.AboveNormal;
    public int? PayloadSize { get; set; }                     // null = the pool grows lazily from the first leader; pass the device's PayloadSize yourself when frames carry chunk data
    public int MaxPayloadBytes { get; set; } = 256 * 1024 * 1024;   // ceiling on a leader-declared frame and on the size the pool will learn
}

public sealed class GevStream : IAsyncDisposable
{
    public int LocalPort { get; }
    public int PacketSize { get; }                            // negotiated SCPS
    public GevStreamStats Stats { get; }                      // live counters (Interlocked), snapshot via Stats.Snapshot()
    public event Action<GevFrameDiag>? FrameDropped;          // incomplete / no-buffer / error frames (called on receiver thread — keep it cheap)

    public Task StartAsync(CancellationToken ct = default);   // bind + tune socket, write SCDA/SCP, negotiate SCPS, start thread. Does NOT send AcquisitionStart.
    // Acquisition is the caller's step and it needs the transport-layer lock first:
    //   await stream.StartAsync(); await device.SetTlParamsLockedAsync(true);
    //   await nodes.GetCommand("AcquisitionStart").ExecuteAsync();
    //   ... await nodes.GetCommand("AcquisitionStop").ExecuteAsync(); await device.SetTlParamsLockedAsync(false);
    public Task StopAsync(CancellationToken ct = default);    // write SCP = 0, stop thread, complete pending receives with GevStreamClosedException
    public ValueTask<GevFrame> ReceiveAsync(CancellationToken ct = default);
    public bool TryReceive(out GevFrame? frame);
    public ValueTask DisposeAsync();
}

public sealed class GevFrame : IDisposable
{
    public ulong FrameId { get; }        // block id (16-bit or 64-bit)
    public ulong Timestamp { get; }      // device ticks from the leader
    public uint PixelFormatCode { get; } // PFNC value; PixelFormat enum via Pfnc.PixelFormatInfo
    public int Width { get; } public int Height { get; } public int OffsetX { get; } public int OffsetY { get; }
    public int PaddingX { get; } public int PaddingY { get; }
    public int Stride { get; }           // bytes per line = PixelFormatInfo.LineBytes(PixelFormatCode, Width) + PaddingX — GVSP Packed and 4:1:1 lines end on a whole 2-/4-pixel group, PFNC p lines on a byte boundary, unknown codes ceil(Width * bpp / 8); 0 means the lines are not byte-aligned and PaddingX is 0, so there is no stride and Data is one continuous run of Width * Height pixels
    public int PayloadSize { get; }      // valid bytes in Data — for a chunk-bearing frame this is image + chunk
    public int ImageSize { get; }        // bytes of Data the image occupies; == PayloadSize unless HasChunkData, then smaller — slice Data to this before reading pixels
    public bool IsComplete { get; }
    public int MissingPackets { get; }
    public ReadOnlyMemory<byte> Data { get; }   // valid until Dispose; ObjectDisposedException afterwards
    public byte[] ToArray();
    public void Dispose();               // returns the buffer to the pool; idempotent
}

public sealed class GevStreamStats { FramesCompleted, FramesIncomplete, FramesDroppedNoBuffer, FramesDelivered, PacketsReceived, PacketsResent (status 0x0100), PacketsMissing, ResendRequests, ResendRecovered, ErrorPackets, BytesReceived, LastFrameId }
```

Receiver design: one dedicated background thread per stream. Blocking `Receive` (not `ReceiveFrom`: no
per-packet `EndPoint` allocation; the deliberate consequence is that the datagram source is not checked
against the device address — any host that can reach the bound UDP port feeds the reassembler) into a
scratch `byte[]` (size = max(PacketSize, 9000) + slack), parse the 8/20-byte header, and copy the payload
straight into the frame buffer at `(packetId - 1) * dataBytesPerPacket`. Track received packets per frame
in a bit array. A hole is an id below the highest id received so far; the not-yet-transmitted tail becomes
holes only once the tail is known (trailer seen, a newer block started, or `PacketTimeoutMs` of silence).
On trailer or on a hole detected by a jump in `packetId`, run the missing-packet check (grace →
`SendPacketResend` per contiguous hole → repeat after `PacketTimeoutMs` → abandon after `FrameRetentionMs`
of no **data** for the frame; a hole the device answers with 0x800C / 0x8012 (0x8011: that id and all before
it) is given up on individually — other holes are still requested, and the frame closes one
`PacketTimeoutMs` after nothing is left to ask for). Three rules keep a slow or stalled device from costing
a healthy frame, each of them learned from a measured failure:
- The request budget counts **distinct** packets. Charging every request would let two answers that come back
  slower than `PacketTimeoutMs` exhaust a 25% budget on a frame that had lost 20%, and throw it away with one
  packet outstanding.
- An **error answer is not progress**: 0x800C / 0x8010 / 0x8011 / 0x8012 / 0x8014 does not restart the
  retention clock. Otherwise a device that keeps answering "temporarily unavailable" pins the frame — and its
  pool buffer — open forever.
- Packets above the highest id received, requested only because the device went quiet (`IsTailAssumed`), are
  **speculative** and do not spend the budget or condemn the frame. A device that pauses mid-frame would
  otherwise burn the frame's loss budget on packets it had not sent yet, making a later real loss
  unrecoverable. A leader or all-in packet for a block that was closed recently opens a
new frame only when it is not evidently a duplicate — a resent copy (status 0x0100) or a leader whose
timestamp equals the closed frame's is dropped as a duplicate; a resent leader for a block older than the
newest one in flight is ignored; any other leader with an "old" block id is treated as the device having
restarted its block numbering (single-frame acquisitions restart at 1) and opens normally. Opening a block
never marks the tail of a block that is not older than it. Only a few frames
are in flight at once (leader of frame N+1 may arrive while N waits for resends); frames close in block
order. Completed frames are pushed to a bounded queue; `ReceiveAsync` awaits it. When the pool is empty,
the incoming frame is dropped and `FramesDroppedNoBuffer` increments — the receiver never reuses a buffer
the consumer holds. Frame buffers live in a pool with a lease version; a `GevFrame` is allocated once per
delivered frame (the object itself is not recycled, so a stale reference kept after `Dispose` always
throws `ObjectDisposedException` instead of silently reading the next lease) and its `Data` validates the
lease.

Packet-size negotiation (`Auto`): write SCPS with `ScpsFireTest | ScpsDoNotFragment | size` for candidate
sizes from the interface MTU (or 9000 if unknown / 16000 cap) down by binary search; a test packet
arriving on the stream socket within 100 ms confirms the size. Do-not-fragment is mandatory on the probe:
without it a device fragments an oversized test datagram, the host reassembles it, and a path MTU below the
NIC MTU goes undetected. Fall back to 1500 if nothing is confirmed. The final SCPS write keeps the flag
bits the device had (do-not-fragment, big-endian; read once at start) and, in `Auto`, sets
do-not-fragment because that is the condition the size was verified under.
Start sequence: bind socket → set `ReceiveBufferSize` (log the actual value the OS granted) → write
SCDA = LocalAddress, SCP = LocalPort (the fire test needs the destination first) → read SCPS flags →
negotiate SCPS → write SCPS → write SCPD if requested → start thread.
Stop sequence: write SCP = 0 (and SCDA = 0) → close socket → join thread.

**Firewall traversal.** After writing SCDA/SCP and *before* the packet-size probe, the stream sends one byte
from its own socket to the device's stream source port (SCSP, valid once the channel is open). A stateful host
firewall — Windows on a Public profile is the common case — drops UDP it has not seen an outbound counterpart
for, so without this the fire-test packet and every GVSP packet are discarded and the stream reports a full
timeout with zero packets received. The single datagram creates the mapping, so no inbound rule and no
administrator rights are needed. Skipped when the device refuses SCSP or reports 0, and switchable with
`GevStreamOpt.FirewallTraversal`. The mapping is then **kept alive**: whenever no packet has arrived for
`FirewallTraversalIntervalMs` (default 15 s, 0 = off), the receiver sends the same byte again, counted in
`GevStreamStats.FirewallKeepAlives`. Inbound traffic refreshes the mapping by itself, so this only runs while
the stream is quiet — a trigger mode with long gaps, or a stream held open with acquisition stopped — which is
exactly when a stateful mapping expires and the next frame would vanish whole. The check costs nothing on the
packet path: it runs only when a receive times out. Measured on a Basler ace on a Public-profile NIC: 0 packets without it,
full rate (jumbo 9000, 61 MB/s, zero missing) with it.

### Pixel formats (`GevSharp.Pfnc`)

`PixelFormat` enum with PFNC values (Mono8 = 0x01080001, Mono10 = 0x01100003, Mono10Packed = 0x010C0004,
Mono12 = 0x01100005, Mono12Packed = 0x010C0006, Mono16 = 0x01100007, Mono10p = 0x010A0046, Mono12p =
0x010C0047, BayerGR8 = 0x01080008, BayerRG8 = 0x01080009, BayerGB8 = 0x0108000A, BayerBG8 = 0x0108000B,
Bayer*10/12/16, RGB8 = 0x02180014, BGR8 = 0x02180015, RGBa8 = 0x02200016, BGRa8 = 0x02200017, YUV422_8 =
0x02100032, YCbCr422_8 = 0x0210003B, …). `PixelFormatInfo`: `BitsPerPixel(code) = (code >> 16) & 0xFF`,
`IsMono`, `IsBayer`, `IsColor`, `BayerPattern`, `Name(code)`, `Depth(code)` (significant bits of an unsigned
Mono/Bayer sample — Mono10 and Mono10Packed are both 10; 0 for signed, multi-component and unknown codes).
`PixelFormatInfo.LineBytes(code, width)` is the single definition of a line and `GevFrame.Stride` is built
from it, so a frame's stride can be passed straight to the unpack and fold routines.
`PixelFormatInfo.FrameBytes(code, width, height, paddingX, paddingY)` is the single definition of an image,
and it is not simply `LineBytes * height`: with `paddingX = 0` the pixels are one continuous run, so the
rounding happens once over `width * height` rather than once per line (measured on hardware — see
`docs/evaluation.md`). `PixelFormatInfo.IsLineByteAligned(code, width)` says whether a line ends on a byte;
when it does not and `PaddingX` is 0 there is no stride at all, `GevFrame.Stride` is 0, and a consumer folds
or unpacks the frame as a single run of `Width * Height` pixels. `PixelUnpack.UnpackToArray` makes that
decision itself from its `paddingX` argument.
`PixelUnpack`: Mono10Packed/Mono12Packed (GigE 3-bytes-per-2-pixels layout) and Mono10p/Mono12p (PFNC
lsb-packed) → `ushort[]`/`Span<ushort>`.
`PixelUnpack.CanFoldToMono8(code)` / `FoldToMono8(code, src, srcStrideBytes, dst, dstStrideBytes, width,
height)` (plus a `byte[]`-returning overload; both strides in bytes, unlike `Unpack`'s pixel-counted
destination stride) write the top 8 bits of every pixel for the single-component Mono/Bayer formats — 8,
unpacked 10/12/14/16, 10/12Packed, 10p/12p — keeping the Bayer mosaic, for consumers that need an 8-bit mono
image; unsupported codes throw `ArgumentException`, dimensions that do not fit an `int` line or array throw
`ArgumentOutOfRangeException`.

### XML (`GevSharp.Xml`)

```csharp
public sealed record GevXmlDoc(string Xml, string Url, string FileName, string? SchemaVersion);
public enum GevXmlUrlKind { Local, File, Http }
public sealed record GevXmlUrl   // Raw, Kind, FileName, Address, Length, FilePath, HttpUri, SchemaVersion, IsZip; static Parse / TryParse
public static class GevXmlLoader
{
    public const int HttpTimeoutMs = 10_000;
    public const int MaxXmlBytes = 64 * 1024 * 1024;   // bounds every source: Local: declared length, File: size, http body (MaxResponseContentBufferSize), ZIP inflation
    public static Task<GevXmlDoc> LoadAsync(IGevPort port, string? cacheDir = null, CancellationToken ct = default);   // First URL, then Second URL on failure
    public static Task<GevXmlDoc> LoadFromUrlAsync(IGevPort port, GevXmlUrl url, string? cacheDir = null, CancellationToken ct = default);   // one URL, no fallback
    public static GevXmlUrl ParseUrl(string url);   // Local:file.zip;F0F00000;3E8B  |  File:///path  |  http(s)://…  (+ ?SchemaVersion=…)
    public static string ExtractXml(byte[] bytes, string fileName);   // ZIP → first *.xml entry; plain XML passthrough
    public static string CacheFileName(string manufacturer, string model, string deviceVersion, string fileName);
}
```

`GevDevice.GetXmlAsync` is `GevXmlLoader.LoadAsync(this, opt.XmlCacheDir, ct)` — the loader depends only on
`IGevPort`. The cache file name is `{Manufacturer}_{Model}_{DeviceVersion}_{FileName}` sanitized, with the
extension rewritten to `.xml` (the cached content is the decompressed text); a cache hit still reads the
three GVBS strings and the URL register to build the key, never the XML region.

Read First URL (512 bytes) then Second URL as fallback. `Local:` addresses/lengths are hexadecimal without
`0x`. Read memory in `MaxMemPayload` chunks, rounding the length up to a multiple of 4 and trimming.
Cache file name: `{Manufacturer}_{Model}_{DeviceVersion}_{FileName}` sanitized; cache is opt-in.

### GenApi (`GevSharp.GenApi`)

Public node interfaces are in `INode.cs`; `GenApiNodeMap` facade in `GenApiNodeMap.Contract.cs`.

```csharp
public partial class GenApiNodeMap
{
    public static GenApiNodeMap Parse(string xml, IGevPort port);       // throws GenApiException on malformed XML
    public static GenApiNodeMap Parse(GenApiXmlModel model, IGevPort port);
}
```

**Transport-layer lock.** `GevDevice.SetTlParamsLockedAsync(bool locked, ct) → Task<bool>` writes the
`TLParamsLocked` node (returns false when the description has none). That node is *not* a device register —
it lives in the node map, and vendor descriptions gate features on it: on a Basler ace, `AcquisitionStart`
carries `ImposedAccessMode=WO` plus `pIsLocked = (TLParamsLocked = 0)`, so it reads as a locked write-only
node — i.e. `NotAvailable` — until the host sets the lock, and the format parameters (`Width`, `Height`,
`PixelFormat`) lock while it holds. The library never sets it implicitly: the caller owns the acquisition
sequence, and hiding the write would also silently change which features are writable. Each `GevDevice`
has its own node map, so the flag is per session and resets when the device is reopened.

Model layer (`GenApi/Model`): `GenApiXmlParser.Parse(string xml) → GenApiXmlModel` — a flat dictionary of
`NodeDef` records (one per node element) plus `RegisterDescriptionInfo`. `<Group>` elements are
transparent containers: recurse into them at any depth and register their children as top-level nodes.
`<StructReg>` expands into one `MaskedIntReg`-like def per `<StructEntry>`, each inheriting the parent's
address/length/port/endianness. Supported elements: Category, Integer, IntReg, MaskedIntReg,
IntSwissKnife, IntConverter, Float, FloatReg, SwissKnife, Converter, String, StringReg, Boolean,
Enumeration/EnumEntry, Command, Register, Port, Group, StructReg/StructEntry, Node. Unknown elements
produce a warning log and a placeholder def (never a crash). All `p*` attributes are stored as names and
resolved at runtime bind time; missing targets are a `GenApiException` at bind, with the node name.
Details the runtime relies on (full list in `docs/genapi-model.md`): EnumEntry defs are keyed
`EnumEntry_{Enumeration}_{EntryName}` in the flat dictionary because entry names repeat across
enumerations — find entries via `EnumerationDef.Entries`/`Symbolic`; `PollingTime` lives on the common
`NodeDef.PollingTimeMs` (it applies to Command too), not inside `RegisterSet`; a named inline address
`IntSwissKnife` is both nested in its register's `RegisterSet.AddressSwissKnives` and registered as a
node; `EventID` is exposed parsed as hexadecimal (`EventIdValue`); element nesting deeper than
`GenApiXmlParser.MaxElementDepth` is rejected with `GenApiException` so a hostile or corrupt device XML
cannot overflow the stack.

Formula layer (`GenApi/Formula`): `Formula.Parse(string) → Formula` (immutable AST, `Variables` list);
`Formula.Evaluate(Func<string, GenApiValue> resolve) → GenApiValue` where `GenApiValue` is an
int64/double union. Grammar: `+ - * / % ** & | ^ ~ << >> && || ! < > <= >= = == <> != ?:`, parentheses,
decimal / hex (`0x`) / float literals, `PI`/`E`, functions `SIN COS TAN ASIN ACOS ATAN ABS EXP LN LG SQRT
TRUNC FLOOR CEIL ROUND SGN NEG`. Precedence follows C. Integer ⊕ integer stays integer (`/` truncates,
`**` integer when exponent ≥ 0); any double promotes. Division by zero and invalid operations throw
`GenApiException` — never return 0 silently. Parse depth is bounded; variable names are identifiers
(letters, digits, `_`, `.`) and are resolved by the caller from `<pVariable Name="X">Node</pVariable>`.

Runtime layer (`GenApi/Runtime`): concrete node classes implementing the public interfaces over the
model + port.
- Address resolution: `Address` + Σ `pAddress` + `pIndex * (Offset | pOffset)` + `IntSwissKnife` children.
- Register cache: per register node, keyed by resolved address; `Cachable` = WriteThrough (default) /
  WriteAround / NoCache; `PollingTime` present → treat as NoCache for reads. Invalidation: writing a node
  invalidates itself, every node listed in its `pInvalidator`s (reverse index), and every node that
  depends on it through `pValue`/`pAddress`/`pIndex`/`pVariable`/`pSelected`. Selectors: writing a
  selector node invalidates all nodes it `pSelected`s (transitively); `pSelecting` is derived.
- Guards: `pIsImplemented`, `pIsAvailable`, `pIsLocked` evaluated on demand (each may be Integer/Boolean
  nodes or SwissKnife); `ImposedAccessMode` and `AccessMode` clamp the result. Reads on `WriteOnly`/
  `NotAvailable`/`NotImplemented` throw `GenApiException` with the node name and reason.
- MaskedIntReg bit numbering: `LSB`/`MSB` are given in the register's own numbering — for `BigEndian`
  registers bit 0 is the most significant bit of the register, for `LittleEndian` bit 0 is the least
  significant. Normalize to a shift/mask over the little-endian integer value after byte-order decoding.
- Integer semantics: `Sign` (Signed/Unsigned) with sign extension for lengths < 8; `Endianess`
  (LittleEndian/BigEndian, default LittleEndian); `Representation`; Min/Max/Inc from literals or
  `pMin`/`pMax`/`pInc`; enforce Min ≤ value ≤ Max and `(value - Min) % Inc == 0` on write (clamp is not
  silent — throw `GenApiException`).
- Float: FloatReg (4 or 8 bytes, IEEE), Converter with `FormulaTo` (host → device, variable `FROM`) /
  `FormulaFrom` (device → host, variable `TO`), `Slope` for min/max — `Increasing`/`Decreasing` map the
  target's endpoints, `Automatic` computes both and sorts them, and `Varying` (declared non-monotonic)
  yields no bounds at all, since the target's endpoints are then no evidence of this node's,
  `pValue` to an Integer or Float, `DisplayNotation`/`DisplayPrecision`/`Unit`.
- Enumeration: entries with `Value`, `Symbolic`, `NumericValue`, own `pIsImplemented`/`pIsAvailable`;
  value comes from `pValue` (Integer/IntReg/MaskedIntReg) or `Value` literal.
- Command: `CommandValue`/`pCommandValue` written to `pValue`; `IsDone` = read `pValue` and compare with the
  command value when `PollingTime` is present, else true.
- Boolean: `OnValue`/`OffValue` (default 1/0) over `pValue` or literal `Value`.
- String: StringReg (fixed length, NUL-padded, ASCII/UTF-8 per device mode), literal `Value`.
- Port: `pPort` on every register node → `IPortNode.Port`; ignore `ChunkID`/`SwapEndianess`/`CacheChunkData`
  unless chunk parsing is implemented (log Debug once).
- Every register read/write goes through `IGevPort`; length-limited to what the port accepts; `Length` >
  512 splits inside the port.

Threading: node map instances are not thread-safe for concurrent writes; concurrent reads are safe
(cache dictionary access under a lock; no mutation on read except cache fill under the same lock).

GenApi runtime — implementation notes where the behaviour is more specific than the list above:
- Binding creates every node first and resolves names afterwards, so forward references (and `.Entry.`
  formula variables on an Enumeration defined later) work. An `.Entry.` variable is a bind-time constant
  and leaves no edge. Cycle detection runs over the value edges (`pValue`, `pValueCopy`, `pValueDefault`,
  `pValueIndexed`, `pIndex`, `pAddress`, `pOffset`, `pLength`, inline address `IntSwissKnife`, `pVariable`,
  `pMin`/`pMax`/`pInc`, `pCommandValue`) and rejects the document; a cycle that closes only through a guard
  edge (`pIsImplemented`/`pIsAvailable`/`pIsLocked`) is logged as a warning and the map still binds, because
  guards are read through the internal value path and never recurse. `pInvalidator`/`pSelected`/`pFeature`
  never form a cycle because evaluation does not follow them (real XML has selectors that select each other).
- Formulas are parsed once at bind time and evaluated with `Formula.EvaluateAsync`: every variable the
  formula names is read first (including variables of an unselected `?:` branch), then the expression is
  computed synchronously. One failing variable read fails the formula — never a silent 0.
- Invalidation after a write of node X drops the caches of the closure reached from X through
  dependents (reverse of every value/guard edge), `pInvalidator` listeners and `pSelected` targets — and,
  for each node reached, its own value chain down to the register (`pValue`, `pValueDefault` and every
  `pValueIndexed` slot). The written register itself is governed by `Cachable` (WriteThrough keeps the
  written bytes; WriteAround/NoCache drop), and a chain walk stops at the written node so it is not undone.
  Registers that share bytes without a graph edge (StructReg entries, alias registers) are found by address
  overlap and dropped. `INode.Invalidate()` uses the same closure but includes the node itself and its whole
  value chain.
- Write-only registers cannot be read for a read-modify-write, so the node map keeps a write shadow — the
  bytes it last wrote at each address — and uses it as the base: a field written through one
  `MaskedIntReg`/`StructEntry` survives the next write of a sibling field. Bytes never written read as 0.
  Reads never feed the shadow (a register may read status and write control) and invalidation never clears
  it (the last write is the only knowledge the host has); content the device changes on its own (e.g. after
  a user-set load) is not reflected.
- Guards are read through the internal value path (no access check on the predicate node), so a guarded
  predicate never recurses into its own access check; a target's guards propagate to nodes delegating to
  it through `pValue`. Error reasons: "not implemented", "not available", "locked", "write-only",
  "read-only", always with the node name. A node whose value source is missing right now (a `pIndex` that
  matches no `pValueIndexed`/`ValueIndexed` slot and has no default) reports `AccessMode.NotAvailable` from
  the query methods instead of throwing; reading or writing it throws with the index and selector name.
- Integer writes are validated (Min ≤ v ≤ Max, `(v − Min) % Inc == 0` — anchored at 0 when `Min` is
  undefined, computed without overflow — and ValidValueSet) before any port access; a Converter validates
  against its own mapped limits first, then the target validates the converted (rounded,
  `MidpointRounding.AwayFromZero`) value. Float writes reject NaN and check Min/Max only (no grid check).

## Public surface index

The sections above spell out the types a consumer drives. This index exists so nothing public is documented
nowhere — every public type of `GevSharp` belongs to exactly one line here.

| Group | Types | Where it is specified |
|---|---|---|
| Discovery, device, stream | `GevDiscovery(Opt)`, `GevDeviceInfo`, `GevDevice`, `GevDeviceOpt`, `GevAccessMode`, `GevStream`, `GevStreamOpt`, `PacketSizeMode`, `GevFrame`, `GevStreamStats`, `GevStreamStatsSnap`, `GevFrameDiag`, `GevFrameDropReason` | the sections above |
| Errors | `GevException`, `GevTimeoutException`, `GevStatusException`, `GevControlLostException`, `GevStreamClosedException`, `GenApiException` | "Errors" in CLAUDE.md; each carries the operation or node it failed on |
| Logging | `GevLog`, `GevLogLevel` | a sink the host installs once; the library writes nowhere by itself |
| Register boundary | `IGevPort` | the one seam between GenApi and a transport |
| GVCP wire | `GvcpConst`, `GvbsAddr`, `GvcpPacket`, `GvcpCmd`, `GvcpAck`, `GvcpCmdHeader`, `GvcpAckHeader`, `GvcpChannel`, `GvcpChannelOpt` | "GVCP channel" above and `docs/protocol-notes.md` |
| GVSP wire | `GvspConst`, `GvspPacketView`, `GvspImageLeader`, `GvspTrailer` | "Stream" above and `docs/protocol-notes.md` |
| GenApi node interfaces | `INode`, `IInteger`, `IFloat`, `IString`, `IBoolean`, `IEnumeration`, `IEnumEntry`, `ICommand`, `IRegister`, `ICategory`, `IPortNode`, `NodeKind`, `AccessMode`, `Visibility`, `Representation` | "GenApi" above; the interfaces themselves carry the per-member contract |
| GenApi model | `GenApiXmlParser`, `GenApiXmlModel`, `NodeDef` and its subtypes, `NodeDefKind`, `NodeNameSpace`, `RegisterSet`, `PIndexDef`, the formula def types | `docs/genapi-model.md` — one row per kind and field |
| Formula engine | `Formula`, `GenApiValue` | "Formula layer" above |
| Pixel formats | `PixelFormat`, `PixelFormatInfo`, `PixelUnpack`, `PixelPacking`, `BayerPattern` | "Pixel formats" above |
| XML retrieval | `GevXmlLoader`, `GevXmlDoc`, `GevXmlUrl`, `GevXmlUrlKind` | "XML" above |

## Buffer ownership rules

1. The receiver thread owns scratch buffers and pool buffers that are not leased.
2. A `GevFrame` returned by `ReceiveAsync`/`TryReceive` is a lease; the consumer owns its buffer until
   `Dispose`. The receiver never writes into a leased buffer.
3. `GevFrame.Data` is `ReadOnlyMemory<byte>` over the pool buffer — zero-copy. Consumers that keep pixels
   beyond `Dispose` call `ToArray()`.
4. Pool exhaustion drops frames (counted) instead of blocking the receiver thread.

## Threading model

- `GevDevice`: one GVCP receive loop thread; requests serialized by a `SemaphoreSlim`; heartbeat task.
- `GevStream`: one receiver thread; delivery through a bounded async queue (no `System.Threading.Channels`
  dependency — a small `AsyncBoundedQueue<T>` with `SemaphoreSlim`).
- `GenApiNodeMap`: async everywhere; no thread affinity.
- Events (`ControlLost`, `FrameDropped`) are raised on library threads; handlers must be cheap.

## Testing strategy

- Unit tests per module with hand-authored fixtures (packets as byte arrays, small GenApi XMLs).
- Loopback integration tests against `GevSharp.Sim` (discovery probe, control/heartbeat, XML fetch,
  node map read/write, streaming with injected packet loss → resend recovery, incomplete-frame policy,
  buffer-pool exhaustion).
- End-to-end tests live in `tests/GevSharp.Tests/Integration/`: `SimRig` starts one `SimDevice` on
  `127.0.0.1:<ephemeral>` and opens a `GevDevice` through the internal `OpenAsync(IPEndPoint, ...)` overload;
  acquisition is driven by writing `SimFeatureAddr` registers directly (no node map). `RecordingPort` wraps
  the device's `IGevPort` to assert the order of stream-channel register accesses. Tests that assert exact
  frame sequences drive the simulator in software-trigger mode (`SimRig.TriggerAsync`) instead of relying
  on its free-running frame rate. A defect these tests find is fixed, not gated: the assertion message names
  the file and the cause so the test keeps guarding the fix.
- `Category=VirtualCamera` tests run only when `GEVSHARP_VIRTUAL_CAMERA=<ip>` is set (CI on Linux). They point the same code at a device somebody else wrote, which is the only check we have that the receiver is not merely agreeing with our own simulator.
- No vendor XML in the repository. Real-camera XMLs are validated locally from an ignored folder.
- **The `netstandard2.0` asset is executed, not merely compiled.** `tests/GevSharp.Net48` links the same test
  sources and builds them for `net48`, so the branches that only exist on that asset actually run — the
  non-`Span` socket sends, `NumericCodec` and `CappedReadStream` fallbacks, and the XML cache swap that has no
  atomic overwrite-move on .NET Framework. Where behaviour genuinely differs per asset the tests say so with
  `#if NETFRAMEWORK` rather than being weakened for both. The project is deliberately **not** in the solution
  (net48 does not restore on Linux or macOS); run it by path — `dotnet test tests/GevSharp.Net48/GevSharp.Net48.csproj`
  — and CI runs it in the Windows job. Editing a linked test source means running this leg too.
- **Repository policy is guarded by tests, not by convention.** `RepositoryPolicyTests` reads the restored
  dependency closure, `git ls-files`, `git ls-files --eol` and the library sources to enforce: no dependency
  outside a small permissive allow-list (transitive included), no committed XML/ZIP beyond the four hand-written
  fixtures, LF in the index with CRLF in the working tree, and no blocking wait on an async result anywhere in
  the library. Each was verified against a planted violation.

## Naming (project-wide)

Types: `*Opt` options, `*Svc` service, `*Store`, `*Evt` event args, `*Cfg` configuration. Interfaces with
`I` and a role suffix (`IGevPort`). Private fields `_camelCase`; booleans `Is*/Has*`; time values carry
their unit (`TimeoutMs`). No `Util`/`Helper` classes — use `Ext`, `Conv`, `IO`, or a noun.

## Evaluation CLI (`samples/GevSharp.Cli`)

`gevsharp-cli` is the real-camera evaluation tool for `docs/evaluation.md` and doubles as the consumption example.
`Program.cs` only turns Ctrl+C into a `CancellationToken` (`ConsoleCancel`: first press cancels, second press terminates)
and calls `CliApp.RunAsync`, which installs the `GevLog` sink on stderr (Info; `--verbose` = Debug, `--quiet` = Warn),
parses the command line with the hand-rolled `CliArgs`/`CliOptSpec` (long/short options, `--name=value`, negative
numbers as positionals, `--` terminator, typed getters that turn bad input into `CliUsageException`), dispatches to an
`ICliCommand` and maps exceptions to exit codes: 0 ok, 1 usage (`CliUsageException`, `ArgumentException`), 2 device
(`GevException`, socket/IO, `NotImplementedException` — including failures while the stream and acquisition are being
started), 3 stream (`GevStreamClosedException`, errors after acquisition started). Global options (`--verbose`, `--quiet`,
`--access <mode>`, `--help`, `--version`) may stand before or after the command name; a valued one before the command
takes its value token with it. stdout carries only command results so it can be piped; every log line goes to stderr.

Commands: `discover` (broadcast on every interface, or `--probe ip[:port]` unicast), `info` (bootstrap block, GVCP
capability bits, heartbeat, tick frequency, stream channel 0, XML URLs; read-only session), `features` / `get` / `set`
(written against the `INode` interfaces only), `grab` (stream with per-interval and final statistics from
`GevStreamStats`, `--save` raw frames plus JSON sidecar, `--packet-timeout` / `--frame-retention` to widen the resend
timings on a slow host; acquisition goes through the `AcquisitionStart`/`AcquisitionStop` command nodes — with the
transport-layer lock set around them — or through `--acq-start-addr`/`--acq-stop-addr` register writes when the node map
is unavailable), `regtest` (alternating reads of two registers while the heartbeat runs; mismatches and latency), and
`sim` (runs `GevSharp.Sim` as a standalone fake camera).
Every `<ip>` accepts a `:port` suffix; a non-standard port uses the internal `OpenAsync(IPEndPoint)` /
`ProbeAsync(IPEndPoint)` overloads, which `src/GevSharp/GevSharp.csproj` grants through
`InternalsVisibleTo("GevSharp.Cli")`. Tests live in `samples/GevSharp.Cli/Tests` (excluded from the executable by
`<Compile Remove="Tests\**" />`) and are compiled into the suite by `tests/GevSharp.Tests` through a `ProjectReference`
plus `<Compile Include="..\..\samples\GevSharp.Cli\Tests\**\*.cs" LinkBase="Cli" />`. They run against `GevSharp.Sim` on
loopback in a non-parallel collection, because `CliApp` swaps the process-wide console writers and `GevLog.Sink`.
`QuickLook.cs` holds the README's example as compiled (never executed) code, so a public-API change breaks the build
instead of silently rotting the first thing a user copies.
