param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_multi.png",
    [int]$Windows = 2
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinEnum {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1600
for ($i = 1; $i -lt $Windows; $i++) {
    $p2 = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Milliseconds 1200
}
Start-Sleep -Milliseconds 800

$handles = [WinEnum]::WindowsOf([uint32]$p.Id)
Write-Output "windows: $($handles.Count)"

$minL = 999999; $minT = 999999; $maxR = -999999; $maxB = -999999
foreach ($h in $handles) {
    $r = New-Object WinEnum+RECT
    [WinEnum]::GetWindowRect($h, [ref]$r) | Out-Null
    if ($r.Right - $r.Left -lt 200) { continue }
    Write-Output ("  {0},{1} {2}x{3}" -f $r.Left, $r.Top, ($r.Right - $r.Left), ($r.Bottom - $r.Top))
    if ($r.Left -lt $minL) { $minL = $r.Left }
    if ($r.Top -lt $minT) { $minT = $r.Top }
    if ($r.Right -gt $maxR) { $maxR = $r.Right }
    if ($r.Bottom -gt $maxB) { $maxB = $r.Bottom }
}

if ($handles.Count -gt 0) { [WinEnum]::SetForegroundWindow($handles[0]) | Out-Null }
Start-Sleep -Milliseconds 400

$w = $maxR - $minL; $hh = $maxB - $minT
Write-Output "union ${w}x${hh} at $minL,$minT"

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($minL, $minT, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
