# Tenka-chan — C# Deep Review MCP

Roslyn-powered **C#/.NET code review MCP server**. It exposes semantic code
inspection, conservative optimization-pattern findings, reproducible generated
test/benchmark projects and a guarded, offline Docker execution path to
MCP-capable coding agents. The language model can be local or remote: the
server never requires an LLM API key.

> **Status:** early functional foundation, including bounded autonomous investigation but not automated bug verification.
> The MCP tools generate *candidate* findings, not proven defects.
> Generated test and benchmark execution is opt-in and requires a prewarmed
> package cache; full self-directed fuzzing, mutation testing, baseline checkout,
> automatic profiler capture and exact per-instruction C#/IL mappings are
> **not yet implemented**. Native RyuJIT instructions can now be collected
> with an explicitly authorized, sandboxed JIT harness.

## One-click Docker-free Windows setup

From the repository root, run
`powershell -ExecutionPolicy Bypass -File .\scripts\start-tenka-local.ps1`.
It starts the local Web dashboard, enables the trusted-app JIT and trace
paths, attempts installation of a pinned dotnet-trace tool if missing,
prewarms official xUnit/BenchmarkDotNet packages and keeps Docker **OFF**.
The startup script's default directory allowlist is this repository only;
set `-WorkspaceRoot "C:\MyProject"` for another trusted project.

After loading a .csproj/.sln the "性能ホットスポット" page discovers
previously built .NET 8+ DLLs and Roslyn JIT method choices. Click
"静的・決定論・性能解析をまとめて実行" for no-execution analysis or
"選択メソッドのJITを取得" for an explicitly approved trusted app run.
The assembly/trace pages also prefill the chosen existing binary.

"テストと性能" supports prepared **xUnit and BenchmarkDotNet without Docker**
via explicit "ホストで実行" consent. Each such run is UNSANDBOXED,
using current user privileges. The script sets both required environment
flags; every single experiment still requires approval, and only previously
built, reviewed DLLs can be referenced. Do NOT run untrusted generated code
this way. More details: [Docker-free setup](docs/one-click-host-analysis.md).

## Local Web dashboard

The **ASP.NET Core Web UI** is a separate, optional entry point alongside the
existing MCP stdio server. It runs on **http://127.0.0.1:5077** and requires no
JavaScript framework, database server or cloud LLM account.

The browser provides eight screens:

- **Dashboard:** open a trusted solution, start Roslyn scans, see project /
  method counts, compilation diagnostics and candidate categories.
- **Performance hotspots:** static costly-loop/call candidates plus imported sampled workload profiles with self/inclusive rankings.
- **Deterministic analysis:** compiler/semantic evidence, method metrics, source links and JSON export without a model.
- **Findings:** search/filter candidate performance, async and correctness rules
  and jump to the affected method.
- **Methods:** prioritize methods using syntax-based heuristics, inspect C#
  source, direct calls, CFG/dataflow, and retrieve candidate boundary inputs.
- **Evidence:** create and inspect durable hypotheses, their status and
  supporting records from the SQLite ledger.
- **Autonomous AI review:** bounded local-LLM investigation, cancellation, live progress and persisted run logs.
- **Tests and performance:** prepare detached xUnit, BenchmarkDotNet, or RyuJIT native machine-code experiments,
  explicitly run it in Docker when enabled, view saved TRX/JSON results, and
  compare compatible benchmark reports.

### Start on Windows PowerShell

Run this with a **trusted** codebase (MSBuild project evaluation can execute
custom targets on the host when enabled):

~~~powershell
git clone https://github.com/slringal2/tenka-chan.git
cd tenka-chan

$env:TENKA_ALLOWED_ROOTS = "C:\Projects"
$env:TENKA_TRUST_MSBUILD = "1"

dotnet run --project src/TenkaChan.Web/TenkaChan.Web.csproj -c Release
~~~

