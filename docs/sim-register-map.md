# SimCamera register map

`tests/GevSharp.Sim` is the in-process device the tests and CI talk to. It answers GVCP on
`SimDeviceOpt.BindAddress:GvcpPort` (default `127.0.0.1:<ephemeral>`) and streams GVSP to `SCDA:SCP`. The
packet builders and parsers are written independently of the library; only the constants in `GvcpConst`,
`GvbsAddr` and `GvspConst` are shared, so a bug on one side cannot cancel out a bug on the other.

This page lists every register the simulator gives meaning to, which node of `Assets/SimCamera.xml` maps to
it, and the protocol behaviours and limits tests can rely on. Conventions:

- All registers are 32-bit big-endian unless a width says otherwise. Strings are NUL-terminated UTF-8 in
  fixed-length fields (the last byte is always NUL).
- Access: **RO** answers WRITEREG/WRITEMEM with `WRITE_PROTECT (0x8004)`; **RW** stores the value; **SC**
  (self-clearing) stores the value, acts on it, and reads back 0; **W→0** is write-only in effect (reads 0).
- Addresses inside the main region that are not listed below are plain RAM: readable, writable, no
  side effects. Bit numbers in the "XML node" column are the register's own GenApi numbering — `BigEndian`
  registers count bit 0 as the most significant bit, so integer bit *k* is XML bit *31 − k*.

## Memory layout

| Region | Range | Contents |
|---|---|---|
| main | `0x0000_0000 .. 0x0001_0FFF` | bootstrap block (64 KiB) + feature page (4 KiB at `0x0001_0000`) |
| XML | `0x0010_0000 .. 0x0010_0000 + ceil4(len) − 1` | the GenApi XML bytes, zero-padded to a multiple of 4, read-only |
| anything else | — | `INVALID_ADDRESS (0x8003)`; an access may not straddle a region boundary |

## Bootstrap block (GVBS)

Reset values come from `SimDeviceOpt` at construction. The DISCOVERY_ACK payload is bytes `0x0000..0x00F7`
of this block, verbatim.

