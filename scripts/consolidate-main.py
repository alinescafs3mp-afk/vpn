#!/usr/bin/env python3
"""One-shot, owner-requested archival consolidation. Never replace current application code."""
import hashlib
import json
import os
import pathlib
import subprocess
import urllib.request

EXPECTED = {
    'astra/implementation-r6': '69df17929a166c104150c1d0783cfbbe8e1ed771',
    'audit/round3-e7797c7-independent': 'a4b2b78e2fde0399798e907daef76e9a2b5c09de',
    'audit/round4-12c4b92-independent': 'a5f110b3c2f326e529f7d866ad9415062e6b64f5',
    'audit/round4-delivery-8ee0c71': '51f031b48ec84ce05954c3eade6539e3af538e5a',
    'audit/round5-49e5bd5-independent': '06f612780c146aedee9d5e8bd8f45ae9c5e93351',
    'audit/round6-bee2602-independent': 'a2aa6f518ae21c0f5fc419e1b7c5badb0b5db31b',
    'audit/round6-delivery-cf61932': '57316dbe00b0de14df05b3f5068590fcffe99956',
    'impl/astra-round6-20261004': '778fd142c25ae0175890d410e27d78d85a5a347c',
    'implementation/astra-r1': '0154ee4e155d4ffeef5989f808dc78ae9161fd10',
    'implementation/astra-r6-20261004': '15e7fd916e421930b33698ada4a671a3553e477e',
    'implementation/astra-v2': '70f9546d81ac58845c174d5dd4669dcd1fe27a5f',
    'implementation/astra-v3': '4e41da6596bfde2fb3db1811b3586e51bc8a2d8b',
    'implementation/astra-v3-verified': '7d99416c01499ed69593f4c948103cf0feb0d8c0',
    'implementation/astra-v3b': 'bc428b04e45cd4b0406998a24100710e182da248',
    'implementation/astra-v3c-status': '120bc69508340bbc1da2666a2a6ab05d5d20755e',
    'implementation/astra-v3d': '0dd9f78da000642e0b4d24dbbdb5a3ca31b21e80',
    'implementation/astra-v3d-tls': '121215c46d0f02c4f5b8f5ed417952866364bb6c',
}

def git(*args):
    return subprocess.check_output(['git', *args])

def text(*args):
    return git(*args).decode().strip()

def remote_heads():
    return {ref.removeprefix('refs/heads/'): sha for sha, ref in
            (line.split() for line in text('ls-remote', '--heads', 'origin').splitlines())}

if os.environ.get('GITHUB_REPOSITORY') != 'alinescafs3mp-afk/vpn' or os.environ.get('GITHUB_REF') != 'refs/heads/main':
    raise SystemExit('Wrong repository or branch')
head = text('rev-parse', 'HEAD')
assert head == os.environ['GITHUB_SHA']
request = urllib.request.Request('https://api.github.com/repos/alinescafs3mp-afk/vpn',
    headers={'Authorization': 'Bearer ' + os.environ['GH_TOKEN'], 'Accept': 'application/vnd.github+json'})
with urllib.request.urlopen(request, timeout=20) as response:
    assert json.load(response)['default_branch'] == 'main'
assert remote_heads() == dict(EXPECTED, main=head), 'Branches changed since review; no deletion permitted'
backup = pathlib.Path('artifacts/branch-backup')
bundle = backup/'AutoVPN-before-branch-consolidation.bundle'
assert hashlib.sha256(bundle.read_bytes()).hexdigest() == '06272071a0d24e1e944fe7cd5a665536769483ac4a0b532bb615af0e50a13c3c'
subprocess.run(['git', 'bundle', 'verify', str(bundle)], check=True)
inventory = json.loads((backup/'inventory.json').read_text())
assert {r['name']: r['sha'] for r in inventory['branches'] if r['name'] != 'main'} == EXPECTED
archive = pathlib.Path('docs/branch-archive')
assert not archive.exists(), 'This one-shot consolidation has already run'
archive.mkdir()
main_objects = {line.split()[0] for line in text('rev-list', '--objects', head).splitlines()}
records = []
for branch in inventory['branches']:
    if branch['name'] == 'main':
        continue
    record = {'branch': branch['name'], 'tip': branch['sha'], 'alreadyAncestor': branch['ancestorOfBase'], 'files': []}
    for change in branch['changes']:
        if not change['disposition'].endswith('_REVIEW'):
            continue
        name = change['path']; blob = change['branch']
        entry = {'originalPath': name, 'blob': blob, 'decision': 'RETAINED_IN_MAIN_HISTORY'}
        if blob and blob['sha'] not in main_objects and not name.endswith('.b64') and not name.startswith('.astra-transfer/'):
            path = pathlib.PurePosixPath(name)
            assert not path.is_absolute() and '..' not in path.parts
            assert blob['type'] == 'blob' and blob['mode'] in ('100644', '100755')
            data = git('cat-file', 'blob', blob['sha'])
            assert hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest() == blob['sha']
            destination = archive/branch['sha'][:12]/path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
            assert destination.read_bytes() == data
            entry.update(decision='ARCHIVED_FOR_REUSE_NOT_ACTIVE_RUNTIME', archivePath=destination.as_posix(), sha256=hashlib.sha256(data).hexdigest())
        record['files'].append(entry)
    records.append(record)
