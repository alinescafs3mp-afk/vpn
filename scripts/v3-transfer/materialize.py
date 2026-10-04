"""Reconstruct the exact authored tree in an isolated Actions checkout.
The compressed payload is a source diff, never an executable or downloaded code.
"""
import base64
import gzip
import hashlib
import os
from pathlib import Path
import subprocess
import tempfile

BASE = '0702f2ab627ce879aa9ded72c26ee9166230b4d9'
TARGET = '03837ba2ca22e4c0aa305679a58866b1c7e614c1'
GZIP_HASH = '78db54719a47acc4131bf79bcbb6d2a83dfe2044327e862bc5fefe56e34e4197'
PATCH_HASH = '3b63658c851eb84333fafde15a53e31ded2a722c8922e5290eed32d0ec809456'

def git(*args, data=None, env=None):
    return subprocess.run(['git', *args], input=data, stdout=subprocess.PIPE,
                          check=True, env=env).stdout.decode('utf-8').strip()

def require(condition, message):
    if not condition:
        raise RuntimeError(message)

stage = git('rev-parse', 'HEAD')
parts = [Path(f'scripts/v3-transfer/{i:02}.b64') for i in range(1, 64)]
require(set(Path('scripts/v3-transfer').glob('*.b64')) == set(parts), 'Unexpected parts')
encoded = ''.join(p.read_text(encoding='ascii').strip() for p in parts)
packed = base64.b64decode(encoded, validate=True)
require(len(packed) == 23335 and hashlib.sha256(packed).hexdigest() == GZIP_HASH,
        'Compressed source transfer mismatch')
patch = gzip.decompress(packed)
require(len(patch) == 88744 and hashlib.sha256(patch).hexdigest() == PATCH_HASH,
        'Source patch mismatch')
git('config', 'core.autocrlf', 'false')
git('checkout-index', '--force', '--all')
git('rm', '-r', '--', 'scripts/v3-transfer', '.github/workflows/astra-v3-materialize.yml')
require(git('write-tree') == BASE, 'Unexpected staged source outside transfer')
git('checkout-index', '--force', '--all')
with tempfile.TemporaryDirectory(prefix='autovpn-source-') as temp:
    path = Path(temp) / 'source.patch'
    path.write_bytes(patch)
    git('apply', '--index', '--unidiff-zero', '--whitespace=error-all', str(path))
git('rm', '--', 'scripts/v3-finish-1.b64')
require(git('write-tree') == TARGET, 'Resulting source tree mismatch')
stamp = str(int(git('show', '-s', '--format=%ct', stage)) + 1) + ' +0000'
env = os.environ.copy()
for role in ('AUTHOR', 'COMMITTER'):
    env[f'GIT_{role}_NAME'] = 'Astra'
    env[f'GIT_{role}_EMAIL'] = 'astra@users.noreply.github.com'
    env[f'GIT_{role}_DATE'] = stamp
commit = git('commit-tree', TARGET, '-p', stage,
             data=b'feat(v3): complete owned live session preview and versioned delivery\n', env=env)
git('update-ref', 'HEAD', commit, stage)
require(not git('status', '--porcelain', '--untracked-files=no'), 'Materialized worktree is dirty')
out = Path('artifacts/source'); out.mkdir(parents=True, exist_ok=True)
git('archive', '--format=zip', '-o', str(out / 'source.zip'), 'HEAD')
(out / 'commit.txt').write_text(commit + '\n', encoding='ascii')
(out / 'tree.txt').write_text(TARGET + '\n', encoding='ascii')
(out / 'stage.txt').write_text(stage + '\n', encoding='ascii')
(out / 'tree.nul').write_bytes(subprocess.check_output(['git', 'ls-tree', '-rz', 'HEAD']))
print('Verified materialized source:', commit, TARGET)
if os.environ.get('GITHUB_OUTPUT'):
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
        output.write(f'commit={commit}\ntree={TARGET}\n')
