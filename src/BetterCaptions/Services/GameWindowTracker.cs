using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using BetterCaptions.Interop;

namespace BetterCaptions.Services
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

        // A 128x22 token is the Windows MINIMIZED placeholder, not a game window. CS:S's
        // lowest supported mode is far larger (its minimum is 640x480), so 200 is a safe
        // floor that still accepts any genuinely windowed game.
        private const double MinGameWindowSize = 200.0;

        // Windows parks minimized windows at an origin around -25600 / -32000. A fixed
        // physical-pixel threshold is used on purpose: the window rect is in PHYSICAL
        // pixels while SystemParameters.VirtualScreen* are DIPs, and mixing the two would
        // risk falsely rejecting legitimate scaled multi-monitor windows.
        private const double OffscreenCoordinateLimit = 20000.0;

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
            IntPtr handle = FindGameWindow(out Rect bounds);
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

            if (_lastBounds is null || _lastBounds.Value != bounds)
            {
                _lastBounds = bounds;
                BoundsChanged?.Invoke(bounds);
            }
        }

        private static IntPtr FindGameWindow(out Rect bounds)
        {
            bounds = Rect.Empty;

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

                            // A minimized window is unusable: it reports the placeholder
                            // rect (e.g. -25600,-25600 at 128x22) and would snap the overlay
                            // off-screen.
                            if (NativeMethods.IsIconic(handle))
                            {
                                continue;
                            }

                            if (!TryGetWindowBounds(handle, out Rect candidateBounds))
                            {
                                continue;
                            }

                            if (!IsUsableBounds(candidateBounds))
                            {
                                continue;
                            }

                            bounds = candidateBounds;
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

        /// <summary>
        /// True when the bounds are usable for snapping. Rejects the minimized placeholder
        /// rect and anything too small to be a game window.
        /// </summary>
        private static bool IsUsableBounds(Rect bounds)
        {
            if (bounds.Width < MinGameWindowSize || bounds.Height < MinGameWindowSize)
            {
                return false;
            }

            if (bounds.Left <= -OffscreenCoordinateLimit || bounds.Top <= -OffscreenCoordinateLimit
                || bounds.Left >= OffscreenCoordinateLimit || bounds.Top >= OffscreenCoordinateLimit)
            {
                return false;
            }

            return true;
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
