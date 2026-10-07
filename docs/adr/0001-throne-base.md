# ADR 0001: Base AutoVPN on Throne 1.3.2

Date: 2026-10-07

Status: Accepted by the owner in the current conversation.

## Decision and reason

The owner already finds Throne 1.3.2 suitable and approved a fork with protected preset subscription groups and a visible Update button for the current group. Reusing its existing GUI, subscription pipeline and networking core avoids completing a second implementation of those facilities before the requested workflow can be used.

This explicit owner decision supersedes the former C#/.NET 10/WPF/Mihomo baseline. It is a change of product foundation and acceptance scope, not a claim that the former language or core is incompatible.

The exact upstream source is throneproj/Throne tag 1.3.2, commit 9dd4fe9606185c147e46f218cf038b254fbcdc9c. Preserve upstream provenance and GPL notices. Do not silently substitute the moving upstream development branch or 1.4 beta.

## Preservation

The previous AutoVPN main is 2199f7ebafae3b6b8009b227f590f521863749cd. Preserve that commit, its full ancestry and the autovpn-pre-throne-2026-10-07 tag. The new main continues that history. Prior evidence and unresolved findings remain available in that snapshot; they are not evidence for this different implementation.

## First increment

- Default plus igareck, zieng2-wl, openproxylist and proxycollector groups, persisted without duplication and protected from deletion.
- An Update button immediately after the TUN controls, operating on the group ID captured at click time, with truthful busy/result state.
- A separately identified Windows build with Russian defaults and no upstream binary self-replacement.
- Bounded tests for migration, group identity/protection, error handling, tab switching and a normal Windows GUI launch.

Each group initially uses one ordinary URI subscription URL. The selected catalogue contains links and attribution only, never a bundled live node list. Source research records publication metadata; it does not certify nodes from the owner's ISP.

## Acceptance boundaries

The existing Throne refresh applies subscription data before its optional URL test. Retaining previously working profiles is not equivalent to the former AutoVPN contract of locally testing every candidate before promotion. Full protected failover, crash-safe network restoration, Windows 11 TUN/DNS/IPv6 and installer acceptance require separate observed tests and remain unaccepted until performed.

The inherited networking implementation is preserved. This increment does not add a kill switch or change transport security based on a third-party subscription README.
