# ADR 0001 — DNS mode redir-host until Windows evidence exists

Date: 2026-10-02

Generated profiles set `enhanced-mode: redir-host`. Fake-IP is not used.

redir-host keeps real addresses in the connection, which matches the requirement that DNS answers not be invented and that LAN exceptions stay explicit. Whether this mode, together with the port-53 rules, actually prevents DNS and IPv6 leaks on Windows is `NOT_RUN`.

This is the starting policy, not a measured proof.
