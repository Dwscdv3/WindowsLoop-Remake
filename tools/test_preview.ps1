param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_preview.png"
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class P {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@

function Capture-Windows($handles, $out) {
    Add-Type -AssemblyName System.Drawing
    $rects = @()
    $minL = [int]::MaxValue; $minT = [int]::MaxValue; $maxR = [int]::MinValue; $maxB = [int]::MinValue
    foreach ($h in $handles) {
        $r = New-Object P+RECT
        [P]::GetWindowRect($h, [ref]$r) | Out-Null
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
        $wpx = $r.Right - $r.Left; $hpx = $r.Bottom - $r.Top
        $tmp = New-Object System.Drawing.Bitmap $wpx, $hpx
        $tg = [System.Drawing.Graphics]::FromImage($tmp)
        $tg.CopyFromScreen($r.Left, $r.Top, 0, 0, $tmp.Size)
        $tg.Dispose()
        $cg.DrawImage($tmp, ($r.Left - $minL), ($r.Top - $minT))
        $tmp.Dispose()
    }
    $cg.Dispose()
    $canvas.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
    return "$minL,$minT"
}

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500
$p2 = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500

$handles = [P]::WindowsOf([uint32]$p.Id)
$rects = @()
foreach ($h in $handles) { $r = New-Object P+RECT; [P]::GetWindowRect($h, [ref]$r) | Out-Null; $rects += ,@($h, $r) }
$rects = $rects | Sort-Object { $_[1].Left }
$w1 = $rects[0]; $w2 = $rects[1]
if ($DragIndex -eq 0) { $w1 = $rects[1]; $w2 = $rects[0] }

$c1x = $w1[1].Left; $c1y = $w1[1].Top
$targetX = $c1x + 600
$targetY = $c1y + 160

# 抓住窗口 2 的任意位置（无边框，整窗可拖）
$grabX = $w2[1].Left + 300; $grabY = $w2[1].Top + 200
$destX = $targetX + 20 + 300   # 停在吸附位置右侧 20px，不松手
$destY = $targetY + 200

[P]::SetForegroundWindow($w2[0]) | Out-Null
Start-Sleep -Milliseconds 200
[P]::SetCursorPos($grabX, $grabY) | Out-Null
Start-Sleep -Milliseconds 150
[P]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 150

for ($i = 1; $i -le 20; $i++) {
    $x = [int]($grabX + ($destX - $grabX) * $i / 20)
    $y = [int]($grabY + ($destY - $grabY) * $i / 20)
    [P]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 25
}
Start-Sleep -Milliseconds 400

# 按住 D 一秒：如果游戏没暂停，角色会往右走
[P]::keybd_event(0x44, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1000
[P]::keybd_event(0x44, 0, 2, [IntPtr]::Zero)

foreach ($h in $handles) {
    [P]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
}
Start-Sleep -Milliseconds 300

$pos = Capture-Windows $handles $Out
Write-Output "captured while dragging ($pos) -> $Out"

$r2 = New-Object P+RECT; [P]::GetWindowRect($w2[0], [ref]$r2) | Out-Null
Write-Output "during drag w2 at $($r2.Left),$($r2.Top) (snap target would be $targetX,$targetY)"

[P]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 600

$r3 = New-Object P+RECT; [P]::GetWindowRect($w2[0], [ref]$r3) | Out-Null
Write-Output "after release w2 at $($r3.Left),$($r3.Top) ; snapped = $($r3.Left -eq $targetX)"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
