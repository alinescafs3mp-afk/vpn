# V3E first validation series: retained failures

Source `faa97966c7c04234308a2703be1241570100b41d`, run `37355416544`, attempt 1. Both full solution builds succeeded. Each platform completed all six planned 511-case runs. TLS/HTTP source and dependency boundary checks passed.

Linux: 3030 passed, 12 failed, 24 named Windows-only skips. Two newly authored socket tests failed in every iteration. Their conflict sockets called Bind without Listen, unlike the production CorePortLease. The fixture now models an actual listening server, and even a failed assertion disposes any unexpectedly acquired lease. No production reservation success criterion was relaxed.

Windows: 3039 passed, 9 failed, 18 old Linux-only skips. Iteration 4: BehaviorTests.LocalPipeRejectsASecondOwnerAndReturnsSnapshot timed out at NamedPipeClientStream.ConnectInternal. Iteration 5: four diagnostic protocol repeats and one startup fixture hit CORE_PORT_UNAVAILABLE; the HTTPS redirect test hit FETCH_TIMEOUT; the catalogue-refresh scenario expected one healthy candidate but received zero; one new two-worker cleanup scenario reported CORE_CLEANUP_REQUIRED. Cleanup diagnostics are now included in fixture failures. These timing/cleanup incidents are not declared fixed by a later successful run.

Joint binding catches the previously hidden transport conflict, but letting UDP's ephemeral allocator select every retry did not guarantee a common TCP/UDP candidate. Reserve now checks at most 32 dispersed explicit high ports, with an odd stride through the 16384-port range, binding both transports before success. The failure remains fail-closed; there is no remote TLS retry or longer startup timeout.

The first series artifacts remain in GitHub Actions: Linux `11364462131`, Windows `11365160088`. Do not discard them or reclassify them as passing. The one-time integration action has been replaced by ordinary read-only validation of the pushed main commit.
