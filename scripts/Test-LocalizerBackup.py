"""Fault checks for the backup tool shipped inside the exact local preview package."""
import importlib.util
import json
from pathlib import Path
import sqlite3
import shutil
import sys
import uuid
import zipfile

tool_path, root, report = map(Path, sys.argv[1:4])
root = root.absolute() / uuid.uuid4().hex
root.mkdir(parents=True)
spec = importlib.util.spec_from_file_location('backup_tool', tool_path)
tool = importlib.util.module_from_spec(spec); spec.loader.exec_module(tool)
source = root / 'store'; source.mkdir()
db = sqlite3.connect(source / 'candidates.db')
db.executescript('PRAGMA application_id=0x4A4D4C43; PRAGMA user_version=4; CREATE TABLE fixture(value TEXT); INSERT INTO fixture VALUES("synthetic");')
db.close()
(source / 'bindings').mkdir(); (source / 'bindings/source.json').write_text('{"synthetic":true}', encoding='utf-8')
(source / 'credentials').mkdir(); (source / 'credentials/groq-api-key.dpapi').write_text('SYNTHETIC-CIPHERTEXT-ONLY')
checks = []
def check(name, action):
    action(); checks.append({'name':name,'passed':True}); print('PASS',name)
def reject(action, expected=(ValueError,FileExistsError)):
    try: action()
    except expected: return
    raise AssertionError('Expected rejection.')
def expect(value):
    if not value: raise AssertionError('Assertion failed.')
archive = root / 'backup.zip'
check('online_backup_and_restore_require_explicit_stop_ack', lambda: (
    reject(lambda:tool.backup(source,archive)), reject(lambda:tool.restore(archive,root/'new'))))
tool.backup(source,archive,server_stopped=True)
check('backup_archive_never_overwrites_existing_file', lambda:reject(lambda:tool.backup(source,archive,server_stopped=True)))
check('archive_inside_source_is_rejected', lambda:reject(lambda:tool.backup(source,source/'nested.zip',server_stopped=True)))
tool.restore(archive,root/'restored',server_stopped=True)
check('whole_directory_hashes_match_after_restore', lambda:expect(tool.files(source)==tool.files(root/'restored')))
check('restore_never_overwrites_existing_directory', lambda:reject(lambda:tool.restore(archive,source,server_stopped=True)))
with zipfile.ZipFile(archive) as z:
    content = {x.filename:z.read(x) for x in z.infolist()}
bad = root/'corrupt.zip'
with zipfile.ZipFile(bad,'x') as z:
    for name,data in content.items(): z.writestr(name,b'changed' if name=='bindings/source.json' else data)
check('tampered_payload_rejected_before_destination_creation', lambda:(
    reject(lambda:tool.restore(bad,root/'bad-restore',server_stopped=True)), expect(not (root/'bad-restore').exists())))
unsafe = root/'traversal.zip'
with zipfile.ZipFile(unsafe,'x') as z: z.writestr('../outside.txt','bad')
check('traversal_rejected_without_writing_outside_destination', lambda:(
    reject(lambda:tool.restore(unsafe,root/'unsafe-restore',server_stopped=True)), expect(not (root/'outside.txt').exists())))
check('windows_aliases_and_absolute_paths_rejected', lambda:[reject(lambda:tool.safe_name(x)) for x in ['/root','C:/file','folder\\file','CON.txt','word.','a//b']])
duplicate = root/'duplicate.zip'
with zipfile.ZipFile(duplicate,'x') as z:
    z.writestr('WORD','1'); z.writestr('word','2')
check('case_colliding_archive_entries_rejected', lambda:reject(lambda:tool.restore(duplicate,root/'case-restore',server_stopped=True)))
bad_source = root/'unrelated'; bad_source.mkdir()
sqlite3.connect(bad_source/'candidates.db').close()
check('unrelated_database_rejected', lambda:reject(lambda:tool.backup(bad_source,root/'unrelated.zip',server_stopped=True)))
deep = tool.native_path(source / ('nested-' + 'x' * 160))
deep.mkdir(); (deep / ('a' * 64 + '.json')).write_text('synthetic long path')
long_archive = root/'long.zip'
tool.backup(source,long_archive,server_stopped=True)
tool.restore(long_archive,root/'long-path-restored',server_stopped=True)
check('windows_long_hash_paths_survive_backup_and_restore_staging', lambda:expect(tool.files(source)==tool.files(root/'long-path-restored')))
wal_live = root/'wal-live'; wal_live.mkdir()
connection = sqlite3.connect(wal_live/'candidates.db')
connection.executescript('PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA application_id=0x4A4D4C43; PRAGMA user_version=4; CREATE TABLE fixture(value TEXT); INSERT INTO fixture VALUES("committed-in-wal");')
connection.commit()
wal_source = root/'wal-frozen'; wal_source.mkdir()
for path in wal_live.iterdir(): shutil.copyfile(path,wal_source/path.name)
connection.close()
wal_before = tool.files(wal_source)
expect(wal_before['candidates.db-wal']['size'] > 0)
tool.backup(wal_source,root/'wal.zip',server_stopped=True)
tool.restore(root/'wal.zip',root/'wal-restored',server_stopped=True)
check('validation_preserves_source_bytes_and_uncheckpointed_wal', lambda:expect(wal_before == tool.files(wal_source) == tool.files(root/'wal-restored')))

