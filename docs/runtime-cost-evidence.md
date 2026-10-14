# Runtime GC, allocation and Monitor contention evidence

The existing `/trace.html` page now has a read-only runtime-event analysis section.
Choose a completed saved capture and inspect its original `.nettrace`, not the
information-reduced Speedscope conversion. No application, compiler, profiler or
symbol downloader is launched by analysis. Capture/analysis are separate actions.

## Explicit detailed capture

The original `performance_trace_capture` default is unchanged. Use the new
`performance_runtime_capture` MCP tool, or check **detailed runtime events** in the
browser's capture form. This requests the existing runtime keyword mask at level 5
instead of level 4, recording additional events including allocation ticks.
The exact requested provider string is saved in `RuntimeProviderConfiguration`;
legacy manifests leave it unknown. Verbose collection increases overhead and file
size. Existing trust requirements, limits and opt-in UNSANDBOXED execution remain.
Tenka does not install dotnet-trace; CI alone installs pinned version 9.0.661903.

`performance_runtime_costs(reportPath, processId?, maxEvents, maxRows, pauseBudgetMs?)`
and `POST /api/traces/runtime-costs` verify saved capture artifacts, copy at most
128 MB into an immutable in-memory snapshot, recheck that snapshot's SHA-256, and
parse it using pinned Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8. One analysis
runs at a time per Tenka process. Event callbacks are bounded to at most two million
(default 500,000), with a cooperative 45-second parsing deadline, 64 PID/CLR groups,
bounded pending events/groups and explicit detail-output limits. Partial event
limits and reported event loss stay visible. Source hashing and parser bounds are
not an OS sandbox against malicious artifacts.

## Quantities that must not be combined

* GC collection start/end: collection lifetime, paired by PID/CLR/GC count. A long
  background GC is NOT a pause of the same length. Overlapping foreground and
  background collections are retained independently; target generations are not
  silently expanded to count lower generations.
* GC suspension window: SuspendEE start through RestartEE stop, for GC reasons 1
  and 6 only. This includes suspension rendezvous and resumption. The shorter
  fully-suspended interval additionally requires SuspendEE stop and RestartEE start.
  Missing or duplicated phases remain flagged; unrelated debugger/shutdown
  suspensions are not relabeled GC. Intervals are not charged per suspended thread.
* Allocation tick bytes: reported allocation volume between threshold events.
  A tick can cover allocations of many types. Only its last/triggering type is
  attached to the event. We do NOT attribute the entire tick byte count to that
  type. Type tables contain trigger counts and known trigger-object sizes, not
  an exact allocation census or method-level allocation profile.
* Monitor contention: DurationNs from managed contention-stop events, converted to
  milliseconds. Aggregate waiter duration can exceed wall time because waiters
  overlap. Missing starts are counted, not used to invent lock/source identities.
  Missing durations are not replaced with zero-cost claims. Other wait mechanisms
  (I/O, tasks, native synchronization etc.) are not covered.

On-CPU time and other-wait time are **null**, not computed by subtracting any of
these numbers from sampled thread time. No 100% CPU/GC/wait breakdown is presented.
No events means **not observed**, not that an event category has zero cost.
Event loss is a parser-reported value, not a completeness proof. Totals describe
observed records, and details may be truncated while counters remain available.

## Investigation priorities and assembly boundary

GC/Monitor evidence normally receives P2 investigation hints, allocation ticks P3.
An explicitly supplied single-suspension-window budget can promote a GC hint to
P1. This is an investigation order, not calibrated confidence, a proven defect or
an SLO breach. All hints retain `defectConfirmed=false`. Runtime hints are displayed
in the trace report, not automatically merged into the separate source priority
queue. A collected GC or wait does not promote every warning in the same method.

The report preserves the same captured DLL SHA-256/MVID inputs used by existing
binary comparisons. This does NOT verify loaded modules or associate individual
runtime events with a Roslyn method, native instruction, or source line. The
assembly workbench remains available for independent code-generation inspection;
no signature-only match is upgraded to exact native-to-IL mapping by this feature.

## Validation and remaining work

A controlled .NET 8 fixture allocates memory, forces GC and creates actual Monitor
contention. CI on Windows and Linux checks real verbose capture, decoding and
nonzero independent event categories. Deterministic tests cover overlapping GC
lifetimes, overlapping waiters, exact unit conversion, PID/CLR isolation, tick
semantics, incomplete/duplicate/out-of-order pairs, limits, overflow and priority
boundaries. HTTP and stdio MCP tests check the read-only path, cancellation/trust
boundaries, validation and artifact integrity. These are correctness tests, not
real-project defect precision or recall estimates.

Still needed: on-CPU sampling, allocation/lock call-stack attribution, complete
wait and GC CPU attribution, authenticated loaded-module/source identities,
exact native-to-IL/C# code-version mapping, and representative held-out project
quality evaluation. Do not report these as completed by the runtime-event reader.

Primary specifications:
- https://learn.microsoft.com/dotnet/fundamentals/diagnostics/runtime-garbage-collection-events
- https://learn.microsoft.com/dotnet/fundamentals/diagnostics/runtime-contention-events
- https://github.com/microsoft/perfview/tree/v3.2.8/src/TraceEvent
