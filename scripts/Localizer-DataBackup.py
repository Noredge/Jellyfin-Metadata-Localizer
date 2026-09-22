"""Offline, local-only backup and restore. Requires an explicitly stopped Jellyfin.

Restore always creates a NEW directory; it never overwrites an installed store.
Python 3.12+; no third-party modules. Backups contain private metadata and DPAPI ciphertext.
"""
import argparse
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import sqlite3
import stat
import shutil
import tempfile
import uuid
import zipfile

MAX_BYTES = 1024 * 1024 * 1024


def native_path(path):
    value = os.path.abspath(path)
    if os.name == 'nt' and not value.startswith('\\\\?\\'):
        value = '\\\\?\\UNC\\' + value[2:] if value.startswith('\\\\') else '\\\\?\\' + value
    return Path(value)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_name(name):
    parts = PurePosixPath(name).parts
    if (not parts or name != '/'.join(parts) or name.startswith('/') or '\\' in name or ':' in name
            or any(p in ('.', '..') or p.endswith(('.', ' ')) or any(ord(c) < 32 for c in p)
                   or p.split('.')[0].upper() in {'CON','PRN','AUX','NUL', *['COM'+str(i) for i in range(1,10)], *['LPT'+str(i) for i in range(1,10)]} for p in parts)):
        raise ValueError('Unsafe archive path.')
    return name


def check_database(directory):
    # Even mode=ro can create WAL/SHM sidecars. Validate a private copy so source and restore bytes stay untouched.
    directory = native_path(directory)
    with tempfile.TemporaryDirectory(prefix='jml-db-check-') as temporary:
        scratch = Path(temporary).resolve()
        if scratch.parent != Path(tempfile.gettempdir()).resolve() or not scratch.name.startswith('jml-db-check-'):
            raise ValueError('Unexpected validation temporary directory.')
        databases = [('candidates.db', 0x4A4D4C43, (4, 5))]
        for name, app in [('campaigns.db', 0x4A4D4C50), ('overviews.db', 1246571599),
                          ('genre-translations.db', 1246571601), ('display-preferences.db', 1246571602)]:
            if (directory / name).exists():
                databases.append((name, app, (1,)))
            elif any((directory / (name + suffix)).exists() for suffix in ('-wal', '-shm', '-journal')):
                raise ValueError(f'Database sidecars require {name}.')
        for name, application_id, version in databases:
            for suffix in ('', '-wal', '-shm', '-journal'):
                source = directory / (name + suffix)
                if source.exists():
                    shutil.copyfile(source, scratch / (name + suffix))
            path = scratch / name
            try:
                with closing(sqlite3.connect(path.as_uri() + '?mode=ro', uri=True)) as db:
                    if db.execute('PRAGMA application_id').fetchone()[0] != application_id or db.execute('PRAGMA user_version').fetchone()[0] not in version:
                        raise ValueError(f'Expected Localizer {name} schema {version}.')
                    if db.execute('PRAGMA integrity_check').fetchall() != [('ok',)] or db.execute('PRAGMA foreign_key_check').fetchall():
                        raise ValueError(f'Localizer {name} integrity check failed.')
            except sqlite3.DatabaseError:
                raise ValueError(f'Invalid Localizer {name}.') from None


def files(directory):
    directory = native_path(directory)
    result = {}
    for path in sorted(directory.rglob('*')):
        if path.is_symlink() or path.is_junction() or path.stat().st_file_attributes & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError('Reparse points are not supported.')
        if path.is_file():
            name = safe_name(path.relative_to(directory).as_posix())
            if name == 'backup-manifest.json':
                raise ValueError('Reserved backup manifest name.')
            result[name] = {'sha256':digest(path.read_bytes()), 'size':path.stat().st_size}
    if len(result) > 10000 or sum(x['size'] for x in result.values()) > MAX_BYTES:
        raise ValueError('Backup exceeds bounded tool limits.')
    return result


def backup(source, archive, *, server_stopped=False):
    if not server_stopped:
        raise ValueError('Stop the server first; online backup is unsupported.')
    source, archive = native_path(source), native_path(archive)
    if source.is_symlink() or source.is_junction() or not source.is_dir() or archive.is_relative_to(source):
        raise ValueError('Use a real source directory and an archive outside it.')
    if archive.exists():
        raise FileExistsError('Backup archive already exists.')
    check_database(source)
    snapshot = files(source)
    archive.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive creation prevents replacing an earlier backup.
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as output:
        for name in snapshot:
            data = (source / name).read_bytes()
            if digest(data) != snapshot[name]['sha256']:
                raise ValueError('Source changed during backup; discard this incomplete archive.')
            output.writestr(name, data)
        if files(source) != snapshot:
            raise ValueError('Source changed during backup; discard this incomplete archive.')
        output.writestr('backup-manifest.json', json.dumps({'format':1,'schema':4,'files':snapshot}, sort_keys=True))
    return len(snapshot)


def restore(archive, destination, *, server_stopped=False):
    if not server_stopped:
        raise ValueError('Stop the server first; online restore is unsupported.')
    archive, destination = native_path(archive), native_path(destination)
    if destination.exists() or destination.is_symlink():
        raise FileExistsError('Restore requires a new destination; installed data is never overwritten.')
    with zipfile.ZipFile(archive) as bundle:
        infos = bundle.infolist(); names = [safe_name(x.filename) for x in infos]
        if len(names) > 10001 or len(set(n.casefold() for n in names)) != len(names) or sum(x.file_size for x in infos) > MAX_BYTES:
            raise ValueError('Duplicate paths or oversized archive.')
        if any(x.is_dir() or stat.S_ISLNK(x.external_attr >> 16) for x in infos):
            raise ValueError('Expected regular file entries.')
        manifest_info = bundle.getinfo('backup-manifest.json')
        if manifest_info.file_size > 4 * 1024 * 1024:
            raise ValueError('Oversized manifest.')
        manifest = json.loads(bundle.read(manifest_info))
        if manifest.get('format') != 1 or manifest.get('schema') != 4:
            raise ValueError('Unsupported backup format.')
        expected = manifest['files']
        if set(names) != set(expected) | {'backup-manifest.json'} or 'candidates.db' not in expected:
            raise ValueError('Backup content does not match manifest.')
        for name, item in expected.items():
            if bundle.getinfo(name).file_size != item['size'] or digest(bundle.read(name)) != item['sha256']:
                raise ValueError('Backup hash mismatch.')
        # Validate the archive before creating any output, then stage privately beside the destination.
        destination.parent.mkdir(parents=True, exist_ok=True)
        staging = destination.with_name(destination.name + '.restore-' + uuid.uuid4().hex)
        staging.mkdir()
        for name, item in expected.items():
            target = staging / name; target.parent.mkdir(parents=True, exist_ok=True)
            data = bundle.read(name)
            if digest(data) != item['sha256']:
                raise ValueError('Archive changed during restore; staging left for inspection.')
            with target.open('xb') as output:
                output.write(data)
        check_database(staging)
        if files(staging) != expected:
            raise ValueError('Restored files differ; staging left for inspection.')
        staging.rename(destination)
    return len(expected)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['backup','restore'])
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path)
    parser.add_argument('--server-stopped', action='store_true', help='Acknowledge that Jellyfin was stopped and no process is writing these files.')
    args = parser.parse_args()
    count = (backup if args.action == 'backup' else restore)(args.source, args.destination, server_stopped=args.server_stopped)
    print(f'{args.action}: verified {count} files; no media changes or network requests.')
