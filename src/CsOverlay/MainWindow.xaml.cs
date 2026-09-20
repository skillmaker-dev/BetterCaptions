using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CsOverlay.Interop;
using CsOverlay.Models;
using CsOverlay.Services;

namespace CsOverlay
{
    public partial class MainWindow : Window
    {
        private const double MinPanelWidth = 120.0;
        private const double DefaultPanelMaxWidth = 640.0;

        // The caption is rendered as a rolling window of the configured number of wrapped
        // ROWS (AppSettings.CaptionMaxRows): the recent caption text (the reader's lines
        // joined) is word-wrapped and only the LAST rows are shown, so the newest words
        // are always at the bottom and older words scroll off the top. There is no
        // ellipsis/truncation anywhere. The SHARED rules (font/row bounds, line-height
        // ratio, row chrome, wrapping and the window cap) live in CaptionRenderRules /
        // CaptionTextWrapper so the Options preview uses the exact same ones.

        // Fallback only. The real content height is the configured row window derived in
        // ComputeCaptionPanelHeight() from the applied metrics, so it cannot drift.
        private const double FallbackPanelHeight = 54.0;

        private static readonly Color DefaultCaptionBackgroundColor = Color.FromArgb(0x8C, 0, 0, 0);

        private readonly SettingsService _settingsService;

        private HwndSource? _hwndSource;
        private IntPtr _handle = IntPtr.Zero;

        private bool _clickThrough;                 // WS_EX_TRANSPARENT style currently applied
        private bool _clickThroughWanted;           // what the app's click-through gating asked for
        private bool _indicatorClickThrough;        // forced on while indicators show and the pointer is away
        private bool _overlayVisible;

        // Caption history state. The newest line is tracked so the arrival
        // animation fires only when a genuinely new line appears, not on every
        // partial update of the line that is still being spoken.
        private string _lastNewestCaption = string.Empty;
        private bool _hasCaptions;

        // The recent caption text (reader lines joined, oldest-first) that is word-wrapped
        // into the rolling row window. Kept so the rows can be re-wrapped when the font
        // size or the available width changes.
        private string _captionText = string.Empty;

        // How many rows are currently visible (0.._maxDisplayedRows).
        private int _visibleRowCount;

        // The configured rolling-window row count, clamped to CaptionRenderRules.MinRows..
        // MaxRows and reduced if necessary so the window fits the work area. Recomputed by
        // ApplyAppearanceSettings; drives the visible window, the row pool and the height.
        private int _maxDisplayedRows = 3;

        // Caption row rendering. A fixed pool of up to _maxDisplayedRows TextBlocks; each
        // holds exactly one already-wrapped row (NoWrap). The stack is bottom-aligned and
        // rows fill from the bottom, so the newest row is the last pool slot.
        private readonly List<TextBlock> _captionLines = new List<TextBlock>();

        // Applied appearance, recomputed by ApplyAppearanceSettings. Every row shares
        // these; the panel height window is derived from them.
        private double _captionFontSize = 20.0;
        private double _captionLineHeight = 24.0;
        private Typeface _rowTypeface = new Typeface("Segoe UI");

        // Extra spacing between rows (DIPs), applied as a bottom margin on every row
        // except the bottom one. Independent of font size.
        private double _captionLineGap = 0.0;

        // Caption-row text alignment.
        private bool _captionTextCentered;

        private Brush _newestTextBrush = Brushes.White;
        private Brush _olderTextBrush = Brushes.White;
        private Brush _captionBackgroundBrush = Brushes.Transparent;

        private bool _dragging;
        private Point _dragStartPoint;
        private double _dragStartLeft;
        private double _dragStartTop;

        // Panel resize state. The height is a fixed row window (configured max rows), so the
        // only resize control is the right-edge grip, which adjusts the MaxWidth cap. The
        // panel's Margin (its top-left) is never touched while resizing. The panel's own
        // Width stays unset so it keeps hugging its content.
        private enum ResizeEdge
        {
            None,
            Right
        }

        private ResizeEdge _resizeMode = ResizeEdge.None;
        private Point _resizeStartPoint;
        private double _resizeStartMaxWidth;

        private bool _overlayControlsRevealed;

        // Last centre value applied through ApplyPanelPlacement. ApplyAppearanceSettings
        // only re-runs placement when this changes, so colour/font/gap edits never touch
        // the panel's margin (which is the coupling that used to reset the width).
        private bool? _appliedPanelCentered;

        // ---- Directional audio indicators -----------------------------------------
        // Peripheral cue only: a soft gradient glow at each screen edge/corner, driven by
        // the smoothed per-direction levels. The peak opacity is deliberately low so it
        // never obscures gameplay.

        private const double MaxIndicatorOpacity = 0.8;

        // ---- Loudness colour ramp --------------------------------------------------
        // The indicator COLOUR is driven by the same 0..1 level as its opacity, so colour
        // consistently means loudness across every direction:
        //     quiet -> blue     mid -> yellow     higher -> orange     loudest -> red
        // The ramp is interpolated in HSV along the hue arc (blue -> cyan -> green ->
        // yellow -> orange -> red), NOT by a straight RGB blend between blue and red: an
        // RGB blend of two distant hues dips through a desaturated grey/purple, whereas
        // walking the hue path keeps saturation and value high throughout, so every step
        // stays vivid. The first anchor is a deliberately muted dark blue, so a very quiet
        // sound reads as a dim (not fully saturated) colour; the ramp reaches full
        // saturation once the level is clearly audible, and full-brightness red at the top.
        private const int LoudnessRampSteps = 64;

        // How loud the audio must be before the colour ramp reaches its warm end. The level
        // fed to the ramp ONLY (never to the opacity) is DIVIDED by this scale, so above 1.0
        // the warm colours (yellow/orange/red) appear only at louder audio; below 1.0 they
        // appear sooner. The bounds stop a hand-edited settings file from producing a silly
        // value (and prevent a divide-by-zero / blow-up). Default 1.0.
        private const double MinAudioLoudnessScale = 0.25;
        private const double MaxAudioLoudnessScale = 4.0;

        // (level, hue degrees, saturation 0..1, value 0..1). Hue decreases monotonically
        // along the arc, so the per-segment lerp never wraps.
        private static readonly (double Level, double Hue, double Saturation, double Value)[] LoudnessRamp =
        {
            (0.00, 215.0, 0.40, 0.35), // muted dark blue - the quiet floor
            (0.10, 208.0, 0.80, 1.00), // vivid blue (the existing accent)
            (0.45,  52.0, 1.00, 1.00), // yellow
            (0.72,  28.0, 1.00, 1.00), // orange
            (1.00,   3.0, 1.00, 1.00)  // red
        };

        // Precomputed once: one RGB colour per quantised level step. Picking a colour on the
        // ~60 Hz path is then a single array index - no allocation and no colour maths.
        private static readonly Color[] LoudnessRampColors = BuildLoudnessRampColors();

        // One entry per indicator: the stops of its own unfrozen brush plus the last
        // quantised ramp step applied, so a steady level never re-writes the stops.
        private sealed class IndicatorRampState
        {
            public IndicatorRampState(GradientStopCollection stops)
            {
                Stops = stops;
            }