Open [http://127.0.0.1:5077](http://127.0.0.1:5077), enter an absolute
`.sln`, `.slnx` or `.csproj` path **under your allowlisted root**, and press
**読み込む**, then **全体をスキャン**.

On Linux/macOS, export the same environment variables in your shell. Change
the port using `TENKA_WEB_PORT` (1024–65535). The service **always binds
loopback**, regardless of the ASP.NET Core URLs environment setting. API
writes require a same-origin CSRF token, and the UI renders source-derived
values as text rather than HTML.

Docker execution is **off by default**. To enable the "実行" action, also
set `TENKA_ENABLE_DOCKER=1`, pre-pull the **matching SDK image** for
the target (`sdk:8.0`, `sdk:9.0`, `sdk:10.0`, etc.) and prewarm
the experiment's offline NuGet cache.
The browser asks for confirmation before launching an experiment.
Review [docs/security.md](docs/security.md) before enabling it.

**Important boundaries:** This is a **local control panel**, not an
autonomous AI chat UI. It uses the same core code as the MCP server, but
the workspace index is per process; the SQLite hypothesis ledger and saved
experiment artifacts are shared when `TENKA_DATA_DIR` matches. In-memory
Roslyn state is not shared between the Web and MCP processes. The browser
does not silently execute code or claim that a static rule proves a defect.

## Run JIT analysis without Docker (trusted applications)

**Docker is optional for static analysis, IL/PDB inspection, existing profile
imports and trusted host-native JIT capture.** On Windows, inspecting native
code emitted by your Windows .NET runtime is often more representative than
running the same program inside a Linux container.

Two deliberately different execution modes are available:

| Mode | Docker | Use case | Isolation |
|---|---|---|---|
| Trusted local JIT (`trusted_local_jit_capture`) | **No** | A self-built, already compiled .NET 8+ runnable DLL on its real Windows/Linux/macOS runtime | **None**: child process inherits current user privileges |
| Generated tests, benchmarks and detached JIT harnesses (`experiment_run`) | **Yes** | AI-generated, experimental, third-party or untrusted code | Network-disabled, resource-limited container |

Local execution is **disabled by default**. To use it with a known trusted
app, build it using your installed SDK first. The app needs its
`App.runtimeconfig.json` beside the DLL, with a supported `net8.0` or newer
TFM. The app must invoke the selected method while it runs.

~~~powershell
# Build the app yourself, not through an AI-generated harness:
dotnet build C:\Projects\Example\Example.csproj -c Release -f net8.0

# Configure Tenka's local Web UI:
$env:TENKA_ALLOWED_ROOTS = "C:\Projects"
$env:TENKA_ENABLE_LOCAL_EXEC = "1"
dotnet run --project src/TenkaChan.Web -c Release
~~~

Open `http://127.0.0.1:5077` → **性能ホットスポット** →
**DockerなしでJIT機械語を取得**. Enter the previously built application
DLL, the class/method selector (`Calculator:Sum`) and a 1–120 second
execution timeout. The browser requires an explicit confirmation before
each run. The Web UI then displays captured machine instructions and
encoding bytes. The run's report and raw JIT text are saved under
`TENKA_DATA_DIR/trusted-local-jit/<run-id>` (default: local app data).

The stdio MCP tools are:

- `trusted_local_jit_capture(assemblyPath,jitFilter,trustedBinary,timeoutSeconds)`
- `trusted_local_jit_result(runId)`
- `trusted_local_jit_correlate(runId)` — requires a loaded Roslyn workspace;
  only uniquely identified methods get a source-method link (not an exact
  machine-instruction-to-C#-line mapping).

For MCP, explicitly pass `trustedBinary=true` *and* enable
`TENKA_ENABLE_LOCAL_EXEC=1` at startup. Neither condition can substitute
for the trustworthiness of the application itself. The runner validates
`TENKA_ALLOWED_ROOTS`, rejects symlink/reparse paths, checks runtimeconfig
and DLL SHA-256 before/after execution, supplies bounded command-line
arguments without a shell, and saves a bounded native-code report.

**Security note:** this is not a sandbox. The trusted DLL can read/write
files, use the network, call native libraries and consume the same OS
privileges as Tenka. Do **not** use this path for code generated by an
LLM, downloaded without review, or supplied by an untrusted repository.
For that use case, keep Docker enabled; in-depth host isolation is
essential. See [security policy](docs/security.md).

**.NET 8/9 Docker compatibility:** hardened SDK containers can fail on
startup with the `NuGet-Migrations` mutex when the entire container root
filesystem is mounted read-only. GitHub CI reproduced this independently
of generated code. For .NET 8/9, Tenka now uses a **disposable writable
container overlay** while keeping source, generated-code, and NuGet cache
bind mounts read-only, with networking disabled, privileges restricted,
and resource quotas enforced. Existing .NET 10 containers retain an
immutable root. The SDK8 xUnit and RyuJIT Docker smoke tests succeeded
under these settings. This isolation is still separate from local
trusted-host execution, which has no container boundary.
Related issue: [dotnet/runtime #91987](https://github.com/dotnet/runtime/issues/91987).

**Measurement note:** a JIT dump shows generated instructions and helper
calls but not their actual runtime CPU expense or allocation frequency.
Use representative profiling + BenchmarkDotNet to establish performance
impact. The runner sets `DOTNET_ReadyToRun=0` and
`DOTNET_TieredCompilation=0`, so the emitted machine code may differ from
production Tiered PGO. The actual .NET runtime must be installed on the
host for the app's target framework.

## Trusted local whole-process performance comparison (no Docker)

Use the **性能ホットスポット** → **信頼済みアプリの実行時間を計測**
screen to run a previously compiled .NET 8+ executable DLL on the **actual
Windows/Linux/macOS host**. Set `TENKA_ENABLE_LOCAL_EXEC=1` and restrict
`TENKA_ALLOWED_ROOTS` to project directories you have reviewed. Each
execution also requires an explicit trusted-binary acknowledgment.

The measurements record **whole-process elapsed time**, **cumulative process
CPU time**, **peak resident working set** (not GC allocations), the exact
input arguments, output hashes and binary SHA-256 fingerprints.

To compare revisions, provide baseline and candidate runnable DLLs with the
same declared target framework, the same arguments and equivalent deterministic
stdout. Tenka runs them in alternating order, takes 5–10 measured launches
each, optionally performs 0–2 warmups, and reports an **exploratory** median
difference and bootstrap interval. If the app outputs differ or vary between
runs, **no performance comparison** is reported.

Corresponding MCP tools:
- `trusted_local_workload_measure(baselineAssembly,trustedBinary,candidateAssembly,repetitions,warmups,timeoutSeconds)`
- `trusted_local_workload_result(runId)`

Corresponding local-only HTTP endpoints:
- `POST /api/local-performance/measure`
- `GET /api/local-performance/{id}`

**Measurement limitations:** process creation, process shutdown and JIT startup
are included; this is **not** BenchmarkDotNet steady-state per-operation
timing. Even when a whole-process difference is observed, use CPU sampling,
GC allocation profiling and a matched microbenchmark before attributing that
difference to a particular method. Comparison estimates are exploratory and
can reflect scheduler, OS or external-system noise.

**Safety:** this launches **trusted executable code with the current
user's privileges**, no Docker sandbox. AI-generated test/benchmark harnesses
still require the original Docker workflow. The runner limits arguments,
run counts, timeouts and inherited environment, but cannot constrain a
trusted child program's access to files or network.

## .NET 8+ optimization targets and RyuJIT machine code



**Target projects:** portable `net8.0` and newer. The Tenka-chan MCP/Web
analyzer **host stays on .NET 10**, but an experiment uses the chosen target
project framework and a matching SDK/runtime container. This distinction
matters: benchmarking a net8.0 library on a net10.0 runtime would not
measure its actual .NET 8 JIT behavior.

The target TFM is read from a literal `TargetFramework` or
`TargetFrameworks` in the project file. For multi-target projects, the
lowest supported version is selected unless you explicitly provide
`targetFramework`. When MSBuild imports or variables supply the TFM,
**pass it explicitly**. `net8.0-windows`, Android/iOS targets and
frameworks older than net8.0 are not supported by the Linux experiment
worker. Future .NET SDK images must be installed locally before use.

### Real RyuJIT native-code collection

The new `kind="jit"` experiment compiles a detached C# **execution
harness**, runs it inside a **network-disabled, unprivileged Docker
container**, and instructs CoreCLR's RyuJIT to disassemble only the chosen
class/method. The harness must call your method under representative
conditions; otherwise there may be no JIT output.

Example MCP arguments for a net8.0 project:

~~~json
{
  "repositoryRoot": "C:\\Projects\\Example",
  "projectRelativePath": "src\\Example\\Example.csproj",
  "kind": "jit",
  "targetFramework": "net8.0",
  "jitDisasmFilter": "Calculator:Sum",
  "generatedCode": "using System; public static class Entry { public static int Main() { var input = new int[] {1,2,3,4,5}; Console.WriteLine(Calculator.Sum(input)); return 0; } }"
}
~~~

Call `experiment_prepare` with these arguments and then, **after
explicitly enabling Docker**, call `experiment_run` with the returned ID.
Use `native_disassembly_result` to inspect generated machine instructions,
raw encoding bytes, code size, detected call/branch/vector/division families
and potential allocation or bounds-check helpers. With a matching source
workspace open, `native_roslyn_correlate` attempts to locate the C#
declaration by type, method and parameter signature.

You can also use the Web UI's **テストと性能** screen. Select
**RyuJIT ネイティブ命令解析**, specify the target framework and method,
paste an invocation harness, prepare the experiment, and explicitly run
it. The saved execution report renders assembly bytes alongside
recognized instructions and a Roslyn method link if uniquely resolved.

The isolated JIT harness sets:

- `DOTNET_JitDisasm=<Class:Method>` and
  `DOTNET_JitDisasmWithCodeBytes=1` to obtain real native instruction text
  and bytes rather than just IL;
- `DOTNET_JitStdOutFile=/work/jit-native.txt` to capture JIT output
  without mixing it into ordinary stdout;
- `DOTNET_TieredCompilation=0` and `DOTNET_ReadyToRun=0` for a
  controlled, reproducible JIT compilation mode.

Machine code is **runtime-/architecture-/configuration-dependent**.
The JIT can inline a method, making it disappear from the dump; for a
focused comparison, consider using
`[MethodImpl(MethodImplOptions.NoInlining)]` on a trusted test fixture.
The parsed machine code is not a measured latency or memory-allocation
result. Comparing instructions is useful for investigating why
BenchmarkDotNet and workload profiles show a difference, not replacing
those measurements.

**Mapping precision:** Roslyn correlation is at the **method level**.
Without JIT native-to-IL sequence mappings and the appropriate PDB,
no reliable exact machine-instruction-to-IL-offset/C#-line correspondence
can be asserted. Unknown or overloaded methods are left unmatched unless
their parameter signatures can be resolved unambiguously.

### Compare native code generation across .NET runtimes

For two completed `jit` experiments, use the MCP tool
`native_compare_runs(baselineExperimentId,candidateExperimentId)`
or the dedicated Web UI comparison form. The tool requires an identical
generated invocation harness and JIT selector, then reports code size,
instruction count, instruction-family and native-call changes. You can,
for example, compare an application's .NET 8 code generation with its
.NET 10 variant; both measurements must target the same architecture.

These are **code generation differences**, not benchmark speedups. A
reliable performance comparison still requires matched workloads and
proper repeated timing/allocation measurements.

### Reproduce the runtime environment

~~~powershell
docker pull mcr.microsoft.com/dotnet/sdk:8.0
docker pull mcr.microsoft.com/dotnet/sdk:9.0
docker pull mcr.microsoft.com/dotnet/sdk:10.0

$env:TENKA_ENABLE_DOCKER = "1"
# Optional: explicitly supply the host NuGet package cache
$env:TENKA_NUGET_PACKAGES = "$env:USERPROFILE\.nuget\packages"
~~~

The selected SDK image must already exist on the machine; the worker
uses `--pull=never` and `--network none`. Cross-version runtime
experiments should use the same architecture/workload if you wish to
compare their code generation.

## Performance evidence: profiler → Roslyn → RyuJIT → IL/PDB

Saved host-native JIT results can also be included **without Docker or a new
process launch** using `localJitCaptureId` (separate from
`jitExperimentId`). This is signature-level disassembly evidence only: a
profile/workspace build identity is not inferred, and local captures do not
automatically supply verified IL/PDB artifact bundles.

For performance work, **start with an observed hotspot**, not the largest
static warning score. Optionally pass a saved BenchmarkDotNet experiment ID
using `benchmarkExperimentId`; time and allocation per operation remain
**separate measurements**, not attributed to a profile frame by name alone. The new `performance_evidence_triage` tool joins a
loaded Speedscope profile, Roslyn static candidates, and optionally a
completed JIT experiment. The result has **separate** observed and
static-only rankings: self/inclusive measurements remain in the profile's
unit, and never get summed with Roslyn risk scores.

`performance_method_dossier` then gathers evidence for a selected,
unambiguously linked Roslyn method. Supply an optional JIT experiment ID
for native machine-code context. For an authenticated captured-build
IL/PDB lookup, choose an exact `binary-*.dll` and MethodDef token returned by
`jit_binary_files` and `jit_binary_methods`; providing a local C# source path
allows checksum verification before displaying code excerpts.

The **性能ホットスポット** Web view exposes these workflows under
**実測優先の根拠レポート** and **メソッド別の詳細調査**. The API endpoints are
`GET /api/hotspots/triage` and `POST /api/hotspots/investigate`.

This is **evidence aggregation, not automatic CPU profiling or an
optimization verdict**. Imported profiles can contain waiting/thread time;
unmatched frames remain unresolved; Portable PDB cannot map individual
native machine instructions to IL offsets. More details:
[performance evidence guide](docs/performance-evidence.md).

## Finding real performance hotspots

The **性能ホットスポット** screen separates two very different questions:

- **Static candidate ranking:** Roslyn inspects every indexed method for
  nested loops, repeated allocations/boxing/string concatenation,
  LINQ materialization/terminal enumeration, List<T> scans inside loops,
  synchronous waits, lock contention candidates, I/O inside loops and
  calls to other solution methods. A two-hop bounded call graph propagation
  increases the screening priority of callers of suspicious methods.
  These scores **are not elapsed time or CPU measurements**.
- **Observed workload hotspots:** Import a **sampled Speedscope JSON**
  created by a profiler for a representative application workload.
  The dashboard shows exclusive/self and inclusive weights/percentages,
  total samples, and conservative mapping of fully-qualified frame names
  back to Roslyn method IDs. Overloads/dynamic methods that cannot be
  uniquely identified are **left unmatched**.

MCP:

- `performance_hotspots(maxMethods,maxSites)` returns static method rankings,
  individual source-linked cost candidates, and direct call edges.
- `performance_profile_import(filePath,profileIndex,maxFrames)` reads
  a **local .json file inside TENKA_ALLOWED_ROOTS**. The import does not
  launch the application or execute the profiler.
- `performance_profile_current()` returns the last imported profile.

Web API: `POST /api/hotspots/scan`,
`POST /api/hotspots/profile/import`, `GET /api/hotspots/report`.

### Capture a representative .NET workload

Use the supported `dotnet-trace` tool separately, while running your
application with normal inputs. Recent versions offer .NET runtime
sampled-thread-time tracing:

~~~powershell
dotnet tool install --global dotnet-trace
dotnet-trace ps
dotnet-trace collect --process-id 12345 --profile dotnet-sampled-thread-time,dotnet-common --output workload.nettrace
dotnet-trace convert workload.nettrace --format Speedscope
~~~

Use an on-CPU profiler for CPU attribution where possible. Some EventPipe
thread-time samples include waiting threads and cannot be interpreted as
exclusive CPU usage. This version imports **sampled and evented** Speedscope profiles. In
sampled mode, weights come from stack samples; in evented mode,
durations between matched open/close events are accumulated. Evented
durations can reflect elapsed/thread time instead of on-CPU time. Copy the
sampled JSON into a directory covered by `TENKA_ALLOWED_ROOTS`, open the
target solution and import it in **性能ホットスポット**. Never send traces
containing secrets or user data to untrusted third-party services.

Only a measured representative trace can answer which frame actually
consumed most sampled time. A static risk score might be high for a method
that was never called. Conversely, hot code can be computationally costly
without containing suspicious syntax. The imported weight unit is
retained as seconds, milliseconds, bytes, or samples (`none`); no
unwarranted conversion to CPU time is performed.

Profile import is read-only, bounded to a 25 MB JSON file and 200,000
samples/events, and validates frame indices, weights and balanced event
stacks. No profiler executable is invoked by the import tool.

## Deterministic C# analysis (no LLM required)


Start with this mode if you want a reproducible, objective inspection before
asking a model for suggestions. The deterministic engine is driven by **Roslyn**
and reports three explicit evidence levels:

- **compiler:** compiler errors/warnings/information from the actual loaded
  compilation, including diagnostics in MSBuild-generated sources.
- **analyzer:** diagnostic from a DiagnosticAnalyzer explicitly installed
  in the trusted project. The engine uses the project's AnalyzerOptions
  and separates analyzer findings from compiler diagnostics. These analyzers
  are executable managed code: review them before enabling MSBuild trust.
- **semantic_fact:** an unambiguous C# language behavior; for example,
  `throw ex;` within a catch resets the captured exception's stack trace.
  The behavior is certain, but whether it violates the intended contract
  still requires review.
- **candidate:** a pattern that *might* matter, such as boxing, LINQ
  materialization, a blocking async call or resource churn. Performance is
  **not established** until benchmarked under a realistic workload.

### Tools and HTTP API

MCP tools:

- `deterministic_analyze(maxFindings, maxMethodMetrics)`: analyze an opened
  solution and return diagnostics, rule matches, traceable source locations,
  stable SHA-256 finding fingerprints and method metrics including optional
  CFG block/branch counts.
- `deterministic_rules()`: list fixed rule IDs, description, default severity,
  evidence level and next verification method.

Browser: select **決定論的解析**, set finding/metric limits, click
**決定論的解析を実行**, explore the linked findings and export JSON.
HTTP endpoints: `POST /api/deterministic/run`,
`GET /api/deterministic/report` and `GET /api/deterministic/rules`.
No model endpoint is called, and no test executable is launched. These
endpoints require an already-loaded trusted workspace; MSBuild project
evaluation remains explicitly opt-in because project files can execute
custom targets on the host.

**Scope and reproducibility:** The finding fingerprint is derived from
the fixed rules version, project name, project-relative source path,
rule/diagnostic ID, location and supporting excerpt or compiler message.
The in-memory Roslyn snapshot ID is intentionally ephemeral. Findings
are stable across reloading identical source and compilation settings;
paths that move between projects, changed compiler versions, changed
diagnostic messages or line shifts can change fingerprints. Rules and
compiler diagnostics are evaluated across loaded C# documents, and the
report explicitly marks when a finding or method-metric output limit is
reached. The generated-code diagnostics are intentionally preserved.

Metrics such as syntactic complexity are **review prioritization hints**,
not performance measurements or defect probabilities. Static call graphs
may omit reflection, dynamic dispatch and framework routing. The
deterministic engine does not yet perform complete interprocedural
symbolic execution, constraint solving, or formal verification.

## Autonomous local-LLM review

Tenka-chan can now **inspect a bounded queue of high-priority Roslyn methods
with a local OpenAI-compatible chat-completions model**. The runner proposes
unverified hypotheses, logs its progress, and persists all claims in the
SQLite review ledger. It does **not** create executable tests or modify
project code on its own. Test/benchmark generation and Docker execution
remain explicit, separately authorized actions.

Use a llama.cpp server (or another compatible **local** endpoint):

~~~powershell
# In a separate terminal:
llama-server -m C:\Models\your-model.gguf --alias local -c 32768 --port 8080

# In the tenka-chan terminal:
$env:TENKA_ALLOWED_ROOTS = "C:\Projects"
$env:TENKA_TRUST_MSBUILD = "1"
$env:TENKA_LLM_ENDPOINT = "http://127.0.0.1:8080/v1/chat/completions"
$env:TENKA_LLM_MODEL = "local"
dotnet run --project src/TenkaChan.Web/TenkaChan.Web.csproj -c Release
~~~

Open the Web dashboard and load a trusted solution. Select **AI自律レビュー**,
set 1–30 methods and a 1–30 minute budget, then click **レビューを開始**.
You can stop a run and inspect live progress and run history from the same
screen. The review attempts one local model call per method; a malformed
response is recorded as a method error instead of being presented as a defect.

The original stdio MCP server exposes the same orchestration:

- `autonomous_review_start(maxMethods, maxMinutes)`
- `autonomous_review_status(runId)`
- `autonomous_review_stop(runId)`
- `autonomous_review_history(limit)`

First call `workspace_open` before starting through MCP. The run-state JSON
records are stored in `TENKA_DATA_DIR/review-runs` and hypothesis records in
`TENKA_DATA_DIR/review-ledger.sqlite`. An in-progress run is marked
`interrupted` after a process restart. There is a single active run per
process. An active run prevents replacement of its Roslyn workspace through
the provided Web and MCP APIs.

The local model endpoint is restricted to HTTP loopback
(`127.0.0.1`, `localhost`, or `::1`) with the exact
`/v1/chat/completions` path. The client disables proxying and redirects,
limits response size, and applies a 120-second per-call timeout. Code comments
and project content are treated as untrusted data in the model prompt;
this is defense-in-depth, not a formal guarantee against prompt injection.

**Current limit:** the model proposes hypotheses and text-based test ideas,
not runnable test code. Claims remain `suspected` until separately validated
with a trusted oracle and observed experiment. A completed run means the
configured number of methods was investigated, **not** that the solution is
free of bugs or performance regressions.

## Features

| Tool | Function |
| --- | --- |
| `autonomous_review_start` / `autonomous_review_status` | Bounded local-LLM method investigation and current run state |
| `autonomous_review_stop` / `autonomous_review_history` | Cancel a run and retrieve persisted history |
| `performance_evidence_triage` / `performance_method_dossier` | Prioritize real workload cost and inspect code-generation/IL/PDB evidence for a single matched method |
| `trusted_local_workload_measure` / `trusted_local_workload_result` | Trusted host whole-process elapsed/CPU measurements and optional A/B comparison |
| `trusted_local_jit_capture` / `trusted_local_jit_result` | Docker-free JIT machine-code capture of explicitly trusted, already built applications |
| `performance_hotspots` / `performance_profile_import` / `performance_profile_current` | Static candidate ranking plus separately imported measured workload profile |
| `deterministic_analyze` / `deterministic_rules` | Reproducible Roslyn analysis and rule catalog, no LLM required |
| `workspace_open` | Evaluate a trusted .sln, .slnx or .csproj with MSBuildWorkspace and index C# methods |
| `symbols_find` | Find methods by signature/path |
| `symbol_inspect` | Get source, calls/callers, CFG, dataflow and compilation diagnostics |
| `review_scan` | Find LINQ, string, regex, HttpClient, swallowed exception and async-blocking candidates |
| `review_targets` | Prioritize review targets by branching, loops, async, allocations and method size |
| `workspace_diagnostics` | List Roslyn compilation errors/warnings with locations and totals |
| `symbol_references` | Resolve usage locations of a C# method across projects |
| `hypothesis_create` | Create a persistent review hypothesis with an explicit failure condition |
| `evidence_add` | Attach an independently sourced contract or experiment observation |
| `hypothesis_set_status` | Track suspected/reproduced/not-reproduced/refuted outcomes |
| `hypothesis_get` / `hypotheses_list` | Resume evidence review after restarting the MCP process |
| `test_plan_suggest` | Propose null/empty/numeric-boundary inputs using Roslyn parameter types, without inventing expected outputs |
| `experiment_prepare` | Validate C# syntax and build detached xUnit/BenchmarkDotNet project with a project reference |
| `experiment_run` | Execute prepared code **only in an opt-in, network-disabled Docker container**, recording TRX or BenchmarkDotNet output |
| `experiments_list` | List recent saved experiments from the same persistent store |
| `experiment_result` | Reopen structured saved results: test counts, failed test cases and benchmark metrics |
| `experiment_attach_result` | Attach observed sandbox test evidence to a hypothesis (not an LLM assertion) |
| `benchmark_compare_runs` | Compare stored benchmark reports when generated benchmark code and host/runtime match |
| `il_inspect` | Decode managed IL instructions directly from a DLL/EXE without executing it |
| `performance_compare` | Compare samples from matched environments with an exploratory bootstrap interval |

## Prerequisites

- .NET 10 SDK
- A .NET solution/project to inspect
- Docker and a local .NET 10 SDK container image only if running experiments
- Cached NuGet packages in a host directory for offline container restore

## Run the MCP server

```bash
dotnet restore TenkaChan.slnx
dotnet build TenkaChan.slnx -c Release
```

Environment variables (set *before* starting the MCP process):

| Variable | Required | Meaning |
| --- | --- | --- |
| `TENKA_ALLOWED_ROOTS` | yes | Explicit allowlisted directories, separated by OS path separator (; on Windows, : on Unix) |
| `TENKA_TRUST_MSBUILD` | for semantic analysis | `1` confirms that the target's MSBuild files are trusted to be evaluated on the host |
| `TENKA_DATA_DIR` | optional | Directory holding experiment manifests and persistent SQLite review ledger. Defaults to local app data/tenka-chan |
| `TENKA_ENABLE_DOCKER` | for execution | `1` enables the Docker-only execution tool |
| `TENKA_NUGET_PACKAGES` | for execution | Host package cache directory mounted read-only to /nuget |

MCP client configuration example:

```json
{
  "mcpServers": {
    "tenka-chan": {
      "command": "dotnet",
      "args": [
        "C:/tools/tenka-chan/src/TenkaChan.Mcp/bin/Release/net10.0/TenkaChan.Mcp.dll"
      ],
      "env": {
        "TENKA_ALLOWED_ROOTS": "C:\\Projects",
        "TENKA_TRUST_MSBUILD": "1"
      }
    }
  }
}
```

The server speaks **MCP over stdio**. Logging is directed to stderr so stdout
stays a valid JSON-RPC transport.

## Suggested agent workflow

1. `workspace_open` a trusted solution.
2. Use `review_targets` to select interesting methods and `workspace_diagnostics` to identify compilation problems. `review_scan` then finds *candidates*; `symbols_find` and `symbol_inspect`
   to explore relevant code. The agent should inspect call sites and invariants
   before claiming that something is a bug.
3. Use `test_plan_suggest` for boundary inputs, then use `hypothesis_create` and `evidence_add` to record failure conditions and contract/test evidence. For reproducible failures, use `hypothesis_set_status` with `reported_reproduced` only after adding test execution and independent contract evidence. Write standalone C# xUnit test source, then call `experiment_prepare`
   with `kind="test"`, a repository root, and the .csproj path **relative to
   that root**.
4. Optionally enable the Docker runner and call `experiment_run` using the
   returned experiment ID. The default is **execution disabled**.
   For xUnit, a valid TRX file with at least one executed test is required to
   report `tests_passed` or `tests_failed`. Use `experiment_result` to
   retrieve the saved report, then `experiment_attach_result` to tie the
   observed result to a hypothesis. Arbitrary `test_execution` evidence in
   `evidence_add` is rejected.
5. Write BenchmarkDotNet classes with `[Benchmark]` methods and
   `[MemoryDiagnoser]`, and prepare `kind="benchmark"`. The runner exports
   BenchmarkDotNet FullJSON when it completes. Use `benchmark_compare_runs`
   for descriptive mean/Allocation differences from two saved runs of the
   **same benchmark source** on a matching machine/runtime. Use
   `performance_compare` with independent raw samples for a more careful
   exploratory bootstrap estimate. Neither alone proves a regression.
6. Report confirmed/reproduced behaviors separately from speculative
   performance opportunities.

Example tool arguments:

```json
{
  "repositoryRoot": "C:\\Projects\\Sample",
  "projectRelativePath": "src\\Sample\\Sample.csproj",
  "kind": "test",
  "generatedCode": "using Xunit; public sealed class SmokeTests { [Fact] public void OneEqualsOne() => Assert.Equal(1, 1); }"
}
```

## Docker execution limitations

`experiment_run` deliberately **does not start host-side dotnet/test**.
It copies the allowlisted repository and generated project into ephemeral
container storage. The container has no network, limited CPU/memory/processes,
dropped Linux capabilities, read-only bind mounts and an unprivileged user.

1. Pre-pull `mcr.microsoft.com/dotnet/sdk:10.0` yourself.
2. Pre-populate the NuGet global packages folder for the target project,
   xUnit/runner/Test SDK or BenchmarkDotNet. The offline container will not
   download missing packages. The image is started with `--pull=never`.
3. Set `TENKA_ENABLE_DOCKER=1` only for projects you intend to execute.
4. Run `experiment_run`. The test runner is currently synchronous and can
   exceed an MCP client's request timeout. It has a three-minute test or
   eight-minute benchmark execution budget; `experiment_result` can retrieve
   completed runs after reconnecting.
5. The runner restricts console log retention, records sandbox exit codes,
   parses TRX test counters, and stores benchmark JSON summaries inside the
   experiment's artifact folder. A missing or invalid result file is never
   interpreted as a pass.

Container isolation has limitations (Docker daemon, kernel, mount and
supply-chain risks). This is not a hardened multi-tenant execution service.
Treat project files as trusted **only if you understand their MSBuild behavior**.
Never mount secrets or production databases. MSBuildWorkspace evaluates project
files on the host when explicitly enabled.

See [architecture](docs/architecture.md) and [security model](docs/security.md).

## Develop and test

```bash
dotnet test TenkaChan.slnx -c Release
```

The checked-in CI workflow builds and runs deterministic unit tests. It
does **not** execute unknown sample projects, untrusted workflows or Docker
benchmarks automatically.

## Sources

- [Official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [Roslyn](https://github.com/dotnet/roslyn)
- [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet)
- [Meziantou.Analyzer](https://github.com/meziantou/Meziantou.Analyzer)

## Roadmap

- Persistent Roslyn solution indexing/daemon (SQLite review ledger is implemented)
- Symbol-based interprocedural analysis and nullable/reflection limitations
- Test oracle provenance and property-based/stateful test generation
- Golden benchmarks on a dedicated runner, dotnet-trace, JIT/IL inspection
- Git baseline replay, reproducible comparison, flaky-test detection
- Strict sandbox hardening, quota/approval and immutable evidence artifacts

## Review ledger semantics

Execution artifact manifests include hashes of generated C# and project
configuration; modifying either after preparation is rejected. TRX XML parsing
prohibits DTDs/external entities. The container's only writable host bind mount
is its dedicated artifact directory.

The SQLite ledger resides in `TENKA_DATA_DIR/review-ledger.sqlite`. A review
hypothesis is **not** a confirmed defect. `reported_reproduced` is a
provenance-gated *claim*: it requires an actually parsed failing TRX report attached through
`experiment_attach_result` and a separately sourced contract record.
This prevents unsupported LLM claims about executing a test, but **does not
prove that the test's oracle is correct**. Reviewers must still inspect
the source and the test assertion.
`review_targets` is a syntactic heuristic, **not** runtime hotness or defect
probability. `symbol_references` cannot resolve all reflection, dynamic
dispatch or dependency-injection paths.

Fully automated test drafting/execution/verification, dynamic profiling, native JIT disassembly and Git-based regression runs remain future work.
