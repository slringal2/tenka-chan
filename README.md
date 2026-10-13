# Tenka-chan — C# Deep Review MCP

Roslyn-powered **C#/.NET code review MCP server**. It exposes semantic code
inspection, conservative optimization-pattern findings, reproducible generated
test/benchmark projects and a guarded, offline Docker execution path to
MCP-capable coding agents. The language model can be local or remote: the
server never requires an LLM API key.

> **Status:** early functional foundation, not an autonomous AI reviewer.
> The MCP tools generate *candidate* findings, not proven defects.
> Generated test and benchmark execution is opt-in and requires a prewarmed
> package cache; full self-directed fuzzing, mutation testing, baseline checkout,
> profiler traces and **native JIT** disassembly are **not yet implemented**.

## Features

| Tool | Function |
| --- | --- |
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

A full autonomous review loop, profiler, native JIT disassembler, and
Git-based benchmark regression runner remain future work.
