using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Core;

namespace AutoVpn.Infrastructure.Probe;

public sealed class ProbeWorker : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly CancellationTokenSource _output;
    private readonly Task<int> _stdout;
    private readonly Task<int> _stderr;
    private readonly string? _directory;
    private int _disposed;

    private ProbeWorker(Process? process, CancellationTokenSource output, Task<int> stdout, Task<int> stderr, string? directory, bool ready, string workerId, StringBuilder tail)
    {
        _process = process;
        _output = output;
        _stdout = stdout;
        _stderr = stderr;
        _directory = directory;
        Ready = ready;
        WorkerId = workerId;
        _tail = tail;
    }

    private readonly StringBuilder _tail;

    public bool Ready { get; }
    public string WorkerId { get; }
    public int OutputBytes { get; private set; }
    public bool DirectoryRemoved { get; private set; }
    public string? Diagnostic { get; private set; }

    public string OutputTail
    {
        get
        {
            lock (_tail)
            {
                return _tail.ToString();
            }
        }
    }

    public static async Task<ProbeWorker> StartAsync(
        ProcessStartInfo startInfo,
        int? readyPort,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? directoryToDelete)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var sessionTail = new StringBuilder();
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return await FailedStartAsync(directoryToDelete).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return await FailedStartAsync(directoryToDelete).ConfigureAwait(false);
        }

        var output = new CancellationTokenSource();
        var stdout = CountAsync(process.StandardOutput, output.Token, null);
        var stderr = CountAsync(process.StandardError, output.Token, sessionTail);
        var ready = false;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                break;
            }

            if (readyPort is null)
            {
                ready = true;
                break;
            }

            if (ProcessOwnsLoopbackPort(process.Id, readyPort.Value))
            {
                ready = true;
                break;
            }

            await Task.Delay(30, CancellationToken.None).ConfigureAwait(false);
        }

        var workerId = ready
            ? process.Id.ToString(CultureInfo.InvariantCulture) + ":" + (readyPort?.ToString(CultureInfo.InvariantCulture) ?? "0")
            : "";
        var session = new ProbeWorker(process, output, stdout, stderr, directoryToDelete, ready, workerId, sessionTail);
        if (!ready)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        return session;
    }

    private static async Task<ProbeWorker> FailedStartAsync(string? directoryToDelete)
    {
        var failed = new ProbeWorker(null, new CancellationTokenSource(), Task.FromResult(0), Task.FromResult(0), directoryToDelete, false, "", new StringBuilder());
        await failed.DisposeAsync().ConfigureAwait(false);
        return failed;
    }

    public static bool ProcessOwnsLoopbackPort(int pid, int port)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsOwnsLoopbackPort(pid, port);
        }

        try
        {
            var needle = "0100007F:" + port.ToString("X4", CultureInfo.InvariantCulture);
            var inodes = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists("/proc/net/tcp"))
            {
                foreach (var line in File.ReadLines("/proc/net/tcp"))
                {
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 10 || !fields[1].Equals(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    inodes.Add(fields[9]);
                }
            }

            if (inodes.Count == 0)
            {
                return false;
            }

            var fdDirectory = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/fd";
            if (!Directory.Exists(fdDirectory))
            {
                return false;
            }

            foreach (var fd in Directory.EnumerateFiles(fdDirectory))
            {
                string link;
                try
                {
                    link = new FileInfo(fd).LinkTarget ?? "";
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var inode in inodes)
                {
                    if (link.Contains("socket:[" + inode + "]", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static bool WindowsOwnsLoopbackPort(int pid, int port)
    {
        var size = 0;
        var probe = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidListener, 0);
        if (probe != NativeMethods.ErrorInsufficientBuffer || size <= 0)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, NativeMethods.AfInet, NativeMethods.TcpTableOwnerPidListener, 0);
            if (result != 0)
            {
                return false;
            }

            var count = Marshal.ReadInt32(buffer);
            var row = 24;
            for (var index = 0; index < count; index++)
            {
                var address = IntPtr.Add(buffer, 4 + (index * row));
                var localAddress = unchecked((uint)Marshal.ReadInt32(address, 4));
                var localPort = unchecked((uint)Marshal.ReadInt32(address, 8));
                var owner = Marshal.ReadInt32(address, 20);
                var hostPort = ((int)(localPort & 0xFF) << 8) | (int)((localPort >> 8) & 0xFF);
                if (owner == pid && hostPort == port && localAddress == 0x0100007F)
                {
                    return true;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return false;
    }

    private static class NativeMethods
    {
        public const uint AfInet = 2;
        public const int TcpTableOwnerPidListener = 3;
        public const uint ErrorInsufficientBuffer = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, uint family, int tableClass, uint reserved);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            if (_process is { HasExited: false })
            {
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
        }

        _output.Cancel();
        try
        {
            OutputBytes = await _stdout.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            OutputBytes += await _stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
        {
            Diagnostic = ex.GetType().Name;
        }

        _output.Dispose();
        _process?.Dispose();
        DirectoryRemoved = DeleteDirectory(_directory);
    }

    private static async Task<int> CountAsync(StreamReader reader, CancellationToken cancellationToken, StringBuilder? tail)
    {
        var buffer = new char[1024];
        var total = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                total = total > int.MaxValue - count ? int.MaxValue : total + count;
                if (tail is not null && tail.Length < 2000)
                {
                    lock (tail)
                    {
                        var room = 2000 - tail.Length;
                        if (room > 0)
                        {
                            tail.Append(buffer, 0, Math.Min(count, room));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }

        return total;
    }

    private static bool DeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return true;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
