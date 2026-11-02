"""Synthetic offline adoption guards. No host or provider is contacted."""
from pathlib import Path
from contextlib import closing
import hashlib
import importlib.util
import json
import sqlite3
import sys
import uuid

spec = importlib.util.spec_from_file_location('adoption', Path(__file__).with_name('Localizer-AdoptData.py'))
a = importlib.util.module_from_spec(spec)
spec.loader.exec_module(a)
root = Path(sys.argv[1]).absolute()
root.mkdir(parents=True, exist_ok=False)
schema = (Path(__file__).resolve().parents[1] / 'src/Localizer.Unified/Schema.sql').read_text(encoding='utf-8-sig')
server = uuid.uuid4().hex
checks = []


def check(name, value):
    assert value, name
    checks.append({'name': name, 'passed': True})
    print('PASS ' + name)


def rejects(fn):
    try:
        fn()
    except (ValueError, FileExistsError, sqlite3.DatabaseError, KeyError):
        return True
    return False


def fixture(name, state='completed', request_state=None):
    source = root / name / 'metadata-localizer'
    source.mkdir(parents=True)
    with closing(sqlite3.connect(source / 'metadata-localizer.db')) as db:
        db.executescript(schema)
        db.execute('pragma foreign_keys=on')
        db.execute('insert into runtime_state values(?,?)', ('schema_definition', hashlib.sha256(schema.encode()).hexdigest()))
        db.execute("insert into media_items(id,server_id,library_id,item_id,media_path) values('media',?,'library','item','C:/fixture/movie.mp4')", (server,))
        db.execute("insert into source_versions(id,kind,owner_id,content,content_hash,created_utc) values('source','Name','media','A mountain journey','hash','2026-01-01')")
        db.execute("insert into work_items(id,submission_key,target_kind,media_id,field,source_id,source_revision,control_revision,language,target_epoch,state,step,created_utc,updated_utc) values('work','submission','media','media','Name','source',1,0,'en',0,?,'verify','2026-01-01','2026-01-01')", (state,))
        if request_state:
            db.execute("insert into generation_inputs(id,source_id,language,prompt_version,fingerprint,created_utc) values('input','source','en','fixture','fp','2026-01-01')")
            db.execute("update work_items set input_id='input'")
            db.execute("insert into connection_versions(id,revision,endpoint,model,timeout_ms,requests_per_minute,created_utc) values('connection',1,'https://example.org/v1','fixture',1000,60,'2026-01-01')")
            db.execute("insert into request_attempts(id,work_id,sequence,input_id,connection_id,state,started_utc) values('request','work',1,'input','connection',?,'2026-01-01')", (request_state,))
        db.commit()
    (source / 'credentials/compatible').mkdir(parents=True)
    (source / 'credentials/compatible/fixture.json').write_text('{"protected":"SYNTHETIC-CIPHERTEXT"}')
    return source, source.parent / 'metadata-localizer-v2', root / (name + '.zip')


check('adoption schema hash matches the product schema', hashlib.sha256(schema.encode()).hexdigest() == a.SCHEMA_HASH)
source, destination, archive = fixture('idle')
before = a.backup.inventory(source)
check('adoption requires stopped-server acknowledgement', rejects(lambda: a.adopt(source, destination, archive, server_id=server)))
plan = a.adopt(source, destination, archive, server_id=server, server_stopped=True)
check('inspection creates neither archive nor destination', plan['passed'] and not plan['applied'] and not archive.exists() and not destination.exists())
check('inspection preserves every source byte', a.backup.inventory(source) == before)
check('different Jellyfin server is rejected', rejects(lambda: a.adopt(source, destination, archive, server_id=uuid.uuid4().hex, server_stopped=True)))
check('arbitrary destination cannot be selected', rejects(lambda: a.adopt(source, destination.parent / 'another', archive, server_id=server, server_stopped=True)))
check('archive cannot be placed in source', rejects(lambda: a.adopt(source, destination, source / 'snapshot.zip', server_id=server, server_stopped=True)))
rule = root / 'fixture-rules.json'
rule.write_text('{"format":1,"titleRules":{"en":{"version":"fixture","instructions":"Natural translation.","rules":[]}}}')
result = a.adopt(source, destination, archive, server_id=server, rules=rule, server_stopped=True, apply=True)
check('apply backs up and copies the whole idle store', result['applied'] and archive.exists() and result['counts']['media_items'] == 1)
after = a.backup.inventory(destination)
check('all original DB credential and history bytes survive adoption', all(after[k] == v for k, v in before.items()) and a.backup.inventory(source) == before)
check('local rule pack is copied without changing its bytes', (destination / 'local-rules.json').read_bytes() == rule.read_bytes())
check('destination is never overwritten or merged', rejects(lambda: a.adopt(source, destination, root / 'repeat.zip', server_id=server, server_stopped=True, apply=True)))
with closing(sqlite3.connect(destination / 'metadata-localizer.db')) as db:
    check('completed task states are preserved without replay', db.execute("select state from work_items").fetchall() == [('completed',)] and db.execute('select count(*) from request_attempts').fetchone()[0] == 0)
