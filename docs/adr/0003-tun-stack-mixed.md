# ADR 0003 — TUN stack stays mixed

Date: 2026-10-02

`MihomoProfileGenerator` writes `stack: mixed` when TUN is requested. The upstream default has moved to `mips`. Mixed remains the v1 starting value until a Windows machine shows that mixed fails and another stack passes the same traffic checks.

No Windows TUN stack has been executed. `mixed` is a pin, not a benchmark result.
