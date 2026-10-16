# Chan.Core: first extraction from Tenka

This change starts the shared foundation for Tenka (C#) and the future Na-chan
(C++). It does **not** add a C++ analyzer, split repositories, change the launcher,
re-enable CI, or change the existing dependency package versions.

Base: `55cc31b4f0396f343deb3f0795ef87a9706a4efd` on `main`.

## Dependency boundary

```text
TenkaChan.Web -> TenkaChan.Mcp -> Chan.Core
TenkaChan.Tests -------------> TenkaChan.Mcp + Chan.Core
Chan.Core.Tests -------------> Chan.Core
```

`src/Chan.Core/Chan.Core.csproj` is a `net8.0` class library with **no NuGet
packages and no project references**. The existing Tenka applications and test
host remain `net10.0`, using the existing repository SDK selection. Referencing
this library does not retarget the application to .NET 8 or broaden analysis
support to older runtimes.

A normal clone of a branch containing this change includes Chan.Core. This is
an ordinary project reference: no second clone, submodule, package publication,
or separate installation is required.

## What moved

| Shared implementation | Existing Tenka entry point | What remains in Tenka |
| --- | --- | --- |
| `Chan.Core.Performance.PerformanceStatistics` | `TenkaChan.Mcp.Analysis.PerformanceStatistics` | Legacy return DTO / JSON contract |
| `Chan.Core.Benchmarks.BenchmarkSummaryComparer` | `BenchmarkRunComparer` | Store access, experiment IDs, source-hash/kind/completion checks, report normalization |
| `Chan.Core.Security.WorkspacePathPolicy` | `WorkspaceGuard` | `TENKA_*` environment variables, MSBuild/Docker permissions, C# project extensions and selected-repository policy |

These are live dependencies, not duplicated implementations: the existing Tenka
entry points delegate their common work to Chan.Core. Legacy public type names,
record fields, default thresholds, statuses, method signatures, error messages
for valid legacy inputs and launch scripts are retained.

The sample-level statistics algorithm, seed, number of bootstrap draws and
legacy explanatory note are unchanged. The note still mentions steady-state
JIT for wire compatibility; native consumers must establish their own warmup
and workload protocol. It remains exploratory evidence, not proof of a regression.

Summary comparison preserves the descriptive-only policy, five-sample check,
500-case-per-side limit, duplicate-case rejection and ordinal case matching.
One defensive change is intentional: non-finite input means or an overflowing
mean ratio are incomparable rather than yielding non-finite output. This also
protects future adapters that do not use Tenka's existing report parser.

## Responsibilities of a future C++ adapter

Normalize time to nanoseconds/op and allocations to bytes/op before constructing
`BenchmarkSummary`. Include all workload/job parameters in case IDs. A matching
string is not proof of equivalent work: the adapter must validate workload
provenance and completed runs separately, just as Tenka still validates stored
benchmark source hashes. The environment fingerprint must represent the actual
CPU, relevant OS/runtime/compiler settings and the measured build.

Do not mix aggregated means with independent raw samples. The summary comparer
is descriptive; the sample-level comparer expects independent samples. Neither
can certify sample independence, a matching binary or workload equivalence.

## Security and compatibility

Path checks still reject paths outside explicit roots and existing symlink /
reparse-point segments, with platform-specific path comparison. `IsWithin` is
only lexical containment: use `RequireFile` / `RequireDirectory` for allowlist,
existence and reparse checks. These checks are not an OS sandbox and cannot
prevent a filesystem race between checking and opening a path. Trusting MSBuild
or executing a process remains a host policy; sharing path code grants neither.

Roslyn/JIT/IL analysis, stores, review prioritization, process executors, Web UI
and MCP registration stay in their current projects. They are intentionally not
moved wholesale because their language-specific and host-specific dependencies
need separate adapter contracts. There is no dynamic plugin loader yet.

## Validation

The change adds a standalone `Chan.Core.Tests` project and Tenka compatibility
tests, without replacing the existing regression tests. Coverage includes
performance classification, reproducibility, input validation, benchmark case
matching, missing/duplicate cases, bounds, non-finite values, path traversal,
legacy JSON shapes and experiment IDs. The symlink-creation test explicitly skips
on Windows because creation needs Developer Mode/privileges; reparse-point
behavior must also be checked on a prepared Windows host.

From the repository root, using the SDK selected by `global.json`:

```powershell
dotnet test tests/Chan.Core.Tests/Chan.Core.Tests.csproj
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj
dotnet build TenkaChan.slnx
```

At authoring time, the editing environment has no .NET SDK and cannot resolve
external hosts to install one. **C# compilation, xUnit execution, Windows
integration and UI/MCP smoke tests have not been run.** Only source/contract and
project-structure checks were performed locally. The change must remain a draft
until the build, tests and Windows smoke checks have been reviewed. Existing
`.github/disabled-workflows` files remain untouched; do not restart CI to verify
this change without the user's explicit instruction.
