# Audit fix status

Baseline: `0b79fb9c135ffb5a510b41319fc3c53cea2d3ac6` (tree audited as `e2b02f9f179d6f2e9a170812c2e34adc054972b7`), plus the directive commit `2edd057c52fb47111e206b379704f4e6a6292977`. The directive file is unchanged.

Package A fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`.
Package B fix commit: `1ba3cf78dd242141d8605286ac2a45101ad737c0`.

Executed on this host against the package B tree: Linux, .NET SDK 10.0.112, 2026-10-03.

```text
dotnet build AutoVpn.slnx -c Release
dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release
```

`AUTOVPN_MIHOMO_PATH` was unset. Result of the unit-test project: 57 passed, 1 skipped, 0 failed, duration 1 s. The skip is `PinnedLinuxCoreValidatesSyntheticNonTunProfileWhenProvided`: native `mihomo -t` is NOT_RUN, not a pass. This command is not `dotnet test AutoVpn.slnx`. The earlier package A solution run was 47 passed and 1 skipped. The Release build of the solution after package B was 0 warnings and 0 errors; Desktop was compiled and not executed. No Windows process was started. The local archive `a4c142f9…` predates both commits.

States used here: `OPEN`, `IN_PROGRESS`, `IMPLEMENTED_NOT_VALIDATED`, `BLOCKED`, `VERIFIED`.

## F01

Status: `BLOCKED`

Fix commit: none

Production paths: `src/AutoVpn.Service/Program.cs` still constructs `UnavailableNetworkGuard` and `RefusingCoreController`.

Regression IDs: AT01 not written. The guard's reason is covered only as a refusal (`BehaviorTests`, `UnavailableNetworkGuard.PlatformReason`).

Environment: Linux unit tests. Windows was not available.

Evidence: this file. No VPN session was started.

Remaining limitation: closure needs an authorized disposable Windows machine, a real core supervisor, and the protection contract from F10. Refusing on this host stays correct.

## F02

Status: `IN_PROGRESS`

Fix commit: `1ba3cf78dd242141d8605286ac2a45101ad737c0`. Tree-shape checks remain in the package A commit.

Production paths: `src/AutoVpn.Infrastructure/Refresh/CatalogueCoordinator.cs`, `SourceLedger.cs`, `src/AutoVpn.Infrastructure/Fetch/ReviewedRegistry.cs`, `PolicyHttpFetcher.cs`, `src/AutoVpn.Infrastructure/Probe/NonTunCoreProbeTransport.cs`, `src/AutoVpn.Desktop/MainWindow.xaml.cs`. The privileged service in `src/AutoVpn.Service/Program.cs` still does not parse subscriptions or call the coordinator.

Regression IDs: AT01 not written as a fresh-install journey. `PackageBTests.FreshImportStaysPendingUntilARealCoreProbeAndConsentSurvivesRestart`, `RefreshDownloadConcurrencyStaysBounded`, `IncompleteDiscoveryDoesNotBecomeASubscriptionRefresh`, `MissingCoreDoesNotReportASuccessfulProbe`. `BehaviorTests.TruncatedTreeIsNotACompleteCatalogue` still holds.

Environment: Linux unit tests, SDK 10.0.112, Mihomo unset. The import used an in-process HTTP handler. No public endpoint was contacted. The desktop process was not started.

Evidence: those tests in the 57-pass run above. A fresh SQLite catalogue stored one `Pending` node, `Eligible` was empty, and a probe with no runnable core left `Attempted` at 0. Raw 500 then GitLab 200 was the mirror fallback inside that handler. Download concurrency stayed in 1..3. A non-executable stand-in returned `CORE_START_FAILED`. The probe YAML contained `listen: 127.0.0.1` and did not contain `tun:`.

Remaining limitation: no node became `Healthy`. The success path that starts the pinned Mihomo and reads a probe target through its SOCKS port was not run. The service catalogue is still separate from the desktop catalogue. AT01's unseeded connect journey is open.

## F03

Status: `IN_PROGRESS`

Fix commit: `1ba3cf78dd242141d8605286ac2a45101ad737c0`

Production paths: `src/AutoVpn.Application/UiSession.cs`, `src/AutoVpn.Desktop/MainWindow.xaml`, `MainWindow.xaml.cs`. Consent is `Consent.AcceptDisclosure` into the catalogue. IPC null, timeout, and `IOException` use `UiSessionReducer.BrokerUnreachable`.

Regression IDs: AT01, AT30, AT33 not written as UI journeys. `PackageBTests.FreshImportStaysPendingUntilARealCoreProbeAndConsentSurvivesRestart` and `ScheduleClampAndUnknownSessionDoNotClaimDisconnect`.

Environment: Linux unit tests. WPF was compiled in the Release solution build and not executed. No click or keyboard pass.

Evidence: disclosure starts false and survives a SQLite reopen after `AcceptDisclosure`. Unchecking the box is not a revoke. `ConnectAllowed(false, true)` is false. A connected protected session that then loses the broker stays phase `Unknown`, `ClaimsVerifiedDisconnect` false, and `PlanExit` refuses to close. A later snapshot with phase `Disconnected` and protection disarmed is the verified disconnect.

Remaining limitation: the desktop and the broker still have separate catalogues. A live pipe loss while the service remained connected was not executed. First-run was not clicked. Explicit Exit in the window was not exercised.

## F04

Status: `OPEN`

Fix commit: none

Production paths: `src/AutoVpn.Infrastructure/Broker/LocalIpcServer.cs`.

Regression IDs: AT02, AT03 not written.

Environment: existing same-user pipe test only.

Evidence: `BehaviorTests.LocalPipeRejectsASecondOwnerAndReturnsSnapshot` does not exercise a second Windows user or a held-open frame.

Remaining limitation: one client can still occupy the accept loop. Package C.

## F05

Status: `BLOCKED`

Fix commit: none

Production paths: `src/AutoVpn.Service/Program.cs` still stamps `windows-user` on Windows. Linux `SO_PEERCRED` remains same-uid.

Regression IDs: AT04 not written.

Environment: Linux.

Evidence: `docs/ARCHITECTURE.md` description still matches the code.

Remaining limitation: two Windows users, a real service identity, and a spoofed server were not available.

## F06

Status: `OPEN`

Fix commit: none

Production paths: `src/AutoVpn.Application/IpcDispatcher.cs`, `src/AutoVpn.Infrastructure/Broker/BrokerEngine.cs`.

Regression IDs: AT05 not written.

Environment: existing replay test only.

Evidence: `BehaviorTests.DispatcherRejectsRemoteReplayForbiddenAndUnknown`.

Remaining limitation: lease renewal and restart idempotency are not closed.

## F07

Status: `OPEN`

Fix commit: none

Production paths: `src/AutoVpn.Infrastructure/Broker/BrokerEngine.cs`.

Regression IDs: AT06 not written. `BehaviorTests.StaleRevisionDoesNotDisconnectAndCancelDuringStartStopsTheCore` covers one in-process cancel.

Environment: Linux unit test.

Evidence: that test.

Remaining limitation: confirmation is not bound to a specific core attempt across a real process.

## F08

Status: `VERIFIED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `src/AutoVpn.Domain/Failover.cs`, `src/AutoVpn.Infrastructure/Broker/BrokerEngine.cs`. `HoldProtected` applies `CoreExited` and leaves phase `Reconnecting` with protection armed.

