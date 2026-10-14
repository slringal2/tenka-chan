# Unified workbench layout and GameLab sample

## What changed

The four workbenches (build preparation, assembly, trace, priorities) now load the same shared frame/theme as the dashboard. Their independent assembly.css / trace.css / priorities.css themes are no longer loaded. common-shell.js supplies one navigation definition and the shared sidebar/header/body layout; it moves the existing form nodes rather than cloning them. It is the first defer script, so existing initialization finds the same IDs after the layout is mounted. No iframe, arbitrary HTML injection, code execution, or permission expansion is used. Main-dashboard buttons keep their existing data-view contract; workbench links use same-origin URLs. The selected item is highlighted on every page. The sidebar scrolls on short desktops and becomes a horizontal strip on small displays.

The dashboard now links to /guide.html. This page explains which screen to use and walks through source findings, measured events, JIT and correctness/performance checks. The sample uses game inventory, loot logging, damage buckets and optional Monitor contention, with baseline and improvement candidates in one runnable .NET 8 DLL. This avoids the current JIT picker limitation where the executable project is selected separately from a class library's methods.

GET /api/samples/game-lab discovers only the fixed bundled source-tree sample. Paths are checked against WorkspaceGuard. Opening the guide is read-only; the user clicks to open the preparation screen with a prefilled project path, then explicitly loads/builds it. An existing DLL flag is only an existence hint, not validated binary/source identity. Missing or unauthorized samples remain unavailable. A published server without the source tree can still use manually selected projects.

The Windows launcher now selects GameLab for new bundled-sample setups and migrates only the old built-in sample setting during explicit Setup mode. It retains the previous DLL-lock check and does not kill an already-running server. Samples include a Japanese run-lab.bat menu, self-verification, xUnit and BenchmarkDotNet projects, plus copyable snippets for the existing generated-experiment page. Optional packages are not added to the sample executable.

## Update and use

This update includes C# and launcher changes, not just static files. Stop the existing Tenka process from its launch console before updating/building, then pull main and run setup.bat. Do not build on top of the running DLLs. Open **使い方・サンプル** from the dashboard. A server restart loses the in-memory source workspace, so load the sample again. UI navigation alone never reloads or executes it.

The small legacy fixture remains in place for existing tests. New sample source: samples/Tenka.Sample.GameLab. Full instructions are in its README.md and the guide page. Tests/Benchmarks sources are excluded from the runnable sample's compile glob.

## Validation actually executed for this revision

- Node.js syntax checks for the added scripts.
- 26 new route/tutorial/HTML-contract tests plus the 39 previous dashboard-sync/path-picker tests: **65 passed**.
- Chromium DOM/layout fixtures at widths 1440, 390 and 320: **18 page-size cases passed**, with common sidebar dimensions/theme, active item, preserved IDs/inputs, idempotence, keyboard/link activation and no page-level horizontal overflow on the workbenches/tutorial.
- The Chromium test uses set_content and the actual new shared code/theme with a mocked sample catalog. It stubs/omits unchanged application scripts and the old detail stylesheet. Links are activated without navigating. It is **not** a real ASP.NET, original-dashboard-action, native-dialog or end-to-end integration test. Normal local HTTP navigation was blocked by the authoring browser's managed policy; this isolated DOM test is the verified scope.
- XML parsing of the three sample project files, Python syntax checks, BAT ASCII/CRLF, PowerShell BOM preservation and changed-file whitespace checks.

Not run: C# compilation; the 23 GameLab xUnit cases; 7 BundledSampleCatalog C# cases; the 35-assertion sample verifier; actual Roslyn detection on the sample; Windows BAT/PowerShell; JIT capture; real BenchmarkDotNet or trace measurements. The authoring environment has no .NET SDK/PowerShell, and attempted SDK download was unavailable. These test definitions are not claimed as passed. No speedup, capture completeness, source/binary equivalence or defect precision is inferred from them.

Optional actual-server check after building: `python tests/Fixtures/game_lab_smoke.py --approve-msbuild`. It explicitly opens the trusted sample and verifies expected Roslyn rule IDs. It was authored, not executed here. Use the sample's run-lab.bat for runtime correctness/benchmark checks.

CI definitions and GitHub Actions settings were not changed or re-enabled. No Docker dependency was added.

## Measurement boundaries

The no-argument stopwatch output is a reference demonstration, not a statistically controlled result. Duration-based traces process different iteration counts across versions; compare bytes/op with BenchmarkDotNet instead of treating aggregate bytes as an improvement ratio. NoInlining keeps sample entry methods visible but does not disable other JIT optimizations. The modulo/mask pair has an explicit shared contract (nonnegative hits and power-of-two bucket count); it is not a general optimization of signed remainder. Synthetic independent work inside/outside a lock teaches contention; it does not justify changing an arbitrary critical section.

Primary reference for the NoInlining boundary: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.methodimploptions?view=net-10.0
Benchmark pattern reference: https://benchmarkdotnet.org/articles/samples/IntroBasic.html
