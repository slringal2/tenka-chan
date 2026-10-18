# Chan.Core as a separate repository

Chan.Core is hosted in [slringal2/chan-core](https://github.com/slringal2/chan-core) and consumed by Tenka-chan via a **Git submodule**, pinned to a specific commit. Gnar-chan can consume the same repository at its own pinned revision when its C++ host is implemented. The Git history of Tenka-chan retains older in-repository copies.

## Tree layout

```text
tenka-chan/
  .gitmodules
  shared/chan-core/       (Git submodule, pinned revision)
    src/Chan.Core/        (.NET 8 library)
    tests/Chan.Core.Tests/ (standalone xUnit tests)
  src/TenkaChan.Mcp/
  src/TenkaChan.Web/
  tests/TenkaChan.Tests/
```

No new NuGet feed, Docker container, CI pipeline, or hidden copy of the core is required.

## Clone and update

First clone:

```powershell
git clone --recurse-submodules https://github.com/slringal2/tenka-chan.git
cd tenka-chan
```

Existing checkout after a pull:

```powershell
git pull
git submodule update --init --recursive
```

If you already used a normal clone, `setup.bat` detects the missing submodule, **asks for confirmation** and retrieves it with Git. The core repository is private: GitHub access via Git Credential Manager / an authenticated HTTPS session is required. The GitHub ZIP download does **not** contain submodule source and is not a complete build artifact.

## Build and tests

```powershell
dotnet build TenkaChan.slnx
dotnet test shared/chan-core/tests/Chan.Core.Tests/Chan.Core.Tests.csproj
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj
```

On a standalone checkout of `chan-core`:

```powershell
dotnet build src/Chan.Core/Chan.Core.csproj
dotnet test tests/Chan.Core.Tests/Chan.Core.Tests.csproj
```

These are **instructions**, not a claim that the tests have run in this editing environment. PowerShell 5.1 setup and the Windows/MCP integration still require host verification. GitHub Actions workflows stay disabled.

## Updating the pinned core revision

Commit and test changes in `chan-core` first. Then in a trusted Tenka checkout:

```powershell
git -C shared/chan-core fetch origin
git -C shared/chan-core checkout <reviewed-core-sha>
git add shared/chan-core
git commit -m "Bump Chan.Core submodule revision"
```

Update the pin in a reviewable Tenka change. Do not let `git submodule update --remote` implicitly change the production revision without review. The same revision can be used independently by a future Gnar-chan adapter.

## Separation boundary

`Chan.Core` owns performance statistics, normalized benchmark comparisons and lexical/path access checks. Tenka's adapters keep its existing public types, persistence/experiment IDs, MSBuild policy, Roslyn, JIT and IL features, and runtime-execution permissions. The shared path policy is not a sandbox. Moving the code between repositories does not by itself share Tenka's UI, MCP server, persistence or review prioritization.
