using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AutoVpn.TestSupport;

// Test-only RM snapshot. Never requests shutdown/restart or registers processes.
internal static class WindowsFileUseControl
{
    internal static async Task<int> RunAsync(FileUseControlMode mode)
    {
        if (!OperatingSystem.IsWindows()) return 5;
        var report = new FileUseSnapshot();
        var output = new ProtocolWriter();
        uint session = 0; var started = false;
        output.Observe(FileUsePhase.HELPER_STARTED);
        try
        {
            using var input = Console.OpenStandardInput();
            if (mode == FileUseControlMode.InputClosed)
            {
                var handle = GetStdHandle(-10);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1) || GetFileType(handle) != 3)
                    throw new InvalidOperationException();
                input.Close();
                // WindowsConsoleStream.Dispose leaves its OS handle open. Close only
                // this disposable child's inherited pipe and remove its standard slot.
                if (!SetStdHandle(-10, IntPtr.Zero) || !CloseHandle(handle))
                    throw new InvalidOperationException();
                // Parent waits for this flushed phase before its actual pipe write.
                output.Observe(FileUsePhase.INPUT_CLOSED);
                await Task.Delay(TimeSpan.FromSeconds(10));
                report = report with { State = "TIMEOUT_CONTROL_FINISHED" };
            }
            else
            {
                if (mode == FileUseControlMode.TimeoutBeforeInput)
                    await Task.Delay(TimeSpan.FromSeconds(10));
                var bytes = new byte[4097]; var length = 0;
                while (length < bytes.Length)
                {
                    var read = await input.ReadAsync(bytes.AsMemory(length));
                    if (read == 0) break;
                    length += read;
                }
                output.Observe(FileUsePhase.INPUT_RECEIVED);
                if (length is 0 or > 4096) throw new InvalidDataException();
                using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
                var root = json.RootElement;
                var path = root.GetProperty("path").GetString()!;
                var hostPid = root.GetProperty("hostPid").GetUInt32();
                var hostCreated = root.GetProperty("hostCreated").GetUInt64();
                var childPid = root.GetProperty("childPid").GetUInt32();
                var childCreated = root.GetProperty("childCreated").GetUInt64();
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || hostPid == 0 || hostCreated == 0)
                    throw new InvalidDataException();
                output.Observe(FileUsePhase.INPUT_VALIDATED);
                if (mode == FileUseControlMode.TimeoutAfterInput)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));
                    report = report with { State = "TIMEOUT_CONTROL_FINISHED" };
                }
                else
                {
                    output.Observe(FileUsePhase.RM_START_BEGIN);
                    report = report with { StartCode = RmStartSession(out session, 0, new StringBuilder(33)) };
                    started = report.StartCode == 0;
                    output.Observe(FileUsePhase.RM_START_RETURNED);
                    if (started)
                    {
                        output.Observe(FileUsePhase.REGISTER_BEGIN);
                        report = report with { RegisterCode = RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero) };
                        output.Observe(FileUsePhase.REGISTER_RETURNED);
                        if (report.RegisterCode == 0)
                        {
                            uint needed, count = 0;
                            output.Observe(FileUsePhase.QUERY_BEGIN, 1);
                            // A staged control, never evidence that the native RM call stalled.
                            if (mode == FileUseControlMode.TimeoutBeforeQuery)
                                await Task.Delay(TimeSpan.FromSeconds(10));
                            report = report with { QueryCalls = report.QueryCalls + 1 };
                            report = report with { QueryCode = RmGetList(session, out needed, ref count, null, out _) };
                            output.Observe(FileUsePhase.QUERY_RETURNED, 1);
                            if (report.QueryCode == 0 && needed == 0 && count == 0)
                                report = report with { State = "NO_HOLDER_OR_INCOMPLETE" };
                            else if (report.QueryCode == 234 && needed is > 0 and <= 32)
                            {
                                var expected = needed;
                                var owners = new ProcessInfo[32]; count = 32;
                                output.Observe(FileUsePhase.QUERY_BEGIN, 2);
                                report = report with { QueryCalls = report.QueryCalls + 1 };
                                report = report with { QueryCode = RmGetList(session, out needed, ref count, owners, out _) };
                                output.Observe(FileUsePhase.QUERY_RETURNED, 2);
                                if (report.QueryCode == 0 && count <= 32)
                                {
                                    report = report with { TotalOwners = (int)count };
                                    var identityIncomplete = false;
                                    foreach (var owner in owners.Take((int)count))
                                    {
                                        var created = ((ulong)owner.Process.Created.High << 32) | owner.Process.Created.Low;
                                        identityIncomplete |= owner.Process.Pid == 0 || created == 0;
                                        if (owner.Process.Pid == hostPid && created != 0 && created == hostCreated)
                                            report = report with { TestHostOwners = report.TestHostOwners + 1 };
                                        else if (childPid != 0 && childCreated != 0 && owner.Process.Pid == childPid && created == childCreated)
                                            report = report with { OwnedChildOwners = report.OwnedChildOwners + 1 };
                                        // OTHER is another observed identity, never a claim of causality.
                                        else report = report with { OtherOwners = report.OtherOwners + 1 };
                                    }
                                    report = report with { Incomplete = identityIncomplete || count == 0 || count != expected || needed > count };
                                    report = report with { State = report.Incomplete ? "NO_HOLDER_OR_INCOMPLETE" : "OBSERVED" };
                                }
                                else report = report with { State = "QUERY_INCOMPLETE" };
                                report = report with { Truncated = needed > 32 || count > 32 };
                            }
                            else
                            {
                                report = report with { Truncated = needed > 32, State = "QUERY_INCOMPLETE" };
                            }
                        }
                    }
                }
            }
        }
        catch (Exception) { report = report with { State = "HELPER_ERROR", Incomplete = true }; }
        finally
        {
            if (started)
            {
                output.Observe(FileUsePhase.END_BEGIN);
                try
                {
                    report = report with { EndCode = RmEndSession(session) };
                    output.Observe(FileUsePhase.END_RETURNED);
                }
                catch (Exception) { report = report with { State = "HELPER_ERROR", Incomplete = true }; }
                if (report.EndCode != 0) report = report with { Incomplete = true, State = "QUERY_INCOMPLETE" };
            }
        }
        output.Observe(FileUsePhase.RESULT_READY);
        output.Result(report);
        return 0;
    }

    private sealed class ProtocolWriter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _sequence;
        private int _writtenBytes;
        private bool _failed;

        internal void Observe(FileUsePhase phase, int? queryOrdinal = null) => Write(new()
        {
            Kind = FileUseFrameKind.PHASE, Sequence = ++_sequence,
            ElapsedMilliseconds = _clock.ElapsedMilliseconds, Phase = phase, QueryOrdinal = queryOrdinal,
        });

        internal void Result(FileUseSnapshot snapshot) => Write(new()
        {
            Kind = FileUseFrameKind.RESULT, Sequence = ++_sequence,
            ElapsedMilliseconds = _clock.ElapsedMilliseconds, Snapshot = snapshot,
        });

        private void Write(FileUseDiagnosticFrame frame)
        {
            if (_failed) return;
            try
            {
                var line = JsonSerializer.Serialize(frame, FileUseDiagnosticProtocol.JsonOptions);
                var bytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (frame.Sequence > FileUseDiagnosticProtocol.MaximumFrames ||
                    bytes > FileUseDiagnosticProtocol.MaximumOutputBytes - _writtenBytes)
                { _failed = true; return; }
                Console.Out.Write(line);
                Console.Out.Write('\n');
                Console.Out.Flush();
                _writtenBytes += bytes;
            }
            catch (Exception)
            {
                // A broken output must not bypass RmEndSession in the owning finally.
                _failed = true;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct UniqueProcess { public uint Pid; public FileTime Created; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
        public uint AppType, Status, SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    // https://learn.microsoft.com/windows/win32/api/restartmanager/nf-restartmanager-rmgetlist
    // https://learn.microsoft.com/windows/win32/api/restartmanager/ns-restartmanager-rm_process_info
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RmRegisterResources(uint session, uint files, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] paths, uint applications, IntPtr processes, uint services, IntPtr names);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[]? owners, out uint rebootReasons);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)] private static extern uint RmEndSession(uint session);
    // https://learn.microsoft.com/windows/console/getstdhandle
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
