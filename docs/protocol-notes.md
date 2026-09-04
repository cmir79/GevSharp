# Protocol notes (GVCP / GVSP / bootstrap registers)

Everything here was learned from public sources — the Wireshark `packet-gvcp.c` / `packet-gvsp.c`
dissectors, open-source implementations, and packet captures. No specification document is quoted. All
multi-byte fields are big-endian. Constants live in `GvcpConst`, `GvbsAddr`, `GvspConst`.

## GVCP (UDP, device port 3956)

### Header (8 bytes)

| offset | CMD | ACK |
|---|---|---|
| 0 | `0x42` packet type | status (u16) |
| 1 | flags | ↑ |
| 2 | command (u16) | ack command (u16) |
| 4 | payload length (u16) | payload length (u16) |
| 6 | req_id (u16) | req_id (u16) |

Flags: bit0 ack-required (`0x01`); DISCOVERY: bit4 allow-broadcast-ack (`0x10`); PACKETRESEND: bit4
extended-ids (`0x10`). An error reply may carry packet type `0x80` in byte 0 — treat the first two bytes
of every reply as the status word and check `status & 0x8000`.

### Commands and payloads

| command | CMD payload | ACK payload |
|---|---|---|
| DISCOVERY 0x0002 / 0x0003 | none | 248 bytes = bootstrap memory 0x0000..0x00F7 |
| FORCEIP 0x0004 / 0x0005 | u16 reserved, u16 MAC-high, u32 MAC-low, 12 reserved, u32 IP, 12 reserved, u32 subnet, 12 reserved, u32 gateway (56 bytes) | none |
| PACKETRESEND 0x0040 | standard: u16 stream-channel, u16 block-id, u32 first-id (24-bit), u32 last-id (24-bit) — 12 bytes. Extended (flag 0x10): u16 stream-channel, u16 reserved, u32 first-id, u32 last-id, u64 block-id — 20 bytes | none (sent with ack-required = 0) |
| READREG 0x0080 / 0x0081 | N × u32 address (N ≤ 135) | N × u32 value |
| WRITEREG 0x0082 / 0x0083 | N × (u32 address, u32 value) | u16 reserved, u16 index (number of registers written; on error the index of the failing one) |
| READMEM 0x0084 / 0x0085 | u32 address, u16 reserved, u16 count (multiple of 4, ≤ 512) | u32 address, data |
| WRITEMEM 0x0086 / 0x0087 | u32 address, data (multiple of 4, ≤ 512) | u16 reserved, u16 index (bytes written) |
| PENDING_ACK 0x0089 | — | u16 reserved, u16 time-to-completion (ms): device asks the host to wait longer for the real ACK |
| EVENT 0x00C0 / 0x00C1, EVENTDATA 0x00C2 / 0x00C3 | device → host on the message channel | host answers with an empty ACK |
| ACTION 0x0100 / 0x0101 | u32 device key, u32 group key, u32 group mask (+ u64 time when scheduled) | none |

Addresses in READREG/WRITEREG must be 4-byte aligned; READMEM/WRITEMEM lengths are multiples of 4 (read
extra and trim). Devices typically process one command at a time; keep at most one request in flight per
channel. `req_id` runs 1..65535 and wraps, skipping 0.

### Bootstrap register map (GVBS)

See `GvbsAddr`. Highlights:

- `0x0000` version (major:minor), `0x0004` device mode (bit31 big-endian device, low 16 bits character set:
  1 = UTF-8, 2 = ASCII), `0x0008`/`0x000C` MAC, `0x0024/0x0034/0x0044` current IP/subnet/gateway.
- Strings (NUL-terminated): manufacturer `0x0048`(32), model `0x0068`(32), device version `0x0088`(32),
  manufacturer info `0x00A8`(48), serial `0x00D8`(16), user name `0x00E8`(16).
- `0x0200` first URL (512), `0x0400` second URL (512) — camera XML location.
- `0x0934` GVCP capability bits (bit2 packet resend, bit5 pending ack, bit29 heartbeat disable, …),
  `0x0938` heartbeat timeout (ms), `0x093C/0x0940` timestamp tick frequency (Hz, 64-bit),
  `0x0944` timestamp control (write 2 = reset, 1 = latch), `0x0948/0x094C` latched timestamp.