Regression IDs: AT07. `AuditRegressionTests.At07CoreExitAtTheSwitchCapHoldsProtectionInsteadOfStayingConnected`. `BehaviorTests.SwitchBudgetStaysOnTheCurrentSession`.

Environment: Linux, `dotnet test AutoVpn.slnx -c Release`, Mihomo unset.

Evidence: those two tests in the run recorded above.

Remaining limitation: the event is `ReportHealth`, not a watched OS process exit. A target outage does not itself change `Reconnecting` back to `Connected`. Live core death is F01.

## F09

Status: `OPEN`

Fix commit: none

Production paths: `src/AutoVpn.Infrastructure/Broker/BrokerEngine.cs`, `src/AutoVpn.Domain/TunnelReducer.cs`.

Regression IDs: AT08 not written.

Environment: not executed for this finding.

Evidence: none new.

Remaining limitation: arm/start/verify/stop/restore failure injection and packet proof are open.

## F10

Status: `BLOCKED`

Fix commit: none

Production paths: `src/AutoVpn.Infrastructure/Broker/UnavailableNetworkGuard.cs`, `src/AutoVpn.Recovery/Program.cs`.

Regression IDs: AT08, AT10, AT30 not written.

Environment: Linux. The guard does not install filters.

Evidence: `UnavailableNetworkGuard` returns `NOT_WINDOWS` off Windows and `WINDOWS_NETWORK_NOT_VALIDATED` on Windows.

