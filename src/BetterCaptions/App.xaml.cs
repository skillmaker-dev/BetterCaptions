using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BetterCaptions.Interop;
using BetterCaptions.Models;
using BetterCaptions.Services;

namespace BetterCaptions
{
    public partial class App : Application
    {
        private SettingsService? _settingsService;
        private MainWindow? _mainWindow;
        private TrayIconService? _tray;
        private GameWindowTracker? _tracker;
        private HotkeyService? _hotkey;
        private LiveCaptionReader? _captions;
        private LiveCaptionsWindowHider? _captionsHider;
        private AudioCaptureService? _audioCapture;
        private DirectionalAudioAnalyzer? _audioAnalyzer;

        // Last Live Captions hiding state we acted on, so ApplyLiveCaptionsHiding() only
        // reacts to a real transition of the setting or a changed target window instead of
        // re-issuing window calls on every unrelated settings change. UI thread only.
        private bool _hideLiveCaptionsApplied;
        private IntPtr _hideLiveCaptionsHandle = IntPtr.Zero;

        private SettingsWindow? _settingsWindow;

        public SettingsService SettingsService =>
            _settingsService ?? throw new InvalidOperationException("Settings service is not initialized.");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Make failures visible instead of silently dying in a tray-only app.
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            _settingsService = new SettingsService();
            AppSettings settings = _settingsService.Settings;

            MainWindow window = new MainWindow(_settingsService);
            _mainWindow = window;
            MainWindow = window;

            _tray = new TrayIconService();
            _tray.ShowRequested += () => _mainWindow?.ShowOverlay();
            _tray.HideRequested += () => _mainWindow?.HideOverlay();
            _tray.ToggleRequested += () => _mainWindow?.ToggleOverlay();
            _tray.SettingsRequested += OnSettingsRequested;
            _tray.ExitRequested += () => Shutdown();

            // The global hotkey toggles the overlay panel.
            window.HotkeyPressed += () => _mainWindow?.ToggleOverlay();

            _tracker = new GameWindowTracker();
            _tracker.BoundsChanged += bounds => _mainWindow?.SnapToGameWindow(bounds);
            _tracker.GameRunningChanged += OnGameRunningChanged;
            _tracker.Polled += OnTrackerPolled;

            // Reads Windows Live Captions via UI Automation on a background thread and
            // marshals updates back to the UI thread through this Dispatcher.
            _captions = new LiveCaptionReader(Dispatcher);
            _captions.LinesChanged += lines => _mainWindow?.SetCaptionLines(lines);
            _captions.StatusChanged += status => _mainWindow?.SetCaptionStatus(status);

            // Hides the Live Captions window (its speech engine keeps running) when the
            // user asks for it, so only the overlay's own captions are visible.
            _captionsHider = new LiveCaptionsWindowHider();
            _captions.CaptionsWindowChanged += OnCaptionsWindowChanged;

            _captions.Start();

            // Apply the persisted preference now; if Live Captions is not up yet,
            // OnCaptionsWindowChanged applies it as soon as its window appears.
            ApplyLiveCaptionsHiding();

            // Force handle creation so the global hotkey can be registered before the
            // overlay is ever shown. The window itself remains hidden.
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            _hotkey = new HotkeyService(handle);

            RegisterHotkeyWithFallback(settings);

            _tracker.Start();

            // Directional audio capture is opt-in; it is created only when the setting is
            // on and torn down again when it is switched off.
            ApplyAudioIndicators();

            // Start hidden unless the previous session left the overlay visible.
            if (settings.OverlayVisible)
            {
                window.ShowOverlay();
            }
            else
            {
                window.HideOverlay();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // ALWAYS undo our Live Captions window change before exiting, so the user's
            // system is never left modified. The restore runs off-thread with a BOUNDED wait,
            // so a slow or unresponsive target can never hang shutdown.
            _captionsHider?.RestoreAndWait(1500);

            DisposeAudio();
            _captions?.Stop();
            _captions?.Dispose();
            _hotkey?.Dispose();
            _tracker?.Stop();
            _tracker?.Dispose();
            _tray?.Dispose();
            _settingsService?.Save();
            base.OnExit(e);
        }

