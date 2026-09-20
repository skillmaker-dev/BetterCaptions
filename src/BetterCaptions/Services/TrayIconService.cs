using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BetterCaptions.Interop;

namespace BetterCaptions.Services
{
    /// <summary>
    /// System tray presence. The icon is generated at runtime so the app has no
    /// dependency on an external .ico file.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Icon _icon;
        private readonly ContextMenuStrip _menu;
        private bool _disposed;

        public event Action? ShowRequested;
        public event Action? HideRequested;
        public event Action? ToggleRequested;
        public event Action? SettingsRequested;
        public event Action? ExitRequested;

        public TrayIconService()
        {
            _icon = CreateTrayIcon();

            _menu = new ContextMenuStrip();
            _menu.Items.Add("Show overlay", null, (_, __) => ShowRequested?.Invoke());
            _menu.Items.Add("Hide overlay", null, (_, __) => HideRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Show options", null, (_, __) => SettingsRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Exit", null, (_, __) => ExitRequested?.Invoke());

            _notifyIcon = new NotifyIcon
            {
                Icon = _icon,
                Text = "BetterCaptions",
                Visible = true,
                ContextMenuStrip = _menu
            };

            _notifyIcon.DoubleClick += (_, __) => ToggleRequested?.Invoke();
        }

        /// <summary>
        /// Shows a non-blocking balloon notification. Safe to call at startup; the
        /// icon is made visible first because balloons require a visible icon.
        /// </summary>
        public void ShowNotification(string title, string message)
        {
            if (_disposed)
            {
                return;
            }

            _notifyIcon.Visible = true;
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(5000);
        }

        /// <summary>
        /// Updates the tray tooltip (used to surface the active hotkey).
        /// </summary>
        public void SetTooltip(string text)
        {
            if (_disposed)
            {
                return;
            }

            string safe = string.IsNullOrEmpty(text) ? "BetterCaptions" : text;

            // The shell truncates tooltips beyond 63 characters.
            if (safe.Length > 63)
            {
                safe = safe.Substring(0, 60) + "...";
            }

            _notifyIcon.Text = safe;
        }

        private static Icon CreateTrayIcon()
        {
            using var bitmap = new Bitmap(16, 16);

            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(System.Drawing.Color.Transparent);

                using var background = new SolidBrush(System.Drawing.Color.FromArgb(230, 20, 22, 28));
                graphics.FillEllipse(background, 0, 0, 15, 15);

                using var border = new Pen(System.Drawing.Color.FromArgb(255, 60, 170, 255), 1.5f);
                graphics.DrawEllipse(border, 1.5f, 1.5f, 12f, 12f);

                using var cross = new Pen(System.Drawing.Color.White, 1.5f);
                graphics.DrawLine(cross, 4f, 8f, 11f, 8f);
                graphics.DrawLine(cross, 8f, 4f, 8f, 11f);
            }

            IntPtr handle = bitmap.GetHicon();
            try
            {
                using Icon temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _icon.Dispose();
        }
    }
}
