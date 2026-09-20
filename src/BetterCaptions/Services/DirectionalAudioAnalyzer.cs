using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace BetterCaptions.Services
{
    /// <summary>
    /// Immutable snapshot of the smoothed, 0..1 per-direction audio levels. A UI thread
    /// can render this directly; the struct is safe to copy across threads.
    /// </summary>
    public readonly struct DirectionalAudioLevels
    {
        public DirectionalAudioLevels(
            float left,
            float right,
            float front,
            float back,
            float frontLeft,
            float frontRight,
            float backLeft,
            float backRight,
            bool frontBackAvailable)
        {
            Left = left;
            Right = right;
            Front = front;
            Back = back;
            FrontLeft = frontLeft;
            FrontRight = frontRight;
            BackLeft = backLeft;
            BackRight = backRight;
            FrontBackAvailable = frontBackAvailable;
        }

        /// <summary>Smoothed level for the left hemisphere, 0..1.</summary>
        public float Left { get; }

        /// <summary>Smoothed level for the right hemisphere, 0..1.</summary>
        public float Right { get; }

        /// <summary>Smoothed level for front-centre content, 0..1.</summary>
        public float Front { get; }

        /// <summary>Smoothed level for rear content, 0..1.</summary>
        public float Back { get; }

        /// <summary>Smoothed level for the front-left corner, 0..1.</summary>
        public float FrontLeft { get; }

        /// <summary>Smoothed level for the front-right corner, 0..1.</summary>
        public float FrontRight { get; }

        /// <summary>Smoothed level for the back-left corner, 0..1.</summary>
        public float BackLeft { get; }

        /// <summary>Smoothed level for the back-right corner, 0..1.</summary>
        public float BackRight { get; }

        /// <summary>
        /// False when the capture format only has two channels: front, back and the four
        /// corners are not available and are always zero. The UI must say so explicitly.
        /// </summary>
        public bool FrontBackAvailable { get; }
    }

    /// <summary>
    /// Turns captured system-audio frames into smoothed, per-direction 0..1 levels.
    /// </summary>
    /// <remarks>
    /// Processing runs on the caller's (capture) thread and is allocation-free once the
    /// format is known. A separate timer publishes the latest snapshot at roughly 60 Hz
    /// so consumers are never called once per audio block.
    ///
    /// Signal chain per channel: high-pass biquad(s) at the user's threshold, then an
    /// envelope follower (fast attack, slow release). Direction levels are combined in
    /// the linear domain and finally mapped to 0..1 through a fixed dB window with
    /// hysteresis and peak-hold so indicators do not flicker.
    /// </remarks>
    public sealed class DirectionalAudioAnalyzer : IDisposable
    {
        // ---- DSP parameters -------------------------------------------------------

        /// <summary>
        /// Number of cascaded RBJ high-pass biquad sections per channel. Three sections
        /// (6th order) give enough stop-band rejection for the threshold to actually gate
        /// content well below the cut-off (a single 2nd-order section only attenuates a
        /// tone one-and-a-half octaves below the cut-off by about 19 dB).
        /// </summary>
        private const int HighPassSections = 3;

        /// <summary>Butterworth Q for each RBJ high-pass section.</summary>
        private const float FilterQ = 0.70710678f;

        /// <summary>Envelope attack time constant, seconds (~10 ms).</summary>
        private const double AttackTauSeconds = 0.010;

        /// <summary>Envelope release time constant, seconds (~200 ms).</summary>
        private const double ReleaseTauSeconds = 0.200;

        /// <summary>Bottom of the level-mapping window, dBFS.</summary>
        private const double FloorDb = -60.0;

        /// <summary>Top of the level-mapping window, dBFS.</summary>
        private const double CeilingDb = 0.0;

        /// <summary>Minimum level change before the displayed value follows (anti-flicker).</summary>
        private const float Hysteresis = 0.03f;

        /// <summary>How fast the peak-hold falls, in level units per second.</summary>
        private const double PeakHoldDecayPerSecond = 1.5;

        /// <summary>Publish interval, milliseconds (about 60 Hz).</summary>
        private const int PublishIntervalMs = 16;

        // Direction indices into the fixed 8-element direction arrays.
        private const int DirLeft = 0;
        private const int DirRight = 1;
        private const int DirFront = 2;
        private const int DirBack = 3;
        private const int DirFrontLeft = 4;
        private const int DirFrontRight = 5;
        private const int DirBackLeft = 6;
        private const int DirBackRight = 7;
        private const int DirectionCount = 8;

        // WAVE_FORMAT_EXTENSIBLE speaker bits (KS_DATAFORMAT / dwChannelMask).
        private const int SpeakerFrontLeft = 0x1;
        private const int SpeakerFrontRight = 0x2;
        private const int SpeakerFrontCenter = 0x4;
        private const int SpeakerLowFrequency = 0x8;
        private const int SpeakerBackLeft = 0x10;
        private const int SpeakerBackRight = 0x20;
        private const int SpeakerSideLeft = 0x200;
        private const int SpeakerSideRight = 0x400;

        // ---- Threading/state ------------------------------------------------------

        /// <summary>Guards <see cref="_filters"/>, envelopes and all DSP state.</summary>
        private readonly object _dspGate = new object();

        /// <summary>Guards the published snapshot.</summary>
        private readonly object _stateGate = new object();

        private readonly Timer _publishTimer;

        private readonly float[] _directionEnvelopes = new float[DirectionCount];
        private readonly float[] _hysteresis = new float[DirectionCount];
        private readonly float[] _peaks = new float[DirectionCount];
        private readonly float[] _displayLevels = new float[DirectionCount];

        private BiQuadFilter[][] _filters = Array.Empty<BiQuadFilter[]>();
        private float[] _envelopes = Array.Empty<float>();
        private int[][] _directionChannels = Array.Empty<int[]>();

        private int _sampleRate;
        private int _channels;
        private int _bitsPerSample;
        private WaveFormatEncoding _encoding;
        private int _formatSignature;
        private bool _hasFormat;
        private bool _frontBackAvailable;

        private double _attackCoeff;
        private double _releaseCoeff;

        private float _thresholdHz = 1500f;
        private float _sensitivity = 1.0f;

        private DirectionalAudioLevels _current;
        private volatile bool _dirty;
        private volatile bool _disposed;

        public DirectionalAudioAnalyzer()
        {
            _publishTimer = new Timer(PublishTick, null, PublishIntervalMs, PublishIntervalMs);
        }

        /// <summary>
        /// Raised on a thread-pool thread at roughly 60 Hz with the latest smoothed
        /// snapshot. Never raised per audio block. Consumers must marshal to the UI thread.
        /// </summary>
        public event Action<DirectionalAudioLevels>? LevelsUpdated;

        /// <summary>The most recently computed snapshot (safe to read from any thread).</summary>
        public DirectionalAudioLevels Current
        {
            get
            {
                lock (_stateGate)
                {
                    return _current;
                }
            }
        }

        /// <summary>
        /// Applies a new threshold (Hz) and sensitivity (linear gain) to the running
        /// analyzer. Recomputes the biquad coefficients in place without disturbing
        /// capture. Safe to call from the UI thread.
        /// </summary>
        public void Configure(double thresholdHz, double sensitivity)
        {
            float threshold = thresholdHz > 0 ? (float)thresholdHz : 1500f;
            float gain = sensitivity > 0 ? (float)sensitivity : 1f;

            lock (_dspGate)
            {
                _thresholdHz = threshold;
                _sensitivity = gain;

                if (!_hasFormat)
                {
                    return;
                }

                float cutoff = ClampCutoff(threshold, _sampleRate);
                for (int ch = 0; ch < _filters.Length; ch++)
                {
                    BiQuadFilter[] sections = _filters[ch];
                    for (int s = 0; s < sections.Length; s++)
                    {
                        // Recompute the coefficients in place; capture is not restarted.
                        // SetHighPassFilter also clears stale filter state so the new
                        // response takes effect immediately.
                        sections[s].SetHighPassFilter(_sampleRate, cutoff, FilterQ);
                    }
                }
            }
        }

        /// <summary>
        /// Consumes one captured block. Called on the capture thread; allocation-free once
        /// the format is known. Never throws.
        /// </summary>
        public void Process(ReadOnlySpan<byte> buffer, WaveFormat format, AudioClientBufferFlags flags)
        {
            if (_disposed || buffer.Length == 0)
            {
                return;
            }

            bool discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
            bool silent = (flags & AudioClientBufferFlags.Silent) != 0;

            try
            {
                lock (_dspGate)
                {
                    EnsureFormat(format);
                    if (!_hasFormat)
                    {
                        return;
                    }

                    if (discontinuity)
                    {
                        ResetFilterState();
                    }

                    int bytesPerSample = Math.Max(1, _bitsPerSample / 8);
                    int frameBytes = bytesPerSample * _channels;
                    if (frameBytes <= 0)
                    {
                        return;
                    }

                    int frames = buffer.Length / frameBytes;
                    if (frames <= 0)
                    {
                        return;
                    }

                    switch (_encoding)
                    {
                        case WaveFormatEncoding.IeeeFloat when _bitsPerSample == 32:
                            ProcessFloat(buffer, frames, bytesPerSample, silent);
                            break;
                        case WaveFormatEncoding.Pcm when _bitsPerSample == 16:
                            ProcessInt16(buffer, frames, bytesPerSample, silent);
                            break;
                        case WaveFormatEncoding.Pcm when _bitsPerSample == 32:
                            ProcessInt32(buffer, frames, bytesPerSample, silent);
                            break;
                        case WaveFormatEncoding.Pcm when _bitsPerSample == 24:
                            ProcessInt24(buffer, frames, bytesPerSample, silent);
                            break;
                        default:
                            // Unsupported sample format: hold the last levels rather than
                            // throwing on the audio thread.
                            return;
                    }

                    MapDirections();
                    UpdatePresentation((double)frames / _sampleRate);
                    StoreSnapshot();
                }
            }
            catch
            {
                // The audio callback must never throw into NAudio.
            }
        }

        /// <summary>Clears filter and envelope state, e.g. after a stream discontinuity.</summary>
        public void Reset()
        {
            lock (_dspGate)
            {
                if (_hasFormat)
                {
                    ResetFilterState();
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _publishTimer.Dispose();
        }

        // ---- Format / filter setup -------------------------------------------------

        private void EnsureFormat(WaveFormat format)
        {
            int mask = format is WaveFormatExtensible extensible ? extensible.ChannelMask : 0;
            int subFormatHash = format is WaveFormatExtensible ext ? ext.SubFormat.GetHashCode() : 0;
            int signature = HashCode.Combine(
                format.SampleRate,
                format.Channels,
                format.BitsPerSample,
                (int)format.Encoding,
                subFormatHash,
                mask);

            if (_hasFormat && signature == _formatSignature)
            {
                return;
            }

            WaveFormat effective = format is WaveFormatExtensible waveFormatExtensible
                ? waveFormatExtensible.ToStandardWaveFormat()
                : format;

            int sampleRate = effective.SampleRate;
            int channels = effective.Channels;
            if (sampleRate <= 0 || channels <= 0)
            {
                _hasFormat = false;
                return;
            }

            _sampleRate = sampleRate;
            _channels = channels;
            _encoding = effective.Encoding;
            _bitsPerSample = effective.BitsPerSample;

            float cutoff = ClampCutoff(_thresholdHz, sampleRate);

            var filters = new BiQuadFilter[channels][];
            for (int ch = 0; ch < channels; ch++)
            {
                var sections = new BiQuadFilter[HighPassSections];
                for (int s = 0; s < HighPassSections; s++)
                {
                    sections[s] = BiQuadFilter.HighPassFilter(sampleRate, cutoff, FilterQ);
                }

                filters[ch] = sections;
            }

            _filters = filters;
            _envelopes = new float[channels];
            _attackCoeff = 1.0 - Math.Exp(-1.0 / (AttackTauSeconds * sampleRate));
            _releaseCoeff = 1.0 - Math.Exp(-1.0 / (ReleaseTauSeconds * sampleRate));
            _directionChannels = BuildDirectionChannels(channels, mask);
            _frontBackAvailable = channels > 2;
            _hasFormat = true;
            _formatSignature = signature;

            Array.Clear(_directionEnvelopes, 0, _directionEnvelopes.Length);
            Array.Clear(_hysteresis, 0, _hysteresis.Length);
            Array.Clear(_peaks, 0, _peaks.Length);
            Array.Clear(_displayLevels, 0, _displayLevels.Length);
        }

        private static float ClampCutoff(float thresholdHz, int sampleRate)
        {
            float max = sampleRate * 0.5f * 0.9f;
            if (thresholdHz < 1f)
            {
                thresholdHz = 1f;
            }

            if (thresholdHz > max)
            {
                thresholdHz = max;
            }

            return thresholdHz;
        }

        private void ResetFilterState()
        {
            for (int ch = 0; ch < _filters.Length; ch++)
            {
                BiQuadFilter[] sections = _filters[ch];
                for (int s = 0; s < sections.Length; s++)
                {
                    sections[s].ResetState();
                }
            }

            Array.Clear(_envelopes, 0, _envelopes.Length);
        }

        // ---- Per-direction channel mapping ----------------------------------------

        /// <summary>
        /// Builds, for each of the eight directions, the list of input channels whose
        /// envelope feeds it. Channels are resolved from the WAVE_FORMAT_EXTENSIBLE
        /// channel mask (speaker bits in ascending order) when present, and from the
        /// canonical layout otherwise. LFE is intentionally ignored (not directional).
        /// </summary>
        private static int[][] BuildDirectionChannels(int channels, int mask)
        {
            int[] speakers = BuildSpeakerMap(channels, mask);

            return new[]
            {
                ChannelsWithAnySpeaker(speakers, SpeakerFrontLeft | SpeakerSideLeft),   // Left
                ChannelsWithAnySpeaker(speakers, SpeakerFrontRight | SpeakerSideRight), // Right
                ChannelsWithAnySpeaker(speakers, SpeakerFrontLeft | SpeakerFrontRight | SpeakerFrontCenter), // Front
                ChannelsWithAnySpeaker(speakers, SpeakerBackLeft | SpeakerBackRight),   // Back
                ChannelsWithAnySpeaker(speakers, SpeakerFrontLeft),                     // FrontLeft
                ChannelsWithAnySpeaker(speakers, SpeakerFrontRight),                    // FrontRight
                ChannelsWithAnySpeaker(speakers, SpeakerBackLeft),                      // BackLeft
                ChannelsWithAnySpeaker(speakers, SpeakerBackRight)                      // BackRight
            };
        }

        private static int[] ChannelsWithAnySpeaker(int[] speakers, int speakerMask)
        {
            var channels = new System.Collections.Generic.List<int>(4);
            for (int ch = 0; ch < speakers.Length; ch++)
            {
                if ((speakers[ch] & speakerMask) != 0)
                {
                    channels.Add(ch);
                }
            }

            return channels.ToArray();
        }

        private static int[] BuildSpeakerMap(int channels, int mask)
        {
            var result = new int[channels];

            if (mask != 0 && BitOperations.PopCount((uint)mask) == channels)
            {
                int ch = 0;
                for (int bit = 0; bit < 32 && ch < channels; bit++)
                {
                    int speaker = 1 << bit;
                    if ((mask & speaker) != 0)
                    {
                        result[ch++] = speaker;
                    }
                }

                return result;
            }

            // No usable mask: fall back to the canonical Microsoft channel order.
            int[] fallback = channels switch
            {
                1 => new[] { SpeakerFrontCenter },
                2 => new[] { SpeakerFrontLeft, SpeakerFrontRight },
                3 => new[] { SpeakerFrontLeft, SpeakerFrontRight, SpeakerFrontCenter },
                4 => new[] { SpeakerFrontLeft, SpeakerFrontRight, SpeakerBackLeft, SpeakerBackRight },
                5 => new[] { SpeakerFrontLeft, SpeakerFrontRight, SpeakerFrontCenter, SpeakerBackLeft, SpeakerBackRight },
                6 => new[] { SpeakerFrontLeft, SpeakerFrontRight, SpeakerFrontCenter, SpeakerLowFrequency, SpeakerBackLeft, SpeakerBackRight },
                7 => new[] { SpeakerFrontLeft, SpeakerFrontRight, SpeakerFrontCenter, SpeakerLowFrequency, SpeakerBackLeft, SpeakerBackRight, SpeakerFrontCenter },
                8 => new[] { SpeakerFrontLeft, SpeakerFrontRight, SpeakerFrontCenter, SpeakerLowFrequency, SpeakerBackLeft, SpeakerBackRight, SpeakerSideLeft, SpeakerSideRight },
                _ => Array.Empty<int>()
            };

            for (int ch = 0; ch < channels; ch++)
            {
                result[ch] = ch < fallback.Length ? fallback[ch] : 0;
            }

            return result;
        }

        // ---- Sample decoding (allocation-free) ------------------------------------

        private void ProcessFloat(ReadOnlySpan<byte> buffer, int frames, int bytesPerSample, bool silent)
        {
            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(
                buffer.Slice(0, frames * bytesPerSample * _channels));

            int channels = _channels;
            int index = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    UpdateChannel(ch, silent ? 0f : samples[index++]);
                }
            }
        }

        private void ProcessInt16(ReadOnlySpan<byte> buffer, int frames, int bytesPerSample, bool silent)
        {
            ReadOnlySpan<short> samples = MemoryMarshal.Cast<byte, short>(
                buffer.Slice(0, frames * bytesPerSample * _channels));

            const float scale = 1f / 32768f;
            int channels = _channels;
            int index = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    UpdateChannel(ch, silent ? 0f : samples[index++] * scale);
                }
            }
        }

        private void ProcessInt32(ReadOnlySpan<byte> buffer, int frames, int bytesPerSample, bool silent)
        {
            ReadOnlySpan<int> samples = MemoryMarshal.Cast<byte, int>(
                buffer.Slice(0, frames * bytesPerSample * _channels));

            const float scale = 1f / 2147483648f;
            int channels = _channels;
            int index = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    UpdateChannel(ch, silent ? 0f : samples[index++] * scale);
                }
            }
        }

        private void ProcessInt24(ReadOnlySpan<byte> buffer, int frames, int bytesPerSample, bool silent)
        {
            const float scale = 1f / 8388608f;
            int channels = _channels;
            int index = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    float sample = 0f;
                    if (!silent)
                    {
                        int b0 = buffer[index];
                        int b1 = buffer[index + 1];
                        int b2 = buffer[index + 2];
                        int value = (b2 << 16) | (b1 << 8) | b0;
                        if ((value & 0x800000) != 0)
                        {
                            value |= unchecked((int)0xFF000000);
                        }

                        sample = value * scale;
                    }

                    index += 3;
                    UpdateChannel(ch, sample);
                }
            }
        }

        private void UpdateChannel(int ch, float sample)
        {
            BiQuadFilter[] sections = _filters[ch];
            float filtered = sample;
            for (int s = 0; s < sections.Length; s++)
            {
                filtered = sections[s].Transform(filtered);
            }

            float magnitude = filtered < 0f ? -filtered : filtered;
            float envelope = _envelopes[ch];
            double coeff = magnitude > envelope ? _attackCoeff : _releaseCoeff;
            _envelopes[ch] = (float)(envelope + coeff * (magnitude - envelope));
        }

        // ---- Direction combination and presentation -------------------------------

        private void MapDirections()
        {
            if (!_frontBackAvailable)
            {
                float left = _channels >= 1 ? _envelopes[0] : 0f;
                float right = _channels >= 2 ? _envelopes[1] : left;

                _directionEnvelopes[DirLeft] = left;
                _directionEnvelopes[DirRight] = right;
                for (int d = DirFront; d < DirectionCount; d++)
                {
                    _directionEnvelopes[d] = 0f;
                }

                return;
            }

            for (int d = 0; d < DirectionCount; d++)
            {
                int[] channels = _directionChannels[d];
                float max = 0f;
                for (int i = 0; i < channels.Length; i++)
                {
                    float value = _envelopes[channels[i]];
                    if (value > max)
                    {
                        max = value;
                    }
                }

                _directionEnvelopes[d] = max;
            }
        }

        private void UpdatePresentation(double dt)
        {
            for (int d = 0; d < DirectionCount; d++)
            {
                float target = EnvelopeToLevel(_directionEnvelopes[d]);

                // Hysteresis: only follow a meaningful change so a steady signal holds.
                float held = _hysteresis[d];
                if (target > held + Hysteresis)
                {
                    held = target;
                }
                else if (target < held - Hysteresis)
                {
                    held = target;
                }

                // Peak-hold: snap up instantly, decay slowly, never below the held value.
                float peak = _peaks[d];
                if (held >= peak)
                {
                    peak = held;
                }
                else
                {
                    peak -= (float)(PeakHoldDecayPerSecond * dt);
                    if (peak < held)
                    {
                        peak = held;
                    }

                    if (peak < 0f)
                    {
                        peak = 0f;
                    }
                }

                _hysteresis[d] = held;
                _peaks[d] = peak;
                _displayLevels[d] = peak;
            }
        }

        private float EnvelopeToLevel(float envelope)
        {
            if (envelope <= 0f)
            {
                return 0f;
            }

            double linear = envelope * _sensitivity;
            if (linear <= 0.0)
            {
                return 0f;
            }

            double db = 20.0 * Math.Log10(linear);
            if (db <= FloorDb)
            {
                return 0f;
            }

            double normalized = (db - FloorDb) / (CeilingDb - FloorDb);
            if (normalized > 1.0)
            {
                normalized = 1.0;
            }

            return (float)normalized;
        }

        private void StoreSnapshot()
        {
            var snapshot = new DirectionalAudioLevels(
                _displayLevels[DirLeft],
                _displayLevels[DirRight],
                _displayLevels[DirFront],
                _displayLevels[DirBack],
                _displayLevels[DirFrontLeft],
                _displayLevels[DirFrontRight],
                _displayLevels[DirBackLeft],
                _displayLevels[DirBackRight],
                _frontBackAvailable);

            lock (_stateGate)
            {
                // Only flag a publish when the snapshot actually changed, so a steady or
                // silent stream does not wake the consumer 60 times a second.
                if (!LevelsEqual(_current, snapshot))
                {
                    _current = snapshot;
                    _dirty = true;
                }
            }
        }

        private static bool LevelsEqual(DirectionalAudioLevels a, DirectionalAudioLevels b)
        {
            const float epsilon = 0.0005f;
            return a.FrontBackAvailable == b.FrontBackAvailable
                && Math.Abs(a.Left - b.Left) < epsilon
                && Math.Abs(a.Right - b.Right) < epsilon
                && Math.Abs(a.Front - b.Front) < epsilon
                && Math.Abs(a.Back - b.Back) < epsilon
                && Math.Abs(a.FrontLeft - b.FrontLeft) < epsilon
                && Math.Abs(a.FrontRight - b.FrontRight) < epsilon
                && Math.Abs(a.BackLeft - b.BackLeft) < epsilon
                && Math.Abs(a.BackRight - b.BackRight) < epsilon;
        }

        private void PublishTick(object? state)
        {
            if (_disposed)
            {
                return;
            }

            DirectionalAudioLevels snapshot;
            lock (_stateGate)
            {
                if (!_dirty)
                {
                    return;
                }

                _dirty = false;
                snapshot = _current;
            }

            LevelsUpdated?.Invoke(snapshot);
        }
    }
}
