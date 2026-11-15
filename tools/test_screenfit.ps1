param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_toast.png"
)

Add-Type -AssemblyName System.Windows.Forms
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
Start-Sleep -Milliseconds 1300
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Milliseconds 1200
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Milliseconds 1600

$handles = [W]::WindowsOf([uint32]$p.Id)
Write-Output "窗口数 = $($handles.Count)"

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
Write-Output ("工作区 = {0},{1} {2}x{3}" -f $screen.Left, $screen.Top, $screen.Width, $screen.Height)

$ok = $true
foreach ($h in $handles) {
    $r = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $inside = ($r.Left -ge $screen.Left) -and ($r.Top -ge $screen.Top) -and ($r.Right -le $screen.Right) -and ($r.Bottom -le $screen.Bottom)
    if (-not $inside) { $ok = $false }
    Write-Output ("  [{0},{1}]-[{2},{3}]  在屏内={4}" -f $r.Left, $r.Top, $r.Right, $r.Bottom, $inside)
}
Write-Output ("全部在屏幕内 = {0}" -f $ok)

# 试着把某个窗口硬推到屏幕外（模拟拖动到边界外），程序应该夹回来
[W]::SetWindowPos($handles[0], [IntPtr]::Zero, 1900, 1000, 0, 0, 0x0001 -bor 0x0004) | Out-Null
Start-Sleep -Milliseconds 600
$r = New-Object W+RECT
[W]::GetWindowRect($handles[0], [ref]$r) | Out-Null
Write-Output ("强行推到 1900,1000 之后实际位置 = [{0},{1}]-[{2},{3}]" -f $r.Left, $r.Top, $r.Right, $r.Bottom)

# 再启动一次触发上限提示，并截一张带提示的图
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Milliseconds 700

Add-Type -AssemblyName System.Drawing
$r2 = New-Object W+RECT
[W]::GetWindowRect($handles[1], [ref]$r2) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r2.Right - $r2.Left), ($r2.Bottom - $r2.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r2.Left, $r2.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
