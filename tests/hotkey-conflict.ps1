param([string]$BinaryPath)
$ErrorActionPreference = 'Stop'
$taskProject = Split-Path -Parent $PSScriptRoot
$taskBinary = if ($BinaryPath) { $BinaryPath } else { Join-Path $taskProject 'dist\Snapline.exe' }
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class SnaplineHotkeyConflict {
 [DllImport("user32.dll",SetLastError=true)] public static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint key);
 [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h,int id);
}
"@
$taskRunBase = Join-Path $PSScriptRoot ('shortcut-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$taskResults = [Collections.Generic.List[string]]::new()
function Invoke-SnaplineShortcutCase([string]$caseName,[string]$mode,[bool]$expected) {
 $caseRoot = $taskRunBase + '-' + $caseName
 New-Item -ItemType Directory -Path $caseRoot | Out-Null
 $caseSettings = @{ ClipboardImages=$false; ScreenshotFolder=$false; Folders=@(); WelcomeShown=$true; CaptureShortcut=$mode; CaptureShortcutRevision=1 }
 [IO.File]::WriteAllText((Join-Path $caseRoot 'settings.json'), ($caseSettings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
 $caseDiagnostics = Join-Path $caseRoot 'diagnostics.txt'
 $caseArguments = @('--no-capture','--data-dir',('"' + $caseRoot + '"'),'--diagnostics',('"' + $caseDiagnostics + '"'),'--quit-after','2')
 $caseProcess = Start-Process -FilePath $taskBinary -ArgumentList $caseArguments -WindowStyle Hidden -PassThru
 if (!$caseProcess.WaitForExit(6000)) { throw "Test instance did not exit: $caseName" }
 if ($caseProcess.ExitCode -ne 0) { throw "Startup failed: $caseName" }
 $caseText = [IO.File]::ReadAllText($caseDiagnostics)
 if (!$caseText.Contains('captureHotkey=' + $expected.ToString())) { throw "Unexpected registration result: $caseName" }
 $caseLabel = if ($mode -eq 'None') { '快捷键已关闭' } elseif ($mode -eq 'ShiftPrintScreen') { 'Shift + PrtSc' } elseif ($mode -eq 'CtrlPrintScreen') { 'Ctrl + PrtSc' } else { 'Ctrl + B' }
 if (!$caseText.Contains('captureShortcut=' + $caseLabel)) { throw "Shortcut preference was not loaded: $caseName" }
 $taskResults.Add('PASS: ' + $caseName)
}
if (![SnaplineHotkeyConflict]::RegisterHotKey([IntPtr]::Zero,2301,0x4002,0x42)) { throw 'Ctrl+B already occupied before conflict test.' }
try { Invoke-SnaplineShortcutCase 'occupied Ctrl+B is not taken over' 'CtrlB' $false }
finally { [SnaplineHotkeyConflict]::UnregisterHotKey([IntPtr]::Zero,2301) | Out-Null }
Invoke-SnaplineShortcutCase 'Ctrl+B registers after competing owner releases it' 'CtrlB' $true
Invoke-SnaplineShortcutCase 'alternative Ctrl+PrtSc persists and registers' 'CtrlPrintScreen' $true
Invoke-SnaplineShortcutCase 'alternative Shift+PrtSc registers' 'ShiftPrintScreen' $true
Invoke-SnaplineShortcutCase 'disabled shortcut does not register' 'None' $false
$taskResults | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'hotkey-results.txt') -Encoding UTF8
$taskResults