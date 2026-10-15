const test=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const root=path.resolve(__dirname,'../..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const {sampleEndpoint,samplePresetArguments}=require('../../src/TenkaChan.Web/wwwroot/project.js');
function files(dir){return fs.readdirSync(path.join(root,dir),{withFileTypes:true}).flatMap(e=>e.isDirectory()?files(dir+'/'+e.name):[dir+'/'+e.name]);}
test('both bundled samples contain one implementation and no prewritten alternative',()=>{
 for(const dir of ['samples/Tenka.Sample.GameLab','samples/Tenka.Sample.RaidLab'])
 for(const f of files(dir).filter(f=>f.endsWith('.cs')))assert.doesNotMatch(read(f),/CountOwnedFast|SumMask|BuildFast|IndexedRaidAnalyzer|BuildOptimized|NoInlining|Benchmark\(Baseline/);
 assert.equal(fs.existsSync(path.join(root,'samples/Tenka.Sample.RaidLab/Application/IndexedRaidAnalyzer.cs')),false);
 assert.equal(fs.existsSync(path.join(root,'samples/Tenka.Sample.RaidLab/Application/BaselineRaidAnalyzer.cs')),false);
 assert.match(read('samples/Tenka.Sample.RaidLab/Application/RaidAnalyzer.cs'),/class RaidAnalyzer/);
});
test('normal sample code does not contain a solution hint label',()=>{
 for(const f of ['samples/Tenka.Sample.RaidLab/Application/RaidAnalyzer.cs','samples/Tenka.Sample.GameLab/GameWorkloads.cs'])
  assert.doesNotMatch(read(f),/Intentionally inefficient|deliberate hotspots|teaching baseline|BuildLookupOnce/);
});
test('removed runtime modes are not offered or silently aliased',()=>{
 for(const preset of ['raid-optimized','raid-baseline'])assert.throws(()=>samplePresetArguments(preset));
 assert.doesNotMatch(read('src/TenkaChan.Web/wwwroot/project.html'),/raid-optimized|raid-baseline/);
 for(const preset of ['raid-run','raid-batch','inventory','contention'])assert.ok(!samplePresetArguments(preset).includes('--mode'));
});
test('large OSS option is a fixed read-only local catalogue',()=>{
 assert.equal(sampleEndpoint('ilspy'),'/api/samples/ilspy');
 for(const x of ['https://github.com/any/repo','../ilspy'])assert.throws(()=>sampleEndpoint(x));
 const s=read('src/TenkaChan.Mcp/Security/LargeOssSampleCatalog.cs');
 assert.match(s,/guard.RequireDirectory/);assert.match(s,/guard.RequireFile/);
 assert.doesNotMatch(s,/ProcessStartInfo|Process.Start|HttpClient|Directory.CreateDirectory/);
});
test('recipe is pinned to a full official commit, not a movable branch',()=>{
 const m=JSON.parse(read('samples/oss/ilspy.lock.json'));
 assert.equal(m.commit,'5deb6fb4258a09ba0ff5f63fa6180b1f8d6fb6ad');
 assert.equal(m.repository,'https://github.com/icsharpcode/ILSpy.git');assert.equal(m.tag,'v10.0');
 assert.equal(m.analysisProjects.length,3);assert.ok(m.analysisProjects.includes(m.entryProject));
 const s=read('scripts/prepare-large-oss.ps1');assert.ok(s.includes(m.commit));assert.ok(s.includes(m.repository));
 assert.match(s,/FETCH_HEAD/);assert.match(s,/--detach/);assert.doesNotMatch(s,/--depth/);
});
test('recipe does not reset, clean, overwrite source, or start CI',()=>{
 const s=read('scripts/prepare-large-oss.ps1');
 assert.doesNotMatch(s,/Remove-Item|Invoke-Expression|git reset|git clean|workflow_dispatch|taskkill/i);
 assert.match(s,/if \(Test-Path -LiteralPath \$source\)/);assert.match(s,/source.partial-/);
 assert.match(s,/core.hooksPath/);assert.match(s,/ReparsePoint/);assert.match(s,/Check-Checkout/);
});
test('acquisition, build, and execution are separate operations',()=>{
 const s=read('scripts/prepare-large-oss.ps1');
 assert.match(s,/'Menu','Fetch','Build','Smoke','Info'/);assert.match(s,/Read-Host.*Y=/);
 assert.match(s,/Invoke-Dotnet @\(\$dll,'--help'\)/);
 assert.doesNotMatch(s,/Invoke-Dotnet @\('test'|submodule.*update|winget.*install/);
});
test('statistics state their scope and report actual tracked-file counts',()=>{
 const s=read('scripts/prepare-large-oss.ps1');assert.match(s,/ls-files/);assert.match(s,/ReadLines/);
 assert.match(s,/physicalCsLines=\$lines/);assert.match(s,/workingTreeChanged/);
 assert.match(s,/including upstream tests and generated sources/);
});
test('the visible guide contains no implementation answer sheet',()=>{
 const s=read('src/TenkaChan.Web/wwwroot/guide.html');
 assert.doesNotMatch(s,/CountOwnedFast|BuildOptimized|SumMask|new HashSet|new StringBuilder/);
 assert.match(s,/run-large-oss.bat/);
});
test('RaidLab retains an independent golden test and non-mutating invariants',()=>{
 const s=read('samples/Tenka.Sample.RaidLab/Verification/ContractChecks.cs');
 assert.match(s,/ExpectedGolden/);assert.match(s,/invariants/);assert.match(s,/Equal\(before, JsonSerializer.Serialize\(data\)\)/);
 assert.doesNotMatch(s,/differential|IndexedRaidAnalyzer/);
});
