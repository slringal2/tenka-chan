using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TenkaChan.Mcp.Agent;

/// <summary>Bounded, in-memory activity journal. Agent statements never become verified observations.</summary>
public sealed class AgentJournal
{
    public const int MaxSessions = 16, MaxCallsPerSession = 200, MaxEvents = 500;
    public const int MaxResultBytes = 8_000_000, TotalResultBytes = 32_000_000;
    private readonly object _sync = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Call> _calls = new(StringComparer.Ordinal);
    private readonly Queue<AgentEvent> _events = new();
    private long _sequence;
    private int _resultBytes;
    private readonly TimeProvider _time;
    private readonly AgentSessionOptions _options;
    public int SessionLimit => _options.MaximumSessions;
    public int IdleTimeoutMinutes => _options.IdleTimeoutMinutes;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public AgentJournal(TimeProvider? time = null, AgentSessionOptions? options = null)
    {
        _time = time ?? TimeProvider.System;
        _options = (options ?? new AgentSessionOptions()).Validate();
    }
    private sealed class Session
    {
        public required string Id, Client, Goal;
        public DateTimeOffset Seen;
        public int Count;
        public string Status = "idle";
        public string? Active;
    }
    private sealed class Call
    {
        public required string Id, SessionId, RequestId, Tool, Digest;
        public DateTimeOffset Started;
        public AgentReply? Reply;
        public JsonElement? Result;
        public int Bytes;
    }
    public AgentSessionView Begin(string client, string goal)
    {
        client = Text(client, 100); goal = Text(goal, 1500);
        lock (_sync)
        {
            Expire();
            if (_sessions.Count >= SessionLimit)
            {
                var oldest = _sessions.Values.Where(s => s.Active is null && (s.Status is "finished" or "expired"))
                    .OrderBy(s => s.Seen).FirstOrDefault() ?? throw new InvalidOperationException($"Agent session limit reached ({SessionLimit}). Use tenka_sessions_list without connecting, then tenka_session_end with an idle sessionId and approved:true, or wait for the {IdleTimeoutMinutes}-minute idle TTL. Running actions are never evicted.");
                foreach (var call in _calls.Values.Where(c => c.SessionId == oldest.Id).ToArray())
                { _resultBytes -= call.Bytes; _calls.Remove(call.Id); }
                _sessions.Remove(oldest.Id);
            }
            var session = new Session { Id = Guid.NewGuid().ToString("N"), Client = client,
                Goal = goal, Seen = _time.GetUtcNow() };
            _sessions.Add(session.Id, session);
            Event(session.Id, null, "session_started", "", "外部エージェントから接続要求を受けました。クライアント名は自己申告です。", null, null);
            return View(session);
        }
    }
    // Returns a prior result for an identical completed request. Never executes it twice.
    public (string CallId, AgentReply? Cached) Start(AgentCallRequest request)
    {
        if (!Guid.TryParseExact(request.RequestId, "N", out _)) throw new ArgumentException("requestId must be a GUID in N format.");
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Tool + "\n" + request.Arguments.GetRawText())));
        lock (_sync)
        {
            var s = RequireSession(request.SessionId);
            var existing = _calls.Values.FirstOrDefault(c => c.SessionId == s.Id && c.RequestId == request.RequestId);
            if (existing is not null)
            {
                if (existing.Digest != digest) throw new InvalidOperationException("requestId was already used with different arguments.");
                if (existing.Reply is null) throw new InvalidOperationException("This request is still running. No duplicate was queued.");
                return (existing.Id, existing.Reply);
            }
            if (s.Active is not null) throw new InvalidOperationException("This agent already has a running action. No action was queued.");
            if (s.Count >= MaxCallsPerSession) throw new InvalidOperationException("Agent call budget exhausted. Finish this session before starting another.");
            var call = new Call { Id = Guid.NewGuid().ToString("N"), SessionId = s.Id,
                RequestId = request.RequestId, Tool = Text(request.Tool, 80), Digest = digest, Started = _time.GetUtcNow() };
            _calls.Add(call.Id, call); s.Active = call.Tool; s.Count++; s.Seen = call.Started;
            Event(s.Id, call.Id, "tool_started", call.Tool, "解析ツールの処理を開始しました。", Snapshot(request.Arguments), null);
            return (call.Id, null);
        }
    }
    public AgentReply Complete(string callId, object? data, string? error = null, bool cancelled = false, string? resolvedSnapshot = null)
    {
        var json = JsonSerializer.SerializeToElement(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var size = Encoding.UTF8.GetByteCount(json.GetRawText());
        lock (_sync)
        {
            var c = _calls[callId];
            if (c.Reply is not null) return c.Reply;
            var s = _sessions[c.SessionId]; s.Active = null; s.Seen = _time.GetUtcNow();
            string? observed = Status(json), snapshot = Snapshot(json) ?? resolvedSnapshot;
            var fields = json.ValueKind == JsonValueKind.Object ? json.EnumerateObject().Select(p => p.Name).ToArray() : [];
            if (size <= MaxResultBytes)
            {
                while (_resultBytes + size > TotalResultBytes)
                {
                    var evict = _calls.Values.Where(x => x.Result.HasValue).OrderBy(x => x.Started).FirstOrDefault();
                    if (evict is null) break;
                    _resultBytes -= evict.Bytes; evict.Bytes = 0; evict.Result = null;
                    if (evict.Reply is not null) evict.Reply = evict.Reply with { Data = null, ResultAvailable = false };
                }
                c.Result = json; c.Bytes = size; _resultBytes += size;
            }
            c.Reply = new(c.Id, cancelled ? "cancelled" : error is null ? "returned" : "failed",
                observed, snapshot, error is null ? null : Text(error.Length <= 1200 ? error : error[..1200], 1200),
                size <= 60000 ? json : null, c.Result.HasValue, fields);
            Event(s.Id, c.Id, c.Reply.Outcome == "returned" ? "tool_result" : c.Reply.Outcome,
                c.Tool, error is not null ? Text(error.Length <= 1200 ? error : error[..1200], 1200) : size > MaxResultBytes
                    ? "結果が保存上限を超えました。表示範囲を絞って再調査してください。"
                    : DescribeResult(json), snapshot, observed);
            return c.Reply;
        }
    }
    public object ReadResult(string sessionId, string callId, string pointer, int offset, int limit)
    {
        if (pointer.Length > 500 || offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Invalid result page.");
        lock (_sync)
        {
            RequireSession(sessionId);
            if (!_calls.TryGetValue(callId, out var c) || c.SessionId != sessionId)
                throw new KeyNotFoundException("Result does not belong to this agent session.");
            if (c.Result is not JsonElement value) throw new KeyNotFoundException("Result is running, expired, or exceeded the storage limit.");
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
            if (value.ValueKind == JsonValueKind.Array)
                return new { callId, pointer, total = value.GetArrayLength(), offset,
                    items = value.EnumerateArray().Skip(offset).Take(limit).ToArray() };
            if (value.GetRawText().Length > 100000)
                return new { callId, pointer, needsFieldSelection = true,
                    fields = value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Select(p => p.Name).ToArray() : [],
                    note = "Choose a more specific JSON pointer. Large strings are not returned by this endpoint." };
            return new { callId, pointer, data = value };
        }
    }
    public (string Agent, IReadOnlyList<TenkaChan.Mcp.Tracking.ReviewEvidenceLink> Evidence) ReviewAttribution(string sessionId, string[]? callIds)
    {
        lock (_sync)
        {
            var session = RequireSession(sessionId);
            var ids = callIds ?? [];
            if (ids.Length > 10) throw new ArgumentException("At most 10 evidence call IDs are supported.");
            var evidence = new List<TenkaChan.Mcp.Tracking.ReviewEvidenceLink>();
            foreach (var id in ids.Distinct(StringComparer.Ordinal))
            {
                if (!_calls.TryGetValue(id, out var call) || call.SessionId != sessionId || call.Reply is null)
                    throw new ArgumentException("Evidence must be completed tool calls from this session.");
                evidence.Add(new(InstanceId, call.Id, call.Tool, call.Reply.Outcome,
                    call.Reply.ObservedStatus, call.Started.ToString("O")));
            }
            return (session.Client, evidence);
        }
    }
    public void Report(string sessionId, AgentNote note)
    {
        if (note.Kind is not ("plan" or "hypothesis" or "conclusion" or "progress")) throw new ArgumentException("Unknown note kind.");
        var text = Text(note.Message, 4000);
        lock (_sync)
        {
            RequireSession(sessionId);
            var ids = note.EvidenceCallIds ?? [];
            if (ids.Length > 10 || ids.Any(id => !_calls.TryGetValue(id, out var c) || c.SessionId != sessionId || c.Reply is null))
                throw new ArgumentException("Evidence IDs must be completed calls from this session.");
            Event(sessionId, null, "agent_" + note.Kind, "agent_note", text +
                (ids.Length == 0 ? "" : "\n参照した呼出し: " + string.Join(", ", ids)), null, "agent_reported_unverified");
        }
    }
    public void Finish(string sessionId)
    {
        lock (_sync)
        {
            var s = RequireSession(sessionId);
            if (s.Active is not null) throw new InvalidOperationException("Wait for or cancel the active tool first.");
            s.Status = "finished";
            Event(sessionId, null, "session_finished", "", "外部エージェントが調査終了を通知しました。", null, null);
        }
    }
    // Machine-authenticated management paths do not need or consume an investigation session.
    // Listing is observational: it must not refresh LastSeen or prevent idle expiry.
    public AgentSessionList ListSessions()
    {
        lock (_sync)
        {
            Expire();
            var items = _sessions.Values.Select(View).OrderByDescending(s => s.LastSeen).ToArray();
            var active = items.Count(s => s.Status is "idle" or "running");
            return new(InstanceId, SessionLimit, IdleTimeoutMinutes, active, SessionLimit - active, items,
                "Client names are self-reported, not authenticated identities. Management requires this server's local credential. Listing does not create a session or renew its TTL. Finished/expired slots are reclaimed by the next connection.");
        }
    }
    public AgentSessionView EndIdleSession(string sessionId, bool approved)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) throw new ArgumentException("sessionId must be a GUID in N format.");
        if (!approved) throw new InvalidOperationException("approval_required: Ending a selected session requires explicit client approval and approved:true. No session was ended.");
        lock (_sync)
        {
            Expire();
            if (!_sessions.TryGetValue(sessionId, out var s)) throw new KeyNotFoundException("Session not found in this server instance. Use tenka_sessions_list.");
            if (s.Active is not null) throw new InvalidOperationException("session_busy: The selected session has a running action. It was not ended or cancelled; wait for completion or cancel through its original client.");
            if (s.Status is "finished" or "expired") return View(s);
            s.Status = "finished";
            Event(sessionId, null, "session_finished", "", "ローカル資格情報を持つクライアントが承認付きでこのセッションを終了しました。", null, null);
            return View(s);
        }
    }
    public object MonitorResult(string callId)
    {
        lock (_sync)
        {
            if (!_calls.TryGetValue(callId, out var call) || call.Reply is null)
                throw new KeyNotFoundException("Result is not available in this server instance.");
            // Reading the browser monitor must NOT touch session activity/last-seen timestamps.
            return new { callId, tool = call.Tool, reply = call.Reply,
                note = call.Reply.Data is null ? "大きい結果は外部エージェントのtenka_read_resultで取得できます。元の解析結果は対応する調査画面でも確認できます。" : "ツールが返したデータです。LLMが書いた説明と区別してください。" };
        }
    }
    private static string DescribeResult(JsonElement value)
    {
        var name = "";
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("signature", out var signature)) name = signature.GetString() ?? "";
            else if (value.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
            else if (value.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.Object && sum.TryGetProperty("totalFindings", out var count)) name = "静的な指摘 " + count + " 件";
        }
        if (name.Length > 200) name = name[..200];
        return (name.Length == 0 ? "" : name + "：") + "結果が返りました。内容は『結果を見る』で確認できます。";
    }
    public AgentMonitor Monitor(bool enabled, bool build, bool execution)
    {
        lock (_sync)
        {
            Expire();
            return new(InstanceId, enabled, build, execution, _sequence,
                _sessions.Values.Select(View).OrderByDescending(s => s.LastSeen).ToArray(), _events.ToArray(),
                $"この起動中の活動です。実行中でないセッションは最終通信から{IdleTimeoutMinutes}分で期限切れになります。LLMのメモは自己申告・未検証。ブラウザーは表示のみでモデルに接続しません。");
        }
    }
    private Session RequireSession(string id)
    {
        Expire();
        if (!_sessions.TryGetValue(id, out var s) || s.Status != "idle") throw new InvalidOperationException("Session ended or expired. Connect again explicitly.");
        s.Seen = _time.GetUtcNow(); return s;
    }
    private void Expire()
    {
        foreach (var s in _sessions.Values)
            if (s.Status == "idle" && s.Active is null && _time.GetUtcNow() - s.Seen >= TimeSpan.FromMinutes(IdleTimeoutMinutes)) s.Status = "expired";
    }
    private AgentSessionView View(Session s) => new(s.Id, s.Client, s.Goal, s.Active is null ? s.Status : "running", s.Seen, s.Count, s.Active);
    private void Event(string session, string? call, string kind, string tool, string message, string? snapshot, string? observed)
    {
        _events.Enqueue(new(++_sequence, session, call, kind, tool, message, snapshot, observed, _time.GetUtcNow()));
        while (_events.Count > MaxEvents) _events.Dequeue();
    }
    public static string Text(string value, int max)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > max) throw new ArgumentException($"Text exceeds {max} characters.");
        return new string(value.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray());
    }
    public static string? Snapshot(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("snapshotId", out var s) && s.ValueKind == JsonValueKind.String) return s.GetString();
        return e.TryGetProperty("summary", out var summary) ? Snapshot(summary) : null;
    }
    private static string? Status(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String) return s.GetString();
        foreach (var k in new[] { "build", "data", "analysis" }) if (e.TryGetProperty(k, out var child) && Status(child) is { } found) return found;
        return null;
    }
}