param(
    [string] $ProcessName = 'XCTrackMapSync',
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
if (-not ('NativeWindowCapture' -as [type])) {
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class NativeWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out RECT rectangle);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr handle, int command);
}
'@
}

[void] [NativeWindowCapture]::SetProcessDPIAware()

$process = Get-Process -Name $ProcessName | Select-Object -First 1
if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
    throw "Process '$ProcessName' does not have a visible main window."
}

[void] [NativeWindowCapture]::ShowWindow($process.MainWindowHandle, 9)
[void] [NativeWindowCapture]::SetForegroundWindow($process.MainWindowHandle)
Start-Sleep -Milliseconds 750
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 250

$rectangle = New-Object NativeWindowCapture+RECT
if (-not [NativeWindowCapture]::GetWindowRect($process.MainWindowHandle, [ref] $rectangle)) {
    throw 'GetWindowRect failed.'
}

$width = $rectangle.Right - $rectangle.Left
$height = $rectangle.Bottom - $rectangle.Top
$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($rectangle.Left, $rectangle.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output $OutputPath
