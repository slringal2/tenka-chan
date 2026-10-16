using System.Diagnostics;
using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record ProposalStage(string Name, string Status, int? ExitCode,
    TestRunSummary? Tests, IReadOnlyList<BenchmarkObservation> Benchmarks, string Output, string Error);
public sealed record ProposalCaseComparison(string CaseId, double BaselineNanoseconds, double CandidateNanoseconds,
    double ChangePercent, double? BaselineAllocatedBytes, double? CandidateAllocatedBytes, string Status);
public sealed record ProposalValidation(string ProposalId, string AfterHash, string TestProject, string BenchmarkProject,
    string BenchmarkFilter, string Status, IReadOnlyList<ProposalStage> Stages,
    IReadOnlyList<ProposalCaseComparison> Cases, string Note)
{
    // Null in legacy receipts and in existing-companion workflows.
    public ProposalGeneratedFixture? GeneratedTest { get; init; }
    public ProposalGeneratedFixture? GeneratedBenchmark { get; init; }
}

/// <summary>
/// One foreground call coordinates frozen baseline/candidate copies using the
/// SAME existing or generated workloads. Completed/unknown stages are not replayed.
/// </summary>
public sealed class ProposalValidationRunner(ChangeProposalStore proposals, WorkspaceGuard guard,
    IWorkspaceBuildProcess? process = null, ExperimentStore? experiments = null)
{
    private readonly IWorkspaceBuildProcess _process = process ?? new DotnetWorkspaceBuildProcess();
    private static readonly string[] StageNames = ["baseline_test", "candidate_test", "baseline_benchmark", "candidate_benchmark"];
    private const string Note = "Tests cover only the selected fixture; coverage of the changed method is not proven. Generated expectations/workloads are agent-authored, not independently derived oracles. Benchmarks are descriptive, not statistical proof. Copies are not a security sandbox: trusted MSBuild, tests and benchmarks run as the current OS user. No automatic source apply or review mark.";

    public static ProposalCompanion Choose(ChangeProposal p, string kind, string? selectedId)
    {
        var candidates = p.Companions.Where(c => c.Kind == kind).ToArray();
        if (selectedId is not null) return candidates.SingleOrDefault(c => c.Id == selectedId)
            ?? throw new ArgumentException("companion_not_found: Select an ID advertised for this proposal and kind.");
        return candidates.Length == 1 ? candidates[0]
            : throw new ArgumentException("companion_selection_required: No unique " + kind + " project. Select a companion, or prepare a generated " + kind + " with tenka_prepare_experiment and pass " + (kind == "test" ? "testExperimentId" : "benchmarkExperimentId") + ". No fixture or expected result was invented.");
    }
    public async Task<ProposalValidation> RunAsync(string proposalId, string? testId, string? benchmarkId,
        string? benchmarkFilter, bool approved, CancellationToken ct,
        string? testExperimentId = null, string? benchmarkExperimentId = null,
        Action<string>? authorizeExperiment = null)
    {
        if (!approved || !guard.TrustMsBuild || Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") != "1"
            || Environment.GetEnvironmentVariable("TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS") != "1")
            throw new UnauthorizedAccessException("approval_required: Proposal validation runs authored source UNSANDBOXED. Enable trusted MSBuild, local execution and generated host experiments; separately approve this invocation.");
        if (testId is not null && testExperimentId is not null || benchmarkId is not null && benchmarkExperimentId is not null)
            throw new ArgumentException("validation_selection_conflict: Choose either a companion ID or an experiment ID for each kind, not both.");
        ct.ThrowIfCancellationRequested();
        using var busy = proposals.Enter(proposalId);
        var p = proposals.Read(proposalId);
        if (p.Status != "prepared") throw new InvalidOperationException("Only a prepared proposal can be validated.");
        void Authorize(string? id)
        {
            if (id is null) return;
            InvestigationFiles.Id(id);
            if (authorizeExperiment is null)
                throw new UnauthorizedAccessException("Generated workloads require an investigation ownership check.");
            authorizeExperiment(id);
        }
        // Check both supplied IDs before any generated fixture is staged.
        Authorize(testExperimentId); Authorize(benchmarkExperimentId);
        var reportFile = Path.Combine(proposals.Folder(proposalId), "validation.json");
        ProposalValidation report;
        if (File.Exists(reportFile))
        {
            report = proposals.ReadValidation(proposalId);
            if (report.Stages is null || report.Stages.Count > StageNames.Length
                || !report.Stages.Select(s => s.Name).SequenceEqual(StageNames.Take(report.Stages.Count)))
                throw new InvalidDataException("Stored workflow stage sequence is invalid.");
            if (report.ProposalId != proposalId || report.AfterHash != p.AfterHash) throw new InvalidDataException("Validation receipt does not match proposal content.");
            Authorize(report.GeneratedTest?.ExperimentId); Authorize(report.GeneratedBenchmark?.ExperimentId);
            if (testId is not null && (report.GeneratedTest is not null || Choose(p, "test", testId).Path != report.TestProject)
                || benchmarkId is not null && (report.GeneratedBenchmark is not null || Choose(p, "benchmark", benchmarkId).Path != report.BenchmarkProject)
                || testExperimentId is not null && testExperimentId != report.GeneratedTest?.ExperimentId
                || benchmarkExperimentId is not null && benchmarkExperimentId != report.GeneratedBenchmark?.ExperimentId
                || benchmarkFilter is not null && benchmarkFilter != report.BenchmarkFilter)
                throw new InvalidOperationException("validation_plan_changed: Keep the frozen plan or prepare a new proposal. Completed stages will not be reused with different workloads.");
            ProposalGeneratedExperiments.VerifyAll(proposals, p, report);
            if (report.Stages.Any(s => s.Status == "running"))
                throw new InvalidOperationException("validation_outcome_unknown: A prior stage started without a terminal receipt. Inspect its artifacts; it will not be repeated automatically.");
            if (report.Status is not "awaiting_next_stage") return report;
        }
        else
        {
            var filter = benchmarkFilter ?? "*";
            if (string.IsNullOrWhiteSpace(filter) || filter.Length > 200 || filter.Any(char.IsControl)) throw new ArgumentException("Invalid benchmarkFilter.");
            var test = testExperimentId is null ? Choose(p, "test", testId) : null;
            var benchmark = benchmarkExperimentId is null ? Choose(p, "benchmark", benchmarkId) : null;
            // Validate existing fixture separation before writing a new plan or starting a child.
            ValidateExistingFixtureSeparation(p, new[] { test?.Path, benchmark?.Path });
            proposals.VerifyInputs(p, proposals.CopyRoot(proposalId, "baseline"), false, ct);
            proposals.VerifyInputs(p, proposals.CopyRoot(proposalId, "candidate"), true, ct);
            ProposalGeneratedFixture? generatedTest = null, generatedBenchmark = null;
            if (testExperimentId is not null || benchmarkExperimentId is not null)
            {
                var generated = new ProposalGeneratedExperiments(proposals, experiments
                    ?? throw new InvalidOperationException("Generated experiment storage is not available in this runner."));
                if (testExperimentId is not null) generatedTest = generated.Prepare(p, testExperimentId, "test");
                if (benchmarkExperimentId is not null) generatedBenchmark = generated.Prepare(p, benchmarkExperimentId, "benchmark");
            }
            report = new(proposalId, p.AfterHash,
                generatedTest is null ? test!.Path : ProposalGeneratedExperiments.Selection(generatedTest),
                generatedBenchmark is null ? benchmark!.Path : ProposalGeneratedExperiments.Selection(generatedBenchmark),
                filter, "awaiting_next_stage", [], [], Note)
            { GeneratedTest = generatedTest, GeneratedBenchmark = generatedBenchmark };
            proposals.SaveValidation(report);
        }
        ValidateExistingFixtureSeparation(p, new[]
        { report.GeneratedTest is null ? report.TestProject : null, report.GeneratedBenchmark is null ? report.BenchmarkProject : null });
        var clock = Stopwatch.StartNew();
        foreach (var name in StageNames.Skip(report.Stages.Count))
        {
            ct.ThrowIfCancellationRequested();
            proposals.VerifyInputs(p, proposals.CopyRoot(proposalId, "baseline"), false, ct);
            proposals.VerifyInputs(p, proposals.CopyRoot(proposalId, "candidate"), true, ct);
            ProposalGeneratedExperiments.VerifyAll(proposals, p, report);
            var running = new ProposalStage(name, "running", null, null, [], "", "");
            report = report with { Stages = report.Stages.Append(running).ToArray(), Status = "running" };
            proposals.SaveValidation(report); // Durable intent BEFORE any child starts.
            ProposalStage stage;
            try
            {
                stage = await RunStage(p, report, name, ct);
                proposals.VerifyInputs(p, proposals.CopyRoot(proposalId, "baseline"), false, ct);
                proposals.VerifyInputs(p, proposals.CopyRoot(proposalId, "candidate"), true, ct);
                ProposalGeneratedExperiments.VerifyAll(proposals, p, report);
            }
            catch (OperationCanceledException)
            {
                stage = running with { Status = "cancelled", Error = "Invocation cancelled; this stage is not retried automatically." };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or JsonException or System.Xml.XmlException or System.ComponentModel.Win32Exception or InvalidDataException)
            {
                stage = running with { Status = "stage_failed", Error = "The runner/input/report failed (" + ex.GetType().Name + "). Inspect local artifacts; nothing was applied." };
            }
            report = report with { Stages = report.Stages.Take(report.Stages.Count - 1).Append(stage).ToArray(),
                Status = stage.Status == "completed" ? "awaiting_next_stage" : "validation_failed" };
            proposals.SaveValidation(report);
            if (stage.Status != "completed") return report;
            if (report.Stages.Count < StageNames.Length && clock.Elapsed >= TimeSpan.FromSeconds(60)) return report;
        }
        var comparison = Compare(report.Stages[2].Benchmarks, report.Stages[3].Benchmarks);
        report = report with { Cases = comparison, Status = comparison.Count > 0 && comparison.All(c => c.Status != "incomparable" && c.Status != "insufficient_samples")
            ? "validation_completed" : "comparison_incomplete" };
        proposals.SaveValidation(report); return report;
    }
    internal static void ValidateFixtureSeparation(ChangeProposal proposal, string testProject, string benchmarkProject) =>
        ValidateExistingFixtureSeparation(proposal, new[] { testProject, benchmarkProject });
    private static void ValidateExistingFixtureSeparation(ChangeProposal proposal, IEnumerable<string?> fixtures)
    {
        var editedFile = ChangeProposalStore.Within(proposal.Root, proposal.File);
        var editedProject = ChangeProposalStore.Within(proposal.Root, proposal.Project);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var fixture in fixtures.Where(f => f is not null))
        {
            var project = ChangeProposalStore.Within(proposal.Root, fixture!);
            if (string.Equals(project, editedProject, comparison)
                || InvestigationFiles.Within(Path.GetDirectoryName(project)!, editedFile))
                throw new InvalidOperationException("validation_fixture_changed: The edited file/project belongs to the selected test or benchmark fixture. This workflow requires unchanged, separate validation fixtures; it does not treat edited expectations or workloads as equivalent.");
        }
    }
    private async Task<ProposalStage> RunStage(ChangeProposal p, ProposalValidation report, string name, CancellationToken ct)
    {
        var variant = name.StartsWith("baseline", StringComparison.Ordinal) ? "baseline" : "candidate";
        var test = name.EndsWith("_test", StringComparison.Ordinal);
        var root = proposals.CopyRoot(p.Id, variant);
        var fixture = test ? report.GeneratedTest : report.GeneratedBenchmark;
        var project = fixture is not null ? ProposalGeneratedExperiments.ProjectPath(proposals, p, fixture, variant)
            : ChangeProposalStore.Within(root, test ? report.TestProject : report.BenchmarkProject);
        var config = Path.Combine(proposals.Folder(p.Id), "offline.config");
        InvestigationFiles.NoLinks(config);
        const string offline = "<configuration><packageSources><clear /></packageSources></configuration>";
        if (!File.Exists(config)) File.WriteAllText(config, offline);
        else if (System.Text.Encoding.UTF8.GetString(InvestigationFiles.Bytes(config, 16000)) != offline)
            throw new InvalidDataException("The workflow restore-source configuration changed.");
        var output = InvestigationFiles.DirectoryAt(Path.Combine(root, ".tenka", name));
        var restore = await Step(project, ["restore", project, "--configfile", config, "-p:NuGetAudit=false", "--disable-build-servers"], 45, ct);
        if (restore.ExitCode != 0 || restore.Cancelled || restore.TimedOut)
            return Stage(name, "restore_failed", restore, null, []);
        string[] args = test
            ? ["test", project, "-c", "Release", "--no-restore", "--logger", "trx;LogFileName=results.trx", "--results-directory", output, "--disable-build-servers"]
            : ["run", "--project", project, "-c", "Release", "--no-restore", "--no-launch-profile", "--",
                "--filter", report.BenchmarkFilter, "--job", "short", "--iterationCount", "5", "--warmupCount", "3", "--outliers", "DontRemove", "--exporters", "FullJSON", "--artifacts", output, "--memory"];
        var run = await Step(project, args, test ? 120 : 180, ct);
        if (run.ExitCode != 0 || run.Cancelled || run.TimedOut) return Stage(name, "execution_failed", run, null, []);
        if (test)
        {
            var file = Path.Combine(output, "results.trx"); InvestigationFiles.NoLinks(file);
            var tests = ExperimentReportParser.ReadTrx(file);
            return Stage(name, AllTestsPassed(tests) ? "completed" : "tests_not_all_passed", run, tests, []);
        }
        var files = Directory.EnumerateFiles(output, "*-report-full.json", new EnumerationOptions
        { RecurseSubdirectories = true, MaxRecursionDepth = 6, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }).Take(21).ToArray();
        if (files.Length is 0 or > 20) return Stage(name, "benchmark_report_missing_or_limited", run, null, []);
        var benchmarks = files.SelectMany(file => { InvestigationFiles.NoLinks(file); return ExperimentReportParser.ReadBenchmarkJson(file); }).ToArray();
        var valid = benchmarks.Length is > 0 and <= 200 && benchmarks.Select(b => b.CaseId).Distinct(StringComparer.Ordinal).Count() == benchmarks.Length
            && benchmarks.All(b => b.MeanNanoseconds is > 0 && double.IsFinite(b.MeanNanoseconds.Value) && b.SampleCount > 0);
        return Stage(name, valid ? "completed" : "benchmark_report_invalid", run, null, benchmarks.Take(200).ToArray());
    }
    private Task<WorkspaceBuildProcessResult> Step(string project, string[] args, int seconds, CancellationToken ct)
    {
        var info = TrustedWorkspaceBuilder.CreateStartInfo(project, null, false); info.ArgumentList.Clear();
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return _process.RunAsync(info, seconds, ct);
    }
    private static ProposalStage Stage(string name, string status, WorkspaceBuildProcessResult run,
        TestRunSummary? tests, IReadOnlyList<BenchmarkObservation> benchmarks) =>
        new(name, status, run.ExitCode, tests, benchmarks, Tail(run.Output), Tail(run.Stderr));
    private static string Tail(string value) => value.Length > 6000 ? value[^6000..] : value;
    internal static bool CanApply(ProposalValidation report)
    {
        if (report.Status != "validation_completed" || report.Stages.Count != 4
            || !report.Stages.Select(s => s.Name).SequenceEqual(StageNames)
            || report.Stages.Any(s => s.Status != "completed" || s.ExitCode != 0)
            || report.Stages[0].Tests is not TestRunSummary baseline || !AllTestsPassed(baseline)
            || report.Stages[1].Tests is not TestRunSummary candidate || !AllTestsPassed(candidate)) return false;
        var cases = Compare(report.Stages[2].Benchmarks, report.Stages[3].Benchmarks);
        return cases.Count > 0 && cases.All(c => c.Status is not ("incomparable" or "insufficient_samples"));
    }
    internal static bool AllTestsPassed(TestRunSummary t) => t.Total > 0 && t.Passed == t.Total && t.Failed == 0 && t.Skipped == 0
        && t.Outcome.Equals("Completed", StringComparison.OrdinalIgnoreCase) && t.Failures.Count == 0;
    internal static IReadOnlyList<ProposalCaseComparison> Compare(IReadOnlyList<BenchmarkObservation> baseline, IReadOnlyList<BenchmarkObservation> candidate)
    {
        if (baseline.Count == 0 || candidate.Count == 0 || baseline.Select(b => b.CaseId).Distinct().Count() != baseline.Count
            || candidate.Select(b => b.CaseId).Distinct().Count() != candidate.Count) return [];
        var old = baseline.ToDictionary(b => b.CaseId, StringComparer.Ordinal); var current = candidate.ToDictionary(b => b.CaseId, StringComparer.Ordinal);
        return old.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id =>
        {
            old.TryGetValue(id, out var a); current.TryGetValue(id, out var b);
            var valid = a is not null && b is not null && !string.IsNullOrWhiteSpace(a.EnvironmentFingerprint)
                && a.EnvironmentFingerprint == b.EnvironmentFingerprint && a.MeanNanoseconds is > 0 && b.MeanNanoseconds is > 0
                && double.IsFinite(a.MeanNanoseconds.Value) && double.IsFinite(b.MeanNanoseconds.Value);
            var change = valid ? (b!.MeanNanoseconds!.Value / a!.MeanNanoseconds!.Value - 1) * 100 : 0;
            var status = !valid || !double.IsFinite(change) ? "incomparable" : Math.Min(a!.SampleCount, b!.SampleCount) < 5 ? "insufficient_samples"
                : change <= -5 ? "faster_observed" : change >= 5 ? "slower_observed" : "difference_below_threshold";
            return new ProposalCaseComparison(id, a?.MeanNanoseconds ?? 0, b?.MeanNanoseconds ?? 0, double.IsFinite(change) ? change : 0,
                a?.AllocatedBytesPerOperation, b?.AllocatedBytesPerOperation, status);
        }).ToArray();
    }
}
