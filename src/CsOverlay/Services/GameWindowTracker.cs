using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CsOverlay.Interop;

namespace CsOverlay.Services
{
    /// <summary>
    /// Polls once per second for the Counter-Strike: Source window and reports its
    /// on-screen bounds. Only reads window geometry - never touches game memory.
    /// </summary>
    public sealed class GameWindowTracker : IDisposable
    {
        // Checked in this order: 64-bit client first, then the 32-bit client, then
        // the shared Source engine process.
        private static readonly string[] ProcessNames = { "cstrike_win64", "cstrike", "hl2" };

        private readonly DispatcherTimer _timer;
        private bool _gameRunning;
        private Rect? _lastBounds;

        public GameWindowTracker()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _timer.Tick += (_, __) => Poll();
        }

        public bool GameRunning => _gameRunning;

        /// <summary>
        /// Raised only when a game window exists and its bounds differ from the
        /// previously reported bounds.
        /// </summary>
        public event Action<Rect>? BoundsChanged;

        /// <summary>Raised only when the "game running" state flips.</summary>
        public event Action<bool>? GameRunningChanged;

        /// <summary>
        /// Raised on EVERY poll tick with the current game-running state, so callers
        /// can periodically re-assert things (e.g. topmost z-order).
        /// </summary>
        public event Action<bool>? Polled;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        private void Poll()
        {
            IntPtr handle = FindGameWindow();
            bool running = handle != IntPtr.Zero;

            if (running != _gameRunning)
            {
                _gameRunning = running;
                GameRunningChanged?.Invoke(running);
            }

            // Every tick, regardless of change (BoundsChanged/GameRunningChanged stay change-only).
            Polled?.Invoke(running);

            if (!running)
            {
                // Forget the cached bounds so a future game session reports again.
                _lastBounds = null;
                return;
            }

            if (TryGetWindowBounds(handle, out Rect bounds))
            {
                if (_lastBounds is null || _lastBounds.Value != bounds)
                {
                    _lastBounds = bounds;
                    BoundsChanged?.Invoke(bounds);
                }
            }
        }

        private static IntPtr FindGameWindow()
        {
            foreach (string name in ProcessNames)
            {
                Process[] candidates;
                try
                {
                    candidates = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                try
                {
                    foreach (Process process in candidates)
                    {
                        try
                        {
                            IntPtr handle = process.MainWindowHandle;
                            if (handle == IntPtr.Zero)
                            {
                                continue;
                            }

                            if (!NativeMethods.IsWindowVisible(handle))
                            {
                                continue;
                            }

                            if (!TryGetWindowBounds(handle, out Rect bounds))
                            {
                                continue;
                            }

                            if (bounds.Width <= 0 || bounds.Height <= 0)
                            {
                                continue;
                            }

                            return handle;
                        }
                        catch
                        {
                            // Process exited between enumeration and inspection.
                        }
                    }
                }
                finally
                {
                    foreach (Process process in candidates)
                    {
                        process.Dispose();
                    }
                }
            }

            return IntPtr.Zero;
        }

        private static bool TryGetWindowBounds(IntPtr handle, out Rect bounds)
        {
            bounds = Rect.Empty;

            int result = NativeMethods.DwmGetWindowAttribute(
                handle,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT nativeRect,
                Marshal.SizeOf<NativeMethods.RECT>());

            if (result != 0)
            {
                if (!NativeMethods.GetWindowRect(handle, out nativeRect))
                {
                    return false;
                }
            }

            int width = nativeRect.Right - nativeRect.Left;
            int height = nativeRect.Bottom - nativeRect.Top;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            bounds = new Rect(nativeRect.Left, nativeRect.Top, width, height);
            return true;
        }

        public void Dispose() => Stop();
    }
}
