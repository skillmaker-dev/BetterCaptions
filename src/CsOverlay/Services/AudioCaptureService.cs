using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CsOverlay.Services
{
    /// <summary>High-level state of the system-audio capture.</summary>
    public enum AudioCaptureStatus
    {
        /// <summary>Loopback capture is running.</summary>
        Capturing,

        /// <summary>No usable default render device exists.</summary>
        NoDevice,

        /// <summary>The device was invalidated/changed and is being reopened.</summary>
        DeviceInvalidated,

        /// <summary>
        /// Sustained silence, or the endpoint could not be opened shared-mode. Most often
        /// the output is muted, or another application holds the device in EXCLUSIVE mode
        /// (which WASAPI loopback cannot capture). Not an error.
        /// </summary>
        PossiblyExclusiveOrMuted,

        /// <summary>Capture could not be started for another reason.</summary>
        Failed
    }

    /// <summary>
    /// Captures the default render device's output via WASAPI loopback and forwards the
    /// raw frames to a sink. Event-driven, off the UI thread, and designed never to throw
    /// into the audio path or disturb the game.
    /// </summary>
    /// <remarks>
    /// Robustness model:
    /// <list type="bullet">
    /// <item>AUDCLNT_E_DEVICE_INVALIDATED (and any unexpected stop) tears the recorder down
    /// and reopens it after a short debounce.</item>
    /// <item>Default render-device changes are observed through NAudio's public
    /// <see cref="MMDeviceNotificationClient"/> and trigger a reopen.</item>
    /// <item>A sustained all-zero stream is surfaced as
    /// <see cref="AudioCaptureStatus.PossiblyExclusiveOrMuted"/> - never thrown.</item>
    /// <item>Because loopback is shared-mode only, an endpoint held in exclusive mode
    /// cannot be captured; that case is reported so the UI can tell the user to disable
    /// exclusive mode.</item>
    /// </list>
    /// </remarks>
    public sealed class AudioCaptureService : IDisposable
    {
        /// <summary>Signature of the audio callback. Receives the recorder's negotiated format.</summary>
        public delegate void AudioFrameHandler(ReadOnlySpan<byte> buffer, WaveFormat format, AudioClientBufferFlags flags);

        private const int SilenceStatusSeconds = 3;
        private const int ReopenDebounceMs = 300;
        private const int RetryBaseMs = 500;
        private const int RetryMaxMs = 5000;

        // WASAPI error codes we classify explicitly.
        private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
        private const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
        private const int AudclntEExclusiveModeNotAllowed = unchecked((int)0x8889000E);

        private readonly object _gate = new object();

        private MMDeviceEnumerator? _enumerator;
        private MMDeviceNotificationClient? _notificationClient;
        private WasapiRecorder? _recorder;
        private WaveFormat? _format;
        private Timer? _reopenTimer;

        private volatile bool _running;
        private volatile bool _disposed;
        private volatile bool _closingRecorder;
        private int _opening;
        private int _generation;
        private int _retryDelayMs = RetryBaseMs;
        private int _silenceReported;
        private long _lastAudibleMs;

        private AudioCaptureStatus _state = AudioCaptureStatus.NoDevice;
        private string _statusMessage = "Audio capture not started.";

        public AudioCaptureService()
        {
            _reopenTimer = new Timer(ReopenTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Raised whenever the status changes (state plus human-readable message), on a
        /// non-UI thread. Consumers must marshal to the UI thread.
        /// </summary>
        public event Action<AudioCaptureStatus, string>? StatusChanged;

        /// <summary>
        /// Sink for captured frames. Set before <see cref="Start"/>. The format argument is
        /// the recorder's negotiated (mix) format.
        /// </summary>
        public AudioFrameHandler? FrameSink { get; set; }

        /// <summary>The negotiated capture format, or null while not capturing.</summary>
        public WaveFormat? Format
        {
            get
            {
                lock (_gate)
                {
                    return _format;
                }
            }
        }

        /// <summary>Negotiated sample rate in Hz, or 0 while not capturing.</summary>
        public int SampleRate => Format?.SampleRate ?? 0;

        /// <summary>Negotiated channel count, or 0 while not capturing.</summary>
        public int Channels => Format?.Channels ?? 0;

        /// <summary>The current status state, safe to read from any thread.</summary>
        public AudioCaptureStatus Status
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        /// <summary>The current human-readable status message, safe to read from any thread.</summary>
        public string StatusMessage
        {
            get
            {
                lock (_gate)
                {
                    return _statusMessage;
                }
            }
        }

        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                if (_running)
                {
                    return;
                }

                _running = true;
                _generation++;
                _lastAudibleMs = Environment.TickCount64;
                Interlocked.Exchange(ref _silenceReported, 0);
            }

            EnsureNotificationClient();
            ScheduleReopen(0);
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_running)
                {
                    return;
                }

                _running = false;
                _generation++;
                _reopenTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }

            CloseRecorder();
            SetStatus(AudioCaptureStatus.NoDevice, "Audio capture stopped.");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();

            lock (_gate)
            {
                _reopenTimer?.Dispose();
                _reopenTimer = null;

                if (_notificationClient is not null)
                {
                    _notificationClient.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                    _notificationClient.Dispose();
                    _notificationClient = null;
                }

                _enumerator?.Dispose();
                _enumerator = null;
            }
        }

        // ---- Opening / reopening --------------------------------------------------

        private void ScheduleReopen(int delayMs)
        {
            if (!_running || _disposed)
            {
                return;
            }

            _reopenTimer?.Change(delayMs, Timeout.Infinite);
        }

        private void ScheduleRetry()
        {
            int delay = _retryDelayMs;
            _retryDelayMs = Math.Min(_retryDelayMs * 2, RetryMaxMs);
            ScheduleReopen(delay);
        }

        private void ReopenTick(object? state)
        {
            if (!_running || _disposed)
            {
                return;
            }

            if (Interlocked.Exchange(ref _opening, 1) == 1)
            {
                return;
            }

            try
            {
                OpenCapture();
            }
            catch (Exception ex)
            {
                HandleFailure(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _opening, 0);
            }
        }

        private void OpenCapture()
        {
            if (!_running || _disposed)
            {
                return;
            }

            CloseRecorder();

            int generation = Volatile.Read(ref _generation);

            MMDeviceEnumerator enumerator = EnsureEnumerator();

            WaveFormat? mixFormat = null;
            string friendlyName = string.Empty;
            try
            {
                if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out MMDevice? device) ||
                    device is null)
                {
                    SetStatus(AudioCaptureStatus.NoDevice, "No audio output device found.");
                    ScheduleRetry();
                    return;
                }

                using (device)
                {
                    friendlyName = device.FriendlyName;
                    try
                    {
                        using AudioClient audioClient = device.CreateAudioClient();
                        mixFormat = audioClient.MixFormat;
                    }
                    catch
                    {
                        mixFormat = null;
                    }
                }
            }
            catch (Exception ex)
            {
                HandleFailure(ex);
                return;
            }

            if (mixFormat is null)
            {
                SetStatus(AudioCaptureStatus.NoDevice, "Could not read the audio output device format.");
                ScheduleRetry();
                return;
            }

            WasapiRecorder recorder = BuildRecorder(mixFormat);
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;

            bool discarded;
            lock (_gate)
            {
                discarded = !_running || _disposed || generation != Volatile.Read(ref _generation);
                if (!discarded)
                {
                    _recorder = recorder;
                    _format = recorder.WaveFormat ?? mixFormat;
                    _lastAudibleMs = Environment.TickCount64;
                    Interlocked.Exchange(ref _silenceReported, 0);
                }
            }

            if (discarded)
            {
                // A Stop or a newer reopen invalidated this attempt; dispose off-lock so a
                // synchronous RecordingStopped callback can never deadlock.
                DetachAndDispose(recorder);
                return;
            }

            try
            {
                recorder.StartRecording();
            }
            catch (Exception ex)
            {
                // Roll the half-open recorder back before classifying the failure.
                CloseRecorder();
                HandleFailure(ex);
                return;
            }

            _retryDelayMs = RetryBaseMs;
            WaveFormat negotiated = recorder.WaveFormat ?? mixFormat;
            string name = string.IsNullOrEmpty(friendlyName) ? "default output device" : friendlyName;
            SetStatus(
                AudioCaptureStatus.Capturing,
                $"Capturing system audio from {name} ({negotiated.SampleRate} Hz, {negotiated.Channels} ch).");
        }

        private static WasapiRecorder BuildRecorder(WaveFormat mixFormat)
        {
            return new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .WithSharedMode()
                .WithEventSync()
                .WithFormat(mixFormat)
                .Build();
        }

        private void CloseRecorder()
        {
            WasapiRecorder? recorder;
            lock (_gate)
            {
                recorder = _recorder;
                _recorder = null;
                _format = null;
            }

            if (recorder is null)
            {
                return;
            }

            DetachAndDispose(recorder);
        }

        private void DetachAndDispose(WasapiRecorder recorder)
        {
            _closingRecorder = true;
            try
            {
                recorder.DataAvailable -= OnDataAvailable;
                recorder.RecordingStopped -= OnRecordingStopped;
            }
            catch
            {
                // Subscription cleanup must never throw.
            }

            try
            {
                recorder.StopRecording();
            }
            catch
            {
                // Already stopped.
            }

            try
            {
                recorder.Dispose();
            }
            catch
            {
                // Disposal must never throw.
            }
            finally
            {
                _closingRecorder = false;
            }
        }

        // ---- Notification / events ------------------------------------------------

        private MMDeviceEnumerator EnsureEnumerator()
        {
            lock (_gate)
            {
                _enumerator ??= new MMDeviceEnumerator();
                return _enumerator;
            }
        }

        private void EnsureNotificationClient()
        {
            if (_notificationClient is not null)
            {
                return;
            }

            try
            {
                MMDeviceNotificationClient client = EnsureEnumerator().CreateNotificationClient(useSynchronizationContext: false);
                client.DefaultDeviceChanged += OnDefaultDeviceChanged;
                _notificationClient = client;
            }
            catch
            {
                // Without notifications we still recover via RecordingStopped; capture
                // must not fail to start just because notifications are unavailable.
            }
        }

        private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e)
        {
            if (!_running || _disposed || e.Flow != DataFlow.Render)
            {
                return;
            }

            SetStatus(AudioCaptureStatus.DeviceInvalidated, "Default audio device changed; switching...");
            ScheduleReopen(ReopenDebounceMs);
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (_closingRecorder || !_running || _disposed)
            {
                return;
            }

            if (IsDeviceInvalidated(e.Exception) || e.Exception is null)
            {
                SetStatus(AudioCaptureStatus.DeviceInvalidated, "Audio device changed or was invalidated; reopening...");
            }
            else if (IsExclusiveOrInUse(e.Exception))
            {
                SetStatus(
                    AudioCaptureStatus.PossiblyExclusiveOrMuted,
                    "The output device is in use in exclusive mode, so system audio cannot be captured. Disable exclusive mode for this device in Windows sound settings.");
            }
            else
            {
                SetStatus(AudioCaptureStatus.Failed, "Audio capture stopped: " + e.Exception.Message);
            }

            ScheduleReopen(ReopenDebounceMs);
        }

        private void OnDataAvailable(
            ReadOnlySpan<byte> buffer,
            AudioClientBufferFlags flags,
            long devicePosition,
            long qpcPosition)
        {
            if (!_running || _disposed)
            {
                return;
            }

            try
            {
                UpdateSignalState(buffer, flags);

                AudioFrameHandler? sink = FrameSink;
                WaveFormat? format = _format;
                if (sink is not null && format is not null)
                {
                    sink(buffer, format, flags);
                }
            }
            catch
            {
                // The audio callback must never throw into NAudio.
            }
        }

        // ---- Status / silence tracking --------------------------------------------

        private void UpdateSignalState(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags)
        {
            bool wasSilent = (flags & AudioClientBufferFlags.Silent) != 0;
            bool allZero = buffer.IndexOfAnyExcept((byte)0) < 0;
            bool audible = !wasSilent && !allZero;

            long now = Environment.TickCount64;

            if (audible)
            {
                _lastAudibleMs = now;
                if (Interlocked.CompareExchange(ref _silenceReported, 0, 1) == 1)
                {
                    SetStatus(AudioCaptureStatus.Capturing, BuildCapturingMessage());
                }

                return;
            }

            if (now - Interlocked.Read(ref _lastAudibleMs) < SilenceStatusSeconds * 1000L)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _silenceReported, 1, 0) == 0)
            {
                SetStatus(
                    AudioCaptureStatus.PossiblyExclusiveOrMuted,
                    "No system audio detected. The output may be muted, or another app is using the device in exclusive mode (which cannot be captured). Disable exclusive mode in Windows sound settings.");
            }
        }

        private string BuildCapturingMessage()
        {
            WaveFormat? format = Format;
            if (format is null)
            {
                return "Capturing system audio.";
            }

            return $"Capturing system audio ({format.SampleRate} Hz, {format.Channels} ch).";
        }

        private void SetStatus(AudioCaptureStatus state, string message)
        {
            bool changed;
            lock (_gate)
            {
                changed = _state != state || !string.Equals(_statusMessage, message, StringComparison.Ordinal);
                _state = state;
                _statusMessage = message;
            }

            if (changed)
            {
                StatusChanged?.Invoke(state, message);
            }
        }

        private void HandleFailure(Exception? exception)
        {
            if (exception is null)
            {
                SetStatus(AudioCaptureStatus.Failed, "Audio capture failed.");
            }
            else if (IsDeviceInvalidated(exception))
            {
                SetStatus(AudioCaptureStatus.DeviceInvalidated, "Audio device invalidated; reopening...");
            }
            else if (IsExclusiveOrInUse(exception))
            {
                SetStatus(
                    AudioCaptureStatus.PossiblyExclusiveOrMuted,
                    "The output device is busy or held in exclusive mode. Disable exclusive mode for this device in Windows sound settings.");
            }
            else
            {
                SetStatus(AudioCaptureStatus.Failed, $"Audio capture failed (0x{exception.HResult:X8}): {exception.Message}");
            }

            ScheduleRetry();
        }

        private static bool IsDeviceInvalidated(Exception? exception) =>
            exception is not null && exception.HResult == AudclntEDeviceInvalidated;

        private static bool IsExclusiveOrInUse(Exception? exception) =>
            exception is not null &&
            (exception.HResult == AudclntEDeviceInUse || exception.HResult == AudclntEExclusiveModeNotAllowed);
    }
}

