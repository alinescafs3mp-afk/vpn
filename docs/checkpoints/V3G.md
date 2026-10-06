# V3G continuation checkpoint

Continue only on main. Do not create branches, force-push, rewrite history,
replace real Windows results with Linux tests, or delegate implementation to Grok.

Tested code: c1c174ccaf4accb0a61abafe68e3f6d7217caa76.
Tested tree: cd8da8fdd1764ea2c91932e0b92d882c00be8778.
Final CI: 37391114702, attempt 1; Linux, Windows and installed-service jobs passed.
Use README_V3G_RU.md and V3G_VALIDATION.json for exact case accounting.
Later delivery documentation does not change this compiled code.

## Implemented and retained

- Integrated 50 cases and safe staged service-query diagnostics from the previously
  delivered V3F local package without reverting upstream process-access/reply fixes.
- Added the Windows available-byte output adapter and 13 cases. The isolated
  legacy/new controls prove pool occupation versus responsiveness on real Windows.
- Changed only the migrated pipe fixture buffers after the first Windows series:
  match the production 4096-byte buffers; no deadline or assertion relaxation.
- Installed service status and authorization lifecycle passed on a disposable
  Windows runner. This is not a TUN or installer acceptance result.

## Next narrow implementation slice

Move from read-only status to an authenticated, bounded selected-node handoff.
The service must independently validate a fixed data schema and generate its
own configuration. Never accept executable/configuration paths, commands,
raw subscription bodies or arbitrary core flags from the caller. Keep secrets
out of logs and diagnostic results. Bind a request to owner, request/operation
identity, service instance and explicit consent; cancellation must not resurrect
an old operation. Do not open the user's SQLite catalogue as SYSTEM.

Connect the handoff to owned real-core lifecycle only after its validation tests.
Do not equate a listening port, a Ready service or a live core with protected VPN.
Before enabling Windows TUN tests, implement owned network-state journaling and
rollback and use an authorized disposable Windows environment. No Linux route,
DNS, proxy, firewall or TUN changes. Retained process handles and exact ownership
must survive failures; parent exit alone is not proof of all descendant exits.

## Testing and delivery

Use the existing six-series scripts/test-v3f.ps1 on both platforms. It keeps
TLS diagnostics, 599 cases and exact platform skip lists. Extend counts only
when adding real cases, not to mask discovery or execution failures.
Preserve the original failing d7c0e6f series and c1c174c final series.
The V3G Windows archive is a Linux cross-publish of the tested code, not a native
execution test of that exact binary. Check it on Windows 11 before claiming that
platform or that artifact is accepted. Do not reuse old version-specific
packaging scripts that still identify their output as V2 or V3B.
