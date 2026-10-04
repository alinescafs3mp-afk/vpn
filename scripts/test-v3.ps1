# V3 evidence wrapper. This script does not enable TUN or change Windows networking.
[CmdletBinding()]
param([switch]$Native, [string]$CorePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if ($Native -and -not $CorePath) { $CorePath = $env:AUTOVPN_MIHOMO_PATH }
if ($Native -and -not $CorePath) { throw 'Pass -CorePath for explicitly provisioned native tests.' }
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$run = Join-Path $root ('artifacts/v3-verification/' + $runId)
New-Item -ItemType Directory -Path $run -Force | Out-Null
$results = Join-Path $root 'artifacts/test-results/results.trx'

function Invoke-V3Stage([string]$Name, [scriptblock]$Command) {
    $stage = Join-Path $run $Name
    New-Item -ItemType Directory -Path $stage | Out-Null
    # A build failure must never be attributed an earlier stage's TRX.
    if (Test-Path -LiteralPath $results) {
        if ((Get-Item -LiteralPath $results).LinkType) { throw 'TRX must not be a link.' }
        Copy-Item -LiteralPath $results -Destination (Join-Path $stage 'preexisting-results.trx')
        Remove-Item -LiteralPath $results
    }
    $log = Join-Path $stage 'command.log'
    $ok = $false; $failure = $null; $counters = $null
    try {
        & $Command *>&1 | Tee-Object -FilePath $log | Out-Host
        $ok = $true
    } catch {
        $failure = $_.Exception.Message
        [IO.File]::AppendAllText($log, [Environment]::NewLine + $failure + [Environment]::NewLine)
    }
    if (Test-Path -LiteralPath $results) {
        Copy-Item -LiteralPath $results -Destination (Join-Path $stage 'results.trx')
        try {
            [xml]$trx = Get-Content -LiteralPath $results -Raw
            $values = $trx.TestRun.ResultSummary.Counters
            if (-not $values) { throw 'No TRX counters.' }
            $counters = @{ total=[int]$values.total; executed=[int]$values.executed; passed=[int]$values.passed;
                failed=[int]$values.failed; notExecuted=[int]$values.notExecuted }
            if ($counters.total -le 0 -or $counters.failed -gt 0) { $ok = $false }
        } catch { $ok = $false; $failure = 'TRX is missing valid counters.' }
    } else { $ok = $false; if (-not $failure) { $failure = 'No new TRX was produced.' } }
    $report = [PSCustomObject]@{ stage=$Name; commandSucceeded=$ok; failure=$failure; counters=$counters;
        utc=[DateTime]::UtcNow.ToString('o'); sourceVersion='0.1.3'; windowsNetworking='NOT_RUN' }
    $report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $stage 'summary.json') -Encoding utf8
    return $report
}

$reports = @()
$normal = Invoke-V3Stage 'normal' { & (Join-Path $PSScriptRoot 'test.ps1') }
$reports += $normal
if ($normal.commandSucceeded -and $Native) {
    $reports += Invoke-V3Stage 'native' { & (Join-Path $PSScriptRoot 'test.ps1') -Native -CorePath $CorePath }
}
$reports | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'summary.json') -Encoding utf8
Write-Host "V3 evidence retained: $run"
if (@($reports | Where-Object { -not $_.commandSucceeded }).Count -gt 0) { throw 'V3 build/tests failed; retain all evidence and return failures to Astra.' }
if (-not $Native) { Write-Host 'Native runtime, WPF and Windows networking were not tested by this command.' }
