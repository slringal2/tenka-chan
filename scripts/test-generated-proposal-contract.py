#!/usr/bin/env python3
"""Offline source-contract checks only. These do NOT compile/execute the C# runner.
Run ProposalValidationWorkflowTestsForGeneratedExperiments in .NET separately.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
PATHS = {
    'fixtures': 'src/TenkaChan.Mcp/Experiments/ProposalGeneratedExperiments.cs',
    'runner': 'src/TenkaChan.Mcp/Experiments/ProposalValidationRunner.cs',
    'store': 'src/TenkaChan.Mcp/Experiments/ChangeProposalStore.cs',
    'tools': 'src/TenkaChan.Mcp/Agent/ReviewProgressTools.cs',
    'gateway': 'src/TenkaChan.Web/AgentGateway.Investigations.cs',
    'fields': 'src/TenkaChan.Web/AgentGatewayEndpoints.cs',
    'tests': 'tests/TenkaChan.Tests/ProposalValidationWorkflowTests.GeneratedExperiments.cs',
}
SOURCES = {name: (ROOT / path).read_text(encoding='utf-8') for name, path in PATHS.items()}


def violations(source):
    problems = []
    def require(ok, label):
        if not ok:
            problems.append(label)
    fixtures, runner, store, tools, gateway, fields = (source[k] for k in ('fixtures', 'runner', 'store', 'tools', 'gateway', 'fields'))
    for name in ('testExperimentId', 'benchmarkExperimentId'):
        require(f'string? {name} = null' in tools, name + ' optional in MCP')
        require(f'"{name}"' in fields, name + ' accepted by gateway')
        require(f'Optional(a, "{name}")' in gateway, name + ' forwarded to runner')
    require('experiments: Get<ExperimentStore>()' in gateway and 'id => OwnExperiment(session, id)' in gateway, 'storage and ownership wired')
    require('Authorize(testExperimentId); Authorize(benchmarkExperimentId);' in runner, 'both IDs checked before staging')
    require('Authorize(report.GeneratedTest?.ExperimentId); Authorize(report.GeneratedBenchmark?.ExperimentId);' in runner, 'resumed IDs authorized')
    require('if (authorizeExperiment is null)' in runner, 'no generated authorization bypass')
    require('testId is not null && testExperimentId is not null' in runner and 'benchmarkId is not null && benchmarkExperimentId is not null' in runner, 'per-kind ambiguity rejected')
    require('testExperimentId != report.GeneratedTest?.ExperimentId' in runner and 'benchmarkExperimentId != report.GeneratedBenchmark?.ExperimentId' in runner, 'frozen workload IDs')
    require('benchmarkFilter != report.BenchmarkFilter' in runner, 'frozen filter')
    require('experiments.Read(experimentId)' in fixtures, 'reuse ExperimentStore')
    require('manifest.Kind != kind' in fixtures and 'string.Equals(subject, original, comparison)' in fixtures, 'kind and subject binding')
    require('ReadHashed(directory, "Generated.cs", manifest.SourceSha256' in fixtures, 'source verified')
    require('ReadHashed(directory, projectName, manifest.ProjectSha256' in fixtures, 'project template verified')
    require('ReadHashed(directory, "Program.cs", manifest.RunnerSha256' in fixtures, 'runner verified')
    require('proposals.CopyRoot(p.Id, variant), p.Project' in fixtures, 'generated project references correct copy')
    require('SetAttributeValue("Include", reference)' in fixtures and 'new XElement("SetTargetFramework", "TargetFramework=" + subject)' in fixtures, 'project reference and subject framework fixed')
    require('new XElement("EnableDefaultCompileItems", "false")' in fixtures, 'explicit compile inventory')
    require('FileMode.CreateNew' in fixtures and 'Prior staging has different content' in fixtures, 'no overwrite of different workload')
    require('ReadExecutionResult' not in fixtures and 'TrustedHostExperimentExecutor' not in fixtures, 'no reuse of old standalone outputs or original DLL executor')
    require('ProposalGeneratedExperiments.ProjectPath(proposals, p, fixture, variant)' in runner, 'generated fixtures actually execute')
    require(runner.count('ProposalGeneratedExperiments.VerifyAll(proposals, p, report);') >= 3, 'verify frozen fixtures on resume and before/after stage')
    require('ProposalGeneratedExperiments.VerifyAll(this, p, validation);' in store, 'apply checks frozen workloads')
    require('VerifyInputs(p, CopyRoot(id, "candidate"), true, ct);' in store, 'apply checks measured source copy')
    require('generated_fixture_changed' in fixtures and 'file.Hash != fixture.SourceSha256' in fixtures, 'workload hash binding')
    require('Generated validation selection has no frozen workload descriptor.' in fixtures, 'missing descriptor not legacy success')
    require('if (report.Status is not "awaiting_next_stage") return report;' in runner and 's.Status == "running"' in runner, 'completed/unknown stages not replayed')
    require('if (!approved || !guard.TrustMsBuild' in runner and 'TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS' in runner, 'existing execution approval retained')
    require('Math.Min(a!.SampleCount, b!.SampleCount) < 5' in runner and 't.Passed == t.Total' in runner, 'test/sample gates retained')
    require('public ProposalGeneratedFixture? GeneratedTest { get; init; }' in runner and 'public ProposalGeneratedFixture? GeneratedBenchmark { get; init; }' in runner, 'additive legacy receipt fields')
    require('ReadSdkPolicy(p, folder)' in fixtures and 'Path.Combine(directory, "global.json")' in fixtures, 'nested SDK policy preserved')
    return problems


class GeneratedProposalSourceContract(unittest.TestCase):
    def test_end_to_end_source_wiring(self):
        self.assertEqual([], violations(SOURCES))

    def test_protective_mutations_are_rejected(self):
        changes = [
            ('gateway', 'id => OwnExperiment(session, id)', 'id => { }'),
            ('runner', 'Authorize(report.GeneratedTest?.ExperimentId); Authorize(report.GeneratedBenchmark?.ExperimentId);', ''),
            ('runner', 'testExperimentId != report.GeneratedTest?.ExperimentId', 'false'),
            ('fixtures', 'manifest.Kind != kind', 'false'),
            ('fixtures', 'proposals.CopyRoot(p.Id, variant), p.Project', 'p.Root, p.Project'),
            ('fixtures', 'new XElement("EnableDefaultCompileItems", "false")', 'new XElement("EnableDefaultCompileItems", "true")'),
            ('store', 'ProposalGeneratedExperiments.VerifyAll(this, p, validation);', ''),
            ('runner', 'ProposalGeneratedExperiments.ProjectPath(proposals, p, fixture, variant)', 'p.Project'),
            ('runner', 'if (!approved || !guard.TrustMsBuild', 'if (!guard.TrustMsBuild'),
            ('runner', 'benchmarkFilter != report.BenchmarkFilter', 'false'),
        ]
        for key, before, after in changes:
            with self.subTest(key=key, before=before):
                self.assertIn(before, SOURCES[key])
                mutated = dict(SOURCES); mutated[key] = mutated[key].replace(before, after)
                self.assertTrue(violations(mutated), 'missed protective mutation')

    def test_workflow_keeps_existing_positional_parameters_and_appends_optional_ids(self):
        signature = re.search(r'public static Task<CallToolResult> ReviewWorkflow\((.*?)\)\s*=>', SOURCES['tools'], re.S).group(1)
        self.assertTrue(signature.endswith('CancellationToken cancellationToken = default, string? testExperimentId = null, string? benchmarkExperimentId = null'))
        self.assertIn('bool approved = false', signature)
        self.assertNotIn('approved = true', SOURCES['tools'])

    def test_no_new_tool_names_or_new_required_parameters(self):
        names = re.findall(r'McpServerTool\(Name = "([^"]+)"', SOURCES['tools'])
        self.assertEqual(11, len(names))
        self.assertEqual(11, len(set(names)))

    def test_csharp_regressions_remain_in_the_existing_filter_namespace(self):
        self.assertIn('class ProposalValidationWorkflowTestsForGeneratedExperiments', SOURCES['tests'])
        self.assertEqual(33, SOURCES['tests'].count('[Fact]') + SOURCES['tests'].count('[InlineData('))
        for test in ('OwnershipOfBothExperimentsIsRequiredBeforeStaging', 'ChangedFrozenWorkloadCannotAuthorizeApply',
                     'ResumeUsesFrozenFilesNotMutableExperimentStoreOrOldReports', 'EachKindCanIndependentlyUseAnExistingOrGeneratedFixture',
                     'StagingPreservesSubjectRuntimeAndNestedSdkPolicy'):
            self.assertIn(test, SOURCES['tests'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
