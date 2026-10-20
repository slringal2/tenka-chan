using System.ComponentModel;
using ModelContextProtocol.Server;
namespace TenkaChan.Mcp.Agent;

[McpServerToolType]
public sealed class DashboardAgentTools
{
    [McpServerTool(Name = "tenka_connect")]
    [Description("Start or explicitly reconnect an external investigation session to the running Tenka dashboard. Model inference stays in your agent client. Never request model API keys from the browser.")]
    public static Task<string> Connect(DashboardAgentClient client, string goal = "", CancellationToken cancellationToken = default) =>
        client.ConnectAsync(goal, cancellationToken);

    [McpServerTool(Name = "tenka_context")]
    [Description("Read the running Tenka workspace, source snapshot, project/output IDs and server permissions. This does not open a project or run a model.")]
    public static Task<string> Context(DashboardAgentClient client, CancellationToken cancellationToken = default) =>
        client.CallAsync("context", new { }, cancellationToken);

    [McpServerTool(Name = "tenka_sample")]
    [Description("Locate game-lab, raid-lab or ILSpy (ilspy) local samples; returns paths and workflow advice without execution. ILSpy.Analysis.slnx contains only three analysis projects and may have NO BenchmarkDotNet companion. When companions are absent, select target from review_queue, prepare a generated benchmark with tenka_prepare_experiment, then call tenka_measure_target DIRECTLY without run_experiment.")]
    public static Task<string> Sample(DashboardAgentClient client, string sample = "game-lab", CancellationToken cancellationToken = default) =>
        client.CallAsync("sample", new { sample }, cancellationToken);

