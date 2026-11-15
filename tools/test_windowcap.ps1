param(
    [string]$Exe = "D:\Workspaces\WindowsLoop-Remastered\src\WindowsLoop\bin\Release\net10.0-windows\WindowsLoop.exe",
    [string]$Out = "D:\Workspaces\WindowsLoop-Remastered\tools\shot_cap.png"
)

# 验证三件事：
# 1) 窗口数量上限生效（第 4/5 次启动不会再开窗，且给出提示）
# 2) 窗口上不再有关闭按钮、不再有按键提示
# 3) 关掉任意一个窗口 => 所有实例一起退出

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
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
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
Start-Sleep -Milliseconds 1200
for ($i = 0; $i -lt 4; $i++) { Start-Process -FilePath $Exe | Out-Null; Start-Sleep -Milliseconds 900 }
Start-Sleep -Milliseconds 800

$handles = [W]::WindowsOf([uint32]$p.Id)
Write-Output ("启动 5 次后窗口数 = {0}（关卡上限应把它截断）" -f $handles.Count)

# 摆成一排，方便看清窗口里没有关闭按钮、没有按键提示
for ($i = 0; $i -lt $handles.Count; $i++) {
    [W]::SetWindowPos($handles[$i], [IntPtr]::Zero, (100 + $i * 610), 240, 0, 0, 0x0001 -bor 0x0004) | Out-Null
}
Start-Sleep -Milliseconds 700

Add-Type -AssemblyName System.Drawing
$minL = [int]::MaxValue; $minT = [int]::MaxValue; $maxR = [int]::MinValue; $maxB = [int]::MinValue
$rects = @()
foreach ($h in $handles) {
    $r = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
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
$canvas.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$canvas.Dispose()
Write-Output "saved $Out"

# 关掉其中一个 -> 应该全部退出
[W]::PostMessage($handles[0], 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE
Start-Sleep -Milliseconds 1500
$left = [W]::WindowsOf([uint32]$p.Id)
Write-Output ("发 WM_CLOSE 给一个窗口后，剩余窗口数 = {0}（应为 0）" -f $left.Count)

Get-Process -Id $p.Id -ErrorAction SilentlyContinue | ForEach-Object { Write-Output "进程仍在运行" }
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
Write-Output ("进程退出 = {0}" -f ($null -eq $alive))

Get-Process WindowsLoop -ErrorAction SilentlyContinue | Stop-Process -Force
