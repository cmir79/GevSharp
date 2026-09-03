# Design requirements

Derived from a code-level survey (2026-09-02) of six public .NET/C implementations. Each item below is a
place where an existing implementation broke in practice; GevSharp treats them as acceptance criteria.

The status table at the end records where each requirement lives and what would fail if it were removed.
It is verified by deleting the behaviour and running the suite, not by reading names — "implemented" and
"guarded" are different claims and the table keeps them apart.

| # | Requirement | Failure it prevents |
|---|---|---|
| R1 | Frames are leased from a pool; the receiver never writes into a buffer the consumer holds. | A popular .NET library handed the consumer a buffer it kept overwriting. |
| R2 | GVSP packet resend is implemented and on by default; reassembly is offset-based (`(id-1) × dataBytes`), never sequential concatenation. | Two libraries had no resend; one dropped a whole frame on a single lost packet. |
| R3 | Incomplete frames are dropped and counted by default; delivery is an explicit opt-in flag and the frame says `IsComplete = false`. | One implementation let a single incomplete frame stall the stream. |
| R4 | GVCP responses are matched to requests by `req_id` and expected ACK command; requests are serialized; heartbeat shares the same discipline. | Concurrent heartbeat + user request read each other's replies. |
| R5 | `req_id` is 16-bit, never 0, thread-safe. | Reserved 0 used; unsafe increment. |
| R6 | `<Group>` elements are recursed at any depth. | Node maps came out empty for many vendors. |
| R7 | Selectors (`pSelected`), guards (`pIsImplemented/pIsAvailable/pIsLocked`), `pInvalidator`, Converter/IntConverter, Boolean-over-register, Float-over-integer writes are all implemented. | Whole feature families silently did nothing. |
| R8 | Formula errors (division by zero, bad syntax, missing variable) throw; never return 0. | A C reference implementation returns 0 on evaluation errors. |
| R9 | Formula evaluation has bounded recursion and cycle detection over `pValue`/`pVariable` graphs. | Stack overflow on cyclic XML. |
| R10 | MaskedIntReg bit numbering follows the register endianness convention (see architecture). | LSB/MSB inverted; valid XML rejected. |
| R11 | Packet size is negotiated (fire-test) up to the NIC MTU; SCPS and SCPD are never hard-coded. | Fixed 1500 → no jumbo frames; fixed delay → no throughput control. |
| R12 | Stream socket buffer is configurable and the granted size is logged; receive loop is a dedicated thread with no per-packet allocation. | Async-per-packet loops and per-packet allocations caused drops under load. |
| R13 | Discovery enumerates every interface and sends both limited and directed broadcast; unicast probe exists for cross-subnet/loopback. | Camera-only NICs were never discovered. |
| R14 | Truncated discovery replies are skipped, not turned into ghost devices. | Ghost entries with IP 255.255.255.255. |
| R15 | Bootstrap registers are read by GVBS offset, never by GenApi feature name. | Feature-name lookups NRE'd on vendors with different names. |
| R16 | Stride is carried on the frame (`Width × bpp/8 + PaddingX`) and consumers must use it; `Stride = 0` says the lines are not byte-aligned and the frame is one continuous run. | Devices with line padding decoded skewed. |
| R17 | Bits-per-pixel comes from the PFNC code (`(code >> 16) & 0xFF`), and packed formats have explicit line-size math. | A packed format computed 10 bpp while its code says 12. |
| R18 | GVSP header parsing masks the content-type bits and honours the extended-ID flag. | Packets with flag bits set matched no case and were dropped silently. |
| R19 | Chunk-data payloads are recognised and, if not parsed, length-validated and flagged rather than treated as plain images. | Chunk frames skipped length validation. |
| R20 | Opening a device never rewrites acquisition-related features (exposure, gain, trigger…). | A library reset user settings on every start. |
| R21 | Heartbeat runs for the whole control session, not only while streaming; `DisposeAsync` releases CCP. | Control lost during idle; devices stayed locked for the timeout after exit. |
| R22 | No commercial or copyleft transitive dependency; the formula engine is in-house. | mXparser (commercial) and Prism (revenue-capped) leaked into consumers. |
| R23 | No vendor XML committed; fixtures are hand-authored. | Confidential vendor XML redistributed in a public repo. |
| R24 | Repository-wide CRLF via `.gitattributes`; no mixed line endings. | Mixed endings turned one-line edits into whole-file diffs. |
| R25 | Cached camera XML is opt-in and written to a caller-chosen directory with a stable name. | XML copies piled up next to the executable on every connect. |
| R26 | Register access from GenApi is async end-to-end; no sync-over-async. | Thread-pool starvation under load. |

