using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CsOverlay.Interop;
using CsOverlay.Services;

namespace CsOverlay
{
    public partial class MainWindow : Window
    {
        private const double MinPanelWidth = 120.0;
        private const double DefaultPanelMaxWidth = 640.0;

        // Hard cap on how many caption lines are rendered. Two keeps the slab short
        // and, importantly, ties the auto-fit width to exactly the lines on screen:
        // a longer line that is not rendered must never widen the slab.
        private const int MaxRenderedCaptionLines = 2;

        // Fallback only, and the panel's height FLOOR (MinHeight). The real value is
        // derived from the caption style metrics in ComputeCaptionPanelHeight() so it
        // cannot silently drift: CaptionNewest (LineHeight 26 + per-bar padding 2+2 +
        // top margin 0 = 30) + CaptionOlder (LineHeight 20 + per-bar padding 2+2 +
        // top margin 0 = 24) + caption-area vertical padding (0) = 54.
        // Two single rows is the floor; the panel grows taller when a line wraps.
        private const double FallbackPanelHeight = 54.0;

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

        // Caption rendering. At most MaxRenderedCaptionLines TextBlocks are created
        // and reused; the stack is bottom-aligned so the newest line sits at the bottom.
        private readonly List<TextBlock> _captionLines = new List<TextBlock>();
        private readonly Style _newestStyle;
        private readonly Style _olderStyle;

        private bool _dragging;
        private Point _dragStartPoint;
        private double _dragStartLeft;
        private double _dragStartTop;

        // Panel resize state. The height is fixed by the two-line content, so both
        // remaining grips (right edge and bottom-right corner) adjust the MaxWidth cap
        // only. The panel's Margin (its top-left) is never touched while resizing. The
        // panel's own Width stays unset so it keeps hugging its content.
        private enum ResizeEdge
        {
            None,
            Right,
            Corner
        }

        private ResizeEdge _resizeMode = ResizeEdge.None;
        private Point _resizeStartPoint;
        private double _resizeStartMaxWidth;

        private bool _overlayControlsRevealed;
        private bool _suppressCenterToggleEvents;

        public MainWindow(SettingsService settingsService)
        {
            _settingsService = settingsService;
            InitializeComponent();

            // No window-level Opacity is applied; only the panel brush is slightly
            // translucent, so the text stays fully opaque.
            _newestStyle = (Style)FindResource("CaptionNewest");
            _olderStyle = (Style)FindResource("CaptionOlder");

            // Seed the hover checkbox from settings without firing the change handler
            // (which would persist and re-apply placement during construction).
            _suppressCenterToggleEvents = true;
            CenterToggle.IsChecked = _settingsService.Settings.PanelCenteredHorizontally;
            _suppressCenterToggleEvents = false;

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
        /// </summary>
        public void SetCaptionLines(string[] lines)
        {
            if (CaptionLinesPanel is null)
            {
                return;
            }

            // The reader publishes oldest-first, so the last element is the newest.
            // Only the newest MaxRenderedCaptionLines are rendered. Capping here (not
            // in the reader) is what keeps the auto-fit width tied to the visible
            // lines: an older, wider line that is not rendered cannot widen the slab.
            string[] safe = lines ?? Array.Empty<string>();
            int supplied = safe.Length;
            int rendered = Math.Min(supplied, MaxRenderedCaptionLines);
            int firstRendered = supplied - rendered;

            EnsureCaptionBlocks(rendered);

            int poolCount = _captionLines.Count;
            int firstUsedSlot = poolCount - rendered;

            for (int slot = 0; slot < poolCount; slot++)
            {
                TextBlock block = _captionLines[slot];

                if (slot < firstUsedSlot)
                {
                    block.Text = string.Empty;
                    block.Visibility = Visibility.Collapsed;
                    continue;
                }

                // The last slot is the newest and gets the emphasised style; the other
                // (at most one) slot uses the quieter history style.
                bool isNewest = slot == poolCount - 1;
                block.Style = isNewest ? _newestStyle : _olderStyle;
                block.Text = safe[firstRendered + (slot - firstUsedSlot)];
                block.Visibility = Visibility.Visible;
            }

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
            _hasCaptions = rendered > 0;

            UpdateCaptionStatusVisibility();

            if (newLineArrived)
            {
                AnimateNewestCaption(_captionLines[poolCount - 1]);
            }
        }

        /// <summary>
        /// Grows the reusable TextBlock pool to fit the (already capped) rendered line
        /// count. It never exceeds MaxRenderedCaptionLines.
        /// </summary>
        private void EnsureCaptionBlocks(int count)
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
        /// centered in the overlay window when the center toggle is on. The Y position
        /// is always the user's.
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
        }

