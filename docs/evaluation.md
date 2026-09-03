# Real-camera evaluation scenarios

GevSharp is validated against two vendors (Basler, Crevis). Passing both is the vendor-free proof.
Each scenario is run with `samples/GevSharp.Cli` and the numbers are recorded here per release.

| # | Scenario | Pass criterion | CLI |
|---|---|---|---|
| 1 | Discovery on a multi-NIC host (camera on a dedicated NIC, office NIC also up) | Both cameras listed once each with correct interface address | `discover` |
| 2 | Register read/write with ID correlation | 10 000 alternating reads of two registers return the right values (no cross-talk) while heartbeat runs | `regtest <ip>` |
| 3 | XML fetch and feature control on a vendor XML that uses `<Group>` | Node count > 100; ExposureTime / Gain / PixelFormat / Width / Height / TriggerMode / AcquisitionMode read and write; selector (GainSelector → Gain) round-trips | `features <ip>` |
| 4 | Full-rate streaming (largest resolution, Mono8, max frame rate, jumbo frames) for 60 s | Dropped frames = 0 with resend on; packet loss reported | `grab <ip> -t 60` |
| 5 | Resend recovery with injected loss (switch port mirroring or `--drop` in the simulator; on a real camera, use a deliberately small socket buffer) | Frames recovered by resend > 0; incomplete frames = 0 after recovery | `grab <ip> --socket-buffer 256k` |
| 6 | Long-run stability (≥ 4 h continuous) | No control loss, no memory growth (working set flat), no leaked frames (pool returns to full on stop) | `grab <ip> -t 14400 --stats-every 60` |
| 7 | Multi-camera concurrent streaming (2 cameras on one NIC, bandwidth-limited with SCPD) | Sum of frame rates matches configured rates; dropped = 0 | two parallel instances: `grab <ip1> --packet-delay <ticks> -t 60` and `grab <ip2> --packet-delay <ticks> -t 60` (one device per invocation) |

## Results

### Basler acA2500-14gm (mono, 2592x1944), 2026-09-03

Direct link on a dedicated NIC, MTU 9000, Windows on a Public firewall profile.

| # | Scenario | Result |
|---|---|---|
| 1 | Discovery, multi-NIC host | One device listed once, every identity field correct |
| 2 | Register read/write with ID correlation | 2000 alternating reads, 0 mismatches, 0 stale acks, 0.29/0.35/2.01 ms min/avg/max |
| 3 | XML fetch and features on a vendor `<Group>` XML | 68,815-byte ZIP from device memory, 2298 nodes bound; selectors, Converter and SwissKnife all read (`ResultingFrameRateAbs` computes); write rejected off the increment grid as specified |
| 4 | Full-rate streaming, 60 s | 874 frames delivered / 876 completed at 14.58 fps (sensor maximum 14.59), 73.5 MB/s, 494,940 packets, **0 incomplete, 0 missing, 0 no-buffer drops**, jumbo 9000 negotiated |
| 5 | Resend recovery | The link loses nothing at full rate, so loss had to be forced. See the section below — the device does answer resends, but only inside a window narrower than our default grace |
| 6 | Long run | 15 min continuous (not the 4 h criterion): 13,133 frames, 7,420,145 packets, 66.2 GB at a flat 73.59 MB/s, **0 incomplete, 0 missing, 0 no-buffer**; control held throughout and the stream stopped clean. The 4 h run is still pending |
| 7 | Multi-camera | Pending (one camera available) |

### Continuous streaming, 15 minutes

Not the four hours scenario 6 asks for, but long enough to say something the 60 s run cannot. At
2592x1944 Mono8, jumbo 9000, default 32 MiB socket buffer:

```
frames      13131 delivered, 13133 completed (14.59 fps), last frame id 13133
dropped     0 incomplete, 0 no-buffer, 0 error, 0 unsupported payload
throughput  73.59 MB/s (66,235,129,796 bytes)
packets     7,420,145 received, 0 missing, 0 duplicated, 0 ignored, 0 unsupported type
resend      95 packets requested, 0 recovered, 0 resent packets received, 0 error packets
```

