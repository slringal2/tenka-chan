using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Mcp.Tools;

[McpServerToolType]
public static class PerformanceInvestigationTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [McpServerTool(Name = "performance_triage")]
    [Description("Rank methods using all imported profile observations before display limits, plus independent static hints. Retains unmatched weight; never equates time-like units with CPU time. No code or LLM execution.")]
    public static async Task<string> Triage(PerformanceInvestigationService service,
        string ranking = "self", int limit = 100, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.TriageAsync(ranking, limit, cancellationToken), JsonOptions);

    [McpServerTool(Name = "performance_investigate")]
    [Description("Produce a read-only per-method dossier: loaded source/CFG, observed profile cost, static sites, and optionally captured JIT/IL/PDB evidence. Supply experiment ID, captured assembly filename and MethodDef token together. Build identities are not inferred from names.")]
    public static async Task<string> Investigate(PerformanceInvestigationService service,
        string methodId, string? jitExperimentId = null, string? assemblyFile = null,
        int? methodToken = null, string? sourceFilePath = null, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.InvestigateAsync(methodId, jitExperimentId,
            assemblyFile, methodToken, sourceFilePath, cancellationToken), JsonOptions);
}
