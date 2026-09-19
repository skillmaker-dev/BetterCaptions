using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CsOverlay
{
    /// <summary>
    /// The SHARED caption rendering rules. BOTH the overlay panel (<see cref="MainWindow"/>)
    /// and the Options preview (<see cref="SettingsWindow"/>) use these, so the preview can
    /// never drift from the real overlay: same wrapping, same row chrome, same line height,
    /// same font size / row bounds and the same rolling-row window cap.
    /// </summary>
    internal static class CaptionRenderRules
    {
        // Font size and rolling-window row bounds (clamped defensively).
        public const double MinFontSize = 12.0;
        public const double MaxFontSize = 40.0;
        public const int MinRows = 1;
        public const int MaxRows = 10;

        // Line box height as a multiple of the font size, applied to every row. Kept TIGHT
        // on purpose: the font-size control must read as a glyph-size control, not as a
        // line-spacing control. Segoe UI's visible Latin extent is about 1.0 em (roughly
        // 0.75 em ascent + 0.25 em descent), so a 1.2 em line box leaves ~0.2 em of
        // headroom and ascenders, descenders and accented capitals cannot be clipped at any
        // size in the 12-40 range.
        public const double TightLineHeightRatio = 1.2;

        // The dimmer history tier: rows above the newest use the text colour at this alpha.
        public const double OlderTextAlphaScale = 0.75;

        // The fitted bar's own padding. ApplyRow sets exactly these, and the wrap/height
        // maths reads them, so the bar chrome has a single source of truth.
        public const double RowPaddingLeft = 10.0;
        public const double RowPaddingRight = 10.0;
        public const double RowPaddingTop = 2.0;
        public const double RowPaddingBottom = 2.0;

        public static double RowPaddingHorizontal => RowPaddingLeft + RowPaddingRight;

        public static double RowPaddingVertical => RowPaddingTop + RowPaddingBottom;

        /// <summary>The one font used for caption rows.</summary>
        public static readonly FontFamily FontFamily = new FontFamily("Segoe UI");

        /// <summary>
        /// Applies the shared row appearance: a fitted bar with its own background and
        /// padding, a single line (NoWrap, no trimming), exact line boxes (BlockLineHeight),
        /// and the gap as a bottom margin on every row except the bottom.
        ///
        /// <paramref name="textCentered"/> is the CaptionTextCentered setting. Because every
        /// bar is fitted to its own text, TextAlignment has nothing to align WITHIN a bar;
        /// the setting is therefore applied to the bar's alignment within the panel:
        /// centred (the default look) or flush-left (a short row sits on the panel's left
        /// edge, so the pyramid is gone). TextAlignment is kept in step for correctness.
        /// </summary>
        public static void ApplyRow(
            TextBlock row,
            bool isBottomRow,
            double fontSize,
            double lineHeight,
            double gap,
            Brush foreground,
            Brush background,
            bool textCentered)
        {
            row.FontFamily = FontFamily;
            row.FontSize = fontSize;
            row.FontWeight = FontWeights.Normal;
            row.LineHeight = lineHeight;
            row.Padding = new Thickness(RowPaddingLeft, RowPaddingTop, RowPaddingRight, RowPaddingBottom);
            row.HorizontalAlignment = textCentered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            row.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            TextOptions.SetTextFormattingMode(row, TextFormattingMode.Display);
            row.SnapsToDevicePixels = true;
            row.TextWrapping = TextWrapping.NoWrap;
            row.TextTrimming = TextTrimming.None;
            row.TextAlignment = textCentered ? TextAlignment.Center : TextAlignment.Left;
            row.Foreground = foreground;
            row.Background = background;
            row.Margin = new Thickness(0, 0, 0, isBottomRow ? 0 : gap);
        }

        /// <summary>
        /// Content height for a window of <paramref name="rows"/> rows: rows x (lineHeight +
        /// row vertical padding) + gap x (rows - 1). Used by both the overlay panel window
        /// and the preview.
        /// </summary>
        public static double PanelHeightForRows(int rows, double lineHeight, double gap)
        {
            double rowHeight = lineHeight + RowPaddingVertical;
            return (rows * rowHeight) + (Math.Max(0, rows - 1) * gap);
        }

        /// <summary>
        /// Clamps the requested row count to MinRows..MaxRows, then reduces it (down to
        /// MinRows) if the resulting window would be taller than the work area. Reducing the
        /// ROW COUNT keeps every displayed row whole rather than clipping the height, so no
        /// glyph is ever sliced - and the preview therefore shows the same effective count.
        /// </summary>
        public static int ClampRowCount(int requested, double lineHeight, double gap)
        {
            int rows = Math.Clamp(requested, MinRows, MaxRows);

            double workHeight = SystemParameters.WorkArea.Height;
            if (workHeight > 0)
            {
                while (rows > MinRows && PanelHeightForRows(rows, lineHeight, gap) > workHeight)
                {
                    rows--;
                }
            }

            return rows;
        }
    }

    /// <summary>
    /// The ONE word-wrap implementation, shared by the overlay and the Options preview so
    /// the row breaks can never drift. Greedy, oldest-first rows; never truncates; a single
    /// word wider than a row is hard-split across rows so the whole word is still shown.
    /// </summary>
    internal static class CaptionTextWrapper
    {
        public static List<string> Wrap(
            string? text,
            double maxRowTextWidth,
            Typeface typeface,
            double fontSize,
            double pixelsPerDip)
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
                    if (Measure(word, typeface, fontSize, pixelsPerDip) <= maxRowTextWidth)
                    {
                        line = word;
                    }
                    else
                    {
                        AppendHardSplit(word, maxRowTextWidth, rows, typeface, fontSize, pixelsPerDip, out line);
                    }

                    continue;
                }

                string candidate = line + " " + word;
                if (Measure(candidate, typeface, fontSize, pixelsPerDip) <= maxRowTextWidth)
                {
                    line = candidate;
                    continue;
                }

                rows.Add(line);

                if (Measure(word, typeface, fontSize, pixelsPerDip) <= maxRowTextWidth)
                {
                    line = word;
                }
                else
                {
                    AppendHardSplit(word, maxRowTextWidth, rows, typeface, fontSize, pixelsPerDip, out line);
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
        /// <paramref name="rows"/> and the trailing fragment is returned as the pending line.
        /// At least one character is always consumed, so a very narrow row cannot loop
        /// forever.
        /// </summary>
        private static void AppendHardSplit(
            string word,
            double maxRowTextWidth,
            List<string> rows,
            Typeface typeface,
            double fontSize,
            double pixelsPerDip,
            out string remainder)
        {
            int start = 0;

            while (start < word.Length)
            {
                int take = 0;
                for (int len = 1; start + len <= word.Length; len++)
                {
                    if (Measure(word.Substring(start, len), typeface, fontSize, pixelsPerDip) <= maxRowTextWidth)
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

        /// <summary>Measured width of <paramref name="text"/> at the given DPI.</summary>
        public static double Measure(string text, Typeface typeface, double fontSize, double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0.0;
            }

            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.Black,
                pixelsPerDip);

            return formatted.Width;
        }
    }
}
