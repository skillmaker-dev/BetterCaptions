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

        /// <summary>
        /// Caption text colour as a hex #AARRGGBB string. The newest caption line uses
        /// this at full value; the older line uses the same hue with alpha scaled to
        /// roughly 75% so the emphasis hierarchy survives.
        /// </summary>
        public string CaptionTextColor { get; set; } = "#FFFFFFFF";

        /// <summary>
        /// When false the caption bars paint no background at all (fully transparent).
        /// </summary>
        public bool CaptionBackgroundEnabled { get; set; } = true;

        /// <summary>
        /// Caption bar background colour as a hex #AARRGGBB string. The alpha channel is
        /// the bar transparency, e.g. the default #8C000000.
        /// </summary>
        public string CaptionBackgroundColor { get; set; } = "#8C000000";

        /// <summary>
        /// Caption glyph size in DIPs for the newest line; the older line renders at
        /// roughly 75% of this. The line box is kept tight around the glyphs (a minimal
        /// ratio, not the font's full proportional leading) so this controls glyph size
        /// rather than the spacing between lines. The panel height floor scales with it.
        /// </summary>
        public double CaptionFontSize { get; set; } = 20;

        /// <summary>
        /// Extra vertical spacing between the two caption bars, in DIPs. This is the ONLY
        /// thing that grows the space between the bars: it is independent of the font
        /// size. The default 0 keeps the bars flush (the original look). It is applied as
        /// a margin between the history bar and the newest bar and is included in the
        /// panel height floor.
        /// </summary>
        public double CaptionLineGap { get; set; } = 0;

        /// <summary>
        /// Maximum number of caption rows shown in the rolling window. The recent caption
        /// text (the reader's lines joined) is word-wrapped and only the newest
        /// CaptionMaxRows rows are displayed; as new words arrive older rows scroll off the
        /// top. The overlay clamps this to a sane range (1..10) and caps the resulting
        /// panel height so it never exceeds the work area.
        /// </summary>
        public int CaptionMaxRows { get; set; } = 3;

        /// <summary>
        /// When true each caption bar centres its text; when false the text is left
        /// aligned (the default, matching the original look). Each bar auto-fits its own
        /// text, so for a single-row caption the text exactly fills the bar and the
        /// alignment is invisible; it shows on a wrapped caption, where the shorter
        /// continuation row is centred instead of left aligned.
        /// </summary>
        public bool CaptionTextCentered { get; set; } = false;

        /// <summary>
        /// When true, CsOverlay minimizes the Windows Live Captions window (and marks it as
        /// a tool window so it leaves the taskbar/Alt-Tab) while the speech engine and its
        /// UI Automation tree keep running, so only the overlay's own captions are visible.
        /// Restored on exit. Default false.
        /// </summary>
        public bool HideLiveCaptionsWindow { get; set; } = false;

        /// <summary>When true the overlay is only shown while the game window exists.</summary>
        public bool ShowOnlyWhenGameRunning { get; set; } = false;

        public bool HotkeyCtrl { get; set; } = true;

        public bool HotkeyShift { get; set; } = true;

        public bool HotkeyAlt { get; set; } = false;

        /// <summary>Virtual-key code. 0x4F == 'O'.</summary>
        public uint HotkeyKey { get; set; } = 0x4F;

        /// <summary>
        /// When true CsOverlay captures the system audio output (WASAPI loopback) and
        /// drives directional audio indicators. Default false, so existing installs keep
        /// their current behaviour until the feature is explicitly turned on.
        /// </summary>
        public bool AudioIndicatorsEnabled { get; set; } = false;

        /// <summary>
        /// High-pass cut-off frequency, in Hz, used by the directional analyzer. Only
        /// energy ABOVE this frequency is allowed to light an indicator, so it selects
        /// which in-game cues are visualised. Applied live - changing it retunes the
        /// running filter without restarting capture. Default 1500.
        /// </summary>
        public double AudioThresholdHz { get; set; } = 1500;

        /// <summary>
        /// Linear gain applied to each channel BEFORE the dB mapping, so quiet cues can
        /// be boosted into the visible window. 1.0 is unity (no change). Applied live
        /// without restarting capture. Default 1.0.
        /// </summary>
        public double AudioSensitivity { get; set; } = 1.0;

        /// <summary>
        /// The level that drives the indicator colour ramp is DIVIDED by this scale - i.e. it
        /// sets how loud the audio must be before the indicator colour reaches the warm end of
        /// the ramp (red). Above 1.0 reaches yellow/orange/red only at louder audio; below 1.0
        /// reaches them sooner. It does NOT affect indicator brightness/opacity, which stays
        /// the unscaled level * MaxIndicatorOpacity. The overlay clamps it to 0.25..4.0 and
        /// applies it live. Default 1.0 (the ramp as designed).
        /// </summary>
        public double AudioLoudnessScale { get; set; } = 1.0;
    }
}
