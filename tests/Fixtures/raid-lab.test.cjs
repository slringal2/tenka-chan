const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {sampleEndpoint,samplePresetArguments} = require('../../src/TenkaChan.Web/wwwroot/project.js');
const root = path.resolve(__dirname,'../..');
test('sample routes are fixed and GameLab remains the default choice',()=>{
 assert.equal(sampleEndpoint('game-lab'),'/api/samples/game-lab');
 assert.equal(sampleEndpoint('raid-lab'),'/api/samples/raid-lab');
 for(const s of ['../../other','https://example.invalid','',null])assert.throws(()=>sampleEndpoint(s));
});
for(const choice of ['raid-run','raid-batch'])test(choice+' uses the bounded RaidLab CLI contract',()=>{
 const a=samplePresetArguments(choice);const values=new Map(Array.from({length:a.length/2},(_,i)=>a.slice(i*2,i*2+2)));
 assert.equal(values.has('--mode'),false);
 assert.equal(values.get('--concurrency'),choice==='raid-batch'?'4':'1');
 assert.equal(values.get('--scope'),choice==='raid-batch'?'batch':'pipeline');
 assert.equal(values.get('--seconds'),'4');assert.equal(values.get('--events'),'6000');
 assert.equal(values.get('--players'),'200');assert.ok(!a.includes('--scenario'));
});
test('single implementation argument presets are bounded',()=>{
 assert.deepEqual(samplePresetArguments(''),[]);
 assert.deepEqual(samplePresetArguments('inventory'),['--scenario','inventory','--seconds','4']);
 assert.deepEqual(samplePresetArguments('contention'),['--scenario','contention','--seconds','4','--size','300']);
 assert.throws(()=>samplePresetArguments('arbitrary'));
});
test('sample selection reads metadata only; no build or open is added to the handler',()=>{
 const js=fs.readFileSync(path.join(root,'src/TenkaChan.Web/wwwroot/project.js'),'utf8');
 const handler=js.split('$("use-sample").addEventListener')[1].split('$("selected-project").addEventListener')[0];
 assert.match(handler,/client.request\(sampleEndpoint/);assert.doesNotMatch(handler,/client.open|client.perform|\/build|\/capture/);
 const html=fs.readFileSync(path.join(root,'src/TenkaChan.Web/wwwroot/project.html'),'utf8');
 assert.match(html,/id="sample-kind"/);assert.match(html,/value="raid-lab"/);
 assert.equal((html.match(/data-path-kind=/g)||[]).length,1);
});
test('MCP sample selection does not grow the tool list or accept arbitrary paths',()=>{
 const tools=fs.readFileSync(path.join(root,'src/TenkaChan.Mcp/Agent/DashboardAgentTools.cs'),'utf8');
 assert.match(tools,/string sample = "game-lab"/);
 const gateway=fs.readFileSync(path.join(root,'src/TenkaChan.Web/AgentGatewayEndpoints.cs'),'utf8');
 assert.match(gateway,/"raid-lab" => new RaidLabSampleCatalog/);assert.match(gateway,/Select game-lab, raid-lab or ilspy/);
});