Remaining limitation: WFP, DNS, and route ownership need the disposable Windows machine. This host's network was not changed.

## F11

Status: `OPEN`

Fix commit: none

Production paths: `src/AutoVpn.Infrastructure/Persistence/EffectJournal.cs`, `src/AutoVpn.Recovery/Program.cs`.

Regression IDs: AT09, AT10 not written. `BehaviorTests.UnsupportedJournalSchemaIsLeftUntouched` still covers the schema refusal.

Environment: Linux unit test for the schema case only.

Evidence: that test.

Remaining limitation: a quarantined journal does not persist a recovery-unknown marker across a second process start.

## F12

Status: `OPEN`

Fix commit: none

Production paths: `src/AutoVpn.Infrastructure/Core/MihomoProfileGenerator.cs` DNS block.

Regression IDs: AT11 not written.

Environment: not executed.

Evidence: `docs/adr/0001-dns-redir-host.md`.

Remaining limitation: no packet capture. The generator still writes plain DoH nameservers.

## F13

Status: `OPEN`

Fix commit: none

Production paths: `src/AutoVpn.Domain/NodeSemantics.cs` (`EndpointSafety`).

Regression IDs: AT12 not written. Literal private and metadata hosts remain rejected by existing import tests.

Environment: Linux unit tests.

Evidence: `ImportTests.PrivateAndMetadataDestinationsAreRejected`.

Remaining limitation: resolved A/AAAA answers and rebinding are not checked.

## F14

Status: `VERIFIED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `src/AutoVpn.Infrastructure/Probe/ProbeCoordinator.cs`, `src/AutoVpn.Infrastructure/Broker/BrokerEngine.cs` (`Select`, `IsCurrentlyEligible`). Pre-connect age is 60 seconds. Catalogue age stays 30 minutes.

Regression IDs: AT13, AT14. `At13StaleHealthyIsRecheckedAndFreshHealthyIsNot`, `At14EpochChangeRechecksAndDiscardsASuccessFromTheOldEpoch`, `PreConnectOlderThanSixtySecondsIsNotAdmission`.

Environment: Linux unit tests, injected transport and `DateTimeOffset.UtcNow` for the broker.

Evidence: those tests in the run above.

Remaining limitation: the probe transport in tests is in-process. There is no candidate-local HTTPS client. That gap is F15.

## F15

Status: `IN_PROGRESS`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed` for ordering, per-attempt cancellation, byte accounting, and a concurrency ceiling of `MaxProbesPerEndpoint`. The loop is still sequential. `MaxProbesPerEndpoint` is no longer a total-attempt cap.

Production paths: `src/AutoVpn.Infrastructure/Probe/ProbeCoordinator.cs`.

