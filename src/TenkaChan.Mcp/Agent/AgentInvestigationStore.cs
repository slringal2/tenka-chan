using System.Text.Json;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Mcp.Agent;

public sealed record SavedInvestigation(string Id, string CreatedUtc, string UpdatedUtc,
    IReadOnlyList<ReviewEvidenceLink> Evidence, IReadOnlyList<string> ExperimentIds,
    IReadOnlyList<string> ProposalIds);

/// <summary>
/// Durable investigation ownership is separate from ephemeral transport sessions.
/// Importing another investigation requires explicit approval under the existing
/// local machine credential. A matching self-reported client name grants nothing.
/// </summary>
public sealed class AgentInvestigationStore
{
    private readonly string _root;
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    public AgentInvestigationStore() : this(InvestigationFiles.DataRoot) { }
    internal AgentInvestigationStore(string dataRoot) => _root = InvestigationFiles.DirectoryAt(Path.Combine(dataRoot, "investigations"));
    private string Folder(string id) => Path.Combine(_root, InvestigationFiles.Id(id));
    private SavedInvestigation Read(string id)
    {
        var record = InvestigationFiles.Read<SavedInvestigation>(Path.Combine(Folder(id), "record.json"));
        if (record.Id != id || record.Evidence is null || record.ExperimentIds is null || record.ProposalIds is null
            || record.Evidence.Count > 512 || record.ExperimentIds.Count > 256 || record.ProposalIds.Count > 128)
            throw new InvalidDataException("Investigation record is inconsistent or exceeds its budget.");
        return record;
    }
    public string CurrentId(string session)
    {
        InvestigationFiles.Id(session);
        lock (_sync)
        {
            if (_bindings.TryGetValue(session, out var bound)) return bound;
            var directory = InvestigationFiles.DirectoryAt(Folder(session));
            using var gate = InvestigationFiles.Lock(directory, "record.lock");
            if (!File.Exists(Path.Combine(directory, "record.json")))
            {
                var now = DateTimeOffset.UtcNow.ToString("O");
                InvestigationFiles.Write(Path.Combine(directory, "record.json"), new SavedInvestigation(session, now, now, [], [], []));
            }
            _bindings.Add(session, session); return session;
        }
    }
    public IDisposable Enter(string session) => InvestigationFiles.Lock(Folder(CurrentId(session)), "operation.lock");
    public SavedInvestigation Current(string session) => Read(CurrentId(session));
    public object List()
    {
        // Deliberately bounded discovery. A partial directory listing is not a complete history.
        var paths = Directory.EnumerateDirectories(_root).Take(201).ToArray();
        var records = new List<object>(); var unavailable = 0;
        foreach (var path in paths.Take(200))
        {
            try
            {
                var saved = Read(Path.GetFileName(path));
                records.Add(new { investigationId = saved.Id, saved.CreatedUtc, saved.UpdatedUtc,
                    evidenceCalls = saved.Evidence.Count, experiments = saved.ExperimentIds.Count, proposals = saved.ProposalIds.Count });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidDataException) { unavailable++; }
        }
        return new { items = records, limited = paths.Length > 200, unavailable,
            note = "Local-machine credential scope, not a multi-user account boundary. Resume is explicit and never based on clientName." };
    }
    public SavedInvestigation Resume(string session, string id, bool approved)
    {
        InvestigationFiles.Id(session); InvestigationFiles.Id(id);
        if (!approved) throw new UnauthorizedAccessException("approval_required: Resume grants this client access to the selected investigation's stored results and experiments. Obtain explicit approval, then pass approved:true.");
        if (!Directory.Exists(Folder(id))) throw new KeyNotFoundException("investigation_not_found: Use tenka_investigation action=list and select an existing ID.");
        using var busy = InvestigationFiles.Lock(Folder(id), "operation.lock");
        var saved = Read(id);
        lock (_sync) _bindings[session] = id;
        return saved;
    }
    private void Update(string session, Func<SavedInvestigation, SavedInvestigation> change)
    {
        var id = CurrentId(session); var folder = Folder(id);
        using var gate = InvestigationFiles.Lock(folder, "record.lock");
        var updated = change(Read(id)) with { UpdatedUtc = DateTimeOffset.UtcNow.ToString("O") };
        InvestigationFiles.Write(Path.Combine(folder, "record.json"), updated);
    }
    public void AddExperiment(string session, string id) => Update(session, record =>
    {
        InvestigationFiles.Id(id);
        if (record.ExperimentIds.Contains(id)) return record;
        if (record.ExperimentIds.Count >= 256) throw new InvalidOperationException("Investigation experiment budget reached; start a new investigation.");
        return record with { ExperimentIds = record.ExperimentIds.Append(id).ToArray() };
    });
    public void AddProposal(string session, string id) => Update(session, record =>
    {
        InvestigationFiles.Id(id);
        if (record.ProposalIds.Contains(id)) return record;
        if (record.ProposalIds.Count >= 128) throw new InvalidOperationException("Investigation proposal budget reached; start a new investigation.");
        return record with { ProposalIds = record.ProposalIds.Append(id).ToArray() };
    });
    public void RequireExperiment(string session, string id)
    {
        if (!Current(session).ExperimentIds.Contains(id, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Experiment is not owned by this investigation. Explicitly resume its investigation before using it; names do not grant access.");
    }
    public void RequireProposal(string session, string id)
    {
        if (!Current(session).ProposalIds.Contains(id, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Proposal is not owned by this investigation. Explicitly resume the correct investigation first.");
    }
    public void Record(string session, string instance, string tool, AgentReply reply)
    {
        // Only completed actual gateway calls enter the receipt store. It does not
        // upgrade failures, agent notes, or completed tests to correctness proofs.
        if (reply.Outcome is not ("returned" or "failed" or "cancelled")) throw new InvalidDataException("Only terminal calls can be persisted.");
        InvestigationFiles.Id(reply.CallId);
        Update(session, record =>
        {
            if (record.Evidence.Any(e => e.CallId == reply.CallId)) return record;
            if (record.Evidence.Count >= 512) throw new InvalidOperationException("Durable call budget reached; earlier receipts were not evicted.");
            InvestigationFiles.Write(Path.Combine(Folder(record.Id), reply.CallId + ".json"), reply);
            var link = new ReviewEvidenceLink(instance, reply.CallId, tool, reply.Outcome, reply.ObservedStatus, DateTimeOffset.UtcNow.ToString("O"));
            return record with { Evidence = record.Evidence.Append(link).ToArray() };
        });
    }
    public IReadOnlyList<ReviewEvidenceLink> Evidence(string session, string[]? ids)
    {
        var requested = ids ?? [];
        if (requested.Length > 10) throw new ArgumentException("At most 10 evidenceCallIds are supported.");
        var saved = Current(session);
        return requested.Distinct(StringComparer.Ordinal).Select(id =>
            saved.Evidence.SingleOrDefault(e => e.CallId == id)
                ?? throw new ArgumentException("evidence_unavailable: Use completed calls from this investigation, or explicitly resume their investigation. No evidence was attached automatically.")).ToArray();
    }
    public object ReadResult(string session, string callId, string pointer, int offset, int limit)
    {
        if (pointer.Length > 500 || offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid result page.");
        _ = Evidence(session, [callId]);
        var reply = InvestigationFiles.Read<AgentReply>(Path.Combine(Folder(CurrentId(session)), callId + ".json"));
        if (reply.CallId != callId) throw new InvalidDataException("Stored call ID mismatch.");
        if (reply.Data is not JsonElement value)
            throw new KeyNotFoundException("result_not_retained: Only the small reply payload and evidence metadata survived. Read the persisted experiment/workflow report; large live-only results are not recreated or rerun.");
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
}
