# Starts Storiva, waits, captures its main window with PrintWindow (what the user sees), then closes it.
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-window.ps1 -Exe <path> -Out <png>
param(
  [Parameter(Mandatory)] [string]$Exe,
  [Parameter(Mandatory)] [string]$Out,
  [int]$WaitSeconds = 4
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class StorivaCapture {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
"@
$p = Start-Process $Exe -PassThru
try {
  $deadline = (Get-Date).AddSeconds(20)
  while ($p.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $p.Refresh() }
  if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "Main window did not appear." }
  Start-Sleep -Seconds $WaitSeconds
  $r = New-Object StorivaCapture+RECT
  [void][StorivaCapture]::GetWindowRect($p.MainWindowHandle, [ref]$r)
  $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
  $g = [Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  [void][StorivaCapture]::PrintWindow($p.MainWindowHandle, $hdc, 2)   # PW_RENDERFULLCONTENT
  $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
  "Captured $($bmp.Width)x$($bmp.Height) -> $Out (title: $($p.MainWindowTitle))"
}
finally {
  if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(5000)) { $p.Kill() } }
  "Exit code: $($p.ExitCode)"
}
