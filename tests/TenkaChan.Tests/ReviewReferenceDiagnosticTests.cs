using Microsoft.CodeAnalysis;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewReferenceDiagnosticTests
{
    private static ReviewReferenceDiagnostic Row(string label, bool complete = true, string project = "p") =>
        ReviewReferenceDiagnostic.Create(project, project + ".csproj", "metadata", label, label,
            "identity:" + label, complete, complete ? null : "file_not_found");

    [Theory]
    [InlineData("file_not_found", "FileNotFoundException")]
    [InlineData("directory_not_found", "DirectoryNotFoundException")]
    [InlineData("bad_image", "BadImageFormatException")]
    [InlineData("access_denied", "UnauthorizedAccessException")]
    [InlineData("not_supported", "NotSupportedException")]
    [InlineData("io_error", "IOException")]
    [InlineData("metadata_unavailable", "InvalidOperationException")]
    public void MetadataFailuresAreNamedWithoutChangingTheExistingFingerprint(string code, string type)
    {
        Exception error = code switch
        {
            "file_not_found" => new FileNotFoundException("not-returned-sentinel"),
            "directory_not_found" => new DirectoryNotFoundException("not-returned-sentinel"),
            "bad_image" => new BadImageFormatException("not-returned-sentinel"),
            "access_denied" => new UnauthorizedAccessException("not-returned-sentinel"),
            "not_supported" => new NotSupportedException("not-returned-sentinel"),
            "io_error" => new IOException("not-returned-sentinel"),
            _ => new InvalidOperationException("not-returned-sentinel")
        };
        var result = ReviewMetadataFingerprint.Read(new BrokenReference(error));
        Assert.False(result.Complete);
        Assert.Equal(code, result.FailureCode);
        Assert.EndsWith("|unresolved:" + type, result.Identity); // v2 identity format is preserved.
        Assert.DoesNotContain("not-returned-sentinel", result.Identity);
    }
    [Fact]
    public void CancelledMetadataReadIsNotReportedAsAnOrdinaryBrokenReference() =>
        Assert.Throws<OperationCanceledException>(() => ReviewMetadataFingerprint.Read(new BrokenReference(new OperationCanceledException())));

    [Fact]
    public void FailureAfterManySuccessfulReferencesIsReturnedFirst()
    {
        var rows = Enumerable.Range(0, 150).Select(i => Row("ref" + i)).Append(Row("missing", false));
        var report = ReviewReferenceDiagnostics.Capture(rows);
        Assert.True(report.Available);
        Assert.True(report.Limited);
        Assert.Equal(151, report.Total);
        Assert.Equal(1, report.UnresolvedIdentities);
        Assert.Equal(100, report.Items.Count);
        Assert.Equal("missing", report.Items[0].Display);
    }
    [Fact]
    public void CombiningDependenciesRetainsCountsIncludingOmittedFailures()
    {
        var a = ReviewReferenceDiagnostics.Capture(Enumerable.Range(0, 101).Select(i => Row("a" + i, false)));
        var b = ReviewReferenceDiagnostics.Capture([Row("b", false, "dependency")]);
        var report = ReviewReferenceDiagnostics.Combine([a, b]);
        Assert.Equal(102, report.Total);
        Assert.Equal(102, report.UnresolvedIdentities);
        Assert.Equal(100, report.Items.Count);
        Assert.True(report.Limited);
    }
    [Fact]
    public void LongLabelsAreBoundedButTheFullIdentityDigestIsPreserved()
    {
        var identity = new string('a', 3000);
        var item = ReviewReferenceDiagnostic.Create("p", "p.csproj", "metadata", identity, identity, identity, true, null);
        Assert.True(item.LabelsTruncated);
        Assert.Equal(2048, item.Identity.Length);
        Assert.Equal(2048, item.Display!.Length);
        Assert.Equal(ReviewProgress.Hash(identity), item.IdentityHash);
    }
    [Fact]
    public void DiagnosticPathDoesNotBecomeAnIdentityHashInput()
    {
        var a = ReviewReferenceDiagnostic.Create("p", "p.csproj", "metadata", "a.dll", "a.dll", "same-loaded-module", true, null);
        var b = ReviewReferenceDiagnostic.Create("p", "p.csproj", "metadata", "b.dll", "b.dll", "same-loaded-module", true, null);
        Assert.Equal(a.IdentityHash, b.IdentityHash);
        Assert.NotEqual(a.FilePath, b.FilePath);
    }
    [Fact]
    public void NoCapturedReportIsDistinctFromAnObservedEmptyReferenceSet()
    {
        Assert.False(ReviewReferenceDiagnostics.Combine([]).Available);
        Assert.True(ReviewReferenceDiagnostics.Capture([]).Available);
    }
    [Fact]
    public void AnUnrelatedProjectIsNotIncludedInTheSelectedClosure()
    {
        var catalog = new ReviewSourceCatalog("s", [], [], new Dictionary<string, HashSet<string>>
        { ["p"] = ["p", "dependency"] }, false)
        {
            ReferenceReports = new Dictionary<string, ReviewReferenceDiagnosticReport>
            {
                ["p"] = ReviewReferenceDiagnostics.Capture([Row("own")]),
                ["dependency"] = ReviewReferenceDiagnostics.Capture([Row("dep", false, "dependency")]),
                ["other"] = ReviewReferenceDiagnostics.Capture([Row("must-not-appear", false, "other")])
            }
        };
        var report = catalog.ReferencesFor("p");
        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.UnresolvedIdentities);
        Assert.DoesNotContain(report.Items, r => r.ProjectId == "other");
        Assert.Throws<KeyNotFoundException>(() => catalog.ReferencesFor("unknown"));
    }
    [Fact]
    public void ReportSerializationDoesNotClaimCompilerOrRuntimeResolution()
    {
        var report = ReviewReferenceDiagnostics.Capture([Row("module")]);
        Assert.Contains("does NOT prove", report.Note);
        Assert.Contains("not a live filesystem probe", report.Note);
    }

    [Fact]
    public void NormalContextSummaryDoesNotDuplicateSuccessfulReferenceRows()
    {
        var all = ReviewReferenceDiagnostics.Capture(Enumerable.Range(0, 150).Select(i => Row("ref" + i)));
        var summary = ReviewReferenceDiagnostics.Summarize(all);
        Assert.Equal(150, summary.Total);
        Assert.Empty(summary.Failures);
        Assert.False(summary.FailuresLimited);
        Assert.Equal("tenka_review_target", summary.DetailsTool);
        var failures = ReviewReferenceDiagnostics.Summarize(ReviewReferenceDiagnostics.Capture(
            Enumerable.Range(0, 8).Select(i => Row("failure" + i, false))));
        Assert.Equal(5, failures.Failures.Count);
        Assert.Equal(8, failures.UnresolvedIdentities);
        Assert.True(failures.FailuresLimited);
    }

    private sealed class BrokenReference : PortableExecutableReference
    {
        private readonly Exception _error;
        public BrokenReference(Exception error, MetadataReferenceProperties properties = default)
            : base(properties, Path.GetFullPath("broken-reference.dll"), DocumentationProvider.Default) => _error = error;
        protected override Metadata GetMetadataImpl() => throw _error;
        protected override DocumentationProvider CreateDocumentationProvider() => DocumentationProvider.Default;
        protected override PortableExecutableReference WithPropertiesImpl(MetadataReferenceProperties properties) => new BrokenReference(_error, properties);
    }
}
