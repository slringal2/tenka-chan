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
| `review_scan` | Find LINQ materialization in loops, string concatenation, repeated regex creation, blocking Task.Result in async |
| `experiment_prepare` | Validate C# syntax and build detached xUnit/BenchmarkDotNet project with a project reference |
| `experiment_run` | Execute a prepared project **in a resource-limited, network-disabled Docker container only** |
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
| `TENKA_DATA_DIR` | optional | Directory holding experiment manifests/source. Defaults to local app data/tenka-chan |
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
2. `review_scan` to obtain *candidates*; `symbols_find` and `symbol_inspect`
   to explore relevant code. The agent should inspect call sites and invariants
   before claiming that something is a bug.
3. Write standalone C# xUnit test source, then call `experiment_prepare`
   with `kind="test"`, a repository root, and the .csproj path **relative to
   that root**.
4. Optionally enable the Docker runner and call `experiment_run` using the
   returned experiment ID. The default is **execution disabled**.
5. Write BenchmarkDotNet classes with `[Benchmark]` methods and
   `[MemoryDiagnoser]`, and prepare `kind="benchmark"`. Compare measured
   samples with `performance_compare` only when the workload and machine
   fingerprint actually match.
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
   download missing packages.
3. Set `TENKA_ENABLE_DOCKER=1` only for projects you intend to execute.
4. Run `experiment_run`. For long benchmarks, timeouts may be insufficient.

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

- Persistent solution indexing and review ledger (SQLite)
- Symbol-based interprocedural analysis and nullable/reflection limitations
- Test oracle provenance and property-based/stateful test generation
- Golden benchmarks on a dedicated runner, dotnet-trace, JIT/IL inspection
- Git baseline replay, reproducible comparison, flaky-test detection
- Strict sandbox hardening, quota/approval and immutable evidence artifacts
