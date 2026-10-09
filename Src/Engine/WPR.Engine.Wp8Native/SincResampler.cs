namespace WPR.Wp8Native
{
    /// <summary>
    /// Streaming windowed-sinc resampler for interleaved 16-bit PCM, with a pitch factor.
    /// </summary>
    /// <remarks>
    /// Android's AudioTrack takes any rate, but anything other than the device's native one
    /// (48 kHz on the S24) is refused the low-latency fast path and resampled by AudioFlinger's
    /// general-purpose resampler, which made WP8 titles' sound (Modern Combat 4 mixes at 32 kHz)
    /// dull. 16 taps, a Blackman window and 512 interpolated phases: inaudible next to the source.
    /// </remarks>
    internal sealed class SincResampler
    {
        private const int Taps = 16;
        private const int Half = Taps / 2;
        private const int Phases = 512;

        private readonly int _channels;
        private readonly double _baseStep;
        private double _step;
        private float[] _table = [];
        private double _tableCutoff = -1;

        /// <summary>Input frames not yet fully consumed, interleaved, as floats.</summary>
        private float[] _history;
        private int _historyFrames;

        /// <summary>Position of the next output frame, in input frames from the start of <see cref="_history"/>.</summary>
        private double _position;

        public SincResampler(int inputRate, int outputRate, int channels)
        {
            _channels = channels;
            _baseStep = inputRate / (double)outputRate;
            _history = new float[4096 * channels];
            SetPitch(1.0);
            Reset();
        }

        /// <summary>Plays <paramref name="factor"/> times faster (and higher).</summary>
        public void SetPitch(double factor)
        {
            _step = _baseStep * factor;

            // Cut off at the lower of the two Nyquist rates, a little under it.
            double cutoff = 0.5 * Math.Min(1.0, 1.0 / _step) * 0.97;
            if (Math.Abs(cutoff - _tableCutoff) > 1e-6)
            {
                BuildTable(cutoff);
            }
        }

        /// <summary>Forgets everything queued (a flush).</summary>
        public void Reset()
        {
            // Half a filter of silence ahead of the first frame, so it is centred on real input.
            Array.Clear(_history);
            _historyFrames = Half;
            _position = Half;
        }

        private void BuildTable(double cutoff)
        {
            _tableCutoff = cutoff;
            _table = new float[(Phases + 1) * Taps];
            for (int p = 0; p <= Phases; p++)
            {
                double fraction = p / (double)Phases;
                double sum = 0;
                for (int t = 0; t < Taps; t++)
                {
                    // Distance from the output point to input tap t (taps sit at -Half+1 .. Half).
                    double x = (t - (Half - 1)) - fraction;
                    double u = x / Half;
                    double window = Math.Abs(u) >= 1 ? 0 : 0.42 + (0.5 * Math.Cos(Math.PI * u)) + (0.08 * Math.Cos(2 * Math.PI * u));
                    double argument = 2 * cutoff * x;
                    double sinc = Math.Abs(argument) < 1e-9 ? 1 : Math.Sin(Math.PI * argument) / (Math.PI * argument);
                    double value = 2 * cutoff * sinc * window;
                    _table[(p * Taps) + t] = (float)value;
                    sum += value;
                }

                // Unity gain at DC for every phase.
                for (int t = 0; t < Taps; t++)
                {
                    _table[(p * Taps) + t] = (float)(_table[(p * Taps) + t] / sum);
                }
            }
        }

        /// <summary>Resamples <paramref name="pcm"/> (interleaved 16-bit) and returns what is ready.</summary>
        public byte[] Process(byte[] pcm)
        {
            int inputFrames = pcm.Length / (2 * _channels);
            EnsureHistory(_historyFrames + inputFrames);
            for (int i = 0, at = _historyFrames * _channels; i < inputFrames * _channels; i++, at++)
            {
                _history[at] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
            }

            _historyFrames += inputFrames;

            // Output while the filter's right half still has input under it.
            int capacity = (int)((inputFrames / _step) + 4);
            byte[] output = new byte[capacity * 2 * _channels];
            int produced = 0;
            while (_position + Half < _historyFrames && produced < capacity)
            {
                int index = (int)_position;
                double fraction = _position - index;
                double phase = fraction * Phases;
                int p = (int)phase;
                float blend = (float)(phase - p);
                int row0 = p * Taps;
                int row1 = row0 + Taps;
                int first = index - (Half - 1);
                for (int c = 0; c < _channels; c++)
                {
                    float acc = 0;
                    for (int t = 0; t < Taps; t++)
                    {
                        float weight = _table[row0 + t] + ((_table[row1 + t] - _table[row0 + t]) * blend);
                        acc += _history[((first + t) * _channels) + c] * weight;
                    }

                    int sample = (int)Math.Round(acc * 32767f);
                    short clamped = (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
                    int o = ((produced * _channels) + c) * 2;
                    output[o] = (byte)clamped;
                    output[o + 1] = (byte)(clamped >> 8);
                }

                produced++;
                _position += _step;
            }

            // Drop input the filter can no longer reach.
            int keepFrom = Math.Max(0, (int)_position - (Half - 1));
            if (keepFrom > 0)
            {
                int keep = _historyFrames - keepFrom;
                Array.Copy(_history, keepFrom * _channels, _history, 0, keep * _channels);
                _historyFrames = keep;
                _position -= keepFrom;
            }

            if (produced * 2 * _channels != output.Length)
            {
                Array.Resize(ref output, produced * 2 * _channels);
            }

            return output;
        }

        private void EnsureHistory(int frames)
        {
            if (frames * _channels > _history.Length)
            {
                Array.Resize(ref _history, Math.Max(frames * _channels, _history.Length * 2));
            }
        }
    }
}
