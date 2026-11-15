param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$OutDrag = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_drag3.png",
    [string]$OutAfter = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_drag3_after.png",
    [string]$OutLate = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_drag3_late.png"
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
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
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
Start-Sleep -Milliseconds 1200
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Milliseconds 1200
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Milliseconds 1600

$handles = [P]::WindowsOf([uint32]$p.Id)
Write-Output "windows = $($handles.Count)"
if ($handles.Count -lt 3) { Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force; exit 1 }

$a = $handles[0]; $b = $handles[1]; $c = $handles[2]

# A、B 先贴好（这是"拖动之前就存在的连接"）
[P]::SetWindowPos($a, [IntPtr]::Zero, 200, 200, 0, 0, 0x0001 -bor 0x0004) | Out-Null
[P]::SetWindowPos($b, [IntPtr]::Zero, 800, 200, 0, 0, 0x0001 -bor 0x0004) | Out-Null
[P]::SetWindowPos($c, [IntPtr]::Zero, 1400, 700, 0, 0, 0x0001 -bor 0x0004) | Out-Null
Start-Sleep -Milliseconds 800

# 用鼠标把 C 拖到 A 下面（松手前先截一张）
[P]::SetForegroundWindow($c) | Out-Null
Start-Sleep -Milliseconds 250
[P]::SetCursorPos(1700, 900) | Out-Null
Start-Sleep -Milliseconds 150
[P]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 150
# 目标：C 客户区落到 (200,600)，抓点在窗口内 (300,200)
[P]::SetCursorPos(500, 800) | Out-Null
Start-Sleep -Milliseconds 400
[P]::SetCursorPos(505, 802) | Out-Null
Start-Sleep -Milliseconds 500

Capture-Windows $handles $OutDrag
Write-Output "captured while dragging -> $OutDrag"

[P]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 120
Capture-Windows $handles $OutAfter
Write-Output "captured right after release -> $OutAfter"

Start-Sleep -Milliseconds 900
Capture-Windows $handles $OutLate
Write-Output "captured 1s after release -> $OutLate"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
