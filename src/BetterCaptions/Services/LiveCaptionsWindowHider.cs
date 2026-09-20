using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BetterCaptions.Interop;

namespace BetterCaptions.Services
{
    /// <summary>
    /// Moves the Windows Live Captions top-level window just off the visible desktop, while
    /// its speech engine (and its UI Automation tree) keeps running, so only BetterCaptions's own
    /// captions are visible.
    ///
    /// It MOVES the window; it does not minimize it. Minimizing is a WINDOW STATE transition
    /// and proved fragile in the field (Live Captions could stop across hide/unhide cycles).
    /// A move is a pure POSITION change: the window stays "shown", keeps rendering and keeps
    /// answering UI Automation - exactly what the caption reader needs. It also leaves the
    /// taskbar entry in place, so the user can always see Live Captions is still running.
    ///
    /// It deliberately does NOTHING ELSE. In particular it does not touch the window styles
    /// and never calls ShowWindow. An earlier version added WS_EX_TOOLWINDOW; that erased the
    /// window from the taskbar AND from Alt-Tab, which is indistinguishable from the app
    /// having closed, so it was removed.
    ///
    /// SetWindowPos on ANOTHER process's window is a SYNCHRONOUS cross-thread call: it posts
    /// to the target's UI thread and does not return until that thread pumps it. Running it on
    /// our UI thread would freeze the whole app, so EVERY window call here runs on the thread
    /// pool. The public methods only do cheap, local checks and then queue the call.
    ///
    /// Idempotence is strong: the ORIGINAL on-screen rect is captured once, on the first Hide,
    /// BEFORE the window is moved, and is never overwritten while we hold the window - so a
    /// repeat Hide can never replace the remembered rect with the off-screen one.
    ///
    /// DRIFT-CORRECTION: Hide decides from the window's ACTUAL current position, never from
    /// the remembered <c>_hidden</c> flag. A fullscreen game's display-mode change can make
    /// Windows pull the parked window back onto a monitor; because we re-read the rect, the
    /// next call (the periodic <see cref="Reassert"/>, ~1 s) moves it off again. The original
    /// rect is only ever captured from an ON-SCREEN position, so drift can never clobber it.
    ///
    /// It ONLY ever touches the Live Captions top-level window: every operation first
    /// re-validates that the handle is a live window whose owning process is "LiveCaptions"
    /// and whose window class is the Live Captions window class. It never kills, closes, or
    /// otherwise terminates that process, and every failure is swallowed - manipulating a
    /// window must never crash the app.
    /// </summary>
    public sealed class LiveCaptionsWindowHider
    {
        private const string LiveCaptionsProcessName = "LiveCaptions";
        private const string LiveCaptionsWindowClass = "LiveCaptionsDesktopWindow";

        // Distance past the RIGHT edge of the virtual desktop to park the window. Windows does
        // NOT clamp SetWindowPos to the work area, so any x beyond the virtual right edge is
        // genuinely off-screen; the margin keeps the origin clearly past the last visible
        // pixel for every monitor layout.
        private const int OffScreenMargin = 64;

        // Live Captions can override a SINGLE on-screen move while its own layout/animation is
        // still settling - the first SetWindowPos back to the remembered rect can be replaced by
        // the app's own transient position. So the restore re-issues the move, waits for any
        // transient override to surface, and verifies the ACTUAL rect, a bounded number of
        // times. The bound keeps RestoreAndWait's exit path from hanging. (Off-screen moves are
        // honoured on the first try, and drift is also covered by the periodic Reassert, so only
        // the restore needs this.)
        private const int RestoreAttempts = 6;
        private const int RestoreVerifyDelayMilliseconds = 100;
        private const int RestoreRetryDelayMilliseconds = 100;

        // Guards the published state. Read by IsHidden from any thread.
        private readonly object _stateGate = new object();

        // Serialises the actual window calls so a move-away and a move-back can never
        // interleave: a restore queued behind an in-flight hide still wins.
        private readonly object _windowCallGate = new object();

        // Published state. While _hidden is true we hold the window at _hiddenHandle, and
        // _originalRect is the on-screen rect captured BEFORE it was moved.
        private bool _hidden;
        private IntPtr _hiddenHandle = IntPtr.Zero;
        private bool _hasOriginalRect;
        private NativeMethods.RECT _originalRect;

        /// <summary>True while we hold the Live Captions window moved off the desktop.</summary>
        public bool IsHidden
        {
            get
            {
                lock (_stateGate)
                {
                    return _hidden;
                }
            }
        }

