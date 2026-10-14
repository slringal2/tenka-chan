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
    [McpServerTool(Name = "performance_runtime_costs")]
    [Description("Read a completed capture's hash-verified original nettrace. Separate GC collection lifetimes, GC suspension windows, allocation ticks and managed Monitor contention durations per PID/CLR. No execution or symbol download. Never infers CPU time or source causality. Optional pauseBudgetMs only affects investigation priority.")]
    public static async Task<string> Read(WorkspaceGuard guard, string reportPath, int? processId = null,
        int maxEvents = 500000, int maxRows = 100, double? pauseBudgetMs = null, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await new RuntimeCostReader(guard).ReadAsync(reportPath, processId,
            new(maxEvents, maxRows, pauseBudgetMs), cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
