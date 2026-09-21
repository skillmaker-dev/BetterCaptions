using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BetterCaptions.Models;
using BetterCaptions.Services;

namespace BetterCaptions
{
    /// <summary>
    /// An ordinary settings window: normal chrome, appears in the taskbar, movable,
    /// closable and activatable normally. It is deliberately NOT topmost, transparent
    /// or non-activating. Edits are written to <see cref="SettingsService"/> immediately
    /// and announced via <see cref="SettingsChanged"/> so the overlay updates live.
    ///
    /// Colour editing is entirely in-box WPF: a row of preset swatch buttons plus an
    /// editable hex box. There is no WinForms colour dialog.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static readonly Color DefaultBackgroundColor = Color.FromArgb(0x8C, 0, 0, 0);

        // Border shown briefly when the hex box holds something unparseable.
        private static readonly Brush InvalidHexBrush = CreateFrozenBrush(Color.FromRgb(0xD1, 0x34, 0x38));

        // Hotkey editor status colours: a neutral hint, and the same red used for rejected
        // input. The status line explains refusals ("need a modifier") and taken combinations.
        private static readonly Brush HotkeyInfoBrush = CreateFrozenBrush(Color.FromRgb(0x55, 0x55, 0x55));
        private static readonly Brush HotkeyErrorBrush = InvalidHexBrush;

        // Fixed, neutral sample text for the live preview. Long enough (at the default font
        // size and panel width) to wrap into roughly six or more rows, so changing Max rows,
        // Gap, Font size, colours and Center text all visibly change the preview.
        private const string PreviewSampleText =
            "The presentation walks through the updated schedule for the week ahead, confirms the "
            + "meeting room reserved for Thursday afternoon, and reminds everyone to submit their "
            + "travel receipts before the end of the month so the finance team can close the quarter "
            + "on time. Please review the attached summary, note any corrections in the shared "
            + "document before Friday, and reply to the planning thread if a different time works "
            + "better for your team.";

        private readonly SettingsService _settingsService;

        // Random photo backdrop for the preview (Lorem Picsum: no API key; source.unsplash.com
        // is retired). Fetched ONCE per window, asynchronously; the solid dark backdrop in the
        // XAML is the fallback if the request fails, times out, or there is no network.
        private const string PreviewImageUrl = "https://picsum.photos/800/300";

        private static readonly HttpClient PreviewHttpClient = CreatePreviewHttpClient();

        private bool _previewImageRequested;

        // The overlay instance we read the read-only audio status from (through
        // Application.Current.MainWindow). Never mutated; unsubscribed on close.
        private MainWindow? _audioOverlay;

        // True only while the Show overlay checkbox is being synced FROM the overlay, so
        // that sync cannot re-enter the toggle handler. The handler itself is idempotent
        // (ShowOverlay/HideOverlay early-return when already in that state), but this keeps
        // the sync a pure read.
        private bool _syncingOverlayCheck;

        // True from field initialisation until the constructor has finished loading and
        // seeding every control. XAML sets slider Minimum/Maximum, which coerces Value
        // and raises ValueChanged DURING InitializeComponent - before the constructor
        // body runs and before later-declared controls exist. Handlers must no-op until
        // this flips to false.
        private bool _initializing = true;

        // True while the hotkey box is waiting for the user to press a combination. While
        // set, the window's PreviewKeyDown swallows keys and routes them to the editor.
        private bool _capturingHotkey;

        public SettingsWindow(SettingsService settingsService)
        {
            _settingsService = settingsService;
            InitializeComponent();
            LoadFromSettings();

            // Kick off the photo backdrop fetch (non-blocking; falls back silently).
            LoadPreviewImageOnceAsync();

            _initializing = false;
        }

        /// <summary>Raised after any setting has been changed and persisted.</summary>
        public event Action? SettingsChanged;

