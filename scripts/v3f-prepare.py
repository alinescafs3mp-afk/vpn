#!/usr/bin/env python3
"""One-shot reviewed V3E -> V3F source integration. No network, execution or Git writes."""
import hashlib
from pathlib import Path


def blob(data):
    return hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()


def change(text, before, after):
    assert text.count(before) == 1, 'Exact integration context drift'
    return text.replace(before, after)


p = Path('src/AutoVpn.Infrastructure/Probe/NonTunCoreProbeTransport.cs')
original = p.read_bytes()
assert blob(original) == '565b08a1612137873c24d50171b754bdd99e4f15', 'Probe source is not the reviewed V3E base'
s = original.decode('utf-8')
s = change(s, 'private readonly Task<int> _stdout;', 'private readonly Task<ProbeOutputResult> _stdout;')
s = change(s, 'private readonly Task<int> _stderr;', 'private readonly Task<ProbeOutputResult> _stderr;')
s = change(s, 'CancellationTokenSource output, Task<int> stdout, Task<int> stderr,', 'CancellationTokenSource output, Task<ProbeOutputResult> stdout, Task<ProbeOutputResult> stderr,')
s = change(s, '    private bool _resourcesReleased;', '''    private bool _resourcesReleased;
    private bool _processExitConfirmed;
    private bool _outputFailed;
    private ProbeCleanupReport? _cleanupReport;
    public ProbeCleanupReport? CleanupReport => Volatile.Read(ref _cleanupReport);''')
s = change(s, 'CountAsync(process.StandardOutput, output.Token, null)', 'ProbeOutputDrain.ReadAsync(process.StandardOutput, output.Token)')
s = change(s, 'CountAsync(process.StandardError, output.Token, sessionTail)', 'ProbeOutputDrain.ReadAsync(process.StandardError, output.Token, sessionTail)')
s = change(s, 'Task.FromResult(0), Task.FromResult(0)', 'Task.FromResult(new ProbeOutputResult(0, ProbeOutputEnd.NotStarted)), Task.FromResult(new ProbeOutputResult(0, ProbeOutputEnd.NotStarted))')
s = change(s, '    public int OutputBytes { get; private set; }', '''    // Compatibility name. This has always counted decoded characters, not bytes.
    public int OutputBytes { get; private set; }
    public int OutputCharacters => OutputBytes;''')
s = change(s, '''        var phase = "PROCESS_STOP";
        try''', '''        var phase = "PROCESS_STOP";
        var elapsed = Stopwatch.StartNew();
        try''')
s = change(s, '''                phase = "OUTPUT_DRAIN";
                _output.Cancel();''', '''                _processExitConfirmed = true;
                phase = "OUTPUT_DRAIN";
                _output.Cancel();''')
s = change(s, '''                OutputBytes = (int)Math.Min(int.MaxValue, (long)counts[0] + counts[1]);''', '''                OutputBytes = (int)Math.Min(int.MaxValue, (long)counts[0].Characters + counts[1].Characters);
                _outputFailed = counts.Any(result => result.End == ProbeOutputEnd.Failed);''')
s = change(s, '''            Diagnostic = null;
            Volatile.Write(ref _disposed, 2);''', '''            // Failed readers have completed, so their owned handles/files can be released.
            // The probe still fails: a read fault must not silently become a healthy observation.
            phase = "OUTPUT_RESULT";
            if (_outputFailed) throw new ProbeCleanupException();
            Diagnostic = null;
            Volatile.Write(ref _disposed, 2);
            Volatile.Write(ref _cleanupReport, Report("COMPLETED", null));''')
s = change(s, '''            // Never dispose the process handle or delete its files after an
            // unconfirmed stop. A caller must not publish probe success here.
            throw new ProbeCleanupException();''', '''            var report = Report(phase + "_FAILED", ex);
            Volatile.Write(ref _cleanupReport, report);
            throw new ProbeCleanupException(report);''')
start = s.index('    private static async Task<int> CountAsync(')
end = s.index('    private static async Task<bool> DeleteDirectoryAsync', start)
s = s[:start] + s[end:]
s = change(s, '''            throw new ProbeCleanupException(report);
        }
    }

    private static async Task<bool> DeleteDirectoryAsync''', '''            throw new ProbeCleanupException(report);
        }

        ProbeCleanupReport Report(string stage, Exception? error) => new(stage, _processExitConfirmed,
            _resourcesReleased, DirectoryRemoved, elapsed.ElapsedMilliseconds, error switch
            {
                null => "NONE", TimeoutException => "TIMEOUT", OperationCanceledException => "CANCELED",
                UnauthorizedAccessException => "ACCESS", Win32Exception => "WIN32",
                IOException => "IO", _ => "STATE",
            }, error?.HResult ?? 0, _stdout.Status, _stderr.Status,
            _stdout.IsCompletedSuccessfully ? _stdout.GetAwaiter().GetResult() : null,
            _stderr.IsCompletedSuccessfully ? _stderr.GetAwaiter().GetResult() : null,
            ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount);
    }

    private static async Task<bool> DeleteDirectoryAsync''')
