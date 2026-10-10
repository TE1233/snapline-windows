param([string]$BinaryPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$binary = if ($BinaryPath) { $BinaryPath } else { Join-Path $projectRoot 'dist\Snapline.exe' }
if (!(Test-Path -LiteralPath $binary)) { & (Join-Path $projectRoot 'build.ps1') }
$runRoot = Join-Path $PSScriptRoot ('run-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
foreach ($mode in @('self-test','memo-test','integration-test','shortcut-test','hover-test')) {
    $dataRoot = $runRoot + '-' + $mode
    $testArguments = @(('--' + $mode), '--data-dir', ('"' + $dataRoot + '"'))
    $testProcess = Start-Process -FilePath $binary -ArgumentList $testArguments -WindowStyle Hidden -PassThru
    if (!$testProcess.WaitForExit(45000)) { throw "Test still running: $mode, PID $($testProcess.Id)" }
    $log = if ($mode -eq 'self-test') { 'self-test-results.txt' } elseif ($mode -eq 'memo-test') { 'memo-results.txt' } elseif ($mode -eq 'shortcut-test') { 'shortcut-results.txt' } elseif ($mode -eq 'hover-test') { 'hover-results.txt' } else { 'integration-results.txt' }
    Get-Content -LiteralPath (Join-Path $dataRoot $log) -Encoding UTF8
    if ($testProcess.ExitCode -ne 0) { throw "$mode failed; data: $dataRoot" }
}
& (Join-Path $PSScriptRoot 'hotkey-conflict.ps1') -BinaryPath $binary
Write-Output 'All tests passed.'
