using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Tools;

[McpServerToolType]
public static class RuntimeCostTools
{
    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [McpServerTool(Name = "performance_runtime_costs")]
    [Description("Read a completed capture's hash-verified original nettrace. Separate GC collection lifetimes, GC suspension windows, allocation ticks and managed Monitor contention durations per PID/CLR. No execution or symbol download. Counts first-chance ExceptionThrown_V1 by type/PID/CLR without retaining messages; caught throws are NOT failures. Never infers CPU time or source causality. Optional pauseBudgetMs only affects investigation priority.")]
    public static async Task<string> Read(WorkspaceGuard guard, string reportPath, int? processId = null,
        int maxEvents = 500000, int maxRows = 100, double? pauseBudgetMs = null, CancellationToken cancellationToken = default) =>
        Json(await new RuntimeCostReader(guard).ReadAsync(reportPath, processId, new(maxEvents, maxRows, pauseBudgetMs), cancellationToken));

    [McpServerTool(Name = "performance_runtime_capture")]
    [Description("Explicit UNSANDBOXED verbose runtime-event capture of a trusted, already-built .NET 8+ app. Requires TENKA_ENABLE_LOCAL_EXEC=1 and trustedBinary=true, plus operator-installed dotnet-trace. Additional GC/allocation/exception events increase overhead. Raw nettrace can contain sensitive exception messages even though parsed summaries discard them. Never builds code/installs tools. This operation executes the application; performance_runtime_costs only reads its saved evidence.")]
    public static async Task<string> Capture(WorkspaceGuard guard, string assemblyPath, string outputDirectory,
        bool trustedBinary, int timeoutSeconds = 60, string[]? arguments = null, CancellationToken cancellationToken = default) =>
        Json(await new TrustedTraceCapture(guard).CaptureAsync(assemblyPath, outputDirectory, trustedBinary,
            timeoutSeconds, arguments, cancellationToken, runtimeCostEvents: true));
}