report = {'schemaVersion': 1, 'sourceMain': head, 'backupRun': 37353615748,
          'backupBundleSha256': hashlib.sha256(bundle.read_bytes()).hexdigest(), 'branches': records,
          'strategy': 'Current runtime retained. Historical branch tips become merge parents; unique reviewable sources archived. Opaque transfer fragments stay only in history.',
          'activePortPlanned': 'ProcessHandleLiveness, SCM state reporting and native handle tests from alternative V3D; tested separately in V3E.',
          'runtimeAlternatives': 'The old LiveCoreSession allows explicitly unprotected TUN and has controller-only startup; it must not replace the current reviewed broker. WindowsProcessJob is retained as a reuse candidate, not proof of watchdog or network restoration.',
          'deletedBranches': 'Deletion result is recorded separately in the workflow artifact.'}
(archive/'inventory.json').write_text(json.dumps(report, indent=2)+'\n')
(archive/'README.md').write_text('# Consolidated branch history\n\nAll former tips are retained as ancestors of the consolidation commit on main. No old application file overwrites newer code.\n\nUnique reviewable audit tools, workflows and alternate runtime sources are kept here byte-for-byte, outside active build and workflow paths. Transfer chunks remain available in Git history and the verified bundle, not in the working source tree. inventory.json maps each path and exact origin. Archived prototypes are not enabled or accepted as production VPN features.\n\nUseful active migrations are recorded and tested in V3E. Restore historical files with git show <tip>:<originalPath>; do not run obsolete import/materialization workflows against main.\n', encoding='utf-8')
subprocess.run(['git', 'add', '--', 'docs/branch-archive'], check=True)
assert all(p.startswith('docs/branch-archive/') for p in text('diff', '--cached', '--name-only').splitlines())
tree = text('write-tree')
parents = [head]
for sha in dict.fromkeys(EXPECTED.values()):
    if subprocess.run(['git', 'merge-base', '--is-ancestor', sha, head]).returncode != 0:
        parents.append(sha)
args = ['git', 'commit-tree', tree]
for parent in parents:
    args.extend(['-p', parent])
merged = subprocess.check_output(args, input=b'chore: preserve all branch histories and unique audit/runtime sources on main\n').decode().strip()
for sha in EXPECTED.values():
    subprocess.run(['git', 'merge-base', '--is-ancestor', sha, merged], check=True)
assert remote_heads() == dict(EXPECTED, main=head), 'Remote changed during archival; stopping'
# Normal fast-forward push for main, never forced.
subprocess.run(['git', 'push', 'origin', merged+':refs/heads/main'], check=True)
assert remote_heads() == dict(EXPECTED, main=merged)
# Explicit leases apply ONLY to deletion of the 17 reviewed refs. Concurrent changes reject the entire atomic deletion.
args = ['git', 'push', '--atomic', '--no-follow-tags']
args += ['--force-with-lease=refs/heads/'+name+':'+sha for name, sha in EXPECTED.items()]
args += ['origin'] + [':refs/heads/'+name for name in EXPECTED]
subprocess.run(args, check=True)
remaining = remote_heads()
assert remaining == {'main': merged}, 'Unexpected final branch state'
result = {'mainCommit': merged, 'mainTree': tree, 'deletedCount': len(EXPECTED), 'remaining': remaining,
          'everyFormerTipReachableFromMain': True, 'archivedFiles': sum('archivePath' in f for r in records for f in r['files'])}
pathlib.Path('artifacts/consolidation-result.json').write_text(json.dumps(result, indent=2)+'\n')
print(json.dumps(result, indent=2))