        private void LoadFromSettings()
        {
            AppSettings settings = _settingsService.Settings;
            Color background = ParseColor(settings.CaptionBackgroundColor, DefaultBackgroundColor);

            RefreshColorControls();

            BackgroundAlphaSlider.Value = background.A;
            BackgroundAlphaValue.Text = FormatAlphaPercent(background.A);

            GapSlider.Value = Math.Clamp(settings.CaptionLineGap, GapSlider.Minimum, GapSlider.Maximum);
            GapValue.Text = ((int)Math.Round(GapSlider.Value)).ToString();

            FontSizeSlider.Value = Math.Clamp(settings.CaptionFontSize, FontSizeSlider.Minimum, FontSizeSlider.Maximum);
            FontSizeValue.Text = ((int)Math.Round(FontSizeSlider.Value)).ToString();

            MaxRowsSlider.Value = Math.Clamp(settings.CaptionMaxRows, MaxRowsSlider.Minimum, MaxRowsSlider.Maximum);
            MaxRowsValue.Text = ((int)Math.Round(MaxRowsSlider.Value)).ToString();

            CenterTextCheck.IsChecked = settings.CaptionTextCentered;
            CenterPanelCheck.IsChecked = settings.PanelCenteredHorizontally;
            ClickThroughWhileVisibleCheck.IsChecked = settings.ClickThroughWhileVisible;
            HideLiveCaptionsCheck.IsChecked = settings.HideLiveCaptionsWindow;

            AudioIndicatorsCheck.IsChecked = settings.AudioIndicatorsEnabled;
            AudioThresholdSlider.Value = Math.Clamp(settings.AudioThresholdHz, AudioThresholdSlider.Minimum, AudioThresholdSlider.Maximum);
            AudioThresholdValue.Text = ((int)Math.Round(AudioThresholdSlider.Value)).ToString() + " Hz";
            AudioSensitivitySlider.Value = Math.Clamp(settings.AudioSensitivity, AudioSensitivitySlider.Minimum, AudioSensitivitySlider.Maximum);
            AudioSensitivityValue.Text = AudioSensitivitySlider.Value.ToString("0.00") + "x";
            AudioLoudnessScaleSlider.Value = Math.Clamp(settings.AudioLoudnessScale, AudioLoudnessScaleSlider.Minimum, AudioLoudnessScaleSlider.Maximum);
            AudioLoudnessScaleValue.Text = AudioLoudnessScaleSlider.Value.ToString("0.00") + "x";

            SubscribeAudioStatus();
            RenderPreview();
            UpdateAudioStatus();

            // Seed the show/hide checkbox from the overlay's real visibility.
            RefreshOverlayCheck();

            // Seed the hotkey box from the persisted combination.
            RefreshHotkeyDisplay();
        }

        private void NotifyChanged()
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Save();
            SettingsChanged?.Invoke();

