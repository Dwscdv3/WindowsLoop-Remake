param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_cross.png",
    [int]$OffsetRows = 4,
    [int]$HoldMs = 2000,
    [switch]$Jump
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
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
Start-Sleep -Milliseconds 1500
$p2 = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500

$handles = [W]::WindowsOf([uint32]$p.Id)
if ($handles.Count -lt 2) { Write-Output "expected 2 windows, got $($handles.Count)"; Get-Process WindowsLoop -EA SilentlyContinue | Stop-Process -Force; exit 1 }

$rects = @()
foreach ($h in $handles) { $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null; $rects += ,@($h, $r) }
$rects = $rects | Sort-Object { $_[1].Left }
$w1 = $rects[0]; $w2 = $rects[1]

$c1x = $w1[1].Left; $c1y = $w1[1].Top
$c2x = $w2[1].Left; $c2y = $w2[1].Top
Write-Output "w1 client $c1x,$c1y   w2 client $c2x,$c2y"

$targetX = $c1x + 600
$targetY = $c1y + $OffsetRows * 40
[W]::SetWindowPos($w2[0], [IntPtr]::Zero, $targetX, $targetY, 0, 0, 0x0001 -bor 0x0004) | Out-Null
Start-Sleep -Milliseconds 400
Write-Output "moved w2 to client $targetX,$targetY"

[W]::SetForegroundWindow($w1[0]) | Out-Null
Start-Sleep -Milliseconds 300

[W]::keybd_event(0x44, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds $HoldMs
[W]::keybd_event(0x44, 0, 2, [IntPtr]::Zero)
Start-Sleep -Milliseconds 200

if ($Jump) {
    for ($k = 0; $k -lt 6; $k++) {
        [W]::keybd_event(0x44, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 160
        [W]::keybd_event(0x20, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 90
        [W]::keybd_event(0x20, 0, 2, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 360
    }
    [W]::keybd_event(0x44, 0, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 400
}

Add-Type -AssemblyName System.Drawing
foreach ($h in $handles) {
    [W]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
}
Start-Sleep -Milliseconds 250
foreach ($h in $handles) {
    [W]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
}
Start-Sleep -Milliseconds 350
$minL = [int]::MaxValue; $minT = [int]::MaxValue; $maxR = [int]::MinValue; $maxB = [int]::MinValue
$rects2 = @()
foreach ($h in $handles) {
    $r = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $rects2 += ,@($h, $r)
    if ($r.Left -lt $minL) { $minL = $r.Left }
    if ($r.Top -lt $minT) { $minT = $r.Top }
    if ($r.Right -gt $maxR) { $maxR = $r.Right }
    if ($r.Bottom -gt $maxB) { $maxB = $r.Bottom }
}
Write-Output "bounds $minL,$minT - $maxR,$maxB  size $($maxR - $minL)x$($maxB - $minT)"

$bmp = $null  # unused placeholder

# capture composites: grab each window separately then paste
$canvas = New-Object System.Drawing.Bitmap ($maxR - $minL), ($maxB - $minT)
$cg = [System.Drawing.Graphics]::FromImage($canvas)
$cg.Clear([System.Drawing.Color]::FromArgb(40, 44, 52))
foreach ($item in $rects2) {
    $r = $item[1]
    $wpx = $r.Right - $r.Left; $hpx = $r.Bottom - $r.Top
    $tmp = New-Object System.Drawing.Bitmap $wpx, $hpx
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
