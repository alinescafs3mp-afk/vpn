$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = 'artifacts/v3f'
New-Item -ItemType Directory $root -Force | Out-Null
dotnet --info | Set-Content "$root/dotnet-info.txt"
dotnet build AutoVpn.slnx -c Release --nologo '-p:Platform=Any CPU' *> "$root/build.log"
if ($LASTEXITCODE -ne 0) { Get-Content "$root/build.log"; throw 'Build failed.' }
$core = ./scripts/fetch-core.ps1
$manifest = Get-Content config/core-manifest.json -Raw | ConvertFrom-Json
$asset = if ($IsWindows) { 'mihomo-windows-amd64.exe' } else { 'mihomo-linux-amd64' }
$hash = ($manifest.assets | Where-Object name -eq $asset).sha256
if ((Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -ne $hash) { throw 'Core hash mismatch.' }
$env:AUTOVPN_MIHOMO_PATH = (Resolve-Path -LiteralPath $core).Path
$env:R5_CORE_PATH = $env:AUTOVPN_MIHOMO_PATH; $env:R5_CORE_HASH = $hash
$env:R6_CORE_PATH = $env:AUTOVPN_MIHOMO_PATH; $env:R6_CORE_HASH = $hash
$env:AUTOVPN_TLS_DIAGNOSTICS = '1'
$allowed = if ($IsWindows) {
    @('AutoVpn.UnitTests.AstraV3ProfileTests.NativeRuntimeOwnsLocalPortsThenStopsItsExactProcess',
      'AutoVpn.UnitTests.IndependentRound3Tests.A16_OutputDrainMustContinueAfterItsRetentionCap',
      'AutoVpn.UnitTests.Round2SliceATests.Rt03WorkerCancelCleansCredentialsAndDoesNotKillTheNextProcess')
} else {
    @('AutoVpn.UnitTests.AstraV3DServiceProcessTests.RetainedLiveProcessHandleIsNotSignaled',
      'AutoVpn.UnitTests.AstraV3DServiceProcessTests.Exited259IsNotMistakenForStillActive',
      'AutoVpn.UnitTests.AstraV3DServiceProcessTests.InvalidAndClosedHandlesFailClosed',
      'AutoVpn.UnitTests.AstraV3EResourceTests.LockedCleanupIsReportedAndCanBeRetriedAfterRelease',
      'AutoVpn.UnitTests.AstraV3FServiceAccessTests.OnlyOwnerQueryAndSynchronizeAreAdded',
      'AutoVpn.UnitTests.AstraV3FServiceAccessTests.ExistingOwnerGrantIsIdempotent',
      'AutoVpn.UnitTests.AstraV3FServiceAccessTests.ExplicitDenialIsPreservedBeforeOwnerAllow',
      'AutoVpn.UnitTests.AstraV3FServiceAccessTests.InheritedRulesAndAnotherUserAreNotReplaced',
      'AutoVpn.UnitTests.AstraV3FServiceAccessTests.NullDaclFailsClosed',
      'AutoVpn.UnitTests.AstraV3FServiceAccessTests.WellKnownAndMalformedOwnersCannotReceiveAGrant')
}
$runs = @()
for ($i = 1; $i -le 6; $i++) {
    $dir = "$root/run-$i"
    New-Item -ItemType Directory $dir -Force | Out-Null
    dotnet test tests/AutoVpn.UnitTests/AutoVpn.UnitTests.csproj -c Release --no-build --nologo --logger 'trx;LogFileName=results.trx' --results-directory $dir --blame-hang-timeout 90s --blame-hang-dump-type none *> "$dir/test.log"
    $code = $LASTEXITCODE
    if (!(Test-Path "$dir/results.trx")) { throw "Missing TRX in iteration $i; series incomplete." }
    [xml]$trx = Get-Content "$dir/results.trx" -Raw
    $cases = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    $passed = @($cases | Where-Object outcome -eq 'Passed')
    $failed = @($cases | Where-Object outcome -eq 'Failed')
    $skipped = @($cases | Where-Object outcome -eq 'NotExecuted' | ForEach-Object testName)
    $identities = @($cases | ForEach-Object testId | Sort-Object -Unique)
    $unknown = @($cases | Where-Object { $_.outcome -notin @('Passed','Failed','NotExecuted') })
    $valid = $code -eq 0 -and $cases.Count -eq 530 -and $identities.Count -eq 530 -and $passed.Count -eq (530 - $allowed.Count) -and $failed.Count -eq 0 -and $unknown.Count -eq 0 -and $skipped.Count -eq $allowed.Count -and @($skipped | Where-Object { $_ -notin $allowed }).Count -eq 0
    $runs += [ordered]@{iteration=$i; exitCode=$code; valid=$valid; total=$cases.Count; passed=$passed.Count; failed=$failed.Count; skipped=$skipped; failures=@($failed | ForEach-Object { @{name=$_.testName; message=$_.Output.ErrorInfo.Message; stack=$_.Output.ErrorInfo.StackTrace} })}
    Write-Host "Iteration $i : passed=$($passed.Count) failed=$($failed.Count) total=$($cases.Count) exit=$code valid=$valid"
    [ordered]@{sourceCommit=(& git rev-parse HEAD); sourceTree=(& git rev-parse 'HEAD^{tree}'); coreSha256=$hash; plannedRuns=6; completedRuns=$runs.Count; runs=$runs; note='Full suite. V3F adds 13 output cases and 6 service-process ACL cases. Previous V3E failures remain recorded. No remote TLS retry, timeout or certificate-policy change.'} | ConvertTo-Json -Depth 12 | Set-Content "$root/summary.json" -Encoding utf8NoBOM
}
$bad = @($runs | Where-Object { !$_.valid })
if ($bad.Count -gt 0) { $bad | ConvertTo-Json -Depth 12 | Write-Host; throw "$($bad.Count) of six iterations failed; all outcomes retained." }
Write-Host 'All six planned full-suite iterations passed.'
