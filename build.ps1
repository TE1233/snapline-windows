param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$csc = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $csc)) { throw '需要 Windows 自带的 .NET Framework 4.8。' }
$dist = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'dist' }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
# Original icon: a mint-colored screenshot card clipped to a charcoal line.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(26,35,49))
$mint = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(168,216,200))
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(236,242,245))
$dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(49,91,117))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(154,171,179)),2
$graphics.DrawLine($pen,4,15,60,15)
$graphics.FillRectangle($white,12,23,40,30)
$graphics.FillRectangle($dark,17,28,30,20)
$graphics.FillRectangle($mint,28,10,8,21)
$graphics.FillEllipse($mint,22,34,8,8)
$iconHandle = $bitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($iconHandle)
$iconFile = Join-Path $projectRoot 'snapline.ico'
$iconStream = [System.IO.File]::Create($iconFile)
$icon.Save($iconStream)
$iconStream.Dispose()
$icon.Dispose()
Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class IconCleanup { [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h); }'
[IconCleanup]::DestroyIcon($iconHandle) | Out-Null
$graphics.Dispose(); $bitmap.Dispose(); $mint.Dispose(); $white.Dispose(); $dark.Dispose(); $pen.Dispose()
$references = @(
    'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll',
    'WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','System.Xaml.dll','WPF\UIAutomationProvider.dll'
) | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output',
    ('/out:' + (Join-Path $dist 'Snapline.exe')),
    ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')),
    ('/win32icon:' + $iconFile)
) + $references + (Join-Path $projectRoot 'src\Snapline.cs'), (Join-Path $projectRoot 'src\Integration.cs'), (Join-Path $projectRoot 'src\RegionCapture.cs'), (Join-Path $projectRoot 'src\Memo.cs'), (Join-Path $projectRoot 'src\Shortcut.cs'), (Join-Path $projectRoot 'src\ShortcutEditor.cs'), (Join-Path $projectRoot 'src\ShortcutTests.cs'), (Join-Path $projectRoot 'src\Hover.cs'), (Join-Path $projectRoot 'src\HoverTests.cs'), (Join-Path $projectRoot 'src\Startup.cs'), (Join-Path $projectRoot 'src\StartupTests.cs'), (Join-Path $projectRoot 'src\Remark.cs'), (Join-Path $projectRoot 'src\RemarkTests.cs'), (Join-Path $projectRoot 'src\ShelfMotion.cs'), (Join-Path $projectRoot 'src\InteractionTests.cs')
$arguments += (Join-Path $projectRoot 'src\FallingPaper.cs'), (Join-Path $projectRoot 'src\HoverEffects.cs')
& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'Snapline.exe.config') -Destination $dist -Force
foreach ($name in @('使用说明.txt','LICENSE.txt','THIRD_PARTY_NOTICES.md')) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $name)) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $dist -Force }
}
Get-Item -LiteralPath (Join-Path $dist 'Snapline.exe') | Select-Object FullName,Length
