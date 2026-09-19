using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CsOverlay.Interop;

namespace CsOverlay.Services
{
    /// <summary>
    /// Moves the Windows Live Captions top-level window just off the visible desktop, while
    /// its speech engine (and its UI Automation tree) keeps running, so only CsOverlay's own
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
        /// Idempotent: the original rect is remembered only the first time, and a repeat call
        /// does no work. The blocking window call runs on a background thread, so this never
        /// blocks the caller.
        /// </summary>
        public bool Hide(IntPtr handle)
        {
            // Cheap, local checks first: never touch a window that is not Live Captions.
            if (!IsLiveCaptionsWindow(handle))
            {
                return false;
            }

            lock (_stateGate)
            {
                if (_hidden && _hiddenHandle == handle)
                {
                    return true; // already handled by us; do not touch the remembered rect
                }

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

                // Remember the ORIGINAL on-screen rect ONCE, while the window is still on
                // screen. This is captured before (and independently of) the worker, so a
                // repeat Hide can never overwrite it with the off-screen position.
                if (!_hasOriginalRect)
                {
                    if (!NativeMethods.GetWindowRect(handle, out NativeMethods.RECT current))
                    {
                        return false; // cannot remember where to put it back: do not move it
                    }

                    _originalRect = current;
                    _hasOriginalRect = true;
                }

                // Claim the hide NOW, so a second Hide() is a no-op before the worker runs.
                _hidden = true;
                _hiddenHandle = handle;
            }

            // The actual SetWindowPos runs off the UI thread.
            _ = Task.Run(() => MoveOffScreenOnWorker(handle));
            return true;
        }

        /// <summary>
        /// Requests that a window previously moved by <see cref="Hide"/> be moved back to its
        /// remembered on-screen rect. No-op when nothing was moved by us. The blocking window
        /// call runs on a background thread, so this never blocks the caller.
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

                    // MOVE BACK ONLY, to the remembered rect. Width/height are passed so the
                    // size is restored exactly; no window style is touched.
                    NativeMethods.SetWindowPos(
                        handle,
                        IntPtr.Zero,
                        rect.Left,
                        rect.Top,
                        rect.Right - rect.Left,
                        rect.Bottom - rect.Top,
                        NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }
            }
            catch
            {
                // Never crash on window manipulation.
            }
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
