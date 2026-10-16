#!/usr/bin/env python3
"""Offline SOURCE-shape guards. These neither compile C# nor run MCP/ASP.NET.
Run the three Agent* C# regression classes separately; see the accompanying docs.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
AGENT = 'src/TenkaChan.Mcp/Agent/'
WEB = 'src/TenkaChan.Web/'
FILES = [AGENT + name for name in ('DashboardAgentTools.cs', 'ReviewProgressTools.cs',
    'AgentJournal.cs', 'AgentSessionClient.cs', 'AgentSessionOptions.cs',
    'AgentSelectionDefaults.cs', 'AgentOperationPermissions.cs')]
FILES += [WEB + 'AgentGatewayEndpoints.cs', WEB + 'DashboardState.AgentDefaults.cs', 'src/TenkaChan.Mcp/Program.cs', 'README.md']
SOURCES = {p: (ROOT / p).read_text(encoding='utf-8') for p in FILES}
TOOL = re.compile(r'\[McpServerTool\(Name = "([^"]+)"\)\].*?public static Task<(?:string|CallToolResult)> \w+\((.*?)\)\s*=>\s*(.*?);', re.S)


def catalogue(sources):
    result = {}
    for path in (AGENT + 'DashboardAgentTools.cs', AGENT + 'ReviewProgressTools.cs'):
        for name, parameters, body in TOOL.findall(sources[path]):
            if name in result:
                raise AssertionError('Duplicate tool: ' + name)
            result[name] = (parameters, body)
    return result


def guards(sources):
    errors = []
    def check(condition, message):
        if not condition: errors.append(message)
    tools = catalogue(sources)
    check(len(tools) == 38, '38 public tools')
    check({'tenka_sessions_list', 'tenka_session_end'} <= tools.keys(), 'management tools')
    snapshots = {n for n, (p, _) in tools.items() if 'snapshotId' in p}
    check(bool(snapshots) and all('string? snapshotId = null' in tools[n][0] for n in snapshots), 'optional snapshots')
    for name in ('tenka_build', 'tenka_prepare_experiment'):
        check('string? projectId = null' in tools[name][0], name + ' optional project')
    check('string path' in tools['tenka_open_project'][0] and 'bool approved = false' in tools['tenka_open_project'][0], 'open remains explicit')
    for name, (parameters, _) in tools.items():
        seen_default = False
        for parameter in parameters.split(','):
            default = '=' in parameter
            check(not seen_default or default, name + ' C# optional ordering')
            seen_default |= default
    gateway = sources[WEB + 'AgentGatewayEndpoints.cs']
    actions = gateway.split('ExplorerActions =', 1)[1].split('private static', 1)[0]
    dispatch = set(re.findall(r'\["([^"]+)"\]', actions))
    extra = gateway.split('SnapshotTools =', 1)[1].split('private static', 1)[0]
    dispatch.update(re.findall(r'"([^"]+)"', extra))
    routed = set()
    for name in snapshots:
        m = re.search(r'CallAsync\("([^"]+)"', tools[name][1])
        if m: routed.add(m.group(1))
    check(routed == dispatch, 'snapshot dispatch covers every declared source tool')
    check(gateway.index('if (cached is not null) return cached') < gateway.index('ResolveAgentSelectionAsync('), 'cache before resolution')
    check('Execute(request.SessionId, request.Tool, arguments, deadline.Token)' in gateway, 'resolved args executed')
    check('resolvedSnapshot: AgentJournal.Snapshot(arguments)' in gateway, 'resolved snapshot recorded')
    resolver = sources[WEB + 'DashboardState.AgentDefaults.cs']
    check('ExploreAsync(requested,' in resolver and 'index.SnapshotId' in resolver, 'resolver shares workspace gate')
    selection = sources[AGENT + 'AgentSelectionDefaults.cs']
    check('requested is not null && requested != currentSnapshotId' in selection, 'explicit stale rejection')
    check('projects.Count == 1' in selection and 'ambiguous_project' in selection, 'no ambiguous first project')
    check('approved' not in selection, 'resolver cannot grant consent')
    policy = sources[AGENT + 'AgentOperationPermissions.cs']
    check(policy.count('if (!approved)') == 2, 'separate approval gates')
    check('if (!hostAllowsBuild)' in policy and 'if (!hostAllowsExecution)' in policy, 'host gates')
    check('canExecuteWithoutApproval = false' in policy and 'executionIsSandboxed = false' in policy, 'honest permission metadata')
    check('AgentOperationPermissions.RequireBuild(policy.AllowBuild, Bool(a, "approved"))' in gateway, 'build policy wired')
    check('AgentOperationPermissions.RequireExecution(policy.AllowExecution, Bool(a, "approved"))' in gateway, 'execution policy wired')
    client = sources[AGENT + 'AgentSessionClient.cs']
    check('/agent/v1/begin' not in client and '/agent/v1/call' not in client, 'management never allocates session')
    check('AllowAutoRedirect = false' in client and 'UseProxy = false' in client and 'DashboardAgentClient.ValidateConnection(saved)' in client, 'loopback credential safety')
    check('HttpCompletionOption.ResponseHeadersRead, deadline.Token' in client and 'ReadBoundedAsync(stream, response.IsSuccessStatusCode ? 1_000_000 : 16_000, deadline.Token)' in client, 'deadline covers body')
    check('catch (OperationCanceledException) when (!ct.IsCancellationRequested)' in client, 'caller cancellation preserved')
    check('ex.Message' not in client and 'ReadHttpDetail' in client, 'safe error exposure')
    check(client.count('http.SendAsync(') == 1, 'single send path')
    journal = sources[AGENT + 'AgentJournal.cs']
    list_body = journal.split('public AgentSessionList ListSessions()', 1)[1].split('public AgentSessionView EndIdleSession', 1)[0]
    check('RequireSession(' not in list_body and '.Seen =' not in list_body and 'Begin(' not in list_body, 'list no renewal or allocation')
    end = journal.split('public AgentSessionView EndIdleSession', 1)[1].split('public object MonitorResult', 1)[0]
    check(end.index('if (s.Active is not null)') < end.index('s.Status = "finished"'), 'running cannot end')
    check('s.Active is null && _time.GetUtcNow() - s.Seen >= TimeSpan.FromMinutes(IdleTimeoutMinutes)' in journal, 'idle-only configured expiry')
    check('_sessions.Count >= SessionLimit' in journal and 'AgentSessionOptions.FromEnvironment()' in gateway, 'configurable limit wired')
    check('request.Path' not in gateway and 'context.Request.Path.StartsWithSegments("/agent/v1")' in gateway and 'connection.Authorize(' in gateway, 'management routes under auth')
    check('journal.ListSessions()' in gateway and 'journal.EndIdleSession(request.SessionId, request.Approved)' in gateway, 'management endpoints direct')
    check('AddSingleton<AgentSessionClient>()' in sources['src/TenkaChan.Mcp/Program.cs'], 'management client injected')
    readme = sources['README.md']
    check('公開ツールは38個' in readme and all(n in readme for n in ('tenka_sessions_list', 'tenka_session_end')), 'README inventory')
    return errors


class SourceContract(unittest.TestCase):
    def test_current_source_contract(self):
        self.assertEqual([], guards(SOURCES))

    def test_registered_classes_match_catalogue(self):
        classes = re.findall(r'\.WithTools<([A-Za-z_][A-Za-z0-9_]*)>\(\)', SOURCES['src/TenkaChan.Mcp/Program.cs'])
        self.assertEqual(['DashboardAgentTools', 'ReviewProgressTools'], classes)
        self.assertEqual(38, len(catalogue(SOURCES)))

    def test_mutations_are_rejected(self):
        mutations = [
            (AGENT+'ReviewProgressTools.cs', 'string? snapshotId = null', 'string snapshotId'),
            (WEB+'AgentGatewayEndpoints.cs', 'Execute(request.SessionId, request.Tool, arguments, deadline.Token)', 'Execute(request.SessionId, request.Tool, request.Arguments, deadline.Token)'),
            (AGENT+'AgentSelectionDefaults.cs', 'requested is not null && requested != currentSnapshotId', 'false'),
            (AGENT+'AgentOperationPermissions.cs', 'if (!approved)', 'if (false)'),
            (AGENT+'AgentSessionClient.cs', 'UseProxy = false', 'UseProxy = true'),
            (AGENT+'AgentSessionClient.cs', 'deadline.Token);\n            if (!response.IsSuccessStatusCode)', 'ct);\n            if (!response.IsSuccessStatusCode)'),
            (AGENT+'AgentJournal.cs', 's.Active is null && _time.GetUtcNow()', '_time.GetUtcNow()'),
            (AGENT+'AgentJournal.cs', '_sessions.Count >= SessionLimit', '_sessions.Count >= MaxSessions'),
            ('src/TenkaChan.Mcp/Program.cs', 'builder.Services.AddSingleton<AgentSessionClient>();', ''),
        ]
        for path, original, replacement in mutations:
            with self.subTest(path=path, original=original):
                self.assertIn(original, SOURCES[path])
                changed = dict(SOURCES); changed[path] = changed[path].replace(original, replacement, 1)
                self.assertTrue(guards(changed), 'Guard accepted a negative mutation')


if __name__ == '__main__':
    unittest.main(verbosity=2)
