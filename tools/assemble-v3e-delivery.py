#!/usr/bin/env python3
"""Retain exact sources, branch recovery and original test outcomes without rerunning tests."""
import collections
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = pathlib.Path('artifacts/v3e-delivery')
ROOT.mkdir(parents=True, exist_ok=True)
TESTED = '5599567c1827780eac3eff858689aa455f43ad50'
BASE = '121215c46d0f02c4f5b8f5ed417952866364bb6c'
FIRST = 'faa97966c7c04234308a2703be1241570100b41d'

def git(*args, env=None):
    return subprocess.check_output(['git', *args], env=env)

def text(*args):
    return git(*args).decode().strip()

def digest(data):
    return hashlib.sha256(data).hexdigest()

head, tree = text('rev-parse', 'HEAD'), text('rev-parse', 'HEAD^{tree}')
subprocess.run(['git', 'diff', '--exit-code', TESTED, head, '--', 'src', 'tests', 'config', 'scripts', 'Directory.Build.props', 'global.json', 'AutoVpn.slnx'], check=True)
expected = json.loads(pathlib.Path('docs/evidence/V3E_VALIDATION.json').read_text())
assert expected['testedCommit'] == TESTED and expected['testedTree'] == text('rev-parse', TESTED+'^{tree}')

history = ROOT/'history'
bundle = history/'AutoVPN-before-branch-consolidation.bundle'
assert digest(bundle.read_bytes()) == expected['branchConsolidation']['bundleSha256']
subprocess.run(['git', 'bundle', 'verify', str(bundle)], check=True)
old_inventory = json.loads((history/'inventory.json').read_text())
old_tips = [item for item in old_inventory['branches'] if item['name'] != 'main']
assert len(old_tips) == 17
for item in old_tips:
    subprocess.run(['git', 'merge-base', '--is-ancestor', item['sha'], head], check=True)
archive = json.loads(pathlib.Path('docs/branch-archive/inventory.json').read_text())
archived = 0
for branch in archive['branches']:
    for entry in branch['files']:
        if 'archivePath' not in entry:
            continue
        data = pathlib.Path(entry['archivePath']).read_bytes()
        assert data == git('cat-file', 'blob', entry['blob']['sha'])
        assert digest(data) == entry['sha256']
        archived += 1

skip_names = {
    'ubuntu-latest': {
        'AutoVpn.UnitTests.AstraV3DServiceProcessTests.RetainedLiveProcessHandleIsNotSignaled',
        'AutoVpn.UnitTests.AstraV3DServiceProcessTests.Exited259IsNotMistakenForStillActive',
        'AutoVpn.UnitTests.AstraV3DServiceProcessTests.InvalidAndClosedHandlesFailClosed',
        'AutoVpn.UnitTests.AstraV3EResourceTests.LockedCleanupIsReportedAndCanBeRetriedAfterRelease'},
    'windows-latest': {
        'AutoVpn.UnitTests.AstraV3ProfileTests.NativeRuntimeOwnsLocalPortsThenStopsItsExactProcess',
        'AutoVpn.UnitTests.IndependentRound3Tests.A16_OutputDrainMustContinueAfterItsRetentionCap',
        'AutoVpn.UnitTests.Round2SliceATests.Rt03WorkerCancelCleansCredentialsAndDoesNotKillTheNextProcess'}
}
results = {}
for label, commit in [('first', FIRST), ('final', TESTED)]:
    results[label] = {}
    for platform in skip_names:
        folder = ROOT/'evidence'/label/f'AutoVPN-V3E-{commit}-{platform}'
        summary = json.loads((folder/'summary.json').read_text(encoding='utf-8-sig'))
        manifest = json.loads((folder/'source-manifest.json').read_text())
        assert summary['sourceCommit'] == manifest['commit'] == commit
        assert manifest['tree'] == summary['sourceTree'] == text('rev-parse', commit+'^{tree}')
        assert summary['completedRuns'] == summary['plannedRuns'] == len(summary['runs']) == 6
        with zipfile.ZipFile(folder/'AutoVPN-V3E-source.zip') as package:
            assert package.testzip() is None
            assert len(package.namelist()) == len(set(package.namelist())) == len(manifest['files'])
            assert set(package.namelist()) == {'source/'+p for p in manifest['files']}
            for name, info in manifest['files'].items():
                data = package.read('source/'+name)
                assert len(data) == info['size'] and digest(data) == info['sha256']
                assert data == git('cat-file', 'blob', info['gitBlob'])
        counts, failures, exits = collections.Counter(), [], []
        added_counts = collections.Counter()
        for number in range(1, 7):
            cases = [e for e in ET.parse(folder/f'run-{number}/results.trx').iter() if e.tag.rsplit('}',1)[-1] == 'UnitTestResult']
            assert len(cases) == len({e.attrib['testId'] for e in cases}) == 511
            outcomes = collections.Counter(e.attrib['outcome'] for e in cases)
            assert set(outcomes) <= {'Passed', 'Failed', 'NotExecuted'}
            assert {e.attrib['testName'] for e in cases if e.attrib['outcome'] == 'NotExecuted'} == skip_names[platform]
            entry = summary['runs'][number-1]
            assert entry['iteration'] == number and entry['total'] == 511
            assert entry['passed'] == outcomes['Passed'] and entry['failed'] == outcomes['Failed']
            assert entry['valid'] == (entry['exitCode'] == 0 and outcomes['Failed'] == 0)
            counts.update(outcomes); exits.append(entry['exitCode'])
            added = [e for e in cases if '.AstraV3EResourceTests.' in e.attrib['testName'] or '.AstraV3DServiceProcessTests.' in e.attrib['testName']]
            assert len(added) == 25
            added_counts.update(e.attrib['outcome'] for e in added)
            failures.extend({'iteration':number, 'name':e.attrib['testName'], 'detail':''.join(e.itertext()).strip()} for e in cases if e.attrib['outcome'] == 'Failed')
        key = 'linux' if platform == 'ubuntu-latest' else 'windows'
        declared = expected[key] if label == 'final' else expected['firstSeries'][key]
        assert counts['Passed'] == declared['passed'] and counts['Failed'] == declared['failed'] and counts['NotExecuted'] == declared['skipped']
        if label == 'final': assert exits == declared['exitCodes']
        build = (folder/'build.log').read_text(encoding='utf-8-sig')
        assert 'Build succeeded.' in build
        warnings = re.findall(r'(\d+) Warning\(s\)', build)
        errors = re.findall(r'(\d+) Error\(s\)', build)
        assert errors and int(errors[-1]) == 0
        results[label][platform] = {'runs':6, 'outcomes':dict(counts), 'addedAndMigratedCases':dict(added_counts), 'exitCodes':exits,
            'failures':failures, 'buildWarnings':int(warnings[-1]) if warnings else None, 'buildErrors':0}

