using Microsoft.Data.Sqlite;
using TenkaChan.Mcp.Review;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewLedgerTests
{
    [Fact]
    public void PersistsHypothesesAndEvidenceAcrossInstances()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tenka-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "reviews.sqlite");
            var ledger = new ReviewLedger(path);
            var hypothesis = ledger.Create("snapshot", "method-id", "correctness",
                "Negative input produces an invalid output", "input < 0");
            Assert.Equal("suspected", hypothesis.Status);
            Assert.Throws<InvalidOperationException>(() =>
                ledger.SetStatus(hypothesis.Id, "reported_reproduced", "Fails on negative input"));

            ledger.AddEvidence(hypothesis.Id, "contract", "Must return positive values",
                "spec:contract:2");
            Assert.Throws<InvalidOperationException>(() =>
                ledger.SetStatus(hypothesis.Id, "reported_reproduced", "Test not executed"));

            ledger.AddEvidence(hypothesis.Id, "test_execution", "Test failed on revision A",
                "run:fixture-001", "EXP-001");
            ledger.SetStatus(hypothesis.Id, "reported_reproduced", "Test failed; independently check source");
            var reopened = new ReviewLedger(path);
            var saved = reopened.Get(hypothesis.Id);
            Assert.Equal("reported_reproduced", saved.Hypothesis.Status);
            Assert.Equal(2, saved.Evidence.Count);
            Assert.Single(reopened.List("reported_reproduced"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RejectsLlmAssumedOracleAndUnknownKinds()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tenka-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var ledger = new ReviewLedger(Path.Combine(folder, "reviews.sqlite"));
            var entry = ledger.Create("snapshot", "symbol", "performance", "Slower", "time > baseline");
            ledger.AddEvidence(entry.Id, "contract", "Imagined speed limit", "llm:assumed");
            ledger.AddEvidence(entry.Id, "test_execution", "Slow sample", "bench:sample");
            Assert.Throws<InvalidOperationException>(() =>
                ledger.SetStatus(entry.Id, "reported_reproduced", "LLM-only oracle"));
            Assert.Throws<ArgumentException>(() =>
                ledger.AddEvidence(entry.Id, "made_up_evidence", "random", "source"));
            Assert.Equal("suspected", ledger.Get(entry.Id).Hypothesis.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void RejectsInvalidInputAndMissingHypothesis()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tenka-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var ledger = new ReviewLedger(Path.Combine(folder, "reviews.sqlite"));
            Assert.Throws<ArgumentException>(() => ledger.Create("s", "m", "invalid-category", "x", "y"));
            Assert.Throws<ArgumentException>(() => ledger.Create("s", "m", "correctness", "", "y"));
            Assert.Throws<KeyNotFoundException>(() =>
                ledger.SetStatus(Guid.NewGuid().ToString("N"), "refuted", "not found"));
            Assert.Throws<SqliteException>(() =>
                ledger.AddEvidence(Guid.NewGuid().ToString("N"), "contract", "not found", "spec"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }
}
