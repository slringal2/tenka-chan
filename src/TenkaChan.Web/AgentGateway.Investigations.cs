using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using TrackedTarget = TenkaChan.Mcp.Tracking.ReviewTarget;

namespace TenkaChan.Web;

public sealed partial class AgentGateway
{
    private readonly Lazy<AgentInvestigationStore> _investigationStore = new(() => investigations ?? new AgentInvestigationStore());
    private AgentInvestigationStore Investigations => _investigationStore.Value;

    private IDisposable EnterInvestigation(AgentCallRequest request)
    {
        var live = journal.Monitor(policy.Enabled, policy.AllowBuild, policy.AllowExecution).Sessions
            .Where(s => s.Status is "idle" or "running").Select(s => s.Id);
        Investigations.RetainSessions(live);
        if (request.Tool == "investigation" && Optional(request.Arguments, "action") == "resume")
        {
            var lease = Investigations.ResumeAndEnter(request.SessionId, Str(request.Arguments, "investigationId"), Bool(request.Arguments, "approved"));
            _approvals.Revoke(request.SessionId); // Resume data, never execution grants.
            return lease;
        }
        return Investigations.Enter(request.SessionId);
    }
    private AgentReply CompleteDurable(AgentCallRequest request, AgentReply reply)
    {
        try { Investigations.Record(request.SessionId, journal.InstanceId, request.Tool, reply); return reply; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or InvalidDataException)
        {
            // The actual action has already completed. Never convert persistence failure
            // into an executable retry, erase its outcome, or claim the receipt survived.
            return reply with { PersistenceWarning = "Action completed, but its durable receipt was not confirmed. Read the live result and check storage/budgets. Do NOT replay execution merely to save evidence." };
        }
    }
    private object InvestigationSummary(string session)
    {
        var saved = Investigations.Current(session);
        return new { investigationId = saved.Id, saved.CreatedUtc, saved.UpdatedUtc,
            experimentIds = saved.ExperimentProjects.Keys.ToArray(), evidenceCalls = saved.Evidence.Count,
            remainingEvidenceSlots = AgentInvestigationStore.MaxEvidence - saved.Evidence.Count,
            maxExperiments = AgentInvestigationStore.MaxExperiments, recentEvidence = saved.Evidence.TakeLast(10).ToArray(),
            resume = new { tool = "tenka_investigation", action = "resume", investigationId = saved.Id, approvalRequired = true },
            note = "Persisted data is independent of transport sessions. Resume does not restore approval, reload a workspace, or rerun an experiment. Large live-only data may not be retained; old pre-upgrade sessions cannot be reconstructed." };
    }
    private object InvestigationAction(string session, JsonElement a) => (Optional(a, "action") ?? "status") switch
    {
        "status" or "resume" => InvestigationSummary(session),
        "list" => Investigations.List(),
        _ => throw new ArgumentException("action must be status, list or resume.")
    };
    private IReadOnlyList<ReviewEvidenceLink> ResolveEvidence(string session, string[]? ids)
    {
        if ((ids?.Length ?? 0) > 10) throw new ArgumentException("At most 10 evidenceCallIds are supported.");
        var result = new List<ReviewEvidenceLink>();
        foreach (var id in (ids ?? []).Distinct(StringComparer.Ordinal))
        {
            try { result.AddRange(Investigations.Evidence(session, [id])); }
            catch (KeyNotFoundException)
            {
                // A failed disk save need not destroy still-live same-session evidence.
                // The journal still rejects foreign sessions and unfinished calls.
                result.AddRange(journal.ReviewAttribution(session, [id]).Evidence);
            }
        }
        return result;
    }
    private object ReadInvestigationResult(string session, JsonElement a)
    {
        var call = Str(a, "callId"); var pointer = Optional(a, "pointer") ?? "";
        var offset = Number(a, "offset"); var limit = Number(a, "limit", 30);
        try { return journal.ReadResult(session, call, pointer, offset, limit); }
        catch (KeyNotFoundException) { return Investigations.ReadResult(session, call, pointer, offset, limit); }
    }
    private ReviewBenchmarkEvidence? MeasurementFor(string session, JsonElement a, TrackedTarget target)
    {
        var before = Optional(a, "baselineExperimentId"); var after = Optional(a, "candidateExperimentId");
        if (before is null && after is null) return null;
        if (before is null || after is null) throw new ArgumentException("measurement_pair_required: Supply both baselineExperimentId and candidateExperimentId, or neither.");
        OwnExperiment(session, before); OwnExperiment(session, after);
        var store = Get<ExperimentStore>();
        var baseline = store.Read(before).Manifest; var candidate = store.Read(after).Manifest;
        return ReviewBenchmarkMeasurements.Create(Investigations.CurrentId(session), target,
            baseline, ReviewBenchmarkMeasurements.ReadReport(store, before), Investigations.Measurement(session, before),
            candidate, ReviewBenchmarkMeasurements.ReadReport(store, after), Investigations.Measurement(session, after));
    }
    private Task<object> MeasureTargetAsync(string session, JsonElement a, CancellationToken ct) =>
        Get<DashboardState>().ExploreAsync(Str(a, "snapshotId"), async (index, _) =>
        {
            var guard = Get<WorkspaceGuard>(); var store = Get<ExperimentStore>();
            var id = Str(a, "experimentId"); var project = OwnExperiment(session, id);
            // Scope and permission are resolved under the same workspace gate as execution.
            _approvals.Authorize(session, project, "run_experiment", Bool(a, "approved"), policy.AllowBuild, policy.AllowExecution);
            var manifest = store.Read(id).Manifest;
            if (manifest.Kind != "benchmark") throw new ArgumentException("tenka_measure_target requires a prepared benchmark experiment.");
            var catalog = await index.ReviewCatalogAsync(guard, ct);
            var target = catalog.Require(Str(a, "targetId"));
            if (!AgentInvestigationStore.SameProject(target.ProjectPath, project))
                throw new ArgumentException("Measurement target must belong to the experiment's project.");
            if (!target.Trackable) throw new InvalidOperationException(target.UnavailableReason);
            if ((await catalog.StaleProjectsAsync(guard, true, ct)).Contains(target.ProjectId))
                throw new ReviewConflictException("Reload changed source before measuring the target.");
            var result = await Get<TrustedHostExperimentExecutor>().RunAsync(id, true, ct);
            // A completed run is not turned into a failed/retryable call if the post-check
            // is cancelled or storage fails. The report remains the execution source of truth.
            var unchanged = false; string? warning = null;
            try { unchanged = !(await catalog.StaleProjectsAsync(guard, true, ct)).Contains(target.ProjectId); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { warning = "The post-run source check did not complete; measurement cannot be attached as current."; }
            ReviewRunObservation? observation = null;
            var retained = false;
            try
            {
                var saved = ReviewBenchmarkMeasurements.ReadReport(store, id);
                if (JsonSerializer.Serialize(saved.Report, Json) != JsonSerializer.Serialize(result, Json))
                    throw new InvalidDataException("Execution report changed before its target observation was recorded.");
                observation = new ReviewRunObservation(id, target.Id, target.ProjectPath,
                    index.SnapshotId, target.SourceHash, target.ContextHash, unchanged,
                    saved.Sha256, result.CompletedUtc);
                Investigations.SaveMeasurement(session, observation); retained = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or InvalidDataException)
            { warning = "Benchmark returned, but target observation was not saved. Read the stored report; do not replay this experiment to save metadata."; }
            return (object)new { result.Status, experimentId = id, investigationId = Investigations.CurrentId(session),
                result, observation, observationRetained = retained, persistenceWarning = warning,
                independentlyVerified = false,
                note = "Use separate before/after experiments with identical workload, then attach their IDs to tenka_mark_review. Observed source/context is not proof that the existing DLL matches source or that the benchmark exercised the target." };
        }, ct);
}