- `0x0A00` CCP: 1 = exclusive, 2 = control, 4 = control switchover enable; 0 = open. Writing CCP requires
  no privilege when the register is 0; writes from a non-controlling host return `ACCESS_DENIED (0x8006)`.
  `0x0A04`/`0x0A14` primary application port/IP (device fills these from the CCP writer's socket).
- Stream channel n (n = 0 first): base `0x0D00 + 0x40·n`: `+0x00` SCP host port (write 0 to close),
  `+0x04` SCPS (bit31 fire test packet, bit30 do-not-fragment, bit29 big-endian payload, bits0–15 packet
  size in bytes including IP+UDP headers), `+0x08` SCPD inter-packet delay (timestamp ticks), `+0x18`
  SCDA destination IPv4, `+0x1C` SCSP source port, `+0x20` capability, `+0x24` configuration.
- `0x9000` manifest table (optional, for multiple XML versions).

### Sequences

**Discovery.** For each interface: bind `(ifaceIp, 0)`, `EnableBroadcast`, send DISCOVERY_CMD (flags
`0x11`) to `255.255.255.255:3956` and `<directed-broadcast>:3956`, repeat once after ~200 ms, collect
DISCOVERY_ACKs until the timeout. Replies come from `<deviceIp>:3956`. Unicast probe: same packet to
`<ip>:3956` from an unbound-address socket.

**Open / control.** Read the identity block field by field — READREG for each 32-bit register (version, mode,
MAC, IP configuration, current IP/subnet/gateway) and READMEM at each string's own address. A single bulk
READMEM of 0x0000..0x00F7 is not a byte image on every device: some leave reserved words (0x0018..0x0023,
0x0028..0x0033, 0x0038..0x0043) unimplemented, answer INVALID_ADDRESS to a READREG there, and drop those
words from a bulk reply while pulling the rest forward — every field from 0x0024 on then appears shifted
(observed on a real camera; reproduced by the simulator's reserved-word-holes option). Serial number and
user-defined name are optional registers (GVCP capability bits 30/31); a refused read leaves them empty.
→ WRITEREG CCP=2 (or 3 for exclusive, +4 for switchover)
→ WRITEREG heartbeat timeout → heartbeat: READREG CCP every `timeout/3`; any GVCP command from the
controlling socket resets the device-side timer. On close: WRITEREG CCP=0.

**Changing the addressing.** Write the mode to `CurrentIpCfg` (0x0014) and, for a fixed address, the
persistent registers (0x064C/0x065C/0x066C); read them back before doing anything else, because a device
may acknowledge a write it did not take. Then release control (CCP = 0) and close.

Write what the operator chose, not what the device currently holds: `Persistent | LLA` for a fixed
address, `DHCP | LLA` for DHCP, masked by the supported-configuration register (0x0010, a capability, not
a state). Deriving the value by clearing and setting bits in the register just read lets whatever the
device did in between leak into the write. Keep LLA on — it is the last resort when everything else
fails, and both vendor tools were captured writing exactly 0x05 and 0x06.

The written mode only decides what the device does *the next time it configures its interface*, so
something has to make it do that. That something is **FORCEIP**, sent after the control channel is
released (a device ignores it while an application holds control) and after a short pause:

- a **fixed address** → FORCEIP with that address. Moving the interface is itself the restart, and it
  says where to go, so a device that refused the persistent registers still lands where it was asked
  instead of drifting to link-local.
- **DHCP** → FORCEIP with IP `0.0.0.0`. There is no address to move to, and losing the current one is
  what makes the device bring its interface up again — this time from DHCP. The mask and gateway fields
  are ignored; only the zero IP is the signal.

No device reset is involved, and that is not a workaround. Two vendors' own configuration tools were
captured, on two cameras, in both directions, and **neither used a device reset — including on the camera
that does expose a `DeviceReset` command**. It is also far cheaper: measured 0.5 s and 2.3 s from the
FORCEIP to the first DISCOVERY_ACK on the new address, against a full boot.

Hold one control session at a time. Two sessions of the same application on one camera — an apply
holding it exclusively while a background read opens it again — end with the second one writing CCP = 0
as it closes, which takes control away from the first; its remaining writes are refused and the whole
apply completes having written nothing. The symptom is misleading: a FORCEIP sent earlier in the same
operation already moved the address, so the camera looks half-configured (address taken, mode not) as
if the device had rejected the mode. It had not been asked.

(One vendor tool writes the configuration through a proprietary GVCP command, `0x8004`/`0x8005` with a
76-byte payload, and never opens a control channel at all. The standard registers work on the same
camera, so there is no reason to imitate that.)

**Stream setup.** Bind UDP `(hostIp, port)`; set `SO_RCVBUF`; negotiate SCPS: for candidate size S write
`SCPS = 0x80000000 | S`; the device sends one test packet of that size to SCDA:SCP (so SCDA/SCP must be
written first for the test, or the device uses the GVCP source); wait ≤ 100 ms; success → keep S. Then
write `SCPS = S`, optional SCPD, `SCDA = hostIp`, `SCP = port`. Acquisition is started through the GenApi
`AcquisitionStart` command (not by the stream). Stop: WRITEREG SCP = 0 (device stops sending).

**Resend.** Host detects a hole in packet ids for block B (or a trailer with missing packets) → after a
short grace (reordering) sends PACKETRESEND(channel 0, B, first, last) from the GVCP control socket
(devices honour resends only from the controlling application). Resent packets carry status `0x0100`.
Status `0x800C` (packet unavailable) means the device no longer has it — abandon that hole.

## GVSP (UDP, device → host, port = SCP)

### Packet header

Standard (8 bytes): `status u16 | block_id u16 | packet_infos u32`.
`packet_infos`: bit31 EI (extended id mode), bits 30–24 content type, bits 23–0 packet id.

Extended (20 bytes, EI = 1): `status u16 | flags u16 | packet_infos u32 | block_id u64 | packet_id u32`.
The EI bit is still read from `packet_infos` bit31; content type from bits 30–24; the packet id comes from
the separate u32 field. Devices enable extended ids through SCCFG (bit for extended id mode) — the host
handles both forms per packet by testing the EI bit.

Content types: 1 leader, 2 trailer, 3 payload, 4 all-in (leader+data+trailer in one packet), 5 H.264,
6 multi-zone, 7 multi-part, 8 GenDC. Block id 0 is reserved; devices count 1..65535 and wrap (64-bit in
extended mode, no wrap in practice).

### Image leader (packet id 0, 36 bytes of data)

`flags u16 | payload_type u16 | timestamp u64 | pixel_format u32 | size_x u32 | size_y u32 | offset_x u32 |
offset_y u32 | padding_x u16 | padding_y u16`. `payload_type & 0x3FFF`: 1 image, 2 raw, 3 file, 4 chunk,
5 extended chunk (image + chunks), 6 JPEG, 7 JPEG2000, 8 H.264, 9 multi-zone, 10 multi-part, 11 GenDC;
bit14 set = chunk data appended after the image.

### Payload (packet ids 1..N)

Header then image bytes. Every payload packet except the last carries `dataBytes = SCPS − 28 − headerLen`.
Frame offset of packet id p = `(p − 1) × dataBytes`, and `N = ceil(bytes / dataBytes)`, where `bpp =
(pixel_format >> 16) & 0xFF` and the image byte count depends on whether lines are padded:

- `padding_x > 0` — lines are separately addressable, so each one is rounded up to a whole byte (and, for
  group formats, to a whole group: GVSP Packed 2 px = 3 bytes, 4:1:1 4 px = 6 bytes). Bytes =
  `size_y × (lineBytes(size_x) + padding_x) + padding_y`.
- `padding_x = 0` — the pixels are one continuous run and lines are **not** aligned. Bytes =
  `lineBytes(size_x × size_y) + padding_y`, i.e. the rounding happens once over the whole image.

The two differ only at widths whose line is not a whole number of bytes (odd widths in the packed
formats, and the PFNC `p` formats at most widths). Measured on a real camera at four geometries: 2591 × 64
Mono12Packed is 248,736 bytes, not the 248,832 a per-line rounding predicts. At such a width there is no
byte stride — each row starts part-way into a byte — and `GevFrame.Stride` reports 0 to say so.

### Chunk payloads (measured, not from a specification)

A device with chunk mode on does not append metadata to an image — it changes what the payload *is*.
Measured on a Basler acA4112-8gc (`ChunkModeActive`, then one chunk enabled):

| State | `PayloadSize` | Leader |
|---|---|---|
| chunk mode off | 12,288,000 (= w × h × 1) | 36 bytes, `payload_type` 1, full geometry |
| chunk mode on, nothing enabled | 12,288,036 (+36) | **12 bytes**, `payload_type` **4**, no geometry |
| + `Timestamp` chunk enabled | 12,288,052 (+16) | as above |

The `+16` for one `u64` value is the shape of the framing: 8 bytes of value and an 8-byte trailer holding
the chunk id and its length. So the payload is a sequence of entries, each ending with its own id/length,
and a reader walks it **from the end**. The image becomes one entry among the others, which is why the
leader can drop the geometry: the size is known only once the last packet arrives, and *where the image is*
only once the entries have been walked.

`payload_type` 5 (extended chunk data) is the other shape: the leader keeps the image geometry and chunks
follow the image. GevSharp assembles 1 and 5; it does not assemble 4 (see `docs/architecture.md`).

**The chunk contents are vendor-defined; only the framing is common.** On this camera the image chunk
carries its own ~20-byte header (offset x/y, size x/y, pixel format, stride) and the XML says which bits are
which — `ChunkWidth` resolves to a `MaskedIntReg` at address 0x8 on the `ExtImagePort`, whose `<ChunkID>` is
the vendor value `9efc2cdb`. Nothing in the naming convention fixes those offsets, so the byte layout cannot
be hard-coded: the device's own XML is the map. Of this camera's node names, 374 are `NameSpace="Standard"`
and 1,504 are `Custom` — the convention standardises names and mechanism, not content.

### Trailer (packet id N + 1, 8 bytes of data)

`reserved u16 | payload_type u16 | size_y u32` (actual height for variable-height acquisitions).

### Status

Same code space as GVCP: `0x0000` ok, `0x0100` resent packet, `0x8xxx` error (`0x800C` packet unavailable
answers an unfulfillable resend; `0x800D` data overrun; `0x8015` overflow). Error packets carry the block
and packet ids they refer to.

### Chunk data

When a frame carries chunks, the payload ends with a sequence of chunk records read backwards from the
end: `chunk_length u32` (last 4 bytes) preceded by `chunk_id u32`, preceded by `chunk_length` bytes of
chunk data; repeat until the image area begins. The GenApi XML maps chunk ids to `ChunkAdapter` register
spaces (`<Port ChunkID="…">`). GevSharp recognises the payload types and validates lengths; chunk parsing
into a node map is a later milestone.

## GenApi essentials used by the runtime

- Root element `<RegisterDescription>` with `ModelName`, `VendorName`, `SchemaMajorVersion` (1),
  `SchemaMinorVersion` (0/1), `MajorVersion`, `MinorVersion`, `SubMinorVersion`, `ProductGuid`, `VersionGuid`,
  `StandardNameSpace` (`GEV`/`IIDC`/`CL`/`USB`/`None`).
- `<Group Comment="…">` groups are transparent; nodes inside are ordinary top-level nodes.
- A `<Category>` lists `<pFeature>` children; the `Root` category is the tree entry.
- Value indirection: `<pValue>` (typed node), `<pValueCopy>` (extra targets on write), `<pValueDefault>`
  (fallback when no `pValue` resolves).
- Register addressing: `<Address>` (hex `0x` or decimal), `<pAddress>` (integer node added), `<pIndex
  Offset="…" | pOffset="…">` (index × offset added), `<IntSwissKnife>` inline; `<Length>` / `<pLength>`;
  a resolved address may exceed 32 bits — a Basler ace declares its file-access base as
  `<Value>0xffffd0000000</Value>` — but GVCP carries only a 32-bit address, so the low 32 bits are the real
  register (0xD0000000 there, confirmed against the camera). Narrow it and warn; an access whose *end* leaves
  the 32-bit space is a genuine error (vendors use 0xFFFFFFFF as a "not present" sentinel for features the
  model does not have).
  `<AccessMode>` RO/WO/RW; `<Cachable>` NoCache/WriteThrough/WriteAround; `<PollingTime>` ms; `<pPort>`.
- Integer registers: `<Sign>` Signed/Unsigned, `<Endianess>` LittleEndian/BigEndian (default LittleEndian
  in GenApi even though GVCP is big-endian — vendors set BigEndian explicitly), `<LSB>`/`<MSB>`/`<Bit>` for
  MaskedIntReg (bit 0 = MSB of the register for BigEndian, = LSB for LittleEndian).
- Formulas: `<Formula>` (SwissKnife), `<FormulaTo>`/`<FormulaFrom>` (Converter; variables `FROM` / `TO`),
  `<pVariable Name="X">Node</pVariable>`, `<Constant Name="X">v</Constant>`, `<Expression Name="X">…</Expression>`.
- Enumerations: `<EnumEntry Name="…"><Value>n</Value><Symbolic>Text</Symbolic><NumericValue>…</NumericValue></EnumEntry>`,
  `<pSelected>` on selector features (the selected features are those listed).
- Guards: `<pIsImplemented>`, `<pIsAvailable>`, `<pIsLocked>` (Integer/Boolean/SwissKnife nodes: non-zero = true),
  `<ImposedAccessMode>`, `<pInvalidator>` (nodes whose write invalidates this node's cache), `<Streamable>`.
- Commands: `<CommandValue>` / `<pCommandValue>` written to `<pValue>`; `<PollingTime>` marks self-clearing bits.
- Booleans: `<OnValue>` / `<OffValue>` (default 1 / 0).
- Strings: `<StringReg>` fixed `Length`, `<String>` literal or `pValue`.
- Floats: `<FloatReg>` Length 4/8 IEEE; `<Converter>`; `<Float>` with `Min/Max/Inc/Unit/Representation/DisplayNotation/DisplayPrecision`.
