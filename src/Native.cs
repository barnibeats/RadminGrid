using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace RadminGrid
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public const int GWL_STYLE = -16;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_DISABLED = 0x08000000;
        public const int ES_PASSWORD = 0x0020;

        public const uint WM_SETTEXT = 0x000C;
        public const uint WM_GETTEXT = 0x000D;
        public const uint WM_GETTEXTLENGTH = 0x000E;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_COMMAND = 0x0111;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const int BN_CLICKED = 0;
        public const int IDOK = 1;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        public const int SW_RESTORE = 9;
        public const uint GW_OWNER = 4;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong32(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLongPtr64(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong32(IntPtr h, int idx, int v);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLongPtr64(IntPtr h, int idx, IntPtr v);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        static extern IntPtr SendMessageTimeoutText(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        static extern IntPtr SendMessageTimeoutString(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        static extern IntPtr SendMessageTimeoutPtr(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);

        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);

        /// <summary>Координаты в физических пикселях на любом мониторе (важно для 4K с масштабированием).</summary>
        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }  // Per-Monitor V2
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }                  // Per-Monitor
            try { SetProcessDPIAware(); } catch { }
        }

        public static int GetStyle(IntPtr h)
        {
            return IntPtr.Size == 8 ? (int)GetWindowLongPtr64(h, GWL_STYLE).ToInt64() : GetWindowLong32(h, GWL_STYLE);
        }

        public static void SetStyle(IntPtr h, int style)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(h, GWL_STYLE, new IntPtr(style));
            else SetWindowLong32(h, GWL_STYLE, style);
        }

        public static string GetClass(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>WM_GETTEXT с таймаутом: работает для контролов чужого процесса и не виснет на зависшем окне.</summary>
        public static string GetText(IntPtr h)
        {
            IntPtr len;
            if (SendMessageTimeoutPtr(h, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out len) == IntPtr.Zero)
                return "";
            int n = (int)len.ToInt64();
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 2);
            IntPtr res;
            SendMessageTimeoutText(h, WM_GETTEXT, new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG, 500, out res);
            return sb.ToString();
        }

        public static bool SetText(IntPtr h, string text)
        {
            IntPtr res;
            return SendMessageTimeoutString(h, WM_SETTEXT, IntPtr.Zero, text ?? "", SMTO_ABORTIFHUNG, 1000, out res) != IntPtr.Zero;
        }

        public static int GetPid(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            return (int)pid;
        }

        public static List<IntPtr> TopLevelWindows()
        {
            var list = new List<IntPtr>();
            EnumWindows(delegate(IntPtr h, IntPtr l) { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        public static List<IntPtr> ChildWindows(IntPtr parent)
        {
            var list = new List<IntPtr>();
            EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) { list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        public static Rectangle GetRect(IntPtr h)
        {
            RECT r;
            if (!GetWindowRect(h, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        /// <summary>Видимая рамка окна без невидимых "resize"-границ Windows 10.</summary>
        public static Rectangle GetFrameRect(IntPtr h)
        {
            RECT r;
            try
            {
                if (DwmGetWindowAttribute(h, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out r, Marshal.SizeOf(typeof(RECT))) == 0)
                    return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            catch { }
            return GetRect(h);
        }

        public static void ClickButton(IntPtr dialog, IntPtr button)
        {
            int id = GetDlgCtrlID(button);
            PostMessage(dialog, WM_COMMAND, new IntPtr((BN_CLICKED << 16) | (id & 0xFFFF)), button);
        }

        public static void PostEnter(IntPtr h)
        {
            PostMessage(h, WM_KEYDOWN, new IntPtr(0x0D), new IntPtr(0x001C0001));
            PostMessage(h, WM_KEYUP, new IntPtr(0x0D), new IntPtr(unchecked((int)0xC01C0001)));
        }

        public static string GetProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Radmin повышен, а мы нет - UIPI заблокирует чтение/управление его окнами.</summary>
        public static bool IsBlockedByUipi(int pid)
        {
            return IsProcessElevated(pid) && !IsProcessElevated(System.Diagnostics.Process.GetCurrentProcess().Id);
        }

        /// <summary>true - процесс повышен (или недоступен).</summary>
        public static bool IsProcessElevated(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return true;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(h, 0x0008 /*TOKEN_QUERY*/, out token)) return true;
                try
                {
                    int elevated, len;
                    if (!GetTokenInformation(token, 20 /*TokenElevation*/, out elevated, 4, out len)) return false;
                    return elevated != 0;
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(h); }
        }
    }
}
