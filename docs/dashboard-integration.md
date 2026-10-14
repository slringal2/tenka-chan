# Dashboard / preparation workbench integration

This is a front-end-only update. The running Web server already shares DashboardState between the preparation workbench and the dashboard; reopening the project is not required to see a completed analysis. Startup/setup alone does not analyze the selected target.

## User-visible behavior

- The main dashboard now loads the native path-picker script. Seven absolute-path fields have browse buttons: project, JIT assembly, baseline/candidate assemblies, profile JSON, source file, and experiment root folder. Relative project paths, saved DLL names and result IDs remain ordinary text fields.
- Sidebar links expose build/preparation, assembly analysis, profile capture and review priorities. The preparation result panel links back to the overview, deterministic report and hotspots via a fixed allowlisted hash route.
- A status panel distinguishes not loaded, not scanned and a completed scan with zero findings. The opened project path is reflected in the dashboard without replacing a manual edit, including a deliberately cleared input.
- Returning to a tab requests a read-only refresh. Visible pages check the overview revision every five seconds, fetching details only after changes; the explicit refresh button and focus/visibility events force a refresh. No background browser timer sends mutations or runs analyses.
- Refresh calls are coalesced. Existing explicit operations invalidate pending refreshes. The bridge checks the overview before and after detail reads and verifies the quick-config snapshot before publishing the bundle. Failed reads are not labelled successful.
- When the workspace changes, method IDs, displayed dossiers and profile/JIT association selections are cleared rather than presented as belonging to the new workspace. Multiple DLL candidates require an explicit choice unless the user already selected one; a single candidate can be filled automatically.

## Scope / boundaries

The source workspace lives in one Web process. Use the same host/port; separate server launches, other ports and MCP processes do not share it. Restarting the server loses the in-memory source analysis, but saved experiment/review records are separate. No cross-process persistence was added.

The endpoints remain separate reads, not an atomic report API. Before/after revision checks reject detectable concurrent workspace/scan changes; they do not prove source/DLL build identity or detect every profile replacement with identical overview counts. Focus or manual refresh reads reports even when those counts are unchanged. Existing explicit dashboard actions retain their own behavior. This module bridges the existing classic-script app functions rather than replacing the analyzer or executing code.

No C#, launcher, NuGet, MSBuild or CI configuration is changed. For the normal start.bat source-tree launch, copy the updated wwwroot files or pull main and use Ctrl+F5 without terminating the server, preserving the analysis already loaded. A separately published deployment must update its actual static-file destination.

## Validation

Executed locally on Node.js 22:

```sh
node --check src/TenkaChan.Web/wwwroot/dashboard-sync.js
node --test tests/Fixtures/dashboard-sync.test.cjs tests/Fixtures/path-picker.test.cjs
```

26 new dashboard tests and 13 existing path-picker tests passed (39 total). They cover read-only retrieval, unchanged polling, forced refresh, completed scans, restart/unloaded state, mismatched snapshots, concurrent invalidation, request coalescing, failure handling, query preservation, manual input, ambiguous DLLs and HTML wiring. The existing path-picker tests use mocked HTTP/minimal DOM. HTML IDs and the original dashboard body were checked while adding the new entry points.

Not run: a real browser against ASP.NET, Windows native dialogs, or .NET compilation/runtime tests. The environment has no .NET SDK or installed Playwright browser; this change does not claim end-to-end or Windows verification. GitHub CI remains paused.
