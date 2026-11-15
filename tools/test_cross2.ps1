param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_cross.png",
    [int]$Rows = 0,
    [int]$HoldMs = 2500
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class T {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@

function Focus-Window($h) {
    for ($i = 0; $i -lt 6; $i++) {
        [T]::ShowWindow($h, 5) | Out-Null
        [T]::BringWindowToTop($h) | Out-Null
        [T]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 120
        if ([T]::GetForegroundWindow() -eq $h) { return $true }
        # 模拟一次 Alt 轻点，绕过前台锁
        [T]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)
        [T]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [T]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 120
        if ([T]::GetForegroundWindow() -eq $h) { return $true }
    }
    return $false
}

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500
$p2 = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500

$handles = [T]::WindowsOf([uint32]$p.Id)
$rects = @()
foreach ($h in $handles) { $r = New-Object T+RECT; [T]::GetWindowRect($h, [ref]$r) | Out-Null; $rects += ,@($h, $r) }
$rects = $rects | Sort-Object { $_[1].Left }
$w1 = $rects[0]; $w2 = $rects[1]

$tx = $w1[1].Left + 600
$ty = $w1[1].Top + $Rows * 40
[T]::SetWindowPos($w2[0], [IntPtr]::Zero, $tx, $ty, 0, 0, 0x0001 -bor 0x0004) | Out-Null
Start-Sleep -Milliseconds 500
Write-Output "w2 -> $tx,$ty (rows=$Rows)"

$focused = Focus-Window $w1[0]
Write-Output "focused = $focused"

[T]::keybd_event(0x44, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds $HoldMs
[T]::keybd_event(0x44, 0, 2, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300

foreach ($h in $handles) {
    [T]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
}
Start-Sleep -Milliseconds 300

Add-Type -AssemblyName System.Drawing
$minL = [int]::MaxValue; $minT = [int]::MaxValue; $maxR = [int]::MinValue; $maxB = [int]::MinValue
$rects2 = @()
foreach ($h in $handles) {
    $r = New-Object T+RECT
    [T]::GetWindowRect($h, [ref]$r) | Out-Null
    $rects2 += ,@($h, $r)
    if ($r.Left -lt $minL) { $minL = $r.Left }
    if ($r.Top -lt $minT) { $minT = $r.Top }
    if ($r.Right -gt $maxR) { $maxR = $r.Right }
    if ($r.Bottom -gt $maxB) { $maxB = $r.Bottom }
}
$canvas = New-Object System.Drawing.Bitmap ($maxR - $minL), ($maxB - $minT)
$cg = [System.Drawing.Graphics]::FromImage($canvas)
$cg.Clear([System.Drawing.Color]::FromArgb(40, 44, 52))
foreach ($item in $rects2) {
    $r = $item[1]
    $tmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $tg = [System.Drawing.Graphics]::FromImage($tmp)
    $tg.CopyFromScreen($r.Left, $r.Top, 0, 0, $tmp.Size)
    $tg.Dispose()
    $cg.DrawImage($tmp, ($r.Left - $minL), ($r.Top - $minT))
    $tmp.Dispose()
}
$cg.Dispose()
$canvas.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()
Write-Output "saved $Out"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
