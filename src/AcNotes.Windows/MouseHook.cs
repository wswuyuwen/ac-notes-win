using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AcNotes.Windows
{
    /// <summary>
    /// 全局低级鼠标钩子（WH_MOUSE_LL），事件驱动，替代 macOS 的 30Hz 轮询。
    /// </summary>
    internal sealed class GlobalMouseHook : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private readonly LowLevelMouseProc _proc;
        private readonly GCHandle _handle; // 防止委托被 GC
        private IntPtr _hookId = IntPtr.Zero;

        public event Action<int, int>? MouseMoved;
        public event Action? LeftButtonDown;
        public event Action? LeftButtonUp;

        public GlobalMouseHook()
        {
            _proc = HookCallback;
            _handle = GCHandle.Alloc(_proc);
        }

        public bool Install()
        {
            using var curProc = Process.GetCurrentProcess();
            using var curModule = curProc.MainModule;
            IntPtr hMod = IntPtr.Zero;
            if (curModule != null)
            {
                try { hMod = GetModuleHandle(curModule.ModuleName); }
                catch { }
            }
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
            return _hookId != IntPtr.Zero;
        }

        private long _logBudget = 200;

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (_logBudget > 0)
                {
                    _logBudget--;
                    var msg = (uint)wParam.ToInt64();
                    Console.WriteLine($"[hook] nCode={nCode} msg=0x{msg:X} pt=({data.pt.x},{data.pt.y})");
                }
                var msg2 = (uint)wParam.ToInt64();
                if (msg2 == WM_LBUTTONDOWN) LeftButtonDown?.Invoke();
                else if (msg2 == WM_LBUTTONUP) LeftButtonUp?.Invoke();
                else if (msg2 == WM_MOUSEMOVE) MouseMoved?.Invoke(data.pt.x, data.pt.y);
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            if (_handle.IsAllocated) _handle.Free();
        }
    }

    /// <summary>
    /// 全局低级键盘钩子（WH_KEYBOARD_LL）：监听 Escape——面板展开后焦点在 WebView2 子窗口，
    /// 主窗口 KeyDown 收不到按键，必须系统级钩子（2026-08-04 用户反馈 Esc 收不起面板）。
    /// 只上报按键不吞键（CallNextHookEx 透传）。
    /// </summary>
    internal sealed class GlobalKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        public const int VK_ESCAPE = 0x1B;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private readonly LowLevelKeyboardProc _proc;
        private readonly GCHandle _handle;
        private IntPtr _hookId = IntPtr.Zero;

        public event Action? EscapePressed;

        public GlobalKeyboardHook()
        {
            _proc = HookCallback;
            _handle = GCHandle.Alloc(_proc);
        }

        public bool Install()
        {
            using var curProc = Process.GetCurrentProcess();
            using var curModule = curProc.MainModule;
            IntPtr hMod = IntPtr.Zero;
            if (curModule != null)
            {
                try { hMod = GetModuleHandle(curModule.ModuleName); }
                catch { }
            }
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
            return _hookId != IntPtr.Zero;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var msg = (uint)wParam.ToInt64();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (data.vkCode == VK_ESCAPE) EscapePressed?.Invoke();
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam); // 不吞键
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            if (_handle.IsAllocated) _handle.Free();
        }
    }
}
