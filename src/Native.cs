using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glint
{
    internal static class Native
    {
        // ---- hotkey / windows ----
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

        /// SetForegroundWindow is refused unless we borrow the foreground thread's input queue.
        public static void ForceForeground(IntPtr hwnd)
        {
            IntPtr fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            if (fgThread != me) AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(hwnd);
            if (fgThread != me) AttachThreadInput(me, fgThread, false);
        }

        // ---- DWM: acrylic backdrop, dark frame, rounded corners (Windows 11) ----
        [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int L, R, T, B; }
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public static bool TryAcrylic(IntPtr hwnd, bool darkMode = true)
        {
            try
            {
                int dark = darkMode ? 1 : 0, round = 2, acrylic = 3;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                if (Environment.OSVersion.Version.Build < 22621) return false;
                var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, 4) == 0;
            }
            catch { return false; }
        }

        // ---- shell icons ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        public const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        // ---- NTFS master file table and change journal ----
        public const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4, FSCTL_ENUM_USN_DATA = 0x000900b3, FSCTL_READ_USN_JOURNAL = 0x000900bb;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref MFT_ENUM_DATA_V0 inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref READ_USN_JOURNAL_DATA_V0 inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out USN_JOURNAL_DATA_V0 outBuf, int outSize, out int returned, IntPtr ov);

        [StructLayout(LayoutKind.Sequential)]
        public struct MFT_ENUM_DATA_V0 { public ulong StartFileReferenceNumber; public long LowUsn; public long HighUsn; }

        [StructLayout(LayoutKind.Sequential)]
        public struct USN_JOURNAL_DATA_V0
        {
            public ulong UsnJournalID; public long FirstUsn; public long NextUsn; public long LowestValidUsn;
            public long MaxUsn; public ulong MaximumSize; public ulong AllocationDelta;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct READ_USN_JOURNAL_DATA_V0
        {
            public long StartUsn; public uint ReasonMask; public uint ReturnOnlyOnClose; public ulong Timeout;
            public ulong BytesToWaitFor; public ulong UsnJournalID;
        }

        public const uint USN_REASON_FILE_CREATE = 0x100, USN_REASON_FILE_DELETE = 0x200,
            USN_REASON_RENAME_OLD_NAME = 0x1000, USN_REASON_RENAME_NEW_NAME = 0x2000;

        public static bool IsAdmin()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
