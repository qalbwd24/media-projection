param([switch]$Package)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$src = Join-Path $PSScriptRoot 'src'
$out = Join-Path $PSScriptRoot 'dist/Windows'
$work = Join-Path $PSScriptRoot 'dist/build'
$framework = Join-Path $env:windir 'Microsoft.NET/Framework64/v4.0.30319'
$metadata = Join-Path $env:windir 'System32/WinMetadata'
$iconPath = Join-Path $work 'MediaProjection.ico'
$exe = Join-Path $out 'MediaProjection.exe'
New-Item -ItemType Directory -Force -Path $out,$work | Out-Null

function Make-Icon {
    $sizes = @(16, 24, 32, 48, 64, 128)
    $images = foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $drawing = [Drawing.Graphics]::FromImage($bitmap)
        $navy = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(27, 52, 73))
        $mint = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(127, 243, 213))
        $rim = [Drawing.Pen]::new([Drawing.Color]::FromArgb(237, 246, 255), 3)
        $signal = [Drawing.Pen]::new($mint.Color, 3)
        $png = [IO.MemoryStream]::new()
        try {
            $drawing.SmoothingMode = 'AntiAlias'
            $drawing.ScaleTransform($size / 64.0, $size / 64.0)
            $drawing.FillEllipse($navy, 0, 0, 64, 64)
            $drawing.DrawRectangle($rim, 14, 14, 24, 36)
            $drawing.FillPolygon($mint, [Drawing.Point[]]@([Drawing.Point]::new(23,25), [Drawing.Point]::new(33,32), [Drawing.Point]::new(23,39)))
            $drawing.DrawArc($signal, 33, 22, 15, 20, -52, 104)
            $drawing.DrawArc($signal, 36, 16, 19, 32, -53, 106)
            $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
            ,$png.ToArray()
        } finally {
            $png.Dispose(); $signal.Dispose(); $rim.Dispose(); $mint.Dispose(); $navy.Dispose(); $drawing.Dispose(); $bitmap.Dispose()
        }
    }
    $stream = [IO.File]::Open($iconPath, [IO.FileMode]::Create)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $writer.Write([byte]$sizes[$index])
            $writer.Write([byte]$sizes[$index])
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $images[$index].Length
        }
        foreach ($bytes in $images) { $writer.Write([byte[]]$bytes) }
    } finally { $writer.Dispose() }
}

Make-Icon
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll',
    'System.Runtime.dll','System.Runtime.InteropServices.WindowsRuntime.dll') |
    ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('Windows.Foundation.winmd','Windows.Devices.winmd','Windows.Storage.winmd') |
    ForEach-Object { '/reference:' + (Join-Path $metadata $_) }
$references += @('WPF/UIAutomationClient.dll','WPF/UIAutomationTypes.dll','WPF/WindowsBase.dll') |
    ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$files = @('Program.cs','RemoteApplication.cs','RemoteWindow.cs','DockWindow.cs','Visuals.cs','Preferences.cs',
    'LocalControl.cs','CapsKeys.cs','BleController.cs','BleLink.cs','TaskbarSpace.cs','LayeredSurface.cs','FrameClock.cs') | ForEach-Object { Join-Path $src $_ }
$manifest = Join-Path $src 'Remote.manifest'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$exe" "/win32icon:$iconPath" "/win32manifest:$manifest" @references @files
if ($LASTEXITCODE -ne 0) { throw "Windows build failed with exit code $LASTEXITCODE" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'windows/Control-MediaProjection.ps1') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'windows/安装到开始菜单.cmd') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'windows/使用说明.md') -Destination $out -Force
if ($Package) {
    $archive = Join-Path $PSScriptRoot 'dist/MediaProjection-Windows-x64.zip'
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $archive -Force
    Get-Item -LiteralPath $archive
}
Get-Item -LiteralPath $exe