        /// <summary>
        /// Applies the saved width cap (clamped) and the two-single-row height floor.
        /// Width is left unset so the slab auto-sizes to its caption text between
        /// MinWidth and MaxWidth, and Height is left unset so the slab GROWS when a
        /// caption line wraps instead of clipping it.
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
        /// Minimum height for two single-row caption lines: each line's slot (style
        /// LineHeight + that line's own vertical bar padding + its top margin) plus the
        /// caption-area vertical padding. Derived from the styles so it cannot drift
        /// from them. Applied as the panel's MinHeight (not an exact Height), so a
        /// wrapped line makes the panel taller instead of being trimmed at the top.
        /// </summary>
        private double ComputeCaptionPanelHeight()
        {
            double newestSlot = GetStyleLineHeight(_newestStyle)
                                + GetStylePaddingVertical(_newestStyle)
                                + GetStyleMarginTop(_newestStyle);
            double olderSlot = GetStyleLineHeight(_olderStyle)
                               + GetStylePaddingVertical(_olderStyle)
                               + GetStyleMarginTop(_olderStyle);
            double padding = CaptionArea.Padding.Top + CaptionArea.Padding.Bottom;

            double height = newestSlot + olderSlot + padding;
            return height > 0 ? height : FallbackPanelHeight;
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

        private static double GetStyleMarginTop(Style style)
        {
            return FindStyleSetter(style, FrameworkElement.MarginProperty) is Thickness margin
                ? margin.Top
                : 0.0;
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

        // The panel's top-left is fixed, so the usable growth is what remains of the
        // overlay canvas to the right of that offset. This keeps the panel from being
        // dragged past the edge of the canvas. ActualWidth tracks the real HWND size
        // (which SnapToGameWindow changes), so prefer it.
        private double MaxPanelWidth()
        {
            double canvasWidth = ActualWidth > 0
                ? ActualWidth
                : (Width > 0 ? Width : SystemParameters.WorkArea.Width);
            return Math.Max(MinPanelWidth, canvasWidth - OverlayPanel.Margin.Left);
        }

        private void OverlayPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // The resize grips and the center checkbox must never start a panel drag.
            // They mark their own mouse-down handled, so this handler normally is not
            // reached for them; the IsMouseOver guard makes the separation explicit
            // regardless.
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

            // While centered, horizontal placement is owned by the checkbox, so only
            // the vertical position follows the drag. The manual X is left untouched.
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

            // Preserve the manual X while centered so unchecking restores it exactly.
            if (!_settingsService.Settings.PanelCenteredHorizontally)
            {
                _settingsService.Settings.PanelX = (int)Math.Round(OverlayPanel.Margin.Left);
            }

            _settingsService.Settings.PanelY = (int)Math.Round(OverlayPanel.Margin.Top);
            _settingsService.Save();

            e.Handled = true;
        }

        // ------------------------------------------------------------------
        // Hover-revealed overlay controls (resize grips + center checkbox)
        // ------------------------------------------------------------------

        private bool IsOverPanelControl()
        {
            return GripCorner.IsMouseOver || GripRight.IsMouseOver || CenterToggle.IsMouseOver;
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
            OverlayControls.IsHitTestVisible = true;

            ResizeGrips.BeginAnimation(OpacityProperty, CreateControlsFade(ResizeGrips.Opacity, 1.0, reveal: true));
            OverlayControls.BeginAnimation(OpacityProperty, CreateControlsFade(OverlayControls.Opacity, 1.0, reveal: true));
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

            var controlsFade = CreateControlsFade(OverlayControls.Opacity, 0.0, reveal: false);
            controlsFade.Completed += (_, _) =>
            {
                if (!_overlayControlsRevealed)
                {
                    OverlayControls.IsHitTestVisible = false;
                }
            };
            OverlayControls.BeginAnimation(OpacityProperty, controlsFade);
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

        private void CenterToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressCenterToggleEvents)
            {
                return;
            }

            _settingsService.Settings.PanelCenteredHorizontally = CenterToggle.IsChecked == true;
            _settingsService.Save();
            ApplyPanelPlacement();
        }

        private void GripRight_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginResize(sender, e, ResizeEdge.Right);
        }

        private void GripCorner_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BeginResize(sender, e, ResizeEdge.Corner);
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

            // Both remaining grips drag the MAX width cap: the slab still hugs its
            // text and only grows visibly if the text needs the extra room.
            OverlayPanel.MaxWidth = ClampPanelMaxWidth(_resizeStartMaxWidth + dx);
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
