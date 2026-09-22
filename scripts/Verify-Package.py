"""Build or verify the explicitly allowlisted public release archive."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = '0.11.0-preview.1'
ASSEMBLIES = ('Localizer.Plugin.dll', 'Localizer.Core.dll', 'Localizer.Jellyfin.dll', 'Localizer.Translation.dll')
PAYLOAD = {f'Localizer/{name}': f'src/Localizer.Plugin/bin/Release/net10.0/{name}' for name in ASSEMBLIES}
PAYLOAD.update({name: name for name in (
    'LICENSE', 'NOTICE', 'README.md', 'README.zh-CN.md', 'CHANGELOG.md',
    'docs/INSTALL.md', 'docs/BACKUP.md', 'docs/PRIVACY.md', 'docs/VALIDATION.md', 'docs/DEVELOPMENT.md')})
PAYLOAD.update({'tools/Save-Credential.ps1': 'scripts/Save-Credential.ps1', 'tools/Localizer-DataBackup.py': 'scripts/Localizer-DataBackup.py'})


def digest(data):
    return hashlib.sha256(data).hexdigest()


def source_hash():
    spec = importlib.util.spec_from_file_location('privacy_check', ROOT / 'scripts/Privacy-Check.py')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    records = [f'{p.relative_to(ROOT).as_posix()} {digest(p.read_bytes())}' for p in module.source_files()]
    return digest(('\n'.join(records) + '\n').encode())


def verify(path):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        expected = set(PAYLOAD) | {'package-manifest.json'}
        if len(names) != len(set(names)) or set(names) != expected:
            raise ValueError('Archive does not match the exact public payload allowlist.')
        if archive.testzip() is not None:
            raise ValueError('Archive CRC validation failed.')
        manifest = json.loads(archive.read('package-manifest.json'))
        if manifest['packageVersion'] != VERSION or manifest['assemblyVersion'] != '0.11.0.0':
            raise ValueError('Unexpected package version.')
        if {entry['path'] for entry in manifest['files']} != set(PAYLOAD):
            raise ValueError('Manifest file inventory mismatch.')
        for entry in manifest['files']:
            data = archive.read(entry['path'])
            if len(data) != entry['size'] or digest(data) != entry['sha256']:
                raise ValueError('Payload hash or length mismatch: ' + entry['path'])
            if not entry['path'].endswith('.dll'):
                text = data.decode('utf-8-sig')
                if re.search(r'[A-Za-z]:[\\/]Users[\\/](?!Public\b|<)', text, re.I):
                    raise ValueError('Private machine path inside archive.')
        if len(manifest['hostDependencies']) != 7:
            raise ValueError('Host reference inventory is incomplete.')
        if any(not re.fullmatch('[0-9a-f]{64}', x['sha256']) for x in manifest['hostDependencies']):
            raise ValueError('Invalid host reference hash.')
    checksum = path.with_name('SHA256SUMS.txt')
    if checksum.read_text(encoding='utf-8').strip() != f'{digest(path.read_bytes())}  {path.name}':
        raise ValueError('Archive checksum mismatch.')
    return {'passed': True, 'package': path.name, 'files': len(names), 'sha256': digest(path.read_bytes())}


def create(metadata_path):
    metadata = json.loads(metadata_path.read_text(encoding='utf-8-sig'))
    data = {target: (ROOT / source).read_bytes() for target, source in PAYLOAD.items()}
    for entry in metadata['assemblies']:
        if entry['version'] != '0.11.0.0' or digest(data['Localizer/' + entry['name']]) != entry['sha256']:
            raise ValueError('Assembly changed since build metadata was collected.')
    manifest = {
        'packageVersion': VERSION, 'assemblyVersion': '0.11.0.0',
        'target': 'Jellyfin 12.0.0 / Windows x64 / .NET 10',
        'sourceTreeSha256': source_hash(), 'sourceHashAlgorithm': 'SHA-256 of sorted UTF-8 relative-path and file-hash lines, with final LF',
        'assemblies': metadata['assemblies'], 'hostDependencies': metadata['hostDependencies'],
        'files': [{'path': name, 'size': len(content), 'sha256': digest(content)} for name, content in sorted(data.items())],
    }
    data['package-manifest.json'] = (json.dumps(manifest, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    folder = ROOT / 'artifacts' / VERSION
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f'Jellyfin.MetadataLocalizer-{VERSION}-win-x64.zip'
    pending = path.with_suffix('.zip.tmp')
    with zipfile.ZipFile(pending, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, content in sorted(data.items()):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o644 << 16
            archive.writestr(entry, content, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    pending.replace(path)
    path.with_name('SHA256SUMS.txt').write_text(f'{digest(path.read_bytes())}  {path.name}\n', encoding='utf-8', newline='\n')
    result = verify(path)
    (ROOT / 'work/package-results.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument('--create', type=Path, metavar='BUILD_METADATA')
    action.add_argument('--verify', type=Path, metavar='ZIP')
    args = parser.parse_args()
    print(json.dumps(create(args.create) if args.create else verify(args.verify), indent=2))
