"""Check publishable source files for common local-data and credential leaks.

This is a deterministic guard, not a substitute for reviewing the public diff.
Generated work, build output, and artifacts are not publishable source inputs.
"""
from pathlib import Path
import json
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
IGNORED = {'.git', 'work', 'artifacts', 'bin', 'obj', '__pycache__', '.vs', '.idea'}
ROOT_FILES = {'.gitignore', '.gitattributes', 'global.json', 'NuGet.config', 'README.md', 'README.zh-CN.md', 'CHANGELOG.md', 'LICENSE', 'NOTICE'}
SOURCE_DIRS = {'src', 'tests', 'scripts', 'resources', 'docs', '.github'}
FORBIDDEN = {'.db', '.sqlite', '.sqlite3', '.nfo', '.dpapi', '.zip', '.7z', '.log', '.pfx', '.pem'}


def source_files():
    for path in sorted(ROOT.rglob('*')):
        relative = path.relative_to(ROOT)
        if not path.is_file() or any(part in IGNORED for part in relative.parts):
            continue
        if relative.parts[0] not in SOURCE_DIRS and relative.as_posix() not in ROOT_FILES:
            raise ValueError('Unexpected publishable path: ' + relative.as_posix())
        yield path


def inspect():
    failures = []
    files = list(source_files())
    patterns = [
        ('private home path', re.compile(r'[A-Za-z]:[\\/]Users[\\/](?!Public\b|<[^>]+>)[^\s\\/]+', re.I)),
        ('private library path', re.compile(r'[A-Za-z]:[\\/](?:JAV|AV)[\\/]', re.I)),
        ('API key', re.compile(r'\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{24,}|gsk_[A-Za-z0-9]{24,}|AIza[A-Za-z0-9_-]{30,})\b')),
        ('GitHub token', re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})\b')),
    ]
    for path in files:
        relative = path.relative_to(ROOT).as_posix()
        if path.suffix.lower() in FORBIDDEN or any(p in {'private', 'credentials', 'archives', 'reports'} for p in path.relative_to(ROOT).parts):
            failures.append({'file': relative, 'reason': 'private or generated payload'})
            continue
        try:
            text = path.read_text(encoding='utf-8-sig')
        except UnicodeDecodeError:
            failures.append({'file': relative, 'reason': 'unexpected binary source file'})
            continue
        for name, pattern in patterns:
            if pattern.search(text):
                failures.append({'file': relative, 'reason': name})
    return {'passed': not failures, 'filesChecked': len(files), 'failures': failures}


if __name__ == '__main__':
    try:
        result = inspect()
    except ValueError as error:
        result = {'passed': False, 'failures': [str(error)]}
    print(json.dumps(result, indent=2))
    sys.exit(0 if result['passed'] else 1)