| Address | Width | Access | Reset value | XML node |
|---|---|---|---|---|
| `0x0000` Version | 4 | RO | `0x0002_0000` (2.0) | — |
| `0x0004` DeviceMode | 4 | RO | `0x8000_0001` (big-endian device, charset UTF-8) | — |
| `0x0008` MacHigh / `0x000C` MacLow | 4 + 4 | RO | `02:47:45:56:xx:xx` — locally administered; the last two bytes hash `SerialNumber` | — |
| `0x0010` SupportedIpCfg | 4 | RO | persistent \| DHCP \| LLA (`0x7`) | — |
| `0x0014` CurrentIpCfg | 4 | RO | persistent (`0x1`) | — |
| `0x0024` CurrentIp | 4 | RO | `BindAddress` | — |
| `0x0034` CurrentSubnet | 4 | RO | `255.0.0.0` | — |
| `0x0044` CurrentGateway | 4 | RO | `0.0.0.0` | — |
| `0x0048` ManufacturerName | 32 | RO | `Manufacturer` ("GevSharp") | `DeviceVendorName` (StringReg) |
| `0x0068` ModelName | 32 | RO | `Model` ("SimCamera") | `DeviceModelName` (StringReg) |
| `0x0088` DeviceVersion | 32 | RO | `DeviceVersion` ("1.0") | `DeviceVersion` (StringReg) |
| `0x00A8` ManufacturerInfo | 48 | RO | `ManufacturerInfo` ("in-process simulator") | — |
| `0x00D8` SerialNumber | 16 | RO | `SerialNumber` ("SIM0001") | `DeviceSerialNumber` (StringReg) |
| `0x00E8` UserDefinedName | 16 | RW | `UserDefinedName` ("") | `DeviceUserID` (StringReg, RW) |
| `0x0200` FirstUrl | 512 | RO | `Local:SimCamera.xml;100000;<hex length>` | — |
| `0x0400` SecondUrl | 512 | RO | empty | — |
| `0x0600` NumNetworkInterfaces | 4 | RO | 1 | — |
| `0x064C` / `0x065C` / `0x066C` PersistentIp/Subnet/Gateway0 | 4 each | RW | bind address / `255.0.0.0` / 0; FORCEIP for this MAC rewrites them | — |
| `0x0670` LinkSpeed0 | 4 | RO | 1000 | — |
| `0x0900` NumMessageChannels | 4 | RO | 0 | — |
| `0x0904` NumStreamChannels | 4 | RO | 1 | — |
| `0x0908` NumActionSignals | 4 | RO | 0 | — |
| `0x090C` ActionDeviceKey | 4 | RW | 0 | — |
| `0x0910` NumActiveLinks | 4 | RO | 1 | — |
| `0x092C` GvspCapability | 4 | RO | 0 | — |
| `0x0930` MessageChannelCapability | 4 | RO | 0 | — |
| `0x0934` GvcpCapability | 4 | RO | concatenation \| write-mem \| packet-resend \| CCP-app-socket \| serial-number \| name-register; + pending-ack when `SupportPendingAck`. Heartbeat-disable is **not** set. | — |
| `0x0938` HeartbeatTimeout | 4 | RW | `HeartbeatTimeoutMs` (3000). 0 = never expire. | `GevHeartbeatTimeout` (Integer → IntReg) |
| `0x093C` / `0x0940` TimestampTickFreq | 8 | RO | `1_000_000_000` (1 GHz — ticks are nanoseconds) | `TimestampTickFrequency` (Integer → 8-byte IntReg) |
| `0x0944` TimestampControl | 4 | W→0 | bit1 (value 2) resets the counter, bit0 (value 1) latches it | `TimestampLatch` (Command, value 1) |
| `0x0948` / `0x094C` TimestampLatched | 8 | RO | 0 until the first latch | `TimestampLatchValue` (Integer → 8-byte IntReg, NoCache) |
| `0x0950` DiscoveryAckDelay | 4 | RW | 0 (not honoured) | — |
| `0x0954` GvcpConfig | 4 | RW | 0 (not honoured) | — |
| `0x0958` PendingTimeout | 4 | RO | `PendingAckDelayMs` | — |
| `0x0A00` CCP | 4 | RW (see below) | 0 | `GevCCP` (Integer → IntReg, NoCache) |
| `0x0A04` PrimaryAppPort | 4 | RO | 0; the CCP writer's UDP port while controlled | — |
| `0x0A14` PrimaryAppIp | 4 | RO | 0; the CCP writer's IPv4 while controlled | — |

## Stream channel 0 (`0x0D00`)

| Address | Access | Reset value | Meaning | XML node |
|---|---|---|---|---|
| `0x0D00` SCP | RW | 0 | host UDP port, bits 15..0. 0 = channel closed: nothing is sent, resends and fire tests are ignored | `GevSCPHostPort` (Integer → MaskedIntReg LSB 31 / MSB 16) |
| `0x0D04` SCPS | RW | `DefaultPacketSize` (1500) | bits 15..0 = IP packet size including IP+UDP headers; bit 31 = fire-test (consumed, never stored); bits 30/29 are stored but ignored | `GevSCPSPacketSize` (Integer → MaskedIntReg LSB 31 / MSB 16) |
| `0x0D08` SCPD | RW | 0 | inter-packet delay in timestamp ticks (ns), applied after the leader and after every payload packet, best effort (sleep above ~2 ms of remaining gap, a short busy-wait below it — yielding there overshoots a microsecond gap by a whole scheduler quantum) | `GevSCPD` (Integer → IntReg) |
| `0x0D18` SCDA | RW | 0 | destination IPv4. 0 = channel closed | `GevSCDA` (Integer → IntReg, IPV4Address) |
| `0x0D1C` SCSP | RO | GVSP socket's port after `Start()` | source port of the GVSP sender | — |
| `0x0D20` SCC | RO | `0x3` | integer bit 0 = packet resend supported, bit 1 = extended ids supported | `GevSCCExtendedIds` (Boolean → MaskedIntReg Bit 30) |
| `0x0D24` SCCFG | RW | `ExtendedIds ? 0x2 : 0` | integer bit 1 = send 64-bit block ids / 20-byte GVSP headers from the next frame on | `GevSCCFGExtendedIds` (Boolean → MaskedIntReg Bit 30) |