            public GradientStopCollection Stops { get; }

            public int LastRampStep { get; set; } = -1;
        }

        private readonly Dictionary<Rectangle, IndicatorRampState> _indicatorRampStates =
            new Dictionary<Rectangle, IndicatorRampState>();

        // The single audio pipeline. It is OWNED by App and BOUND to this window through
        // AttachAudio; the overlay never constructs or disposes it - it only subscribes to
        // drive the indicators and to publish the read-only status surface.
        private AudioCaptureService? _audioCapture;
        private DirectionalAudioAnalyzer? _audioAnalyzer;

        // The latest snapshot is written by the analyzer's 60 Hz thread-pool event and read
        // by the UI timer. The lock is held only to copy a struct, so the ~60 Hz update path
        // allocates nothing.
        private readonly object _audioLevelsGate = new object();
        private DirectionalAudioLevels _audioLevels;

        private DispatcherTimer? _audioIndicatorTimer;
        private Rectangle[] _allIndicators = Array.Empty<Rectangle>();
        private Rectangle[] _frontBackIndicators = Array.Empty<Rectangle>();

        // Stereo output (2 ch) cannot resolve front/back: those indicators are hidden, and
        // the Options status line says so. Optimistic until the capture format is known.
        private bool _audioFrontBackAvailable = true;

        // Read-only status surface for the Options window (via Application.Current.MainWindow).
        private AudioCaptureStatus _audioCaptureState = AudioCaptureStatus.NoDevice;
        private string _audioCaptureMessage = string.Empty;

        public MainWindow(SettingsService settingsService)
        {
            _settingsService = settingsService;
            InitializeComponent();

            // Map each indicator element to its direction. Left/Right stay on every output;
            // the front/back group is hidden on stereo.
            _allIndicators = new[]
            {
                IndicatorLeft, IndicatorRight,
                IndicatorFront, IndicatorBack,
                IndicatorFrontLeft, IndicatorFrontRight,
                IndicatorBackLeft, IndicatorBackRight
            };
            _frontBackIndicators = new[]
            {
                IndicatorFront, IndicatorBack,
                IndicatorFrontLeft, IndicatorFrontRight,
                IndicatorBackLeft, IndicatorBackRight
            };

            // Give each indicator its own modifiable brush copy so the loudness ramp can
            // recolour its stops in place (once, here - never per frame).
            InitializeIndicatorRamps();

            // Pull the latest audio snapshot on the UI thread at ~60 Hz. A DispatcherTimer
            // (not a per-event BeginInvoke) keeps the update path allocation-free.
            _audioIndicatorTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _audioIndicatorTimer.Tick += OnAudioIndicatorTick;

            // No window-level Opacity is applied; only the panel brush is slightly
            // translucent, so the text stays fully opaque.

            // Apply the persisted appearance at startup. Size, placement and the centre
            // checkbox are applied in OnSourceInitialized once the canvas size is known.
            // The audio indicators are attached later, by App, once it creates the pipeline.
            ApplyAppearanceSettings();
        }

        public bool IsOverlayVisible => _overlayVisible;

        // ---- Read-only audio status for the Options window ------------------------
        // SettingsWindow reaches this instance through Application.Current.MainWindow and
        // only reads these (and subscribes to the event); it never mutates the pipeline.
        public event Action? AudioStatusChanged;

        /// <summary>True while the directional-audio pipeline is running.</summary>
        public bool AudioIndicatorsActive => _audioCapture is not null;

        /// <summary>The capture state, safe to read from the UI thread.</summary>
        public AudioCaptureStatus AudioCaptureState => _audioCaptureState;

        /// <summary>The capture's human-readable status / hint.</summary>
        public string AudioCaptureMessage => _audioCaptureMessage;

        /// <summary>
        /// False on stereo output: front/back (and the four corners) cannot be resolved, so
        /// those indicators are hidden and only left/right are shown.
        /// </summary>
        public bool AudioFrontBackAvailable => _audioFrontBackAvailable;

        /// <summary>
        /// Raised when the hotkey is pressed. App decides what the hotkey
        /// toggles (overlay panel + PiP video together); the tray keeps using the
        /// individual Show/Hide methods for panel-only control.
        /// </summary>
        public event Action? HotkeyPressed;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _handle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(_handle);
            _hwndSource?.AddHook(WndProc);

            // Tool window keeps us out of the taskbar/alt-tab; no-activate keeps the
            // overlay from ever stealing focus from the game.
            long exStyle = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
            ApplyFrameChange();

            // No game bounds are known yet, so cover the primary screen to give the
            // panel a full canvas. SnapToGameWindow overrides this later.
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            // Size and placement are applied only here (startup) and when the user
            // actually changes them (width grip / Options centre setting / position drag).
            // Placement runs first so the size clamp can never see a stale margin.
            ApplyPanelPlacement();
            ApplyPanelSize();

            // The window starts hidden: hidden overlays are pass-through when enabled.
            _overlayVisible = false;
            SetClickThrough(_settingsService.Settings.ClickThroughWhenIdle && !_overlayVisible);
        }

        protected override void OnClosed(EventArgs e)
        {
            DetachAudio();
            _hwndSource?.RemoveHook(WndProc);
            base.OnClosed(e);
        }

        /// <summary>
        /// Records the click-through state the rest of the app wants (the ClickThroughWhenIdle
        /// gating). The style actually applied also honours the forced indicator click-through;
        /// see <see cref="ApplyClickThrough"/>.
        /// </summary>
        public void SetClickThrough(bool enabled)
        {
            _clickThroughWanted = enabled;
            ApplyClickThrough();
        }

        /// <summary>
        /// Applies WS_EX_TRANSPARENT when either the app wants click-through OR the audio
        /// indicators are showing and the pointer is away from the caption panel.
        ///
        /// This is the real fix for the indicator layer swallowing clicks: the overlay is an
        /// AllowsTransparency (layered) window, and layered windows hit-test PER PIXEL, so any
        /// non-transparent pixel captures the click regardless of IsHitTestVisible. Only
        /// WS_EX_TRANSPARENT makes the OS ignore the window for hit testing.
        /// </summary>
        private void ApplyClickThrough()
        {
            bool enabled = _clickThroughWanted || _indicatorClickThrough;

            if (_handle == IntPtr.Zero || _clickThrough == enabled)
            {
                return;
            }

            long exStyle = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            if (enabled)
            {
                exStyle |= NativeMethods.WS_EX_TRANSPARENT;
            }
            else
            {
                exStyle &= ~(long)NativeMethods.WS_EX_TRANSPARENT;
            }

            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
            ApplyFrameChange();
            _clickThrough = enabled;
        }

        /// <summary>
        /// Moves and resizes the overlay to the supplied screen bounds (physical pixels),
        /// exactly matching the game window's extended frame bounds.
        /// </summary>
        public void SnapToGameWindow(Rect bounds)
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                _handle,
                IntPtr.Zero,
                (int)Math.Round(bounds.Left),
                (int)Math.Round(bounds.Top),
                (int)Math.Round(bounds.Width),
                (int)Math.Round(bounds.Height),
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

