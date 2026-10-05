[CmdletBinding()]
param([switch]$DisposableRunner)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if (!$DisposableRunner -or !$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This fixture is restricted to an explicitly selected disposable GitHub-hosted Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator fixture context required.' } }
finally { $identity.Dispose() }
$root = Join-Path $env:ProgramFiles 'AutoVPN'
$serviceDir = Join-Path $root 'Service'
$name = 'AutoVPN.Broker'
$evidence = Join-Path (Get-Location) 'artifacts/v3f-service'
New-Item -ItemType Directory $evidence -Force | Out-Null
$null = & sc.exe query $name 2>&1
if ($LASTEXITCODE -ne 1060 -or (Test-Path -LiteralPath $root)) { throw 'Existing AutoVPN installation must not be touched by this fixture.' }
$users = @(); $createdRoot = $false; $createdService = $false; $success = $false
$checks = @(); $cleanupErrors = @(); $stage = 'PREPARE'
$env:AUTOVPN_DISPOSABLE_SERVICE_LAB = '1'
function Set-ProtectedDirectory([string]$Path) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $admins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $acl.SetOwner($admins)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544','S-1-5-32-545')) {
        $rights = if ($sid -eq 'S-1-5-32-545') { [Security.AccessControl.FileSystemRights]::ReadAndExecute } else { [Security.AccessControl.FileSystemRights]::FullControl }
        $rule = New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), $rights,
            ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Stop-OwnedService {
    $controller = Get-Service -Name $name
    try {
        if ($controller.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
            $controller.Stop()
            $controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(10))
        }
    } finally { $controller.Dispose() }
}
function Delete-OwnedService {
    $null = & sc.exe delete $name 2>&1
    if ($LASTEXITCODE -notin @(0,1060)) { throw 'Owned service deletion failed.' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $null = & sc.exe query $name 2>&1
        if ($LASTEXITCODE -eq 1060) { return }
        if ($clock.Elapsed.TotalSeconds -ge 5) { throw 'Owned service removal not confirmed.' }
        Start-Sleep -Milliseconds 50
    } while ($true)
}
function Invoke-Lab([string]$Verb, $Account) {
    $env:AUTOVPN_LAB_USER = $Account.Name
    $env:AUTOVPN_LAB_PASSWORD = $Account.Password
    $raw = & dotnet $lab $Verb 2>&1
    $code = $LASTEXITCODE
    $raw | Add-Content (Join-Path $evidence 'client.log')
    if ($code -ne 0) { throw "Client assertion failed at $Verb." }
    return ($raw | ConvertFrom-Json)
}
try {
    $stage = 'BUILD'
    dotnet publish src/AutoVpn.Service/AutoVpn.Service.csproj -c Release -r win-x64 --self-contained true -o artifacts/v3f-service/publish *> (Join-Path $evidence 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
    dotnet build tests/AutoVpn.ServiceLab/AutoVpn.ServiceLab.csproj -c Release *> (Join-Path $evidence 'lab-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Service lab build failed.' }
    $lab = (Resolve-Path tests/AutoVpn.ServiceLab/bin/Release/net10.0/AutoVpn.ServiceLab.dll).Path
    $stage = 'ACCOUNTS'
    foreach ($suffix in @('a','b')) {
        $username = 'avlab' + [Guid]::NewGuid().ToString('N').Substring(0,8) + $suffix
        $password = [Guid]::NewGuid().ToString('N') + 'aA9!'
        Write-Host "::add-mask::$password"
        $account = New-LocalUser -Name $username -Password (ConvertTo-SecureString $password -AsPlainText -Force) -Description 'Disposable AutoVPN CI status-only fixture'
        $users += @{Name=$username; Password=$password; Sid=$account.SID.Value}
        $group = Get-LocalGroup -SID 'S-1-5-32-545'
        Add-LocalGroupMember -Group $group -Member $account
    }
    $stage = 'INSTALL_OWNED_CONTROL_PLANE'
    New-Item -ItemType Directory $root | Out-Null; $createdRoot = $true
    Set-ProtectedDirectory $root
    New-Item -ItemType Directory $serviceDir | Out-Null
    Set-ProtectedDirectory $serviceDir
    Copy-Item artifacts/v3f-service/publish/* -Destination $serviceDir -Recurse
    # All copied files are fixture-owned; establish the expected trusted ACL owner explicitly.
    $admins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    Get-ChildItem -LiteralPath $serviceDir -Recurse -Force | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected installation link.' }
        $acl = Get-Acl -LiteralPath $_.FullName; $acl.SetOwner($admins); Set-Acl -LiteralPath $_.FullName -AclObject $acl
    }
    @{schemaVersion=1; ownerSid=$users[0].Sid} | ConvertTo-Json -Compress | Set-Content (Join-Path $serviceDir 'service-owner.json') -Encoding utf8NoBOM
    $acl = Get-Acl -LiteralPath (Join-Path $serviceDir 'service-owner.json'); $acl.SetOwner($admins)
    Set-Acl -LiteralPath (Join-Path $serviceDir 'service-owner.json') -AclObject $acl
    $binary = '"' + (Join-Path $serviceDir 'AutoVpn.Service.exe') + '" --service'
    $controller = New-Service -Name $name -BinaryPathName $binary -StartupType Manual -Description 'Disposable AutoVPN V3F status-only CI fixture'
    $createdService = $true; $controller.Dispose()
    Start-Service -Name $name
    $stage = 'AUTHORIZED_STANDARD_USER'
    $first = Invoke-Lab ready $users[0]; $checks += @{name=$stage; result=$first}
    $stage = 'UNAUTHORIZED_STANDARD_USER'
    $checks += @{name=$stage; result=(Invoke-Lab denied $users[1])}
    $stage = 'MALFORMED_IDLE_AND_UNSUPPORTED_REQUESTS'
    $checks += @{name=$stage; result=(Invoke-Lab negative $users[0])}
    $stage = 'SCM_STOP'
    Stop-OwnedService
    $checks += @{name=$stage; result=(Invoke-Lab stopped $users[0])}
    $stage = 'SCM_RESTART'
    Start-Service -Name $name
    $second = Invoke-Lab ready $users[0]
    if ($first.instanceId -eq $second.instanceId) { throw 'Restart reused old service instance identity.' }
    $checks += @{name=$stage; result=$second; newInstance=$true}
    $stage = 'SCM_REMOVE'
    Stop-OwnedService; Delete-OwnedService; $createdService = $false
    $checks += @{name=$stage; result=(Invoke-Lab missing $users[0])}
    $success = $true
} catch {
    # Keep the failing stage and type; commands above do not emit credentials into shared logs.
    $checks += @{name=$stage; failed=$true; exceptionType=$_.Exception.GetType().Name}
    Write-Host "Service fixture failed at $stage. Inspect retained client/build evidence."
} finally {
    if ($createdService) {
        try { Stop-OwnedService; Delete-OwnedService; $createdService = $false }
        catch { $cleanupErrors += 'SERVICE_REMOVAL_UNCONFIRMED' }
    }
    if ($createdRoot -and !$createdService) {
        try { Remove-Item -LiteralPath $root -Recurse -Force; if (Test-Path -LiteralPath $root) { throw 'Remaining owned path.' } }
        catch { $cleanupErrors += 'INSTALLATION_REMOVAL_UNCONFIRMED' }
    }
    foreach ($account in $users) {
        try {
            $current = Get-LocalUser -Name $account.Name
            if ($current.SID.Value -ne $account.Sid) { throw 'Account identity changed.' }
            Remove-LocalUser -SID $current.SID
        } catch { $cleanupErrors += 'OWNED_ACCOUNT_REMOVAL_UNCONFIRMED' }
    }
    'AUTOVPN_LAB_USER','AUTOVPN_LAB_PASSWORD','AUTOVPN_DISPOSABLE_SERVICE_LAB' | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
    $record = [ordered]@{schemaVersion=1; sourceCommit=(& git rev-parse HEAD); passed=($success -and $cleanupErrors.Count -eq 0);
        scope='Disposable Windows SCM control-plane only; no TUN, WFP, proxy, routes or user catalogue';
        checks=$checks; cleanupErrors=$cleanupErrors; noInstalledServiceRemaining=(!$createdService); noOwnedInstallationRemaining=(!(Test-Path -LiteralPath $root))}
    $record | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $evidence 'summary.json') -Encoding utf8NoBOM
    $record | ConvertTo-Json -Depth 12 | Write-Host
}
if (!$success -or $cleanupErrors.Count -ne 0) { throw 'Installed service fixture acceptance failed; original outcomes retained.' }