## Feature page (`0x0001_0000`, `SimFeatureAddr`)

`UserSetLoad` restores every RW value in this table to its reset value (`FrameCounter` is kept).

| Address | Name | Access | Reset value | Meaning | XML node |
|---|---|---|---|---|---|
| `0x10000` | Width | RW | `Opt.Width` (640) | frame width in pixels | `Width` (Integer Min 8, pMax WidthMax, Inc 4, pIsLocked AcquisitionActive) → `WidthReg` |
| `0x10004` | Height | RW | `Opt.Height` (480) | frame height in pixels | `Height` (Integer Min 8, pMax HeightMax, Inc 2, pIsLocked) → `HeightReg` |
| `0x10008` | OffsetX | RW | 0 | copied into the leader | `OffsetX` (Integer 0..4088 Inc 4) → `OffsetXReg` |
| `0x1000C` | OffsetY | RW | 0 | copied into the leader | `OffsetY` (Integer 0..4094 Inc 2) → `OffsetYReg` |
| `0x10010` | PixelFormat | RW | `Opt.PixelFormat` (Mono8 `0x01080001`) | PFNC code; bits 23..16 give bits per pixel for the frame size | `PixelFormat` (Enumeration: Mono8, Mono10, Mono12, Mono16, BayerRG8, RGB8; pIsLocked) → `PixelFormatReg` |
| `0x10014` | ExposureTimeRaw | RW | 10 000 000 (10 ms) | exposure in timestamp ticks; no effect on timing | `ExposureTimeRaw` (Integer 1000..2e9) → `ExposureTimeRawReg`; `ExposureTime` (Converter, µs: `FormulaFrom = TO * 1000000.0 / TICKFREQ`, `FormulaTo = FROM * TICKFREQ / 1000000`, TICKFREQ = TimestampTickFrequency) |
| `0x10018` | GainSelector | RW | 0 | index 0..2 into the GainRaw block | `GainSelector` (Enumeration AnalogAll/DigitalAll/DigitalRed, pSelected Gain, GainRaw) → `GainSelectorReg` |
| `0x1001C` + 4·n | GainRaw[n], n = 0..2 | RW | 0 | 0.1 dB units | `GainRaw` (Integer 0..1023) → `GainRawReg` (Address 0x1001C, `pIndex Offset=4` GainSelectorReg); `Gain` (Converter dB: `FormulaFrom = TO / 10.0`, `FormulaTo = FROM * 10`) |
| `0x10028` | TriggerControl | RW | 0 | integer bit 0 = TriggerMode (1 = On), bits 7..4 = TriggerSource (0 Software, 1 Line0, 2 Line1) | StructReg → `TriggerModeReg` (Bit 31), `TriggerSourceReg` (LSB 27 / MSB 24); `TriggerMode` (Enumeration Off/On), `TriggerSource` (Enumeration, pIsAvailable TriggerModeIsOn) |
| `0x1002C` | AcquisitionMode | RW | 0 | 0 Continuous, 1 SingleFrame, 2 MultiFrame | `AcquisitionMode` (Enumeration) → `AcquisitionModeReg` |
| `0x10030` | AcquisitionStart | SC | 0 | 1 starts the sender thread | `AcquisitionStart` (Command value 1, PollingTime 10) → `AcquisitionStartReg` (NoCache) |
| `0x10034` | AcquisitionStop | SC | 0 | 1 stops the sender and waits for it | `AcquisitionStop` (Command value 1, PollingTime 10) → `AcquisitionStopReg` (NoCache) |
| `0x10038` | AcquisitionStatus | RO | 0 | 1 while the sender thread runs | `AcquisitionActive` (Integer, Guru) → `AcquisitionActiveReg` (NoCache); the pIsLocked predicate of Width/Height/PixelFormat |
| `0x1003C` | AcquisitionFrameRate | RW | `Opt.FrameRateHz` (30) as IEEE-754 binary32 big-endian | frame period in free-running mode; NaN/0/negative → 1 Hz | `AcquisitionFrameRate` (Float 1..1000 Hz) → `AcquisitionFrameRateReg` (FloatReg 4) |
| `0x10040` | TestPattern | RW | 1 | 0 Off (all zero), 1 DiagonalRamp, 2 FrameCounter | `TestPattern` (Enumeration) → `TestPatternReg` |
| `0x10044` | UserSetSelector | RW | 0 | 0 Default, 1 UserSet1 (both load the same defaults) | `UserSetSelector` (Enumeration, pSelected UserSetLoad) → `UserSetSelectorReg` |
| `0x10048` | UserSetLoad | SC | 0 | 1 restores the feature page | `UserSetLoad` (Command value 1, PollingTime 10) → `UserSetLoadReg` (NoCache) |
| `0x1004C` | AcquisitionFrameCount | RW | 1 | frames per start in MultiFrame mode | `AcquisitionFrameCount` (Integer 1..65535, pIsAvailable AcquisitionModeIsMultiFrame) → `AcquisitionFrameCountReg` |
| `0x10050` | ReverseX | RW | 0 | 0/1; the pattern is not mirrored | `ReverseX` (Boolean) → `ReverseXReg` |
| `0x10054` | WidthMax | RO | 4096 | — | `WidthMax` (Integer) → `WidthMaxReg` |
| `0x10058` | HeightMax | RO | 4096 | — | `HeightMax` (Integer) → `HeightMaxReg` |
| `0x1005C` | FrameCounter | RO | 0 | frames sent since construction | — |
| `0x10060` | TriggerSoftware | SC | 0 | 1 releases one frame when TriggerMode = On | `TriggerSoftware` (Command, pIsAvailable TriggerSoftwareIsAvailable) → `TriggerSoftwareReg` (NoCache) |