        /// <summary>
        /// Opens the settings window, or re-activates the existing one so the tray can
        /// never stack duplicates. The window is a normal, activatable window; it does
        /// not own the overlay, so it never becomes topmost.
        /// </summary>
        private void OnSettingsRequested()
        {
            if (_settingsService is null)
            {
                return;
            }

            if (_settingsWindow is null)
            {
                var settingsWindow = new SettingsWindow(_settingsService);
                settingsWindow.SettingsChanged += OnSettingsChanged;

                // Drop the reference when it closes so a later open creates a fresh one;
                // closing it must NOT exit the app (ShutdownMode is OnExplicitShutdown).
                settingsWindow.Closed += (_, __) =>
                {
                    if (ReferenceEquals(_settingsWindow, settingsWindow))
                    {
                        _settingsWindow = null;
                    }
                };

                _settingsWindow = settingsWindow;

                // Shown as a normal FOREGROUND desktop window. It is never topmost, so it
                // can never float over the game - it belongs on the desktop.
                settingsWindow.Show();
                settingsWindow.Activate();
            }
            else
            {
                if (_settingsWindow.WindowState == WindowState.Minimized)
                {
                    _settingsWindow.WindowState = WindowState.Normal;
                }

                _settingsWindow.Activate();
            }
        }

        /// <summary>
        /// Applies a settings-window change to the running overlay immediately. The
        /// window has already persisted the change.
        /// </summary>
        private void OnSettingsChanged()
        {
            if (_settingsService is null || _mainWindow is null)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;

            // Caption appearance, panel width/height floor and placement.
            _mainWindow.ApplyAppearanceSettings();

            // Re-apply click-through gating with the new preference.
            _mainWindow.SetClickThrough(settings.ClickThroughWhenIdle && !_mainWindow.IsOverlayVisible);

            // Hide or restore the Live Captions window when that preference changed.
            ApplyLiveCaptionsHiding();

            // Enable/disable capture and live-apply threshold/sensitivity changes without
            // restarting capture.
            ApplyAudioIndicators();
        }

        /// <summary>
        /// Creates, starts, reconfigures or tears down the directional audio pipeline
        /// according to the current settings. Threshold and sensitivity are applied to the
        /// running analyzer in place, so capture is never restarted for a settings change.
        /// </summary>
        private void ApplyAudioIndicators()
        {
            if (_settingsService is null)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;

            if (!settings.AudioIndicatorsEnabled)
            {
                DisposeAudio();
                return;
            }

            if (_audioAnalyzer is null)
            {
                _audioAnalyzer = new DirectionalAudioAnalyzer();
            }

            _audioAnalyzer.Configure(settings.AudioThresholdHz, settings.AudioSensitivity);

            if (_audioCapture is null)
            {
                var capture = new AudioCaptureService();
                capture.FrameSink = _audioAnalyzer.Process;
                _audioCapture = capture;
                capture.Start();
            }

            // Hand the ONE pipeline to the overlay so it renders the indicators from it.
            _mainWindow?.AttachAudio(_audioAnalyzer, _audioCapture);
        }

        /// <summary>
        /// Stops and disposes the directional audio pipeline. Safe to call when it was
        /// never created.
        /// </summary>
        private void DisposeAudio()
        {
            // Unsubscribe the overlay BEFORE stopping/disposing, so no event fires into a
            // closed (or closing) window.
            _mainWindow?.DetachAudio();

            if (_audioCapture is not null)
            {
                _audioCapture.FrameSink = null;
                _audioCapture.Stop();
                _audioCapture.Dispose();
                _audioCapture = null;
            }

            if (_audioAnalyzer is not null)
            {
                _audioAnalyzer.Dispose();
                _audioAnalyzer = null;
            }
        }

