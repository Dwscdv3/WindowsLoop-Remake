param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Level = "level2",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_wilds.png",
    [int]$HoldMs = 5000,
    [int]$Vk = 0x44,
    [int]$PreVk = 0,
    [int]$PreMs = 0
)

# 大图环形滚动测试：一直往右走，位置应该越过地图宽度继续增长（摄像机跟着，画面无缝循环）

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class R {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public static List<IntPtr> WindowsOf(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@

function Focus-Window($h) {
    for ($i = 0; $i -lt 6; $i++) {
        [R]::ShowWindow($h, 5) | Out-Null
        [R]::BringWindowToTop($h) | Out-Null
        [R]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 120
        if ([R]::GetForegroundWindow() -eq $h) { return $true }
        [R]::keybd_event(0x12, 0, 0, [IntPtr]::Zero)
        [R]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 80
        [R]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 120
        if ([R]::GetForegroundWindow() -eq $h) { return $true }
    }
    return $false
}

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$p = Start-Process -FilePath $Exe -ArgumentList @("--level", $Level) -PassThru
Start-Sleep -Milliseconds 1800
$handles = [R]::WindowsOf([uint32]$p.Id)
Write-Output "windows = $($handles.Count)"
if ($handles.Count -lt 1) { exit 1 }

$focused = Focus-Window $handles[0]
Write-Output "focused = $focused"

# 先朝 PreVk 走 PreMs 毫秒（可选），再一直按住 Vk
# （0x44=D 右，0x41=A 左，0x57=W 上，0x53=S 下）
if ($PreVk -ne 0 -and $PreMs -gt 0) {
    [R]::keybd_event([byte]$PreVk, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds $PreMs
    [R]::keybd_event([byte]$PreVk, 0, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
}

[R]::keybd_event([byte]$Vk, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds $HoldMs
[R]::keybd_event([byte]$Vk, 0, 2, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300

foreach ($h in $handles) {
    [R]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
}
Start-Sleep -Milliseconds 400

Add-Type -AssemblyName System.Drawing
$r = New-Object R+RECT
[R]::GetWindowRect($handles[0], [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
