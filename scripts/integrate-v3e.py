#!/usr/bin/env python3
"""Exact, readable one-time source refactor. Runs in CI because local authoring is unavailable."""
import pathlib
import subprocess

PATH = 'src/AutoVpn.Infrastructure/Probe/NonTunCoreProbeTransport.cs'
source = subprocess.check_output(['git', 'show', 'HEAD:'+PATH]).decode()
assert subprocess.check_output(['git', 'rev-parse', 'HEAD:'+PATH]).decode().strip() == 'd2f4155019d7f874d609da9826b941fd0d4f12bd'
tls_marker = 'public readonly record struct TlsProbeExchange'
worker_marker = 'public sealed class ProbeWorker'
original_tls = source.split(tls_marker, 1)[1].split(worker_marker, 1)[0]

def once(text, old, new):
    assert text.count(old) == 1, 'Expected one exact source anchor: '+old[:80]
    return text.replace(old, new, 1)

source = once(source, 'var directory = Directory.CreateTempSubdirectory("autovpn-probe-");',
    'var directory = Directory.CreateTempSubdirectory("autovpn-probe-");\n        var cleanupOwnedByWorker = false;')
assert source.count('List<TcpListener>') == 2
source = source.replace('List<TcpListener>', 'List<CorePortLease>')
source = once(source, '''        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        held.Add(listener);
        return ((IPEndPoint)listener.LocalEndpoint).Port;''', '''        var listener = CorePortLease.Reserve();
        held.Add(listener);
        return listener.Port;''')
source = source.replace('listener.Stop();', 'listener.Dispose();')
source = once(source, '            await using var session = await ProbeWorker.StartAsync(',
    '            cleanupOwnedByWorker = true;\n            await using var session = await ProbeWorker.StartAsync(')
source = once(source, '''        catch (OperationCanceledException)
        {
            return Fail(ProbeClass.Canceled, ReasonCodes.Canceled, digest, target);
        }
        finally''', '''        catch (OperationCanceledException)
        {
            return Fail(ProbeClass.Canceled, ReasonCodes.Canceled, digest, target);
        }
        catch (CorePortReservationException)
        {
            LastDiagnostic = "CORE_PORT_UNAVAILABLE";
            return Fail(ProbeClass.CoreFailure, LastDiagnostic, digest, target);
        }
        catch (ProbeCleanupException)
        {
            LastDiagnostic = "CORE_CLEANUP_REQUIRED";
            return Fail(ProbeClass.CoreFailure, LastDiagnostic, digest, target);
        }
        finally''')
source = once(source, 'if (directory.Exists)', 'if (!cleanupOwnedByWorker && directory.Exists)')
source = once(source, '    private int _disposed;', '''    private int _disposed;
    private readonly object _cleanupGate = new();
    private Task? _cleanupTask;
    private bool _resourcesReleased;''')
source = once(source, '        startInfo.UseShellExecute = false;', '''        if (cancellationToken.IsCancellationRequested)
            return await FailedStartAsync(directoryToDelete).ConfigureAwait(false);
        startInfo.UseShellExecute = false;''')
start = source.index('    public async ValueTask DisposeAsync()')
end = source.index('    private static async Task<int> CountAsync', start)
source = source[:start] + '''    public ValueTask DisposeAsync()
    {
        lock (_cleanupGate)
        {
            // Concurrent callers join one attempt. A failed attempt remains
            // observable and can be retried after the cause has been removed.
            if (_cleanupTask is null || _cleanupTask.IsFaulted || _cleanupTask.IsCanceled)
                _cleanupTask = CleanupAsync();
            return new ValueTask(_cleanupTask);
        }
    }

    private async Task CleanupAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        var phase = "PROCESS_STOP";
        try
        {
            if (!_resourcesReleased)
            {
                if (_process is not null)
                {
                    try
                    {
                        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when ((ex is InvalidOperationException or Win32Exception) && _process.HasExited)
                    {
                        // A natural exit may race Kill. Still wait on the retained object.
                    }
                    await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                phase = "OUTPUT_DRAIN";
                _output.Cancel();
                var counts = await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                OutputBytes = (int)Math.Min(int.MaxValue, (long)counts[0] + counts[1]);
                _output.Dispose();
                _process?.Dispose();
                _resourcesReleased = true;
            }
            phase = "DIRECTORY_CLEANUP";
            DirectoryRemoved = await DeleteDirectoryAsync(_directory).ConfigureAwait(false);
            if (!DirectoryRemoved) throw new ProbeCleanupException();
            Diagnostic = null;
            Volatile.Write(ref _disposed, 2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException
            or InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            Diagnostic = phase + "_FAILED";
            // Never dispose the process handle or delete its files after an
            // unconfirmed stop. A caller must not publish probe success here.
            throw new ProbeCleanupException();
        }
    }

''' + source[end:]
start = source.index('    private static bool DeleteDirectory(string? directory)')
assert source[start:].rstrip().endswith('}')
source = source[:start] + '''    private static async Task<bool> DeleteDirectoryAsync(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return true;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException ex) when (OperatingSystem.IsWindows() &&
                ((ex.HResult & 0xffff) is 32 or 33) && elapsed.Elapsed < TimeSpan.FromSeconds(1))
            {
                // Only a transient sharing/lock violation, after confirmed
                // owned-process exit. No handshake or test is retried.
                await Task.Delay(25).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}
'''
assert source.split(tls_marker, 1)[1].split(worker_marker, 1)[0] == original_tls
pathlib.Path(PATH).write_text(source, encoding='utf-8')

path = pathlib.Path('tests/AutoVpn.UnitTests/Round6NativeHandshakeTests.cs')
text = path.read_text()
text = once(text, 'var listener = new TcpListener(IPAddress.Loopback, 0);', 'using var listener = CorePortLease.Reserve();')
text = once(text, 'var socksListener = new TcpListener(IPAddress.Loopback, 0);', 'using var socksListener = CorePortLease.Reserve();')
text = once(text, 'listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;', 'var port=listener.Port;')
text = once(text, 'socksListener.Start();var socksPort=((IPEndPoint)socksListener.LocalEndpoint).Port;', 'var socksPort=socksListener.Port;')
assert text.count('listener.Stop();socksListener.Stop();') == 2
text = text.replace('listener.Stop();socksListener.Stop();', 'listener.Dispose();socksListener.Dispose();')
path.write_text(text)
path = pathlib.Path('tests/AutoVpn.UnitTests/AstraV3DStartupReadinessTests.cs')
text = path.read_text()
text = once(text, 'var reserved = new TcpListener(IPAddress.Loopback, 0);', 'using var reserved = CorePortLease.Reserve();')
text = once(text, '            reserved.Start();\n            var socks = ((IPEndPoint)reserved.LocalEndpoint).Port;', '            var socks = reserved.Port;')
assert text.count('reserved.Stop();') == 2
path.write_text(text.replace('reserved.Stop();', 'reserved.Dispose();'))
path = pathlib.Path('Directory.Build.props')
path.write_text(once(path.read_text(), '<Version>0.1.4</Version>', '<Version>0.1.5</Version>'))
print('Applied V3E port and cleanup integration. TLS/HTTP exchange bytes unchanged.')
