# Automatic DLL/JIT setup and Docker-free experiments

After opening an allowlisted trusted project in the Web UI, the performance
screen auto-discovers previously built DLLs in bin/Release or bin/Debug under
net8.0+ (including common RID subdirectories), plus Roslyn JIT method choices.
No paths or class:method filters need to be typed by default. "静的・決定論・性能解析をまとめて実行"
runs without launching the target. "選択メソッドのJITを取得" runs only after
the user explicitly confirms trusting that executable. The assembly/trace
screens also auto-fill an existing DLL after the workspace opens.

To run generated xUnit/BenchmarkDotNet without Docker, enable both
TENKA_ENABLE_LOCAL_EXEC=1 and TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS=1.
The experimental code is staged in a separate directory, referencing the
already built target DLL. No original source modifications. No automatic
build of the target is performed; build it first in Release if needed.
Packages for generated tests/benchmarks are restored only from the operator's
local NuGet cache with configured package sources cleared. This can fail if
dependencies have not been prewarmed. Run limits apply, and structured
TRX/FullJSON are retained for review.

WARNING: Generated code, tests, build tooling and dependencies execute
UNSANDBOXED with the same OS user permissions. They can access files and the
network and are not safe for untrusted code. Merely enabling the flags never
authorizes a run; each run requires a distinct approval and was deliberately
not made automatic on project open. Execution timeouts and process-tree kill
are best-effort cleanup, not isolation. Save backups and review generated
code beforehand. Users requiring isolation should use an OS sandbox.

DLL discovery is a bounded heuristic over conventional paths and literal
AssemblyName overrides. It does not establish build/source identity or
prove a selected method executes. The user can choose a different detected
method/assembly. No speedup is concluded from native instruction changes.
