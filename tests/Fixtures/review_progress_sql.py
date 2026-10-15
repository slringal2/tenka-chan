"""Execute SQL statements extracted from ReviewProgressStore.cs against local SQLite.
This validates SQL/schema behavior only. It does not execute .NET or the C# guards.
"""
from pathlib import Path
import json,re,sqlite3,tempfile

def main():
    code=(Path(__file__).resolve().parents[2]/'src/TenkaChan.Mcp/Tracking/ReviewProgressStore.cs').read_text()
    blocks=re.findall(r'cmd.CommandText\s*=\s*"""(.*?)"""',code,re.S)
    assert len(blocks)==2
    schema,latest=blocks
    strings=re.findall(r'cmd.CommandText\s*=\s*"([^"\n]+)";',code)
    current=next(s for s in strings if s.startswith('SELECT COALESCE(MAX') and 'WHERE target_id' in s)
    revision=next(s for s in strings if s.startswith('SELECT COALESCE(MAX') and 'WHERE' not in s)
    history=next(s for s in strings if 'ORDER BY revision DESC LIMIT' in s)
    read=next(s for s in strings if s.startswith('SELECT revision,payload,source'))
    insert=next(s for s in strings if s.startswith('INSERT')).split(';')[0]
    checks=0
    with tempfile.TemporaryDirectory() as folder:
        file=Path(folder)/'progress.sqlite'
        db=sqlite3.connect(file);db.executescript(schema)
        assert db.execute(current,dict(id='a')).fetchone()[0]==0;checks+=1
        for target,status,source in [('a','checked','old'),('a','optimized','new'),('b','checked','other')]:
            db.execute('BEGIN IMMEDIATE')
            db.execute(insert,dict(id=target,payload=json.dumps(dict(targetId=target,status=status)),source=source,size=len(source)))
            db.commit()
        db.close();db=sqlite3.connect(file)
        rows=db.execute(latest).fetchall();assert len(rows)==2;checks+=1
        assert json.loads(next(r[1] for r in rows if json.loads(r[1])['targetId']=='a'))['status']=='optimized';checks+=1
        entries=db.execute(history,dict(id='a',limit=50,offset=0)).fetchall();assert len(entries)==2;checks+=1
        first=entries[-1][0];last=entries[0][0]
        assert db.execute(read,dict(id='a',revision=first)).fetchone()[2]=='old';checks+=1
        assert db.execute(read,dict(id='a',revision=last)).fetchone()[2]=='new';checks+=1
        assert db.execute(read,dict(id='b',revision=first)).fetchone() is None;checks+=1
        assert db.execute(current,dict(id='a')).fetchone()[0]==last;checks+=1
        assert len(db.execute(history,dict(id='a',limit=1,offset=1)).fetchall())==1;checks+=1
        assert db.execute('SELECT SUM(source_chars) FROM review_checkpoints').fetchone()[0]==11;checks+=1
        # High-water mark and payload are read in the same SQLite snapshot. Exercise
        # the actual extracted queries using two connections, not a Python cache clone.
        assert db.execute(revision).fetchone()[0]==3;checks+=1
        db.execute('PRAGMA journal_mode=WAL')
        other=sqlite3.connect(file)
        db.execute('BEGIN')
        before=db.execute(revision).fetchone()[0]
        other.execute(insert,dict(id='a',payload=json.dumps(dict(targetId='a',status='checked')),source='newer',size=5))
        other.commit()
        assert db.execute(revision).fetchone()[0]==before;checks+=1
        assert max(r[0] for r in db.execute(latest))==before;checks+=1
        db.commit()
        assert db.execute(revision).fetchone()[0]==before+1;checks+=1
        assert max(r[0] for r in db.execute(latest))==before+1;checks+=1
        assert db.execute(current,dict(id='b')).fetchone()[0]==3;checks+=1
        other.close();db.close()
    print(json.dumps(dict(checks=checks,sqliteSchema='extracted actual SQL',csharpExecuted=False)))
if __name__=='__main__':main()
