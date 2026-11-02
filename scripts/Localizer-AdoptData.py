"""Explicit offline adoption of an idle schema-1 store into a NEW versioned directory."""
from pathlib import Path
from contextlib import closing
import argparse
import importlib.util
import json
import shutil
import sqlite3
import stat
import tempfile
import uuid

spec = importlib.util.spec_from_file_location('localizer_backup', Path(__file__).with_name('Localizer-DataBackup.py'))
backup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backup)
SCHEMA_HASH = backup.SCHEMA_HASH
MAX_RULE_BYTES = 1024 * 1024


def pairs(items):
    result = {}
    for key, value in items:
        if key.casefold() in {k.casefold() for k in result}:
            raise ValueError('Duplicate rule property.')
        result[key] = value
    return result


def rule_bytes(path):
    path = Path(path)
    if not path.is_file() or path.is_symlink() or getattr(path.stat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
        raise ValueError('Regular local rule file required.')
    with path.open('rb') as f:
        data = f.read(MAX_RULE_BYTES + 1)
    if len(data) > MAX_RULE_BYTES:
        raise ValueError('Local rule file exceeds 1 MiB.')
    doc = json.loads(data, object_pairs_hook=pairs, parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Invalid JSON constant.')))
    if not isinstance(doc, dict) or type(doc.get('format')) is not int or doc.get('format') != 1 or set(doc) - {'format', 'titleRules', 'genreTerms'}:
        raise ValueError('Expected local rule format 1 with camelCase field names.')
    titles = doc.get('titleRules')
    if titles is not None and (not isinstance(titles, dict) or set(titles) - {'zh-Hans', 'en'}):
        raise ValueError('Unsupported title languages.')
    terms = doc.get('genreTerms')
    if terms is not None and (not isinstance(terms, list) or len(terms) > 5000):
        raise ValueError('Invalid local genre inventory.')
    # Regex/content validation belongs to the plugin; it fails closed before opening an executor.
    return data


def inspect(source, server_id):
    backup.check_database(source)
    expected = uuid.UUID(server_id).hex
    with tempfile.TemporaryDirectory(prefix='localizer-adopt-check-') as temp:
        copy = Path(temp)
        for suffix in ('', '-wal', '-shm', '-journal'):
            p = source / ('metadata-localizer.db' + suffix)
            if p.exists():
                shutil.copyfile(p, copy / p.name)
        with closing(sqlite3.connect((copy / 'metadata-localizer.db').as_uri() + '?mode=ro', uri=True)) as db:
            if any(uuid.UUID(row[0]).hex != expected for row in db.execute('select distinct server_id from media_items')):
                raise ValueError('Media belongs to a different Jellyfin server.')
            unfinished = db.execute("select count(*) from work_items where state in ('queued','running','waiting','paused')").fetchone()[0]
            unresolved = db.execute("select count(*) from request_attempts where state in ('prepared','sending','unknown')").fetchone()[0]
            if unfinished or unresolved:
                raise ValueError('Resolve unfinished work and unknown requests before adoption; no tasks are reset or replayed.')
            if db.execute("select count(*) from display_intents where state='pending'").fetchone()[0] or db.execute("select count(*) from display_writes where state in ('prepared','called')").fetchone()[0]:
                raise ValueError('Resolve pending or uncertain display writes before adoption.')
            return {table: db.execute('select count(*) from ' + table).fetchone()[0]
                    for table in ('media_items', 'translation_versions', 'work_items', 'request_attempts', 'person_name_rules', 'genre_terms')}


def adopt(source, destination, archive, *, server_id, rules=None, server_stopped=False, apply=False):
    source, destination, archive = Path(source).absolute(), Path(destination).absolute(), Path(archive).absolute()
    if source.is_symlink() or source.is_junction():
        raise ValueError('Reparse source directories are not supported.')
    source = source.resolve()
    if destination.name != 'metadata-localizer-v2' or destination.parent.resolve() != source.parent.resolve() or source == destination:
        raise ValueError('Destination must be the new sibling metadata-localizer-v2 directory.')
    if destination.exists() or destination.is_symlink() or destination.is_junction():
        raise FileExistsError('Destination already exists; stores are never merged or overwritten.')
    if archive.exists() or archive.resolve().is_relative_to(source) or archive.resolve().is_relative_to(destination.resolve()):
        raise ValueError('Use a new backup archive outside both data directories.')
    backup.require_stopped(source, server_stopped)
    before = backup.inventory(source)
    counts = inspect(source, server_id)
    data = rule_bytes(rules) if rules is not None else None
    existing_rules = source / 'local-rules.json'
    if existing_rules.exists():
        existing = rule_bytes(existing_rules)
        if data is not None and data != existing:
            raise ValueError('Existing local rules would be replaced.')
        data = existing
    report = {'passed': True, 'applied': False, 'counts': counts, 'localRules': data is not None,
              'schemaMigrated': False, 'sourceRetained': True, 'tasksReplayed': 0, 'realModelRequests': 0}
    if not apply:
        if backup.inventory(source) != before:
            raise ValueError('Source changed during inspection.')
        return report
    snapshot = backup.backup(source, archive, server_stopped=server_stopped)
    pending = destination.parent / ('.localizer-adopt-' + uuid.uuid4().hex)
    try:
        backup.restore(archive, pending, server_stopped=server_stopped)
        if backup.inventory(pending) != before:
            raise ValueError('Restored bytes differ from source.')
        if data is not None and not (pending / 'local-rules.json').exists():
            (pending / 'local-rules.json').write_bytes(data)
        if inspect(pending, server_id) != counts or backup.inventory(source) != before:
            raise ValueError('Data changed during adoption.')
        backup.require_stopped(source, server_stopped)
        if destination.exists():
            raise FileExistsError('Destination was created concurrently.')
        pending.rename(destination)
    except BaseException:
        if pending.exists():
            assert pending.resolve().parent == destination.parent.resolve() and pending.name.startswith('.localizer-adopt-')
            shutil.rmtree(pending)
        raise
    return {**report, 'applied': True, 'backupSha256': snapshot['sha256'], 'preservedSourceFiles': len(before)}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--destination', required=True, type=Path)
    parser.add_argument('--archive', required=True, type=Path)
    parser.add_argument('--server-id', required=True)
    parser.add_argument('--rules', type=Path)
    parser.add_argument('--server-stopped', action='store_true')
    parser.add_argument('--apply', action='store_true', help='Default is a read-only inspection; apply copies into a NEW directory.')
    args = parser.parse_args()
    print(json.dumps(adopt(args.source, args.destination, args.archive, server_id=args.server_id, rules=args.rules,
                           server_stopped=args.server_stopped, apply=args.apply), indent=2))
