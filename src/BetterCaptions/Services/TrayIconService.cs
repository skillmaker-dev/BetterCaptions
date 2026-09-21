using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BetterCaptions.Services
{
    /// <summary>
    /// System tray presence. The tray shows the app's real icon (Assets/app.ico, embedded
    /// in this assembly): the exact small-size frame is loaded so the tray is crisp at the
    /// current DPI, rather than a generated placeholder scaled down from a larger image.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        // Logical name of the EmbeddedResource produced by <EmbeddedResource Include="Assets\app.ico" />
        // (RootNamespace + folder path).
        private const string AppIconResourceName = "BetterCaptions.Assets.app.ico";

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
            _icon = LoadAppIcon();

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

        /// <summary>
        /// Loads the app icon from the embedded multi-resolution <c>Assets/app.ico</c>,
        /// picking the frame that matches the tray's small-icon size (so Windows never has
        /// to scale a larger bitmap down). Falls back to the running exe's own icon and
        /// finally to a private copy of the default application icon, so the tray always
        /// has something valid to show.
        /// </summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                using Stream? stream = typeof(TrayIconService).Assembly
                    .GetManifestResourceStream(AppIconResourceName);

                if (stream is not null)
                {
                    return new Icon(stream, SystemInformation.SmallIconSize);
                }
            }
            catch
            {
                // Resource missing or unreadable: fall through to the exe icon.
            }

            try
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    Icon? extracted = Icon.ExtractAssociatedIcon(exePath);
                    if (extracted is not null)
                    {
                        return extracted;
                    }
                }
            }
            catch
            {
                // Exe icon unavailable: fall through to the system default.
            }

            // Clone so disposing the tray icon never disposes the shared system icon.
            return (Icon)SystemIcons.Application.Clone();
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