def campaign_database(path, *, application=0x4A4D4C50, version=1, orphan=False, wal=False):
    connection = sqlite3.connect(path)
    if wal:
        connection.executescript('PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;')
    connection.executescript(f'''
        PRAGMA application_id={application}; PRAGMA user_version={version};
        CREATE TABLE campaigns(id TEXT PRIMARY KEY, revision TEXT NOT NULL, header_json TEXT NOT NULL, item_count INTEGER NOT NULL CHECK(item_count>=0));
        CREATE TABLE campaign_items(campaign_id TEXT NOT NULL REFERENCES campaigns(id), ordinal INTEGER NOT NULL CHECK(ordinal>=0), payload_json TEXT NOT NULL, PRIMARY KEY(campaign_id,ordinal));
        INSERT INTO campaigns VALUES('synthetic-campaign','revision-1','{{"State":"Completed"}}',1);
    ''')
    connection.execute('INSERT INTO campaign_items VALUES(?,0,?)', ('missing-parent' if orphan else 'synthetic-campaign', '{"State":"Applied"}'))
    connection.commit()
    return connection

campaign_source = root/'campaign-store'
shutil.copytree(root/'restored', campaign_source)
(campaign_source/'campaigns').mkdir()
(campaign_source/'campaigns/legacy.json').write_text('{"retained":"legacy journal"}', encoding='utf-8')
campaign_database(campaign_source/'campaigns.db').close()
campaign_before = tool.files(campaign_source)
tool.backup(campaign_source, root/'campaign.zip', server_stopped=True)
tool.restore(root/'campaign.zip', root/'campaign-restored', server_stopped=True)
check('campaign_sqlite_and_legacy_json_restore_with_identical_bytes', lambda:expect(
    campaign_before == tool.files(campaign_source) == tool.files(root/'campaign-restored')))

campaign_wal_live = root/'campaign-wal-live'; campaign_wal_live.mkdir()
campaign_connection = campaign_database(campaign_wal_live/'campaigns.db', wal=True)
campaign_wal_source = root/'campaign-wal-frozen'
shutil.copytree(root/'restored', campaign_wal_source)
for path in campaign_wal_live.iterdir(): shutil.copyfile(path, campaign_wal_source/path.name)
campaign_connection.close()
campaign_wal_before = tool.files(campaign_wal_source)
expect(campaign_wal_before['campaigns.db-wal']['size'] > 0 and 'campaigns.db-shm' in campaign_wal_before)
tool.backup(campaign_wal_source, root/'campaign-wal.zip', server_stopped=True)
tool.restore(root/'campaign-wal.zip', root/'campaign-wal-restored', server_stopped=True)
check('campaign_validation_uses_wal_copy_without_changing_source_or_restore', lambda:expect(
    campaign_wal_before == tool.files(campaign_wal_source) == tool.files(root/'campaign-wal-restored')))

def invalid_campaign_check(kind):
    damaged = root/('campaign-' + kind); damaged.mkdir()
    shutil.copyfile(source/'candidates.db', damaged/'candidates.db')
    if kind == 'corrupt':
        (damaged/'campaigns.db').write_bytes(b'Not a SQLite database')
    elif kind == 'sidecar-without-database':
        (damaged/'campaigns.db-wal').write_bytes(b'orphan sidecar')
    else:
        campaign_database(damaged/'campaigns.db', application=123 if kind == 'foreign' else 0x4A4D4C50,
                          version=99 if kind == 'unsupported-schema' else 1, orphan=kind == 'foreign-key').close()
    before = tool.files(damaged)
    backup_path = root/('rejected-' + kind + '.zip')
    reject(lambda:tool.backup(damaged, backup_path, server_stopped=True))
    expect(not backup_path.exists() and tool.files(damaged) == before)
    # Build a hash-consistent archive so restore must reject the database itself, not just a modified payload hash.
    bad_archive = root/('campaign-' + kind + '.zip')
    with zipfile.ZipFile(bad_archive, 'x') as bundle:
        for name in before: bundle.writestr(name, (damaged/name).read_bytes())
        bundle.writestr('backup-manifest.json', json.dumps({'format':1, 'schema':4, 'files':before}))
    destination = root/('campaign-' + kind + '-restore')
    reject(lambda:tool.restore(bad_archive, destination, server_stopped=True))
    expect(not destination.exists() and tool.files(damaged) == before)

for kind in ['corrupt', 'foreign', 'unsupported-schema', 'foreign-key', 'sidecar-without-database']:
    check('campaign_' + kind.replace('-', '_') + '_rejected_for_backup_and_restore', lambda kind=kind:invalid_campaign_check(kind))

modern = root/'modern'; shutil.copytree(root/'restored', modern)
with sqlite3.connect(modern/'candidates.db') as db: db.execute('PRAGMA user_version=5')
for name, app in [('overviews.db',1246571599),('genre-translations.db',1246571601),('display-preferences.db',1246571602)]:
    with sqlite3.connect(modern/name) as db:
        db.executescript(f'PRAGMA application_id={app}; PRAGMA user_version=1; CREATE TABLE fixture(value TEXT);')
(modern/'original-display-requests').mkdir()
(modern/'original-display-requests/request.json').write_text('{"State":"Prepared"}')
tool.backup(modern,root/'modern.zip',server_stopped=True)
tool.restore(root/'modern.zip',root/'modern-restored',server_stopped=True)
check('schema5_and_all_new_stores_and_receipts_roundtrip',lambda:expect(tool.files(modern)==tool.files(root/'modern-restored')))
for name in ['overviews.db','genre-translations.db','display-preferences.db']:
    with sqlite3.connect(modern/name) as db: db.execute('PRAGMA user_version=99')
    check(name+'_unsupported_schema_rejected',lambda:reject(lambda:tool.backup(modern,root/(name+'.zip'),server_stopped=True)))
    with sqlite3.connect(modern/name) as db: db.execute('PRAGMA user_version=1')
report.write_text(json.dumps({'stage':'M38 offline backup compatibility','total':len(checks),'passed':len(checks),'failed':0,'checks':checks},indent=2),encoding='utf-8')
