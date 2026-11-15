param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe"
)

# 用真实的鼠标拖拽把窗口往屏幕右下角甩出去，松手后应该被夹回工作区内

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

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1600

$h = [W]::WindowsOf([uint32]$p.Id)[0]
$r = New-Object W+RECT
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
Write-Output ("起始 [{0},{1}]-[{2},{3}]，工作区 {4}x{5}" -f $r.Left, $r.Top, $r.Right, $r.Bottom, $screen.Width, $screen.Height)

[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 300
$grabX = $r.Left + 300; $grabY = $r.Top + 200
[W]::SetCursorPos($grabX, $grabY) | Out-Null
Start-Sleep -Milliseconds 150
[W]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)      # 左键按下
Start-Sleep -Milliseconds 150
[W]::SetCursorPos(1900, 1000) | Out-Null
Start-Sleep -Milliseconds 300
[W]::SetCursorPos(2130, 1020) | Out-Null
Start-Sleep -Milliseconds 400

$r2 = New-Object W+RECT
[W]::GetWindowRect($h, [ref]$r2) | Out-Null
Write-Output ("拖到角落（未松手）[{0},{1}]-[{2},{3}]" -f $r2.Left, $r2.Top, $r2.Right, $r2.Bottom)

[W]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)      # 左键松开
Start-Sleep -Milliseconds 500

$r3 = New-Object W+RECT
[W]::GetWindowRect($h, [ref]$r3) | Out-Null
$inside = ($r3.Left -ge $screen.Left) -and ($r3.Top -ge $screen.Top) -and ($r3.Right -le $screen.Right) -and ($r3.Bottom -le $screen.Bottom)
Write-Output ("松手后 [{0},{1}]-[{2},{3}]  在屏内={4}" -f $r3.Left, $r3.Top, $r3.Right, $r3.Bottom, $inside)

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
