using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CsOverlay.Interop;
using CsOverlay.Models;
using CsOverlay.Services;

namespace CsOverlay
{
    public partial class App : Application
    {
        private SettingsService? _settingsService;
        private MainWindow? _mainWindow;
        private TrayIconService? _tray;
        private GameWindowTracker? _tracker;
        private HotkeyService? _hotkey;
        private LiveCaptionReader? _captions;

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
            _captions.Start();

            // Force handle creation so the global hotkey can be registered before the
            // overlay is ever shown. The window itself remains hidden.
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            _hotkey = new HotkeyService(handle);

            RegisterHotkeyWithFallback(settings);

            _tracker.Start();

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
                    "CsOverlay",
                    $"{configuredText} is in use by another app \u2014 bound {boundText} instead.");
                return;
            }

            // Nothing registered: keep running, tray icon is the fallback control.
            _mainWindow?.SetHotkeyDisplay(string.Empty);
            _tray?.SetTooltip("CsOverlay \u2014 no hotkey");
            _tray?.ShowNotification(
                "CsOverlay",
                "No global hotkey available \u2014 use the tray icon to toggle the overlay.");
        }

        private bool TryRegisterHotkey(HotkeyCandidate candidate)
        {
            if (_hotkey is null)
            {
                return false;
            }

            uint modifiers = 0;

            if (candidate.Ctrl)
            {
                modifiers |= NativeMethods.MOD_CONTROL;
            }

            if (candidate.Shift)
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }

            if (candidate.Alt)
            {
                modifiers |= NativeMethods.MOD_ALT;
            }

            // Register surfaces a clear error + Win32 code; the loop simply moves on.
            return _hotkey.Register(modifiers, candidate.Key, out _);
        }

        private void ApplyHotkeyDisplay(HotkeyCandidate candidate)
        {
            string text = HotkeyFormatter.Format(candidate.Ctrl, candidate.Shift, candidate.Alt, candidate.Key);
            _mainWindow?.SetHotkeyDisplay(text);
            _tray?.SetTooltip("CsOverlay \u2014 " + text);
        }

        private readonly record struct HotkeyCandidate(bool Ctrl, bool Shift, bool Alt, uint Key);

        private void OnGameRunningChanged(bool running)
        {
            if (_mainWindow is null || _settingsService is null)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;

            _mainWindow.SetGameStatus(running);

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
        /// Runs on every tracker tick. While the game is running, re-assert the topmost
        /// z-order on the overlay so the game's own topmost fullscreen window cannot sit
        /// above it. Skipped entirely when the game is not running to avoid churning
        /// z-order on the desktop.
        /// </summary>
        private void OnTrackerPolled(bool running)
        {
            if (!running)
            {
                return;
            }

            if (_mainWindow?.IsOverlayVisible == true)
            {
                _mainWindow.EnforceTopmost();
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                "CsOverlay encountered an unexpected error:\n\n" + e.Exception,
                "CsOverlay - unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true;
        }
    }
}
