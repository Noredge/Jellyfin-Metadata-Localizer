"""Synthetic unified backup fault checks. Does not access Jellyfin or a provider."""
from pathlib import Path
import hashlib
import importlib.util
import json
import os
import sqlite3
import sys
import zipfile

spec=importlib.util.spec_from_file_location('backup',Path(__file__).with_name('Localizer-DataBackup.py'))
b=importlib.util.module_from_spec(spec);spec.loader.exec_module(b)
root=Path(sys.argv[1]).absolute();root.mkdir(parents=True,exist_ok=False);source=root/'store';source.mkdir()
schema=(Path(__file__).resolve().parents[1]/'src/Localizer.Unified/Schema.sql').read_text(encoding='utf-8-sig')
with sqlite3.connect(source/'metadata-localizer.db') as db:
    db.executescript(schema)
    db.execute('insert into runtime_state values(?,?)',('schema_definition',hashlib.sha256(schema.encode()).hexdigest()))
(source/'credentials/compatible').mkdir(parents=True)
(source/'credentials/compatible/synthetic.json').write_text('{"protected":"SYNTHETIC-CIPHERTEXT"}')
checks=[]
def check(name,condition):
    assert condition,name
    checks.append({'name':name,'passed':True});print('PASS '+name)
def rejects(fn):
    try:fn()
    except (ValueError,FileExistsError,sqlite3.DatabaseError,KeyError):return True
    return False
archive=root/'backup.zip'
check('backup requires stopped-server acknowledgement',rejects(lambda:b.backup(source,archive)))
check('archive cannot be inside data directory',rejects(lambda:b.backup(source,source/'backup.zip',server_stopped=True)))
original=b.inventory(source);result=b.backup(source,archive,server_stopped=True)
check('backup leaves every source byte unchanged',b.inventory(source)==original and result['files']==2)
check('existing archive is never overwritten',rejects(lambda:b.backup(source,archive,server_stopped=True)))
check('restore requires stopped-server acknowledgement',rejects(lambda:b.restore(archive,root/'restored')))
b.restore(archive,root/'restored',server_stopped=True)
check('restored DB and encrypted credential bytes match',b.inventory(root/'restored')==original)
limit=b.MAX_BYTES
try:
    b.MAX_BYTES=sum(x['size'] for x in original.values())
    boundary=root/'boundary.zip';b.backup(source,boundary,server_stopped=True)
    b.restore(boundary,root/'boundary-restored',server_stopped=True)
    check('data at the size limit remains restorable with its manifest',b.inventory(root/'boundary-restored')==original)
finally:b.MAX_BYTES=limit
check('restore cannot overwrite installed or restored data',rejects(lambda:b.restore(archive,root/'restored',server_stopped=True)))
for index,name in enumerate(['../escape','/absolute','C:/escape','folder\\escape','CON','bad.','bad ','bad:stream']):
    bad=root/f'unsafe-{index}.zip'
    with zipfile.ZipFile(bad,'w') as z:z.writestr(name,'synthetic')
    check('unsafe archive path rejected '+str(index),rejects(lambda:b.restore(bad,root/f'unsafe-target-{index}',server_stopped=True)))
with zipfile.ZipFile(archive) as z:entries={x.filename:z.read(x) for x in z.infolist()}
bad=root/'corrupt.zip'
with zipfile.ZipFile(bad,'w') as z:
    for name,data in entries.items():z.writestr(name,b'corrupt' if name.endswith('.db') else data)
check('corrupt payload fails before creating destination',rejects(lambda:b.restore(bad,root/'corrupt-target',server_stopped=True)) and not (root/'corrupt-target').exists())
badroot=root/'other';badroot.mkdir()
with sqlite3.connect(badroot/'metadata-localizer.db') as db:db.execute('create table unrelated(value)')
check('unrelated database is rejected',rejects(lambda:b.backup(badroot,root/'other.zip',server_stopped=True)))
if os.name=='nt':
    import ctypes
    from ctypes import wintypes
    lock=source/'metadata-localizer.worker.lock';lock.touch()
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,wintypes.LPVOID,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE];kernel.CreateFileW.restype=wintypes.HANDLE
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=kernel.CreateFileW(str(lock),0x80000000,0,None,3,0,None)
    assert handle!=wintypes.HANDLE(-1).value
    try:check('active worker lock prevents an acknowledged backup',rejects(lambda:b.backup(source,root/'active.zip',server_stopped=True)))
    finally:kernel.CloseHandle(handle)
check('temporary restore directories are not left behind',not list(root.glob('.localizer-restore-*')))
Path(sys.argv[2]).write_text(json.dumps({'status':'passed','checks':checks,'realModelRequests':0},indent=2))
