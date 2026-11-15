param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$OutA = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_flash_a.png",
    [string]$OutB = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_flash_b.png"
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class F {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@

function Capture($handles, $out) {
    Add-Type -AssemblyName System.Drawing
    $rects = @()
    $minL = [int]::MaxValue; $minT = [int]::MaxValue; $maxR = [int]::MinValue; $maxB = [int]::MinValue
    foreach ($h in $handles) {
        $r = New-Object F+RECT
        [F]::GetWindowRect($h, [ref]$r) | Out-Null
        $rects += ,@($h, $r)
        if ($r.Left -lt $minL) { $minL = $r.Left }
        if ($r.Top -lt $minT) { $minT = $r.Top }
        if ($r.Right -gt $maxR) { $maxR = $r.Right }
        if ($r.Bottom -gt $maxB) { $maxB = $r.Bottom }
    }
    $canvas = New-Object System.Drawing.Bitmap ($maxR - $minL), ($maxB - $minT)
    $cg = [System.Drawing.Graphics]::FromImage($canvas)
    $cg.Clear([System.Drawing.Color]::FromArgb(40, 44, 52))
    foreach ($item in $rects) {
        $r = $item[1]
        $tmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
        $tg = [System.Drawing.Graphics]::FromImage($tmp)
        $tg.CopyFromScreen($r.Left, $r.Top, 0, 0, $tmp.Size)
        $tg.Dispose()
        $cg.DrawImage($tmp, ($r.Left - $minL), ($r.Top - $minT))
        $tmp.Dispose()
    }
    $cg.Dispose()
    $canvas.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
}

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500
$p2 = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500

$handles = [F]::WindowsOf([uint32]$p.Id)
$rects = @()
foreach ($h in $handles) { $r = New-Object F+RECT; [F]::GetWindowRect($h, [ref]$r) | Out-Null; $rects += ,@($h, $r) }
$rects = $rects | Sort-Object { $_[1].Left }
$w1 = $rects[0]; $w2 = $rects[1]

$grabX = $w2[1].Left + 300; $grabY = $w2[1].Top + 200
$destX = $w1[1].Left + 600 + 300 + 20
$destY = $w1[1].Top + 160 + 200

[F]::SetForegroundWindow($w2[0]) | Out-Null
Start-Sleep -Milliseconds 200
[F]::SetCursorPos($grabX, $grabY) | Out-Null
Start-Sleep -Milliseconds 120
[F]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 120
for ($i = 1; $i -le 16; $i++) {
    [F]::SetCursorPos(($grabX + [int](($destX - $grabX) * $i / 16)), ($grabY + [int](($destY - $grabY) * $i / 16))) | Out-Null
    Start-Sleep -Milliseconds 25
}
Start-Sleep -Milliseconds 200
[F]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)

Capture $handles $OutA
Start-Sleep -Milliseconds 900
Capture $handles $OutB

Write-Output "captured $OutA (just released) and $OutB (after 0.9s)"
Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
