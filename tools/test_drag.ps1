param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [int]$TargetRows = 4
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class M {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
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
Start-Sleep -Milliseconds 1500
$p2 = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500

$handles = [M]::WindowsOf([uint32]$p.Id)
$rects = @()
foreach ($h in $handles) { $r = New-Object M+RECT; [M]::GetWindowRect($h, [ref]$r) | Out-Null; $rects += ,@($h, $r) }
$rects = $rects | Sort-Object { $_[1].Left }
$w1 = $rects[0]; $w2 = $rects[1]
$c1x = $w1[1].Left; $c1y = $w1[1].Top
Write-Output "w1 client $c1x,$c1y ; w2 window $($w2[1].Left),$($w2[1].Top)"

$grabX = $w2[1].Left + 300
$grabY = $w2[1].Top + 200
$destX = $c1x + 600 + 8 + 300
$destY = $c1y + $TargetRows * 40 + 200
Write-Output "grab $grabX,$grabY -> $destX,$destY"

[M]::SetForegroundWindow($w2[0]) | Out-Null
Start-Sleep -Milliseconds 200
[M]::SetCursorPos($grabX, $grabY) | Out-Null
Start-Sleep -Milliseconds 150
[M]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 150

for ($i = 1; $i -le 24; $i++) {
    $x = [int]($grabX + ($destX - $grabX) * $i / 24)
    $y = [int]($grabY + ($destY - $grabY) * $i / 24)
    [M]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 30
}
Start-Sleep -Milliseconds 300
[M]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 600

$r1 = New-Object M+RECT; [M]::GetWindowRect($w1[0], [ref]$r1) | Out-Null
$r2 = New-Object M+RECT; [M]::GetWindowRect($w2[0], [ref]$r2) | Out-Null
$c1 = "$($r1.Left),$($r1.Top)"
$c2 = "$($r2.Left),$($r2.Top)"
Write-Output "final w1 client $c1 ; w2 client $c2"
$adj = (($r1.Left + 600) -eq ($r2.Left))
Write-Output "adjacent-right = $adj ; rowOffset = $(($r2.Top - ($r1.Top)) / 40)"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
