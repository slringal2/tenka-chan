# Architecture v0.1

```text
MCP client / local LLM
         |
    MCP stdio host
         |
  +------+---------+--------------------+
  |                |                    |
Roslyn session  Rule scanner      ExperimentStore
  |                |                    |
MSBuildWorkspace  conservative        syntax validation
  |             rule candidates        detached project
  |                                     |
symbol index, CFG,                    DockerExecutor
dataflow, diagnostics                 network-off worker
                                         |
                              xUnit / BenchmarkDotNet
```

## Deliberate boundaries

- Semantic information comes from Roslyn, **not** LLM reconstruction.
- The server treats optimization warnings as *measurement candidates*.
- No test code is injected into the original repository.
- Tests and benchmarks are only run after an explicit call and environment
  opt-in, and only within Docker.
- Source/experiment references are bounded by explicitly allowed directories.
- `workspace_open` evaluates project files via MSBuild and requires an
  explicit trust switch. It must never be used on unknown code without a
  stronger isolation boundary.
- Snapshot IDs only live for the current MCP server lifetime. Durable
  experiments have UUID-based IDs and manifests on disk.

## Finding categories

- PERF1001: LINQ materialization (`ToList`/`ToArray`) inside a loop.
- PERF1002: string `+=` in a loop.
- PERF1003: `new Regex(...)` inside a loop.
- PERF1004: LINQ `Count()` used in a comparison inside a loop.
- ASYNC1001: `Task<T>.Result` accessed in an async method.

These are intentionally not high-confidence bug findings. Rules rely on
resolved semantic symbols and may skip code with unresolved references.

## Experiment lifecycle

```text
LLM hypothesis (external to this server)
  -> experiment_prepare(kind,test/benchmark,generatedCode)
  -> Roslyn C# syntax parse -> manifest + generated project
  -> OPTIONAL experiment_run(id) -> offline Docker
  -> independent result interpretation
  -> performance_compare(matched sample series)
```

A syntax-valid generated test can still contain compilation or behavioral
errors. Passing a test proves only the asserted behavior for that input.

## Next architectural milestones

1. Move Roslyn Workspace to a separate persistent daemon keyed by git
   revision, SDK, target framework and compilation options.
2. Make test-oracle source and coverage mandatory experiment metadata.
3. Integrate baseline checkout and matching benchmark replay.
4. Add typed MCP resources for large evidentiary artifacts rather than
   returning unbounded logs through tool calls.
5. Add property-based testing, fuzzing and mutation testing with seed replay.

The `il_inspect` tool reads managed IL using PEReader/MetadataReader without executing target assemblies. Native JIT disassembly remains future work.


## Structured experiment verification

- `experiment_prepare` writes generated source, a detached project and SHA-256
  hashes for both generated files. A later source/project edit invalidates the
  experiment manifest.
- `experiment_run` has explicit opt-in and Docker-only command templates,
  read-only source/packages mounts and a single writable artifacts directory.
  The container has no network and cannot pull images at runtime.
- Generated xUnit tests must write a TRX result report. Zero discovered tests,
  missing reports, failed tests and infrastructure failures are distinct states.
- BenchmarkDotNet FullJSON summaries expose means, medians, samples and
  allocations. Run comparisons require identical generated benchmark source
  and a matching environment fingerprint.
- Logs are bounded and saved result records survive MCP process restart.
  `experiment_result` retrieves a completed record.
- `experiment_attach_result` can associate parsed failing-test evidence
  with the SQLite review ledger. Arbitrary `test_execution` evidence is
  rejected.
- Benchmark pair comparisons are **descriptive only**. No statistical
  significance or Git commit regression is asserted from aggregated means.
