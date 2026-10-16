#!/usr/bin/env python3
"""Source guards only, not C# compilation/runtime or real SDK protocol tests."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
AGENT = ROOT/'src/TenkaChan.Mcp/Agent'
TRACKING = ROOT/'src/TenkaChan.Mcp/Tracking'


def violations(sources):
    errors=[]
    def check(ok, note):
        if not ok: errors.append(note)
    tools=sources['DashboardAgentTools.cs']+sources['ReviewProgressTools.cs']
    names=re.findall(r'\[McpServerTool\(Name = "([^"]+)"\)',tools)
    check(len(names)==len(set(names))==44,'44 unique tools')
    check(tools.count('public static Task<CallToolResult>')==44,'typed MCP results')
    check(tools.count('AgentToolResponse.RunAsync(() => client.')==44,'all tool wrappers')
    check('approved = true' not in tools,'no default approval')
    check('.WithRequestFilters(filters => filters.AddCallToolFilter(AgentToolCallFilter.Create))' in sources['Program.cs'],'pre-binding filter registration')
    validator=sources['AgentToolCallFilter.cs']
    check('typeof(DashboardAgentTools), typeof(ReviewProgressTools)' in validator,'same registered classes')
    check('Nullable.GetUnderlyingType(p.ParameterType)' in validator and 'nullability.Create(p)' in validator,'declared nullability')
    check('!p.HasDefaultValue' in validator,'required parameters derived from declarations')
    check('value.TryGetInt32(out _)' in validator and 'value.TryGetInt64(out _)' in validator,'integer range checks')
    check('missingArguments = missing' in validator and 'invalidArguments = invalid' in validator,'named diagnostics')
    check('invalid ?? await next()' in validator,'reject before next handler')
    check('args.Keys.Count' in validator and 'allowedArguments = parameters.Select' in validator,'unknown names not echoed')
    response=sources['AgentToolResponse.cs']
    check('StructuredContent = detail.Clone()' in response and 'Text = detail.GetRawText()' in response,'same structured and text JSON')
    check('catch (OperationCanceledException) { throw; }' in response and 'catch (McpProtocolException) { throw; }' in response,'protocol and cancellation semantics')
    check('ex.Message' not in response and 'failure.Message' in response,'only known safe detail published')
    check('Text = await operation()' in response,'success text unchanged')
    navigation=sources['ReviewNavigation.cs']
    check('if (!state.RequiresReload)' in navigation,'no stale next actions')
    check('if (target.Trackable)' in navigation,'no untrackable mark inputs')
    check('expectedRevision = state.Revision' in navigation,'revision preserved')
    check('"tenka_mark_review"' not in navigation and 'approved =' not in navigation,'no write or consent suggestions')
    catalog=sources['ReviewSourceCatalog.cs']
    check('t.Id == id || t.SelectionId == id' in catalog and '.Take(2)' in catalog,'exact bounded ID resolution')
    endpoints=sources['ReviewTrackingEndpoints.cs']
    target=endpoints.split('public Task<object> Target(',1)[1].split('public Task<object> Badges(',1)[0]
    mark=endpoints.split('public Task<object> Mark(',1)[1]
    check('ResolveForRead(targetId)' in target and 'ReviewNavigation.ForTarget' in target,'read-only navigation wired')
    check('catalog.Require(request.TargetId)' in mark and 'ResolveForRead' not in mark,'writes remain canonical')
    check('ReviewProgress.ValidateMark(target,request,stale.Contains(target.ProjectId))' in mark,'write source guard remains')
    check('ReviewNavigation.CompareHashes(left.Item1,right.Item1,target)' in endpoints,'selected diff checkpoints used')
    check('[property: JsonIgnore] string Source)' in sources['ReviewProgress.cs'],'source remains hidden')
    launcher=sources['check-tenka.ps1']
    for name in ('AgentToolContractTests','ReviewSelectionBridgeTests','AgentWorkflowDefaultsTests','AgentSessionLifecycleTests','AgentSessionClientTests','DashboardAgentClientFailureTests'):
        check('FullyQualifiedName~'+name in launcher,'launcher includes '+name)
    check("'tests/Fixtures/agent_binding_probe.py'" in launcher,'binding probe wired')
    return errors


SOURCES={p.name:p.read_text(encoding='utf-8-sig') for p in [
    *[AGENT/n for n in ('DashboardAgentTools.cs','ReviewProgressTools.cs','AgentToolCallFilter.cs','AgentToolResponse.cs')],
    *[TRACKING/n for n in ('ReviewNavigation.cs','ReviewSourceCatalog.cs','ReviewProgress.cs')],
    ROOT/'src/TenkaChan.Web/ReviewTrackingEndpoints.cs',ROOT/'src/TenkaChan.Mcp/Program.cs',ROOT/'scripts/check-tenka.ps1']}


class SourceTests(unittest.TestCase):
    def test_all_source_contracts(self):
        self.assertEqual([],violations(SOURCES))

    def test_protective_mutations_are_rejected(self):
        mutations=[
            ('AgentToolCallFilter.cs','invalid ?? await next()','await next()'),
            ('AgentToolCallFilter.cs','value.TryGetInt32(out _)','true'),
            ('AgentToolResponse.cs','StructuredContent = detail.Clone()','StructuredContent = null'),
            ('ReviewNavigation.cs','if (!state.RequiresReload)','if (true)'),
            ('ReviewTrackingEndpoints.cs','catalog.Require(request.TargetId)','catalog.ResolveForRead(request.TargetId)'),
            ('DashboardAgentTools.cs','bool approved = false','bool approved = true'),
            ('Program.cs','.WithRequestFilters(filters => filters.AddCallToolFilter(AgentToolCallFilter.Create))',''),
        ]
        for file,old,new in mutations:
            with self.subTest(file=file,old=old):
                self.assertIn(old,SOURCES[file]);changed=dict(SOURCES);changed[file]=changed[file].replace(old,new,1)
                self.assertTrue(violations(changed))

    def test_guarded_tools_still_forward_named_json_arguments(self):
        for file in ('DashboardAgentTools.cs','ReviewProgressTools.cs'):
            tools=re.findall(r'public static Task<CallToolResult> (\w+)\((.*?)\)\s*=>\s*AgentToolResponse.RunAsync\(\(\) => (client\..*?)\);',SOURCES[file],re.S)
            self.assertEqual(33 if file.startswith('Dashboard') else 11,len(tools))
            for method,parameters,body in tools:
                # No wrapper adds argument values, execution approval or a retry loop.
                self.assertNotIn('true',body)
                self.assertIn('cancellationToken',body)
                self.assertIn('CancellationToken cancellationToken = default',parameters)


if __name__=='__main__':unittest.main(verbosity=2)
