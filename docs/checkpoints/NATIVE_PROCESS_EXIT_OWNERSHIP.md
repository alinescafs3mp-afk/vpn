# Native process-exit and original-reader ownership — 2026-10-07

## Scope and current status

This slice follows `8fd20b6a3c3bebfffd71b37b04c06c31f7368387` and its retained Windows sharing-violation evidence. It strengthens two concrete cleanup obligations and adds an independent held/disposed fixture-observer comparison. The earlier failed execution is preserved in [WINDOWS_FILE_LIFETIME_DIAGNOSTICS.md](WINDOWS_FILE_LIFETIME_DIAGNOSTICS.md) and its evidence file. A new successful run, if obtained, does not identify that earlier failure's cause by itself.

Implementation commit: `463048597a73977c43ba425b829a5bcb0e69f4bc`, tree `1b49157278ba1bc366923acc7ba6db1b5c849036`. Corrected test/source commit: `19196ebafa8d5a9697f465a275afb4a350d47098`, tree `d955b964878a85aa047a92ee6d7d3147a0f0ac81`. All production blobs are identical between them. **The corrected full regression and all six triggered workflows passed, attempt 1.** The first candidate failures remain retained. The implementation host has no .NET SDK; all builds and Windows/Linux executions reported here are actual GitHub Actions results, not local builds. Full records: [NATIVE_PROCESS_EXIT_OWNERSHIP_VALIDATION.json](../evidence/NATIVE_PROCESS_EXIT_OWNERSHIP_VALIDATION.json).

The first compiled candidate is `463048597a73977c43ba425b829a5bcb0e69f4bc` (tree `1b49157278ba1bc366923acc7ba6db1b5c849036`). Its Linux executions exposed an error in the newly added observation assertion: .NET uses `AnonymousPipeClientStream` for Unix redirected stdout/stderr, so no FileStream handle is captured and both handle observations correctly remain null. The follow-up changes only this platform expectation, requiring null on Linux and closed on Windows. Both platforms still require original reader presence and successful disposal; all existing file probes and cleanup assertions remain. The original candidate's failures are retained, and the changed source receives its own automatic CI run rather than rerunning the failed source until green. Exact upstream: [.NET 10.0.12 Process.Unix.cs](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs), blob `2dde9eb778cdc9daa24746a0691656fdcba05de6`.

## Why the cleanup contract needed correction

The pinned .NET 10.0.12 implementation has two relevant behaviors:

1. Windows `ProcessManager.HasExited` checks `GetExitCodeProcess` before waiting on the process handle. An exit code other than `STILL_ACTIVE` can return `true` without native signal. `Process.HasExited` caches this result, and `WaitForExitAsync` has early paths through it. Therefore managed completion alone is insufficient for our stronger `ProcessExitConfirmed` contract.
2. Accessing `Process.StandardOutput` / `StandardError` changes the corresponding reader mode to `SyncMode`. `Process.Close`, including disposal, leaves such externally referenced readers to the caller. Our borrowed pipe adapter deliberately does not own the original pipe handle. Joining its drain and disposing only the Process wrapper did not explicitly release those original readers.

Exact primary sources:

- [.NET 10.0.12 ProcessManager.Windows.cs](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs), blob `86399634e4da7325d76be80cec32f42cd463b3e6`.
- [.NET 10.0.12 Process.cs](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.cs), including `HasExited`, `StandardOutput`, `StandardError`, `Close`, and `WaitForExitAsync`.
- [Process.Close contract](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.close?view=net-10.0).
- [WaitForSingleObject contract](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject).
- [Windows executable-image lifetime](https://learn.microsoft.com/en-us/windows-hardware/drivers/ifs/executable-images): image sections can prevent file writes without an ordinary open file handle. This explains why an empty Restart Manager snapshot cannot prove absence of image use; it does not identify the prior failure's owner.

## Implemented ownership rules

On Windows, cleanup now requires `WAIT_OBJECT_0` from the same owned `SafeProcessHandle`. The initial native probe is zero-time. Only a nonsignaled result starts one finite background thread for the remaining existing exit budget. Native waiting does not occupy a managed ThreadPool worker. Managed cleanup continuations retain their normal scheduler; this is not a guarantee of ThreadPool-independent end-to-end wall time.

There is no PID reopen, native handle duplication, infinite wait, extra sequential five-second budget, file retry, arbitrary sleep, or background resource reaper. Native failure, timeout, or an unexpected status cannot confirm exit. A pending wait and the owned resources remain retained; any later cleanup still requires the existing explicit retry capability. SafeHandle marshalling protects the original handle during each native call.

`OwnedProcessExitReport` records bounded immutable values: whether a Windows native wait is required, managed `HasExited` immediately before that wait, the initial native signal observation, and confirmed native signal. Initial native signal is `null` if the probe did not yield a valid observation, `false` only for `WAIT_TIMEOUT`, and `true` only for `WAIT_OBJECT_0`. An old snapshot cannot be rewritten by later task settlement or cleanup retry.

Both original StreamReaders are adopted before any drain starts. When their underlying stream is a FileStream, its same SafeFileHandle is captured before I/O. The existing release phase, reached only after exit, original-task settlement and input-directory removal, explicitly disposes stdout, stderr, Process and Binary in that order. Successful disposal flags are retained across a partial failure; retry does not repeat a successful earlier disposal. Absent/non-file handles remain unknown rather than fabricated closed.

## Added controls and preserved assertions

The full suite is **940 unique cases**, exactly the previous 920 plus:

| Controls | Cases | Windows skips | Linux skips |
| --- | ---: | ---: | ---: |
| Original-reader ownership and partial release | 4 | 0 | 0 |
| Native signal waiter, budget, status and handle controls | 14 | 0 | 1 |
| Independent held/disposed fixture-observer comparison | 2 | 0 | 2 |
| Total additions | 20 | 0 | 3 |

The managed waiter controls use a deterministic native-call seam. They are control-flow evidence, not native executable-image evidence. One Windows control performs a finite native wait against the live current test process without terminating it. Existing actual-child validator cases exercise the production signal gate.

The two new comparison cases cancel independent ready children backed by fresh executable copies. Both require complete cleanup, healthy output and confirmed native signal. One retains the exact fixture observer wrapper at the binary probe; the other disposes it first and observes that same wrapper's handle closed. Each performs exactly one unchanged `File.Open(..., FileAccess.Write, FileShare.None)` attempt. Failure does not trigger a second write attempt after disposal. The original cancellation case and all its assertions remain.

The explicit skip sets are Windows **15** (unchanged) and Linux **24** (previous 21 plus exactly three new Windows-only cases). Six full iterations per OS remain required. All previous identities, original failures, body/disposal exception preservation, first-result immutability, TLS/wrong-credential/readiness assertions, 32 port attempts and 20/5/3-second production budgets remain. No runner or product pool settings change. Unpublished V3H remains excluded.

## Verified execution and observed native signal gap

The first main run is [37561704528](https://github.com/alinescafs3mp-afk/vpn/actions/runs/37561704528); corrected main is [37561949766](https://github.com/alinescafs3mp-afk/vpn/actions/runs/37561949766). All twenty added controls passed on each source: 222 passed / 0 failed / 18 platform skips across both OS series.

| Source / OS | Runs | Cases each | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: | ---: |
| First / Windows Server 2025 | 6 | 940 | 5550 | 0 | 90 |
| First / Linux | 6 | 940 | 5472 | 24 | 144 |
| Corrected / Windows Server 2025 | 6 | 940 | 5550 | 0 | 90 |
| Corrected / Linux | 6 | 940 | 5496 | 0 | 144 |

Four primary ZIP artifacts (`11457600055`, `11456648118`, `11456553528`, `11456458701`) were checked against API size and SHA-256, ZIP CRC and exact commit/tree. All 380 source blobs in every nested source archive match the corresponding reviewed manifest. All 24 TRX were independently recounted; every run has the same 940 unique identities, including every previous 920 identity. The two builds per source contain zero warnings and errors. A separate root parser rechecked all four archives, all source blobs, all 24 TRX and actual one-shot binary probes.

On **each source**, 24 separate native cleanup observations show `ManagedHasExitedBeforeNativeWait=true`, `NativeSignaledInitially=false`, then `NativeSignalConfirmed=true`. These are six original cancellation cases, six held-observer cancellations, six disposed-observer cancellations and six deliberately timed-out diagnostic-helper controls. Thus 18 actual validator cancellations per source demonstrate the managed/native gap; the new gate waited for the native signal. The observations do not time the interval or identify the owner of the earlier historical file-sharing failure.

Both observer comparison arms passed six times per source. Before the sole write probe, the held arm's same observer handle was open and the disposed arm's same handle was closed. Across each source's main series, all **42 Windows Write/None probes** and **24 Linux Read/None probes** passed; all 66 fixture-directory deletions passed. No failed file operation was retried to obtain success.

Each source retains 42 original pipe-control JSON records, 330 actual file-lifetime stages and 24 helper-control records. First-source final validator JSON count is 42: the 24 Linux assertion failures occurred after successful binary probes but before final evidence emission. Corrected source has all 66 final validator records: 54 original-case records plus 12 comparison records. Missing first-source records were not invented.

### Distinct first-source diagnostic failure remains open

First R1 Windows normal execution, run [37561704390](https://github.com/alinescafs3mp-afk/vpn/actions/runs/37561704390), job `112600207852`, artifact `11456948026`, failed `WindowsFileUseDiagnosticsTests.NoHolderSnapshotRemainsExplicitlyIncomplete`. Its report was `QUERY_TIMEOUT`, all RM return codes unknown, `QueryCalls=0`, `Incomplete=true`; the unchanged test expected `StartCode=0`. Owned helper cleanup subsequently completed with both EOFs and native signal, in 921 ms. The full original failure, stack, stdout and decoded report are embedded separately in the evidence file.

This is distinct from the Linux assertion correction. The second commit changes no diagnostic code, query timeout or RM assertion. A later passing run does not explain this timeout. The **two-second diagnostic budget remains unchanged**, and no identical workflow was rerun.

### Other CI and Windows labs

First-source secondary raw TRX totals: 22 distinct executions, 10941 passed / 21 failed / 280 skipped, plus two byte-identical retained prior-stage copies excluded from counting. Twenty failures are the new Linux nullable-handle assertion; one is the R1 Windows diagnostic timeout. Ordinary CI records four additional matching Linux failures in its log, outside raw TRX totals. Failed downstream/skipped stages remain recorded.

Corrected-source V2, V3, R1, ordinary CI and checkpoint workflows all passed, attempt 1: runs `37561949792`, `37561949746`, `37561949777`, `37561949760`, `37561949836`. Together with the main run these are all six triggered workflows. The secondary reports recount 28 distinct raw TRX: 11978 passed / 0 failed / 304 skipped; three byte-identical previous-stage copies are excluded. V2 TLS passed five repetitions of six cases, V3 lifecycle five repetitions of twenty cases on each OS, and V2/V3/R1 WPF smoke each contains eight verified PNGs plus reopen/settings/DPAPI results. Fifteen downloaded secondary ZIPs match API size/digest/CRC; two source archives each match all 380 blobs. Large V3/R1 development-package archives were not downloaded; only API metadata and completed package-identity logs were checked.

Actual Server 2025 installed-service and standard-user node-runtime labs passed on both sources. Service checks exercise SCM lifecycle, authorization and eight negative/recovery controls using real account impersonation. The separate node-runtime process uses an actual standard-user primary token and passed all six protocols with natural process/job exit and owned cleanup. `forcedJobTermination=false` describes that owned Job Object; runner orphan housekeeping is separately retained.

Server 2022 passed on the implementation commit in run `37561704697`. The two corrected test/doc paths do not trigger that workflow, so Server 2022 is **NOT_RUN_ON_CORRECTED**, with the earlier run explicitly attributed and the relevant production/service blobs confirmed unchanged. It is not counted as a third corrected lab.

## Next bounded work

1. Give the diagnostic helper's accessed stdin writer an explicit owner on exceptional write/close paths, and distinguish helper startup, input, RM query and response phases within the same two-second total budget. The current stdout/stderr ownership fix does not establish stdin closure on a failing input write. Retain the recorded timeout and original assertions.
2. Continue the selected-node service handoff from verified current main and the existing owned runtime entry. Keep the unpublished V3H candidate separate; do not overwrite current code or accounting with its older snapshot.
3. Keep owned network-state journaling and restoration ahead of privileged TUN/DNS/IPv6 acceptance and Windows 11 installer/user-journey checks.

## Acceptance boundaries

The earlier sharing violation remains historical **FAILED** evidence. The two cleanup defects above are established by exact source and ownership analysis; the precise cause of the old `.exe` failure remains **UNPROVEN** unless a controlled observation establishes it. Fresh all-green repetitions alone cannot retrospectively prove causation.

Windows Server runner results, when available, do not establish Windows 11 TUN, DNS/IPv6 protection, crash recovery, installer or release readiness. Production privileged/TUN admission remains closed under the existing gates. No network settings on the implementation host were changed.
