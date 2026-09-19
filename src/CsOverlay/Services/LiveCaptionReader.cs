using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;

namespace CsOverlay.Services
{
    /// <summary>
    /// Polls Windows Live Captions (LiveCaptions.exe) via UI Automation and publishes
    /// the caption text. Read-only and fully out-of-process: it never injects, hooks,
    /// or touches the target process's memory.
    /// </summary>
    public sealed class LiveCaptionReader : IDisposable
    {
        // ------------------------------------------------------------------
        // Internal Windows implementation details. These names are NOT a stable
        // public contract and may change across Windows updates. Every failure path
        // below degrades softly (status text only) - nothing here may throw out of
        // the polling loop or crash the app.
        // ------------------------------------------------------------------

        /// <summary>Process name of the Live Captions app (System32\LiveCaptions.exe).</summary>
        private const string LiveCaptionsProcessName = "LiveCaptions";

        /// <summary>Top-level window class of the Live Captions window.</summary>
        private const string LiveCaptionsWindowClass = "LiveCaptionsDesktopWindow";

        /// <summary>AutomationId of the TextBlock that exposes the accumulated caption text.</summary>
        private const string CaptionsTextBlockAutomationId = "CaptionsTextBlock";

        private const int PollIntervalMs = 250;
        private const int ProcessMissingRetryMs = 1000;
        private const int ErrorBackoffMs = 750;

        /// <summary>
        /// If one poll iteration does not complete within this window, managed UI
        /// Automation is assumed to be wedged (it has no internal timeout) and the
        /// worker thread is abandoned. See the watchdog comment in Start().
        /// </summary>
        private const int PollWatchdogMs = 3000;

        public const int DefaultLineCount = 8;

        private const string StatusStarting = "Live captions starting...";
        private const string StatusNotRunning = "Live Captions not running \u2014 press Win+Ctrl+L";
        private const string StatusNoCaptions = "No captions yet";
        private const string StatusActive = "Live captions active";

        private readonly Dispatcher _dispatcher;
        private readonly int _lineCount;
        private readonly object _publishGate = new object();

        // Worker/threading state.
        private readonly object _workerGate = new object();
        private System.Threading.Timer? _watchdog;
        private volatile bool _running;
        private int _generation;
        private long _lastProgressMs;

        // UIA state (only touched by a worker thread).
        private AutomationElement? _cachedTextElement;

        // The resolved Live Captions TOP-LEVEL window handle (the same window this reader
        // already finds to locate the caption element). Stored as bits so it can be read
        // and written atomically across the worker and UI threads.
        private long _captionsWindowHandleBits;

        // Published state.
        private volatile string _status = StatusStarting;
        private volatile bool _available;
        private volatile string _lastRawText = string.Empty;
        private string[] _currentLines = Array.Empty<string>();

        public LiveCaptionReader(Dispatcher dispatcher, int lineCount = DefaultLineCount)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _lineCount = lineCount > 0 ? lineCount : DefaultLineCount;
        }

        /// <summary>Raised on the UI thread when the displayed caption lines change.</summary>
        public event Action<string[]>? LinesChanged;

        /// <summary>Raised on the UI thread when the status message changes.</summary>
        public event Action<string>? StatusChanged;

        /// <summary>
        /// Raised on the UI thread when the Live Captions top-level window handle becomes
        /// available, changes (e.g. the app restarted), or is lost (reported as
        /// <see cref="IntPtr.Zero"/>). Lets a consumer hide that window without doing its
        /// own search.
        /// </summary>
        public event Action<IntPtr>? CaptionsWindowChanged;

        /// <summary>
        /// The resolved Live Captions top-level window handle, or <see cref="IntPtr.Zero"/>
        /// when it has not been found (yet).
        /// </summary>
        public IntPtr CaptionsWindowHandle => new IntPtr(Interlocked.Read(ref _captionsWindowHandleBits));

        /// <summary>True when the caption text element has been located at least once.</summary>
        public bool IsAvailable => _available;

        /// <summary>Human-readable status, e.g. "Live Captions not running - press Win+Ctrl+L".</summary>
        public string Status => _status;

        /// <summary>The most recently published lines (last N, default 3).</summary>
        public string[] CurrentLines
        {
            get
            {
                lock (_publishGate)
                {
                    return (string[])_currentLines.Clone();
                }
            }
        }

        /// <summary>
        /// The single latest line. Live Captions emits partial/growing lines, so this is
        /// usually the line that is still being spoken.
        /// </summary>
        public string LatestLine
        {
            get
            {
                lock (_publishGate)
                {
                    return _currentLines.Length > 0 ? _currentLines[^1] : string.Empty;
                }
            }
        }