Regression IDs: AT15, AT16. `At15ThirdSameEndpointVariantIsNotStarved`, `At16HungProbeIsCancelledAndBudgetsLeaveTheRestPending`. Uplink stop: `BehaviorTests.ProbePublishesOnlySuccessfulSamplesAndUplinkDoesNotFailTheRest`.

Environment: Linux unit tests.

Evidence: those tests.

Remaining limitation: AT17 is not implemented. A broken candidate can still not be proven to fail while another node's TUN is up. One Boolean success is still enough to mark `Healthy`. No isolated HTTPS transport.

## F16

Status: `VERIFIED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed` for snapshot validity. Refetch: `1ba3cf78dd242141d8605286ac2a45101ad737c0`.

Production paths: `src/AutoVpn.Infrastructure/Refresh/RefreshMerge.cs`, `CatalogueCoordinator.cs`, `src/AutoVpn.Infrastructure/Import/SubscriptionImporter.cs` (`DocumentValid`, `EmptyValidDocument`).

Regression IDs: AT18. `At18InvalidSnapshotsKeepLastGoodMembershipAndRecognizedEmptyDoesNot`. `PackageBTests.NotModifiedWithoutMembershipRefetchesAndCancellationDoesNotPublish`.

Environment: Linux unit tests. The refetch used an in-process handler, not a public host.

Evidence: AT18 keeps family A and family B across malformed JSON, HTML, a truncated object, `{}`, a non-list `proxies` document, and an invalid URI. `proxies: []` removes only family A. The coordinator test stored an etag with no artifact membership, received 304, refetched without that etag, and published one `Pending` node (`RefetchPerformed`). Cancelling a blocked download published nothing.

Remaining limitation: the refetch ran through the coordinator and an in-process handler. A public upstream was not fetched.

## F17

Status: `IMPLEMENTED_NOT_VALIDATED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `src/AutoVpn.Infrastructure/Import/ShareLinkParser.cs`, `SubscriptionImporter.cs`, `src/AutoVpn.Infrastructure/Fetch/GithubTreeParser.cs`.

Regression IDs: AT19. `At19MalformedRecordDoesNotAbortTheBatchOrLookEmpty`. Existing duplicate-key and HTML tests in `ImportTests` still pass.

Environment: Linux unit tests.

Evidence: that test. `[]` and a numeric `path` return an incomplete tree, not a thrown exception and not an empty success.

Remaining limitation: no dedicated malformed-Unicode or maximum-depth property run was added. Xray still uses the first `vnext` user (F20).

## F18

Status: `IMPLEMENTED_NOT_VALIDATED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `src/AutoVpn.Infrastructure/Core/MihomoProfileGenerator.cs`, `src/AutoVpn.Infrastructure/Import/ClashProxyParser.cs`, `ShareLinkParser.cs`.

Regression IDs: AT20. `At20ProtocolFieldsRoundTripIntoTheEmitter`.

Environment: Linux unit tests. Strings were compared. `mihomo -t` was not run.

Evidence: that test and `docs/PROTOCOL_COMPATIBILITY.md`.

Remaining limitation: no per-protocol native validation and no handshake where SNI or cipher changes the peer result.

## F19

Status: `VERIFIED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `src/AutoVpn.Domain/NodeSemantics.cs` (`HasClosedSecurity`), `SubscriptionImporter.cs`, `MihomoProfileGenerator.cs` (`AcceptedSecurity`).

Regression IDs: AT21. `At21UnknownSecurityIsRejectedBeforeAProfileExists`, including `security=bogus`, `BOGUS`, `TLS`, and a missing security value.

Environment: Linux unit tests. No core process was launched.

Evidence: that test.

Remaining limitation: certificate checking was not loosened. Native validation was not used, because generation throws `CORE_CONFIG_REJECTED` first.

## F20

Status: `IMPLEMENTED_NOT_VALIDATED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `ShareLinkParser.cs`, `ClashProxyParser.cs`, `NodeSemantics.cs`, `src/AutoVpn.Contracts/Ipc.cs`, `NodeWireFactory.cs`, `MihomoProfileGenerator.cs`.

