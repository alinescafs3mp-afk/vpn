#!/usr/bin/env python3
"""Read-only branch comparison and recoverable Git bundle; never merge or delete."""
import hashlib
import json
import pathlib
import subprocess

ROOT = pathlib.Path('artifacts/branch-inventory')
ROOT.mkdir(parents=True, exist_ok=True)
BASE = '121215c46d0f02c4f5b8f5ed417952866364bb6c'

def git(*args):
    return subprocess.check_output(['git', *args])

def text(*args):
    return git(*args).decode('utf-8').strip()

def tree(ref):
    result = {}
    for entry in git('ls-tree', '-r', '-z', ref).split(b'\0'):
        if entry:
            metadata, name = entry.split(b'\t', 1)
            mode, kind, sha = metadata.decode().split()
            result[name.decode()] = {'mode': mode, 'type': kind, 'sha': sha}
    return result

main = tree(BASE)
refs = []
for line in text('for-each-ref', '--format=%(refname) %(objectname)', 'refs/remotes/origin/').splitlines():
    ref, sha = line.split()
    if ref.endswith('/HEAD'):
        continue
    refs.append({'name': ref.removeprefix('refs/remotes/origin/'), 'ref': ref, 'sha': sha})
refs.sort(key=lambda x: x['name'])
records = []
for index, item in enumerate(refs):
    name, sha = item['name'], item['sha']
    ancestor = subprocess.run(['git', 'merge-base', '--is-ancestor', sha, BASE], check=False).returncode == 0
    common = text('merge-base', BASE, sha)
    branch, original = tree(sha), tree(common)
    changes = []
    for path in sorted(set(branch) | set(original)):
        current, old, present = branch.get(path), original.get(path), main.get(path)
        if current == old:
            continue
        entry = {'path': path, 'branch': current, 'base': old, 'main': present}
        if current == present:
            entry['disposition'] = 'IDENTICAL_IN_MAIN'
        elif ancestor:
            entry['disposition'] = 'ALREADY_IN_MAIN_HISTORY'
        elif current is None:
            entry['disposition'] = 'BRANCH_DELETION_REVIEW'
        elif present is None:
            entry['disposition'] = 'UNIQUE_PATH_REVIEW'
        else:
            entry['disposition'] = 'DIVERGENT_CONTENT_REVIEW'
        changes.append(entry)
    row = dict(item, ancestorOfBase=ancestor, mergeBase=common, changes=changes)
    records.append(row)
    review = [c for c in changes if c['disposition'].endswith('_REVIEW')]
    print(json.dumps({'branch': name, 'sha': sha, 'ancestorOfBase': ancestor,
                      'review': [{'path': c['path'], 'disposition': c['disposition'],
                                  'sha': c['branch']['sha'] if c['branch'] else None} for c in review]}, ensure_ascii=False))
    if not ancestor and name != 'main':
        patch = git('diff', '--binary', '--full-index', common, sha)
        (ROOT / f'{index:02d}-branch-change.patch').write_bytes(patch)

manifest = {'schemaVersion': 1, 'baseMain': BASE, 'auditCommit': text('rev-parse', 'HEAD'), 'branches': records}
(ROOT / 'inventory.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
bundle = ROOT / 'AutoVPN-before-branch-consolidation.bundle'
subprocess.run(['git', 'bundle', 'create', str(bundle), '--all'], check=True)
subprocess.run(['git', 'bundle', 'verify', str(bundle)], check=True)
(ROOT / 'BUNDLE-HEADS.txt').write_bytes(git('bundle', 'list-heads', str(bundle)))
sha = hashlib.sha256(bundle.read_bytes()).hexdigest()
(ROOT / 'SHA256SUMS.txt').write_text(sha + '  ' + bundle.name + '\n')
print('BUNDLE_SHA256 ' + sha)
print('BRANCH_COUNT ' + str(len(records)))
