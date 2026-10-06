[CmdletBinding()]
param([switch]$DisposableRunner)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (!$DisposableRunner -or !$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This fixture is restricted to an explicitly selected disposable GitHub-hosted Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if ($identity.IsSystem -or !(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'An ordinary administrator fixture context, not LocalSystem, is required.'
    }
} finally { $identity.Dispose() }

$evidence = Join-Path (Get-Location) 'artifacts/v3f-node-runtime'
New-Item -ItemType Directory $evidence -Force | Out-Null
$labRoot = Join-Path $env:ProgramData ('AutoVPN-NodeRuntime-Lab-' + [Guid]::NewGuid().ToString('N'))
$binaryDirectory = Join-Path $labRoot 'bin'
$workDirectory = Join-Path $labRoot 'work'
$account = $null; $securePassword = $null; $createdRoot = $false
$child = $null; $started = $false; $job = $null; $assigned = $false
$stdoutFile = $null; $stderrFile = $null; $stdoutDrain = $null; $stderrDrain = $null
$success = $false; $cleanupErrors = @(); $checks = @(); $stage = 'PREPARE'
$primaryIdentityVerified = $false; $naturalProcessExit = $false; $naturalJobEmpty = $false
$noOwnedProcessesRemaining = $true; $noOwnedAccountRemaining = $true; $noOwnedProfileRemaining = $true
$forcedJobTermination = $false; $coreHash = $null; $report = $null; $childSessionId = $null; $childExitCode = $null

function Set-LabDirectoryAcl([string]$Path, [string]$UserSid, [switch]$Writable) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544', $UserSid)) {
        $rights = if ($sid -eq $UserSid) {
            if ($Writable) { [Security.AccessControl.FileSystemRights]::Modify } else { [Security.AccessControl.FileSystemRights]::ReadAndExecute }
        } else { [Security.AccessControl.FileSystemRights]::FullControl }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($sid), $rights,
            ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# ProcessStartInfo credentials call CreateProcessWithLogonW with a separate primary token.
# The trusted child waits on stdin until it belongs to this private Job Object. No core can
# escape through the process-start / job-assignment interval. No impersonation is used here.
# https://learn.microsoft.com/dotnet/api/system.diagnostics.process.start
# https://learn.microsoft.com/windows/win32/procthread/job-objects
$launcherSource = @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

public sealed class AutoVpnRuntimeLabJob : IDisposable
{
    private IntPtr handle;
    public AutoVpnRuntimeLabJob()
    {
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) Fail();
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; no breakaway.
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(handle); handle = IntPtr.Zero;
            throw new Win32Exception(error, "OWNED_JOB_SETUP_FAILED");
        }
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) Fail();
    }
    public uint ActiveProcesses
    {
        get
        {
            Accounting accounting;
            if (!QueryInformationJobObject(handle, 1, out accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero)) Fail();
            return accounting.ActiveProcesses;
        }
    }
    public bool WaitEmpty(int milliseconds)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            if (ActiveProcesses == 0) return true;
            if (deadline.ElapsedMilliseconds >= milliseconds) return false;
            Thread.Sleep(25);
        } while (true);
    }
    public void Terminate()
    {
        if (!TerminateJobObject(handle, 5)) Fail();
    }
    public static bool PrimaryIdentityMatches(Process process, string expectedSid)
    {
        IntPtr token;
        // WindowsPrincipal duplicates a primary token for CheckTokenMembership; this never impersonates a thread.
        if (!OpenProcessToken(process.Handle, 8 | 2, out token)) Fail(); // TOKEN_QUERY | TOKEN_DUPLICATE
        try
        {
            int tokenType; int returned;
            if (!GetTokenInformation(token, 8, out tokenType, sizeof(int), out returned)) Fail(); // TokenType
            using (var identity = new WindowsIdentity(token))
            {
                return tokenType == 1 && process.SessionId > 0 && !identity.IsSystem &&
                    identity.User != null && identity.User.Value == expectedSid &&
                    !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        finally { CloseHandle(token); }
    }
    public void Dispose()
    {
        if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; }
    }
    private static void Fail() { throw new Win32Exception(Marshal.GetLastWin32Error(), "OWNED_PROCESS_OPERATION_FAILED"); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out Accounting info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}
'@

try {
    $stage = 'NATIVE_LAUNCHER_BUILD'
    Add-Type -TypeDefinition $launcherSource *> (Join-Path $evidence 'native-launcher-build.log')
    $stage = 'BUILD'
    dotnet publish tests/AutoVpn.ProcessFixture/AutoVpn.ProcessFixture.csproj -c Release -r win-x64 --self-contained true -o (Join-Path $evidence 'publish') *> (Join-Path $evidence 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime fixture publish failed.' }
    $stage = 'PINNED_CORE'
    $core = & (Join-Path $PSScriptRoot '../fetch-core.ps1') -Destination (Join-Path $evidence 'core')
    $manifest = Get-Content config/core-manifest.json -Raw | ConvertFrom-Json
    $coreHash = ($manifest.assets | Where-Object name -eq 'mihomo-windows-amd64.exe').sha256
    if (!$coreHash -or (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -ne $coreHash) { throw 'Pinned core hash mismatch.' }
    $stage = 'OWNED_ACCOUNT_AND_DIRECTORIES'
    $username = 'avnrt' + [Guid]::NewGuid().ToString('N').Substring(0,10)
    $password = [Guid]::NewGuid().ToString('N') + 'aA9!'
    Write-Host "::add-mask::$password"
    $securePassword = ConvertTo-SecureString $password -AsPlainText -Force
    $password = $null
    $account = New-LocalUser -Name $username -Password $securePassword -UserMayNotChangePassword -Description 'Disposable AutoVPN non-TUN runtime CI fixture'
    $noOwnedAccountRemaining = $false
    $noOwnedProfileRemaining = $false
    Add-LocalGroupMember -Group (Get-LocalGroup -SID 'S-1-5-32-545') -Member $account
    if (Test-Path -LiteralPath $labRoot) { throw 'Unexpected existing fixture directory.' }
    New-Item -ItemType Directory $labRoot | Out-Null
    $createdRoot = $true
    Set-LabDirectoryAcl $labRoot $account.SID.Value
    New-Item -ItemType Directory $binaryDirectory, $workDirectory | Out-Null
    Set-LabDirectoryAcl $binaryDirectory $account.SID.Value
    Set-LabDirectoryAcl $workDirectory $account.SID.Value -Writable
    Copy-Item (Join-Path $evidence 'publish/*') -Destination $binaryDirectory -Recurse
    $ownedCore = Join-Path $binaryDirectory 'mihomo-windows-amd64.exe'
    Copy-Item -LiteralPath $core -Destination $ownedCore
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    Get-ChildItem -LiteralPath $binaryDirectory -Recurse -Force | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected executable directory link.' }
        $acl = Get-Acl -LiteralPath $_.FullName
        $acl.SetOwner($admins)
        Set-Acl -LiteralPath $_.FullName -AclObject $acl
    }
    if ((Get-FileHash -LiteralPath $ownedCore -Algorithm SHA256).Hash -ne $coreHash) { throw 'Copied core hash mismatch.' }

    $stage = 'PRIMARY_TOKEN_PROCESS_START'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $binaryDirectory 'AutoVpn.ProcessFixture.exe'
    $start.ArgumentList.Add('node-runtime')
    $start.UseShellExecute = $false
    $start.UserName = $account.Name
    $start.Domain = $env:COMPUTERNAME
    $start.Password = $securePassword
    $start.LoadUserProfile = $false
    $start.WorkingDirectory = $workDirectory
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Supply only this fixture's environment; runner credentials and user settings are not inherited.
    $start.Environment.Clear()
    $childEnvironment = @{
        SystemRoot=$env:SystemRoot; windir=$env:SystemRoot; SystemDrive=$env:SystemDrive;
        PATH=(Join-Path $env:SystemRoot 'System32'); COMSPEC=(Join-Path $env:SystemRoot 'System32/cmd.exe');
        TEMP=$workDirectory; TMP=$workDirectory; USERPROFILE=$workDirectory;
        LOCALAPPDATA=$workDirectory; APPDATA=$workDirectory; USERNAME=$account.Name; USERDOMAIN=$env:COMPUTERNAME;
        AUTOVPN_MIHOMO_PATH=$ownedCore; R6_CORE_HASH=$coreHash;
        AUTOVPN_RUNTIME_LAB_EXPECTED_SID=$account.SID.Value; AUTOVPN_RUNTIME_LAB_START_GATE='1';
        DOTNET_EnableDiagnostics='0'
    }
    foreach ($entry in $childEnvironment.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
    $stdoutFile = [IO.File]::Open((Join-Path $evidence 'fixture.json'), [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $stderrFile = [IO.File]::Open((Join-Path $evidence 'fixture.stderr.log'), [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $job = [AutoVpnRuntimeLabJob]::new()
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    if (!$child.Start()) { throw 'Runtime fixture did not start.' }
    $started = $true; $noOwnedProcessesRemaining = $false
    $stdoutDrain = $child.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
    $stderrDrain = $child.StandardError.BaseStream.CopyToAsync($stderrFile)
    $childSessionId = $child.SessionId
    $primaryIdentityVerified = [AutoVpnRuntimeLabJob]::PrimaryIdentityMatches($child, $account.SID.Value)
    if (!$primaryIdentityVerified) { throw 'The actual process primary token or session is not an accepted standard user.' }
    $job.Assign($child); $assigned = $true
    $child.StandardInput.WriteLine('START')
    $child.StandardInput.Flush()
    $child.StandardInput.Close()

    $stage = 'SIX_NATIVE_PROTOCOL_STARTS_AND_STOPS'
    # Six 30-second cases may each need the runtime's separate bounded Stop cleanup.
    # Retain the complete failing report before enforcing this outer five-minute ceiling.
    if (!$child.WaitForExit(300000)) { throw 'Runtime fixture exceeded its absolute process deadline.' }
    $naturalProcessExit = $true
    $childExitCode = $child.ExitCode
    if (!$job.WaitEmpty(5000)) { throw 'A fixture child process remained after its parent exited.' }
    $naturalJobEmpty = $true
    if (![Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdoutDrain, $stderrDrain)).Wait(5000)) { throw 'Runtime fixture output did not reach EOF.' }
    $stdoutFile.Flush(); $stderrFile.Flush()
    if ($stdoutFile.Length -gt 1MB -or $stderrFile.Length -ne 0) { throw 'Unexpected fixture output.' }
    $stdoutFile.Dispose(); $stdoutFile = $null
    $stderrFile.Dispose(); $stderrFile = $null
    $raw = [IO.File]::ReadAllText((Join-Path $evidence 'fixture.json'), [Text.UTF8Encoding]::new($false, $true))
    $report = $raw | ConvertFrom-Json
    $protocols = @('Vless', 'Vmess', 'Trojan', 'Shadowsocks', 'Hysteria2', 'Tuic')
    $observed = @($report.cases | ForEach-Object protocol)
    if ($child.ExitCode -ne 0 -or $report.passed -isnot [bool] -or !$report.passed -or
        $report.standardUser -isnot [bool] -or !$report.standardUser -or
        $report.sessionId -le 0 -or $report.sessionId -ne $childSessionId -or
        $observed.Count -ne 6 -or @($observed | Sort-Object -Unique).Count -ne 6 -or
        @($observed | Where-Object { $_ -cnotin $protocols }).Count -ne 0 -or
        @($report.cases | Where-Object { $_.passed -isnot [bool] -or !$_.passed }).Count -ne 0) {
        throw 'The retained six-protocol report did not satisfy the native standard-user contract.'
    }
    $checks += @{name=$stage; result=$report}
    $success = $true
} catch {
    $failure = $_.Exception
    while ($null -ne $failure.InnerException) { $failure = $failure.InnerException }
    $checks += @{name=$stage; failed=$true; exceptionType=$failure.GetType().Name; hResult=$failure.HResult;
        win32Error=$(if ($failure -is [ComponentModel.Win32Exception]) { $failure.NativeErrorCode } else { $null })}
    Write-Host "Node runtime fixture failed at $stage. Inspect retained build and fixture evidence."
} finally {
    if ($started) {
        try {
            if ($assigned) {
                if ($job.ActiveProcesses -ne 0) { $job.Terminate(); $forcedJobTermination = $true }
                if (!$job.WaitEmpty(10000)) { throw 'Owned job processes remain.' }
            } elseif (!$child.HasExited) {
                # The stdin gate is still closed, so this exact retained process has not started a core.
                $child.Kill($true)
            }
            if (!$child.WaitForExit(10000)) { throw 'Owned fixture process remains.' }
            $childExitCode = $child.ExitCode
            $noOwnedProcessesRemaining = $true
        } catch { $cleanupErrors += 'OWNED_PROCESS_REMOVAL_UNCONFIRMED' }
    }
    foreach ($drain in @($stdoutDrain, $stderrDrain)) {
        if ($null -ne $drain) {
            try { if (!$drain.Wait(5000)) { throw 'Owned output drain remains.' } }
            catch { $cleanupErrors += 'OWNED_OUTPUT_DRAIN_UNCONFIRMED' }
        }
    }
    foreach ($resource in @($child, $job, $stdoutFile, $stderrFile)) {
        if ($null -ne $resource) {
            try { $resource.Dispose() }
            catch { $cleanupErrors += 'OWNED_HANDLE_RELEASE_UNCONFIRMED' }
        }
    }
    if ($createdRoot -and $noOwnedProcessesRemaining) {
        try {
            if ((Get-Item -LiteralPath $labRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Owned root changed into a link.' }
            Remove-Item -LiteralPath $labRoot -Recurse -Force
            if (Test-Path -LiteralPath $labRoot) { throw 'Owned fixture files remain.' }
        } catch { $cleanupErrors += 'OWNED_DIRECTORY_REMOVAL_UNCONFIRMED' }
    }
    if ($null -ne $account) {
        try {
            $profiles = @(Get-CimInstance Win32_UserProfile -Filter ("SID='" + $account.SID.Value + "'"))
            $noOwnedProfileRemaining = $profiles.Count -eq 0
            foreach ($profile in $profiles) {
                if ($profile.Loaded) { throw 'Owned account profile is still loaded.' }
                $profile | Remove-CimInstance
            }
            if (@(Get-CimInstance Win32_UserProfile -Filter ("SID='" + $account.SID.Value + "'")).Count -ne 0) { throw 'Owned account profile remains.' }
            $noOwnedProfileRemaining = $true
        } catch { $cleanupErrors += 'OWNED_PROFILE_REMOVAL_UNCONFIRMED' }
        try {
            $current = @(Get-LocalUser | Where-Object { $_.SID.Value -eq $account.SID.Value })
            if ($current.Count -ne 1 -or $current[0].Name -cne $account.Name) { throw 'Owned account identity changed.' }
            Remove-LocalUser -SID $account.SID
            if (@(Get-LocalUser | Where-Object { $_.SID.Value -eq $account.SID.Value }).Count -ne 0) { throw 'Owned account remains.' }
            $noOwnedAccountRemaining = $true
        } catch { $cleanupErrors += 'OWNED_ACCOUNT_REMOVAL_UNCONFIRMED' }
    }
    if ($null -ne $securePassword) { $securePassword.Dispose() }
    $record = [ordered]@{
        schemaVersion=1; sourceCommit=(& git rev-parse HEAD); sourceTree=(& git rev-parse 'HEAD^{tree}');
        coreSha256=$coreHash; runnerImage=$env:ImageOS; runnerImageVersion=$env:ImageVersion;
        passed=($success -and $cleanupErrors.Count -eq 0);
        scope='GitHub-hosted Windows Server, actual standard-user primary token, six synthetic non-TUN runtime starts and cleanup; no remote connectivity, installation or Windows 11 acceptance';
        primaryIdentityVerified=$primaryIdentityVerified; childSessionId=$childSessionId; childExitCode=$childExitCode;
        naturalProcessExit=$naturalProcessExit; naturalJobEmpty=$naturalJobEmpty;
        forcedJobTermination=$forcedJobTermination; noOwnedProcessesRemaining=$noOwnedProcessesRemaining;
        noOwnedDirectoryRemaining=(!(Test-Path -LiteralPath $labRoot)); noOwnedAccountRemaining=$noOwnedAccountRemaining;
        noOwnedProfileRemaining=$noOwnedProfileRemaining; checks=$checks; cleanupErrors=$cleanupErrors
    }
    $record | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $evidence 'summary.json') -Encoding utf8NoBOM
    $record | ConvertTo-Json -Depth 12 | Write-Host
}
if (!$success -or $cleanupErrors.Count -ne 0) { throw 'Standard-user node runtime acceptance failed; original outcomes retained.' }
