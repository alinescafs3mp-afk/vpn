# ADR 0004 — Do not install untested WFP filters

Date: 2026-10-02

A native WFP call that was never executed on Windows can leave filters the process cannot name or remove. `UnavailableNetworkGuard` therefore installs nothing.

On Linux, `Arm` returns `NOT_WINDOWS` and `Armed=false`. On Windows the same type returns `WINDOWS_NETWORK_NOT_VALIDATED` and still does not arm. Disarm of an empty set completes. Recover of a non-empty journal does not mark those rows removed.

The broker can report Connected only after `ConfirmProduction(true)`, which the IPC surface cannot call. The default connect path never reaches that call because the guard does not arm.

Replacing this guard with a real filter driver requires a disposable Windows run and a recovery tool that removes only the filters it recorded.
