param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$OutA = "D:\Workspaces\WindowsLoop-Remastered\tools\mid_a.png",
    [string]$OutB = "D:\Workspaces\WindowsLoop-Remastered\tools\mid_b.png"
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class G {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@

function Snap($h, $out) {
    Add-Type -AssemblyName System.Drawing
    $r = New-Object G+RECT
    [G]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return "$($r.Left),$($r.Top) ${w}x${hh}"
}

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 2000

$handles = [G]::WindowsOf([uint32]$p.Id)
$h = $handles[0]
$r = New-Object G+RECT
[G]::GetWindowRect($h, [ref]$r) | Out-Null
$grabX = $r.Left + 400; $grabY = $r.Top + 300

for ($i = 0; $i -lt 6; $i++) {
    [G]::ShowWindow($h, 5) | Out-Null
    [G]::BringWindowToTop($h) | Out-Null
    [G]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 120
    if ([G]::GetForegroundWindow() -eq $h) { break }
    [G]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)
    [G]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [G]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 80
}
Write-Output "focused = $([G]::GetForegroundWindow() -eq $h)"
Start-Sleep -Milliseconds 150
[G]::SetCursorPos($grabX, $grabY) | Out-Null
Start-Sleep -Milliseconds 150
[G]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 150

for ($i = 1; $i -le 8; $i++) {
    [G]::SetCursorPos($grabX + 40 * $i, $grabY + 20 * $i) | Out-Null
    Start-Sleep -Milliseconds 40
}
$a = Snap $h $OutA
Write-Output "A: $a"

for ($i = 1; $i -le 16; $i++) {
    [G]::SetCursorPos($grabX + 40 * (8 + $i * 2), $grabY + 20 * (8 + $i * 2)) | Out-Null
    Start-Sleep -Milliseconds 30
}
$b = Snap $h $OutB
Write-Output "B: $b"

[G]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
