const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { ProjectExplorerClient, projectDependsOn, projectLabel } = require('../../src/TenkaChan.Web/wwwroot/project-client.js');
const { pages, primaryKeys, detailKeys, route } = require('../../src/TenkaChan.Web/wwwroot/common-shell.js');
const { tabs } = require('../../src/TenkaChan.Web/wwwroot/project-tabs.js');
const projects = () => [
 {id:'app', name:'Game.App', path:'C:\\Game\\App\\App.csproj',projectReferenceIds:['core']},
 {id:'core',name:'Core',path:'C:\\Game\\GameLogic\\Core.csproj',projectReferenceIds:['utils']},
 {id:'utils',name:'Core',path:'C:\\Game\\Math\\Core.csproj',projectReferenceIds:[]},
 {id:'other',name:'Editor',path:'C:\\Game\\Editor\\Editor.csproj',projectReferenceIds:[]}
];
function context(snapshot='s1') { return {snapshotId:snapshot,projects:projects(),assemblies:[
 {id:'app-output',projectId:'app',runnable:true,assemblyPath:'app.dll'},
 {id:'core-output',projectId:'core',runnable:false,assemblyPath:'core.dll'},
 {id:'other-output',projectId:'other',runnable:true,assemblyPath:'editor.dll'}
],companions:[],history:[]}; }
function client() {const calls=[];const c=new ProjectExplorerClient(async(url)=>{calls.push(url);return {ok:true,json:async()=>({snapshotId:'s1',items:[],total:0})};});c.replaceContext(context());return {c,calls};}
test('only four primary navigation entries, all old routes still reachable',()=>{
 assert.deepEqual(primaryKeys,['overview','project','review-progress','autoreview']);
 assert.equal(new Set([...primaryKeys,...detailKeys,'guide']).size,pages.length);
 for(const p of pages){const u=new URL(p[3],'http://localhost');assert.equal(route(u.pathname,u.hash),p[0]);}
});
test('phase tabs cover preparation, code, execution and results',()=>assert.deepEqual(tabs,['prepare','code','run','results']));
test('selecting a class project does not narrow all-project search',async()=>{
 const {c,calls}=client();c.setProject('core');await c.types();
 assert.equal(new URL(calls[0],'http://localhost').searchParams.get('projectId'),'');
 assert.equal(c.projectId,'core');assert.equal(c.assemblyId,'core-output');
});
test('changing the search scope does not change the build or execution selection',async()=>{
 const {c,calls}=client();c.setProject('core');c.setScope('utils');await c.types();
 assert.equal(new URL(calls[0],'http://localhost').searchParams.get('projectId'),'utils');
 assert.equal(c.projectId,'core');assert.equal(c.launchId,'app-output');
});
test('only a transitive reference-compatible application is auto-selected',()=>{
 const {c}=client();c.setProject('utils');assert.equal(c.launchId,'app-output');
 assert.equal(projectDependsOn(projects(),'app','utils'),true);
 assert.equal(projectDependsOn(projects(),'other','utils'),false);
});
test('multiple compatible apps are left for explicit selection',()=>{
 const {c}=client();c.context.projects.find(p=>p.id==='other').projectReferenceIds=['core'];
 c.setProject('core');assert.equal(c.launchId,'');
});
test('single unrelated executable is not silently selected for a library',()=>{
 const {c}=client();c.context.assemblies=c.context.assemblies.filter(a=>a.id!=='app-output');
 c.setProject('core');assert.equal(c.launchId,'');
});
test('unknown project references and cycles terminate without matching unrelated projects',()=>{
 const p=[{id:'a',projectReferenceIds:['b','missing']},{id:'b',projectReferenceIds:['a']}];
 assert.equal(projectDependsOn(p,'a','b'),true);assert.equal(projectDependsOn(p,'a','c'),false);
});
test('same-named projects are distinguishable without using names as identity',()=>{
 const rows=projects(),a=projectLabel(rows[1],rows),b=projectLabel(rows[2],rows);
 assert.notEqual(a,b);assert.match(a,/GameLogic\/Core.csproj/);assert.match(b,/Math\/Core.csproj/);
 assert.equal(projectLabel(rows[0],rows),'Game.App');
});
test('identical project paths can still be distinguished by IDs',()=>{
 const rows=[{id:'a111',name:'same',path:'A.csproj'},{id:'b222',name:'same',path:'A.csproj'}];
 assert.notEqual(projectLabel(rows[0],rows),projectLabel(rows[1],rows));
});
test('reloading keeps search scope by project path, not stale Roslyn IDs',()=>{
 const {c}=client();c.setScope('core');const next=context('s2');next.projects[1].id='new-core';c.replaceContext(next);
 assert.equal(c.scopeProjectId,'new-core');
});
test('stale search result cannot overwrite a later scope',async()=>{
 let resolve;const c=new ProjectExplorerClient(()=>new Promise(r=>resolve=r));c.replaceContext(context());
 const read=c.types();c.setScope('core');resolve({ok:true,json:async()=>({snapshotId:'s1',items:[],total:0})});
 await assert.rejects(read,{name:'StaleExplorerResponse'});
});
test('no new model controls or raw source injection were introduced',()=>{
 const ui=fs.readFileSync(path.join(__dirname,'../../src/TenkaChan.Web/wwwroot/project-tabs.js'),'utf8');
 assert.doesNotMatch(ui,/fetch\(|innerHTML|method:\s*['"]POST/);
 const html=fs.readFileSync(path.join(__dirname,'../../src/TenkaChan.Web/wwwroot/project.html'),'utf8');
 for(const id of ['project-open','project-build','project-scan','capture-jit','capture-trace','result-history'])assert.ok(html.includes('id="'+id+'"'));
 for(const key of tabs)assert.ok(html.includes('id="workspace-panel-'+key+'"'));
});