Regression IDs: AT20.

Environment: Linux unit tests, YAML text only.

Evidence: `At20ProtocolFieldsRoundTripIntoTheEmitter` and `docs/PROTOCOL_COMPATIBILITY.md`.

Remaining limitation: Xray server arrays beyond the first user are not mapped. Header type on `tcp` fails closed instead of being emitted. No native round-trip.

## F21

Status: `VERIFIED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `src/AutoVpn.Domain/NodeSemantics.cs`, `src/AutoVpn.Domain/ProductLimits.cs` (`CanonicalizerVersion` 2), `src/AutoVpn.Application/Catalogue.cs` (`ReconcileStoredDigest`), `SqliteCatalogue.cs` load path.

Regression IDs: AT22. `At22OpaqueBytesSurviveAndMigrationDoesNotInventHealth`.

Environment: Linux unit tests with SQLite and `PassthroughSecretProtector`.

Evidence: that test. A v1 digest keeps Healthy and the favorite. A digest that matches neither v1 nor v2 drops the assessment and keeps the favorite. Path ` /x `, obfs password ` secret `, and Reality `spx` ` /x ` survive import and generation.

Remaining limitation: host header and ALPN tokens are still trimmed. They are treated as names, not as the opaque fields above.

## F22

Status: `VERIFIED`

Fix commit: `1ba3cf78dd242141d8605286ac2a45101ad737c0`

Production paths: `src/AutoVpn.Infrastructure/Fetch/PolicyHttpFetcher.cs`. `CreateProductionHandler` sets `AllowAutoRedirect` false, `UseProxy` false, and leaves the certificate callback unset. `HttpClient.Timeout` is infinite. One linked token bounds `SendAsync` and the body read. A redirect drops the previous etag. 429 is `RATE_LIMITED` and is not retried.

Regression IDs: AT23. `PackageBTests.ProductionHandlerCancelsAStalledBodyAndDoesNotFollowAnOffRegistryRedirect`, `RateLimitIsNotTreatedAsADocument`. `BehaviorTests.FetcherRejectsHtmlOversizedAndOffRegistryRedirects` still passes.

Environment: Linux unit tests against loopback HTTPS. The production callback was null before the test pinned a fixture certificate. Certificate validation was not disabled in product code.

Evidence: an off-registry path and an approved path that redirected to a second loopback port both returned `OFF_REGISTRY_REDIRECT` with a null body. The second server stayed at 0 requests. A separate handler with `AllowAutoRedirect` true, which the production constructor rejects, did reach that second server. After headers, a stalled body returned `FETCH_TIMEOUT` with a null body in under 4 seconds. Three allowed retries of HTTP 429 produced one call, a null body, and `RetryAfterSeconds` 9.

Remaining limitation: the attempt deadline is the caller's `TimeSpan`. The product coordinator passes it through. This test does not cover a public mirror.

## F23

Status: `VERIFIED`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `src/AutoVpn.Infrastructure/Persistence/SqliteCatalogue.cs`. Mutations copy, save, then publish.

Regression IDs: AT24. `At24FailedSqliteCommitDoesNotPublishTheCopy`.

Environment: Linux, SQLite, protector whose `Protect` throws `IOException`.

Evidence: that test. Favorite and assessment in memory and on reopen stay at the last committed row.

Remaining limitation: the injection is the protector, not a killed SQLite transaction after `Commit`. Rollback is the `using` transaction dispose.

## F24

Status: `OPEN`

Fix commit: none

Production paths: `SqliteCatalogue.Save` still rewrites the database.

Regression IDs: AT25 not written.

Environment: not executed at the 10_000-node bound.

Evidence: none new.

Remaining limitation: one assessment still rewrites the catalogue.

## F25

Status: `IN_PROGRESS`

Fix commit: `1ba3cf78dd242141d8605286ac2a45101ad737c0`. The ingest flag remains in the package A commit.

Production paths: `src/AutoVpn.Infrastructure/Fetch/ReviewedRegistry.cs`, `src/AutoVpn.Infrastructure/Refresh/CatalogueCoordinator.cs`, `SourceLedger.cs`, `config/source-manifest.json`, `config/mirrors.json`.

Regression IDs: AT26 partial. `PackageBTests.ReviewedRegistryRejectsUnsafePathsAndRejectedHosts`, `IncompleteDiscoveryDoesNotBecomeASubscriptionRefresh`, `NotModifiedWithoutMembershipRefetchesAndCancellationDoesNotPublish`.

Environment: Linux unit tests. The registry was the repository `config/` directory. Discovery and refetch used stub HTTP, not `api.github.com`.

Evidence: `..`, absolute paths, and backslashes are rejected. `bitbucket.org`, `raw.githack.com`, and `translate.yandex.ru` are not emitted. An incomplete tree produced no refresh work and did not delete a seeded node. A probe URI outside the approved target file returned `Attempted` 0. A 304 without membership refetched once.

Remaining limitation: no live GitHub discovery, no comparison of disagreeing mirror bodies, and no proof that an added or removed upstream family changes only that family. AT26 is not closed.

## F26

Status: `OPEN`

Fix commit: none

Production paths: country helpers under `src/AutoVpn.Domain`.

Regression IDs: AT27 not written. `BehaviorTests.PinnedAndStrictCountryDoNotSwitch` still passes.

Environment: Linux unit test for the old strict-country case.

Evidence: that test.

Remaining limitation: conflicting country labels are not retained as evidence.

## F27

Status: `IN_PROGRESS`

Fix commit: `1ba3cf78dd242141d8605286ac2a45101ad737c0` for the measurement gate only. Ranking and standby selection were not replaced.

Production paths: `src/AutoVpn.Domain/BoundedTransfer.cs`, `src/AutoVpn.Infrastructure/Refresh/CatalogueCoordinator.cs` (`SpeedMeasurement`), `src/AutoVpn.Application/UiSession.cs`.

Regression IDs: AT27 not written. `PackageBTests.SocksClientReadsALocalStatusAndSpeedStaysUnknownWithoutHealth`.

Environment: Linux unit tests. A local TCP listener returned HTTP 204. A local SOCKS5 relay forwarded CONNECT to it. No VPN node was dialed.

Evidence: `Socks5Client.GetStatusAsync` read 204. `SpeedMeasurement.MeasureHealthyDownloadAsync` on an empty catalogue returned null. `BoundedTransfer` stopped a blocking stream. The presentation text for an unmeasured node contains «не измерялась».

Remaining limitation: a direct or SOCKS 204 is not node eligibility. Healthy/Degraded ranking, the standby cap, and dwell were not implemented. No speed was stored for a pending node.

## F28

Status: `IN_PROGRESS`

Fix commit: `1ba3cf78dd242141d8605286ac2a45101ad737c0` for the refresh-interval clamp. Probe budgets were not rewritten here.

Production paths: `src/AutoVpn.Domain/Scheduling.cs`, `src/AutoVpn.Domain/Settings.cs`.

Regression IDs: AT28 partial. `PackageBTests.ScheduleClampAndUnknownSessionDoNotClaimDisconnect`. `BehaviorTests.ScheduleDoesNotHonorOneMinuteAdvisoryOrReplayAGap` still expects `Interval(1, 0, 0)` to be 15 minutes.

Environment: Linux unit tests, no stepped clock and no NIC event.

Evidence: seeds 0, 1, -1, -100, `int.MinValue`, and `int.MaxValue` keep a 120-minute base with 10-minute jitter inside 110..130. `ProductSettings.Validate` rejects 8 days. The 15-minute floor still holds.

Remaining limitation: monotonic probe budgets, sleep, NIC changes, and a self-TUN epoch storm are not covered. AT28 is not closed.

## F29

Status: `OPEN`

Fix commit: none

Production paths: `MihomoProfileGenerator` still rejects control characters. `MihomoProcessController` was not given a reaper.

Regression IDs: AT29 not written. `BehaviorTests.ProfileRejectsControlCharactersInsecureCertsAndEmitsPluginOpts` still passes.

Environment: Linux unit test.

Evidence: that test.

Remaining limitation: spaced secrets inside a real process command line, and cleanup of a cancelled child, are not tested.

## F30

Status: `BLOCKED`

Fix commit: none

Production paths: no installer project.

Regression IDs: AT30, AT32 not written.

Environment: Linux. `scripts/package.ps1` was not run.

Evidence: local gitignored archive `artifacts/autovpn-0.1.0-win-x64-self-contained.tar.xz`, SHA-256 `a4c142f9d88c85849278c6e7b0e13cdfc7a26975bdd3bf565d944a3ecd0c64cf`, 134272704 bytes, packed `2026-10-02T19:58:42Z`. It predates `2b46431` and is not an installer.

Remaining limitation: no retrievable GitHub release, no Authenticode, no Mihomo, no Wintun.

## F31

Status: `IN_PROGRESS`

Fix commit: `2b46431693fe58eb02c40a36a0b192ce000b6fed`

Production paths: `src/AutoVpn.Infrastructure/Broker/UnavailableNetworkGuard.cs` (`PlatformReason`), `tests/AutoVpn.UnitTests/BehaviorTests.cs`, `.github/workflows/ci.yml`.

Regression IDs: AT31 partial. Connect expects `PlatformReason()`. Missing-binary TUN validation expects `CORE_MISSING` on Windows and `NOT_WINDOWS` otherwise. `RequiresMihomoFact` skips when `AUTOVPN_MIHOMO_PATH` is empty. The switch-budget test expects `Reconnecting`.

Environment: Linux. CI still sets `AUTOVPN_MIHOMO_PATH` to an empty string, which is a skip.

Evidence: the package A solution run was 47 passed and 1 skipped. The package B unit-test run was 57 passed and 1 skipped. Neither run is a Windows pass.

Remaining limitation: the second-owner pipe test is still the same user. Admin scripts still exit 2. Windows expectations were not executed on Windows. Seeded consent in broker tests remains.

## F32

Status: `OPEN`

Fix commit: none

Production paths: `THIRD_PARTY_NOTICES.md` still says MIT for the pinned Mihomo commit.

Regression IDs: AT32 not written.

Environment: the upstream license file was not re-read in this increment.

Evidence: none new. The directive records GPL-3.0-only at that commit plus bundled BSD/Apache/MIT notices.

Remaining limitation: the notice, SBOM, and component license list are not corrected yet. Do not treat this row as a license decision.

## F33

Status: `OPEN`

Fix commit: none for a new artifact. `docs/evidence/build-manifest.json` names code commit `1ba3cf78dd242141d8605286ac2a45101ad737c0` and does not claim the archive contains it.

Production paths: `docs/evidence/build-manifest.json`.

Regression IDs: AT32 not written.

Environment: Linux bookkeeping only.

Evidence: this file.

Remaining limitation: no source-to-artifact manifest for `2b46431`. The published binaries were not rebuilt.

## F34

Status: `OPEN`

Fix commit: none

Production paths: `src/AutoVpn.Desktop`.

Regression IDs: AT33 not written.

Environment: WPF was compiled on Linux and not run.

Evidence: none.

Remaining limitation: themes, DPI, keyboard, tray, and empty/error/protected screens were not exercised. Needs Windows for a real UI pass.
