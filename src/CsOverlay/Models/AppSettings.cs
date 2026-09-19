namespace CsOverlay.Models
{
    /// <summary>
    /// Persisted application settings. Serialized as JSON to
    /// %LOCALAPPDATA%\CsOverlay\settings.json by <see cref="Services.SettingsService"/>.
    /// </summary>
    public sealed class AppSettings
    {
        /// <summary>Last known overlay visibility. Startup honours this value.</summary>
        public bool OverlayVisible { get; set; } = false;

        /// <summary>
        /// When true the overlay becomes click-through while the game is running so
        /// gameplay input is never intercepted.
        /// </summary>
        public bool ClickThroughWhenIdle { get; set; } = true;

        /// <summary>Opacity of the overlay window (0.1 - 1.0).</summary>
        public double OverlayOpacity { get; set; } = 0.9;

        /// <summary>
        /// X offset of the overlay panel inside the overlay canvas, in
        /// device-independent pixels (DIPs).
        /// </summary>
        public int PanelX { get; set; } = 0;

        /// <summary>
        /// Y offset of the overlay panel inside the overlay canvas, in
        /// device-independent pixels (DIPs).
        /// </summary>
        public int PanelY { get; set; } = 0;

        /// <summary>
        /// MAXIMUM width the overlay panel may grow to, in device-independent pixels
        /// (DIPs). The slab auto-sizes to its caption text and grows up to this cap;
        /// it is NOT an exact width (the slab is narrower whenever captions are short).
        /// Captions wrap once a line would exceed it.
        /// </summary>
        public int PanelMaxWidth { get; set; } = 640;

        /// <summary>
        /// When true the panel is horizontally centered within the overlay window
        /// (keeping its Y position); the manually dragged X in <see cref="PanelX"/> is
        /// preserved so unchecking restores it.
        /// </summary>
        public bool PanelCenteredHorizontally { get; set; } = false;

        /// <summary>When true the overlay is only shown while the game window exists.</summary>
        public bool ShowOnlyWhenGameRunning { get; set; } = false;

        public bool HotkeyCtrl { get; set; } = true;

        public bool HotkeyShift { get; set; } = true;

        public bool HotkeyAlt { get; set; } = false;

        /// <summary>Virtual-key code. 0x4F == 'O'.</summary>
        public uint HotkeyKey { get; set; } = 0x4F;
    }
}
