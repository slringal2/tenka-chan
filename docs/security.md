# Security boundaries (current local architecture)

## Trust model

Tenka-chan is primarily a **local Windows developer tool**. It uses a
loopback-only ASP.NET host and an authenticated stdio MCP adapter, not a
public multi-tenant SaaS server. An externally connected agent can provide
untrusted instructions; the agent's text must not be treated as host approval.
Project source/comments, reports and compiler diagnostics are untrusted
input, not commands.

**Important: MSBuild and host-side target execution are UNSANDBOXED.**
Neither `TENKA_ALLOWED_ROOTS` nor a timeout limits what an authorized
MSBuild target or host process can access at the OS level.

### Source and project opening

- `TENKA_ALLOWED_ROOTS` restricts which user-provided paths are accepted.
  Reparse/symlink checks are best-effort; path validation is **not** an OS
  sandbox and cannot constrain MSBuild imports or code executed by targets.
- `TENKA_TRUST_MSBUILD=1` deliberately enables evaluation of trusted
  `.csproj/.sln/.slnx`. Evaluation and analyzers may execute code.
  Do not load arbitrary untrusted repositories with this enabled.
- Agent `tenka_open_project`, `tenka_reload_workspace` and build/execution
  operations additionally require server policy switches and explicit
  client approval. These are authorization gates, **not proof of an actual
  human click**. Loaded source/selection IDs are snapshot-specific.
- Missing `Compile` sources and unrecognized out-of-root linked files
  fail closed rather than being silently indexed. Very narrow reviewed
  NuGet-generated file exemptions may remove analysis-only synthetic source
  and report that exclusion.

### Local execution paths

- Trusted local JIT/trace/binary execution uses
  `TENKA_ENABLE_LOCAL_EXEC=1` and specific call approval. It launches
  `dotnet` with bounded arguments and a deadline. This is not safe for
  malicious DLLs or arbitrary downloaded code.
- Executing **generated** test/benchmark experiments on the host is
  a separate opt-in requiring both `TENKA_ENABLE_LOCAL_EXEC=1` and
  `TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS=1` plus the policy/approval
  gates. Tests, benchmarks and their MSBuild targets execute with the user's
  permissions. Never infer a generated experiment is harmless.
- The repository also contains an older, separate `DockerExecutor`
  for optional isolated experiments behind `TENKA_ENABLE_DOCKER=1`.
  Docker is **not required or enabled by the normal Windows setup**.
  Isolation settings differ by runtime version and are not an audited
  security guarantee. The Docker path remains experimental/legacy.
- None of the above authorizes background retries, automatically repeating
  an experiment, expanding the workspace allowlist or running a process
  without an explicit trusted selection.

### Connection credentials and stored evidence

- The local loopback agent uses a per-instance bearer credential in a
  local connection file. Treat that descriptor, `mcp-config.json`, launcher
  settings and transcript/logs as **private**. Never commit them or copy
  bearer values into bug reports.
- Review marks, source/context hashes and some investigation/measurement
  evidence are stored locally (including SQLite). Checkpoint state is an
  agent work record, not a verified assertion that code is correct.
  An `optimized` mark is not a statistically verified speedup.
- MCP sessions and large live result pages have retention limits.
  Session expiration/reconnect may discard an oversized result even if a
  smaller durable receipt survives. Do not rerun executed code just to
  regenerate a lost page.
- EventPipe traces, ETLX conversions, debugger symbols and generated output
  can contain proprietary methods, paths, allocation types, exception
  details or other sensitive data. Preserve them locally and review before
  sharing. Parser summaries do not automatically sanitize raw traces.
- Source-to-DLL matching is limited unless independently verified
  (`binariesVerified=false` means **not verified**, not a proven mismatch).

## Publishing this repository

Publishing `main` would also expose reachable Git history and other visible
refs/issues on GitHub. A clean current file tree does **not** establish
that credentials or protected source never appeared in earlier commits.
See [the public-release checklist](PUBLIC-RELEASE-CHECKLIST.md) and
[SECURITY.md](../SECURITY.md) before changing visibility.

GitHub Actions CI is currently deliberately paused (see
[CI-PAUSED](../.github/CI-PAUSED.md)); do not re-enable workflows, Docker or
hosted execution without explicit owner approval.
