param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe"
)

# 结论性检查：
# 1) 窗口创建出来就带 WS_EX_TOPMOST（置顶样式）
# 2) WS_EX_TOPMOST 用 SetWindowLong 改不动（返回成功但值不变），只能靠 SetWindowPos 改
#    ——所以"检测到掉了就补一下样式"这条路走不通，别在程序里做

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Q {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", SetLastError=true)] public static extern int SetWindowLong(IntPtr h, int i, int v);
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

$h = [Q]::WindowsOf([uint32]$p.Id)[0]
$ex = [Q]::GetWindowLong($h, -20)
$isTop = ($ex -band 0x8) -ne 0
Write-Output ("exstyle=0x{0:X}  WS_EX_TOPMOST={1}" -f $ex, $isTop)

$null = [Q]::SetWindowLong($h, -20, ($ex -band (-bnot 0x8)))
$after = [Q]::GetWindowLong($h, -20)
Write-Output ("尝试用 SetWindowLong 抹掉置顶位后：0x{0:X}（值不变 = 系统不接受这种改法）" -f $after)

Write-Output ("置顶样式就位 = {0}" -f $isTop)

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
