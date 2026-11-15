param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe"
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class O {
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
Start-Sleep -Milliseconds 1500
$p2 = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1500

$handles = [O]::WindowsOf([uint32]$p.Id)
$rects = @()
foreach ($h in $handles) { $r = New-Object O+RECT; [O]::GetWindowRect($h, [ref]$r) | Out-Null; $rects += ,@($h, $r) }
$rects = $rects | Sort-Object { $_[1].Left }
$w1 = $rects[0]; $w2 = $rects[1]

$grabX = $w2[1].Left + 300; $grabY = $w2[1].Top + 200
$destX = $w1[1].Left + 300; $destY = $w1[1].Top + 200

[O]::SetForegroundWindow($w2[0]) | Out-Null
Start-Sleep -Milliseconds 200
[O]::SetCursorPos($grabX, $grabY) | Out-Null
Start-Sleep -Milliseconds 120
[O]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 120
for ($i = 1; $i -le 20; $i++) {
    [O]::SetCursorPos(($grabX + [int](($destX - $grabX) * $i / 20)), ($grabY + [int](($destY - $grabY) * $i / 20))) | Out-Null
    Start-Sleep -Milliseconds 25
}
[O]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 500

$r1 = New-Object O+RECT; [O]::GetWindowRect($w1[0], [ref]$r1) | Out-Null
$r2 = New-Object O+RECT; [O]::GetWindowRect($w2[0], [ref]$r2) | Out-Null
Write-Output "w1 $($r1.Left),$($r1.Top)-$($r1.Right),$($r1.Bottom)"
Write-Output "w2 $($r2.Left),$($r2.Top)-$($r2.Right),$($r2.Bottom)"
$overlap = ($r1.Left -lt $r2.Right) -and ($r2.Left -lt $r1.Right) -and ($r1.Top -lt $r2.Bottom) -and ($r2.Top -lt $r1.Bottom)
Write-Output "overlap = $overlap"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