for state in ('queued', 'running', 'waiting', 'paused'):
    s, target, z = fixture(state, state)
    check('unfinished ' + state + ' work blocks adoption', rejects(lambda: a.adopt(s, target, z, server_id=server, server_stopped=True, apply=True)) and not target.exists() and not z.exists())
for state in ('prepared', 'sending', 'unknown'):
    s, target, z = fixture(state, request_state=state)
    check('unresolved ' + state + ' request blocks adoption', rejects(lambda: a.adopt(s, target, z, server_id=server, server_stopped=True, apply=True)) and not target.exists())
s, target, z = fixture('existing-rules')
(s / 'local-rules.json').write_text('{"format":1}')
check('existing personal rule pack is never replaced', rejects(lambda: a.adopt(s, target, z, server_id=server, rules=rule, server_stopped=True, apply=True)))
for state in ('pending', 'called'):
    s, target, z = fixture('display-' + state)
    with closing(sqlite3.connect(s / 'metadata-localizer.db')) as db:
        db.execute("insert into display_intents(id,media_id,field,revision,mode,source_id,source_revision,control_revision,target_epoch,language,expected_value,state,updated_utc) values('intent','media','Name',1,'translated','source',1,0,0,'en','A mountain journey',?,'2026-01-01')", ('pending' if state == 'pending' else 'applied',))
        if state == 'called':
            db.execute("insert into display_writes(id,intent_id,intent_revision,action,state,source_revision,control_revision,target_epoch,before_value,before_hash,target_value,target_hash,created_utc) values('write','intent',1,'apply','called',1,0,0,'old','hash','new','hash','2026-01-01')")
        db.commit()
    check('uncertain display ' + state + ' prevents takeover', rejects(lambda: a.adopt(s, target, z, server_id=server, server_stopped=True, apply=True)) and not target.exists())
s, target, z = fixture('old-schema')
with closing(sqlite3.connect(s / 'metadata-localizer.db')) as db:
    db.execute("update runtime_state set value='unsupported' where key='schema_definition'")
    db.commit()
check('older or unknown schema is not silently imported', rejects(lambda: a.adopt(s, target, z, server_id=server, server_stopped=True, apply=True)) and not z.exists())
s, target, z = fixture('bad-rule')
rule.write_text('{"format":1,"apiKey":"SYNTHETIC-NOT-A-KEY"}')
check('credentials and unknown fields are excluded from rule files', rejects(lambda: a.adopt(s, target, z, server_id=server, rules=rule, server_stopped=True, apply=True)) and not z.exists())
s, target, z = fixture('wal')
with closing(sqlite3.connect(s / 'metadata-localizer.db')) as writer:
    writer.execute('pragma journal_mode=wal')
    writer.execute('pragma wal_autocheckpoint=0')
    writer.execute("insert into media_items(id,server_id,library_id,item_id,media_path) values('media2',?,'library','item2','C:/fixture/second.mp4')", (server,))
    writer.commit()
    wal_before = a.backup.inventory(s)
    report = a.adopt(s, target, z, server_id=server, server_stopped=True, apply=True)
    wal_after = a.backup.inventory(target)
    check('committed WAL rows and complete DB sidecars survive adoption', report['counts']['media_items'] == 2 and wal_after == wal_before and a.backup.inventory(s) == wal_before)
check('temporary adoption directories are not left behind', not list(root.rglob('.localizer-adopt-*')))
Path(sys.argv[2]).write_text(json.dumps({'status': 'passed', 'checks': checks, 'realModelRequests': 0}, indent=2))
