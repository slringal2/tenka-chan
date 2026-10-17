using System.Text.Json;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Mcp.Agent;

public sealed record SavedInvestigation(string Id, string CreatedUtc, string UpdatedUtc,
    IReadOnlyList<ReviewEvidenceLink> Evidence, IReadOnlyDictionary<string, string> ExperimentProjects)
{
    public int Schema { get; init; } = 1;
}

/// <summary>
/// Narrow port of PR #20's durable ownership/receipts. Transport sessions, source
/// snapshots and execution grants are NOT restored. Local-machine credential scope;
/// resume requires approval and never trusts a self-reported client name.
/// The gateway holds Enter/ResumeAndEnter through execution AND durable recording.
/// </summary>
public sealed class AgentInvestigationStore
{
    public const int MaxEvidence = 4096, MaxExperiments = 256;
    private readonly string _root;
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
    public AgentInvestigationStore() : this(InvestigationFiles.DataRoot) { }
    public AgentInvestigationStore(string dataRoot) => _root = InvestigationFiles.DirectoryAt(
        Path.Combine(Path.GetFullPath(dataRoot), "investigations-v1"));
    private string Folder(string id) => Path.Combine(_root, InvestigationFiles.Id(id));
    private SavedInvestigation Read(string id)
    {
        var record = InvestigationFiles.Read<SavedInvestigation>(Path.Combine(Folder(id), "record.json"));
        if (record.Schema != 1 || record.Id != id || record.Evidence is null || record.ExperimentProjects is null ||
            record.Evidence.Count > MaxEvidence || record.ExperimentProjects.Count > MaxExperiments ||
            record.Evidence.Select(e => e.CallId).Distinct(StringComparer.Ordinal).Count() != record.Evidence.Count)
            throw new InvalidDataException("Investigation record is inconsistent or exceeds its budget.");
        foreach (var e in record.Evidence) InvestigationFiles.Id(e.CallId);
        foreach (var pair in record.ExperimentProjects)
        {
            InvestigationFiles.Id(pair.Key);
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 4000 || !Path.IsPathFullyQualified(pair.Value))
                throw new InvalidDataException("Invalid experiment project provenance.");
        }
        return record;
    }
    public string CurrentId(string session)
    {
        InvestigationFiles.Id(session);
        lock (_sync)
        {
            if (_bindings.TryGetValue(session, out var bound)) return bound;
            var id = Guid.NewGuid().ToString("N");
            var folder = InvestigationFiles.DirectoryAt(Folder(id));
            var now = DateTimeOffset.UtcNow.ToString("O");
            InvestigationFiles.Write(Path.Combine(folder, "record.json"),
                new SavedInvestigation(id, now, now, [], new Dictionary<string, string>(StringComparer.Ordinal)));
            _bindings.Add(session, id); return id;
        }
    }
    public void Unbind(string session) { lock (_sync) _bindings.Remove(session); }
    public void RetainSessions(IEnumerable<string> sessions)
    {
        var live = sessions.ToHashSet(StringComparer.Ordinal);
        lock (_sync) foreach (var id in _bindings.Keys.Where(id => !live.Contains(id)).ToArray()) _bindings.Remove(id);
    }
    public SavedInvestigation Current(string session) => Read(CurrentId(session));
    public IDisposable Enter(string session) => InvestigationFiles.Lock(Folder(CurrentId(session)), "operation.lock");
    public IDisposable ResumeAndEnter(string session, string id, bool approved)
    {
        InvestigationFiles.Id(session); InvestigationFiles.Id(id);
        if (!approved) throw new UnauthorizedAccessException("investigation_resume_approval_required: Explicitly approve access to the selected stored investigation; clientName does not grant access.");
        if (!Directory.Exists(Folder(id))) throw new KeyNotFoundException("investigation_not_found: List investigations and select an existing ID.");
        var lease = InvestigationFiles.Lock(Folder(id), "operation.lock");
        try
        {
            _ = Read(id);
            lock (_sync) _bindings[session] = id;
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    public object List()
    {
        var paths = Directory.EnumerateDirectories(_root).Take(201).ToArray();
        var items = new List<object>(); var unavailable = 0;
        foreach (var path in paths.Take(200))
        {
            try
            {
                var saved = Read(Path.GetFileName(path));
                items.Add(new { investigationId = saved.Id, saved.CreatedUtc, saved.UpdatedUtc,
                    evidenceCalls = saved.Evidence.Count, experiments = saved.ExperimentProjects.Count });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or InvalidDataException)
            { unavailable++; }
        }
        return new { items, limited = paths.Length > 200, unavailable,
            note = "Bounded local-machine listing, not a multi-user identity boundary. Resume is explicit; no execution grant is restored." };
    }
    private void Update(string session, Func<SavedInvestigation, SavedInvestigation> change)
    {
        var id = CurrentId(session); var folder = Folder(id);
        using var lease = InvestigationFiles.Lock(folder, "record.lock");
        var updated = change(Read(id)) with { UpdatedUtc = DateTimeOffset.UtcNow.ToString("O") };
        InvestigationFiles.Write(Path.Combine(folder, "record.json"), updated);
    }
    public void AddExperiment(string session, string id, string projectPath) => Update(session, record =>
    {
        InvestigationFiles.Id(id);
        var project = Path.GetFullPath(projectPath);
        if (project.Length > 4000) throw new ArgumentException("Project path exceeds budget.");
        if (record.ExperimentProjects.TryGetValue(id, out var prior))
        {
            if (!SameProject(prior, project)) throw new InvalidDataException("Experiment ownership cannot be rebound.");
            return record;
        }
        if (record.ExperimentProjects.Count >= MaxExperiments) throw new InvalidOperationException("Investigation experiment budget reached; no earlier records were deleted.");
        var projects = record.ExperimentProjects.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        projects.Add(id, project); return record with { ExperimentProjects = projects };
    });
    public static bool SameProject(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public string RequireExperiment(string session, string id)
    {
        InvestigationFiles.Id(id);
        return Current(session).ExperimentProjects.TryGetValue(id, out var path) ? path :
            throw new UnauthorizedAccessException("experiment_investigation_mismatch: Explicitly resume the owning investigation before using this experiment. Approval and clientName do not transfer ownership.");
    }
    public void Record(string session, string instance, string tool, AgentReply reply)
    {
        if (reply.Outcome is not ("returned" or "failed" or "cancelled")) throw new InvalidDataException("Only terminal gateway calls may be persisted.");
        InvestigationFiles.Id(reply.CallId);
        Update(session, record =>
        {
            if (record.Evidence.Any(e => e.CallId == reply.CallId)) return record;
            if (record.Evidence.Count >= MaxEvidence) throw new InvalidOperationException("Durable evidence budget reached. Existing receipts remain intact.");
            // The journal only puts small data inline. Never advertise an unretained large result as durable.
            var saved = reply with { ResultAvailable = reply.Data.HasValue };
            if (JsonSerializer.SerializeToUtf8Bytes(saved, InvestigationFiles.Json).Length > 100_000)
                saved = saved with { Data = null, ResultAvailable = false };
            InvestigationFiles.Write(Path.Combine(Folder(record.Id), "call-" + reply.CallId + ".json"), saved);
            var link = new ReviewEvidenceLink(instance, reply.CallId, tool, reply.Outcome,
                reply.ObservedStatus, DateTimeOffset.UtcNow.ToString("O"));
            return record with { Evidence = record.Evidence.Append(link).ToArray() };
        });
    }
    public IReadOnlyList<ReviewEvidenceLink> Evidence(string session, string[]? ids)
    {
        var wanted = ids ?? [];
        if (wanted.Length > 10) throw new ArgumentException("At most 10 evidenceCallIds are supported.");
        var saved = Current(session);
        return wanted.Distinct(StringComparer.Ordinal).Select(id =>
            saved.Evidence.SingleOrDefault(e => e.CallId == InvestigationFiles.Id(id)) ??
            throw new KeyNotFoundException("evidence_unavailable: No completed receipt in this investigation. Resume the correct investigation; do not recreate evidence by replaying execution.")).ToArray();
    }
    public object ReadResult(string session, string callId, string pointer, int offset, int limit)
    {
        if (pointer.Length > 500 || offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid result page.");
        _ = Evidence(session, [callId]);
        var reply = InvestigationFiles.Read<AgentReply>(Path.Combine(Folder(CurrentId(session)), "call-" + InvestigationFiles.Id(callId) + ".json"));
        if (reply.CallId != callId) throw new InvalidDataException("Stored call ID mismatch.");
        if (reply.Data is not JsonElement value)
            throw new KeyNotFoundException("result_not_retained: The small receipt survived, but large data did not. Read the saved experiment report; do not rerun execution just to retrieve output.");
        if (pointer.Length > 0)
        {
            if (!pointer.StartsWith('/')) throw new ArgumentException("Use a JSON pointer such as /findings.");
            foreach (var raw in pointer[1..].Split('/'))
            {
                var key = raw.Replace("~1", "/").Replace("~0", "~");
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child)) value = child;
                else if (value.ValueKind == JsonValueKind.Array && int.TryParse(key, out var i) && i >= 0 && i < value.GetArrayLength()) value = value[i];
                else throw new KeyNotFoundException("Result field was not found.");
            }
        }
        return value.ValueKind == JsonValueKind.Array
            ? new { callId, pointer, total = value.GetArrayLength(), offset, items = value.EnumerateArray().Skip(offset).Take(limit).ToArray() }
            : (object)new { callId, pointer, data = value };
    }
    public void SaveMeasurement(string session, ReviewRunObservation observation)
    {
        var path = RequireExperiment(session, observation.ExperimentId);
        if (!SameProject(path, observation.ProjectPath)) throw new InvalidDataException("Measured project differs from the experiment owner.");
        var file = Path.Combine(Folder(CurrentId(session)), "measurement-" + InvestigationFiles.Id(observation.ExperimentId) + ".json");
        using var lease = InvestigationFiles.Lock(Folder(CurrentId(session)), "record.lock");
        if (File.Exists(file)) throw new InvalidOperationException("A measurement observation already exists; it was not overwritten.");
        InvestigationFiles.Write(file, observation);
    }
    public ReviewRunObservation Measurement(string session, string experimentId)
    {
        var project = RequireExperiment(session, experimentId);
        var file = Path.Combine(Folder(CurrentId(session)), "measurement-" + InvestigationFiles.Id(experimentId) + ".json");
        if (!File.Exists(file)) throw new InvalidOperationException("measurement_binding_missing: Use tenka_measure_target for a prepared benchmark. Existing unbound runs are not retroactively bound or rerun.");
        var observation = InvestigationFiles.Read<ReviewRunObservation>(file);
        if (observation.ExperimentId != experimentId || !SameProject(project, observation.ProjectPath))
            throw new InvalidDataException("Stored measurement identity mismatch.");
        return observation;
    }
}