updated = {str(p): s.encode('utf-8')}
p = Path('src/AutoVpn.Infrastructure/Probe/CorePortLease.cs')
assert blob(p.read_bytes()) == 'fcad15e889b477eaf812b18abf4618a34fbc41f0', 'Port source drift'
s = change(p.read_text(), '    public ProbeCleanupException() : base("CORE_CLEANUP_REQUIRED") { }', '''    public ProbeCleanupReport? Report { get; }
    public ProbeCleanupException() : base("CORE_CLEANUP_REQUIRED") { }
    public ProbeCleanupException(ProbeCleanupReport report) : base("CORE_CLEANUP_REQUIRED:" + report.Summary)
    { Report = report; }''')
updated[str(p)] = s.encode('utf-8')
updated['Directory.Build.props'] = change(Path('Directory.Build.props').read_text(), '<Version>0.1.5</Version>', '<Version>0.1.6</Version>').encode()
s = Path('scripts/test-v3e.ps1').read_text().replace('artifacts/v3e', 'artifacts/v3f').replace('511', '524').replace('Previous V3D failures remain recorded. No remote TLS retry or certificate-policy change.', 'V3F adds 13 output cases. Previous V3E failures remain recorded. No remote TLS retry, timeout or certificate-policy change.')
updated['scripts/test-v3f.ps1'] = s.encode()
files = {
 'ProbeOutputDrain.cs': 'src/AutoVpn.Infrastructure/Probe/ProbeOutputDrain.cs',
 'AstraV3FOutputTests.cs': 'tests/AutoVpn.UnitTests/AstraV3FOutputTests.cs',
 'AutoVpn.ServiceLab.csproj': 'tests/AutoVpn.ServiceLab/AutoVpn.ServiceLab.csproj',
 'ServiceLabProgram.cs': 'tests/AutoVpn.ServiceLab/Program.cs',
 'service-smoke.ps1': 'scripts/lab/service-smoke.ps1',
}
for name, target in files.items():
    assert not Path(target).exists(), 'New source path already exists'
    updated[target] = (Path('docs/v3f-staging') / name).read_bytes()
expected = {
 'src/AutoVpn.Infrastructure/Probe/ProbeOutputDrain.cs': 'bd382713354131acf3317034591eb3777bb3b178b8c8ec38cf33e55bec872055',
 'src/AutoVpn.Infrastructure/Probe/NonTunCoreProbeTransport.cs': 'f3f71227cbca8d19f266e7bf8d50b779d26bd5b3c8eb823f4bcf5d3fb7a28a6c',
 'src/AutoVpn.Infrastructure/Probe/CorePortLease.cs': '8e8a192bfbceddf40ecaa5186f7bde30f3ddab9b74c3e17a85d08d63e7862533',
 'tests/AutoVpn.UnitTests/AstraV3FOutputTests.cs': 'bd7132584f6ee350c0be7538a37820b801edd5665aaf8ea939892f5fee895d54',
 'tests/AutoVpn.ServiceLab/AutoVpn.ServiceLab.csproj': '8a5aa4ca5be383aad4c72f9d06f5f7d240648b7100ff480100c234391bf1ad61',
 'tests/AutoVpn.ServiceLab/Program.cs': '1e8df4781636059d7d52405b68db7e5025164d372333e78e1fa26444add42ec8',
 'scripts/lab/service-smoke.ps1': 'eb58fb7f8f05fabef153becccd3411bcd1b2a956b144003c7e3e9bac25ee8bef',
 'scripts/test-v3f.ps1': '039397d1c746f487a7047d2216b4dd6cdbba33705414ef09b80a80292dc0eb7b',
 'Directory.Build.props': '6cae3b9359165d9001e46208acd0808c535def22b041dc2145c9469d40693fd7',
}
assert set(expected) == set(updated)
for name, data in updated.items():
    assert hashlib.sha256(data).hexdigest() == expected[name], 'Local reviewed source identity mismatch: ' + name
for name, data in updated.items():
    p = Path(name); p.parent.mkdir(parents=True, exist_ok=True); p.write_bytes(data)
for name in files:
    (Path('docs/v3f-staging') / name).unlink()
print('Applied nine exact reviewed files. TLS, SDK and core pin were not changed.')
