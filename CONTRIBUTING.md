# Contributing to Tenka-chan

Tenka-chan is a Windows-first C#/.NET code-analysis project. It exposes
deterministic Roslyn/IL/JIT/trace evidence to an **external** MCP agent. The
Web UI is primarily a monitor; it does not run its own language model.

## Setup and local tests

- Windows 11, Windows PowerShell 5.1+, .NET 10 SDK. `global.json` permits an
  installed later .NET 10 feature band.
- Run `setup.bat` once and `start.bat` afterward. For local checks use
  `check-tenka.bat`. Stop any running Release host before rebuilding its DLLs.
- `dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Debug`
- `dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Debug`
- `dotnet test tests/Chan.Core.Tests/Chan.Core.Tests.csproj -c Debug`

The main test fixtures and synthetic review data do not require Docker, Node
or a paid model. Some integration tests require an installed diagnostic tool,
a trusted local sample and/or an explicit opt-in flag; report skipped tests
separately from passing tests.

## Architecture and change boundaries

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Source files live in
`src/Chan.Core`, `src/TenkaChan.Mcp`, and `src/TenkaChan.Web`. Production
samples and fixtures are kept in `samples/` and `tests/`.

- Prefer deterministic source/IL/runtime observations over unverified LLM
  interpretations. Clearly distinguish candidate findings from measured facts.
- Preserve the workspace path allowlist, read-only APIs and explicit build/
  execution approval; MSBuild is **not** sandboxed.
- Never treat `optimized` as verified speedup or `checked` as verified
  absence of defects. Keep source/context hashes, revisions and conflicts
  stable unless a migration is explicitly designed and tested.
- Avoid changing older release/experiment records in place; keep additive,
  bounded fields and backwards-compatible read paths.
- Add regression tests for incorrect input, stale IDs, data truncation and
  failure/cancellation, not just the happy path.
- Large OSS `samples/External/` checkouts are obtained separately, ignored
  by Git and must not be pasted into a pull request.

## Pull requests and CI

GitHub CI is deliberately **paused** in this project. Do not add or enable
`.github/workflows/` without the maintainer's explicit request. Provide
the exact locally run build/test commands and results, or explicitly state
that they have not been run. No Docker requirement is introduced.

Do not include secrets, local MCP connection descriptors, machine paths
from confidential workspaces, third-party binaries or copyrighted artwork.

## License and contributions

Contributions are proposed under the project's MIT license **only where the
contributor has the right to license their contribution**. Do not submit
employer-owned, restricted, third-party or copied code without authorization.
Dependencies keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
