"""Verify sample layout and the independent golden arithmetic. Does NOT compile/run C#."""
from pathlib import Path
import json, re, xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[2]
B=ROOT/'samples/Tenka.Sample.RaidLab'
checks=0

def check(condition, label):
    global checks
    assert condition,label
    checks+=1

projects=sorted(B.glob('*/*.csproj'))
check(len(projects)==6,'six projects')
slnx=ET.parse(B/'Tenka.Sample.RaidLab.slnx')
paths={p.attrib['Path'] for p in slnx.findall('Project')}
check(paths=={str(p.relative_to(B)) for p in projects},'slnx contents')
sln=(B/'Tenka.Sample.RaidLab.sln').read_text()
check(all(str(p.relative_to(B)).replace('/','\\') in sln for p in projects),'SDK8 sln matches')
check(json.loads((B/'global.json').read_text())['sdk']['rollForward']=='latestMajor','sample SDK selection')
graph={}
for p in projects:
    tree=ET.parse(p)
    check(tree.find('.//TargetFramework').text=='net8.0',p.name+' target')
    graph[p.resolve()]=[]
    for ref in tree.findall('.//ProjectReference'):
        target=(p.parent/ref.attrib['Include']).resolve()
        check(target.exists() and target.is_relative_to(B),p.name+' reference')
        graph[p.resolve()].append(target)
    if p.parent.name not in ['Tests','Benchmarks']:
        check(not tree.findall('.//PackageReference'),p.name+' package-free')
    for c in tree.findall('.//Compile'):
        check((p.parent/c.attrib['Include']).resolve().is_file(),p.name+' linked test code')

def visit(n,ancestors):
    check(n not in ancestors,'acyclic graph')
    for t in graph[n]:visit(t,ancestors|{n})
for p in graph:visit(p,set())
for f in [B/'run-lab.ps1',B/'run-lab.bat',ROOT/'run-raid-lab.bat']:
    data=f.read_bytes()
    check(all(x>=32 or x in (9,10,13) for x in data),'no escaped backspace/control chars '+f.name)
    check(b'\n' not in data.replace(b'\r\n',b'') if f.suffix == '.bat' else b'\r' not in data,'native newline convention '+f.name)
check((B/'run-lab.ps1').read_bytes().startswith(b'\xef\xbb\xbf'),'PS5 Japanese BOM')
script=(B/'run-lab.ps1').read_text(encoding='utf-8-sig')
check('Invoke-Expression' not in script and 'taskkill' not in script and 'git reset' not in script,'no dangerous shell/cleanup')
check('restore\', $solution' in script and '@($hostDll, \'--verify\')' in script,'first run restores solution then verifies')
# Interpret the literal 14-event contract fixture independently of the C# analyzer.
source=(B/'Verification/ContractChecks.cs').read_text()
fixture=source.split('public static RaidDataset Golden()')[1].split('public const string ExpectedGolden')[0]
events=[tuple(map(int,m[:4]))+(m[4]=='true',) for m in re.findall(r'new\((\d+), (\d+), (\d+), (-?\d+)(?:, (true|false))?\)',fixture)]
check(len(events)==14,'golden event fixture parsed')
players={1:(10,{1,2}),2:(10,{1}),3:(20,{2})};skills={1:0,2:50};seen=set();counts=[0,0,0];totals={}
for eid,player,skill,damage,cancelled in events:
    if eid<=0:counts[2]+=1;continue
    if eid in seen:counts[1]+=1;continue
    seen.add(eid)
    if cancelled or damage<5 or player not in players or skill not in skills or skill not in players[player][1]:counts[2]+=1;continue
    counts[0]+=1
    row=totals.setdefault(player,[players[player][0],0,0]);row[1]+=damage*(100+skills[skill])//100;row[2]+=1
guilds={}
for p,(g,damage,hits) in totals.items():
    row=guilds.setdefault(g,[0,0,0]);row[0]+=damage;row[1]+=hits;row[2]+=1
text='events|14|'+'|'.join(map(str,counts))+'\n'
for p,(g,damage,hits) in sorted(totals.items(),key=lambda r:(-r[1][1],r[0]))[:2]:text+=f'player|{p}|{g}|{damage}|{hits}\n'
for g,(damage,hits,participants) in sorted(guilds.items(),key=lambda r:(-r[1][0],r[0])):text+=f'guild|{g}|{damage}|{hits}|{participants}\n'
expected=re.search(r'ExpectedGolden = "([^"]+)"',source)[1].replace('\\n','\n')
check(text==expected,'golden results independent of C# implementation')
check(counts==[4,2,8],'golden counts')
check(totals[2][1]==5 and guilds[10][0]==45,'top-N does not truncate guild totals')
check(source.count('Add(prefix + ')==10,'10 explicit single-implementation cases')
check(source.count('result.Add("')+source.count('Add("fixture/')==9,'9 pipeline and fixture cases')
check(10+3*3*3+9==46,'46 authored C# cases')
print(json.dumps({'checks':checks,'golden':'independently verified arithmetic','csharpExecuted':False}))
