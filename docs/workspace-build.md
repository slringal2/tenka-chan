# Trusted workspace build and preparation (no Docker / no CI)

PR #18 was merged into main as `81f12234260859f45a865cbc9309314c62fb2f63` at the owner's explicit request. CI stays paused. The follow-up in this document adds a local build/preparation workflow; it does not enable any GitHub Actions definition.

## Browser workflow

1. Start Tenka with `scripts/start-tenka-local.ps1`, using `-WorkspaceRoot` for the trusted source directory. The existing startup script sets `TENKA_TRUST_MSBUILD=1`, `TENKA_ENABLE_LOCAL_EXEC=1`, and keeps Docker off.
2. Open `http://127.0.0.1:5077/workspace-build.html`, or use **アセンブリ解析 -> ビルドと解析の準備**. Load a trusted `.csproj`, `.sln`, or `.slnx`. An already-open workspace is reused.
3. Select a project only when several are present. A single project is selected automatically. The framework is optional; blank uses the project's build settings. Configuration is Release. Optional explicit frameworks must be .NET 8+.
4. Choose whether to allow implicit NuGet restore. It is off by default. Approve the host build in the confirmation dialog. On success the workspace is reopened and DLL candidates are rediscovered. The default checked option also runs source rules, deterministic analysis, and static performance-hotspot analysis. Existing dashboard report screens show those updated results.

The small .NET 8 sample is `tests/Fixtures/WorkspaceBuildSample/WorkspaceBuildSample.csproj`. Enable NuGet restore for its first build if the required assets/reference packs are not already restored. It produces `WorkspaceBuildSample.dll` and a runnable entry point that calls `SampleWork:SumSquares`, useful for the separate trusted-local JIT screen. JIT capture for this sample is not automatically launched or validated by this change.

## Guarantees and limits

- Building requires MSBuild trust, the local execution flag, and explicit per-run consent. POST endpoints inherit the existing loopback, same-origin, and CSRF checks. No arbitrary command line is accepted.
- Only a currently loaded, allowlisted `.csproj` can be built. The caller supplies the current workspace snapshot ID. A stale ID or a busy workspace is rejected; no second build is queued and no failed build is automatically retried.
- `dotnet build` uses `ProcessStartInfo.ArgumentList`, no shell, one MSBuild worker, and disabled build servers/shared compilation. The command runs from the project directory so its ordinary SDK/global.json resolution applies.
- The 10-300 second timeout covers process exit and stdout/stderr draining. Each stream retains its last 32,000 characters. Process-tree termination is best effort, not an OS isolation guarantee; a child that escapes the process tree may remain.
- This is **UNSANDBOXED**. MSBuild imports, custom targets, generators and package tasks can execute code with the user's privileges. Clearing ambient tool variables does not prevent access to credentials in files. `--no-restore` only disables implicit NuGet restore; it does not stop build targets from using the network or writing files.
- Existing analysis and source/profile associations are invalidated before building, including on failure. A failed build does not automatically choose an older DLL. After a build/reload failure, reopen the project before retrying. A successful build with an incomplete reload/analysis is displayed separately from complete preparation.
- DLL outputs remain **candidates**, not verified source/build identities. Imported output settings and unusual build layouts can still evade the read-only discovery routine. Runtime/dependency preflight is not exhaustive. Roslyn uses its existing workspace evaluation settings; this change does not prove configuration/TFM equivalence between its source snapshot and the selected Release output.
- Combined static reports are published only after all static stages finish while holding the workspace gate. They are investigation candidates, not proven bugs or measured CPU/GC costs. Quick-config reads now reject a busy session instead of racing workspace replacement.
- The build service does not launch the target application, JIT capture, generated tests, benchmarks, profiling or an LLM. Those retain their separate explicit actions and consent. The build lock is per Tenka process, not a lock against separate IDE/MSBuild/Tenka processes.

## Validation for this change

Executed in the authoring environment:

- `node --check src/TenkaChan.Web/wwwroot/workspace-build.js`.
- `node --test tests/Fixtures/workspace-build-ui.test.cjs`: **18 tests passed**, using mocked HTTP responses, not a running ASP.NET server or browser.
- Python syntax compilation for the opt-in HTTP smoke script.
- HTML element-ID/wiring checks and `git diff --cached --check` for the authored changes.

Not executed: C# compilation, the **23 added C# test cases**, the earlier DLL-discovery C# tests, real ASP.NET/MSBuild HTTP integration, browser rendering, or Windows runtime/JIT execution. The authoring environment has no .NET SDK, and SDK download attempts failed because external network access was unavailable. No CI was re-enabled as a workaround.

### Local regression commands

```powershell
# Add -Restore when development test dependencies need downloading.
.\scripts\test-local-build.ps1 -Restore
```

The C# tests use an injected process runner to check authorization, allowlists, framework/timeout validation, argument construction, environment filtering, status handling and refusal of concurrent builds. They do not themselves execute a real project build. Node.js 18+ is needed only for the JavaScript tests, not for running the Web UI.

To exercise the real HTTP path after starting the local dashboard with this repository allowlisted:

```powershell
python tests/Fixtures/workspace_build_smoke.py --approve-host-build --allow-restore
```

That smoke test explicitly builds the trusted sample, checks denial without approval and for stale snapshots, checks source reload/static analysis/DLL discovery, and ensures the old snapshot is rejected afterward. Its `--approve-host-build` flag is mandatory. It was added but **not run** in the authoring environment.