            // Any appearance change re-renders the preview with the SAME shared rules.
            RenderPreview();
            UpdateAudioStatus();
        }

        // ------------------------------------------------------------------
        // Colour: preview, presets and hex entry
        // ------------------------------------------------------------------

        private void TextPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            if (TryGetPresetRgb(sender, out Color rgb))
            {
                ApplyTextRgb(rgb);
            }
        }

        private void BackgroundPreset_Click(object sender, RoutedEventArgs e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            if (TryGetPresetRgb(sender, out Color rgb))
            {
                ApplyBackgroundRgb(rgb);
            }
        }

        private void TextHexBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitTextHex();
            }
        }

        private void TextHexBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitTextHex();
        }

        private void BackgroundHexBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitBackgroundHex();
            }
        }

        private void BackgroundHexBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitBackgroundHex();
        }

        /// <summary>
        /// Applies the text RGB, keeping the configured text alpha (text is normally
        /// fully opaque). Presets and hex entry therefore only ever change the hue.
        /// </summary>
        private void ApplyTextRgb(Color rgb)
        {
            AppSettings settings = _settingsService.Settings;
            Color current = ParseColor(settings.CaptionTextColor, Colors.White);
            string previous = settings.CaptionTextColor;

            settings.CaptionTextColor = ToHex(Color.FromArgb(current.A, rgb.R, rgb.G, rgb.B));
            RefreshColorControls();

            if (!string.Equals(settings.CaptionTextColor, previous, StringComparison.OrdinalIgnoreCase))
            {
                NotifyChanged();
            }
        }

        /// <summary>
        /// Applies the background RGB, keeping the configured alpha. The alpha slider
        /// remains the single way to control background transparency.
        /// </summary>
        private void ApplyBackgroundRgb(Color rgb)
        {
            AppSettings settings = _settingsService.Settings;
            Color current = ParseColor(settings.CaptionBackgroundColor, DefaultBackgroundColor);
            string previous = settings.CaptionBackgroundColor;

            settings.CaptionBackgroundColor = ToHex(Color.FromArgb(current.A, rgb.R, rgb.G, rgb.B));
            RefreshColorControls();

            if (!string.Equals(settings.CaptionBackgroundColor, previous, StringComparison.OrdinalIgnoreCase))
            {
                NotifyChanged();
            }
        }

        private void CommitTextHex()
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            if (TryParseHexColor(TextHexBox.Text, out Color parsed))
            {
                ApplyTextRgb(parsed);
                return;
            }

            // Invalid: keep the stored value, show the box as it really is, and flag it.
            FlashInvalidHex(TextHexBox);
            TextHexBox.Text = _settingsService.Settings.CaptionTextColor;
        }

        private void CommitBackgroundHex()
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            if (TryParseHexColor(BackgroundHexBox.Text, out Color parsed))
            {
                ApplyBackgroundRgb(parsed);
                return;
            }

            FlashInvalidHex(BackgroundHexBox);
            BackgroundHexBox.Text = _settingsService.Settings.CaptionBackgroundColor;
        }

        /// <summary>Repaints both previews and both hex boxes from the stored settings.</summary>
        private void RefreshColorControls()
        {
            AppSettings settings = _settingsService.Settings;

            TextColorPreview.Background = CreateFrozenBrush(ParseColor(settings.CaptionTextColor, Colors.White));
            BackgroundColorPreview.Background = CreateFrozenBrush(ParseColor(settings.CaptionBackgroundColor, DefaultBackgroundColor));

            SetHexBoxText(TextHexBox, settings.CaptionTextColor);
            SetHexBoxText(BackgroundHexBox, settings.CaptionBackgroundColor);
        }

        private void BackgroundAlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;
            Color current = ParseColor(settings.CaptionBackgroundColor, DefaultBackgroundColor);
            byte alpha = (byte)Math.Clamp((int)Math.Round(BackgroundAlphaSlider.Value), 0, 255);

            settings.CaptionBackgroundColor = ToHex(Color.FromArgb(alpha, current.R, current.G, current.B));
            BackgroundAlphaValue.Text = FormatAlphaPercent(alpha);
            RefreshColorControls();
            NotifyChanged();
        }

        // ------------------------------------------------------------------
        // Sliders
        // ------------------------------------------------------------------

        private void GapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.CaptionLineGap = GapSlider.Value;
            GapValue.Text = ((int)Math.Round(GapSlider.Value)).ToString();
            NotifyChanged();
        }

        private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.CaptionFontSize = FontSizeSlider.Value;
            FontSizeValue.Text = ((int)Math.Round(FontSizeSlider.Value)).ToString();
            NotifyChanged();
        }

        private void MaxRowsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.CaptionMaxRows = (int)Math.Round(MaxRowsSlider.Value);
            MaxRowsValue.Text = ((int)Math.Round(MaxRowsSlider.Value)).ToString();
            NotifyChanged();
        }

        // ------------------------------------------------------------------
        // Checkboxes
        // ------------------------------------------------------------------

        /// <summary>
        /// Shows or hides the caption overlay through the overlay's own public methods -
        /// the same ones the tray menu and the global hotkey call. Deliberately does NOT
        /// call <see cref="NotifyChanged"/>: <c>ShowOverlay</c>/<c>HideOverlay</c> already
        /// persist <c>OverlayVisible</c> and re-apply click-through, so saving here too
        /// would double-save and could fight them.
        /// </summary>
        private void ShowOverlayCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing || _syncingOverlayCheck || _settingsService is null)
            {
                return;
            }

            MainWindow? overlay = Application.Current?.MainWindow as MainWindow;
            if (overlay is null)
            {
                // No reachable overlay: keep the control honest and do nothing.
                RefreshOverlayCheck();
                return;
            }

            if (ShowOverlayCheck.IsChecked == true)
            {
                overlay.ShowOverlay();
            }
            else
            {
                overlay.HideOverlay();
            }
        }

        /// <summary>
        /// Mirrors the overlay's current visibility into the checkbox and greys the control
        /// out when no overlay is reachable. Called on load and whenever this window is
        /// activated, so a show/hide made by the tray or the hotkey (or by the app at
        /// startup) cannot leave the checkbox stale. A full two-way binding is not used:
        /// the overlay has no visibility-changed event to bind against.
        /// </summary>
        private void RefreshOverlayCheck()
        {
            if (ShowOverlayCheck is null)
            {
                return;
            }

            MainWindow? overlay = Application.Current?.MainWindow as MainWindow;
            if (overlay is null)
            {
                ShowOverlayCheck.IsEnabled = false;
                return;
            }

            ShowOverlayCheck.IsEnabled = true;
            _syncingOverlayCheck = true;
            try
            {
                ShowOverlayCheck.IsChecked = overlay.IsOverlayVisible;
            }
            finally
            {
                _syncingOverlayCheck = false;
            }
        }

        private void CenterTextCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.CaptionTextCentered = CenterTextCheck.IsChecked == true;
            NotifyChanged();
        }

        /// <summary>
        /// Writes <see cref="AppSettings.PanelCenteredHorizontally"/>; a change here
        /// live-applies (the overlay re-places the panel through its placement path).
        /// </summary>
        private void CenterPanelCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.PanelCenteredHorizontally = CenterPanelCheck.IsChecked == true;
            NotifyChanged();
        }

        /// <summary>
        /// Writes <see cref="AppSettings.ClickThroughWhileVisible"/>. The overlay reconciles
        /// this into its single click-through decision the next time App re-applies settings
        /// (OnSettingsChanged calls SetClickThrough), so it takes effect immediately. The
        /// note under the checkbox says the panel cannot be moved while this is on.
        /// </summary>
        private void ClickThroughWhileVisibleCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.ClickThroughWhileVisible = ClickThroughWhileVisibleCheck.IsChecked == true;
            NotifyChanged();
        }

        /// <summary>
        /// Writes <see cref="AppSettings.HideLiveCaptionsWindow"/>; a change here is
        /// live-applied by the app (it hides or restores the Live Captions window).
        /// </summary>
        private void HideLiveCaptionsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.HideLiveCaptionsWindow = HideLiveCaptionsCheck.IsChecked == true;
            NotifyChanged();
        }

        // ------------------------------------------------------------------
        // Hotkey editor
        // ------------------------------------------------------------------

        /// <summary>
        /// Puts the hotkey box into capture mode. The window's PreviewKeyDown then swallows
        /// the next combination and routes it here, so the user simply presses the keys they
        /// want; the box itself is just a button that displays the current combination.
        /// </summary>
        private void HotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            BeginHotkeyCapture();
        }

        private void BeginHotkeyCapture()
        {
            _capturingHotkey = true;
            HotkeyButton.Content = "Press a combination...";
            HotkeyStatusText.Foreground = HotkeyInfoBrush;
            HotkeyStatusText.Text = "Press the new combination now (Esc cancels). Include at least one of Ctrl, Alt or Shift.";
            HotkeyButton.Focus();
        }

        /// <summary>
        /// Captures the combination while the box is in capture mode. It reads the modifier
        /// state at the moment a non-modifier key goes down, which is the only reliable way to
        /// know what the user is holding. Escape cancels; a bare key (no modifier) is refused
        /// with a plain reason and capture continues.
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturingHotkey)
            {
                return;
            }

            // While capturing, the editor owns the keyboard: swallow every key so it cannot
            // activate other controls, scroll, or type into the hex boxes.
            e.Handled = true;

            if (e.Key == Key.Escape)
            {
                CancelHotkeyCapture();
                return;
            }

            // With Alt held, WPF reports the real key in SystemKey rather than Key.
            Key pressed = e.Key == Key.System ? e.SystemKey : e.Key;

            // A lone modifier press is not a combination; keep waiting.
            if (IsModifierKey(pressed))
            {
                return;
            }

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            if (!ctrl && !shift && !alt)
            {
                // Refuse a bare key: registering it would swallow that key system-wide.
                HotkeyStatusText.Foreground = HotkeyErrorBrush;
                HotkeyStatusText.Text = "Include at least one modifier (Ctrl, Alt or Shift). A bare key would be captured system-wide and block that key in every other app.";
                return;
            }

            uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(pressed);
            if (virtualKey == 0)
            {
                HotkeyStatusText.Foreground = HotkeyErrorBrush;
                HotkeyStatusText.Text = "That key cannot be used as a global hotkey. Try another combination.";
                return;
            }

            ApplyHotkeyChange(ctrl, shift, alt, virtualKey);
        }

        /// <summary>
        /// Asks App to register and persist the combination live. On success the box shows the
        /// new combination. On failure the previous hotkey is still in force (App puts it back)
        /// and the reason is shown; capture stays on so the user can try another immediately.
        /// </summary>
        private void ApplyHotkeyChange(bool ctrl, bool shift, bool alt, uint virtualKey)
        {
            string attempted = HotkeyFormatter.Format(ctrl, shift, alt, virtualKey);

            if (Application.Current is not App app)
            {
                EndHotkeyCapture();
                HotkeyStatusText.Foreground = HotkeyErrorBrush;
                HotkeyStatusText.Text = "The hotkey could not be changed right now.";
                return;
            }

            if (app.TryChangeHotkey(ctrl, shift, alt, virtualKey, out string? error))
            {
                EndHotkeyCapture();
                RefreshHotkeyDisplay();
                HotkeyStatusText.Foreground = HotkeyInfoBrush;
                HotkeyStatusText.Text = $"Hotkey set to {attempted}. It is active immediately and saved.";
            }
            else
            {
                // Keep capturing so another combination can be tried at once. The button still
                // reads "Press a combination...", so it stays clear that the editor is waiting.
                HotkeyStatusText.Foreground = HotkeyErrorBrush;
                HotkeyStatusText.Text = error ?? "That combination is already in use by another application.";
            }
        }

        private void CancelHotkeyCapture()
        {
            EndHotkeyCapture();
            RefreshHotkeyDisplay();
        }

        private void EndHotkeyCapture()
        {
            _capturingHotkey = false;
        }

        /// <summary>Repaints the hotkey box from the persisted combination.</summary>
        private void RefreshHotkeyDisplay()
        {
            if (HotkeyButton is null || _settingsService is null)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;
            HotkeyButton.Content = HotkeyFormatter.Format(
                settings.HotkeyCtrl, settings.HotkeyShift, settings.HotkeyAlt, settings.HotkeyKey);

            HotkeyStatusText.Foreground = HotkeyInfoBrush;
            HotkeyStatusText.Text = "Click the box, then press the new combination. At least one of Ctrl, Alt or Shift is required.";
        }

        private static bool IsModifierKey(Key key) => key switch
        {
            Key.LeftCtrl or Key.RightCtrl => true,
            Key.LeftShift or Key.RightShift => true,
            Key.LeftAlt or Key.RightAlt => true,
            Key.LWin or Key.RWin => true,
            Key.System => true,
            _ => false
        };

        // ------------------------------------------------------------------
        // Audio
        // ------------------------------------------------------------------

        private void AudioIndicatorsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.AudioIndicatorsEnabled = AudioIndicatorsCheck.IsChecked == true;
            NotifyChanged();
        }

        private void AudioThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.AudioThresholdHz = AudioThresholdSlider.Value;
            AudioThresholdValue.Text = ((int)Math.Round(AudioThresholdSlider.Value)).ToString() + " Hz";
            NotifyChanged();
        }

        private void AudioSensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.AudioSensitivity = AudioSensitivitySlider.Value;
            AudioSensitivityValue.Text = AudioSensitivitySlider.Value.ToString("0.00") + "x";
            NotifyChanged();
        }

        /// <summary>
        /// Writes <see cref="AppSettings.AudioLoudnessScale"/>: the level used ONLY by the
        /// colour ramp is divided by this scale, so a higher value reaches the warm colours
        /// (yellow/orange/red) only on louder audio and a lower value reaches them sooner.
        /// The overlay reads it every tick, so this live-applies; brightness is unaffected.
        /// Distinct from Sensitivity, which is the overall gain and also changes brightness.
        /// </summary>
        private void AudioLoudnessScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_initializing || _settingsService is null)
            {
                return;
            }

            _settingsService.Settings.AudioLoudnessScale = AudioLoudnessScaleSlider.Value;
            AudioLoudnessScaleValue.Text = AudioLoudnessScaleSlider.Value.ToString("0.00") + "x";
            NotifyChanged();
        }

        /// <summary>
        /// Subscribes (once) to the overlay's read-only audio status event. The overlay is
        /// reached through Application.Current.MainWindow - a public surface that already
        /// exists - because App owns the audio services privately and may not be edited.
        /// </summary>
        private void SubscribeAudioStatus()
        {
            MainWindow? overlay = Application.Current?.MainWindow as MainWindow;
            if (ReferenceEquals(overlay, _audioOverlay))
            {
                return;
            }

            UnsubscribeAudioStatus();
            _audioOverlay = overlay;

            if (_audioOverlay is not null)
            {
                _audioOverlay.AudioStatusChanged += OnOverlayAudioStatusChanged;
            }
        }

        private void UnsubscribeAudioStatus()
        {
            if (_audioOverlay is not null)
            {
                _audioOverlay.AudioStatusChanged -= OnOverlayAudioStatusChanged;
                _audioOverlay = null;
            }
        }

        private void OnOverlayAudioStatusChanged()
        {
            UpdateAudioStatus();
        }

        /// <summary>Refreshes the capture status line from the overlay's read-only surface.</summary>
        private void UpdateAudioStatus()
        {
            if (AudioStatusText is null || _settingsService is null)
            {
                return;
            }

            SubscribeAudioStatus();

            if (!_settingsService.Settings.AudioIndicatorsEnabled)
            {
                AudioStatusText.Text = "Indicators are off.";
                return;
            }

            MainWindow? overlay = _audioOverlay;
            if (overlay is null || !overlay.AudioIndicatorsActive)
            {
                AudioStatusText.Text = "Starting audio capture...";
                return;
            }

            string message = overlay.AudioCaptureMessage;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = DescribeAudioState(overlay.AudioCaptureState);
            }

            if (!overlay.AudioFrontBackAvailable)
            {
                message += " Front/back indicators need 5.1/7.1 output; showing left/right only.";
            }

            AudioStatusText.Text = message;
        }

        private static string DescribeAudioState(AudioCaptureStatus state) => state switch
        {
            AudioCaptureStatus.Capturing => "Capturing system audio.",
            AudioCaptureStatus.NoDevice => "No audio output device was found.",
            AudioCaptureStatus.DeviceInvalidated => "Audio device changed; reconnecting...",
            AudioCaptureStatus.PossiblyExclusiveOrMuted =>
                "No audio detected. The output may be muted, or another app holds it in exclusive mode - "
                + "disable exclusive mode in Windows sound settings.",
            AudioCaptureStatus.Failed => "Audio capture failed.",
            _ => "Audio capture status unknown."
        };

        /// <summary>
        /// The tray and the global hotkey can show/hide the overlay while this window is
        /// open, so re-read the overlay's real state every time this window is activated:
        /// that keeps the Show overlay checkbox honest without a two-way binding.
        /// </summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            RefreshOverlayCheck();
        }

        protected override void OnClosed(EventArgs e)
        {
            UnsubscribeAudioStatus();
            base.OnClosed(e);
        }

        /// <summary>
        /// Renders the fixed sample text through the SAME shared rules the overlay uses:
        /// <see cref="CaptionTextWrapper"/> for wrapping and <see cref="CaptionRenderRules"/>
        /// for the wrap width, row window, bar appearance, colours and gap. Called on load
        /// and after every setting change, so it stays live. Display-only.
        /// </summary>
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            // Re-render once the window is realised so the DPI used for measurement is final.
            RenderPreview();
        }

        private void RenderPreview()
        {
            if (_settingsService is null || PreviewRows is null)
            {
                return;
            }

            AppSettings settings = _settingsService.Settings;

            double fontSize = Math.Clamp(settings.CaptionFontSize, CaptionRenderRules.MinFontSize, CaptionRenderRules.MaxFontSize);
            double lineHeight = fontSize * CaptionRenderRules.TightLineHeightRatio;
            double gap = Math.Max(0, settings.CaptionLineGap);
            var typeface = new Typeface(
                CaptionRenderRules.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            // The overlay's effective max-width cap (its default when unset), clamped the
            // same way, then the SAME effective wrap width it wraps to: cap minus the row
            // horizontal padding minus the 1 DIP clip gutter. This makes the row breaks
            // match the overlay exactly.
            double cap = settings.PanelMaxWidth > 0 ? settings.PanelMaxWidth : 640.0;
            cap = Math.Clamp(cap, 120.0, Math.Max(120.0, SystemParameters.PrimaryScreenWidth));
            double textArea = Math.Max(1.0, cap - CaptionRenderRules.RowPaddingHorizontal - 1.0);

            // Same rolling-window cap as the overlay (clamped to 1..10 and to the work area).
            int maxRows = CaptionRenderRules.ClampRowCount(settings.CaptionMaxRows, lineHeight, gap);

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            List<string> rows = CaptionTextWrapper.Wrap(PreviewSampleText, textArea, typeface, fontSize, pixelsPerDip);

            int visible = Math.Min(rows.Count, maxRows);
            int firstVisible = rows.Count - visible;

            // Colours, exactly as the overlay builds them.
            Color textColor = ParseColor(settings.CaptionTextColor, Colors.White);
            byte olderAlpha = (byte)Math.Round(textColor.A * CaptionRenderRules.OlderTextAlphaScale);

            Brush newestBrush = CreateFrozenBrush(textColor);
            Brush olderBrush = CreateFrozenBrush(Color.FromArgb(olderAlpha, textColor.R, textColor.G, textColor.B));
            Brush background = settings.CaptionBackgroundEnabled
                ? CreateFrozenBrush(ParseColor(settings.CaptionBackgroundColor, DefaultBackgroundColor))
                : Brushes.Transparent;
            bool textCentered = settings.CaptionTextCentered;

            PreviewRows.Children.Clear();

            // Rows are oldest-first; the bottom row is the newest (brightest).
            for (int i = 0; i < visible; i++)
            {
                bool isBottomRow = i == visible - 1;
                var row = new TextBlock { Text = rows[firstVisible + i] };

                CaptionRenderRules.ApplyRow(
                    row,
                    isBottomRow,
                    fontSize,
                    lineHeight,
                    gap,
                    isBottomRow ? newestBrush : olderBrush,
                    background,
                    textCentered);

                PreviewRows.Children.Add(row);
            }
        }

        // ------------------------------------------------------------------
        // Preview photo backdrop
        // ------------------------------------------------------------------

        private static HttpClient CreatePreviewHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "BetterCaptions/1.0");
            return client;
        }

        /// <summary>
        /// Downloads the random photo backdrop ONCE per window and applies it, off the UI
        /// thread. Every failure (offline, timeout, DNS, non-image) is swallowed: the solid
        /// dark backdrop remains, so the preview still works and nothing can block or throw.
        /// </summary>
        private async void LoadPreviewImageOnceAsync()
        {
            if (_previewImageRequested)
            {
                return;
            }

            _previewImageRequested = true;

            try
            {
                byte[] bytes = await PreviewHttpClient.GetByteArrayAsync(PreviewImageUrl);

                if (PreviewImage is null)
                {
                    return;
                }

                // Decode with OnLoad so the stream can be disposed immediately.
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }

                bitmap.Freeze();
                PreviewImage.Source = bitmap;
            }
            catch
            {
                // Offline / slow / failed / non-image response: keep the solid dark backdrop.
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static bool TryGetPresetRgb(object sender, out Color rgb)
        {
            if (sender is Button { Tag: string hex } && TryParseHexColor(hex, out rgb))
            {
                return true;
            }

            rgb = default;
            return false;
        }

        private static void SetHexBoxText(TextBox box, string value)
        {
            if (!string.Equals(box.Text, value, StringComparison.OrdinalIgnoreCase))
            {
                box.Text = value;
            }
        }

        /// <summary>
        /// Briefly outlines a hex box in red to signal rejected input, then restores it.
        /// The timer is parked on the box's Tag so a repeat flash cancels the old one.
        /// </summary>
        private static void FlashInvalidHex(TextBox box)
        {
            if (box.Tag is DispatcherTimer previousTimer)
            {
                previousTimer.Stop();
            }

            box.BorderBrush = InvalidHexBrush;
            box.BorderThickness = new Thickness(2);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                box.ClearValue(Control.BorderBrushProperty);
                box.ClearValue(Control.BorderThicknessProperty);
                box.Tag = null;
            };

            box.Tag = timer;
            timer.Start();
        }

        /// <summary>
        /// Accepts only "#RRGGBB" or "#AARRGGBB" and validates the value with
        /// <see cref="ColorConverter"/>. Anything else is rejected rather than guessed.
        /// </summary>
        private static bool TryParseHexColor(string? input, out Color color)
        {
            color = default;

            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }

            string text = input.Trim();
            if ((text.Length != 7 && text.Length != 9) || text[0] != '#')
            {
                return false;
            }

            for (int i = 1; i < text.Length; i++)
            {
                if (!Uri.IsHexDigit(text[i]))
                {
                    return false;
                }
            }

            try
            {
                if (ColorConverter.ConvertFromString(text) is Color parsed)
                {
                    color = parsed;
                    return true;
                }
            }
            catch (FormatException)
            {
                // Malformed hex: fall through to the rejection path.
            }

            return false;
        }

        private static string FormatAlphaPercent(byte alpha) =>
            ((int)Math.Round(alpha * 100.0 / 255.0)).ToString() + "%";

        private static string ToHex(Color color) =>
            $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        private static Color ParseColor(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            try
            {
                object? converted = ColorConverter.ConvertFromString(value.Trim());
                if (converted is Color color)
                {
                    return color;
                }
            }
            catch (FormatException)
            {
                // Invalid hex string: fall back to the default.
            }

            return fallback;
        }

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
