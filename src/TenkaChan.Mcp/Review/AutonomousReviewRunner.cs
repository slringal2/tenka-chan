namespace TenkaChan.Mcp.Review;

/// <summary>
/// Bounded, cancellable single-run coordinator. Roslyn provides method facts,
/// the local LLM proposes hypotheses, and SQLite stores each claim as
/// suspected. No model output can execute a shell command or promote itself
/// to a verified bug. Test execution remains an explicit separate operation.
/// </summary>
public sealed class AutonomousReviewRunner : IDisposable
{
    private readonly IReviewContextSource _source;
    private readonly IReviewReasoner _reasoner;
    private readonly ReviewLedger _ledger;
    private readonly ReviewRunStore _store;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private AutonomousReviewStatus? _current;
    private CancellationTokenSource? _cancellation;
    private bool _cancelRequested;

    public AutonomousReviewRunner(
        IReviewContextSource source, IReviewReasoner reasoner,
        ReviewLedger ledger, ReviewRunStore store)
    {
        _source = source;
        _reasoner = reasoner;
        _ledger = ledger;
        _store = store;
    }

    public bool IsActive
    {
        get { lock (_sync) return _current is not null &&
            _current.Status is "queued" or "running" or "cancelling"; }
    }

    public AutonomousReviewStatus? Current
    {
        get { lock (_sync) return _current; }
    }

    public IReadOnlyList<AutonomousReviewStatus> History(int limit = 20) =>
        _store.List(limit);

    public AutonomousReviewStatus Get(string id)
    {
        lock (_sync)
        {
            if (_current?.RunId == id) return _current;
        }
        return _store.Get(id);
    }

    public async Task<AutonomousReviewStatus> StartAsync(
        AutonomousReviewOptions options, CancellationToken cancellationToken = default)
    {
        if (options.MaxMethods is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(options.MaxMethods),
                "Maximum 30 methods per run.");
        if (options.MaxMinutes is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(options.MaxMinutes),
                "Maximum 30 minutes per run.");

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (IsActive) throw new InvalidOperationException("Review already running.");
            var batch = await _source.GetContextAsync(options.MaxMethods, cancellationToken);
            if (string.IsNullOrWhiteSpace(batch.SnapshotId))
                throw new InvalidOperationException("Roslyn snapshot ID is missing.");
            if (batch.Targets.Count == 0)
                throw new InvalidOperationException("No methods to review.");

            var id = Guid.NewGuid().ToString("N");
            var run = new AutonomousReviewStatus(
                id, batch.SnapshotId, _reasoner.ModelName, "queued",
                Math.Min(batch.Targets.Count, options.MaxMethods),
                0, 0, 0, null, DateTimeOffset.UtcNow.ToString("O"), null,
                [NewActivity("started", "Review queued with explicit resource budget.")]);
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(options.MaxMinutes));
            lock (_sync)
            {
                _cancelRequested = false;
                _cancellation?.Dispose();
                _cancellation = cts;
                _current = run;
                _store.Save(run);
            }

