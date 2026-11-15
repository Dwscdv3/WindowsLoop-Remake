param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_dragself.png",
    [int]$Dx = 160,
    [int]$Dy = 120
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class D {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 2000

$handles = [D]::WindowsOf([uint32]$p.Id)
$h = $handles[0]
$r = New-Object D+RECT
[D]::GetWindowRect($h, [ref]$r) | Out-Null
Write-Output "before: $($r.Left),$($r.Top)"

$grabX = $r.Left + 400; $grabY = $r.Top + 300
[D]::SetForegroundWindow($h) | Out-Null
[D]::SetCursorPos($grabX, $grabY) | Out-Null
Start-Sleep -Milliseconds 150
[D]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 120
for ($i = 1; $i -le 12; $i++) {
    [D]::SetCursorPos(($grabX + [int]($Dx * $i / 12)), ($grabY + [int]($Dy * $i / 12))) | Out-Null
    Start-Sleep -Milliseconds 30
}
[D]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500

$r2 = New-Object D+RECT
[D]::GetWindowRect($h, [ref]$r2) | Out-Null
Write-Output "after:  $($r2.Left),$($r2.Top)"

Add-Type -AssemblyName System.Drawing
$w = $r2.Right - $r2.Left; $hh = $r2.Bottom - $r2.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r2.Left, $r2.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