Every 60 s interval reported the same 875-876 frames and the same 73.59 MB/s — no drift, no slow leak of
frame rate, and no interval with a single missing packet. Control was never lost: the heartbeat ran the whole
time on the same GVCP channel that answered the 95 resend requests. The stream stopped clean, with the pool
accounting for every buffer (13,133 completed against 13,131 delivered — the two in flight at the stop are
the ones the consumer had not taken yet).

What this run does **not** establish is scenario 6's memory criterion: the working set was not sampled, so
"no memory growth" is still unmeasured. The 4 h run remains to be done, with the working set recorded.

### Resend on a real camera — the device answers, but only while the block is still current

Scenario 5 cannot be run by waiting for loss: at 2592x1944 Mono8, 14.56 fps, jumbo 9000, the link
delivered 494,940 packets in 60 s with none missing. Loss (or at least late delivery) had to be forced by
shrinking the receive socket buffer, and the results say more about the device than about our recovery
path. All rows below are 15 s at full rate unless noted; "resent status" counts packets carrying the GVSP
resend status `0x0100`, "duplicates" counts payload packets for an id already held.

| socket buffer | `--packet-timeout` | resend requests | resent status | duplicates | error packets | missing | incomplete |
|---|---|---|---|---|---|---|---|
| 32 MiB (default) | 20 ms (default) | 0 | 0 | 0 | 0 | 0 | 0 |
| 256 KiB | 20 ms | 45 (30 s) | 0 | 0 | 0 | 0 | 0 |
| 64 KiB | 20 ms | 716 | 0 | 0 | 0 | 0 | 0 |
| 64 KiB | 5 ms | 321 | 0 | **31** | 0 | 0 | 0 |
| 64 KiB | 2 ms | 4035 | 0 | **628** | **2871** | 260 | 10 |
| 64 KiB + 40-way CPU load | 20 ms | 12,265 (25 s) | 0 | 0 | 0 | 110 | 2 |

Three things follow.

**The device answers only while it still holds the block.** It advertises packet resend (GVCP capability
`0xFE00000F`, bit 2) and our requests are well formed, yet at the default 20 ms grace it returns *nothing* —
not a resent packet, not even an "unavailable" error. At 5 ms some requests come back as data; at 2 ms most
come back as an error packet (asking for a packet it has not sent yet) and hundreds come back as data. A
frame's 563 packets take about 14 ms to transmit at this rate, so a request that arrives 20 ms after the
silence began is asking about a block the device has already retired. A grace tuned for reordering on the
wire is, on this camera, past the only window in which resend can work at all.

**Asking sooner is not simply better.** The 2 ms row is the only one with damage — 10 incomplete frames and
260 missing packets, against zero in every other row. It sends 269 GVCP requests per second down the same
serialized control channel that carries the heartbeat, and the 628 duplicates it earns land in a socket
buffer that is already the bottleneck. We did not isolate cause from correlation here, but the mechanism is
plain enough that "lower the grace to make resend work" is not a change to make blind.

**Resent packets arrive with the ordinary success status.** `PacketsResent` (status `0x0100`) stayed at 0 in
every row while duplicates moved, so on this camera resends are indistinguishable from originals by status
alone. That is why the receiver's recently-closed guard does not rely on the resend flag: it also compares
the leader timestamp against the block it just closed (`ShouldOpenForLeader`), which is what stops a late
resent leader from opening a ghost frame here. Anyone reading `Stats.PacketsResent` as "resend is working"
would read 0 on this device and be wrong; `PacketsDuplicated` is the signal that moves.

The recovery logic itself — request, receive, fill the hole, complete the frame — remains covered by the
simulator, which honours resends the way the protocol describes.

### Basler acA4112-8gc (colour, 4096x3000 BayerRG8), 2026-09-03

A second camera, swapped onto the same host and NIC. It matters because everything above was mono 8-bit:
this one streams a Bayer format at 12,288,000 bytes per frame, 1371 payload packets, and a higher
instantaneous rate.

