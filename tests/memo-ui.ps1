param([string]$DataRoot)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { throw 'Run with Windows PowerShell -STA.' }
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,PresentationCore,WindowsBase
Add-Type -TypeDefinition @"
using System;using System.Text;using System.Runtime.InteropServices;
public static class MemoUIQA {
 public delegate bool Callback(IntPtr h,IntPtr s);
 [DllImport("user32.dll")]public static extern bool EnumWindows(Callback cb,IntPtr s);
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
 [DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")]public static extern bool IsWindowEnabled(IntPtr h);
 [DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern bool GetCursorPos(out Point p);
 [DllImport("user32.dll")]public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")]public static extern bool ScreenToClient(IntPtr h,ref Point p);
 [DllImport("user32.dll")]public static extern IntPtr GetDlgItem(IntPtr h,int id);
 public struct Point {public int X,Y;}
}
"@
[void][MemoUIQA]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$DataRoot) { $DataRoot=Join-Path $PSScriptRoot ('memo-actions-'+[Guid]::NewGuid().ToString('N').Substring(0,8)) }
if (Test-Path $DataRoot) { throw 'UI QA requires a new isolated directory.' }
New-Item -ItemType Directory -Path $DataRoot | Out-Null
$report=[Collections.Generic.List[string]]::new()
$originalPointer=[MemoUIQA+Point]::new();[void][MemoUIQA]::GetCursorPos([ref]$originalPointer)
$ownedPointer=$null;$process=$null;$main=[IntPtr]::Zero
function Own-Windows {
 $script:ownWindows=[Collections.Generic.List[IntPtr]]::new()
 [void][MemoUIQA]::EnumWindows({param($h,$s) $id=0;[void][MemoUIQA]::GetWindowThreadProcessId($h,[ref]$id);if($id -eq $process.Id -and [MemoUIQA]::IsWindowVisible($h)){$script:ownWindows.Add($h)};return $true},[IntPtr]::Zero)
 return $script:ownWindows.ToArray()
}
function Find-ID($handle,$id) { $root=[Windows.Automation.AutomationElement]::FromHandle($handle);$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)) }
function Find-Name($handle,$name) { $root=[Windows.Automation.AutomationElement]::FromHandle($handle);$root.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$name)) }
function Own-Name($name) { foreach($h in (Own-Windows)){$root=[Windows.Automation.AutomationElement]::FromHandle($h);if($root.Current.Name -eq $name){return $root};$found=Find-Name $h $name;if($null -ne $found){return $found}} }
function Invoke-UI($element) { if($null -eq $element){throw 'Missing UI control.'};([Windows.Automation.InvokePattern]$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Note-Menu($text) {
 $body=Find-Name $main $text;if($null -eq $body){throw 'Note card missing.'}
 $b=$body.Current.BoundingRectangle;$x=[int]($b.X+$b.Width/2);$y=[int]($b.Y+$b.Height/2)
 [void][MemoUIQA]::SetCursorPos($x,$y);$script:ownedPointer=[MemoUIQA+Point]@{X=$x;Y=$y}
  $clientPoint=[MemoUIQA+Point]@{X=$x;Y=$y};[void][MemoUIQA]::ScreenToClient($main,[ref]$clientPoint)
 $l=[IntPtr]::new(($clientPoint.Y -shl 16)-bor $clientPoint.X)
 [void][MemoUIQA]::PostMessage($main,0x200,[IntPtr]::Zero,$l)
 [void][MemoUIQA]::PostMessage($main,0x204,[IntPtr]::new(2),$l)
 [void][MemoUIQA]::PostMessage($main,0x205,[IntPtr]::Zero,$l)
 Start-Sleep -Milliseconds 180
}
try {
 $diag=Join-Path $DataRoot 'diagnostics.txt'
 $process=Start-Process -FilePath (Join-Path $projectRoot 'dist\Snapline.exe') -ArgumentList @('--demo','--demo-notes','--no-capture','--data-dir',('"'+$DataRoot+'"'),'--diagnostics',('"'+$diag+'"')) -WindowStyle Hidden -PassThru
 for($i=0;$i -lt 50 -and !(Test-Path $diag);$i++){Start-Sleep -Milliseconds 100}
 $details=Get-Content $diag | ConvertFrom-StringData;$main=[IntPtr]::new([long]$details.windowHandle)
 $notesPath=Join-Path $DataRoot 'notes.json';$before=Get-Content $notesPath -Raw -Encoding UTF8 | ConvertFrom-Json
 Invoke-UI (Find-ID $main 'NewNote');Start-Sleep -Milliseconds 120
 $editor=Own-Name '编辑便签 · 截图晾台';$editorHandle=[IntPtr]::new($editor.Current.NativeWindowHandle)
 $expected="只属于测试的便签 📷`n第二行：文字按原样保存。"
 ([Windows.Automation.ValuePattern](Find-ID $editorHandle 'NoteText').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)).SetValue($expected)
 Invoke-UI (Find-ID $editorHandle 'NoteColor-Lavender');Invoke-UI (Find-ID $editorHandle 'NoteDone');Start-Sleep -Milliseconds 100
 for($wait=0;$wait -lt 30;$wait++){
  $saved=Get-Content $notesPath -Raw -Encoding UTF8 | ConvertFrom-Json;$new=$saved | Where-Object Text -eq $expected
  if($saved.Count -eq $before.Count+1 -and $new.Color -eq 'Lavender'){break};Start-Sleep -Milliseconds 100
 }
 if($saved.Count -ne $before.Count+1 -or $new.Color -ne 'Lavender'){throw 'Done failed to save latest text/color.'}
 for($wait=0;$wait -lt 40;$wait++){
  if([MemoUIQA]::IsWindowEnabled($main) -and $null -eq (Own-Name '编辑便签 · 截图晾台')){break};Start-Sleep -Milliseconds 100
 }
 if(![MemoUIQA]::IsWindowEnabled($main)){throw 'Editor did not close.'}
 $report.Add('PASS: New note, Unicode input, paper color and immediate Done save through actual UI controls')
 foreach($answer in @(7,6)) {
  Note-Menu $expected
  if($null -eq (Own-Name '复制文字')){
   foreach($wh in (Own-Windows)){$root=[Windows.Automation.AutomationElement]::FromHandle($wh);$root.Current.Name | Out-Host;$root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object {$_.Current.Name} | Out-Host}
   throw 'Copy menu missing.'
  }
  $delete=Own-Name '删除便签…'
  if($null -eq $delete){foreach($wh in (Own-Windows)){$root=[Windows.Automation.AutomationElement]::FromHandle($wh);$root.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) | ForEach-Object {$_.Current.Name} | Out-Host};throw 'Delete menu item missing'}
  Invoke-UI $delete;Start-Sleep -Milliseconds 250
  $dialog=Own-Name '删除便签';if($null -eq $dialog){throw 'Deletion confirmation missing.'}
  $choice=[MemoUIQA]::GetDlgItem([IntPtr]::new($dialog.Current.NativeWindowHandle),$answer)
  if($choice -eq [IntPtr]::Zero){throw 'Confirmation button missing.'}
  [void][MemoUIQA]::PostMessage($choice,0xF5,[IntPtr]::Zero,[IntPtr]::Zero);Start-Sleep -Milliseconds 120
  $current=Get-Content $notesPath -Raw -Encoding UTF8 | ConvertFrom-Json
  if($answer -eq 7 -and $current.Count -ne $saved.Count){throw 'Cancelled deletion removed a note.'}
  if($answer -eq 6 -and ($current.Count -ne $before.Count -or @($current | Where-Object Id -eq $new.Id).Count -ne 0)){throw 'Confirmed deletion did not remove only chosen note.'}
 }
 foreach($prior in $before){$after=$current | Where-Object Id -eq $prior.Id;if($after.Text -ne $prior.Text -or $after.Color -ne $prior.Color){throw 'Another note changed during deletion.'}}
 $report.Add('PASS: Delete asks for confirmation; No preserves note; Yes deletes only selected note')
} catch {
 $testFailure=$_;$report.Add('FAIL: '+$_.Exception.Message)
} finally {
 if($null -ne $process -and !$process.HasExited -and $main -ne [IntPtr]::Zero){[void][MemoUIQA]::PostMessage($main,0x10,[IntPtr]::Zero,[IntPtr]::Zero);if(!$process.WaitForExit(5000)){throw 'QA process did not close normally.'}}
 if($null -ne $ownedPointer){$now=[MemoUIQA+Point]::new();[void][MemoUIQA]::GetCursorPos([ref]$now);if($now.X -eq $ownedPointer.X -and $now.Y -eq $ownedPointer.Y){[void][MemoUIQA]::SetCursorPos($originalPointer.X,$originalPointer.Y)}}
 $report | Set-Content (Join-Path $DataRoot 'memo-ui-results.txt') -Encoding UTF8
}
$report
if($testFailure){throw $testFailure}
