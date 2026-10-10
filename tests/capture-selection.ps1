param([string]$BinaryPath,[switch]$CustomShortcut)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;using System.Text;
public static class SnaplineSelectionQA {
 public delegate bool Callback(IntPtr h,IntPtr state);
 [DllImport("user32.dll")]public static extern bool EnumWindows(Callback cb,IntPtr state);
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]public static extern uint GetClipboardSequenceNumber();
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern bool GetCursorPos(out Point p);
 [DllImport("user32.dll")]public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")]public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")]public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 [DllImport("user32.dll")]public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
 [StructLayout(LayoutKind.Sequential)]public struct Point{public int X,Y;}
 [StructLayout(LayoutKind.Sequential)]public struct Rect{public int Left,Top,Right,Bottom;}
}
"@
$priorDpi=[SnaplineSelectionQA]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
Add-Type -AssemblyName System.Windows.Forms,System.Drawing,PresentationCore,PresentationFramework,WindowsBase
$projectRoot=Split-Path -Parent $PSScriptRoot
$binary=if($BinaryPath){$BinaryPath}else{Join-Path $projectRoot 'dist\Snapline.exe'}
$captureKey=if($CustomShortcut){0x7A}else{0x42}
$captureLabel=if($CustomShortcut){'Ctrl+F11 (custom)'}else{'Ctrl+B'}
$dataRoot=Join-Path $PSScriptRoot ('selection-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $dataRoot|Out-Null
$backup=[Windows.DataObject]::new()
$previous=[Windows.Clipboard]::GetDataObject()
$backupEmpty=$null -eq $previous -or $previous.GetFormats($false).Length -eq 0
if($null -ne $previous){foreach($format in $previous.GetFormats($false)){
 $value=$previous.GetData($format,$false);if($null -eq $value){throw ('Cannot preserve clipboard format: '+$format)}
 if($value -is [IO.MemoryStream]){$value=[IO.MemoryStream]::new($value.ToArray())}
 if($value -is [Windows.Media.Imaging.BitmapSource]){$value=$value.Clone();$value.Freeze()}
 $backup.SetData($format,$value,$false)
}}
$lastOwnedSequence=0
$cursor=[SnaplineSelectionQA+Point]::new();[void][SnaplineSelectionQA]::GetCursorPos([ref]$cursor)
$lastPointer=[SnaplineSelectionQA+Point]::new()
$report=[Collections.Generic.List[string]]::new()
$fixture=[Windows.Forms.Form]::new();$fixture.FormBorderStyle='None';$fixture.AutoScaleMode='None';$fixture.StartPosition='Manual';$fixture.Bounds=[Drawing.Rectangle]::new(220,520,500,320)
$fixture.Text='Snapline screenshot test fixture';$fixture.BackColor=[Drawing.Color]::FromArgb(49,91,117);$fixture.TopMost=$true
$label=[Windows.Forms.Label]::new();$label.Text='Snapline capture verification';$label.ForeColor=[Drawing.Color]::White;$label.AutoSize=$true;$label.Location=[Drawing.Point]::new(20,20);$label.Font=[Drawing.Font]::new('Segoe UI',16);$fixture.Controls.Add($label)
$appProcess=$null
function Pump([int]$milliseconds){$until=[DateTime]::UtcNow.AddMilliseconds($milliseconds);while([DateTime]::UtcNow -lt $until){[Windows.Forms.Application]::DoEvents();Start-Sleep -Milliseconds 30}}
function Find-Overlay {
 $script:overlay=[IntPtr]::Zero
 [void][SnaplineSelectionQA]::EnumWindows({param($h,$state)
  [uint32]$windowProcess=0;[void][SnaplineSelectionQA]::GetWindowThreadProcessId($h,[ref]$windowProcess)
  if($windowProcess -eq $script:appProcess.Id){$title=[Text.StringBuilder]::new(256);[void][SnaplineSelectionQA]::GetWindowText($h,$title,256);if($title.ToString() -eq '截图晾台 · 框选截图'){$script:overlay=$h}}
  return $true
 },[IntPtr]::Zero)
 return $script:overlay
}
function Wait-Overlay {
 $until=[DateTime]::UtcNow.AddSeconds(5)
 do{Pump 60;$found=Find-Overlay;if($found -ne [IntPtr]::Zero){return $found}}while([DateTime]::UtcNow -lt $until)
 throw 'Capture overlay did not appear.'
}
function Set-TestPointer([int]$x,[int]$y){[void][SnaplineSelectionQA]::SetCursorPos($x,$y);$script:lastPointer.X=$x;$script:lastPointer.Y=$y}
function Get-Items {
 $path=Join-Path $dataRoot 'items.json'
 if(Test-Path -LiteralPath $path){return @([IO.File]::ReadAllText($path)|ConvertFrom-Json)}
 return @()
}
function Set-ExternalImage {
 $bitmap=[Drawing.Bitmap]::new(32,24)
 try{$g=[Drawing.Graphics]::FromImage($bitmap);try{$g.Clear([Drawing.Color]::Orange)}finally{$g.Dispose()};[Windows.Forms.Clipboard]::SetImage($bitmap)}finally{$bitmap.Dispose()}
}
try{
 $fixture.Show();$fixture.Activate();Pump 200
 $diag=Join-Path $dataRoot 'diagnostics.txt'
 $settings=@{ClipboardImages=$true;ScreenshotFolder=$true;Folders=@();WelcomeShown=$true;CaptureShortcut=$(if($CustomShortcut){'Custom'}else{'CtrlB'});CaptureShortcutRevision=1;CustomCaptureModifiers=2;CustomCaptureKey=$captureKey}
 [IO.File]::WriteAllText((Join-Path $dataRoot 'settings.json'),($settings|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
 $appProcess=Start-Process -FilePath $binary -ArgumentList @('--data-dir',('"'+$dataRoot+'"'),'--diagnostics',('"'+$diag+'"')) -WindowStyle Hidden -PassThru
 $startupDeadline=[DateTime]::UtcNow.AddSeconds(8)
 while(!(Test-Path -LiteralPath $diag) -and !$appProcess.HasExited -and [DateTime]::UtcNow -lt $startupDeadline){Pump 100}
 if(!(Test-Path -LiteralPath $diag)){throw 'Startup diagnostics were not produced.'}
 $diagnostics=[IO.File]::ReadAllText($diag)
 if(!$diagnostics.Contains('captureHotkey=True') -or !$diagnostics.Contains('clipboardListener=False')){throw 'Unexpected capture/listener state.'}
 $main=[IntPtr]::new([long]([regex]::Match($diagnostics,'windowHandle=(\d+)').Groups[1].Value))
 $fixture.Activate();Pump 200
 [SnaplineSelectionQA]::keybd_event(0x11,0,0,[UIntPtr]::Zero);[SnaplineSelectionQA]::keybd_event($captureKey,0,0,[UIntPtr]::Zero)
 Pump 80
 [SnaplineSelectionQA]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
 Pump 350
 if((Find-Overlay) -ne [IntPtr]::Zero){throw 'Overlay opened before trigger key was released.'}
 $report.Add('PASS: capture waits for the actual trigger key after Ctrl is released')
 [SnaplineSelectionQA]::keybd_event($captureKey,0,2,[UIntPtr]::Zero)
 $overlay=Wait-Overlay
 $report.Add('PASS: real '+$captureLabel+' keyboard input opens the built-in selection surface')
 Set-ExternalImage;$lastOwnedSequence=[SnaplineSelectionQA]::GetClipboardSequenceNumber();Pump 300
 if(@(Get-Items).Count -ne 0){throw 'External clipboard image was collected during actual selection.'}
 $report.Add('PASS: competing clipboard image during the real selection is ignored')
 $bounds=[SnaplineSelectionQA+Rect]::new();[void][SnaplineSelectionQA]::GetWindowRect($fixture.Handle,[ref]$bounds)
 $x=$bounds.Left+60;$y=$bounds.Top+100
 Set-TestPointer $x $y
 $captureBounds=[SnaplineSelectionQA+Rect]::new();[void][SnaplineSelectionQA]::GetWindowRect($overlay,[ref]$captureBounds)
 $localX=$x-$captureBounds.Left;$localY=$y-$captureBounds.Top
 [void][SnaplineSelectionQA]::PostMessage($overlay,0x201,[IntPtr]::new(1),[IntPtr]::new(($localY -shl 16)-bor $localX));Pump 100
 Set-TestPointer ($x+120) ($y+80)
 [void][SnaplineSelectionQA]::PostMessage($overlay,0x200,[IntPtr]::new(1),[IntPtr]::new((($localY+80) -shl 16)-bor ($localX+120)));Pump 250
 $ui=[Drawing.Bitmap]::new(500,320)
 try{$g=[Drawing.Graphics]::FromImage($ui);try{$g.CopyFromScreen($bounds.Left,$bounds.Top,0,0,$ui.Size)}finally{$g.Dispose()};$ui.Save((Join-Path $dataRoot 'selection-ui.png'),[Drawing.Imaging.ImageFormat]::Png)}finally{$ui.Dispose()}
 [void][SnaplineSelectionQA]::PostMessage($overlay,0x202,[IntPtr]::Zero,[IntPtr]::new((($localY+80) -shl 16)-bor ($localX+120)))
 Pump 1400
 $lastOwnedSequence=[SnaplineSelectionQA]::GetClipboardSequenceNumber()
 $items=@(Get-Items);if($items.Count -ne 1){throw ('Actual selection did not produce one card: '+$items.Count)}
 $result=[Drawing.Bitmap]::new((Join-Path (Join-Path $dataRoot 'Inbox') $items[0].File))
 try{
  if($result.Width -ne 120 -or $result.Height -ne 80){throw ('Unexpected crop size: '+$result.Width+'x'+$result.Height)}
  $expected=[Drawing.Color]::FromArgb(49,91,117).ToArgb()
  foreach($sample in @(@(1,1),@(60,40),@(118,78))){if($result.GetPixel($sample[0],$sample[1]).ToArgb() -ne $expected){throw 'Captured pixels include shade or selection UI.'}}
 }finally{$result.Dispose()}
 $report.Add('PASS: real selection mouse messages save exactly 120x80 original-color pixels, without capture UI or shade')
 if(![Windows.Forms.Clipboard]::ContainsImage()){throw 'Selected image was not copied.'}
 $report.Add('PASS: completed capture is copied to the clipboard')
 [void][SnaplineSelectionQA]::PostMessage($main,0x312,[IntPtr]::new(2),[IntPtr]::Zero)
 $overlay=Wait-Overlay
 Set-ExternalImage;$lastOwnedSequence=[SnaplineSelectionQA]::GetClipboardSequenceNumber();Pump 100
 [SnaplineSelectionQA]::keybd_event(0x1B,0,0,[UIntPtr]::Zero);Pump 80;[SnaplineSelectionQA]::keybd_event(0x1B,0,2,[UIntPtr]::Zero)
 Pump 400
 if((Find-Overlay) -ne [IntPtr]::Zero){throw 'Esc did not dismiss actual capture overlay.'}
 if(@(Get-Items).Count -ne 1){throw 'Cancelled actual capture added an image.'}
 $external=[Windows.Forms.Clipboard]::GetImage();try{if($external.Width -ne 32 -or $external.Height -ne 24){throw 'Cancellation overwrote clipboard contents.'}}finally{$external.Dispose()}
 $report.Add('PASS: real Esc cancels selection without collecting or overwriting the competing clipboard image')
 Set-ExternalImage;$lastOwnedSequence=[SnaplineSelectionQA]::GetClipboardSequenceNumber();Pump 500
 if(@(Get-Items).Count -ne 1){throw 'External image after actual cancellation was collected.'}
 $report.Add('PASS: external image copied after actual cancellation is ignored')
 [void][SnaplineSelectionQA]::PostMessage($main,0x312,[IntPtr]::new(2),[IntPtr]::Zero)
 $overlay=Wait-Overlay;Set-TestPointer $x $y
 [SnaplineSelectionQA]::mouse_event(8,0,0,0,[UIntPtr]::Zero);[SnaplineSelectionQA]::mouse_event(16,0,0,0,[UIntPtr]::Zero);Pump 400
 if((Find-Overlay) -ne [IntPtr]::Zero -or @(Get-Items).Count -ne 1){throw 'Right-click did not cancel cleanly.'}
 $report.Add('PASS: real right-click cancels selection without adding an image')
}catch{
 $report.Add('FAIL: '+$_.Exception.Message)
 throw
}finally{
 [SnaplineSelectionQA]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 [SnaplineSelectionQA]::keybd_event($captureKey,0,2,[UIntPtr]::Zero);[SnaplineSelectionQA]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
 if($null -ne $appProcess -and !$appProcess.HasExited){
  $active=Find-Overlay;if($active -ne [IntPtr]::Zero){[void][SnaplineSelectionQA]::PostMessage($active,0x10,[IntPtr]::Zero,[IntPtr]::Zero);Pump 200}
  if($null -ne $main){[void][SnaplineSelectionQA]::PostMessage($main,0x10,[IntPtr]::Zero,[IntPtr]::Zero)}
  if(!$appProcess.WaitForExit(4000)){$report.Add('FAIL: selection test instance did not exit gracefully')}
 }
 $fixture.Close();$fixture.Dispose();$label.Font.Dispose()
 if([SnaplineSelectionQA]::GetClipboardSequenceNumber() -eq $lastOwnedSequence -or [Windows.Clipboard]::IsCurrent($backup)){
  if($backupEmpty){[Windows.Clipboard]::Clear()}else{[Windows.Clipboard]::SetDataObject($backup,$false);Pump 150;[Windows.Clipboard]::Flush()}
  $report.Add('PASS: previous clipboard restored')
 }else{$report.Add('NOTE: clipboard changed externally; newer contents preserved')}
 $current=[SnaplineSelectionQA+Point]::new();[void][SnaplineSelectionQA]::GetCursorPos([ref]$current)
 if($current.X -eq $lastPointer.X -and $current.Y -eq $lastPointer.Y){[void][SnaplineSelectionQA]::SetCursorPos($cursor.X,$cursor.Y)}
 [void][SnaplineSelectionQA]::SetThreadDpiAwarenessContext($priorDpi)
 [IO.File]::WriteAllLines((Join-Path $dataRoot 'selection-results.txt'),$report,[Text.UTF8Encoding]::new($true))
}
$report
'Actual selection test data: '+$dataRoot