        /// <summary>
        /// Hides or restores the Windows Live Captions window according to the current
        /// setting. Acts ONLY on an actual transition of the setting, or when the target
        /// window genuinely changed (Live Captions started or restarted) - so unrelated
        /// settings changes (colours, gap, font size, ...) no longer re-issue window calls.
        /// The hider is idempotent as a safety net.
        /// </summary>
        private void ApplyLiveCaptionsHiding()
        {
            if (_settingsService is null || _captionsHider is null || _captions is null)
            {
                return;
            }

            bool wantHide = _settingsService.Settings.HideLiveCaptionsWindow;
            IntPtr handle = _captions.CaptionsWindowHandle;

            bool settingChanged = wantHide != _hideLiveCaptionsApplied;
            bool handleChanged = handle != IntPtr.Zero && handle != _hideLiveCaptionsHandle;

            if (!settingChanged && !handleChanged)
            {
                return; // nothing relevant changed: do not touch the window
            }

            if (wantHide)
            {
                if (handle != IntPtr.Zero)
                {
                    _captionsHider.Hide(handle);
                    _hideLiveCaptionsHandle = handle;
                }

                // Mark applied even when the window is not up yet: OnCaptionsWindowChanged
                // hides it the moment it appears.
                _hideLiveCaptionsApplied = true;
            }
            else
            {
                _captionsHider.Restore();
                _hideLiveCaptionsApplied = false;
                _hideLiveCaptionsHandle = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Applies the hiding preference whenever the Live Captions window is found,
        /// appears later, or changes (e.g. the app was restarted).
        /// </summary>
        private void OnCaptionsWindowChanged(IntPtr handle)
        {
            if (_settingsService is null || _captionsHider is null || handle == IntPtr.Zero)
            {
                return;
            }

            if (_settingsService.Settings.HideLiveCaptionsWindow)
            {
                _captionsHider.Hide(handle);
                _hideLiveCaptionsHandle = handle;
                _hideLiveCaptionsApplied = true;
            }
        }

        /// <summary>
        /// Fallback combinations tried, in order, when the configured hotkey is already
        /// taken by another application.
        /// </summary>
        private static readonly HotkeyCandidate[] FallbackHotkeys =
        {
            new HotkeyCandidate(Ctrl: true, Shift: false, Alt: true, Key: 0x4F),  // Ctrl+Alt+O
            new HotkeyCandidate(Ctrl: true, Shift: true, Alt: false, Key: 0x79),  // Ctrl+Shift+F10
            new HotkeyCandidate(Ctrl: true, Shift: true, Alt: true, Key: 0x4F),   // Ctrl+Alt+Shift+O
            new HotkeyCandidate(Ctrl: true, Shift: false, Alt: true, Key: 0x79)   // Ctrl+Alt+F10
        };

        /// <summary>
        /// Tries the configured hotkey first, then the fallback chain. On success the
        /// bound combination is persisted; on total failure the app stays usable through
        /// the tray and reports the situation with a non-blocking balloon.
        /// </summary>
        private void RegisterHotkeyWithFallback(AppSettings settings)
        {
            if (_hotkey is null)
            {
                return;
            }

            var configured = new HotkeyCandidate(
                settings.HotkeyCtrl,
                settings.HotkeyShift,
                settings.HotkeyAlt,
                settings.HotkeyKey);

            string configuredText = HotkeyFormatter.Format(
                configured.Ctrl, configured.Shift, configured.Alt, configured.Key);

            if (TryRegisterHotkey(configured))
            {
                ApplyHotkeyDisplay(configured);
                return;
            }

            foreach (HotkeyCandidate candidate in FallbackHotkeys)
            {
                if (!TryRegisterHotkey(candidate))
                {
                    continue;
                }

                // Persist the working combination so the next launch uses it directly.
                settings.HotkeyCtrl = candidate.Ctrl;
                settings.HotkeyShift = candidate.Shift;
                settings.HotkeyAlt = candidate.Alt;
                settings.HotkeyKey = candidate.Key;
                _settingsService?.Save();

                ApplyHotkeyDisplay(candidate);

                string boundText = HotkeyFormatter.Format(
                    candidate.Ctrl, candidate.Shift, candidate.Alt, candidate.Key);

                _tray?.ShowNotification(
                    "BetterCaptions",
                    $"{configuredText} is in use by another app \u2014 bound {boundText} instead.");
                return;
            }

            // Nothing registered: keep running, tray icon is the fallback control.
            _tray?.SetTooltip("BetterCaptions \u2014 no hotkey");
            _tray?.ShowNotification(
                "BetterCaptions",
                "No global hotkey available \u2014 use the tray icon to toggle the overlay.");
        }

        private bool TryRegisterHotkey(HotkeyCandidate candidate)
        {
            if (_hotkey is null)
            {
                return false;
            }

            // Register surfaces a clear error + Win32 code; the loop simply moves on.
            return _hotkey.Register(
                BuildHotkeyModifiers(candidate.Ctrl, candidate.Shift, candidate.Alt), candidate.Key, out _);
        }

        /// <summary>
        /// Builds the Win32 modifier mask for a combination. This is the single source of the
        /// Ctrl/Shift/Alt to MOD_* mapping, shared by the startup registration and the Options
        /// hotkey editor.
        /// </summary>
        private static uint BuildHotkeyModifiers(bool ctrl, bool shift, bool alt)
        {
            uint modifiers = 0;

            if (ctrl)
            {
                modifiers |= NativeMethods.MOD_CONTROL;
            }

            if (shift)
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }

            if (alt)
            {
                modifiers |= NativeMethods.MOD_ALT;
            }

            return modifiers;
        }

        /// <summary>
        /// Switches the global hotkey to the supplied combination, LIVE. Called by the Options
        /// hotkey editor. On success the new combination is registered, persisted, and
        /// reflected in the tray tooltip.
        ///
        /// On failure the PREVIOUS working hotkey is put back, so a combination that is already
        /// taken never leaves the user without a hotkey; <paramref name="error"/> carries a
        /// human-readable reason for the editor to show.
        /// </summary>
        public bool TryChangeHotkey(bool ctrl, bool shift, bool alt, uint virtualKey, out string? error)
        {
            error = null;

            if (_hotkey is null || _settingsService is null)
            {
                error = "The hotkey service is not available.";
                return false;
            }

            // Defence in depth: the editor refuses this already, but a bare key would be
            // captured system-wide, so never register one.
            if (!ctrl && !shift && !alt)
            {
                error = "Include at least one modifier (Ctrl, Alt or Shift). A bare key would be captured system-wide and block that key in every other app.";
                return false;
            }

            AppSettings settings = _settingsService.Settings;

            // Remember the currently working combination so it can be restored on failure.
            bool previousCtrl = settings.HotkeyCtrl;
            bool previousShift = settings.HotkeyShift;
            bool previousAlt = settings.HotkeyAlt;
            uint previousKey = settings.HotkeyKey;

            if (_hotkey.Register(BuildHotkeyModifiers(ctrl, shift, alt), virtualKey, out _))
            {
                settings.HotkeyCtrl = ctrl;
                settings.HotkeyShift = shift;
                settings.HotkeyAlt = alt;
                settings.HotkeyKey = virtualKey;
                _settingsService.Save();

                ApplyHotkeyDisplay(new HotkeyCandidate(ctrl, shift, alt, virtualKey));
                return true;
            }

            // The combination is taken. Restore the previous working hotkey so the user is
            // never left without one.
            string previousText = HotkeyFormatter.Format(previousCtrl, previousShift, previousAlt, previousKey);

            if (_hotkey.Register(
                    BuildHotkeyModifiers(previousCtrl, previousShift, previousAlt), previousKey, out _))
            {
                ApplyHotkeyDisplay(new HotkeyCandidate(previousCtrl, previousShift, previousAlt, previousKey));
                error = $"That combination is already in use by another application. Still using {previousText}.";
            }
            else
            {
                // Extremely unlikely: the previous combination became unavailable too. Be
                // honest in the tooltip rather than claim a hotkey that is not registered.
                _tray?.SetTooltip("BetterCaptions \u2014 no hotkey");
                error = "That combination is already in use by another application, and the previous hotkey could not be restored. Use the tray icon to toggle the overlay, or choose another combination.";
            }

            return false;
        }

        private void ApplyHotkeyDisplay(HotkeyCandidate candidate)
        {
            string text = HotkeyFormatter.Format(candidate.Ctrl, candidate.Shift, candidate.Alt, candidate.Key);
            _tray?.SetTooltip("BetterCaptions \u2014 " + text);
        }

        private readonly record struct HotkeyCandidate(bool Ctrl, bool Shift, bool Alt, uint Key);

        private void OnGameRunningChanged(bool running)
        {
            if (_mainWindow is null || _settingsService is null)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;

            if (!running)
            {
                // The game window is gone OR unusable (minimized / alt-tabbed): return the
                // overlay to the desktop instead of leaving it snapped to the minimized
                // placeholder rect (-25600,-25600 at 128x22), which hid the panel.
                _mainWindow.RestoreToDesktopBounds();
            }

            if (settings.ShowOnlyWhenGameRunning)
            {
                if (running)
                {
                    _mainWindow.ShowOverlay();
                }
                else
                {
                    _mainWindow.HideOverlay();
                }
            }
        }

        /// <summary>
        /// Runs on every tracker tick. Re-asserts the topmost z-order on the overlay
        /// whenever it is visible, so the game's own topmost fullscreen window cannot
        /// sit above it.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT gated on the game running. The Windows taskbar is itself a
        /// topmost window and the overlay is often parked near the bottom of the screen,
        /// so on the desktop the taskbar would cover it. Skipping this while no game was
        /// running is exactly why the panel used to appear only in-game.
        /// </remarks>
        private void OnTrackerPolled(bool running)
        {
            if (_mainWindow?.IsOverlayVisible == true)
            {
                _mainWindow.EnforceTopmost();
            }

            // Piggyback the Live Captions drift guard on the poll that is already running,
            // so no extra timer or event plumbing is needed.
            ReassertLiveCaptionsHiding();
        }

        /// <summary>
        /// Cheap periodic drift guard for the Live Captions window, run on every ~1 s tracker
        /// tick while the hide preference is on. A fullscreen game's display-mode change can
        /// make Windows pull the parked window back onto a monitor; this notices and re-hides
        /// it within about a second. It is a no-op when the setting is off or Live Captions is
        /// not running, and in the steady state it costs a single GetWindowRect - the hider
        /// only issues a cross-process SetWindowPos when the window has actually come back
        /// on screen. It never re-captures the remembered original rect.
        /// </summary>
        private void ReassertLiveCaptionsHiding()
        {
            if (_settingsService?.Settings.HideLiveCaptionsWindow != true
                || _captionsHider is null
                || _captions is null)
            {
                return;
            }

            IntPtr handle = _captions.CaptionsWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return; // Live Captions is not running: nothing to re-hide
            }

            if (handle != _hideLiveCaptionsHandle || !_captionsHider.IsHidden)
            {
                // A new window (Live Captions restarted), or the initial hide has not taken
                // yet: run the full guarded hide path for this handle.
                _captionsHider.Hide(handle);
            }
            else
            {
                // Same window we already hold: let the hider cheaply check for drift.
                _captionsHider.Reassert();
            }

            _hideLiveCaptionsHandle = handle;
            _hideLiveCaptionsApplied = true;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                "BetterCaptions encountered an unexpected error:\n\n" + e.Exception,
                "BetterCaptions - unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }
    }
}
