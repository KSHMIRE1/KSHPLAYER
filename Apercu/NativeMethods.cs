using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KSHPLAYER.Apercu
{
    /// <summary>Fonctions Windows utilisées par l'aperçu (clavier, fenêtres, Explorateur).</summary>
    internal static class NativeMethods
    {
        // ----- Crochet clavier bas niveau -----
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        public const int WM_TIMER = 0x0113, WM_APP = 0x8000, WM_CLOSE = 0x0010;
        public const uint LLKHF_INJECTED = 0x10, LLKHF_ALTDOWN = 0x20;
        public const int VK_SPACE = 0x20, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // ----- Boucle de messages d'un fil -----
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // ----- Fenêtres -----
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }
        public const uint GUI_INMENUMODE = 0x4, GUI_INMOVESIZE = 0x2, GUI_POPUPMENUMODE = 0x10;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        public const int SW_SHOW = 5, SW_RESTORE = 9;
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        public const uint GA_ROOT = 2;
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowTitle);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize, dwTime; }
        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        // ----- Écrans (l'aperçu s'ouvre sur l'écran de l'Explorateur) -----
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);
        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        // ----- Renvoi d'une touche Espace (quand la sélection n'est finalement pas une vidéo) -----
        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;   // le plus grand membre : donne la bonne taille à INPUT
            [FieldOffset(0)] public KEYBDINPUT ki;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }
        public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // ----- Shell : chemin → PIDL (pour resélectionner la vidéo dans l'Explorateur) -----
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr ILCreateFromPathW(string pszPath);
        [DllImport("shell32.dll")]
        public static extern IntPtr ILFindLastID(IntPtr pidl);
        [DllImport("shell32.dll")]
        public static extern void ILFree(IntPtr pidl);

        // ----- Icône de la fenêtre d'aperçu -----
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static void SetWindowIcon(IntPtr hwnd)
        {
            try
            {
                var exe = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrEmpty(exe) || ExtractIconEx(exe, 0, out var big, out var small, 1) == 0) return;
                if (big != IntPtr.Zero) PostMessage(hwnd, 0x0080, (IntPtr)1, big);       // WM_SETICON, ICON_BIG
                if (small != IntPtr.Zero) PostMessage(hwnd, 0x0080, IntPtr.Zero, small); // ICON_SMALL
            }
            catch { }
        }

        // ----- COM (fil de l'aperçu, voir PreviewWorker.Run) -----
        public const uint COINIT_APARTMENTTHREADED = 0x2;
        [DllImport("ole32.dll")]
        public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        // ----- Utilitaires -----
        public static string ClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        public static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        public static uint LastInputTick()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            return GetLastInputInfo(ref lii) ? lii.dwTime : 0;
        }
    }
}