        /// <summary>
        /// Requests that the Live Captions window be moved off the visible desktop. Returns
        /// true when the request was accepted (including when it is already handled); false
        /// when the handle is not the Live Captions window, or its current rect could not be
        /// read (in which case it is left alone so it can always be put back).
        ///
        /// DRIFT-CORRECTING and idempotent: the decision comes from the window's CURRENT
        /// position, not from a remembered flag. If the window is on (or overlapping) the
        /// visible desktop it is moved off again - which recovers it when Windows pulls it
        /// back during a fullscreen display-mode change. If it is already parked off-screen
        /// this does no window work at all. The blocking SetWindowPos runs on a background
        /// thread, so this never blocks the caller.
        /// </summary>
        public bool Hide(IntPtr handle)
        {
            // Cheap, local checks first: never touch a window that is not Live Captions.
            if (!IsLiveCaptionsWindow(handle))
            {
                return false;
            }

            bool needsMove;

            lock (_stateGate)
            {
                // A different window than the one we hold (Live Captions restarted): the
                // remembered rect belongs to the previous window, so discard it.
                if (_hiddenHandle != handle)
                {
                    _hasOriginalRect = false;
                }

                // A minimized window has no meaningful on-screen rect to remember; leave it.
                if (NativeMethods.IsIconic(handle))
                {
                    return true;
                }

                // Decide from the window's ACTUAL position, never from _hidden. A display-mode
                // change can drag the parked window back on screen while _hidden still says
                // true, so trusting the flag is exactly the drift bug.
                if (!NativeMethods.GetWindowRect(handle, out NativeMethods.RECT current))
                {
                    return false; // cannot read where it is: do not move it
                }

                bool onScreen = OverlapsVirtualDesktop(current);

                // CRITICAL INVARIANT: capture the ORIGINAL rect only ONCE per handle, and only
                // while the window is ON SCREEN. It is never overwritten afterwards - least of
                // all by an off-screen position - so Restore can never put the window back
                // off-screen. This is independent of (and precedes) the worker.
                if (onScreen && !_hasOriginalRect)
                {
                    _originalRect = current;
                    _hasOriginalRect = true;
                }

                // Track the window we hold even when it is already parked, so Restore/exit
                // know what (if anything) is ours to put back.
                _hidden = true;
                _hiddenHandle = handle;

                // Only issue a cross-process move when the window genuinely came back onto the
                // visible desktop. While it is parked this stays false, so repeat/periodic
                // calls make no window call at all.
                needsMove = onScreen;
            }

            if (needsMove)
            {
                // The actual SetWindowPos runs off the UI thread.
                _ = Task.Run(() => MoveOffScreenOnWorker(handle));
            }

            return true;
        }

        /// <summary>
        /// Requests that a window previously moved by <see cref="Hide"/> be moved back to its
        /// remembered on-screen rect. No-op when nothing was moved by us. The blocking window
        /// call runs on a background thread, so this never blocks the caller.
        ///
        /// The move is re-issued and VERIFIED against the window's actual rect (bounded), because
        /// Live Captions can override a single on-screen move while it is settling.
        /// </summary>
        public void Restore()
        {
            IntPtr handle;
            NativeMethods.RECT rect;
            lock (_stateGate)
            {
                if (!_hidden || !_hasOriginalRect)
                {
                    return;
                }

                handle = _hiddenHandle;
                rect = _originalRect;

                _hidden = false;
                _hiddenHandle = IntPtr.Zero;
                _hasOriginalRect = false;
                _originalRect = default;
            }

            _ = Task.Run(() => MoveBackOnWorker(handle, rect));
        }

        /// <summary>
        /// Periodic drift guard, called while hiding is enabled (once per game-tracker tick,
        /// ~1 s). It is deliberately CHEAP in the common case: exactly ONE
        /// <see cref="NativeMethods.GetWindowRect"/> and nothing else while the held window is
        /// still parked off the visible desktop - no process re-validation and, crucially, no
        /// cross-process SetWindowPos. Only when the window has come back on screen does it
        /// fall through to <see cref="Hide"/>, which re-validates the window and moves it off
        /// again WITHOUT touching the remembered original rect.
        /// </summary>
        public void Reassert()
        {
            IntPtr handle;
            lock (_stateGate)
            {
                if (!_hidden || _hiddenHandle == IntPtr.Zero)
                {
                    return; // nothing held (setting off, or never hidden): nothing to do
                }

                handle = _hiddenHandle;
            }

            // ONE position read per tick. Still parked (or unreadable) => stop here.
            if (!NativeMethods.GetWindowRect(handle, out NativeMethods.RECT current)
                || !OverlapsVirtualDesktop(current))
            {
                return;
            }

            // It drifted back onto the desktop: run the full, guarded hide path.
            Hide(handle);
        }

        /// <summary>
        /// Restores the window like <see cref="Restore"/>, but waits up to
        /// <paramref name="timeoutMilliseconds"/> for the background call to finish. Used on
        /// exit so shutdown can never hang on an unresponsive target: the wait is abandoned
        /// after the timeout and shutdown continues.
        /// </summary>
        public void RestoreAndWait(int timeoutMilliseconds)
        {
            IntPtr handle;
            NativeMethods.RECT rect;
            lock (_stateGate)
            {
                if (!_hidden || !_hasOriginalRect)
                {
                    return;
                }

                handle = _hiddenHandle;
                rect = _originalRect;

                _hidden = false;
                _hiddenHandle = IntPtr.Zero;
                _hasOriginalRect = false;
                _originalRect = default;
            }

            Task restore = Task.Run(() => MoveBackOnWorker(handle, rect));
            try
            {
                restore.Wait(timeoutMilliseconds);
            }
            catch
            {
                // A bounded wait must never throw into shutdown.
            }
        }

