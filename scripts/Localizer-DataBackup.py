"""Offline backup/restore for Metadata Localizer 0.12 unified data. Never overwrites a destination."""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sqlite3
import stat
import tempfile
import uuid
import zipfile

MAX_BYTES = 1024 * 1024 * 1024
MAX_FILES = 10000


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def safe_name(name):
    parts = PurePosixPath(name).parts
    if not parts or name != '/'.join(parts) or name.startswith('/') or '\\' in name or ':' in name or any(
        p in ('.', '..') or p.endswith(('.', ' ')) or any(ord(c) < 32 for c in p)
        or p.split('.')[0].upper() in {'CON', 'PRN', 'AUX', 'NUL', *['COM'+str(i) for i in range(1, 10)], *['LPT'+str(i) for i in range(1, 10)]} for p in parts):
        raise ValueError('Unsafe archive path.')
    return name


def inventory(root):
    result = {}
    for p in sorted(root.rglob('*')):
        if p.is_symlink() or p.is_junction() or getattr(p.stat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError('Reparse points are not supported.')
        if p.is_file():
            name = safe_name(p.relative_to(root).as_posix())
            if name == 'backup-manifest.json':
                raise ValueError('Reserved manifest filename.')
            result[name] = {'size': p.stat().st_size, 'sha256': sha(p)}
    if len(result) > MAX_FILES or sum(x['size'] for x in result.values()) > MAX_BYTES:
        raise ValueError('Backup exceeds limits.')
    return result


def require_stopped(root, acknowledged):
    if not acknowledged:
        raise ValueError('Stop Jellyfin first and pass --server-stopped.')
    lock = root / 'metadata-localizer.worker.lock'
    if os.name == 'nt' and lock.exists():
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.CreateFileW(str(lock), 0x80000000, 0, None, 3, 0, None)
        if handle == wintypes.HANDLE(-1).value:
            raise ValueError('Worker lock is in use or inaccessible. Stop Jellyfin first.')
        kernel.CloseHandle(handle)


def check_database(root):
    # SQLite may create shared-memory sidecars even in read-only mode. Inspect a private copy.
    with tempfile.TemporaryDirectory(prefix='localizer-check-') as temp:
        scratch = Path(temp)
        for suffix in ('', '-wal', '-shm', '-journal'):
            source = root / ('metadata-localizer.db' + suffix)
            if source.exists():
                shutil.copyfile(source, scratch / source.name)
        with closing(sqlite3.connect((scratch / 'metadata-localizer.db').as_uri() + '?mode=ro', uri=True)) as db:
            if db.execute('pragma application_id').fetchone()[0] != 1246571857 or db.execute('pragma user_version').fetchone()[0] != 1:
                raise ValueError('Expected unified Metadata Localizer schema 1.')
            if db.execute('pragma integrity_check').fetchall() != [('ok',)] or db.execute('pragma foreign_key_check').fetchall():
                raise ValueError('Database integrity check failed.')
            if not db.execute("select value from runtime_state where key='schema_definition'").fetchone():
                raise ValueError('Missing schema definition.')


def backup(source, archive, *, server_stopped=False):
    if Path(source).is_symlink() or Path(source).is_junction():
        raise ValueError('Reparse source directories are not supported.')
    source, archive = Path(source).resolve(), Path(archive).absolute()
    require_stopped(source, server_stopped)
    if not source.is_dir() or source.is_symlink() or source.is_junction() or archive.is_relative_to(source):
        raise ValueError('Use a real data directory and an archive outside it.')
    if archive.exists():
        raise FileExistsError('Archive already exists.')
    before = inventory(source)
    check_database(source)
    archive.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as z:
        for name in before:
            z.write(source / name, name)
        if inventory(source) != before:
            raise ValueError('Source changed during backup.')
        z.writestr('backup-manifest.json', json.dumps({'format': 2, 'files': before}, sort_keys=True))
    return {'files': len(before), 'sha256': sha(archive)}


def restore(archive, destination, *, server_stopped=False):
    archive, destination = Path(archive).resolve(), Path(destination).absolute()
    require_stopped(destination, server_stopped)
    if destination.exists() or destination.is_symlink():
        raise FileExistsError('Restore requires a new directory.')
    if not destination.parent.is_dir():
        raise ValueError('Destination parent must exist.')
    with zipfile.ZipFile(archive) as z:
        infos = z.infolist()
        names = [safe_name(x.filename) for x in infos]
        if len(names) > MAX_FILES+1 or len({x.casefold() for x in names}) != len(names) or sum(x.file_size for x in infos if x.filename != 'backup-manifest.json') > MAX_BYTES:
            raise ValueError('Invalid archive inventory.')
        if any(x.is_dir() or stat.S_ISLNK(x.external_attr >> 16) for x in infos):
            raise ValueError('Regular file entries required.')
        entry = z.getinfo('backup-manifest.json')
        if entry.file_size > 4 * 1024 * 1024:
            raise ValueError('Oversized manifest.')
        manifest = json.loads(z.read(entry))
        if manifest.get('format') != 2 or set(names) != set(manifest['files']) | {'backup-manifest.json'} or 'metadata-localizer.db' not in manifest['files']:
            raise ValueError('Unsupported archive.')
        for name, expected in manifest['files'].items():
            data = z.read(name)
            if len(data) != expected['size'] or hashlib.sha256(data).hexdigest() != expected['sha256']:
                raise ValueError('Archive digest mismatch.')
        pending = destination.parent / ('.localizer-restore-' + uuid.uuid4().hex)
        pending.mkdir()
        try:
            for name in manifest['files']:
                target = pending / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(z.read(name))
            check_database(pending)
            if inventory(pending) != manifest['files']:
                raise ValueError('Restored inventory mismatch.')
            pending.rename(destination)
        except BaseException:
            if pending.exists():
                assert pending.resolve().parent == destination.parent.resolve() and pending.name.startswith('.localizer-restore-')
                shutil.rmtree(pending)
            raise
    return {'files': len(manifest['files']), 'destination': str(destination)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['backup', 'restore'])
    parser.add_argument('--archive', required=True, type=Path)
    parser.add_argument('--source', type=Path)
    parser.add_argument('--destination', type=Path)
    parser.add_argument('--server-stopped', action='store_true')
    args = parser.parse_args()
    if args.action == 'backup' and args.source:
        result = backup(args.source, args.archive, server_stopped=args.server_stopped)
    elif args.action == 'restore' and args.destination:
        result = restore(args.archive, args.destination, server_stopped=args.server_stopped)
    else:
        parser.error('Supply --source for backup or --destination for restore.')
    print(json.dumps({'passed': True, **result}))