Derived nodes without a register: `PayloadSize` (IntSwissKnife `((WIDTH * ((PIXFMT >> 16) & 0xFF) + 7) / 8) * HEIGHT`,
invalidated by Width/Height/PixelFormat), `AcquisitionModeIsMultiFrame`, `TriggerModeIsOn`,
`TriggerSoftwareIsAvailable` (IntSwissKnife predicates), `TLParamsLocked` (host-side Integer literal).

Both Converters carry a float literal in `FormulaFrom` on purpose. `TO` is the integer register value and the
formula engine keeps integer ÷ integer as a truncating integer division, so `TO / 10` would read 0.0 dB for
every raw value below 10 and break the write→read round trip. `FormulaTo` receives the Converter's float value
in `FROM`, so it is floating-point already. Both directions are therefore floating-point: a value written
through `Gain` reads back unchanged to 0.1 dB, and `ExposureTime` keeps sub-microsecond raw values.

## Pixel content

`DiagonalRamp` (default): byte *b* of line *y* in frame *f* is `(b + y + f) & 0xFF`, where *f* is the block
id. For Mono8 that is `(x + y + frameId) & 0xFF`; for wider formats the same byte ramp runs over
`ceil(width × bpp / 8)` bytes per line. `FrameCounter`: every byte is `f & 0xFF`. `Off`: zeros.
`SimDevice.BuildPatternFrame(width, height, pixelFormat, frameId, pattern)` returns the same bytes for tests.
Padding X/Y are always 0. Because there is no line padding, a group format's data is one continuous run:
the image is `lineBytes(width × height)` bytes, not `lineBytes(width) × height`, and at a width whose line
is not a whole number of bytes the ramp runs over the whole image as a single line (there is no stride, and
the receiver reports `Stride` 0).

