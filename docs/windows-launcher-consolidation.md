# Windows launcher consolidation (2026-10-18)

Use **setup.bat** to configure your workspace, permissions, and local dependencies; use **start.bat** for subsequent launches. Both already call `scripts/tenka-windows.ps1` (UTF-8 BOM) and use the per-checkout `settings.json`.

The legacy `scripts/start-tenka-local.ps1` is now a **BOM-encoded wrapper** that calls the same canonical Start path without writing a separate `TENKA_ALLOWED_ROOTS`, enabling host experiments, installing tools automatically, or changing execution approvals.

Old overrides `-WorkspaceRoot`, `-ModelEndpoint`, and `-ModelName` are explicitly rejected instead of silently discarded. Change workspace or permissions with **setup.bat**; configure models in the external MCP client. `scripts/test-windows-setup.ps1` checks both entry points for BOM and PowerShell parser errors before the existing UI tests.

**No CI changes.** The launcher still cannot be executed from the current authoring environment because Windows PowerShell 5.1 and .NET SDK are unavailable; the actual launcher smoke remains a Windows-side check.
