# V3G: bounded reads of owned Windows process output

## Observed failure and scope

At upstream `77c86fbf9a67c37c14dbd840d67bbc40753b2260`, Windows regression
`37378375366` retained an `OUTPUT_DRAIN_FAILED` in
`AstraV3EResourceTests.CleanupDoesNotDeleteAnotherWorkersDirectory`.
The report confirmed process exit, but both output tasks remained pending;
it recorded six pool threads and seven queued work items. This is evidence of
where cleanup failed, not by itself proof of the cause of every historic timeout.

`Process` output uses synchronous anonymous-pipe handles on Windows. Async reads
on those handles can occupy pool threads while an otherwise healthy child is
quiet. A neighboring child can therefore delay unrelated managed continuations.
The two isolated `pool-legacy` / `pool-available` controls exercise the old and new
read paths with a two-worker pool in a *separate fixture process*. They do not
change ThreadPool settings in the application or unit-test runner. Check the
retained Windows TRX before claiming those controls passed.

## Decision

For each fresh Windows `Process.StandardOutput` / `StandardError` reader whose
base FileStream is synchronous, use one exclusive handle reader. Peek at the
available bytes, read no more than that count, and yield through a cancellable
10 ms timer when there are none. A pipe with no bytes is idle, not EOF. A broken
or disconnected owned pipe is EOF; other I/O failures remain failures. The
original StreamReader encoding and BOM detection are preserved in a fresh
stream decoder. Retained output remains capped independently of draining.

The process remains the owner of its handles. The adapter neither closes nor
duplicates them. Process disposal still follows confirmed process exit and
completed output tasks. Existing stop and output deadlines are unchanged.

## Required invariants and trade-offs

- Call once, before any read on the original reader; no second consumer may read
  this handle. Buffered or shared readers are not supported by this adapter.
- No blocking OS read is outstanding while the handle is empty. Do not introduce
  a concurrent read elsewhere: Microsoft explicitly warns that PeekNamedPipe on
  synchronous handles can block in multithreaded applications.
- The poll interval adds small idle wake-up overhead and up to one poll interval
  of output-observation latency. This is not a general lock-free or real-time
  guarantee, nor an atomic child lifecycle implementation.
- A future launcher with genuinely overlapped named pipes could remove polling,
  but needs correct handle inheritance and launch ownership. It is not silently
  substituted in this increment.
- Parent exit is not proof that every descendant has exited. This change does not
  activate the archived Job Object prototype or claim whole-tree recovery.
- No remote request retry, TLS relaxation, firewall/routing/DNS mutation, or
  production ThreadPool tuning is introduced.

## Primary references

- Microsoft, PeekNamedPipe: https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-peeknamedpipe
- Microsoft, Synchronous and Overlapped Pipe I/O: https://learn.microsoft.com/en-us/windows/win32/ipc/synchronous-and-overlapped-input-and-output
- Microsoft, DisconnectNamedPipe: https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-disconnectnamedpipe

The service reply-lifetime fix already present in upstream is retained unchanged.
The portable tests from the previously delivered V3F package are integrated with
that implementation rather than replacing its native Windows tests.

## Recorded outcome

At code c1c174ccaf4accb0a61abafe68e3f6d7217caa76, run 37391114702,
both isolated Windows controls passed in each of six full-suite runs.
The original output-cleanup failures did not recur in either V3G Windows series.
This supports the specific pool-occupation fix; it is not proof of the cause
of every unrelated historic timer or catalogue-publication failure.

The first V3G series retained 18 failures in three migrated pipe tests whose
zero-buffer Windows fixture contradicted their writer-before-reader barrier.
Only the fixture buffer sizes were corrected to match the installed server.
All cases and original deadlines remain.
