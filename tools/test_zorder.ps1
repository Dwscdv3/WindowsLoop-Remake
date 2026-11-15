param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe"
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Z {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public static List<IntPtr> Order(uint pid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
"@

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Milliseconds 1200
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Milliseconds 1200
Start-Process -FilePath $Exe | Out-Null
Start-Sleep -Milliseconds 2500

$o1 = [Z]::Order([uint32]$p.Id) -join ","
Start-Sleep -Milliseconds 2600
$o2 = [Z]::Order([uint32]$p.Id) -join ","
Start-Sleep -Milliseconds 2600
$o3 = [Z]::Order([uint32]$p.Id) -join ","

Write-Output "t0: $o1"
Write-Output "t1: $o2"
Write-Output "t2: $o3"
Write-Output ("层序稳定 = {0}" -f (($o1 -eq $o2) -and ($o2 -eq $o3)))

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
