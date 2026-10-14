# Trusted EventPipe capture (.NET 8+)

This adds an explicit execution step before the existing measured-first triage:

`built DLL -> dotnet-trace collect -> original nettrace + Speedscope -> explicit thread selection -> Roslyn hotspots`

## Important measurement correction

Ordinary cross-platform EventPipe collection measures **sampled thread time**, which can include waiting and blocking, NOT per-method on-CPU time. Microsoft removed the misleading `cpu-sampling` profile name for ordinary `collect`; this integration instead pins explicit SampleProfiler/runtime provider settings. A large self percentage is not evidence of that percentage of CPU execution.

Official reference: https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace

## Operator setup

Install `dotnet-trace` yourself. Tenka never downloads, updates, builds or installs executables in response to a review tool call. CI verifies version `9.0.661903` with .NET 8 fixture apps on Linux and Windows.

Set `TENKA_DOTNET_TRACE_PATH` to its absolute executable path. Optional `TENKA_DOTNET_HOST_PATH` selects an absolute dotnet host; otherwise an absolute PATH entry is resolved. Both the profiler and target application must be trusted by the operator.

Set `TENKA_ENABLE_LOCAL_EXEC=1`. Each capture additionally requires `trustedBinary=true`. This is **UNSANDBOXED**, with the current user's filesystem, network and process permissions. It is not a replacement for Docker/VM isolation for generated or unknown code.

Both the already-built runnable DLL and a pre-existing output directory must be in `TENKA_ALLOWED_ROOTS`. The output goes into a fresh `tenka-trace-<id>` subdirectory, never overwriting user files. The target needs its runtimeconfig and installed matching runtime. The profiler runs `dotnet exec <app.dll>`, never `dotnet run`, `build` or `restore`.

## MCP workflow

1. `performance_trace_capture(assemblyPath,outputDirectory,trustedBinary,timeoutSeconds,arguments)` runs a finite workload from startup to exit, then converts its trace. The whole operation has a 1-120 second deadline, including tool probing/conversion. It returns the capture.json path, all profile/thread indexes, entry DLL SHA-256/MVID and sidecar hashes. A timeout, failed conversion or changed input is not a completed capture.
2. `performance_trace_get(reportPath)` reloads saved metadata and verifies both artifact hashes without launching a process.
3. Open the corresponding trusted Roslyn project as usual, then call `performance_trace_import(reportPath,profileIndex)` to explicitly select a thread. It uses the existing importer and measured-first triage; no first-thread or all-thread CPU claim is silently made.
4. `performance_evidence_triage` and `performance_method_dossier` work on the imported profile as before.
5. `performance_trace_compare_binary(reportPath,assemblyPath)` distinguishes matching recorded entry DLL/MVID/runtimeconfig/deps files from a changed build.

The existing Web profile importer can also import the produced `workload.speedscope.json` at a chosen profile index; it does not itself verify the capture manifest.

## Provenance and limitations

A matching entry DLL hash is **not** a loaded-module identity or Roslyn/source build verification. Dependency/runtime/native-library inventories are incomplete, transient file changes can evade before/after hashing, and matching profiler names remain conservative name-based links. Saved manifests are unsigned: hashes detect accidental edits, not a hostile operator forging both hashes and files.

Collection includes startup, JIT/PGO and instrumentation overhead; it does not force the code-generation settings used by the separate JIT disassembler. Speedscope conversion loses metadata and the provider buffer can drop events. Keep the original nettrace for independent inspection.

A single capture per Tenka process is allowed. Trace output is monitored at 128 MB and Speedscope at 25 MB, with bounded logs. These polling limits and best-effort process-tree cancellation are not kernel-enforced quotas or a sandbox. A trusted application can still spawn detached children or perform harmful operations. Arguments, traces, file paths and method names may be sensitive; no artifacts are uploaded automatically by Tenka.
