# Browser capture workflow

Open `http://127.0.0.1:5077/trace.html` in the running Tenka Web host. The page is separate from the existing eight-screen dashboard and links back to it.

Configure `TENKA_DOTNET_TRACE_PATH`, `TENKA_ENABLE_LOCAL_EXEC` and allowlisted source/output roots as described in [trusted-trace-capture.md](trusted-trace-capture.md). No installer is run by the Web API. New captures require a checked trust acknowledgement and browser confirmation. The cancel button aborts the request, causing best-effort server-side process-tree cancellation; it is not an isolation guarantee.

The page can collect an already-built application, verify and reopen capture.json without execution, select one thread, import it into the current Roslyn session, and compare entry-DLL/sidecar identities. Open the corresponding trusted project in the main dashboard before importing. The imported profile becomes available to the existing measured-first triage and method dossiers.

New local APIs run under the same loopback, Origin and CSRF protections as the rest of the dashboard:

- `POST /api/traces/capture`
- `GET /api/traces/report?reportPath=...`
- `POST /api/traces/import` (explicit `profileIndex` required)
- `POST /api/traces/compare-binary`

The browser writes all captured text with textContent, not HTML. It labels the data as waiting-inclusive sampled thread time, not CPU utilization. Binary matches remain distinct from source/build verification.
