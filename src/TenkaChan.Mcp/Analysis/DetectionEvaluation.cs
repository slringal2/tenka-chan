namespace TenkaChan.Mcp.Analysis;

public sealed record DetectionLabel(string Id, bool Relevant);
public sealed record DetectionEvaluationReport(int Cases, int RelevantCases, int UniquePredictions,
    int TruePositives, int FalsePositives, int FalseNegatives, double? Precision, double? Recall,
    int RequestedTopK, int ReturnedTopK, double? PrecisionAtK,
    IReadOnlyList<string> MissedIds, IReadOnlyList<string> FalsePositiveIds,
    string Limitation);

/// <summary>Metrics require a completely labeled corpus; absence of a label is never a true negative.</summary>
public static class DetectionEvaluation
{
    public static DetectionEvaluationReport Evaluate(IReadOnlyList<DetectionLabel> labels,
        IReadOnlyList<string> rankedPredictions, int topK = 10)
    {
        if (topK is < 1 or > 1000 || labels.Count > 100000 || rankedPredictions.Count > 100000)
            throw new ArgumentException("Evaluation limits exceeded.");
        if (labels.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id)) ||
            labels.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != labels.Count)
            throw new ArgumentException("Every corpus case needs a unique nonempty label ID.");
        var truth = labels.ToDictionary(x => x.Id, x => x.Relevant, StringComparer.Ordinal);
        if (rankedPredictions.Any(id => id is null || !truth.ContainsKey(id)))
            throw new ArgumentException("Prediction without a ground-truth label; refusing incomplete evaluation.");
        var predicted = rankedPredictions.Distinct(StringComparer.Ordinal).ToArray();
        var selected = predicted.ToHashSet(StringComparer.Ordinal);
        var tp = predicted.Count(id => truth[id]);
        var falsePositive = predicted.Where(id => !truth[id]).ToArray();
        var missed = labels.Where(x => x.Relevant && !selected.Contains(x.Id)).Select(x => x.Id).ToArray();
        var top = predicted.Take(topK).ToArray();
        var positive = labels.Count(x => x.Relevant);
        return new(labels.Count, positive, predicted.Length, tp, falsePositive.Length, missed.Length,
            predicted.Length == 0 ? null : (double)tp / predicted.Length,
            positive == 0 ? null : (double)tp / positive, topK, top.Length,
            top.Length == 0 ? null : (double)top.Count(id => truth[id]) / top.Length,
            missed, falsePositive,
            "These metrics describe this labeled corpus only, not real-project defect rates, population precision or confidence. Top-K uses the actual number of returned unique items; zero denominators are null.");
    }
}