            // The app process owns the worker. This is not a remotely scheduled
            // automation; the run stops if the application process exits.
            _ = Task.Run(() => ExecuteAsync(batch, run.RunId, cts.Token));
            return run;
        }
        finally { _startGate.Release(); }
    }

    public AutonomousReviewStatus Stop(string id)
    {
        lock (_sync)
        {
            if (_current?.RunId != id || !IsActive)
                throw new InvalidOperationException("No matching active run to stop.");
            _cancelRequested = true;
            _cancellation!.Cancel();
            UpdateLocked(s => s with
            {
                Status = "cancelling",
                Activity = Append(s.Activity, NewActivity("cancelling", "Stop requested."))
            });
            return _current!;
        }
    }

    private async Task ExecuteAsync(ReviewContext batch, string id, CancellationToken token)
    {
        var proposalsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Update(s => s with { Status = "running",
                Activity = Append(s.Activity, NewActivity("running", "Review started.")) });

            foreach (var target in batch.Targets.Take(30))
            {
                token.ThrowIfCancellationRequested();
                var method = target.Method;
                Update(s => s with
                {
                    CurrentMethod = method.Signature,
                    Activity = Append(s.Activity,
                        NewActivity("method", "Analyzing " + Clip(method.Signature, 170), method.Id))
                });
                try
                {
                    var context = await _source.GetMethodAsync(method.Id, token);
                    var output = await _reasoner.AnalyzeAsync(context, token);
                    var added = 0;

                    foreach (var finding in output.Hypotheses.Take(3))
                    {
                        token.ThrowIfCancellationRequested();
                        var signature = method.Id + "|" + finding.Category + "|" +
                            finding.Claim + "|" + finding.FailureCondition;
                        if (!proposalsSeen.Add(signature)) continue;
                        var hypothesis = _ledger.Create(batch.SnapshotId, method.Id,
                            finding.Category, finding.Claim, finding.FailureCondition);
                        added++;
                        var message = "UNVERIFIED: " + Clip(finding.Claim, 300) +
                            (finding.TestIdeas.Count > 0
                                ? " | Suggested test: " + Clip(finding.TestIdeas[0], 160) : "");
                        Update(s => s with
                        {
                            HypothesisCount = s.HypothesisCount + 1,
                            Activity = Append(s.Activity,
                                NewActivity("hypothesis", message, method.Id, hypothesis.Id))
                        });
                    }

                    Update(s => s with
                    {
                        CompletedMethods = s.CompletedMethods + 1,
                        CurrentMethod = null,
                        Activity = Append(s.Activity,
                            NewActivity("completed_method",
                                $"Reviewed {Clip(method.Signature, 120)}; {added} new unverified hypotheses.",
                                method.Id))
                    });
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // One bad model output must not discard analysis of all
                    // remaining methods or label failures as code defects.
                    Update(s => s with
                    {
                        CompletedMethods = s.CompletedMethods + 1,
                        ErrorCount = s.ErrorCount + 1,
                        CurrentMethod = null,
                        Activity = Append(s.Activity, NewActivity("review_error",
                            Clip(ex.GetType().Name + ": " + ex.Message, 280), method.Id))
                    });
                }
            }
            Update(s => s with
            {
                Status = s.ErrorCount == 0 ? "completed" : "completed_with_errors",
                CurrentMethod = null,
                FinishedUtc = DateTimeOffset.UtcNow.ToString("O"),
                Activity = Append(s.Activity, NewActivity("finished",
                    "Review finished. Hypotheses are not validated defects."))
            });
        }
        catch (OperationCanceledException)
        {
            bool user;
            lock (_sync) user = _cancelRequested;
            Update(s => s with
            {
                Status = user ? "cancelled" : "timed_out",
                FinishedUtc = DateTimeOffset.UtcNow.ToString("O"),
                CurrentMethod = null,
                Activity = Append(s.Activity, NewActivity("finished",
                    user ? "Stopped by user." : "Review time budget expired."))
            });
        }
        catch (Exception ex)
        {
            Update(s => s with
            {
                Status = "failed", ErrorCount = s.ErrorCount + 1,
                CurrentMethod = null, FinishedUtc = DateTimeOffset.UtcNow.ToString("O"),
                Activity = Append(s.Activity,
                    NewActivity("error", Clip(ex.GetType().Name + ": " + ex.Message, 280)))
            });
        }
    }

    private void Update(Func<AutonomousReviewStatus, AutonomousReviewStatus> mutation)
    {
        lock (_sync) UpdateLocked(mutation);
    }

    private void UpdateLocked(Func<AutonomousReviewStatus, AutonomousReviewStatus> mutation)
    {
        if (_current is null) return;
        _current = mutation(_current);
        _store.Save(_current);
    }

    private static ReviewActivity NewActivity(string kind, string message,
        string? methodId = null, string? hypothesisId = null) =>
        new(DateTimeOffset.UtcNow.ToString("O"), kind, message, methodId, hypothesisId);

    private static IReadOnlyList<ReviewActivity> Append(
        IReadOnlyList<ReviewActivity> activities, ReviewActivity item) =>
        activities.Append(item).TakeLast(80).ToArray();

    private static string Clip(string text, int limit) =>
        text.Length > limit ? text[..limit] + "..." : text;

    public void Dispose()
    {
        lock (_sync)
        {
            _cancellation?.Cancel();
            // Do not dispose a CTS while its worker may still inspect Token.
        }
        _startGate.Dispose();
    }
}
