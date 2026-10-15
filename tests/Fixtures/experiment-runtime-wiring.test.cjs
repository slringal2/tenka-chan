// Source/document/launcher contract checks, NOT execution or compilation of C#.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');

test('runtime selection reaches the existing experiment tool without adding a new tool', () => {
  const tools=read('src/TenkaChan.Mcp/Agent/DashboardAgentTools.cs');
  const gateway=read('src/TenkaChan.Web/AgentGatewayEndpoints.cs');
  assert.match(tools,/string\? runtimeFramework = null/);
  assert.match(tools,/new \{ snapshotId, projectId, kind, generatedCode, targetFramework, runtimeFramework \}/);
  assert.match(gateway,/"generatedCode", "targetFramework", "runtimeFramework"/);
  assert.match(gateway,/kind, code, Optional\(a, "targetFramework"\), null, Optional\(a, "runtimeFramework"\)/);
});
test('library selection and runtime are separate in saved manifests and receipts',()=>{
  const store=read('src/TenkaChan.Mcp/Experiments/ExperimentStore.cs');
  assert.match(store,/SubjectTargetFramework = frameworks.SubjectTargetFramework/);
  assert.match(store,/new XElement\("TargetFramework", selection.TargetFramework\)/);
  assert.match(store,/ExperimentFrameworkPolicy.ValidatePair\(manifest.SubjectTargetFramework \?\? manifest.TargetFramework, manifest.TargetFramework\)/);
});
test('netstandard experiments refuse the legacy container staging path',()=>{
  const store=read('src/TenkaChan.Mcp/Experiments/ExperimentStore.cs');
  const artifacts=store.split('internal string CreateArtifactsDirectory')[1].split('internal void SaveExecutionResult')[0];
  assert.match(artifacts,/IsStandardLibrary\(manifest.SubjectTargetFramework\)/);
  assert.match(artifacts,/requires the host runner/);
});
test('generated host runner uses a reference target rather than executing the library directly',()=>{
  const host=read('src/TenkaChan.Mcp/Experiments/TrustedHostExperimentExecutor.cs');
  assert.match(host,/RequireBuiltExperimentReference/);
  assert.match(host,/subjectFramework, manifest.TargetFramework/);
  assert.match(host,/!Enabled \|\| !approveUnisolatedExecution/);
  assert.match(host,/TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS/);
});
test('framework resolver is read-only and does not start a model or install a runtime',()=>{
  const policy=read('src/TenkaChan.Mcp/Experiments/ExperimentFrameworkPolicy.cs');
  assert.match(policy,/DtdProcessing = DtdProcessing.Prohibit/);
  assert.match(policy,/default_net8_baseline_not_production/);
  assert.doesNotMatch(policy,/Process\.Start|HttpClient|File\.Write|File\.Delete|Assembly\.Load/);
});
test('README lists exactly the currently registered tool names',()=>{
  const source=read('src/TenkaChan.Mcp/Agent/DashboardAgentTools.cs')+read('src/TenkaChan.Mcp/Agent/ReviewProgressTools.cs');
  const names=new Set([...source.matchAll(/McpServerTool\(Name = "([^"]+)"/g)].map(m=>m[1]));
  const docs=new Set([...read('README.md').matchAll(/`(tenka_[a-z_]+)`/g)].map(m=>m[1]));
  assert.equal(names.size,36);assert.deepEqual([...docs].sort(),[...names].sort());
});
test('main quick start no longer tells the operator to configure an internal model',()=>{
  const doc=read('README.md');
  assert.match(doc,/外部LLMエージェント → stdio MCPアダプター/);
  assert.match(doc,/同じWebプロセス/);
  assert.doesNotMatch(doc,/TENKA_LLM_ENDPOINT|llama-server|autonomous_review_start/);
  assert.match(doc,/CIは\[停止中\]/);
});
test('local check launcher retains exit code, does not kill or reset user processes/files',()=>{
  const bat=read('check-tenka.bat'),ps=read('scripts/check-tenka.ps1');
  assert.match(bat,/exit \/b %TENKA_CHECK_EXIT%/);
  assert.match(ps,/summary.json/);assert.match(ps,/regression.trx/);
  assert.doesNotMatch(ps,/Stop-Process|taskkill|Invoke-Expression|git reset|git clean|\$home\s*=/i);
  assert.doesNotMatch(ps,/Get-ChildItem\s+Env:|Get-Content[^\n]+\$connection/);
  assert.match(ps,/TENKA_ENABLE_DOCKER = '0'/);
});