| What | Result |
|---|---|
| Discovery, identity | Listed once, every identity field correct |
| Node map, feature read/write | Bound and read; `ExposureTimeAbs` written and read back |
| Streaming | 4096x3000 BayerRG8, jumbo 9000, 107 MB/s instantaneous, **0 incomplete, 0 missing, 0 resend requests** across every run |
| Frame bytes | Exactly `4096 x 3000 x 1`; `Stride` 3888-style padding absent (`PaddingX` 0, `Stride` 4096) |
| Bayer phase | Correct — demosaicing the delivered mosaic as RGGB gives channel ratios matching the vendor viewer's own render (R/G 0.812 vs 0.802, B/G 1.050 vs 1.074) |

**Every pixel format the camera offers was streamed, and our size agrees with the device on all of them.**
This is the check that the odd-width finding above says matters: the receiver's own computation of the frame
size against the device's `PayloadSize` node, per format, at a real geometry (4096 x 3000). Two frames each,
jumbo 9000.

| PixelFormat (vendor name) | Resolved to | Device `PayloadSize` | Our stride | Frames |
|---|---|---|---|---|
| Mono8 | Mono8 `0x01080001` | 12,288,000 | 4096 | 2/2 complete, 0 missing |
| BayerRG8 | BayerRG8 `0x01080009` | 12,288,000 | 4096 | 2/2 complete, 0 missing |
| BayerRG12 | BayerRG12 `0x01100011` | 24,576,000 | 8192 | 2/2 complete, 0 missing |
| BayerRG12Packed | BayerRG12Packed `0x010C002B` | **18,432,000** | 6144 | 2/2 complete, 0 missing |
| YUV422Packed | YUV422_8_UYVY `0x0210001F` | 24,576,000 | 8192 | 2/2 complete, 0 missing |
| YUV422_YUYV_Packed | YUV422_8 `0x02100032` | 24,576,000 | 8192 | 2/2 complete, 0 missing |