        private void MoveOffScreenOnWorker(IntPtr handle)
        {
            try
            {
                lock (_windowCallGate)
                {
                    // A Restore may have run (or be running) since this work was queued.
                    lock (_stateGate)
                    {
                        if (!_hidden || _hiddenHandle != handle)
                        {
                            return;
                        }
                    }

                    if (!IsLiveCaptionsWindow(handle))
                    {
                        return;
                    }

                    // MOVE ONLY: SWP_NOSIZE keeps the window's size exactly as it was, and no
                    // window style is touched.
                    NativeMethods.SetWindowPos(
                        handle,
                        IntPtr.Zero,
                        ComputeOffScreenX(),
                        ComputeOffScreenY(),
                        0,
                        0,
                        NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }
            }
            catch
            {
                // Never crash on window manipulation.
            }
        }

        private void MoveBackOnWorker(IntPtr handle, NativeMethods.RECT rect)
        {
            try
            {
                lock (_windowCallGate)
                {
                    if (!IsLiveCaptionsWindow(handle))
                    {
                        // The window is gone (Live Captions exited/restarted): nothing to do.
                        return;
                    }

                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;

                    // MOVE BACK ONLY, to the remembered rect. Width/height are passed so the
                    // size is restored exactly; no window style is touched. The move is
                    // re-issued and VERIFIED because Live Captions can override the first one
                    // while it is settling - without this, Restore() would leave the window at
                    // the app's own transient position instead of the remembered original.
                    for (int attempt = 0; attempt < RestoreAttempts; attempt++)
                    {
                        NativeMethods.SetWindowPos(
                            handle,
                            IntPtr.Zero,
                            rect.Left,
                            rect.Top,
                            width,
                            height,
                            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

                        // Let a transient override by the target surface before trusting the
                        // rect; a bare immediate read can look correct and then be replaced.
                        Thread.Sleep(RestoreVerifyDelayMilliseconds);

                        if (IsAtRect(handle, rect))
                        {
                            return;
                        }

                        if (attempt < RestoreAttempts - 1)
                        {
                            Thread.Sleep(RestoreRetryDelayMilliseconds);
                        }
                    }
                }
            }
            catch
            {
                // Never crash on window manipulation.
            }
        }

        /// <summary>
        /// True when the window's CURRENT rect equals <paramref name="target"/> exactly, both
        /// position and size. Used by the restore retry to confirm the move actually stuck
        /// instead of being overridden by the target application.
        /// </summary>
        private static bool IsAtRect(IntPtr handle, NativeMethods.RECT target)
        {
            if (!NativeMethods.GetWindowRect(handle, out NativeMethods.RECT current))
            {
                return false;
            }

            return current.Left == target.Left
                && current.Top == target.Top
                && current.Right == target.Right
                && current.Bottom == target.Bottom;
        }

        /// <summary>
        /// True when the rect overlaps any part of the visible virtual desktop. A window we
        /// park sits entirely to the RIGHT of the virtual desktop, so this reports false for
        /// it and turns true the moment Windows - or the user - brings it back on screen.
        /// All values are PHYSICAL pixels, matching GetWindowRect/SetWindowPos.
        /// </summary>
        private static bool OverlapsVirtualDesktop(NativeMethods.RECT rect)
        {
            int left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            int right = left + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            int bottom = top + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

            return rect.Right > left && rect.Left < right
                && rect.Bottom > top && rect.Top < bottom;
        }

        // Just beyond the RIGHT edge of the virtual desktop, in PHYSICAL pixels (matching
        // GetWindowRect/SetWindowPos). Windows does not clamp SetWindowPos to the work area,
        // so this cannot be snapped back onto a monitor.
        private static int ComputeOffScreenX()
        {
            int virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int virtualWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            return virtualLeft + virtualWidth + OffScreenMargin;
        }

        private static int ComputeOffScreenY()
        {
            // Vertical placement is irrelevant once X is off the desktop; keep it inside the
            // virtual desktop so the window is only ever off to one side.
            return NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        }

        /// <summary>
        /// True only for a live window that belongs to the LiveCaptions process and has the
        /// Live Captions window class. This is what guarantees no other window is touched.
        /// </summary>
        private static bool IsLiveCaptionsWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            {
                return false;
            }

            try
            {
                NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
                if (pid == 0)
                {
                    return false;
                }

                using Process process = Process.GetProcessById((int)pid);
                if (!string.Equals(process.ProcessName, LiveCaptionsProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch
            {
                // Process exited or is inaccessible: do not touch the window.
                return false;
            }

            var className = new System.Text.StringBuilder(256);
            NativeMethods.GetClassName(handle, className, className.Capacity);
            return string.Equals(className.ToString(), LiveCaptionsWindowClass, StringComparison.Ordinal);
        }
    }
}