            // The canvas width changed, so the width cap (and therefore the wrap width)
            // may have changed: re-wrap the rolling rows.
            RecomputeAndRenderRows();
        }

        /// <summary>
        /// Returns the overlay to the full primary screen, exactly as the startup path in
        /// OnSourceInitialized does, and re-applies the saved panel placement/size. Used
        /// when the game window stops being usable (minimized / alt-tabbed), so the panel
        /// is not left stranded off-screen at the minimized placeholder coordinates.
        /// </summary>
        public void RestoreToDesktopBounds()
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            // Placement first so the size clamp can never see a stale margin, matching
            // the ordering used at startup.
            ApplyPanelPlacement();
            ApplyPanelSize();

            // The canvas (and so the wrap width) changed: re-wrap the rolling rows.
            RecomputeAndRenderRows();
        }

        /// <summary>
        /// Re-asserts the topmost z-order band without activating the window. The
        /// Source engine's fullscreen window is also topmost, and Windows orders that
        /// band by recency, so we periodically push ourselves back to the front.
        /// </summary>
        public void EnforceTopmost()
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                _handle,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        public void ShowOverlay()
        {
            if (_overlayVisible)
            {
                return;
            }

            Show();
            EnforceTopmost();
            _overlayVisible = true;
            SetClickThrough(_settingsService.Settings.ClickThroughWhenIdle && !_overlayVisible);
            _settingsService.Settings.OverlayVisible = true;
            _settingsService.Save();
        }

        public void HideOverlay()
        {
            if (!_overlayVisible)
            {
                return;
            }

            Hide();
            _overlayVisible = false;
            SetClickThrough(_settingsService.Settings.ClickThroughWhenIdle && !_overlayVisible);
            _settingsService.Settings.OverlayVisible = false;
            _settingsService.Save();
        }

        public void ToggleOverlay()
        {
            if (_overlayVisible)
            {
                HideOverlay();
            }
            else
            {
                ShowOverlay();
            }
        }

        /// <summary>
        /// Sets the live caption lines shown as the panel's primary content. App marshals
        /// these from the caption reader's background polling thread. Lines arrive
        /// oldest-first, so the last element is the newest (and usually still growing).
        /// The recent lines are joined and re-wrapped into the rolling row window.
        /// </summary>
        public void SetCaptionLines(string[] lines)
        {
            if (CaptionLinesPanel is null)
            {
                return;
            }

            // The reader publishes oldest-first and supplies up to its own line cap (8).
            // Join ALL of them into one rolling text: the row cap (CaptionMaxRows) is what
            // now governs how much history is visible, not a fixed line limit.
            string[] safe = lines ?? Array.Empty<string>();
            int supplied = safe.Length;

            var parts = new List<string>(supplied);
            foreach (string part in safe)
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    parts.Add(part.Trim());
                }
            }

            _captionText = string.Join(" ", parts);

            string newest = supplied > 0 ? safe[supplied - 1] : string.Empty;

            bool newestChanged = !string.Equals(newest, _lastNewestCaption, StringComparison.Ordinal);

            // Live Captions rewrites the newest line word by word. Only treat a line
            // as newly arrived when it is not simply the previous line growing, so
            // the fade/rise does not fire on every partial update.
            bool newLineArrived =
                newest.Length > 0 &&
                newestChanged &&
                (_lastNewestCaption.Length == 0 ||
                 !newest.StartsWith(_lastNewestCaption, StringComparison.Ordinal));

            _lastNewestCaption = newest;

            // Re-wrap the joined text and render the last _maxDisplayedRows rows.
            RecomputeAndRenderRows();

            _hasCaptions = _visibleRowCount > 0;
            UpdateCaptionStatusVisibility();

            if (newLineArrived && _captionLines.Count > 0)
            {
                AnimateNewestCaption(_captionLines[_captionLines.Count - 1]);
            }
        }

        /// <summary>
        /// Word-wraps the current caption text to the panel width cap and renders the LAST
        /// _maxDisplayedRows wrapped rows. Rows fill from the BOTTOM and unused rows are
        /// collapsed, so the panel stays a fixed window of that many rows and an unused row
        /// paints no bar. Called whenever the text, row count, font size or width changes.
        /// </summary>
        private void RecomputeAndRenderRows()
        {
            if (CaptionLinesPanel is null)
            {
                return;
            }

            // Wrap width is the panel's max-width cap minus one row's horizontal padding,
            // minus a 1 DIP gutter so a rendered row (TextFormattingMode=Display) can never
            // come out a hair wider than the measured width and clip at the panel edge.
            // The panel still auto-fits to the widest VISIBLE row, so a short caption stays
            // narrow rather than becoming full-width.
            double textArea = Math.Max(1.0, CurrentWrapWidth() - CaptionRenderRules.RowPaddingHorizontal - 1.0);

            // The ONE wrap implementation, shared with the Options preview.
            List<string> allRows = CaptionTextWrapper.Wrap(
                _captionText, textArea, _rowTypeface, _captionFontSize, PixelsPerDip);

            int visible = Math.Min(allRows.Count, _maxDisplayedRows);
            int firstVisible = allRows.Count - visible;
            _visibleRowCount = visible;

            EnsureRowBlocks(visible > 0 ? _maxDisplayedRows : 0);

            int poolCount = _captionLines.Count;

            for (int slot = 0; slot < poolCount; slot++)
            {
                TextBlock row = _captionLines[slot];

                // Rows fill from the BOTTOM: the last pool slot is the newest row.
                int rowsFromBottom = (poolCount - 1) - slot;
                bool hasText = rowsFromBottom < visible;

                if (!hasText)
                {
                    row.Text = string.Empty;
                    row.Visibility = Visibility.Collapsed;
                    continue;
                }

                // allRows is oldest-first, so the bottom row is the last entry.
                row.Text = allRows[firstVisible + (visible - 1 - rowsFromBottom)];
                row.Visibility = Visibility.Visible;
                ApplyRowAppearance(row, isBottomRow: slot == poolCount - 1);
            }
        }

        /// <summary>The render DPI of this visual, used for shared text measurement.</summary>
        private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

        /// <summary>
        /// The width the caption text is wrapped to: the panel's current max-width cap,
        /// clamped the same way the panel itself is. The panel still auto-fits to the
        /// widest visible row, so a short caption stays narrow.
        /// </summary>
        private double CurrentWrapWidth()
        {
            double cap = OverlayPanel.MaxWidth;

            if (double.IsNaN(cap) || double.IsInfinity(cap) || cap <= 0)
            {
                double saved = _settingsService.Settings.PanelMaxWidth > 0
                    ? _settingsService.Settings.PanelMaxWidth
                    : DefaultPanelMaxWidth;
                cap = ClampPanelMaxWidth(saved);
            }

            return Math.Max(MinPanelWidth, cap);
        }

        /// <summary>
        /// Grows the reusable row-block pool. Every block is reused across updates; unused
        /// ones are collapsed and paint no bar.
        /// </summary>
        private void EnsureRowBlocks(int count)
        {
            while (_captionLines.Count < count)
            {
                var block = new TextBlock
                {
                    RenderTransform = new TranslateTransform()
                };

                _captionLines.Add(block);
                CaptionLinesPanel.Children.Add(block);
            }
        }

        /// <summary>
        /// A single quick fade/rise for a newly arrived caption line. Restrained on
        /// purpose: this repeats constantly over live gameplay.
        /// </summary>
        private void AnimateNewestCaption(TextBlock block)
        {
            var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            block.BeginAnimation(OpacityProperty, fade);

            if (block.RenderTransform is TranslateTransform transform)
            {
                var rise = new DoubleAnimation(4.0, 0.0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                transform.BeginAnimation(TranslateTransform.YProperty, rise);
            }
        }

        /// <summary>
        /// Sets the caption status line (e.g. "Live Captions not running"). It is only
        /// shown while there is no caption text to display.
        /// </summary>
        public void SetCaptionStatus(string status)
        {
            if (CaptionStatusText is null)
            {
                return;
            }

            CaptionStatusText.Text = status ?? string.Empty;
            UpdateCaptionStatusVisibility();
        }

        private void UpdateCaptionStatusVisibility()
        {
            if (CaptionStatusText is null)
            {
                return;
            }

            // When captions are on screen the hint gets out of the way entirely, leaving
            // the panel as pure caption content.
            Visibility statusVisibility = _hasCaptions ? Visibility.Collapsed : Visibility.Visible;
            CaptionStatusText.Visibility = statusVisibility;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case NativeMethods.WM_MOUSEACTIVATE:
                    // Never activate: clicking the overlay must not pull focus off the game.
                    handled = true;
                    return new IntPtr(NativeMethods.MA_NOACTIVATE);

                case NativeMethods.WM_HOTKEY:
                    if (wParam.ToInt32() == HotkeyService.HotkeyId)
                    {
                        // Let App coordinate what the hotkey toggles.
                        HotkeyPressed?.Invoke();
                        handled = true;
                    }

                    return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private void ApplyFrameChange()
        {
            if (_handle == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.SetWindowPos(
                _handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_FRAMECHANGED |
                NativeMethods.SWP_NOMOVE |
                NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOZORDER |
                NativeMethods.SWP_NOACTIVATE);
        }

        /// <summary>
        /// Positions the slab: either the user's manually dragged X, or horizontally
        /// centered in the overlay window when the centre setting (from Options) is on.
        /// The Y position is always the user's.
        /// </summary>
        private void ApplyPanelPlacement()
        {
            var settings = _settingsService.Settings;

            if (settings.PanelCenteredHorizontally)
            {
                // Alignment does the centering, so it tracks the slab's auto-fit width
                // automatically. The left margin must be zero or it would bias it.
                OverlayPanel.HorizontalAlignment = HorizontalAlignment.Center;
                OverlayPanel.Margin = new Thickness(0, settings.PanelY, 0, 0);
            }
            else
            {
                OverlayPanel.HorizontalAlignment = HorizontalAlignment.Left;
                OverlayPanel.Margin = new Thickness(settings.PanelX, settings.PanelY, 0, 0);
            }

            // Record what placement now reflects, so ApplyAppearanceSettings can skip
            // re-applying it when only an appearance value changed.
            _appliedPanelCentered = settings.PanelCenteredHorizontally;
        }

        /// <summary>
        /// Applies the saved width cap (clamped) and the configured row-window height.
        /// Width is left unset so the slab auto-fits its (already word-wrapped) rows
        /// between MinWidth and MaxWidth; the height comes from MinHeight, which is the
        /// configured row window, so the panel stops resizing as captions change.
        /// </summary>
        private void ApplyPanelSize()
        {
            var settings = _settingsService.Settings;

            double maxWidth = settings.PanelMaxWidth > 0 ? settings.PanelMaxWidth : DefaultPanelMaxWidth;

            OverlayPanel.MinWidth = MinPanelWidth;
            OverlayPanel.MaxWidth = ClampPanelMaxWidth(maxWidth);

            // A floor, not an exact height: the panel fits its content, so a wrapped
            // line makes it taller rather than being trimmed at the top edge.
            OverlayPanel.MinHeight = ComputeCaptionPanelHeight();
        }

        // ------------------------------------------------------------------
        // Appearance (live-applied from settings)
        // ------------------------------------------------------------------

        /// <summary>
        /// Applies the persisted appearance settings to the caption rows immediately: the
        /// text/background brushes, the uniform font size / line height, the caption text
        /// alignment, the rolling-window row count, and the panel row-window height. Called
        /// at startup and whenever a setting changes. Re-wraps the rolling rows because the
        /// row count, font size and row width can change.
        ///
        /// Panel WIDTH is deliberately NOT touched here. It is owned by ApplyPanelSize(),
        /// which runs only on startup and when the user drags a resize grip; re-running it
        /// for a colour/gap edit is what used to re-clamp the saved width cap. PLACEMENT is
        /// delegated to ApplyPanelPlacement() (never set inline here) and only re-applied
        /// when the centre value actually changed, so the other appearance edits never
        /// touch the panel's margin either.
        /// </summary>
        public void ApplyAppearanceSettings()
        {
            AppSettings settings = _settingsService.Settings;

            // Metrics: every row uses the SAME font size and the same tight line height.
            // CaptionLineGap is the ONLY source of space between rows.
            double captionFontSize = Math.Clamp(settings.CaptionFontSize, CaptionRenderRules.MinFontSize, CaptionRenderRules.MaxFontSize);
            _captionFontSize = captionFontSize;
            _captionLineHeight = captionFontSize * CaptionRenderRules.TightLineHeightRatio;
            _rowTypeface = new Typeface(
                CaptionRenderRules.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            _captionLineGap = Math.Max(0, settings.CaptionLineGap);

            // The configured rolling-window row count, clamped to a sane range and reduced
            // if necessary so the window fits the work area. Set before the re-wrap and the
            // height, both of which depend on it.
            _maxDisplayedRows = ClampCaptionRowCount(settings.CaptionMaxRows);

            // Caption-row text alignment, applied to every row in ApplyRowAppearance.
            _captionTextCentered = settings.CaptionTextCentered;

            // Brushes. The older line uses the SAME hue with alpha scaled, so the
            // emphasis hierarchy survives any colour the user picks.
            Color textColor = ParseColor(settings.CaptionTextColor, Colors.White);
            byte olderAlpha = (byte)Math.Round(textColor.A * CaptionRenderRules.OlderTextAlphaScale);

            _newestTextBrush = CreateFrozenBrush(textColor);
            _olderTextBrush = CreateFrozenBrush(Color.FromArgb(olderAlpha, textColor.R, textColor.G, textColor.B));

            // Transparent keeps the (invisible) bar hit-testable, which keeps the panel
            // draggable across it; {x:Null} would not be hit-testable.
            if (settings.CaptionBackgroundEnabled)
            {
                _captionBackgroundBrush = CreateFrozenBrush(
                    ParseColor(settings.CaptionBackgroundColor, DefaultCaptionBackgroundColor));
            }
            else
            {
                _captionBackgroundBrush = Brushes.Transparent;
            }

            // Re-wrap (font size / line height / gap may have changed) and repaint the
            // rolling rows. Empty text simply renders no rows.
            RecomputeAndRenderRows();

            // The empty-state hint and the footer are the only visible content when
            // there are no captions, so they track the same settings as the bars
            // instead of their hardcoded StatusBar defaults.
            ApplyStatusAppearance();

            // The configured row window follows the font metrics and row count applied
            // above, so it belongs to the appearance path. MinWidth/MaxWidth/alignment/
            // margin are left untouched here: those belong to the size/placement paths.
            OverlayPanel.MinHeight = ComputeCaptionPanelHeight();

            // The centre setting now lives in the Options window. Route any actual
            // placement through the dedicated placement path - never by setting
            // OverlayPanel's alignment inline here. Placement is re-applied only when the
            // centre value changed, so the other appearance edits (colour/size/gap) never
            // touch the panel margin.
            if (_appliedPanelCentered != settings.PanelCenteredHorizontally)
            {
                ApplyPanelPlacement();
            }

            // Bring the audio indicator layer in line with the setting. App owns the audio
            // pipeline and calls AttachAudio/DetachAudio; this only guarantees the layer is
            // inert when the feature is off.
            ApplyAudioSettings();
        }

        /// <summary>
        /// Applies the SHARED row appearance (CaptionRenderRules.ApplyRow) so the overlay and
        /// the Options preview render identical bars: a fitted bar with its own background +
        /// padding, one line (no trimming), exact line boxes, the gap as a bottom margin, the
        /// colour tier (bottom/newest row brightest, rows above dimmer), and the
        /// CaptionTextCentered alignment (centred, or flush-left when off).
        /// </summary>
        private void ApplyRowAppearance(TextBlock row, bool isBottomRow)
        {
            CaptionRenderRules.ApplyRow(
                row,
                isBottomRow,
                _captionFontSize,
                _captionLineHeight,
                _captionLineGap,
                isBottomRow ? _newestTextBrush : _olderTextBrush,
                _captionBackgroundBrush,
                _captionTextCentered);
        }

        /// <summary>
        /// Tints the no-caption hint (<see cref="CaptionStatusText"/>) with the SAME settings
        /// as the caption bars: the configured text colour for the foreground and the
        /// configured caption background for the backer - or nothing at all when backgrounds
        /// are disabled (which still stays hit-testable so the panel remains draggable across
        /// it). The hint is the only content in the empty state, so it uses the text colour at
        /// its FULL configured alpha to stay clearly readable over a game or a desktop.
        /// </summary>
        private void ApplyStatusAppearance()
        {
            // The caption text-alignment setting is deliberately NOT applied here: the hint is
            // single-line and already centred as an element, so TextAlignment would have no
            // visible effect (it would only matter if the hint wrapped). It keeps the default
            // left text alignment.
            if (CaptionStatusText is not null)
            {
                CaptionStatusText.Foreground = _newestTextBrush;
                CaptionStatusText.Background = _captionBackgroundBrush;
            }
        }

        // ------------------------------------------------------------------
        // Directional audio indicators
        // ------------------------------------------------------------------

        /// <summary>
        /// Keeps the indicator layer in step with the feature setting. App owns the audio
        /// pipeline and drives AttachAudio/DetachAudio; here we only ensure that when the
        /// feature is off nothing lingers.
        /// </summary>
        private void ApplyAudioSettings()
        {
            if (!_settingsService.Settings.AudioIndicatorsEnabled)
            {
                DetachAudio();
            }
        }

        /// <summary>
        /// Binds the app-owned directional-audio pipeline to this window. The overlay never
        /// constructs or disposes the analyzer/capture - it subscribes to them to render the
        /// indicators and to publish the read-only status the Options window reads.
        /// </summary>
        public void AttachAudio(DirectionalAudioAnalyzer analyzer, AudioCaptureService capture)
        {
            if (analyzer is null || capture is null)
            {
                return;
            }

            if (ReferenceEquals(_audioAnalyzer, analyzer) && ReferenceEquals(_audioCapture, capture))
            {
                return; // already bound to this exact pipeline
            }

            DetachAudio();

            _audioAnalyzer = analyzer;
            _audioCapture = capture;
            _audioAnalyzer.LevelsUpdated += OnAudioLevelsUpdated;
            _audioCapture.StatusChanged += OnAudioCaptureStatusChanged;

            // Seed the status surface immediately; the capture raises changes from here on.
            _audioCaptureState = capture.Status;
            _audioCaptureMessage = capture.StatusMessage;
            _audioFrontBackAvailable = true;
            UpdateAudioFrontBackAvailability();

            if (AudioIndicatorLayer is not null)
            {
                AudioIndicatorLayer.Visibility = Visibility.Visible;
            }

            _audioIndicatorTimer?.Start();

            // Immediately make the window click-through over the freshly shown layer unless
            // the pointer is already over the panel.
            UpdateIndicatorClickThrough();

            AudioStatusChanged?.Invoke();
        }

        /// <summary>
        /// Unsubscribes from the bound pipeline and makes the indicator layer fully inert.
        /// Symmetric with <see cref="AttachAudio"/> and safe to call when nothing is bound.
        /// It never disposes the pipeline - App owns it.
        /// </summary>
        public void DetachAudio()
        {
            if (_audioAnalyzer is not null)
            {
                _audioAnalyzer.LevelsUpdated -= OnAudioLevelsUpdated;
                _audioAnalyzer = null;
            }

            if (_audioCapture is not null)
            {
                _audioCapture.StatusChanged -= OnAudioCaptureStatusChanged;
                _audioCapture = null;
            }

            _audioIndicatorTimer?.Stop();

            lock (_audioLevelsGate)
            {
                _audioLevels = default;
            }

            for (int i = 0; i < _allIndicators.Length; i++)
            {
                _allIndicators[i].Opacity = 0.0;
            }

            // Restore the front/back group so a later multichannel attach shows it again.
            for (int i = 0; i < _frontBackIndicators.Length; i++)
            {
                _frontBackIndicators[i].Visibility = Visibility.Visible;
            }

            _audioFrontBackAvailable = true;

            if (AudioIndicatorLayer is not null)
            {
                AudioIndicatorLayer.Visibility = Visibility.Collapsed;
            }

            // Drop the forced indicator click-through and return to the app's gating.
            if (_indicatorClickThrough)
            {
                _indicatorClickThrough = false;
                ApplyClickThrough();
            }

            if (_audioCaptureState != AudioCaptureStatus.NoDevice || _audioCaptureMessage.Length > 0)
            {
                _audioCaptureState = AudioCaptureStatus.NoDevice;
                _audioCaptureMessage = string.Empty;
                AudioStatusChanged?.Invoke();
            }
        }

        // Written on the analyzer's thread-pool thread at ~60 Hz; read by the UI timer. Only
        // a struct copy happens under the lock, so this path allocates nothing.
        private void OnAudioLevelsUpdated(DirectionalAudioLevels levels)
        {
            lock (_audioLevelsGate)
            {
                _audioLevels = levels;
            }
        }

        // Applied on the UI thread. Copies the latest snapshot and applies each direction's
        // level to its indicator - Opacity AND its ramp colour - directly: no per-update
        // delegate, dispatcher call, animation object or brush allocation.
        private void OnAudioIndicatorTick(object? sender, EventArgs e)
        {
            // Keep the layered window click-through everywhere except over the caption
            // panel, so the lit indicator bands never swallow game input.
            UpdateIndicatorClickThrough();

            DirectionalAudioLevels levels;
            lock (_audioLevelsGate)
            {
                levels = _audioLevels;
            }

            // Read the loudness scale once per tick, so a slider change is picked up on the
            // next frame with no restart and no extra work per indicator. It only moves the
            // ramp (colour); the opacity mapping below stays untouched.
            double loudnessScale = ClampLoudnessScale(_settingsService.Settings.AudioLoudnessScale);

            if (_audioFrontBackAvailable)
            {
                // Multichannel: each direction is already isolated by speaker channel, so
                // the per-direction levels are used directly.
                SetIndicatorIntensity(IndicatorLeft, levels.Left, loudnessScale);
                SetIndicatorIntensity(IndicatorRight, levels.Right, loudnessScale);
                SetIndicatorIntensity(IndicatorFront, levels.Front, loudnessScale);
                SetIndicatorIntensity(IndicatorBack, levels.Back, loudnessScale);
                SetIndicatorIntensity(IndicatorFrontLeft, levels.FrontLeft, loudnessScale);
                SetIndicatorIntensity(IndicatorFrontRight, levels.FrontRight, loudnessScale);
                SetIndicatorIntensity(IndicatorBackLeft, levels.BackLeft, loudnessScale);
                SetIndicatorIntensity(IndicatorBackRight, levels.BackRight, loudnessScale);
            }
            else
            {
                // Stereo: a stereo mix is not a hard switch, but driving each side from its
                // own absolute level makes both light for a one-sided sound. Drive the two
                // sides by BALANCE instead, so the dominant side wins.
                ComputeStereoBalance(levels.Left, levels.Right, out float leftIntensity, out float rightIntensity);
                SetIndicatorIntensity(IndicatorLeft, leftIntensity, loudnessScale);
                SetIndicatorIntensity(IndicatorRight, rightIntensity, loudnessScale);
            }
        }

        /// <summary>
        /// Maps the two smoothed channel levels onto the left/right indicators by BALANCE,
        /// so the side that actually dominates wins:
        ///   total = max(L, R);  pan = (R - L) / (L + R);  left = total * (1 - pan) / 2;
        ///   right = total * (1 + pan) / 2.
        /// A hard-left sound gives left = total and right = 0; a centred sound gives both =
        /// total / 2 (so a sound dead ahead still registers, equally on both sides). The
        /// inputs are the analyzer's already-smoothed / peak-held levels, so nothing jitters.
        /// </summary>
        private static void ComputeStereoBalance(float left, float right, out float leftIntensity, out float rightIntensity)
        {
            float total = Math.Max(left, right);
            if (total <= 0f)
            {
                leftIntensity = 0f;
                rightIntensity = 0f;
                return;
            }

            float pan = (right - left) / (left + right + 0.0001f); // -1 hard left, +1 hard right
            leftIntensity = total * Math.Clamp((1f - pan) * 0.5f, 0f, 1f);
            rightIntensity = total * Math.Clamp((1f + pan) * 0.5f, 0f, 1f);
        }

        /// <summary>
        /// While the indicator layer is showing, keeps the whole window click-through
        /// (WS_EX_TRANSPARENT) except when the pointer is over the caption panel - so the
        /// panel stays draggable and the indicator bands never capture input. Never forces
        /// it during a drag/resize, and does nothing when the layer is off, so the normal
        /// click-through gating is unaffected the rest of the time.
        /// </summary>
        private void UpdateIndicatorClickThrough()
        {
            bool layerShowing = AudioIndicatorLayer is not null
                && AudioIndicatorLayer.Visibility == Visibility.Visible
                && IsVisible;

            bool force = layerShowing
                && !_dragging
                && _resizeMode == ResizeEdge.None
                && !IsCursorOverPanel();

            if (force == _indicatorClickThrough)
            {
                return;
            }

            _indicatorClickThrough = force;
            ApplyClickThrough();
        }

        // Screen-space cursor test against the caption panel's bounds. It is a pure
        // transform (not input), so it still works while the window is click-through - which
        // is what lets the panel become interactive again the moment the pointer reaches it.
        private bool IsCursorOverPanel()
        {
            if (OverlayPanel is null)
            {
                return false;
            }

            System.Drawing.Point mouse = System.Windows.Forms.Control.MousePosition;
            Point local = OverlayPanel.PointFromScreen(new Point(mouse.X, mouse.Y));

            return local.X >= 0 && local.Y >= 0
                && local.X < OverlayPanel.ActualWidth
                && local.Y < OverlayPanel.ActualHeight;
        }

        /// <summary>
        /// Gives every indicator its own unfrozen copy of its gradient brush and remembers
        /// that copy's stop collection, so the per-frame colour ramp can rewrite the stop
        /// colours in place. Cloning the XAML brush keeps the exact fade geometry and the
        /// per-stop alphas; cloning also guarantees the brush is modifiable even if the
        /// resource dictionary froze the original. Runs once, at construction.
        /// </summary>
        private void InitializeIndicatorRamps()
        {
            for (int i = 0; i < _allIndicators.Length; i++)
            {
                Rectangle element = _allIndicators[i];
                GradientBrush template = element.Fill as GradientBrush
                    ?? throw new InvalidOperationException("Audio indicator must use a gradient brush.");

                // Clone() returns a modifiable copy: the fade geometry and the per-stop
                // alphas carry over untouched; only the RGB will change with the level.
                var brush = (GradientBrush)template.Clone();
                element.Fill = brush;
                _indicatorRampStates[element] = new IndicatorRampState(brush.GradientStops);
            }
        }

        /// <summary>
        /// Clamps the persisted loudness scale to the supported range. A non-finite value
        /// (only reachable from a hand-edited settings file) falls back to unity.
        /// </summary>
        private static double ClampLoudnessScale(double scale)
        {
            if (double.IsNaN(scale))
            {
                return 1.0;
            }

            return Math.Clamp(scale, MinAudioLoudnessScale, MaxAudioLoudnessScale);
        }

        /// <summary>
        /// Applies one direction's 0..1 level to its indicator. The element Opacity gets the
        /// level scaled to <see cref="MaxIndicatorOpacity"/> - unaffected by the loudness
        /// scale - while the gradient stops get the ramp colour for
        /// <c>clamp(level / loudnessScale, 0, 1)</c>, so a larger scale reserves the warm
        /// colours for louder audio without changing brightness. Only the stop RGB is
        /// rewritten: the offsets and the alphas (opaque at the screen edge/corner, fully
        /// transparent inward) are left alone, so the soft peripheral fade is preserved
        /// exactly. The colour is quantised, so a steady level does no work at all.
        /// </summary>
        private void SetIndicatorIntensity(Rectangle indicator, float level, double loudnessScale)
        {
            float clamped = Math.Clamp(level, 0f, 1f);

            if (_indicatorRampStates.TryGetValue(indicator, out IndicatorRampState? state) && state is not null)
            {
                float rampLevel = (float)Math.Clamp(clamped / loudnessScale, 0.0, 1.0);
                int step = (int)((rampLevel * (LoudnessRampSteps - 1)) + 0.5f);
                if (step != state.LastRampStep)
                {
                    state.LastRampStep = step;
                    Color ramp = LoudnessRampColors[step];
                    GradientStopCollection stops = state.Stops;
                    for (int i = 0; i < stops.Count; i++)
                    {
                        GradientStop stop = stops[i];
                        byte alpha = stop.Color.A;
                        stop.Color = Color.FromArgb(alpha, ramp.R, ramp.G, ramp.B);
                    }
                }
            }

            double target = clamped <= 0f
                ? 0.0
                : Math.Min(clamped * MaxIndicatorOpacity, MaxIndicatorOpacity);

            // Skip imperceptible changes so a steady signal does not churn the render pass.
            if (Math.Abs(indicator.Opacity - target) > 0.004)
            {
                indicator.Opacity = target;
            }
        }

        // Builds the quantised RGB ramp once. Level i maps to i / (LoudnessRampSteps - 1).
        private static Color[] BuildLoudnessRampColors()
        {
            var colors = new Color[LoudnessRampSteps];
            for (int i = 0; i < LoudnessRampSteps; i++)
            {
                colors[i] = LoudnessColor((double)i / (LoudnessRampSteps - 1));
            }

            return colors;
        }

        // Piecewise-linear interpolation of the HSV anchors: hue walks the arc, saturation
        // and value ramp too. Working in HSV (not RGB) is what keeps the mid range vivid.
        private static Color LoudnessColor(double level)
        {
            if (level <= LoudnessRamp[0].Level)
            {
                return HsvToColor(LoudnessRamp[0].Hue, LoudnessRamp[0].Saturation, LoudnessRamp[0].Value);
            }

            for (int i = 1; i < LoudnessRamp.Length; i++)
            {
                var upper = LoudnessRamp[i];
                if (level <= upper.Level)
                {
                    var lower = LoudnessRamp[i - 1];
                    double t = (level - lower.Level) / (upper.Level - lower.Level);
                    return HsvToColor(
                        lower.Hue + ((upper.Hue - lower.Hue) * t),
                        lower.Saturation + ((upper.Saturation - lower.Saturation) * t),
                        lower.Value + ((upper.Value - lower.Value) * t));
                }
            }

            var last = LoudnessRamp[LoudnessRamp.Length - 1];
            return HsvToColor(last.Hue, last.Saturation, last.Value);
        }

        private static Color HsvToColor(double hue, double saturation, double value)
        {
            hue = ((hue % 360.0) + 360.0) % 360.0;
            saturation = Math.Clamp(saturation, 0.0, 1.0);
            value = Math.Clamp(value, 0.0, 1.0);

            double chroma = value * saturation;
            double sector = hue / 60.0;
            double second = chroma * (1.0 - Math.Abs((sector % 2.0) - 1.0));
            double match = value - chroma;

            double r = 0.0;
            double g = 0.0;
            double b = 0.0;
            switch ((int)sector)
            {
                case 0: r = chroma; g = second; break;
                case 1: r = second; g = chroma; break;
                case 2: g = chroma; b = second; break;
                case 3: g = second; b = chroma; break;
                case 4: r = second; b = chroma; break;
                default: r = chroma; b = second; break;
            }

            return Color.FromArgb(
                0xFF,
                (byte)Math.Round((r + match) * 255.0),
                (byte)Math.Round((g + match) * 255.0),
                (byte)Math.Round((b + match) * 255.0));
        }

        // Capture status arrives on a non-UI thread; marshal once (rare) and publish.
        private void OnAudioCaptureStatusChanged(AudioCaptureStatus state, string message)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _audioCaptureState = state;
                    _audioCaptureMessage = message;
                    UpdateAudioFrontBackAvailability();
                    AudioStatusChanged?.Invoke();
                }));
            }
            catch
            {
                // A status update must never throw into the capture thread (or at shutdown).
            }
        }

        /// <summary>
        /// Front/back (and the four corners) need more than two channels. This uses the
        /// capture's negotiated channel count, so it is known as soon as capture starts -
        /// even before any audio is heard - and the impossible indicators are hidden rather
        /// than shown dead.
        /// </summary>
        private void UpdateAudioFrontBackAvailability()
        {
            int channels = _audioCapture?.Channels ?? 0;
            if (channels < 2)
            {
                return;
            }

            bool available = channels > 2;
            bool changed = available != _audioFrontBackAvailable;
            _audioFrontBackAvailable = available;

            // Always (re)apply so a re-attach with a different channel count takes effect.
            Visibility visibility = available ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < _frontBackIndicators.Length; i++)
            {
                Rectangle indicator = _frontBackIndicators[i];
                indicator.Visibility = visibility;
                if (!available)
                {
                    indicator.Opacity = 0.0;
                }
            }

            if (changed)
            {
                AudioStatusChanged?.Invoke();
            }
        }

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

        /// <summary>
        /// Clamps a requested maximum width to the allowed range. This bounds the cap,
        /// not the slab's actual width (which is content-driven and can be smaller).
        /// </summary>
        private double ClampPanelMaxWidth(double maxWidth)
        {
            if (double.IsNaN(maxWidth) || double.IsInfinity(maxWidth))
            {
                return DefaultPanelMaxWidth;
            }

            return Math.Clamp(maxWidth, MinPanelWidth, MaxPanelWidth());
        }

        /// <summary>
        /// The panel content height for a given row count: rows x (LineHeight + row
        /// vertical padding) + Gap x (rows - 1) + caption-area padding. Derived from the
        /// numbers actually applied so it cannot drift.
        /// </summary>
        private double ComputeCaptionPanelHeightForRows(int rows)
        {
            // Same shared formula the preview uses, plus the caption-area chrome.
            double chrome = CaptionArea.Padding.Top + CaptionArea.Padding.Bottom;
            return CaptionRenderRules.PanelHeightForRows(rows, _captionLineHeight, _captionLineGap) + chrome;
        }

        /// <summary>
        /// The panel's fixed content height for the configured row window, capped to the
        /// work area so it can never exceed the screen. Because the rendered content is
        /// capped at exactly this many rows and unused rows are collapsed, the height is
        /// fixed and stops resizing as captions change; line advance stays exact, so the
        /// window trims whole rows only and never slices a partial row.
        /// </summary>
        private double ComputeCaptionPanelHeight()
        {
            double height = ComputeCaptionPanelHeightForRows(_maxDisplayedRows);

            double workHeight = SystemParameters.WorkArea.Height;
            if (workHeight > 0)
            {
                height = Math.Min(height, workHeight);
            }

            return height > 0 ? height : FallbackPanelHeight;
        }

        /// <summary>
        /// The configured rolling-window row count, clamped to CaptionRenderRules.MinRows..
        /// MaxRows and reduced if necessary so the window fits the work area. Delegates to
        /// the shared rule so the Options preview shows the same effective count.
        /// </summary>
        private int ClampCaptionRowCount(int requested)
            => CaptionRenderRules.ClampRowCount(requested, _captionLineHeight, _captionLineGap);

        // The widest the panel cap may be is the overlay canvas itself. The panel's X
        // offset is deliberately NOT subtracted: where the panel sits must never shrink
        // how wide the user is allowed to make it (otherwise merely moving the panel
        // would silently clamp the saved cap). ActualWidth tracks the real HWND size
        // (which SnapToGameWindow changes), so prefer it.
        private double MaxPanelWidth()
        {
            double canvasWidth = ActualWidth > 0
                ? ActualWidth
                : (Width > 0 ? Width : SystemParameters.WorkArea.Width);
            return Math.Max(MinPanelWidth, canvasWidth);
        }

        private void OverlayPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // The width grip must never start a panel drag. It marks its own mouse-down
            // handled, so this handler normally is not reached for it; the IsMouseOver
            // guard makes the separation explicit regardless.
            if (IsOverPanelControl())
            {
                return;
            }

            _dragging = true;
            _dragStartPoint = e.GetPosition(this);
            _dragStartLeft = OverlayPanel.Margin.Left;
            _dragStartTop = OverlayPanel.Margin.Top;

            OverlayPanel.CaptureMouse();
            e.Handled = true;
        }

        private void OverlayPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            Point current = e.GetPosition(this);
            double dx = current.X - _dragStartPoint.X;
            double dy = current.Y - _dragStartPoint.Y;

            // While centred, horizontal placement is owned by the Options centre setting,
            // so only the vertical position follows the drag. The manual X is left
            // untouched.
            bool centered = _settingsService.Settings.PanelCenteredHorizontally;

            double maxTop = Math.Max(0, ActualHeight - OverlayPanel.ActualHeight);
            double left = centered
                ? 0.0
                : ClampValue(_dragStartLeft + dx, 0, Math.Max(0, ActualWidth - OverlayPanel.ActualWidth));
            double top = ClampValue(_dragStartTop + dy, 0, maxTop);

            OverlayPanel.Margin = new Thickness(left, top, 0, 0);
        }

        private void OverlayPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            OverlayPanel.ReleaseMouseCapture();

            // Preserve the manual X while centred so turning centring off restores it exactly.
            if (!_settingsService.Settings.PanelCenteredHorizontally)
            {
                _settingsService.Settings.PanelX = (int)Math.Round(OverlayPanel.Margin.Left);
            }

            _settingsService.Settings.PanelY = (int)Math.Round(OverlayPanel.Margin.Top);
            _settingsService.Save();

            // Re-apply placement from the persisted values so the drag ends on the exact
            // settings-driven placement (and centred mode stays centred).
            ApplyPanelPlacement();

            e.Handled = true;
        }

        // ------------------------------------------------------------------
        // Hover-revealed overlay control (the width grip)
        // ------------------------------------------------------------------

        private bool IsOverPanelControl()
        {
            return GripRight.IsMouseOver;
        }

        private void OverlayPanel_MouseEnter(object sender, MouseEventArgs e)
        {
            RevealOverlayControls();
        }

        private void OverlayPanel_MouseLeave(object sender, MouseEventArgs e)
        {
            // Keep the controls visible while a resize drag is in progress.
            if (_resizeMode != ResizeEdge.None)
            {
                return;
            }

            HideOverlayControls();
        }

        private void RevealOverlayControls()
        {
            if (_overlayControlsRevealed)
            {
                return;
            }

            _overlayControlsRevealed = true;
            ResizeGrips.IsHitTestVisible = true;

            ResizeGrips.BeginAnimation(OpacityProperty, CreateControlsFade(ResizeGrips.Opacity, 1.0, reveal: true));
        }

        private void HideOverlayControls()
        {
            if (!_overlayControlsRevealed)
            {
                return;
            }

            _overlayControlsRevealed = false;

            var gripFade = CreateControlsFade(ResizeGrips.Opacity, 0.0, reveal: false);
            gripFade.Completed += (_, _) =>
            {
                // Only drop hit-testing if we were not re-revealed meanwhile.
                if (!_overlayControlsRevealed)
                {
                    ResizeGrips.IsHitTestVisible = false;
                }
            };
            ResizeGrips.BeginAnimation(OpacityProperty, gripFade);
        }

        private static DoubleAnimation CreateControlsFade(double from, double to, bool reveal)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(reveal ? 120 : 160))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = reveal ? EasingMode.EaseOut : EasingMode.EaseIn
                }
            };
        }

        private void GripRight_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginResize(sender, e, ResizeEdge.Right);
        }

        private void BeginResize(object sender, MouseButtonEventArgs e, ResizeEdge mode)
        {
            if (sender is not UIElement element)
            {
                return;
            }

            _resizeMode = mode;
            _resizeStartPoint = e.GetPosition(this);
            _resizeStartMaxWidth = ClampPanelMaxWidth(OverlayPanel.MaxWidth);

            element.CaptureMouse();

            // Claim the press so the panel never starts a window drag for it.
            e.Handled = true;
        }

        private void Grip_MouseMove(object sender, MouseEventArgs e)
        {
            if (_resizeMode == ResizeEdge.None)
            {
                return;
            }

            // GetPosition is in DIPs and the panel's top-left does not move during a
            // resize, so this delta is a stable DIP delta (no device/DIP mixing).
            Point current = e.GetPosition(this);
            double dx = current.X - _resizeStartPoint.X;

            // The right-edge grip drags the MAX width cap: the slab still hugs its
            // text and only grows visibly if the text needs the extra room.
            OverlayPanel.MaxWidth = ClampPanelMaxWidth(_resizeStartMaxWidth + dx);

            // The wrap width changed: re-wrap so rows never exceed the new cap.
            RecomputeAndRenderRows();
        }

        private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_resizeMode == ResizeEdge.None)
            {
                return;
            }

            _resizeMode = ResizeEdge.None;

            if (sender is UIElement element)
            {
                element.ReleaseMouseCapture();
            }

            // Persist only when the drag ends. PanelMaxWidth is the cap, not the
            // slab's current (content-driven) width.
            _settingsService.Settings.PanelMaxWidth = (int)Math.Round(ClampPanelMaxWidth(OverlayPanel.MaxWidth));
            _settingsService.Save();

            // Settle the rows at the final cap.
            RecomputeAndRenderRows();

            // If the drag ended outside the panel, no MouseLeave will arrive again,
            // so retire the controls here.
            if (!OverlayPanel.IsMouseOver)
            {
                HideOverlayControls();
            }

            e.Handled = true;
        }

        private static double ClampValue(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
