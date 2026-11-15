using System.Runtime.InteropServices;

namespace WindowsLoop;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    public const int WM_QUIT = 0x0012;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_MOVING = 0x0216;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_MOVE = 0x0003;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_NCRBUTTONUP = 0x00A5;
    public const int WM_CAPTURECHANGED = 0x0215;

    public const uint QS_ALLINPUT = 0x04FF;

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[]? pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ---- 原生弹出菜单 ----
    // 不用 WinForms 的 ContextMenuStrip：它依赖 WinForms 自己的消息循环
    // （Application.MessageLoop），而本程序是手写的 PeekMessage 泵，那套状态机起不来，
    // 菜单会进入降级状态——第一次点击只当"关掉菜单"，要点第二次才生效。
    // 原生 TrackPopupMenu 的输入完全由 Windows 自己处理，和进程的消息循环方式无关。
    public const int WM_NULL = 0x0000;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;

    public const uint MF_STRING = 0x0000;
    public const uint MF_SEPARATOR = 0x0800;

    public const uint TPM_LEFTALIGN = 0x0000;
    public const uint TPM_TOPALIGN = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);


    public const int VK_A = 0x41, VK_D = 0x44, VK_W = 0x57, VK_S = 0x53, VK_SPACE = 0x20, VK_R = 0x52, VK_M = 0x4D, VK_ESCAPE = 0x1B;
    public const int VK_UP = 0x26, VK_DOWN = 0x28;
}