        public void Start()
        {
            lock (_workerGate)
            {
                if (_running)
                {
                    return;
                }

                _running = true;
                _generation++;
                _lastProgressMs = Environment.TickCount64;
                StartWorkerLocked();

                // Watchdog. Managed UI Automation has no timeout, so a wedged call can
                // block its worker thread forever. If an iteration makes no progress for
                // PollWatchdogMs we abandon that worker (background thread, never Join'd,
                // so it can neither block shutdown nor the new worker) and spawn a fresh
                // one so captions can recover. The abandoned call, if it ever returns,
                // sees a stale generation and exits without publishing.
                _watchdog = new System.Threading.Timer(
                    WatchdogTick,
                    null,
                    PollWatchdogMs,
                    PollWatchdogMs);
            }
        }

        public void Stop()
        {
            lock (_workerGate)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;

                _watchdog?.Dispose();
                _watchdog = null;

                // Invalidate any in-flight worker (including a wedged one).
                _generation++;

                // Deliberately no Join: the worker may be stuck inside a UIA call. It is
                // a background thread, so the process can still exit.
                _cachedTextElement = null;
                _available = false;
            }
        }

        public void Dispose() => Stop();

        private void WatchdogTick(object? state)
        {
            if (!_running)
            {
                return;
            }

            long idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastProgressMs);
            if (idleMs < PollWatchdogMs)
            {
                return;
            }

            lock (_workerGate)
            {
                if (!_running)
                {
                    return;
                }

                _generation++;
                Interlocked.Exchange(ref _lastProgressMs, Environment.TickCount64);
                StartWorkerLocked();
            }
        }

        private void StartWorkerLocked()
        {
            int generation = _generation;

            var worker = new Thread(() => PollLoop(generation))
            {
                IsBackground = true,
                Name = "CsOverlay.LiveCaptionReader"
            };

            // All UIA work stays on this dedicated MTA thread; the WPF UI thread must
            // never call UIA.
            worker.SetApartmentState(ApartmentState.MTA);

            worker.Start();
        }

        private void PollLoop(int generation)
        {
            while (_running && generation == Volatile.Read(ref _generation))
            {
                Interlocked.Exchange(ref _lastProgressMs, Environment.TickCount64);

                int delay = PollIntervalMs;
                try
                {
                    delay = PollOnce(generation);
                }
                catch (Exception ex)
                {
                    // Absolute safety net: the loop must never die from an unexpected throw.
                    PublishStatus("Live captions error: " + ex.Message, generation);
                    delay = ErrorBackoffMs;
                }

                Interlocked.Exchange(ref _lastProgressMs, Environment.TickCount64);

                if (!_running || generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                SleepInterruptibly(delay);
            }
        }

        private int PollOnce(int generation)
        {
            AutomationElement? textElement = GetOrResolveTextElement(out int retryDelayMs);

            if (textElement is null)
            {
                // Keep the status set by the resolver (not running / starting) and make
                // sure stale caption text does not linger on screen.
                PublishLines(Array.Empty<string>(), string.Empty, generation);
                return retryDelayMs;
            }

            string text;
            try
            {
                text = textElement.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                _cachedTextElement = null;
                return ErrorBackoffMs;
            }
            catch (COMException)
            {
                _cachedTextElement = null;
                return ErrorBackoffMs;
            }
            catch (InvalidOperationException)
            {
                _cachedTextElement = null;
                return ErrorBackoffMs;
            }

            _available = true;

            // An empty value is a normal transient state, not an error: publish an
            // empty update and keep the status meaningful.
            if (string.IsNullOrWhiteSpace(text))
            {
                PublishStatus(StatusNoCaptions, generation);
                PublishLines(Array.Empty<string>(), string.Empty, generation);
                return PollIntervalMs;
            }

            PublishStatus(StatusActive, generation);
            PublishLines(ParseLines(text), text, generation);
            return PollIntervalMs;
        }

        private AutomationElement? GetOrResolveTextElement(out int retryDelayMs)
        {
            retryDelayMs = PollIntervalMs;

            // 2. Use the cached element until it goes invalid.
            AutomationElement? cached = _cachedTextElement;
            if (cached is not null)
            {
                try
                {
                    _ = cached.Current.AutomationId;
                    return cached;
                }
                catch (ElementNotAvailableException)
                {
                    _cachedTextElement = null;
                }
                catch (InvalidOperationException)
                {
                    _cachedTextElement = null;
                }
                catch (COMException)
                {
                    _cachedTextElement = null;
                }
            }

            // 1. Re-resolve: process -> main window -> caption TextBlock.
            Process? process = FindLiveCaptionsProcess();
            if (process is null)
            {
                _available = false;
                UpdateCaptionsWindowHandle(IntPtr.Zero);
                PublishStatus(StatusNotRunning, Volatile.Read(ref _generation));
                retryDelayMs = ProcessMissingRetryMs;
                return null;
            }

            IntPtr handle;
            try
            {
                handle = process.MainWindowHandle;
            }
            finally
            {
                process.Dispose();
            }

            if (handle == IntPtr.Zero)
            {
                _available = false;
                PublishStatus(StatusStarting, Volatile.Read(ref _generation));
                retryDelayMs = ProcessMissingRetryMs;
                return null;
            }

            // Publish the resolved top-level handle (once, on change) so a consumer can
            // hide that window. No second search is performed anywhere.
            UpdateCaptionsWindowHandle(handle);

            try
            {
                AutomationElement window = AutomationElement.FromHandle(handle);
                if (window is null)
                {
                    _available = false;
                    PublishStatus(StatusStarting, Volatile.Read(ref _generation));
                    retryDelayMs = ErrorBackoffMs;
                    return null;
                }

                AutomationElement? textElement = window.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, CaptionsTextBlockAutomationId));

                if (textElement is null)
                {
                    // Live Captions is running but has not created its caption surface yet.
                    _available = false;
                    PublishStatus(StatusStarting, Volatile.Read(ref _generation));
                    retryDelayMs = ErrorBackoffMs;
                    return null;
                }

                _cachedTextElement = textElement;
                _available = true;
                return textElement;
            }
            catch (ElementNotAvailableException)
            {
                _cachedTextElement = null;
            }
            catch (COMException)
            {
                _cachedTextElement = null;
            }
            catch (InvalidOperationException)
            {
                _cachedTextElement = null;
            }

            _available = false;
            PublishStatus(StatusStarting, Volatile.Read(ref _generation));
            retryDelayMs = ErrorBackoffMs;
            return null;
        }

        private static Process? FindLiveCaptionsProcess()
        {
            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(LiveCaptionsProcessName);
            }
            catch
            {
                return null;
            }

            if (matches.Length == 0)
            {
                return null;
            }

            // Keep the first match and release the rest.
            Process chosen = matches[0];
            for (int i = 1; i < matches.Length; i++)
            {
                matches[i].Dispose();
            }

            return chosen;
        }

        /// <summary>
        /// Parses the accumulated <c>Name</c> value into non-empty, trimmed lines and
        /// returns the last <see cref="_lineCount"/> of them. Live Captions emits the
        /// rolling scrollback and the final line is usually still growing while it is
        /// being spoken, so "last N" is the meaningful window to display.
        /// </summary>
        private string[] ParseLines(string rawText)
        {
            string[] allLines = rawText
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');

            var cleaned = new List<string>(allLines.Length);
            foreach (string line in allLines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    cleaned.Add(trimmed);
                }
            }

            if (cleaned.Count <= _lineCount)
            {
                return cleaned.ToArray();
            }

            var result = new string[_lineCount];
            cleaned.CopyTo(cleaned.Count - _lineCount, result, 0, _lineCount);
            return result;
        }

        private void PublishStatus(string status, int generation)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            bool changed;
            lock (_publishGate)
            {
                changed = !string.Equals(_status, status, StringComparison.Ordinal);
                if (changed)
                {
                    _status = status;
                }
            }

            if (changed)
            {
                Post(() => StatusChanged?.Invoke(status));
            }
        }

        private void PublishLines(string[] lines, string rawText, int generation)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            // 4. Publish only when the underlying text actually changed.
            bool changed;
            lock (_publishGate)
            {
                changed = !string.Equals(_lastRawText, rawText, StringComparison.Ordinal);
                if (changed)
                {
                    _lastRawText = rawText;
                    _currentLines = lines;
                }
            }

            if (changed)
            {
                Post(() => LinesChanged?.Invoke(lines));
            }
        }

        private void Post(Action action)
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            {
                return;
            }

            // Marshal back to the UI thread; never raise on the worker thread.
            _dispatcher.BeginInvoke(action);
        }

        private void UpdateCaptionsWindowHandle(IntPtr handle)
        {
            long value = handle.ToInt64();
            if (Interlocked.Read(ref _captionsWindowHandleBits) == value)
            {
                return;
            }

            Interlocked.Exchange(ref _captionsWindowHandleBits, value);
            Post(() => CaptionsWindowChanged?.Invoke(handle));
        }

        private void SleepInterruptibly(int milliseconds)
        {
            int remaining = milliseconds;
            while (remaining > 0 && _running)
            {
                int slice = Math.Min(50, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }
    }
}
