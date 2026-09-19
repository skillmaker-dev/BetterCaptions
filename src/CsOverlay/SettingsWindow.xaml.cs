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
using CsOverlay.Models;
using CsOverlay.Services;

namespace CsOverlay
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

        // True from field initialisation until the constructor has finished loading and
        // seeding every control. XAML sets slider Minimum/Maximum, which coerces Value
        // and raises ValueChanged DURING InitializeComponent - before the constructor
        // body runs and before later-declared controls exist. Handlers must no-op until
        // this flips to false.
        private bool _initializing = true;

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

            RenderPreview();
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
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "CsOverlay/1.0");
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