    [McpServerTool(Name = "tenka_open_project")]
    [Description("Open a trusted .csproj/.sln/.slnx. MSBuild evaluation can execute code: requires host allow-build and approval in this external client. Use current expectedSnapshotId (null only when no project is loaded).")]
    public static Task<string> OpenProject(DashboardAgentClient client, string path, string? expectedSnapshotId = null, bool approved = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("open_project", new { path, expectedSnapshotId, approved }, cancellationToken);

    [McpServerTool(Name = "tenka_reload_workspace")]
    [Description("Explicitly reopen ONLY the currently loaded .sln/.slnx/.csproj after a source change. Requires current snapshotId and approved=true plus TENKA_AGENT_ALLOW_BUILD=1 and MSBuild trust: MSBuild evaluation is UNSANDBOXED and can execute targets. Rejects stale snapshot IDs and busy workspaces, never automatically restores/builds or switches paths. Re-fetch tenka_context, review_queue and target source IDs after reload. Durable marks remain untouched but may become needs_review.")]
    public static Task<string> ReloadWorkspace(DashboardAgentClient client, string snapshotId,
        bool approved = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("reload_workspace", new {snapshotId,approved}, cancellationToken);

    [McpServerTool(Name = "tenka_list_types")]
    [Description("Browse/search semantic classes, partial/nested types, structs and interfaces. Returned typeId/methodId/projectId are EPHEMERAL selection IDs for the currently loaded Roslyn snapshot and Web instance; pass the same snapshotId to the next inspection. After reload/reconnect check tenka_context and reselect. Durable review targetId (64 hex) comes from tenka_review_queue and is resolved with tenka_review_context.")]
    public static Task<string> ListTypes(DashboardAgentClient client, string snapshotId, string? projectId = null, string? query = null, int offset = 0, int limit = 80, CancellationToken cancellationToken = default) =>
        client.CallAsync("list_types", new { snapshotId, projectId, query, offset, limit }, cancellationToken);

    [McpServerTool(Name = "tenka_inspect_type")]
    [Description("Read class structure, members, methods, source and findings. The typeId must come from tenka_list_types in the CURRENT loaded snapshot. If it is no longer available, refresh tenka_context/list_types; do not substitute a durable targetId here. No target execution.")]
    public static Task<string> InspectType(DashboardAgentClient client, string snapshotId, string typeId, CancellationToken cancellationToken = default) =>
        client.CallAsync("inspect_type", new { snapshotId, typeId }, cancellationToken);

    [McpServerTool(Name = "tenka_type_references")]
    [Description("Find static references to a type within the loaded solution. Runtime/reflection targets are not inferred.")]
    public static Task<string> TypeReferences(DashboardAgentClient client, string snapshotId, string typeId, CancellationToken cancellationToken = default) =>
        client.CallAsync("type_references", new { snapshotId, typeId }, cancellationToken);

    [McpServerTool(Name = "tenka_inspect_method")]
    [Description("Read method source, calls/callers, flow/dataflow and compiler diagnostics. The methodId is from the CURRENT loaded snapshot (tenka_inspect_type or review_context), not a persistent targetId. Refresh IDs after a reload or server restart.")]
    public static Task<string> InspectMethod(DashboardAgentClient client, string snapshotId, string methodId, CancellationToken cancellationToken = default) =>
        client.CallAsync("inspect_method", new { snapshotId, methodId }, cancellationToken);

    [McpServerTool(Name = "tenka_method_references")]
    [Description("Find static references to a selected method. Returns navigable enclosing method IDs.")]
    public static Task<string> MethodReferences(DashboardAgentClient client, string snapshotId, string methodId, CancellationToken cancellationToken = default) =>
        client.CallAsync("method_references", new { snapshotId, methodId }, cancellationToken);

    [McpServerTool(Name = "tenka_scan")]
    [Description("Run deterministic source analysis and static hotspot screening, publishing the same result to the dashboard. Findings are candidates, not confirmed defects.")]
    public static Task<string> Scan(DashboardAgentClient client, string snapshotId, CancellationToken cancellationToken = default) =>
        client.CallAsync("scan", new { snapshotId }, cancellationToken);

    [McpServerTool(Name = "tenka_rules")]
    [Description("List deterministic rule IDs, meanings and independent verification guidance.")]
    public static Task<string> Rules(DashboardAgentClient client, CancellationToken cancellationToken = default) =>
        client.CallAsync("rules", new { }, cancellationToken);

    [McpServerTool(Name = "tenka_priorities")]
    [Description("Rank investigation candidates from the current workspace. Priority is not a calibrated bug probability.")]
    public static Task<string> Priorities(DashboardAgentClient client, string snapshotId, CancellationToken cancellationToken = default) =>
        client.CallAsync("priorities", new { snapshotId }, cancellationToken);

    [McpServerTool(Name = "tenka_test_inputs")]
    [Description("Suggest type-based boundary inputs for a method. No oracle, valid DI fixture or expected result is invented.")]
    public static Task<string> TestInputs(DashboardAgentClient client, string snapshotId, string methodId, CancellationToken cancellationToken = default) =>
        client.CallAsync("test_inputs", new { snapshotId, methodId }, cancellationToken);

    [McpServerTool(Name = "tenka_binary_methods")]
    [Description("Find metadata signatures/tokens corresponding to a method in a discovered assembly. Names are candidate links, not proof of source-build identity.")]
    public static Task<string> BinaryMethods(DashboardAgentClient client, string snapshotId, string methodId, string assemblyId, CancellationToken cancellationToken = default) =>
        client.CallAsync("binary_methods", new { snapshotId, methodId, assemblyId }, cancellationToken);

    [McpServerTool(Name = "tenka_il")]
    [Description("Inspect IL/PDB for a chosen metadata token. Use assemblySha256 and token returned by tenka_binary_methods; stale binaries are rejected.")]
    public static Task<string> Il(DashboardAgentClient client, string snapshotId, string methodId, string assemblyId, int metadataToken, string assemblySha256, CancellationToken cancellationToken = default) =>
        client.CallAsync("il", new { snapshotId, methodId, assemblyId, metadataToken, assemblySha256 }, cancellationToken);

    [McpServerTool(Name = "tenka_build")]
    [Description("Release build and analyze a selected project. UNSANDBOXED MSBuild. Requires host allow-build and explicit client approval. Refresh context after completion; IDs change.")]
    public static Task<string> Build(DashboardAgentClient client, string snapshotId, string projectId, bool approved = false, bool allowRestore = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("build", new { snapshotId, projectId, approved, allowRestore }, cancellationToken);

    [McpServerTool(Name = "tenka_jit")]
    [Description("Run the selected executable and capture JIT for a method. UNSANDBOXED; host allow-execution and client approval required. Method must actually be reached. Instruction count is not speed.")]
    public static Task<string> Jit(DashboardAgentClient client, string snapshotId, string methodId, string assemblyId, bool approved = false, string[]? arguments = null, CancellationToken cancellationToken = default) =>
        client.CallAsync("jit", new { snapshotId, methodId, assemblyId, approved, arguments }, cancellationToken);

    [McpServerTool(Name = "tenka_trace")]
    [Description("Run a trusted application with detailed trace capture to an automatic output directory. UNSANDBOXED; approval required. EventPipe thread samples are not pure CPU percentages.")]
    public static Task<string> Trace(DashboardAgentClient client, string snapshotId, string assemblyId, bool approved = false, string[]? arguments = null, CancellationToken cancellationToken = default) =>
        client.CallAsync("trace", new { snapshotId, assemblyId, approved, arguments }, cancellationToken);

    [McpServerTool(Name = "tenka_artifact")]
    [Description("Read an artifact by the ID returned by this source snapshot. No path input.")]
    public static Task<string> Artifact(DashboardAgentClient client, string snapshotId, string artifactId, CancellationToken cancellationToken = default) =>
        client.CallAsync("artifact", new { snapshotId, artifactId }, cancellationToken);

    [McpServerTool(Name = "tenka_native")]
    [Description("Decode saved JIT instructions/branches. Select a method index from the captured listing. Read-only.")]
    public static Task<string> Native(DashboardAgentClient client, string snapshotId, string artifactId, int nativeIndex = 0, CancellationToken cancellationToken = default) =>
        client.CallAsync("native", new { snapshotId, artifactId, nativeIndex }, cancellationToken);

    [McpServerTool(Name = "tenka_compare_native")]
    [Description("Compare saved JIT code generation. Different signatures may be incomparable. No measured speedup is asserted.")]
    public static Task<string> CompareNative(DashboardAgentClient client, string snapshotId, string baselineId, string artifactId, int baselineIndex = 0, int nativeIndex = 0, CancellationToken cancellationToken = default) =>
        client.CallAsync("compare_native", new { snapshotId, baselineId, artifactId, baselineIndex, nativeIndex }, cancellationToken);

    [McpServerTool(Name = "tenka_import_thread")]
    [Description("Import an explicitly selected observed thread into the shared hotspot view. Name association is not source/build identity.")]
    public static Task<string> ImportThread(DashboardAgentClient client, string snapshotId, string artifactId, int profileIndex, CancellationToken cancellationToken = default) =>
        client.CallAsync("import_thread", new { snapshotId, artifactId, profileIndex }, cancellationToken);

    [McpServerTool(Name = "tenka_runtime_costs")]
    [Description("Read saved GC/allocation/Monitor events from a trace. No program launch. Missing events mean not observed, not zero cost.")]
    public static Task<string> RuntimeCosts(DashboardAgentClient client, string snapshotId, string artifactId, CancellationToken cancellationToken = default) =>
        client.CallAsync("runtime_costs", new { snapshotId, artifactId }, cancellationToken);

    [McpServerTool(Name = "tenka_run_companion")]
    [Description("Build/run a discovered existing test or BenchmarkDotNet project. Requires BOTH host build/execution permission and external-client approval. Source reports are invalidated/reloaded.")]
    public static Task<string> RunCompanion(DashboardAgentClient client, string snapshotId, string companionId, bool approved = false, bool allowRestore = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("run_companion", new { snapshotId, companionId, approved, allowRestore }, cancellationToken);

    [McpServerTool(Name = "tenka_prepare_experiment")]
    [Description("Stage agent-authored C# test/benchmark code using a current project ID. Returns experimentId (id) AND durable investigationId. For measured benchmark evidence use prepare_experiment(kind=benchmark) -> tenka_measure_target on the UNEXECUTED experiment; measure_target executes and binds in one approved call. Do NOT call tenka_run_experiment before measure_target (already executed IDs cannot be measured). Use the owning investigationId when crossing adapters, or explicitly resume. No source edits/execution at prepare time. targetFramework selects the library; runtimeFramework selects the runtime for netstandard2.0/2.1. Justify test oracles.")]
    public static Task<string> PrepareExperiment(DashboardAgentClient client, string snapshotId, string projectId, string kind, string generatedCode, string? targetFramework = null, string? runtimeFramework = null, CancellationToken cancellationToken = default) =>
        client.CallAsync("prepare_experiment", new { snapshotId, projectId, kind, generatedCode, targetFramework, runtimeFramework }, cancellationToken);

    [McpServerTool(Name = "tenka_run_experiment")]
    [Description("Execute a generated experiment WITHOUT associating its run with a review target. A completed experiment ID can never be run or measured again. For before/after benchmark evidence do NOT call this first: use prepare_experiment(kind=benchmark) -> tenka_measure_target on the still-unexecuted ID. UNSANDBOXED; host switches and per-call approval required.")]
    public static Task<string> RunExperiment(DashboardAgentClient client, string experimentId, bool approved = false, CancellationToken cancellationToken = default) =>
        client.CallAsync("run_experiment", new { experimentId, approved }, cancellationToken);

    [McpServerTool(Name = "tenka_experiment_result")]
    [Description("Read an experiment owned by the current or explicitly resumed investigation; tests and benchmarks are actual recorded outputs, not model claims.")]
    public static Task<string> ExperimentResult(DashboardAgentClient client, string experimentId, CancellationToken cancellationToken = default) =>
        client.CallAsync("experiment_result", new { experimentId }, cancellationToken);

    [McpServerTool(Name = "tenka_compare_benchmarks")]
    [Description("Compare two completed experiments from the current or explicitly resumed investigation with matching benchmark source and environment. Descriptive comparison, not proof of a regression.")]
    public static Task<string> CompareBenchmarks(DashboardAgentClient client, string baselineExperimentId, string candidateExperimentId, CancellationToken cancellationToken = default) =>
        client.CallAsync("compare_benchmarks", new { baselineExperimentId, candidateExperimentId }, cancellationToken);

    [McpServerTool(Name = "tenka_note")]
    [Description("Publish a brief user-facing plan/progress/hypothesis/conclusion to the monitor. NOT private chain of thought. All notes are labelled agent-reported/unverified, even when referencing tool call IDs.")]
    public static Task<string> Note(DashboardAgentClient client, string kind, string message, string[]? evidenceCallIds = null, CancellationToken cancellationToken = default) =>
        client.CallAsync("note", new { kind, message, evidenceCallIds }, cancellationToken);

    [McpServerTool(Name = "tenka_read_result")]
    [Description("Read a prior tool result using callId and JSON pointer. For a retained review_context or inspect_method response, pointer=/source returns paged data.source directly (offset is character offset, limit is 200-character blocks, max 16000); the existing /inspection/source pointer also remains supported. Prefer tenka_review_focus with snapshotId+durable targetId for full tracked source and analysis in one call. Results >60KB (data=null) are paged ONLY in the SAME live MCP transport session and lost on disconnect/server restart; do not use an ID from another adapter session. Small retained receipts can be read after explicitly resuming the owning investigation; the large paged data itself is not durable. Do not rerun execution merely to retrieve output.")]
    public static Task<string> ReadResult(DashboardAgentClient client, string callId, string pointer = "", int offset = 0, int limit = 30, CancellationToken cancellationToken = default) =>
        client.CallAsync("read_result", new { callId, pointer, offset, limit }, cancellationToken);

    [McpServerTool(Name = "tenka_finish")]
    [Description("Finish this transport session and revoke its temporary approval. Durable investigation records remain available for explicit resume. Publish a concise qualified conclusion with tenka_note first. This does not assert every issue was found or fixed.")]
    public static Task<string> Finish(DashboardAgentClient client, CancellationToken cancellationToken = default) =>
        client.FinishAsync(cancellationToken);
}
