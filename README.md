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
> profiler traces and **native JIT** disassembly are **not yet implemented**.

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
- **Tests and performance:** prepare a detached xUnit/BenchmarkDotNet experiment,
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
set `TENKA_ENABLE_DOCKER=1`, pre-pull
`mcr.microsoft.com/dotnet/sdk:10.0`, and prewarm your offline NuGet cache.
The browser asks for confirmation before launching an experiment.
Review [docs/security.md](docs/security.md) before enabling it.

**Important boundaries:** This is a **local control panel**, not an
autonomous AI chat UI. It uses the same core code as the MCP server, but
the workspace index is per process; the SQLite hypothesis ledger and saved
experiment artifacts are shared when `TENKA_DATA_DIR` matches. In-memory
Roslyn state is not shared between the Web and MCP processes. The browser
does not silently execute code or claim that a static rule proves a defect.

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

- \`performance_hotspots(maxMethods,maxSites)\` returns static method rankings,
  individual source-linked cost candidates, and direct call edges.
- \`performance_profile_import(filePath,profileIndex,maxFrames)\` reads
  a **local .json file inside TENKA_ALLOWED_ROOTS**. The import does not
  launch the application or execute the profiler.
- \`performance_profile_current()\` returns the last imported profile.

Web API: \`POST /api/hotspots/scan\`,
\`POST /api/hotspots/profile/import\`, \`GET /api/hotspots/report\`.

### Capture a representative .NET workload

Use the supported \`dotnet-trace\` tool separately, while running your
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
exclusive CPU usage. Inspect the converted file's profile type: **this
version imports sampled profiles only**; evented Speedscope traces must
first be converted to sampled stacks by a compatible tool. Copy the
sampled JSON into a directory covered by \`TENKA_ALLOWED_ROOTS\`, open the
target solution and import it in **性能ホットスポット**. Never send traces
containing secrets or user data to untrusted third-party services.

Only a measured representative trace can answer which frame actually
consumed most sampled time. A static risk score might be high for a method
that was never called. Conversely, hot code can be computationally costly
without containing suspicious syntax. The imported weight unit is
retained as seconds, milliseconds, bytes, or samples (\`none\`); no
unwarranted conversion to CPU time is performed.

The sampled import is read-only, bounded to a 25 MB JSON file and
200,000 samples, and it validates stack frame indices and nonnegative,
finite sample weights.

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