Three things this settles that no simulator could. The **packed** rule holds at a real large geometry
(4096 x 3000 x 1.5 = 18,432,000, three quarters of the unpacked size). The **16-bit container** path streams
(the camera drops to 4.5 fps because 24.5 MB a frame is the link's limit, not ours). And the vendor's own
format names map onto the right PFNC codes and names — `YUV422Packed` is `YUV422_8_UYVY`, not `YUV422_8`,
and getting those two the wrong way round would swap the chroma order silently.

The enumeration's per-entry access modes also came through correctly: on this colour camera the mono 10/12
entries report `NI` (not implemented) and the non-RG Bayer phases report `NA` (not available), which is the
guard machinery (R7) doing its job against a real vendor XML.

**Turning chunk mode on stops the stream — and that is a limitation, not a bug we fixed.** With
`ChunkModeActive = true` this camera changes `payload_type` to **4 (chunk data)** and sends a **12-byte**
leader: flags, payload type and timestamp, with no geometry at all — the image itself becomes one entry in
the chunk stream. GevSharp assembles payload types 1 (image) and 5 (extended chunk data), where the leader
still carries width, height and pixel format; it does not parse a type-4 chunk stream, so no frame can be
built. Streaming ran at 111 MB/s with 0 missing packets and delivered 0 frames.

Two real defects were found by trying it, both now fixed:

1. **The drop was silent.** 182 frames were counted as `error` with nothing in the log at any level. A
   receiver that stops delivering frames has to say why — that is the whole point of the diagnostics. It now
   warns once, naming the byte count and the declared type.
2. **The reason was wrong.** The leader was parsed as a 36-byte *image* leader first, so an unsupported
   payload type was reported as a corrupt header (`error` / invalid header) instead of what it is
   (`unsupported`). The payload type sits at the same offset in every leader and the rest of the leader's
   shape follows from it, so the type is now read first and dispatched on. `ChunkStreamingTests` pins both
   sides: type 4 is `Unsupported`, and a leader too short to even name its type is still `Error`.

Supporting type 4 means parsing the chunk stream (id + length trailers) to find the image chunk. That is a
feature, and it is not implemented.

**A third defect the same afternoon: chunk nodes were reporting invented values.** Asking what chunk data
*is* led to reading the nodes, and they answered — with fiction. On this camera, before the fix:

```
ChunkStride       = 512     (the real stride is 4096)
ChunkOffsetX      = 256     (the real OffsetX is 8)
ChunkExposureTime = 512     (the real exposure is 3000 us)
```

Every value came from reading the node's address in *device register space*. A `<Port>` carrying a
`ChunkID` is the XML saying "this value arrives in the frame, not from the device" — all 17 chunk ports on
this camera declare one — and the runtime was ignoring that and reading the address anyway. Whatever bytes
happened to live there came back looking like an answer. That is the failure the project's own rule names
(never let an error flow out as a plausible value).

Registers on a chunk port now report `NotAvailable`, and the read is refused at the port boundary with a
message that names the port. The boundary matters: blocking only at the access-mode layer still leaked,
because a formula's variable read (`SwissKnife`) fetches a value without consulting access mode — which is
exactly how `ChunkExposureTime` got its 512, through `Float → SwissKnife → chunk IntReg`. Where a node's
`pIsAvailable` predicate itself needs chunk data, the predicate cannot be answered, and that *is*
"not available" — so that case resolves to `NotAvailable` rather than throwing out of an access-mode query.
Only the chunk-tagged exception is caught there; a device refusal or a timeout still propagates.

After the fix every `Chunk*` node on the camera reports `NA`, or, where the node is readable but its value
is not, an inline error naming the port. No fabricated numbers remain.

**A caveat for anyone repeating a pixel comparison against a vendor viewer.** Two applications cannot receive
the same frame, so brightness comparisons are only as stable as the lighting. Under fluorescent light with a
3000 us exposure, this camera's frame-to-frame mean brightness swung **13.3%** peak to peak with every
automatic function off (`ExposureAuto`, `GainAuto`, `BalanceWhiteAuto` all Off, gain 0) — mains at 60 Hz
pulses the lamp at 120 Hz, and an exposure shorter than that 8333 us period samples a different part of the
cycle each frame. Setting the exposure to exactly one period collapsed the swing to **0.03%** (six frames:
106.94-106.96). The useful control when a capture "looks different from the viewer" is therefore to compare
two of *your own* consecutive frames first: here they differed by nearly as much (NCC 0.982, mean |diff|
5.54) as ours differed from the viewer's (NCC 0.878), which located the difference in the light rather than
in the transport.

### Idle-gap measurement (firewall mapping), same camera

A stream held open with acquisition stopped, then started again. Nothing else differs between the two rows.

| Idle gap | Keep-alive | Frames after restarting acquisition |
|---|---|---|
| 90 s | off | 3 of 3 (mapping still alive) |
| 300 s | off | **0 — not a single packet** |
| 300 s | on (15 s, 19 datagrams sent) | 3 of 3 |

So the host's stateful UDP mapping expires between 90 s and 300 s of silence, and the stream dies with it
until something is sent outbound. This is why `GevStreamOpt.FirewallTraversalIntervalMs` defaults to 15 s
rather than to "punch once at start": a trigger mode with long gaps, or an operator who stops acquisition and
resumes later, would otherwise see the stream go permanently silent with no error anywhere.

Three defects were found here that no simulator run had shown, each now fixed and guarded by a test:
the transport-layer lock (`TLParamsLocked`) that gates the acquisition commands, the host firewall that
silently swallowed every GVSP packet, and GenApi addresses above 32 bits that made the whole File Access
category unreadable. A fourth was cosmetic but real: a blocking receive can return `IOPending` on Windows,
which the receiver logged as an error and answered with a sleep.

### Odd-width GVSP Packed line rule — settled by measurement, and we had it wrong

`docs/protocol-notes.md` and `PixelFormatInfo.LineBytes` rounded a GVSP Packed line up to a whole 2-pixel
group (`ceil(width / 2) * 3`), because no public source settled it. The camera settles it. Reading the
device's own `PayloadSize` node at four geometries, height 64, PaddingX 0:

| Width | Format | Device PayloadSize | `w*h*bpp/8` | Group-rounded line x h |
|---|---|---|---|---|
| 2592 (even) | Mono12Packed | 248,832 | 248,832 | 248,832 |
| 2591 (odd) | Mono12Packed | **248,736** | 248,736 | 248,832 |
| 121 (odd) | Mono12Packed | **11,616** | 11,616 | 11,712 |
| 120 (even) | Mono12Packed | 11,520 | 11,520 | 11,520 |

Every value is exactly `width x height x 12 / 8`. The device does **not** align lines: had it padded each
line to a whole byte (3887 for width 2591) the frame would be 248,768, and it is not. With an odd width at
12 bits, each row starts half a byte into the buffer, so a byte-aligned stride does not exist for that frame.
Two of the public implementations checked earlier model it the same way — a continuous group stream with no
per-line padding — so the ecosystem and the hardware agree and the group-per-line rounding was our invention.

Streaming 2591x64 Mono12Packed still delivered `IsComplete = true` frames, which is the dangerous part: the
expected packet count happened to coincide (170 either way), so the receiver saw a complete frame while
reporting `PayloadSize` 248,832 and `Stride` 3888 — 96 bytes of the buffer never written by the device, and a
stride that is wrong for every row after the first. A geometry where the 96-byte over-estimate crosses a
packet boundary would instead expect a packet the device never sends, and the frame would never complete.

The model that matches the hardware, now implemented: with `PaddingX > 0` the device is padding lines, so
lines are byte-aligned and the per-line group rule applies; with `PaddingX = 0` the pixels are one
continuous run and the rounding happens once over `width x height`.

Verified on the same camera after the fix. At 2591 x 64 Mono12Packed the device reports `PayloadSize`
248,736 and the receiver now expects exactly that; 30 frames streamed with 0 incomplete, 0 missing packets
and 0 resend requests, and every saved frame is 248,736 bytes (7,462,080 for 30). `Stride` comes out **0**,
the "no byte stride" signal, and the sidecar records it. Widening to 2592 puts it back on the per-line
branch: `PayloadSize` 248,832, `Stride` 3888, 0 incomplete. Both branches were exercised against hardware,
not only against the simulator.

`PixelFormatInfo.FrameBytes` is the single definition of that and `GvspImageLeader.ImageBytes` routes
through it, so the receiver sizes a frame the way the device does. Where a line is not a whole number of
bytes and there is no line padding there is no stride at all, and saying so is part of the fix:
`PixelFormatInfo.IsLineByteAligned(code, width)` reports it, `GevFrame.Stride` is **0** for such a frame
(documented as "one continuous run of Width x Height pixels"), and `PixelUnpack.UnpackToArray` decodes the
whole image as a single run rather than line by line — which is also what makes an odd-width packed frame
decode correctly at all, since every row after the first starts part-way into a byte. Both frame sources in
the test suite (`GevSharp.Sim` and `GvspTestSender`) implement the rule independently of the library, so a
mistake on one side cannot cancel a mistake on the other, and `StreamingScenarioTests` pins the 121 x 40
case end to end (7,260 bytes, not 7,320).

### A second defect the same session found: `Slope=Varying` converters reject every write

`PixelFormat` on this camera is an `Enumeration` over an `IntConverter` (`PixelFormat_CtrlValueFao`) whose
formulas are a lookup table — a chain of ternaries ending in `0xffffffff` for "no such format" — declared
`Slope=Varying`. `ConverterLimitsAsync` derives Min and Max by pushing the target's own Min and Max through
`FormulaFrom`; both endpoints fall through to the `0xffffffff` branch, so the node reports `Min == Max ==
4294967295` and **every** write is rejected as out of range:

```
GenApiException: Value 17301505 for node 'PixelFormat_CtrlValueFao' is outside the range 4294967295..4294967295.
```

Changing the pixel format — one of the most basic operations there is — fails on this camera. Deriving limits
from the endpoints is only meaningful for a monotonic converter; `Varying` says the slope is not monotonic, so
the endpoints are not bounds.

Fixed: `ConverterLimitsAsync` returns open ends for `Slope=Varying` and does not read the target's limits at
all, so such a node reports no bounds of its own and the range check that remains is the target node's —
which is the only one with a basis. `Decreasing` and `Automatic` are unchanged. Two tests pin it, both
verified to fail without the fix: `FloatNodeTests.Converter_SlopeVarying_DeclaresNoLimits` (open bounds, the
write still lands, the target's own check still rejects an out-of-range value) and
`Converter_SlopeVarying_LookupTable_StaysWritable`, which reproduces the camera's shape — a ternary chain
whose "no such value" constant swallows both endpoints — and writes a value that is in the table.

Verified on the camera: `set PixelFormat Mono12Packed` now succeeds and reads back, and `set PixelFormat
Mono8` puts it back. Before the fix the same command failed on every value, the table's own entries included.

Simulator counterparts of scenarios 2–7 run in CI against `GevSharp.Sim` (loopback) and, on Linux,
against a third-party virtual camera.

### Crevis MG-A500M-22 (mono, 2464x2056), 2026-09-03 — a second vendor, and two defects it exposed

Bringing up a second vendor on the same host found two real defects in this library. Both were
fatal to that camera and invisible on the first one, which is the point of testing a second vendor.

**First: mutual limit references were mistaken for a value cycle.** The node map declares

    AutoGainLowerLimit.pMax -> AutoGainUpperLimit
    AutoGainUpperLimit.pMin -> AutoGainLowerLimit

which is ordinary — each node bounds the other's range. The binder counted `pMin`/`pMax`/`pInc`
as value references, so cycle detection reported

    could not build the GenApi node map
    (Reference cycle: AutoGainLowerLimit -> AutoGainUpperLimit -> AutoGainLowerLimit)

and the failure is not local: the whole map is rejected, so the camera cannot be opened at all.
Limits narrow a range without defining a value, so a cycle among them never recurses. They are
now `RefKind.Limit` — excluded from cycle adjacency, still included in invalidation. With the
fix the map binds 517 nodes and every feature reads and writes.

**Second: firewall traversal aimed at the wrong port when the device hides its source port.**
With the map bound, the device accepted the full stream-channel configuration and started
acquisition, yet not one GVSP packet reached the socket — at 9000, 1500 and 576 bytes alike, and
the fire-test packet never arrived either. The measurement that located the loss, taken across one
acquisition:

| Counter | Value |
|---|---|
| NIC received bytes during acquisition | 261,885,179 (about 420 Mbps) |
| Datagrams on the bound UDP socket | 0 |

The camera was streaming at full rate and the bytes were being consumed somewhere above the
adapter. Two suspects sit there: a stateful host firewall, and the vendor GigE filter drivers that
`Get-NetAdapterBinding` showed bound to that adapter (three of them, one per vendor whose SDK had
been installed). Unbinding all three changed nothing — still zero packets — which cleared the
filter drivers and left the firewall. A temporary inbound rule scoped to the camera subnet made
the stream appear immediately, at jumbo 9000 with no missing packets, which settled it.

The reason only this camera was affected is the port. Traversal works by sending one byte from the
stream socket to the port the device will stream *from*, so that a port-restricted firewall has a
mapping to match the returning packets against. That port is normally read from SCSP, but this
device reports SCSP as 0 throughout, including during acquisition. The previous fallback punched
the GVCP control port, which creates a mapping for the wrong port and helps nothing. Reading the
source endpoint of the datagrams directly showed what the device actually does:

| Written to SCP (host port) | Device's actual GVSP source port |
|---|---|
| 54629 | 54629 |

It mirrors the host port it was given. Traversal now falls back to that number instead of the
control port, and both cameras then stream with the firewall enabled, no inbound rule, and every
vendor filter driver unbound:

| Camera | Result |
|---|---|
| Crevis MG-A500M-22 | 2464x2056 Mono8, packet size 9000 negotiated, 3275 packets, 0 missing |
| Basler acA2500-14gm | 2592x1944 Mono8, packet size 9000 negotiated, 3390 packets, 0 missing |

That is the condition worth recording: no vendor SDK, no kernel filter driver, no firewall
exception, and both vendors stream. A filter driver would have hidden both defects rather than
fixed anything, since it bypasses the socket stack that the traversal exists to satisfy.

Three smaller facts fell out of the same session. This device never implements SCC or SCCFG —
both answer `INVALID_ADDRESS`. Its stream-channel writes are otherwise exact (SCDA, SCP and SCPS
all read back what was written). And while it saturates the link it stops answering GVCP within
about a second of `AcquisitionStart`, so a short heartbeat timeout can misread a healthy stream as
lost control; control recovers as soon as acquisition stops.