files = {}
source_zip = ROOT/'AutoVPN-V3E-source.zip'
with zipfile.ZipFile(source_zip, 'w', zipfile.ZIP_DEFLATED) as package:
    for entry in git('ls-tree', '-rz', head).split(b'\0'):
        if not entry: continue
        meta, name = entry.split(b'\t',1)
        mode, kind, oid = meta.decode().split(); name = name.decode()
        path = pathlib.PurePosixPath(name)
        assert kind == 'blob' and mode in ('100644','100755') and '..' not in path.parts and not path.is_absolute()
        data = git('cat-file','blob',oid)
        files[name] = {'sha256':digest(data),'size':len(data),'gitBlob':oid}
        package.writestr('source/'+name,data)
with zipfile.ZipFile(source_zip) as package:
    assert package.testzip() is None
    package.extractall(ROOT)
for name, info in files.items():
    data = (ROOT/'source'/name).read_bytes()
    assert len(data) == info['size'] and digest(data) == info['sha256']
patch = (ROOT/'AutoVPN-V3E-from-V3D.patch').resolve()
patch.write_bytes(git('diff','--binary','--full-index',BASE,head))
with tempfile.TemporaryDirectory(prefix='v3e-index-') as temp:
    env = dict(os.environ, GIT_INDEX_FILE=str(pathlib.Path(temp)/'index'))
    git('read-tree',BASE,env=env)
    git('apply','--cached','--check',str(patch),env=env)
    git('apply','--cached',str(patch),env=env)
    assert git('write-tree',env=env).decode().strip() == tree
    git('apply','--cached','--reverse',str(patch),env=env)
    assert git('write-tree',env=env).decode().strip() == text('rev-parse',BASE+'^{tree}')
(ROOT/'source-manifest.json').write_text(json.dumps({'commit':head,'tree':tree,'testedCommit':TESTED,'files':files},indent=2)+'\n')
shutil.copyfile('README_V3E_RU.md', ROOT/'START-HERE-RU.md')
report = {'schemaVersion':1, 'deliveryCommit':head, 'testedCommit':TESTED, 'codeIdentityVerified':True,
    'sourceFileCount':len(files),'sourceZipSha256':digest(source_zip.read_bytes()),'sourceUnpackedAndVerified':True,
    'patchForwardReverseVerified':True,'archivedUniqueFilesVerified':archived,'all17FormerTipsReachable':True,
    'results':results,'testSuitePassed':False,'installedVpnTested':False,'newWindowsBinaryIncluded':False,
    'note':'Source and evidence integrity passed. Windows test acceptance remains failed. No tests were rerun in this packaging action.'}
(ROOT/'DELIVERY-VALIDATION.json').write_text(json.dumps(report,indent=2)+'\n')
verifier = '''#!/usr/bin/env python3
import hashlib, json, pathlib
root=pathlib.Path(__file__).resolve().parent
manifest=json.loads((root/'source-manifest.json').read_text())
source=root/'source'
if source.is_symlink() or not source.is_dir(): raise SystemExit('Invalid source directory')
actual={}
for path in source.rglob('*'):
    if path.is_symlink(): raise SystemExit('Symlink rejected')
    if path.is_file(): actual[path.relative_to(source).as_posix()]=path
if set(actual)!=set(manifest['files']): raise SystemExit('Missing or extra source files')
for name,info in manifest['files'].items():
    data=actual[name].read_bytes()
    if len(data)!=info['size'] or hashlib.sha256(data).hexdigest()!=info['sha256']: raise SystemExit('Changed file: '+name)
print('Verified source files:',len(actual))
'''
(ROOT/'verify-source.py').write_text(verifier)
subprocess.run(['python',str(ROOT/'verify-source.py')],check=True)
hashes = [digest(p.read_bytes())+'  '+p.relative_to(ROOT).as_posix() for p in sorted(ROOT.rglob('*')) if p.is_file()]
(ROOT/'SHA256SUMS.txt').write_text('\n'.join(hashes)+'\n')
print(json.dumps({'deliveryCommit':head,'testedCommit':TESTED,'sourceFileCount':len(files),'archivedFilesVerified':archived,
    'all17FormerTipsReachable':True,'sourceIntegrity':'PASS','patchForwardReverse':'PASS',
    'testSuitePassed':False,'finalCounts':{p:r['outcomes'] for p,r in results['final'].items()}},indent=2))
