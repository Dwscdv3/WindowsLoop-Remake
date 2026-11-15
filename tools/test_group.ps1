param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_group.png",
    [string]$Layout = "tee"
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
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@

$count = if ($Layout -eq "plus") { 5 } elseif ($Layout -eq "tee") { 3 } else { 3 }

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1200
for ($i = 1; $i -lt $count; $i++) {
    Start-Process -FilePath $Exe | Out-Null
    Start-Sleep -Milliseconds 1200
}
Start-Sleep -Milliseconds 800

$handles = [T]::WindowsOf([uint32]$p.Id)
Write-Output "windows = $($handles.Count)  (请求 $count，关卡上限会截断)"
if ($handles.Count -lt 1) { Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force; exit 1 }
$count = $handles.Count

$W = 600; $H = 400
[int[]]$px = @(); [int[]]$py = @()

if ($Layout -eq "plus") {
    # 十字形：中心那块四面各贴一块
    $cx = 700; $cy = 500
    $px = @($cx, ($cx - $W), ($cx + $W), $cx, $cx)
    $py = @($cy, $cy, $cy, ($cy - $H), ($cy + $H))
} elseif ($Layout -eq "tee") {
    # 与你截图一致：右上 A、左下 B、中间 C
    $bx = 200; $by = 520
    $px = @(($bx + $W + 160), $bx, ($bx + $W))
    $py = @(($by - $H), $by, $by)
} else {
    $ax = 200; $ay = 120
    $px = @($ax, ($ax + $W), ($ax + 2 * $W))
    $py = @($ay, ($ay + 160), ($ay + 320))
}

for ($i = 0; $i -lt $count; $i++) {
    [T]::SetWindowPos($handles[$i], [IntPtr]::Zero, $px[$i], $py[$i], 0, 0, 0x0001 -bor 0x0004) | Out-Null
}
Start-Sleep -Milliseconds 900

$handles | ForEach-Object {
    [T]::SetWindowPos($_, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
}
Start-Sleep -Milliseconds 600

Add-Type -AssemblyName System.Drawing
$minL = [int]::MaxValue; $minT = [int]::MaxValue; $maxR = [int]::MinValue; $maxB = [int]::MinValue
$rects = @()
foreach ($h in $handles) {
    $r = New-Object T+RECT
    [T]::GetWindowRect($h, [ref]$r) | Out-Null
    $rects += ,@($h, $r)
    if ($r.Left -lt $minL) { $minL = $r.Left }
    if ($r.Top -lt $minT) { $minT = $r.Top }
    if ($r.Right -gt $maxR) { $maxR = $r.Right }
    if ($r.Bottom -gt $maxB) { $maxB = $r.Bottom }
}

# 先截整张桌面再裁剪，重叠窗口的层级才是真实的
$desk = New-Object System.Drawing.Bitmap ($maxR - $minL), ($maxB - $minT)
$dg = [System.Drawing.Graphics]::FromImage($desk)
$dg.CopyFromScreen($minL, $minT, 0, 0, $desk.Size)
$dg.Dispose()
$desk.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$desk.Dispose()
Write-Output "saved $Out ($Layout)"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