## GVCP behaviour

- One server thread; commands are processed one at a time in arrival order. Every reply echoes `req_id`.
  `ack_required = 0` → no reply (the command is still executed). Replies come from the GVCP socket.
- **DISCOVERY** → 248-byte `DISCOVERY_ACK`. Only unicast to `GvcpEndPoint` is answered, on whatever port the
  socket has. Broadcast DISCOVERY is never seen: the socket is bound to `BindAddress` (a unicast address), and a
  unicast-bound UDP socket does not receive datagrams sent to a broadcast address. `GvcpPort = 3956` only makes
  unicast probes to the standard port work (one instance per host); discovery tests against the simulator use a
  unicast probe, not broadcast.
- **FORCEIP** with this device's MAC rewrites the persistent IP/subnet/gateway registers and ACKs; the bound
  address never changes. Other MACs are ignored silently.
- **READREG** (≤ 135 addresses): unaligned → `BAD_ALIGNMENT`, unmapped → `INVALID_ADDRESS`; the reply carries
  the values read before the failing one.
- **WRITEREG** (≤ 135 pairs): processed in order; on failure the ACK index is the failing pair's index and
  later pairs are not written. Read-only → `WRITE_PROTECT`.
- **READMEM**: count must be a non-zero multiple of 4 ≤ 512 (`INVALID_PARAMETER`), aligned (`BAD_ALIGNMENT`),
  inside one region (`INVALID_ADDRESS`). Reply = address + data.
- **WRITEMEM**: same checks; the whole range must be writable (`WRITE_PROTECT`); words go through the same
  path as WRITEREG so side effects (CCP, SCPS fire-test, command bits) apply. ACK index = bytes written.
- **CCP**: 0 → any endpoint may take control (`2` control, `1` exclusive, `+4` switchover bit is stored).
  While non-zero, only the owner endpoint (IP + UDP port of the writer) may write anything — everyone else
  gets `ACCESS_DENIED (0x8006)` on WRITEREG/WRITEMEM; reads remain open. The owner writing a value without
  the control/exclusive bits releases. `PrimaryAppPort/Ip` follow the owner.
- **Heartbeat**: any command from the owner restarts the timer. When the timer exceeds `HeartbeatTimeout`
  (checked every ≤ 20 ms), CCP is cleared, `ControlOwner` becomes null, `HeartbeatTimeouts` increments and
  `ControlOwnerChanged(null)` fires. `HeartbeatTimeout = 0` disables expiry. Owner reads of CCP increment
  `HeartbeatObserved`.
- **PENDING_ACK** (`SupportPendingAck`): every acknowledged WRITEREG first gets `PENDING_ACK` with
  time = `PendingAckDelayMs`, then the real `WRITEREG_ACK` after that delay (the server thread sleeps).
- **PACKETRESEND**: never acknowledged. Accepted only from the owner and for channel 0; anything else is
  recorded in `ResendRequests` with `IsAccepted = false`. Standard (12-byte) and extended (20-byte, flag
  `0x10`) forms are parsed. `ResendRequests` keeps at most `ResendRequestsCap` (1024) entries — older ones
  are dropped and counted in `ResendRequestsTrimmed`; `ClearResendRequests()` empties it.
- Unknown commands → `NOT_IMPLEMENTED (0x8001)` with ack command = command + 1. Malformed datagrams
  (short header, wrong packet type, length field beyond the datagram, short payloads) are dropped and
  counted in `MalformedCount`; `LastError` describes the last problem in English. The receive buffer is
  65536 bytes, so every legal UDP datagram fits; should the socket ever report an oversize datagram
  (`MessageSize`) it is counted as malformed on every platform rather than as a socket error.

## GVSP behaviour

