using System;
using System.Runtime.InteropServices;
using BetterCaptions.Interop;

namespace BetterCaptions.Services
{
    /// <summary>
    /// Registers a single system-wide hotkey against a supplied window handle.
    /// WM_HOTKEY messages are delivered to that window's message hook.
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        public const int HotkeyId = 1;

        private readonly IntPtr _windowHandle;
        private bool _registered;

        public HotkeyService(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
        }

        public bool IsRegistered => _registered;

        /// <summary>
        /// Registers the hotkey. Returns false and supplies a human readable
        /// <paramref name="error"/> when the combination is already taken.
        /// </summary>
        public bool Register(uint modifiers, uint virtualKey, out string? error)
        {
            Unregister();

            if (NativeMethods.RegisterHotKey(
                    _windowHandle,
                    HotkeyId,
                    modifiers | NativeMethods.MOD_NOREPEAT,
                    virtualKey))
            {
                _registered = true;
                error = null;
                return true;
            }

            int win32Error = Marshal.GetLastWin32Error();
            error =
                "Could not register the global hotkey (default Ctrl+Shift+O).\n\n" +
                "Another application is most likely already using this key combination. " +
                "Close it or edit the hotkey in %LOCALAPPDATA%\\BetterCaptions\\settings.json.\n\n" +
                $"(Win32 error {win32Error})";
            return false;
        }

        public void Unregister()
        {
            if (!_registered)
            {
                return;
            }

            NativeMethods.UnregisterHotKey(_windowHandle, HotkeyId);
            _registered = false;
        }

        public void Dispose() => Unregister();
    }
}
