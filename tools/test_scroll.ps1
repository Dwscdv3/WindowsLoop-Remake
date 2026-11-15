param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_scroll.png",
    [string]$Key = "D",
    [int]$HoldMs = 4000
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class S {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
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
Start-Sleep -Milliseconds 2000

$h = ([S]::WindowsOf([uint32]$p.Id))[0]
[S]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 300

$vk = switch ($Key) { "A" { 0x41 } "D" { 0x44 } "W" { 0x57 } "S" { 0x53 } default { 0x44 } }
[S]::keybd_event($vk, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds $HoldMs
[S]::keybd_event($vk, 0, 2, [IntPtr]::Zero)
Start-Sleep -Milliseconds 400

Add-Type -AssemblyName System.Drawing
$r = New-Object S+RECT
[S]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved $Out"

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