- `AcquisitionStart` starts a sender thread; frames go out only while `SCP ≠ 0` and `SCDA ≠ 0` (the loop keeps
  running and starts sending as soon as the channel is opened). `AcquisitionStop`, `Stop()` and `Dispose()`
  end it; `SingleFrame` stops after one frame has been sent, `MultiFrame` after `AcquisitionFrameCount` frames
  have been sent — periods spent with the channel closed or with a failed send do not count.
- A second `AcquisitionStart` while the sender is running is ignored and recorded in `LastError`; one that
  arrives while a finished SingleFrame/MultiFrame sender is still winding down waits for it and then starts
  normally. `AcquisitionStop` and `Stop()` wait up to 3 s for the sender thread; if it is still alive after
  that, `AcquisitionStatus` stays 1, `LastError` says so, and the next `AcquisitionStart` is refused until the
  thread has gone.
- Free-running frames follow `AcquisitionFrameRate`; with `TriggerMode = On` a frame is sent per
  `TriggerSoftware` write (hardware lines never fire). `TriggerSoftware` arms only while `TriggerMode = On`,
  and `AcquisitionStart` discards any armed trigger, so a trigger written before the start, while
  `TriggerMode = Off`, or left over from a previous run never releases a frame.
- Per frame: image leader (payload type 1, 1 GHz timestamp from a monotonic counter, pixel format, size,
  offsets, padding 0) → payload packets `1..N` of `SCPS − 28 − header` bytes (last one shorter) → trailer
  (payload type 1, size_y). Header is 8 bytes, or 20 bytes with the extended-id bit when SCCFG bit 1 is set.
- Block ids start at 1 and wrap 65535 → 1 in standard mode; in extended mode they are 64-bit and do not wrap.
  `SeedBlockId(n)` makes the next frame `n + 1` so wrap tests need not send 65535 frames.
- `DropPacket(frameId, packetId)` is consulted once per payload packet of the first transmission; leader,
  trailer and resent copies are never dropped. Dropped packets count in `PacketsDropped`.
- The last `ResendHistoryFrames` frames are kept. A resend of a known block re-sends the requested ids
  (0 = leader, `1..N` payload, `N+1` trailer) with status `0x0100`; ids beyond the trailer, or any id of an
  unknown block, produce a header-only error packet with status `0x800C` for that id (at most 256 per
  request). Resends need an open channel.
- `SCPS` fire-test bit: the bit is consumed (never stored); a header-only zero-filled packet of
  `size − 28` bytes is sent to `SCDA:SCP` immediately when `size ≤ MaxPacketSize` (or no cap) and the channel
  is open; otherwise nothing is sent (`TestPacketsIgnored`). A fire test issued while SCDA or SCP is still 0
  is ignored by design (`TestPacketsIgnored`, `LastError`) — like a real device, the simulator has nowhere to
  send it — so a host must write SCDA = its address and SCP = its port *before* negotiating SCPS
  (docs/architecture.md, GevStream start sequence).

## Limitations

- Unicast discovery only — broadcast DISCOVERY never reaches the unicast-bound socket, whatever `GvcpPort`
  is. `BindAddress` must be IPv4 (the constructor throws `ArgumentException` otherwise).
- One stream channel, no message channel, no events, no actions, no chunk data, no manifest table.
- Width/Height/PixelFormat are not refused while acquiring — the lock lives in the XML (`pIsLocked`); a
  change takes effect from the next frame.
- The device does not stop streaming when control is lost; SCP stays as written.
- FORCEIP does not rebind sockets. `DiscoveryAckDelay`, `GvcpConfig`, SCPS bits 30/29 are stored but unused.
- SCPD timing and the frame period are best effort on a general-purpose OS. A test may bound them only with a
  wide margin, and a bound below tens of milliseconds needs its own check that the host is not starving the
  process (a sleep/yield probe), because a starved sender is indistinguishable from a slow one in wall time.
- The sender holds a core for the last ~2 ms of every wait (frame period and SCPD alike) instead of yielding
  it, so the simulator is a poor neighbour on a host with one or two cores.
