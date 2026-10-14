using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ButterFlow.Core;

namespace ButterFlow.Engine
{
    /// <summary>
    /// Microsecond-level low-level hook whose ONLY purpose is to swallow
    /// local input on the Host PC while the remote screen is actively controlled.
    /// Returns in &lt; 1 microsecond without heap allocation or blocking.
    /// </summary>
    public sealed class LowLevelSwallowHook : IDisposable
    {
        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private NativeMethods.HookProc _mouseProc;
        private NativeMethods.HookProc _keyboardProc;
        private volatile bool _isSwallowing = false;

        public event Action OnEmergencyEscape;

        public bool IsSwallowing
        {
            get { return _isSwallowing; }
            set { _isSwallowing = value; }
        }

        public LowLevelSwallowHook()
        {
            _mouseProc = MouseHookCallback;
            _keyboardProc = KeyboardHookCallback;
        }

        public void Install()
        {
            if (_mouseHook != IntPtr.Zero) return;

            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                IntPtr moduleHandle = NativeMethods.GetModuleHandle(curModule.ModuleName);
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, moduleHandle, 0);
                _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, moduleHandle, 0);
            }
        }

        public void Uninstall()
        {
            _isSwallowing = false;
            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            if (_keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isSwallowing)
            {
                NativeMethods.MSLLHOOKSTRUCT hookStruct = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));

                // Discard synthetic repositioning events injected by SetCursorPos
                if ((hookStruct.flags & 1) != 0)
                {
                    return (IntPtr)1;
                }

                // Microsecond swallow: return (IntPtr)1 to suppress local host mouse cursor
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isSwallowing)
            {
                NativeMethods.KBDLLHOOKSTRUCT hookStruct = (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));

                // Emergency Breakout: Ctrl + Alt + Escape or Scroll Lock (VK 0x91)
                if (hookStruct.vkCode == NativeMethods.VK_ESCAPE || hookStruct.vkCode == 0x91)
                {
                    bool ctrlHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
                    bool altHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;

                    if ((ctrlHeld && altHeld) || hookStruct.vkCode == 0x91)
                    {
                        _isSwallowing = false;
                        if (OnEmergencyEscape != null)
                        {
                            OnEmergencyEscape();
                        }
                        return (IntPtr)1;
                    }
                }

                // Swallow local keystroke so it is only typed on the remote PC
                return (IntPtr)1;
            }

            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Uninstall();
        }
    }
}
