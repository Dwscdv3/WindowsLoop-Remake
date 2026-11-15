using System.Drawing;

namespace WindowsLoop;

/// <summary>一条菜单项。<see cref="Text"/> 为 null 表示分隔线，<see cref="Id"/> 是回给调用方的命令号。</summary>
internal readonly record struct MenuEntry(int Id, string? Text);

/// <summary>
/// 系统原生弹出菜单（CreatePopupMenu + TrackPopupMenu）。
///
/// 为什么不用 WinForms 的 ContextMenuStrip：它靠 WinForms 自己的消息循环来接管鼠标
/// （ToolStripManager.ModalMenuFilter 由 Application 的消息过滤器驱动），而本程序是手写的
/// PeekMessage / DispatchMessage 泵，从未调用 Application.Run，于是
/// Application.MessageLoop == false，那套状态机起不来。结果是菜单进入降级状态：
/// 第一次点击只是把菜单关掉，要点第二次菜单项才真正执行。
///
/// 原生菜单的输入、高亮、键盘导航、点到外面关闭全部由 Windows 自己处理，
/// 与进程怎么泵消息无关；而且它是阻塞调用，弹出期间游戏自然暂停。
/// </summary>
internal static class GameMenu
{
    /// <summary>屏幕坐标处弹出菜单，返回选中的命令号；没选（或取消）返回 0。</summary>
    public static int Show(IntPtr owner, Point screen, params MenuEntry[] entries)
    {
        // MSDN 明确要求：调用前先让 owner 成为前台窗口，
        // 否则点菜单外面它不会消失（菜单会一直挂着）。
        Native.SetForegroundWindow(owner);

        IntPtr menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return 0;

        try
        {
            foreach (var entry in entries)
            {
                if (entry.Text == null)
                    Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                else
                    Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)(uint)entry.Id, entry.Text);
            }

            int cmd = Native.TrackPopupMenu(menu,
                Native.TPM_LEFTALIGN | Native.TPM_TOPALIGN | Native.TPM_RIGHTBUTTON |
                Native.TPM_NONOTIFY | Native.TPM_RETURNCMD,
                screen.X, screen.Y, 0, owner, IntPtr.Zero);

            // MSDN：紧接着补一条无害消息，否则"第二次弹出的菜单会立刻消失"。
            Native.PostMessage(owner, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return cmd;
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }
}
