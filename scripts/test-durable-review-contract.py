#!/usr/bin/env python3
"""Static source-contract guards only; not C# compilation or runtime tests."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
FILES = {
    "gateway": "src/TenkaChan.Web/AgentGatewayEndpoints.cs",
    "durable": "src/TenkaChan.Web/AgentGateway.Investigations.cs",
    "workflow": "src/TenkaChan.Web/AgentGateway.Workflow.cs",
    "store": "src/TenkaChan.Mcp/Agent/AgentInvestigationStore.cs",
    "measurement": "src/TenkaChan.Mcp/Tracking/ReviewBenchmarkEvidence.cs",
    "progress": "src/TenkaChan.Mcp/Tracking/ReviewProgress.cs",
    "tracking": "src/TenkaChan.Web/ReviewTrackingEndpoints.cs",
    "compact": "src/TenkaChan.Mcp/Tracking/ReviewContextFingerprint.cs",
    "program": "src/TenkaChan.Mcp/Program.cs",
    "tools": "src/TenkaChan.Mcp/Agent/ReviewProgressTools.cs",
}


def validate(src: dict[str, str]) -> None:
    required = {
        "gateway": ['if (cached is not null) return cached;', 'EnterInvestigation(request)',
                    'finally { investigationLease?.Dispose(); }', 'CompleteDurable(request, journal.Complete(callId, result))',
                    'Investigations.AddExperiment(session, id, project.Path)', 'MeasurementFor(session,a,target)'],
        "durable": ['ResumeAndEnter(request.SessionId', '_approvals.Revoke(request.SessionId)',
                    'journal.ReviewAttribution(session, [id]).Evidence', 'Investigations.ReadResult(session',
                    '_approvals.Authorize(session, project, "run_experiment"', 'StaleProjectsAsync(guard, true, ct)',
                    'ReadReport(store, id)', 'persistenceWarning = warning', 'independentlyVerified = false'],
        "store": ['record.ExperimentProjects.TryGetValue(id', 'reply.Outcome is not ("returned" or "failed" or "cancelled")',
                  '"operation.lock"', '"result_not_retained:', '"measurement_binding_missing:'],
        "measurement": ['saved.Sha256 != observation.ReportSha256', 'observation.TargetId != target.Id',
                        'after.SourceHash != target.SourceHash || after.ContextHash != target.ContextHash',
                        'baselineManifest.SourceSha256 != candidateManifest.SourceSha256',
                        'baselineManifest.ProjectSha256 != candidateManifest.ProjectSha256',
                        'baselineManifest.RunnerSha256 != candidateManifest.RunnerSha256',
                        'result.ExitCode != 0', 'observation.SourceInputsUnchanged', 'Core.BenchmarkSummaryComparer.Compare(',
                        'completedBefore > startedAfter', 'InvestigationFiles.Bytes(Path.Combine(directory, "execution-report.json")'],
        "progress": ['ReviewPerformanceState.Unmeasured', 'Performance = ReviewPerformanceState.From(mark.Measurement',
                     'ReviewBenchmarkMeasurements.ValidateAttachment(target, request.Measurement)'],
        "tracking": ['measurementFactory?.Invoke(target)', 'store.Save(target,measuredRequest,agent,session,evidence)'],
        "compact": ['ContextFingerprint = null, Measurement = null'],
        "program": ['.WithTools<InvestigationTools>()', 'AgentToolErrorAdvice.MissingRequired'],
        "tools": ['baselineExperimentId,candidateExperimentId', 'kind = ReviewQueueViews.DefaultKind'],
    }
    for name, markers in required.items():
        for marker in markers:
            assert marker in src[name], f"{name}: missing contract guard {marker!r}"
    assert '_experiments' not in src['gateway'] + src['workflow'], 'Ephemeral ownership dictionary remains'
    g = src['gateway']
    assert g.index('if (cached is not null) return cached;') < g.index('EnterInvestigation(request)') < g.index('await Execute(')
    d = src['durable'][src['durable'].index('private Task<object> MeasureTargetAsync'):]
    assert d.index('_approvals.Authorize') < d.index('RunAsync(id, true, ct)')
    assert '"mean"' not in g and '"measurement"' not in g, 'Caller-supplied measurement object is allowlisted'


class DurableReviewContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self.src = {key: (ROOT / path).read_text(encoding='utf-8') for key, path in FILES.items()}

    def test_all_contract_markers(self) -> None:
        validate(self.src)

    def test_resume_never_grants_execution(self) -> None:
        part = self.src['durable'].split('private AgentReply CompleteDurable')[0]
        self.assertNotIn('.Grant(', part)
        self.assertIn('_approvals.Revoke', part)

    def test_actual_report_bytes_are_hashed(self) -> None:
        m = self.src['measurement']
        self.assertIn('InvestigationFiles.Hash(bytes)', m)
        self.assertNotIn('ReportHash(ExecutionResult', m)

    def test_receipt_failure_does_not_erase_completed_outcome(self) -> None:
        d = self.src['durable']
        part = d[d.index('private AgentReply CompleteDurable'):d.index('private object InvestigationSummary')]
        self.assertIn('return reply with { PersistenceWarning', part)
        self.assertNotIn('RunAsync', part)

    def test_existing_tool_parameters_keep_position(self) -> None:
        self.assertIn('CancellationToken cancellationToken = default,\n        string? baselineExperimentId', self.src['tools'])

    def test_plain_marks_remain_unmeasured(self) -> None:
        self.assertIn('evidence is null ? Unmeasured', self.src['measurement'])
        self.assertIn('true, false, evidence.Baseline', self.src['measurement'])

    def test_current_source_is_rechecked_before_saving(self) -> None:
        s = self.src['tracking']
        a = s.index('var measuredRequest'); b = s.index('store.Save(target,measuredRequest')
        self.assertIn('StaleProjectsAsync(guard,true,ct)', s[a:b])
        self.assertIn('ct.ThrowIfCancellationRequested()', s[a:b])

    def test_session_end_only_unbinds_data(self) -> None:
        s = self.src['workflow'].split('private object SessionStatus')[0]
        self.assertIn('Investigations.Unbind(session)', s)
        self.assertNotIn('Delete', s)

    def test_mutation_guards_detect_removed_safety_checks(self) -> None:
        mutations = [
            ('gateway', 'if (cached is not null) return cached;'),
            ('gateway', 'finally { investigationLease?.Dispose(); }'),
            ('durable', '_approvals.Revoke(request.SessionId)'),
            ('measurement', 'saved.Sha256 != observation.ReportSha256'),
            ('measurement', 'observation.TargetId != target.Id'),
            ('measurement', 'baselineManifest.SourceSha256 != candidateManifest.SourceSha256'),
            ('measurement', 'result.ExitCode != 0'),
            ('program', '.WithTools<InvestigationTools>()'),
        ]
        for file, marker in mutations:
            with self.subTest(file=file, marker=marker):
                changed = dict(self.src); changed[file] = changed[file].replace(marker, 'REMOVED_GUARD', 1)
                with self.assertRaises(AssertionError):
                    validate(changed)


if __name__ == '__main__':
    unittest.main(verbosity=2)
