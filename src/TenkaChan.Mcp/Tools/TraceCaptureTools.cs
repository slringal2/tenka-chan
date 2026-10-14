using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Review;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Tools;

[McpServerToolType]
public static class TraceCaptureTools
{
    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [McpServerTool(Name = "performance_trace_capture")]
    [Description("Explicit UNSANDBOXED execution of an already-built trusted .NET 8+ runnable DLL via operator-installed dotnet-trace. Requires local execution opt-in and trustedBinary=true. Produces nettrace, Speedscope, thread choices and input hashes. Measures sampled thread time including waits, NOT per-method CPU time. Never builds or installs tools.")]
    public static async Task<string> Capture(WorkspaceGuard guard, string assemblyPath,
        string outputDirectory, bool trustedBinary, int timeoutSeconds = 60,
        string[]? arguments = null, CancellationToken cancellationToken = default) =>
        Json(await new TrustedTraceCapture(guard).CaptureAsync(assemblyPath, outputDirectory,
            trustedBinary, timeoutSeconds, arguments, cancellationToken));

    [McpServerTool(Name = "performance_trace_get")]
    [Description("Read a saved capture.json and verify completed nettrace/Speedscope hashes. Does not execute any process. Returns all selectable thread/profile indexes and explicit measurement limitations.")]
    public static string Get(WorkspaceGuard guard, string reportPath) =>
        Json(new TrustedTraceCapture(guard).Read(reportPath));

    [McpServerTool(Name = "performance_trace_compare_binary")]
    [Description("Read-only comparison of a capture's recorded entry DLL SHA-256/MVID, runtimeconfig and deps identity with an allowlisted current binary. Does NOT verify the whole dependency graph or Roslyn/source build equality.")]
    public static string CompareBinary(WorkspaceGuard guard, string reportPath, string assemblyPath) =>
        Json(new TrustedTraceCapture(guard).CompareBinary(reportPath, assemblyPath));

    [McpServerTool(Name = "performance_trace_import")]
    [Description("Verify saved trace artifacts and import an explicitly chosen thread/profile into the current Roslyn hotspot session. No process launch. Profile matching is name-based, not a verified profile-to-source build association.")]
    public static async Task<string> Import(WorkspaceGuard guard, RoslynReviewService roslyn,
        AutonomousReviewRunner reviewer, string reportPath, int profileIndex,
        int maxFrames = 200, CancellationToken cancellationToken = default)
    {
        if (reviewer.IsActive) throw new InvalidOperationException("Stop active LLM review before replacing the workload profile.");
        var reader = new TrustedTraceCapture(guard);
        var capture = reader.Read(reportPath);
        var path = reader.RequireProfile(capture, profileIndex);
        var snapshot = roslyn.CurrentSnapshotId;
        var report = await roslyn.ImportHotspotProfileAsync(path, profileIndex, maxFrames, cancellationToken);
        return Json(new
        {
            capture.Id, capture.MeasurementKind, capture.Speedscope, profileIndex,
            snapshotId = roslyn.CurrentSnapshotId, workspaceChanged = snapshot != roslyn.CurrentSnapshotId,
            association = "profile_names_only__source_build_not_verified", report
        });
    }
}
