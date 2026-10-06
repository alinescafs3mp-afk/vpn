using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Test-only RM snapshot. Never requests shutdown/restart or registers processes.
internal static class WindowsFileUseControl
{
    internal static async Task<int> RunAsync(bool deliberateTimeout)
    {
        if (!OperatingSystem.IsWindows()) return 5;
        var report = new Report();
        uint session = 0; var started = false;
        try
        {
            using var input = Console.OpenStandardInput();
            var bytes = new byte[4097]; var length = 0;
            while (length < bytes.Length)
            {
                var read = await input.ReadAsync(bytes.AsMemory(length));
                if (read == 0) break;
                length += read;
            }
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
            if (deliberateTimeout)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                report.State = "TIMEOUT_CONTROL_FINISHED";
            }
            else
            {
                report.StartCode = RmStartSession(out session, 0, new StringBuilder(33));
                started = report.StartCode == 0;
                if (started)
                {
                    report.RegisterCode = RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero);
                    if (report.RegisterCode == 0)
                    {
                        uint needed, count = 0;
                        report.QueryCalls++;
                        report.QueryCode = RmGetList(session, out needed, ref count, null, out _);
                        if (report.QueryCode == 0 && needed == 0 && count == 0)
                            report.State = "NO_HOLDER_OR_INCOMPLETE";
                        else if (report.QueryCode == 234 && needed is > 0 and <= 32)
                        {
                            var expected = needed;
                            var owners = new ProcessInfo[32]; count = 32;
                            report.QueryCalls++;
                            report.QueryCode = RmGetList(session, out needed, ref count, owners, out _);
                            if (report.QueryCode == 0 && count <= 32)
                            {
                                report.TotalOwners = (int)count;
                                var identityIncomplete = false;
                                foreach (var owner in owners.Take((int)count))
                                {
                                    var created = ((ulong)owner.Process.Created.High << 32) | owner.Process.Created.Low;
                                    identityIncomplete |= owner.Process.Pid == 0 || created == 0;
                                    if (owner.Process.Pid == hostPid && created != 0 && created == hostCreated)
                                        report.TestHostOwners++;
                                    else if (childPid != 0 && childCreated != 0 && owner.Process.Pid == childPid && created == childCreated)
                                        report.OwnedChildOwners++;
                                    // OTHER is another observed identity, never a claim of causality.
                                    else report.OtherOwners++;
                                }
                                report.Incomplete = identityIncomplete || count == 0 || count != expected || needed > count;
                                report.State = report.Incomplete ? "NO_HOLDER_OR_INCOMPLETE" : "OBSERVED";
                            }
                            else report.State = "QUERY_INCOMPLETE";
                            report.Truncated = needed > 32 || count > 32;
                        }
                        else
                        {
                            report.Truncated = needed > 32;
                            report.State = "QUERY_INCOMPLETE";
                        }
                    }
                }
            }
        }
        catch (Exception) { report.State = "HELPER_ERROR"; report.Incomplete = true; }
        finally
        {
            if (started)
            {
                try { report.EndCode = RmEndSession(session); }
                catch (Exception) { report.State = "HELPER_ERROR"; report.Incomplete = true; }
                if (report.EndCode != 0) { report.Incomplete = true; report.State = "QUERY_INCOMPLETE"; }
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }

    private sealed class Report
    {
        public string State { get; set; } = "QUERY_INCOMPLETE";
        public uint? StartCode { get; set; }
        public uint? RegisterCode { get; set; }
        public uint? QueryCode { get; set; }
        public uint? EndCode { get; set; }
        public int QueryCalls { get; set; }
        public int TotalOwners { get; set; }
        public int TestHostOwners { get; set; }
        public int OwnedChildOwners { get; set; }
        public int OtherOwners { get; set; }
        public bool Truncated { get; set; }
        public bool Incomplete { get; set; } = true;
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
}
