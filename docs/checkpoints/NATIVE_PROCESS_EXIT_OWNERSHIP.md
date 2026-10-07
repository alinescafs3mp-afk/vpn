# Native process-exit and original-reader ownership — 2026-10-07

## Scope and current status

This slice follows `8fd20b6a3c3bebfffd71b37b04c06c31f7368387` and its retained Windows sharing-violation evidence. It strengthens two concrete cleanup obligations and adds an independent held/disposed fixture-observer comparison. The earlier failed execution is preserved in [WINDOWS_FILE_LIFETIME_DIAGNOSTICS.md](WINDOWS_FILE_LIFETIME_DIAGNOSTICS.md) and its evidence file. A new successful run, if obtained, does not identify that earlier failure's cause by itself.

At source publication, compilation and Windows/Linux execution are **PENDING**. The implementation host has no .NET SDK; no local build or native acceptance is claimed. This document will be completed with the actual first CI attempt and exact artifact accounting.

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

## Acceptance boundaries

The earlier sharing violation remains historical **FAILED** evidence. The two cleanup defects above are established by exact source and ownership analysis; the precise cause of the old `.exe` failure remains **UNPROVEN** unless a controlled observation establishes it. Fresh all-green repetitions alone cannot retrospectively prove causation.

Windows Server runner results, when available, do not establish Windows 11 TUN, DNS/IPv6 protection, crash recovery, installer or release readiness. Production privileged/TUN admission remains closed under the existing gates. No network settings on the implementation host were changed.
