param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_corner.png",
    [string]$Mode = "corner"
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class C {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
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

$handles = [C]::WindowsOf([uint32]$p.Id)
$rects = @()
foreach ($h in $handles) { $r = New-Object C+RECT; [C]::GetWindowRect($h, [ref]$r) | Out-Null; $rects += ,@($h, $r) }
$rects = $rects | Sort-Object { $_[1].Left }
$w1 = $rects[0]; $w2 = $rects[1]

if ($Mode -eq "corner") {
    $tx = $w1[1].Right
    $ty = $w1[1].Bottom
} else {
    # 故意重叠
    $tx = $w1[1].Left + 200
    $ty = $w1[1].Top + 120
}
[C]::SetWindowPos($w2[0], [IntPtr]::Zero, $tx, $ty, 0, 0, 0x0001 -bor 0x0004) | Out-Null
Start-Sleep -Milliseconds 600

foreach ($h in $handles) {
    [C]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
}
Start-Sleep -Milliseconds 400

Add-Type -AssemblyName System.Drawing
$minL = [int]::MaxValue; $minT = [int]::MaxValue; $maxR = [int]::MinValue; $maxB = [int]::MinValue
$rects2 = @()
foreach ($h in $handles) {
    $r = New-Object C+RECT
    [C]::GetWindowRect($h, [ref]$r) | Out-Null
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
Write-Output "saved $Out ($Mode)"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
