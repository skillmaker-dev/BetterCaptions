using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        // ellipsis/truncation anywhere. The count is clamped to this sane range so a
        // hand-edited settings file cannot request zero rows or an absurd height, and the
        // resulting panel height is additionally capped to the work area.
        private const int MinCaptionRows = 1;
        private const int MaxCaptionRows = 10;

        // Fallback only. The real content height is the configured row window derived in
        // ComputeCaptionPanelHeight() from the applied metrics, so it cannot drift.
        private const double FallbackPanelHeight = 54.0;

        // Bounds and the dimming scale for the live appearance settings. Every row shares
        // one font size; only the colour tier differs (see ApplyRowAppearance).
        private const double MinCaptionFontSize = 12.0;
        private const double MaxCaptionFontSize = 40.0;
        private const double OlderTextAlphaScale = 0.75;

        // Line box height as a multiple of the font size, applied to both bars. Kept
        // TIGHT on purpose: the font-size control must read as a glyph-size control, not
        // as a line-spacing control. Segoe UI's visible Latin extent is about 1.0 em
        // (roughly 0.75 em ascent + 0.25 em descent), so a 1.2 em line box leaves ~0.2 em
        // of headroom and ascenders, descenders and accented capitals cannot be clipped at
        // any size in the 12-40 range. (The old 1.3 ratio was the font's full leading.)
        private const double TightLineHeightRatio = 1.2;

        private static readonly Color DefaultCaptionBackgroundColor = Color.FromArgb(0x8C, 0, 0, 0);

        private readonly SettingsService _settingsService;

        private HwndSource? _hwndSource;
        private IntPtr _handle = IntPtr.Zero;

        private bool _clickThrough;
        private bool _overlayVisible;
        private bool _gameRunning;
        private string _hotkeyDisplay = string.Empty;

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

        // The configured rolling-window row count, clamped to MinCaptionRows..MaxCaptionRows
        // and reduced if necessary so the window fits the work area. Recomputed by
        // ApplyAppearanceSettings; drives the visible window, the row pool and the height.
        private int _maxDisplayedRows = 3;

        // Caption row rendering. A fixed pool of up to _maxDisplayedRows TextBlocks; each
        // holds exactly one already-wrapped row (NoWrap). The stack is bottom-aligned and
        // rows fill from the bottom, so the newest row is the last pool slot.
        private readonly List<TextBlock> _captionLines = new List<TextBlock>();
        private readonly Style _rowStyle;

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

        public MainWindow(SettingsService settingsService)
        {
            _settingsService = settingsService;
            InitializeComponent();

            // No window-level Opacity is applied; only the panel brush is slightly
            // translucent, so the text stays fully opaque.
            _rowStyle = (Style)FindResource("CaptionRow");

            // Apply the persisted appearance at startup. Size, placement and the centre
            // checkbox are applied in OnSourceInitialized once the canvas size is known.
            ApplyAppearanceSettings();

            SetGameStatus(false);
        }

        public bool IsOverlayVisible => _overlayVisible;

        /// <summary>
        /// Raised when the global hotkey is pressed. App decides what the hotkey
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
            _hwndSource?.RemoveHook(WndProc);
            base.OnClosed(e);
        }

        /// <summary>
        /// Toggles WS_EX_TRANSPARENT so mouse input either passes through to the game
        /// or is handled by this window.
        /// </summary>
        public void SetClickThrough(bool enabled)
        {
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

        public void SetGameStatus(bool running)
        {
            _gameRunning = running;
            UpdateStatusText();
        }

        /// <summary>
        /// Supplies the readable active hotkey (e.g. "Ctrl+Alt+O") for display in the
        /// overlay. Pass an empty string when no hotkey is bound.
        /// </summary>
        public void SetHotkeyDisplay(string hotkeyText)
        {
            _hotkeyDisplay = hotkeyText ?? string.Empty;
            UpdateStatusText();
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
            double textArea = Math.Max(1.0, CurrentWrapWidth() - GetStylePaddingHorizontal(_rowStyle) - 1.0);

            List<string> allRows = WrapRows(_captionText, textArea);

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

        /// <summary>
        /// Greedy word-wrap into rows that fit the available text width. Never truncates:
        /// a single word wider than the row is hard-split across rows so the whole word is
        /// still shown. Empty/whitespace text yields no rows.
        /// </summary>
        private List<string> WrapRows(string text, double maxRowTextWidth)
        {
            var rows = new List<string>();

            if (string.IsNullOrWhiteSpace(text))
            {
                return rows;
            }

            string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string line = string.Empty;

            foreach (string word in words)
            {
                if (line.Length == 0)
                {
                    if (MeasureTextWidth(word) <= maxRowTextWidth)
                    {
                        line = word;
                    }
                    else
                    {
                        AppendHardSplit(word, maxRowTextWidth, rows, out line);
                    }

                    continue;
                }

                string candidate = line + " " + word;
                if (MeasureTextWidth(candidate) <= maxRowTextWidth)
                {
                    line = candidate;
                    continue;
                }

                rows.Add(line);

                if (MeasureTextWidth(word) <= maxRowTextWidth)
                {
                    line = word;
                }
                else
                {
                    AppendHardSplit(word, maxRowTextWidth, rows, out line);
                }
            }

            if (line.Length > 0)
            {
                rows.Add(line);
            }

            return rows;
        }

        /// <summary>
        /// Splits a single word that is wider than one row. Full chunks are added to
        /// <paramref name="rows"/> and the trailing fragment is returned as the pending
        /// line. At least one character is always consumed, so a very narrow panel cannot
        /// loop forever.
        /// </summary>
        private void AppendHardSplit(string word, double maxRowTextWidth, List<string> rows, out string remainder)
        {
            int start = 0;

            while (start < word.Length)
            {
                int take = 0;
                for (int len = 1; start + len <= word.Length; len++)
                {
                    if (MeasureTextWidth(word.Substring(start, len)) <= maxRowTextWidth)
                    {
                        take = len;
                    }
                    else
                    {
                        break;
                    }
                }

                if (take == 0)
                {
                    take = 1;
                }

                string piece = word.Substring(start, take);
                start += take;

                if (start < word.Length)
                {
                    rows.Add(piece);
                }
                else
                {
                    remainder = piece;
                    return;
                }
            }

            remainder = string.Empty;
        }

        private double MeasureTextWidth(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0.0;
            }

            // Real DPI from the visual so the measurement matches what TextBlock renders.
            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _rowTypeface,
                _captionFontSize,
                Brushes.Black,
                pixelsPerDip);

            return formatted.Width;
        }

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

            // When captions are on screen both status lines get out of the way
            // entirely, leaving the panel as pure caption content.
            Visibility statusVisibility = _hasCaptions ? Visibility.Collapsed : Visibility.Visible;
            CaptionStatusText.Visibility = statusVisibility;

            if (StatusText is not null)
            {
                StatusText.Visibility = statusVisibility;
            }
        }

        private void UpdateStatusText()
        {
            if (StatusText is null)
            {
                return;
            }

            string status = _gameRunning
                ? "game: Counter-Strike: Source detected"
                : "waiting for game...";

            string suffix = string.IsNullOrEmpty(_hotkeyDisplay)
                ? " (no hotkey)"
                : $" ({_hotkeyDisplay})";

            StatusText.Text = status + suffix;
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
            double captionFontSize = Math.Clamp(settings.CaptionFontSize, MinCaptionFontSize, MaxCaptionFontSize);
            _captionFontSize = captionFontSize;
            _captionLineHeight = captionFontSize * TightLineHeightRatio;
            _rowTypeface = new Typeface(
                GetStyleFontFamily(_rowStyle), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
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
            byte olderAlpha = (byte)Math.Round(textColor.A * OlderTextAlphaScale);

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
        }

        /// <summary>
        /// Applies the uniform row appearance. Every row uses the SAME font size, line
        /// height and weight; only the colour tier differs - the bottom (newest) row is
        /// brightest and the rows above it use the dimmer history brush. There is no
        /// trimming: each row is already a single wrapped line (NoWrap). The gap is a
        /// bottom margin on every row except the bottom one.
        /// </summary>
        private void ApplyRowAppearance(TextBlock row, bool isBottomRow)
        {
            row.FontSize = _captionFontSize;
            row.LineHeight = _captionLineHeight;
            row.FontWeight = FontWeights.Normal;
            row.Foreground = isBottomRow ? _newestTextBrush : _olderTextBrush;
            row.Background = _captionBackgroundBrush;
            row.TextAlignment = _captionTextCentered ? TextAlignment.Center : TextAlignment.Left;
            row.TextWrapping = TextWrapping.NoWrap;
            row.TextTrimming = TextTrimming.None;
            row.Margin = new Thickness(0, 0, 0, isBottomRow ? 0 : _captionLineGap);
        }

        /// <summary>
        /// Tints the no-caption hint (<see cref="CaptionStatusText"/>) and the footer
        /// (<see cref="StatusText"/>) with the SAME settings as the caption bars:
        /// the configured text colour for the foreground and the configured caption
        /// background for the backer - or nothing at all when backgrounds are disabled
        /// (which still stays hit-testable so the panel remains draggable across it).
        ///
        /// Alpha choice: the hint is the only content in the empty state, so it uses the
        /// text colour at its FULL configured alpha (the newest-line tier) to stay clearly
        /// readable over a game or a desktop. The footer is secondary chrome and uses the
        /// quieter history tier - the text colour at <see cref="OlderTextAlphaScale"/>
        /// (~75%) - matching the older caption line. No new alpha values are introduced.
        /// </summary>
        private void ApplyStatusAppearance()
        {
            // The caption text-alignment setting is deliberately NOT applied here: the hint
            // and footer are single-line and already centred as elements, so TextAlignment
            // would have no visible effect (it would only matter if the hint wrapped). They
            // keep the default left text alignment.
            if (CaptionStatusText is not null)
            {
                CaptionStatusText.Foreground = _newestTextBrush;
                CaptionStatusText.Background = _captionBackgroundBrush;
            }

            if (StatusText is not null)
            {
                StatusText.Foreground = _olderTextBrush;
                StatusText.Background = _captionBackgroundBrush;
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
            double lineHeight = _captionLineHeight > 0 ? _captionLineHeight : GetStyleLineHeight(_rowStyle);
            double rowHeight = lineHeight + GetStylePaddingVertical(_rowStyle);
            double chrome = CaptionArea.Padding.Top + CaptionArea.Padding.Bottom;

            // Gaps sit BETWEEN rows, so there is one fewer than the row count.
            double gaps = Math.Max(0, rows - 1) * _captionLineGap;
            return (rows * rowHeight) + gaps + chrome;
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
        /// Clamps the requested row count to MinCaptionRows..MaxCaptionRows, then reduces
        /// it further (down to MinCaptionRows) if the resulting window would still be taller
        /// than the work area. Reducing the ROW COUNT rather than merely clipping the height
        /// keeps every displayed row whole, so no glyph is ever sliced.
        /// </summary>
        private int ClampCaptionRowCount(int requested)
        {
            int rows = Math.Clamp(requested, MinCaptionRows, MaxCaptionRows);

            double workHeight = SystemParameters.WorkArea.Height;
            if (workHeight > 0)
            {
                while (rows > MinCaptionRows && ComputeCaptionPanelHeightForRows(rows) > workHeight)
                {
                    rows--;
                }
            }

            return rows;
        }

        private static double GetStyleLineHeight(Style style)
        {
            return FindStyleSetter(style, TextBlock.LineHeightProperty) is double lineHeight
                   && !double.IsNaN(lineHeight) && lineHeight > 0
                ? lineHeight
                : 0.0;
        }

        private static double GetStylePaddingVertical(Style style)
        {
            return FindStyleSetter(style, TextBlock.PaddingProperty) is Thickness padding
                ? padding.Top + padding.Bottom
                : 0.0;
        }

        private static double GetStylePaddingHorizontal(Style style)
        {
            return FindStyleSetter(style, TextBlock.PaddingProperty) is Thickness padding
                ? padding.Left + padding.Right
                : 0.0;
        }

        private static FontFamily GetStyleFontFamily(Style style)
        {
            return FindStyleSetter(style, TextBlock.FontFamilyProperty) as FontFamily
                ?? new FontFamily("Segoe UI");
        }

        // Walks the style and its BasedOn chain so inherited setters (e.g. the base
        // margin) are found too.
        private static object? FindStyleSetter(Style? style, DependencyProperty property)
        {
            while (style is not null)
            {
                foreach (SetterBase setterBase in style.Setters)
                {
                    if (setterBase is Setter setter && setter.Property == property)
                    {
                        return setter.Value;
                    }
                }

                style = style.BasedOn;
            }

            return null;
        }

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