## Status

Verified 2026-09-03 against the tree at that time. `met` = implemented **and** a named test fails when the
behaviour is deleted. `met-untested` = implemented, but deleting it leaves the suite green — the requirement
holds today and nothing would notice a regression. `partial` = some cases guarded, others not.

| # | Status | Implemented at | Guarded by |
|---|---|---|---|
| R1 | met | `Gvsp/GevFramePool.cs:67,108`, `GevFrame.cs:126,134`, `GevStream.Receiver.cs:697,772` | `GevFramePoolTests.RentsUpToCountThenFails`, `ReturnWithStaleVersionIsIgnored`, `GevStreamTests.PoolExhaustionDropsNewFramesWithoutTouchingHeldBuffers`, `StreamingTests.PoolExhaustion_DropsNewFrames_AndNeverTouchesHeldBuffers` |
| R2 | met | `GevStream.Receiver.cs:781` (offset copy), `:1036`, `:1140`; `GevStreamOpt.ResendEnabled = true` | `GevStreamTests.DroppedPayloadPacketsAreRecoveredByResend`, `LostLeaderIsRequestedAndRecovered`, `LostTailIsRequestedAfterSilence`, `StreamingScenarioTests.LargeFrames_WithScatteredDrops_AreRecoveredByResend_OneRequestPerHole` |
| R3 | met | `GevStream.Receiver.cs:1290,1327,1341,1352` | `StreamingTests.DroppedPackets_WithoutResend_IncompleteFramesAreCountedAndNotDelivered`, `DroppedPackets_DeliverIncomplete_FlagsFramesAndZeroesTheHole`, `GevStreamTests.UnrecoverableHoleDropsTheFrameWithDiagnostics` |
| R4 | met | `Gvcp/GvcpChannel.cs:128,398,418`; heartbeat uses the same `RequestAsync` (`GevDevice.cs:197`) | `GvcpChannelTests.ConcurrentRequestsAreSerializedAndNeverCrossTalk`, `ReplyWithWrongReqIdIsDroppedAndCounted`, `DeviceControlTests.ConcurrentRegisterAccess_IsSerialized_WithoutCrossTalk` |
| R5 | met | `Gvcp/GvcpChannel.cs:244-251` (`Interlocked`, skips 0) | `GvcpPacketTests.ReqIdSkipsZeroAndWraps`, `GvcpChannelTests.ReqIdsAreNonZeroDistinctAndEchoedByTheDevice` |
| R6 | met | `GenApi/Model/GenApiXmlParser.cs:189-197` (recurses on `Group`), depth bounded at `:25,79` | `GenApiXmlParserGroupsTests.NodesInsideNestedGroupsAreTopLevel` (fixture nests three deep) |
| R7 | met | selectors `NodeBinder.cs:157` + `GenApiNodeMap.Runtime.cs:104,128`; guards `NodeBase.cs:127-146`; `ConverterNode`, `IntConverterNode`, `BooleanNode`, Float-over-integer | `CacheInvalidationTests.Selector_*`, `PInvalidator_*`, `GuardTests.PIs*`, `FloatNodeTests.Converter_RoundTripsThroughIntegerTarget`, `OtherNodeTests.Boolean_OverRegister_UsesOnOffValues` |
| R8 | met | `GenApi/Formula/FormulaOps.cs:117,122,132,137,149` — no catch-and-return-0 anywhere in `Formula*.cs` | `FormulaTests.RuntimeErrorsThrowGenApiException` (46 rows), `FloatNodeTests.SwissKnife_DivisionByZero_Throws` |
| R9 | met | `FormulaParser.cs:270,277` (depth 200); `NodeBinder.cs:179-239` — **iterative** DFS, so cyclic XML cannot overflow the stack | `NodeMapBindTests.ReferenceCycle_IsDetectedAtBind`, `FormulaTests.DeepParenthesesAreRejectedWithoutStackOverflow` |
| R10 | met | `GenApi/Runtime/IntegerNodes.cs:365-380` (`FieldOf` flips LSB/MSB for BigEndian) | `IntegerNodeTests.MaskedIntReg_BigEndian_Bit0_IsMostSignificantBit`, `MaskedIntReg_LittleEndian_Lsb0Msb7_IsLowByte`, `MaskedIntReg_BitBeyondRegister_FailsAtBind` |
| R11 | met | `Gvsp/GevStream.PacketSize.cs:26-65,78,157`; SCPD from the option only | `PacketSizeNegotiationTests.*`, `StreamingScenarioTests.Start_AccessesChannelRegistersInTheDocumentedOrder_AndStopReversesIt` |
| R12 | met | `Gvsp/GevStream.cs` (granted socket buffer read back and logged, dedicated receiver thread), `Receiver.cs` (one reusable scratch buffer, pooled slots and frame buffers) | `ReceiverAllocationTests` — the receiver's per-datagram work is called on the test thread (`FeedPacketForTest`), so `GC.GetAllocatedBytesForCurrentThread` can see it: 0 bytes across 700+ packets, 0 bytes for late/duplicate packets of a closed block, and completing a frame allocates only the `GevFrame` object (≤ 256 bytes, not the 64 KiB image). Mutation-checked: one unguarded interpolated `GevLog.Debug` on the packet path fails both. `GvcpChannelTests.PacketResendDoesNotAllocateOnTheHotPath` guards the GVCP side |
| R13 | met | `Gvcp/GevDiscovery.cs` — `SelectInterfaces` enumerates every up IPv4 interface, `BuildTargets` forms the limited and directed broadcasts; `GevNet.cs` (`GetIpv4Interfaces`, `DirectedBroadcast`) | `GevDiscoveryTests.Probe*`, `DiscoverCollectsRepliesFromEveryTargetAndDedupesByMac`, and `DiscoveryBroadcastTests` (11 cases: per-mask directed address, unknown mask, /0 collapsing onto the limited one, unicast appended not substituted, loopback opt-in enumeration, and both broadcasts observed leaving the socket) — mutation-checked: forcing `DirectedBroadcast` off fails 7 |
| R14 | met | `GevDiscovery.cs:214-218,284-288`; `GevDeviceInfo.cs:52` | `GevDiscoveryTests.DiscoverSkipsTruncatedRepliesInsteadOfCreatingGhosts`, `ProbeSkipsTruncatedDiscoveryAck` |
| R15 | met | `Gvcp/GvbsAddr.cs` (the offset table), `GevDeviceInfo.cs:55-101`, `GevDevice.cs:113-116` — the only name lookup in the library is `TLParamsLocked`, which is not a bootstrap register | `GevDeviceInfoReadTests.EveryFieldIsReadAtItsOwnAddress`, `OpenIsNotFooledByADeviceWhoseBulkReadSkipsUnimplementedWords` |
| R16 | met | `GvspPacketView.cs` (`LineBytes`, `IsLineByteAligned`, `ImageBytes`), `GevStream.Receiver.cs` (stride 0 when a line is not byte-aligned), `GevFrame.cs:54,87`; `PixelUnpack` takes a stride | `GevStreamTests.StrideAndPayloadSizeFollowPixelFormatAndPadding`, `StreamingScenarioTests.PixelFormat_GvspPackedFormats_AtOddWidth_CarryTwoPixelsInThreeBytes`, `PixelFoldTests.LeaderStrideFeedsFoldAndUnpackDirectly` |
| R17 | met | `Pfnc/PixelFormatInfo.cs` — `BitsPerPixel` is `(code >> 16) & 0xFF`; `PackedBytesLong` is the one group-based size rule, and `FrameBytes` applies it per line or once over the image depending on `paddingX` | `PixelFormatInfoTests.BitsPerPixelComesFromCode` — pins `Mono10Packed` at bpp 12 and depth 10, the exact surveyed defect; `FrameBytesAddsPadding` pins the measured 2591 × 64 geometry |
| R18 | met | `GvspPacketView.cs:76-97`; masks at `GvspConst.cs:20-23` | `GvspPacketViewTests.ContentTypeIgnoresExtendedIdBit`, `GevStreamTests.CompleteFramesAreDeliveredInOrder(extendedIds)` |
| R19 | met | `GvspPacketView.cs:163,166`; `GevStream.Receiver.cs` (leader, overflow-and-grow, `FinalizePayloadSize`, `ZeroHoles`); `GevFrame.HasChunkData` | `GvspPacketViewTests.ExtendedChunkLeaderCountsAsChunkData` and `ChunkStreamingTests` (both chunk payload types delivered whole, the no-flag control cut to the leader size, overflow dropped then the pool grown, an incomplete chunk frame's valid bytes and zero-fill) — mutation-checked: forcing `hasChunk = false` fails 4 of the 5 |
| R20 | met | `GevDevice.cs:111-161` — `InitAsync` writes only CCP and the heartbeat timeout; a read-only session writes nothing | mutation-checked: adding one stray feature write to `InitAsync` fails 26 tests |
| R21 | met | `GevDevice.cs:159` (heartbeat starts at open, not at streaming), `:278-319` (`DisposeAsync` releases CCP), `:130` (`_ccpWriteSent` set before the write) | `DeviceLifecycleTests.Heartbeat_KeepsControlForThreeDeviceTimeouts`, `Dispose_ReleasesCcp_AndLetsTheNextSessionTakeControl`, `DeviceControlTests.Open_CancelledWhileTheCcpWriteIsInFlight_StillReleasesControl` |
| R22 | met | `GevSharp.csproj:25-32`; the formula engine is in `GenApi/Formula/` | `RepositoryPolicyTests.OnlyTheAllowedPackagesReachTheLibrary_IncludingTransitiveOnes` reads the restored `project.assets.json` for all three TFMs and compares the whole closure with an allow-list — mutation-checked: adding a `PackageReference` to Newtonsoft.Json and restoring fails it |
| R23 | met | four hand-authored fixtures; the vendor corpus is opt-in behind `GEVSHARP_VENDOR_XML` | `RepositoryPolicyTests.NoXmlOrZipAssetIsCommittedBeyondTheHandWrittenFixtures` compares `git ls-files` for XML/ZIP with the four fixtures, in both directions — mutation-checked: staging one extra `.xml` fails it |
| R24 | met | `.gitattributes:2` | `RepositoryPolicyTests.EveryTrackedTextFileIsStoredWithLfAndCheckedOutAsCrlf` asserts every tracked text file is `i/lf` (binaries `i/-text` are exempt) — the index judgment, not a byte count, because a stray CR can fold in the clean filter and still reach the commit; mutation-checked: staging a file with a doubled CR shows `i/mixed` and fails it |
| R25 | met | `GevDeviceOpt.XmlCacheDir` (null = off), `Xml/GevXmlLoader.cs:119-137,169-177,420-442` | `GevXmlLoaderTests.NoCacheDirMeansNothingIsWritten`, `CacheFileNameIsSanitizedAndStable`, `CacheMissWritesFileAndHitSkipsDeviceXmlRead` |
| R26 | met | `IGevPort` has no sync surface; `RegisterCore.cs:147,180` await the port | `RepositoryPolicyTests.TheLibraryNeverBlocksOnAnAsyncResult` scans every library source for `.Result`, `.Wait()`, `GetAwaiter().GetResult()`, `Task.WaitAll/WaitAny` and `RunSynchronously()` (`Task.Run` is allowed — moving a blocking join or socket wait off the caller is not the same thing) — mutation-checked: planting one `.Result` fails it |

The four *policy* requirements — "no commercial dependency", "no vendor XML", "CRLF", "no sync-over-async" —
are properties of the repository rather than runtime behaviours, so they are guarded by
`RepositoryPolicyTests`, which reads the repository itself (the restored dependency closure, `git ls-files`,
`git ls-files --eol`, and the library sources). Each of the four was checked against a planted violation, so
none of them is a checker that quietly passes. Running in the test suite means they run in CI on all three
operating systems; outside a checkout the git-backed ones say so and pass rather than pretending to check.

No row is `met-untested` any more: every requirement has a named test that fails when the behaviour is removed.
