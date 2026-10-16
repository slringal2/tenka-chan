"""SOURCE CONTRACT checks only. This script neither compiles nor runs C#.
Every dynamic copy/runner case is authored in xUnit and must run in a .NET SDK environment.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
PATHS = {
 'store': 'src/TenkaChan.Mcp/Agent/AgentInvestigationStore.cs',
 'files': 'src/TenkaChan.Mcp/Agent/InvestigationFiles.cs',
 'proposal': 'src/TenkaChan.Mcp/Experiments/ChangeProposalStore.cs',
 'runner': 'src/TenkaChan.Mcp/Experiments/ProposalValidationRunner.cs',
 'gateway': 'src/TenkaChan.Web/AgentGatewayEndpoints.cs',
 'bridge': 'src/TenkaChan.Web/AgentGateway.Investigations.cs',
 'selection': 'src/TenkaChan.Web/DashboardState.Proposals.cs',
 'tools': 'src/TenkaChan.Mcp/Agent/ReviewProgressTools.cs',
}
SOURCES = {k:(ROOT/v).read_text() for k,v in PATHS.items()}

def guard(s):
 errors=[]
 def check(condition,reason):
  if not condition:errors.append(reason)
 check('if (!approved)' in s['store'].split(' Resume(',1)[1].split('private void Update',1)[0], 'resume approval')
 check('InvestigationFiles.Lock(Folder(id), "operation.lock")' in s['store'], 'busy resume lease')
 check('clientName ==' not in s['store'], 'self reported name not auth')
 check('Outcome' in s['store'] and 'Only terminal calls' in s['store'], 'completed outcome receipt')
 check('expectedFileHash' in s['proposal'] and 'InvestigationFiles.Hash(before) != expectedFileHash' in s['proposal'], 'file hash precondition')
 check('File.Replace(temporary, original, backup)' in s['proposal'], 'retained displaced backup')
 check('"proposal_recovery_required:' in s['proposal'], 'no crash replay')
 check('!ProposalValidationRunner.CanApply(validation)' in s['proposal'], 'validated apply')
 check('if (!InvestigationFiles.Within(_root, root) &&' in s['proposal'], 'candidate copies not skipped')
 check('t.Passed == t.Total' in s['runner'] and 't.Skipped == 0' in s['runner'], 'all tests passed not partial')
 check('report.Stages.Any(s => s.Status == "running")' in s['runner'], 'unknown started stage refuses replay')
 check('proposals.SaveValidation(report); // Durable intent BEFORE' in s['runner'], 'durable intent')
 check('a.EnvironmentFingerprint == b.EnvironmentFingerprint' in s['runner'], 'same benchmark environment')
 check('StageNames.Skip(report.Stages.Count)' in s['runner'], 'resume without rerunning completed stages')
 check('var (callId, cached) = journal.Start(request)' in s['gateway'] and s['gateway'].index('if (cached is not null) return cached')<s['gateway'].index('_investigations.Resume('),'idempotence before resume')
 check('_investigations.RequireExperiment(session, id)' in s['gateway'], 'durable experiment ownership')
 check('_investigations.Evidence(session,Strings(a,"evidenceCallIds"))' in s['gateway'], 'durable review attribution')
 check('_investigations.RequireProposal(session, proposalId)' in s['bridge'], 'proposal owned')
 check('PersistenceWarning' in s['bridge'] and 'return reply with' in s['bridge'], 'save failure not execution replay')
 check('catalog.StaleProjectsAsync(guard, true, ct)' in s['selection'] and 'ExploreAsync(snapshotId' in s['selection'], 'forced stale source check and gate')
 check(s['tools'].count('McpServerTool(Name = ')==11,'five old and six new tools')
 for name in ['tenka_investigation','tenka_propose_change','tenka_diff_proposal','tenka_apply_change','tenka_revert_change','tenka_review_workflow']:
  check(name in s['tools'],name)
 return errors

class Contracts(unittest.TestCase):
 def test_source_contract(self): self.assertEqual([],guard(SOURCES))
 def test_negative_mutations(self):
  mutations=[('store','if (!approved)','if (false)'),('store','InvestigationFiles.Lock(Folder(id), "operation.lock")','InvestigationFiles.Lock(Folder(id), "wrong.lock")'),
   ('proposal','!ProposalValidationRunner.CanApply(validation)','false'),('proposal','if (!InvestigationFiles.Within(_root, root) &&','if (true &&'),
   ('runner','t.Passed == t.Total','t.Passed > 0'),('runner','t.Skipped == 0','true'),
   ('runner','a.EnvironmentFingerprint == b.EnvironmentFingerprint','true'),('runner','StageNames.Skip(report.Stages.Count)','StageNames'),
   ('gateway','_investigations.RequireExperiment(session, id)','_ = id'),('selection','catalog.StaleProjectsAsync(guard, true, ct)','catalog.StaleProjectsAsync(guard, false, ct)')]
  for file,old,new in mutations:
   with self.subTest(file=file,old=old):
    self.assertIn(old,SOURCES[file]);s=dict(SOURCES);s[file]=s[file].replace(old,new,1);self.assertTrue(guard(s))
 def test_tools_use_supported_parameter_shapes_without_implicit_approval(self):
  tools=re.findall(r'public static Task<CallToolResult> (\w+)\((.*?)\)\s*=>\s*AgentToolResponse.RunAsync\(\(\) => (client\..*?)\);',SOURCES['tools'],re.S)
  self.assertEqual(11,len(tools))
  for name,params,body in tools:
   seen=False
   for param in params.split(','):
    self.assertRegex(param.strip(),r'^(DashboardAgentClient|string\??|int\??|long\??|bool|string\[\]\??|CancellationToken) ')
    default='=' in param;self.assertFalse(seen and not default,(name,param));seen|=default
   self.assertNotIn('approved = true',params);self.assertNotIn('approved = true',body)
 def test_original_source_write_is_confined_to_separate_replace_operation(self):
  before,replace=SOURCES['proposal'].split('public object Replace(',1)
  self.assertNotIn('File.Replace(',before)
  self.assertNotIn('proposals.Replace(',SOURCES['runner'])
  self.assertNotIn('ReviewProgressStore',SOURCES['runner'])
 def test_recovery_does_not_delete_backup_or_reset_git(self):
  for name in ['proposal','runner','bridge','store']:
   self.assertNotIn('Directory.Delete(',SOURCES[name]);self.assertNotIn('File.Delete(backup)',SOURCES[name]);self.assertNotIn('git reset',SOURCES[name])
 def test_store_limits_and_no_reparse_checks_exist(self):
  self.assertIn('FileAttributes.ReparsePoint',SOURCES['files']);self.assertIn('FileShare.None',SOURCES['files'])
  self.assertIn('record.Evidence.Count >= 512',SOURCES['store']);self.assertIn('visited > 16000',SOURCES['proposal'])

if __name__=='__main__': unittest.main(verbosity=2)
