using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.MediaFoundation;

namespace AudioDuplicator
{
    public class WasapiLoopbackCaptureEx : WasapiCapture
    {
        public WasapiLoopbackCaptureEx(MMDevice captureDevice, int bufferMillis) : base(captureDevice, true, bufferMillis)
        {
        }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        {
            return AudioClientStreamFlags.Loopback;
        }
    }

    // Applies a 2nd-order Butterworth low-pass filter (12 dB/octave) to each audio channel.
    // The cutoff comes from getCutoffHz on every Read, so the UI can change it while audio plays.
    // A cutoff of 0 means off (the audio passes through unchanged). Supports 32-bit float and
    // 16-bit PCM; other formats pass through unchanged (IsSupported is false).
    public sealed class LowPassWaveProvider : IWaveProvider
    {
        private static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

        private readonly IWaveProvider _source;
        private readonly Func<int> _getCutoffHz;
        private readonly int _channels;
        private readonly double _sampleRate;
        private readonly bool _isFloat;
        public readonly bool IsSupported;

        private int _currentHz;
        private double _b0, _b1, _b2, _a1, _a2;
        private readonly double[] _x1, _x2, _y1, _y2;
        private float[] _floatScratch = new float[0];
        private short[] _shortScratch = new short[0];

        public LowPassWaveProvider(IWaveProvider source, Func<int> getCutoffHz)
        {
            _source = source;
            _getCutoffHz = getCutoffHz;
            var f = source.WaveFormat;
            _channels = Math.Max(1, f.Channels);
            _sampleRate = f.SampleRate;

            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
            bool isPcm = f.Encoding == WaveFormatEncoding.Pcm;
            var ext = f as WaveFormatExtensible;
            if (ext != null)
            {
                isFloat = ext.SubFormat == SubtypeFloat;
                isPcm = ext.SubFormat == SubtypePcm;
            }
            _isFloat = isFloat && f.BitsPerSample == 32;
            IsSupported = _isFloat || (isPcm && f.BitsPerSample == 16);

            _x1 = new double[_channels]; _x2 = new double[_channels];
            _y1 = new double[_channels]; _y2 = new double[_channels];
        }

        public WaveFormat WaveFormat { get { return _source.WaveFormat; } }

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            int hz = IsSupported ? _getCutoffHz() : 0;
            if (hz <= 0)
            {
                _currentHz = 0;
                return read;
            }
            if (hz != _currentHz)
            {
                if (_currentHz == 0) ResetState(); // old state is stale after a bypass period
                SetCutoff(hz);
                _currentHz = hz;
            }

            int bytesPerSample = _isFloat ? 4 : 2;
            int frameBytes = bytesPerSample * _channels;
            int bytes = read - (read % frameBytes);
            int samples = bytes / bytesPerSample;

            if (_isFloat)
            {
                if (_floatScratch.Length < samples) _floatScratch = new float[samples];
                Buffer.BlockCopy(buffer, offset, _floatScratch, 0, bytes);
                for (int i = 0; i < samples; i++)
                {
                    _floatScratch[i] = (float)Process(i % _channels, _floatScratch[i]);
                }
                Buffer.BlockCopy(_floatScratch, 0, buffer, offset, bytes);
            }
            else
            {
                if (_shortScratch.Length < samples) _shortScratch = new short[samples];
                Buffer.BlockCopy(buffer, offset, _shortScratch, 0, bytes);
                for (int i = 0; i < samples; i++)
                {
                    double y = Process(i % _channels, _shortScratch[i]);
                    _shortScratch[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(y)));
                }
                Buffer.BlockCopy(_shortScratch, 0, buffer, offset, bytes);
            }
            return read;
        }

        private double Process(int ch, double x)
        {
            double y = _b0 * x + _b1 * _x1[ch] + _b2 * _x2[ch] - _a1 * _y1[ch] - _a2 * _y2[ch];
            if (Math.Abs(y) < 1e-20) y = 0; // flush denormals in silence
            _x2[ch] = _x1[ch]; _x1[ch] = x;
            _y2[ch] = _y1[ch]; _y1[ch] = y;
            return y;
        }

        private void ResetState()
        {
            Array.Clear(_x1, 0, _channels); Array.Clear(_x2, 0, _channels);
            Array.Clear(_y1, 0, _channels); Array.Clear(_y2, 0, _channels);
        }

        // RBJ Audio EQ Cookbook low-pass, Q = 1/sqrt(2).
        private void SetCutoff(int hz)
        {
            double f = Math.Max(10.0, Math.Min(hz, _sampleRate * 0.45));
            double w0 = 2 * Math.PI * f / _sampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * 0.7071067811865476);
            double a0 = 1 + alpha;
            _b0 = (1 - cos) / 2 / a0;
            _b1 = (1 - cos) / a0;
            _b2 = _b0;
            _a1 = -2 * cos / a0;
            _a2 = (1 - alpha) / a0;
        }
    }

    // Adds a sine tone to the source. The same sample goes to every channel.
    // Supports 32-bit float and 16-bit PCM; other formats pass through unchanged.
    public sealed class ToneMixWaveProvider : IWaveProvider
    {
        private static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private const double Amplitude = 0.2;

        private readonly IWaveProvider _source;
        private readonly Func<int> _getFrequencyHz;
        private readonly int _channels;
        private readonly double _sampleRate;
        private readonly bool _isFloat;
        public readonly bool IsSupported;
        private double _phase;
        private float[] _floatScratch = new float[0];
        private short[] _shortScratch = new short[0];

        public ToneMixWaveProvider(IWaveProvider source, Func<int> getFrequencyHz)
        {
            _source = source;
            _getFrequencyHz = getFrequencyHz;
            var f = source.WaveFormat;
            _channels = Math.Max(1, f.Channels);
            _sampleRate = f.SampleRate;

            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
            bool isPcm = f.Encoding == WaveFormatEncoding.Pcm;
            var ext = f as WaveFormatExtensible;
            if (ext != null)
            {
                isFloat = ext.SubFormat == SubtypeFloat;
                isPcm = ext.SubFormat == SubtypePcm;
            }
            _isFloat = isFloat && f.BitsPerSample == 32;
            IsSupported = _isFloat || (isPcm && f.BitsPerSample == 16);
        }

        public WaveFormat WaveFormat { get { return _source.WaveFormat; } }

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            int hz = IsSupported ? _getFrequencyHz() : 0;
            if (hz <= 0)
            {
                _phase = 0;
                return read;
            }

            int bytesPerSample = _isFloat ? 4 : 2;
            int frameBytes = bytesPerSample * _channels;
            int bytes = read - (read % frameBytes);
            int frames = bytes / frameBytes;
            double phaseStep = 2 * Math.PI * hz / _sampleRate;

            if (_isFloat)
            {
                int samples = bytes / bytesPerSample;
                if (_floatScratch.Length < samples) _floatScratch = new float[samples];
                Buffer.BlockCopy(buffer, offset, _floatScratch, 0, bytes);
                for (int frame = 0; frame < frames; frame++)
                {
                    float tone = (float)(Amplitude * Math.Sin(_phase));
                    _phase = (_phase + phaseStep) % (2 * Math.PI);
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        int i = frame * _channels + ch;
                        _floatScratch[i] = Math.Max(-1f, Math.Min(1f, _floatScratch[i] + tone));
                    }
                }
                Buffer.BlockCopy(_floatScratch, 0, buffer, offset, bytes);
            }
            else
            {
                int samples = bytes / bytesPerSample;
                if (_shortScratch.Length < samples) _shortScratch = new short[samples];
                Buffer.BlockCopy(buffer, offset, _shortScratch, 0, bytes);
                for (int frame = 0; frame < frames; frame++)
                {
                    double tone = Amplitude * short.MaxValue * Math.Sin(_phase);
                    _phase = (_phase + phaseStep) % (2 * Math.PI);
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        int i = frame * _channels + ch;
                        double mixed = _shortScratch[i] + tone;
                        _shortScratch[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(mixed)));
                    }
                }
                Buffer.BlockCopy(_shortScratch, 0, buffer, offset, bytes);
            }
            return read;
        }
    }

    // Settings of one 3-band equalizer plus its high cut. Immutable: the UI makes a new instance
    // for each change and swaps the reference, so the playback thread always sees a consistent set.
    //
    // The user sets points (frequency, gain). The constructor turns them into filters so the
    // total response passes through every point:
    //  - High cut: a fixed-shape Butterworth low-pass with its corner at the highest point
    //    (at MaxHz when the EQ is off).
    //  - Width: each peaking band is as wide as the distance (in octaves) to its nearest
    //    neighbour point, so bands overlap less when points are close.
    //  - Gain: the bands overlap, so each band's own gain is solved (iteratively) so that the
    //    sum of all bands plus the high cut equals the point's gain at every point.
    public sealed class EqSettings
    {
        public const int Bands = 3;
        public const int MinHz = 10;
        public const int MaxHz = 250;
        public const double MinDb = -24.0;
        public const double MaxDb = 12.0;
        public const double MinBwOct = 0.25, MaxBwOct = 2.5; // band width limits, octaves
        public const double FilterDbLimit = 36.0;             // limit for solved band gains
        public const double PlotSampleRate = 48000.0;

        public readonly bool On;
        public readonly int CutoffHz;          // high-cut corner frequency
        private readonly int[] _freqHz;
        private readonly double[] _gainDb;     // point gains (what the user set)
        private readonly double[] _q;          // solved band widths
        private readonly double[] _filterDb;   // solved band gains

        public static readonly EqSettings Default =
            new EqSettings(false, new[] { 20, 50, 125 }, new double[Bands]);

        public EqSettings(bool on, int[] freqHz, double[] gainDb)
        {
            On = on;
            _freqHz = new int[Bands];
            _gainDb = new double[Bands];
            _q = new double[Bands];
            _filterDb = new double[Bands];
            for (int i = 0; i < Bands; i++)
            {
                _freqHz[i] = ClampHz(freqHz[i]);
                _gainDb[i] = ClampDb(gainDb[i]);
            }
            int top = 0;
            for (int i = 0; i < Bands; i++) top = Math.Max(top, _freqHz[i]);
            CutoffHz = on ? top : MaxHz;
            if (on) Solve();
        }

        public int FreqHz(int band) { return _freqHz[band]; }
        public double GainDb(int band) { return _gainDb[band]; }
        public double FilterQ(int band) { return _q[band]; }
        public double FilterDb(int band) { return _filterDb[band]; }

        // True when no peaking band needs to run (the high cut still runs).
        public bool IsFlat
        {
            get
            {
                if (!On) return true;
                for (int i = 0; i < Bands; i++) if (Math.Abs(_filterDb[i]) > 1e-6) return false;
                return true;
            }
        }

        private void Solve()
        {
            for (int i = 0; i < Bands; i++)
            {
                double nearest = double.MaxValue;
                for (int j = 0; j < Bands; j++)
                    if (j != i) nearest = Math.Min(nearest, Math.Abs(Math.Log((double)_freqHz[i] / _freqHz[j], 2)));
                double bw = Math.Max(MinBwOct, Math.Min(MaxBwOct, nearest));
                double p = Math.Pow(2, bw);
                _q[i] = Math.Sqrt(p) / (p - 1);
            }

            // Damped least squares (Levenberg-Marquardt) on the band gains, with a numeric
            // Jacobian. It meets every point when that is possible; when it is not (two points
            // at one frequency with different gains) it finds the closest fit instead of
            // running away. Band gains are limited to +/-FilterDbLimit.
            var g = (double[])_gainDb.Clone();
            double[] err = Errors(g);
            double cost = SumSq(err);
            double lambda = 1e-3;
            for (int iter = 0; iter < 50 && cost > 1e-6; iter++)
            {
                var jac = new double[Bands, Bands];
                for (int j = 0; j < Bands; j++)
                {
                    var gj = (double[])g.Clone();
                    gj[j] += 0.01;
                    var ej = Errors(gj);
                    for (int i = 0; i < Bands; i++) jac[i, j] = (err[i] - ej[i]) / 0.01; // d(response)/d(gain)
                }
                // (J'J + lambda*diag) step = J'err
                var a = new double[Bands, Bands];
                var rhs = new double[Bands];
                for (int r = 0; r < Bands; r++)
                {
                    for (int c = 0; c < Bands; c++)
                    {
                        double s = 0;
                        for (int i = 0; i < Bands; i++) s += jac[i, r] * jac[i, c];
                        a[r, c] = s;
                    }
                    a[r, r] += lambda * (1 + a[r, r]);
                    double v = 0;
                    for (int i = 0; i < Bands; i++) v += jac[i, r] * err[i];
                    rhs[r] = v;
                }
                var step = SolveLinear(a, rhs);
                var next = new double[Bands];
                for (int i = 0; i < Bands; i++)
                    next[i] = Math.Max(-FilterDbLimit, Math.Min(FilterDbLimit, g[i] + (step != null ? step[i] : 0)));
                var nextErr = Errors(next);
                double nextCost = SumSq(nextErr);
                if (step != null && nextCost < cost)
                {
                    g = next; err = nextErr; cost = nextCost;
                    lambda = Math.Max(1e-7, lambda * 0.3);
                }
                else
                {
                    lambda *= 10;
                    if (lambda > 1e6) break;
                }
            }
            Array.Copy(g, _filterDb, Bands);
        }

        private double[] Errors(double[] bandDb)
        {
            var e = new double[Bands];
            for (int i = 0; i < Bands; i++) e[i] = _gainDb[i] - TotalDb(_freqHz[i], bandDb, PlotSampleRate);
            return e;
        }

        private static double SumSq(double[] v)
        {
            double s = 0;
            foreach (var x in v) s += x * x;
            return s;
        }

        // Gaussian elimination with partial pivoting; null when the matrix is singular.
        private static double[] SolveLinear(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone();
            var x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
                if (Math.Abs(m[piv, c]) < 1e-12) return null;
                if (piv != c)
                {
                    for (int k = 0; k < n; k++) { double t = m[c, k]; m[c, k] = m[piv, k]; m[piv, k] = t; }
                    double tb = x[c]; x[c] = x[piv]; x[piv] = tb;
                }
                for (int r = c + 1; r < n; r++)
                {
                    double f = m[r, c] / m[c, c];
                    for (int k = c; k < n; k++) m[r, k] -= f * m[c, k];
                    x[r] -= f * x[c];
                }
            }
            for (int r = n - 1; r >= 0; r--)
            {
                double s = x[r];
                for (int k = r + 1; k < n; k++) s -= m[r, k] * x[k];
                x[r] = s / m[r, r];
            }
            return x;
        }

        public EqSettings WithOn(bool on) { return new EqSettings(on, _freqHz, _gainDb); }

        public EqSettings WithBand(int band, int hz, double db)
        {
            var f = (int[])_freqHz.Clone();
            var g = (double[])_gainDb.Clone();
            f[band] = hz;
            g[band] = db;
            return new EqSettings(On, f, g);
        }

        public EqSettings Flat() { return new EqSettings(On, _freqHz, new double[Bands]); }

        public static int ClampHz(int hz) { return Math.Max(MinHz, Math.Min(MaxHz, hz)); }

        // Gains snap to 0.5 dB.
        public static double ClampDb(double db)
        {
            if (double.IsNaN(db)) return 0;
            return Math.Round(Math.Max(MinDb, Math.Min(MaxDb, db)) * 2) / 2.0;
        }

        // RBJ Audio EQ Cookbook peaking filter: b0, b1, b2, a1, a2 (normalized by a0).
        public static double[] Peaking(double hz, double db, double q, double sampleRate)
        {
            double f = Math.Max(5.0, Math.Min(hz, sampleRate * 0.45));
            double a = Math.Pow(10, db / 40);
            double w0 = 2 * Math.PI * f / sampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * q);
            double a0 = 1 + alpha / a;
            return new[] { (1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0 };
        }

        // RBJ Audio EQ Cookbook low-pass, Q = 1/sqrt(2) (Butterworth); same as LowPassWaveProvider.
        public static double[] LowPass(double hz, double sampleRate)
        {
            double f = Math.Max(10.0, Math.Min(hz, sampleRate * 0.45));
            double w0 = 2 * Math.PI * f / sampleRate;
            double cos = Math.Cos(w0);
            double alpha = Math.Sin(w0) / (2 * 0.7071067811865476);
            double a0 = 1 + alpha;
            return new[] { (1 - cos) / 2 / a0, (1 - cos) / a0, (1 - cos) / 2 / a0, -2 * cos / a0, (1 - alpha) / a0 };
        }

        private static double BiquadDb(double[] k, double hz, double sampleRate)
        {
            double w = 2 * Math.PI * hz / sampleRate;
            double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
            double nr = k[0] + k[1] * c1 + k[2] * c2, ni = -(k[1] * s1 + k[2] * s2);
            double dr = 1 + k[3] * c1 + k[4] * c2, di = -(k[3] * s1 + k[4] * s2);
            return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di) + 1e-30);
        }

        private double TotalDb(double hz, double[] bandDb, double sampleRate)
        {
            double sum = BiquadDb(LowPass(CutoffHz, sampleRate), hz, sampleRate);
            if (!On) return sum;
            for (int b = 0; b < Bands; b++)
                if (Math.Abs(bandDb[b]) > 1e-6) sum += BiquadDb(Peaking(_freqHz[b], bandDb[b], _q[b], sampleRate), hz, sampleRate);
            return sum;
        }

        // Total gain at one frequency (all bands plus the high cut), in dB.
        public double ResponseDb(double hz, double sampleRate)
        {
            return TotalDb(hz, _filterDb, sampleRate);
        }

        public string Describe()
        {
            if (!On) return "off (high cut " + CutoffHz + " Hz)";
            var parts = new string[Bands];
            for (int i = 0; i < Bands; i++)
                parts[i] = string.Format(CultureInfo.InvariantCulture, "{0}Hz/{1:+0.0;-0.0;0.0}dB", _freqHz[i], _gainDb[i]);
            return string.Join(" ", parts) + " (high cut " + CutoffHz + " Hz)";
        }
    }

    // Applies the 3-band peaking equalizer to each audio channel. The settings come from getEq
    // on every Read; when the reference changes the coefficients are recomputed. Off or flat
    // passes the audio through unchanged. Supports 32-bit float and 16-bit PCM.
    public sealed class EqWaveProvider : IWaveProvider
    {
        private static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

        private readonly IWaveProvider _source;
        private readonly Func<EqSettings> _getEq;
        private readonly int _channels;
        private readonly double _sampleRate;
        private readonly bool _isFloat;
        public readonly bool IsSupported;

        private EqSettings _applied;
        private readonly double[][] _coeffs = new double[EqSettings.Bands][];
        private readonly int[] _active = new int[EqSettings.Bands];
        private readonly bool[] _wasActive = new bool[EqSettings.Bands];
        private int _activeCount;
        private readonly double[,] _x1, _x2, _y1, _y2; // [band, channel]
        private float[] _floatScratch = new float[0];
        private short[] _shortScratch = new short[0];

        public EqWaveProvider(IWaveProvider source, Func<EqSettings> getEq)
        {
            _source = source;
            _getEq = getEq;
            var f = source.WaveFormat;
            _channels = Math.Max(1, f.Channels);
            _sampleRate = f.SampleRate;

            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
            bool isPcm = f.Encoding == WaveFormatEncoding.Pcm;
            var ext = f as WaveFormatExtensible;
            if (ext != null)
            {
                isFloat = ext.SubFormat == SubtypeFloat;
                isPcm = ext.SubFormat == SubtypePcm;
            }
            _isFloat = isFloat && f.BitsPerSample == 32;
            IsSupported = _isFloat || (isPcm && f.BitsPerSample == 16);

            int n = EqSettings.Bands;
            _x1 = new double[n, _channels]; _x2 = new double[n, _channels];
            _y1 = new double[n, _channels]; _y2 = new double[n, _channels];
        }

        public WaveFormat WaveFormat { get { return _source.WaveFormat; } }

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            var eq = IsSupported ? _getEq() : null;
            if (eq == null || !eq.On || eq.IsFlat)
            {
                _applied = null;
                for (int b = 0; b < EqSettings.Bands; b++) _wasActive[b] = false;
                return read;
            }
            if (!ReferenceEquals(eq, _applied)) Configure(eq);

            int bytesPerSample = _isFloat ? 4 : 2;
            int frameBytes = bytesPerSample * _channels;
            int bytes = read - (read % frameBytes);
            int samples = bytes / bytesPerSample;

            if (_isFloat)
            {
                if (_floatScratch.Length < samples) _floatScratch = new float[samples];
                Buffer.BlockCopy(buffer, offset, _floatScratch, 0, bytes);
                for (int i = 0; i < samples; i++)
                {
                    double y = Process(i % _channels, _floatScratch[i]);
                    _floatScratch[i] = (float)Math.Max(-1.0, Math.Min(1.0, y));
                }
                Buffer.BlockCopy(_floatScratch, 0, buffer, offset, bytes);
            }
            else
            {
                if (_shortScratch.Length < samples) _shortScratch = new short[samples];
                Buffer.BlockCopy(buffer, offset, _shortScratch, 0, bytes);
                for (int i = 0; i < samples; i++)
                {
                    double y = Process(i % _channels, _shortScratch[i]);
                    _shortScratch[i] = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(y)));
                }
                Buffer.BlockCopy(_shortScratch, 0, buffer, offset, bytes);
            }
            return read;
        }

        private void Configure(EqSettings eq)
        {
            _activeCount = 0;
            for (int b = 0; b < EqSettings.Bands; b++)
            {
                bool active = Math.Abs(eq.FilterDb(b)) > 1e-6;
                if (active)
                {
                    // A band that was off has stale state; clear it so it starts clean.
                    if (!_wasActive[b]) for (int ch = 0; ch < _channels; ch++) { _x1[b, ch] = _x2[b, ch] = _y1[b, ch] = _y2[b, ch] = 0; }
                    _coeffs[b] = EqSettings.Peaking(eq.FreqHz(b), eq.FilterDb(b), eq.FilterQ(b), _sampleRate);
                    _active[_activeCount++] = b;
                }
                _wasActive[b] = active;
            }
            _applied = eq;
        }

        private double Process(int ch, double x)
        {
            for (int k = 0; k < _activeCount; k++)
            {
                int b = _active[k];
                var c = _coeffs[b];
                double y = c[0] * x + c[1] * _x1[b, ch] + c[2] * _x2[b, ch] - c[3] * _y1[b, ch] - c[4] * _y2[b, ch];
                if (Math.Abs(y) < 1e-20) y = 0; // flush denormals in silence
                _x2[b, ch] = _x1[b, ch]; _x1[b, ch] = x;
                _y2[b, ch] = _y1[b, ch]; _y1[b, ch] = y;
                x = y;
            }
            return x;
        }
    }

    // Frequency-response plot of an EqSettings with one draggable point per band:
    // horizontal = frequency (log, 10..250 Hz), vertical = gain (-24..+12 dB).
    // Double-click a point to set its gain to 0 dB.
    public sealed class EqPlot : Control
    {
        public Func<EqSettings> GetEq;
        public Action<int, int, double> BandMoved; // band, Hz, dB
        public Action<int> BandReset;

        private const int PointRadius = 7;
        private const int PadLeft = 34, PadTop = 8, PadRight = 10, PadBottom = 20;
        private static readonly Color PointColor = Color.FromArgb(59, 130, 246);
        private static readonly Color ActiveColor = Color.FromArgb(234, 88, 12);
        private readonly Font _small = new Font("Segoe UI", 7.5F);
        private int _drag = -1, _hover = -1;

        public EqPlot()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Color.White;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _small.Dispose();
            base.Dispose(disposing);
        }

        private Rectangle Area { get { return new Rectangle(PadLeft, PadTop, Math.Max(10, Width - PadLeft - PadRight), Math.Max(10, Height - PadTop - PadBottom)); } }

        private float XOf(double hz)
        {
            var a = Area;
            return a.Left + (float)(a.Width * Math.Log(hz / EqSettings.MinHz) / Math.Log((double)EqSettings.MaxHz / EqSettings.MinHz));
        }

        private double HzAt(int x)
        {
            var a = Area;
            double t = Math.Max(0, Math.Min(1, (x - a.Left) / (double)a.Width));
            return EqSettings.MinHz * Math.Pow((double)EqSettings.MaxHz / EqSettings.MinHz, t);
        }

        private float YOf(double db)
        {
            var a = Area;
            return a.Top + (float)(a.Height * (EqSettings.MaxDb - db) / (EqSettings.MaxDb - EqSettings.MinDb));
        }

        private double DbAt(int y)
        {
            var a = Area;
            return EqSettings.MaxDb - (y - a.Top) / (double)a.Height * (EqSettings.MaxDb - EqSettings.MinDb);
        }

        private int HitTest(Point p)
        {
            var eq = GetEq != null ? GetEq() : null;
            if (eq == null) return -1;
            int best = -1;
            double bestDist = PointRadius + 4;
            for (int b = 0; b < EqSettings.Bands; b++)
            {
                double dx = p.X - XOf(eq.FreqHz(b)), dy = p.Y - YOf(eq.GainDb(b));
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d <= bestDist) { bestDist = d; best = b; }
            }
            return best;
        }

        private static string HzLabel(double hz)
        {
            return hz < 1000 ? ((int)hz).ToString(CultureInfo.InvariantCulture) : (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var a = Area;
            var eq = GetEq != null ? GetEq() : null;
            bool on = Enabled && eq != null && eq.On;
            var text = on ? SystemColors.ControlText : SystemColors.GrayText;

            using (var grid = new Pen(Color.FromArgb(225, 228, 232)))
            using (var zero = new Pen(Color.FromArgb(160, 166, 175)))
            using (var textBrush = new SolidBrush(text))
            {
                foreach (int hz in new[] { 10, 15, 20, 30, 50, 70, 100, 150, 200, 250 })
                {
                    float x = XOf(hz);
                    g.DrawLine(grid, x, a.Top, x, a.Bottom);
                    var label = HzLabel(hz);
                    var size = g.MeasureString(label, _small);
                    float lx = Math.Max(a.Left - 2, Math.Min(a.Right - size.Width + 2, x - size.Width / 2));
                    g.DrawString(label, _small, textBrush, lx, a.Bottom + 3);
                }
                for (double db = EqSettings.MinDb; db <= EqSettings.MaxDb; db += 6)
                {
                    float y = YOf(db);
                    g.DrawLine(db == 0 ? zero : grid, a.Left, y, a.Right, y);
                    var label = db.ToString("+0;-0;0", CultureInfo.InvariantCulture);
                    var size = g.MeasureString(label, _small);
                    g.DrawString(label, _small, textBrush, a.Left - size.Width - 2, y - size.Height / 2);
                }
                g.DrawRectangle(zero, a);
            }
            if (eq == null) return;

            // Response curve, one point per pixel column, clipped to the plot area.
            var pts = new PointF[a.Width + 1];
            for (int i = 0; i <= a.Width; i++)
            {
                double hz = HzAt(a.Left + i);
                double db = Math.Max(EqSettings.MinDb - 3, Math.Min(EqSettings.MaxDb + 3, eq.ResponseDb(hz, EqSettings.PlotSampleRate)));
                pts[i] = new PointF(a.Left + i, YOf(db));
            }
            var clip = g.Clip;
            g.SetClip(a);
            using (var curve = new Pen(on ? PointColor : SystemColors.GrayText, 2f))
                g.DrawLines(curve, pts);
            g.Clip = clip;

            for (int b = 0; b < EqSettings.Bands; b++)
            {
                float x = XOf(eq.FreqHz(b)), y = YOf(eq.GainDb(b));
                var color = !on ? SystemColors.GrayText : (b == _drag || b == _hover ? ActiveColor : PointColor);
                using (var fill = new SolidBrush(color))
                    g.FillEllipse(fill, x - PointRadius, y - PointRadius, 2 * PointRadius, 2 * PointRadius);
                var num = (b + 1).ToString(CultureInfo.InvariantCulture);
                var ns = g.MeasureString(num, _small);
                g.DrawString(num, _small, Brushes.White, x - ns.Width / 2 + 0.5f, y - ns.Height / 2);
            }

            if (_drag >= 0)
            {
                string tip = string.Format(CultureInfo.InvariantCulture, "{0}: {1} Hz  {2:+0.0;-0.0;0.0} dB",
                    _drag + 1, eq.FreqHz(_drag), eq.GainDb(_drag));
                var ts = g.MeasureString(tip, Font);
                float tx = Math.Max(a.Left + 2, Math.Min(a.Right - ts.Width - 2, XOf(eq.FreqHz(_drag)) - ts.Width / 2));
                float ty = YOf(eq.GainDb(_drag)) - PointRadius - ts.Height - 4;
                if (ty < a.Top + 2) ty = YOf(eq.GainDb(_drag)) + PointRadius + 4;
                using (var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    g.FillRectangle(bg, tx - 3, ty - 1, ts.Width + 6, ts.Height + 2);
                g.DrawString(tip, Font, SystemBrushes.ControlText, tx, ty);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _drag = HitTest(e.Location);
            if (_drag >= 0) { Capture = true; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_drag >= 0)
            {
                if (BandMoved != null)
                    BandMoved(_drag, EqSettings.ClampHz((int)Math.Round(HzAt(e.X))), EqSettings.ClampDb(DbAt(e.Y)));
                return;
            }
            int hover = HitTest(e.Location);
            if (hover != _hover)
            {
                _hover = hover;
                Cursor = hover >= 0 ? Cursors.SizeAll : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_drag >= 0) { _drag = -1; Capture = false; Invalidate(); }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _drag >= 0) { _drag = -1; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover >= 0 && _drag < 0) { _hover = -1; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int b = HitTest(e.Location);
            if (b >= 0 && BandReset != null) BandReset(b);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            _drag = _hover = -1;
            Invalidate();
        }
    }

    static class Program
    {
        // Settings & State
        // "" = not selected. "default" only when the user explicitly picks System Default:
        // a saved device that is missing is waited for, never replaced by the default device.
        private static string _selectedInputId = "";
        private static string _selectedOut1Id = "";
        private static string _selectedOut2Id = "";
        private static volatile bool _isRunning = false;   // capture pipeline is currently up
        private static volatile bool _wantRunning = false; // user wants duplication on; drives auto-reconnect
        private static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        // One playback path. The Provider lives as long as the capture; Play/Resampler are
        // recreated on each reconnect so one output can recover without touching the other.
        private sealed class OutputSlot
        {
            public readonly string Name;
            public OutputSlot(string name) { Name = name; }

            public BufferedWaveProvider Provider;
            public WasapiOut Play;
            public MediaFoundationResampler Resampler;
            public volatile bool Live;    // playing; capture feeds Provider only while true
            public volatile bool Faulted; // playback thread stopped with an exception
            public long PruneCount;       // times the buffer was force-cleared
            public string LastError;      // last reported open failure, so retries log only on change

            // Written by the UI thread, read by the playback thread.
            public volatile bool ToneOn;
            public volatile int ToneFrequencyHz = 100;
            public int EffectiveToneHz() { return ToneOn ? ToneFrequencyHz : 0; }

            // Replaced as a whole by the UI thread (EqSettings is immutable).
            public volatile EqSettings Eq = EqSettings.Default;
        }

        private const int ToneMinHz = 10;
        private const int ToneMaxHz = 250;
        private static string _captureDeviceId; // ID of the device the running capture uses; guarded by _audioLock
        private static Form _filterForm;
        private static System.Windows.Forms.Timer _saveTimer;

        // Audio Objects
        private static WasapiCapture _capture;
        private static volatile bool _captureFaulted = false;
        private static readonly OutputSlot _out1 = new OutputSlot("Output 1");
        private static readonly OutputSlot _out2 = new OutputSlot("Output 2");
        private const int ReconnectPollMillis = 5000; // some devices take a while to appear after boot
        private static string _lastStartError;        // so failed start retries log only on change
        private static bool _inputWaitLogged;

        private static NotifyIcon _sysTrayIcon;
        private static ContextMenu _sysTrayMenu;
        private static readonly object _audioLock = new object();

        // Diagnostics / logging
        private static readonly object _logLock = new object();
        private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        private static long _captureBytesSinceTick = 0;   // bytes delivered by capture since last stats tick
        private static int _captureCallbacksSinceTick = 0; // number of DataAvailable callbacks since last stats tick

        private static void Log(string msg)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}", DateTime.Now, msg);
            Console.WriteLine(line);
            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
            }
            catch { }
        }

        private static string DescribeFormat(WaveFormat f)
        {
            if (f == null) return "(null)";
            return string.Format(CultureInfo.InvariantCulture, "{0}Hz {1}ch {2}bit {3}", f.SampleRate, f.Channels, f.BitsPerSample, f.Encoding);
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Startup MediaFoundation for resampling
            try
            {
                MediaFoundationApi.Startup();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to initialize Media Foundation: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Load settings
            LoadSettings();

            // Run diagnostic stats logging loop
            Task.Run(() => StatsLoop());

            // Initialize System Tray Icon (the sole control surface)
            try
            {
                SetupSystemTray();
            }
            catch (Exception ex)
            {
                Log("System tray icon could not be created (headless session?): " + ex.Message);
            }

            // If all devices are saved, ask for duplication. ReconnectLoop opens the devices now and
            // keeps trying every few seconds, because after boot some devices appear late.
            lock (_audioLock)
            {
                if (!string.IsNullOrEmpty(_selectedInputId) && !string.IsNullOrEmpty(_selectedOut1Id) && !string.IsNullOrEmpty(_selectedOut2Id))
                {
                    _wantRunning = true;
                    Log("Startup: connecting to the saved devices (retry every " + (ReconnectPollMillis / 1000) + " s).");
                }
                else
                {
                    Log("Startup: input, Output 1, and Output 2 are not all selected. Waiting for a selection in the tray menu.");
                }
            }

            // Open the saved devices, and reopen lost devices when they return
            new Thread(ReconnectLoop) { IsBackground = true, Name = "Reconnect" }.Start();

            // Run Windows Message Loop (keeps app alive in tray) or wait on event in headless mode
            if (_sysTrayIcon != null)
            {
                Application.Run();
            }
            else
            {
                Log("Running in headless mode (no tray). Service is active. Ctrl+C to exit.");
                var exitEvent = new ManualResetEvent(false);
                exitEvent.WaitOne();
            }

            // Clean up on exit
            Shutdown();
        }

        #region System Tray UI

        private static void SetupSystemTray()
        {
            _sysTrayMenu = new ContextMenu();

            var inputMenu = new MenuItem("Input Device");
            var out1Menu = new MenuItem("Output 1");
            var out2Menu = new MenuItem("Output 2");
            _sysTrayMenu.MenuItems.Add(inputMenu);
            _sysTrayMenu.MenuItems.Add(out1Menu);
            _sysTrayMenu.MenuItems.Add(out2Menu);
            _sysTrayMenu.MenuItems.Add("Sine Tone and Equalizer...", (s, e) => ShowFilterWindow());

            _sysTrayMenu.MenuItems.Add("-");

            var toggleItem = new MenuItem("Start Duplication", (s, e) => ToggleDuplicationFromTray());
            _sysTrayMenu.MenuItems.Add(toggleItem);

            _sysTrayMenu.MenuItems.Add("-");
            _sysTrayMenu.MenuItems.Add("Exit", (s, e) => Application.Exit());

            // Rebuild dynamic content every time the menu opens so device lists and
            // checkmarks reflect the current state.
            _sysTrayMenu.Popup += (s, e) =>
            {
                toggleItem.Text = _wantRunning ? "Stop Duplication" : "Start Duplication";
                toggleItem.Checked = _wantRunning;

                // An output that is also the input makes a feedback loop, so those choices are disabled.
                string inputId = ResolveInputDeviceId();
                PopulateDeviceMenu(inputMenu, "Input Device", 0, _selectedInputId, new[] { _selectedOut1Id, _selectedOut2Id });
                PopulateDeviceMenu(out1Menu, "Output 1", 1, _selectedOut1Id, new[] { inputId });
                PopulateDeviceMenu(out2Menu, "Output 2", 2, _selectedOut2Id, new[] { inputId });
            };

            // Generate dynamic tray icon (Blue/white double circle logo)
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                using (var brush = new SolidBrush(Color.FromArgb(59, 130, 246))) // Premium Blue
                {
                    g.FillEllipse(brush, 1, 1, 14, 14);
                }
                using (var brush = new SolidBrush(Color.White))
                {
                    g.FillEllipse(brush, 4, 4, 8, 8);
                }
            }

            _sysTrayIcon = new NotifyIcon
            {
                Icon = Icon.FromHandle(bmp.GetHicon()),
                ContextMenu = _sysTrayMenu,
                Text = "Audio Duplicator Service",
                Visible = true
            };

            // Left double-click toggles duplication for convenience.
            _sysTrayIcon.DoubleClick += (s, e) => ToggleDuplicationFromTray();
        }

        // Rebuilds a device submenu with the active render endpoints. slot: 0=input, 1=out1, 2=out2.
        // The input submenu additionally offers "System Default Render Loopback" (value "default").
        // Devices in blockedIds are shown disabled, because selecting them makes a feedback loop.
        private static void PopulateDeviceMenu(MenuItem parent, string baseLabel, int slot, string selectedId, string[] blockedIds)
        {
            parent.MenuItems.Clear();
            string selectedName = null;
            bool selectedBlocked = false;
            string blockedNote = slot == 0 ? "  (used as an output)" : "  (same as input)";

            if (slot == 0)
            {
                bool isDefault = selectedId == "default";
                bool defBlocked = IsBlocked(DefaultRenderDeviceId(), blockedIds);
                var def = new MenuItem("System Default Render Loopback" + (defBlocked ? blockedNote : ""), (s, e) => OnDeviceSelected(0, "default"));
                def.RadioCheck = true;
                def.Checked = isDefault;
                def.Enabled = !defBlocked;
                parent.MenuItems.Add(def);
                if (isDefault) { selectedName = "System Default"; selectedBlocked = defBlocked; }
            }

            try
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int i = 0; i < devices.Count; i++)
                {
                    var d = devices[i];
                    string id = d.ID;            // captured per-iteration by the click handler
                    string name = d.FriendlyName;
                    bool blocked = IsBlocked(id, blockedIds);
                    var item = new MenuItem(name + (blocked ? blockedNote : ""), (s, e) => OnDeviceSelected(slot, id));
                    item.RadioCheck = true;
                    item.Checked = (selectedId == id);
                    item.Enabled = !blocked;
                    if (item.Checked) { selectedName = name; selectedBlocked = blocked; }
                    parent.MenuItems.Add(item);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to enumerate render devices for menu: " + ex.Message);
                parent.MenuItems.Add(new MenuItem("(error listing devices)") { Enabled = false });
            }

            parent.Text = selectedName != null ? baseLabel + ": " + selectedName : baseLabel;
            if (selectedBlocked && slot != 0) parent.Text += "  (blocked: same as input)";
        }

        private static bool IsBlocked(string id, string[] blockedIds)
        {
            return !string.IsNullOrEmpty(id) && Array.IndexOf(blockedIds, id) >= 0;
        }

        private static string DefaultRenderDeviceId()
        {
            try
            {
                return new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch
            {
                return null; // no default render endpoint exists right now
            }
        }

        // The device ID the input selection points to now ("default" is resolved). Null if unknown.
        private static string ResolveInputDeviceId()
        {
            if (_selectedInputId == "default") return DefaultRenderDeviceId();
            return string.IsNullOrEmpty(_selectedInputId) ? null : _selectedInputId;
        }

        #region Sine Tone and Equalizer Window

        private static string FormatHz(int hz)
        {
            return hz < 1000
                ? string.Format(CultureInfo.InvariantCulture, "{0} Hz", hz)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.0#} kHz", hz / 1000.0);
        }

        // The tone slider is logarithmic: 0..1000 maps to 10 Hz..250 Hz.
        private static int ToneSliderToHz(int value)
        {
            return (int)Math.Round(ToneMinHz * Math.Pow((double)ToneMaxHz / ToneMinHz, value / 1000.0));
        }

        private static int ToneHzToSlider(int hz)
        {
            hz = Math.Max(ToneMinHz, Math.Min(ToneMaxHz, hz));
            return (int)Math.Round(1000 * Math.Log((double)hz / ToneMinHz) / Math.Log((double)ToneMaxHz / ToneMinHz));
        }

        private static string DeviceName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(not selected)";
            try { return new MMDeviceEnumerator().GetDevice(id).FriendlyName; }
            catch { return "(device not found)"; }
        }

        private static void ShowFilterWindow()
        {
            if (_filterForm != null && !_filterForm.IsDisposed)
            {
                _filterForm.WindowState = FormWindowState.Normal;
                _filterForm.Activate();
                return;
            }

            var form = new Form
            {
                Text = "Sine Tone and Equalizer",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(472, 556),
                Font = new Font("Segoe UI", 9F)
            };

            var tabs = new TabControl { Location = new Point(8, 8), Size = new Size(456, 504) };
            tabs.TabPages.Add(BuildOutputPage(_out1, "Output 1", _selectedOut1Id));
            tabs.TabPages.Add(BuildOutputPage(_out2, "Output 2", _selectedOut2Id));
            form.Controls.Add(tabs);

            form.Controls.Add(new Label
            {
                Text = "Changes apply at once and are saved. Order: tone (20%) → EQ → high cut.\n" +
                       "Drag a point for frequency and gain; double-click it for 0 dB.",
                Location = new Point(12, 518),
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            });

            form.FormClosed += (s, e) =>
            {
                if (_saveTimer != null && _saveTimer.Enabled) { _saveTimer.Stop(); SaveSettings(); }
                _filterForm = null;
            };

            _filterForm = form;
            form.Show();
            form.Activate();
        }

        private static TabPage BuildOutputPage(OutputSlot slot, string label, string deviceId)
        {
            var page = new TabPage(label) { UseVisualStyleBackColor = true };
            AddToneGroup(page, slot, deviceId);
            AddEqGroup(page, slot);
            return page;
        }

        private static string FormatDb(double db)
        {
            return db.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";
        }

        private static void AddEqGroup(Control parent, OutputSlot slot)
        {
            var group = new GroupBox { Text = EqSettings.Bands + "-band equalizer", Location = new Point(4, 90), Size = new Size(438, 380) };
            var start = slot.Eq;
            var on = new CheckBox { Text = "Equalizer", Location = new Point(14, 22), AutoSize = true, Checked = start.On };
            var flat = new Button { Text = "Flat", Location = new Point(348, 18), Size = new Size(76, 26), Enabled = start.On };
            var plot = new EqPlot { Location = new Point(14, 50), Size = new Size(410, 298), GetEq = () => slot.Eq, Enabled = start.On };

            // One read-out per point under the plot (the points are set by dragging them).
            int colW = 410 / EqSettings.Bands;
            var labels = new Label[EqSettings.Bands];
            for (int i = 0; i < EqSettings.Bands; i++)
            {
                labels[i] = new Label
                {
                    Location = new Point(14 + i * colW, 352),
                    Size = new Size(colW, 20),
                    TextAlign = ContentAlignment.TopCenter,
                    Enabled = start.On
                };
                group.Controls.Add(labels[i]);
            }

            Action refresh = () =>
            {
                var eq = slot.Eq;
                for (int i = 0; i < EqSettings.Bands; i++)
                    labels[i].Text = (i + 1).ToString(CultureInfo.InvariantCulture) + ": " + FormatHz(eq.FreqHz(i)) + "  " + FormatDb(eq.GainDb(i));
                plot.Invalidate();
            };
            Action<EqSettings> apply = eq =>
            {
                slot.Eq = eq;
                refresh();
                ScheduleSave();
            };

            plot.BandMoved = (b, hz, db) => apply(slot.Eq.WithBand(b, hz, db));
            plot.BandReset = b => apply(slot.Eq.WithBand(b, slot.Eq.FreqHz(b), 0));
            on.CheckedChanged += (s, e) =>
            {
                bool v = on.Checked;
                plot.Enabled = v;
                flat.Enabled = v;
                for (int i = 0; i < EqSettings.Bands; i++) labels[i].Enabled = v;
                apply(slot.Eq.WithOn(v));
            };
            flat.Click += (s, e) => apply(slot.Eq.Flat());

            refresh();
            group.Controls.Add(on);
            group.Controls.Add(flat);
            group.Controls.Add(plot);
            parent.Controls.Add(group);
        }

        private static void AddToneGroup(Control parent, OutputSlot slot, string deviceId)
        {
            var group = new GroupBox
            {
                Text = "Device: " + DeviceName(deviceId),
                Location = new Point(4, 4),
                Size = new Size(438, 80)
            };

            var toneEnabled = new CheckBox { Text = "Sine tone", Location = new Point(14, 36), AutoSize = true, Checked = slot.ToneOn };
            var toneSlider = new TrackBar
            {
                Location = new Point(120, 24),
                Width = 170,
                Minimum = 0,
                Maximum = 1000,
                TickFrequency = 100,
                SmallChange = 10,
                LargeChange = 100,
                Value = ToneHzToSlider(slot.ToneFrequencyHz),
                Enabled = slot.ToneOn
            };
            var toneDown = new Button { Text = "−", Location = new Point(294, 30), Size = new Size(28, 26), Enabled = slot.ToneOn };
            var toneUp = new Button { Text = "+", Location = new Point(326, 30), Size = new Size(28, 26), Enabled = slot.ToneOn };
            var toneValue = new Label
            {
                Location = new Point(360, 36),
                Width = 70,
                Text = FormatHz(slot.ToneFrequencyHz),
                Enabled = slot.ToneOn
            };

            toneEnabled.CheckedChanged += (s, e) =>
            {
                slot.ToneOn = toneEnabled.Checked;
                toneSlider.Enabled = toneEnabled.Checked;
                toneDown.Enabled = toneEnabled.Checked;
                toneUp.Enabled = toneEnabled.Checked;
                toneValue.Enabled = toneEnabled.Checked;
                ScheduleSave();
            };
            // The log slider has steps of more than 1 Hz near the top, so the buttons set
            // the frequency directly and move the slider without letting it round the value.
            bool settingSlider = false;
            toneSlider.ValueChanged += (s, e) =>
            {
                if (settingSlider) return;
                int hz = ToneSliderToHz(toneSlider.Value);
                slot.ToneFrequencyHz = hz;
                toneValue.Text = FormatHz(hz);
                ScheduleSave();
            };
            Action<int> stepTone = delta =>
            {
                int hz = Math.Max(ToneMinHz, Math.Min(ToneMaxHz, slot.ToneFrequencyHz + delta));
                slot.ToneFrequencyHz = hz;
                toneValue.Text = FormatHz(hz);
                settingSlider = true;
                toneSlider.Value = ToneHzToSlider(hz);
                settingSlider = false;
                ScheduleSave();
            };
            // A mouse press steps once at once, then five times each second while the button is held.
            // Keyboard presses (Space/Enter) come in as Click without MouseEventArgs and step once;
            // a mouse Click is ignored because MouseDown already did the step.
            var repeatTimer = new System.Windows.Forms.Timer { Interval = 200 };
            int repeatDelta = 0;
            repeatTimer.Tick += (s, e) => stepTone(repeatDelta);
            Action stopRepeat = () => { repeatTimer.Stop(); repeatDelta = 0; };
            Action<Button, int> wireStepButton = (btn, delta) =>
            {
                btn.MouseDown += (s, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    repeatDelta = delta;
                    stepTone(delta);
                    repeatTimer.Stop();
                    repeatTimer.Start();
                };
                btn.MouseUp += (s, e) => stopRepeat();
                btn.MouseCaptureChanged += (s, e) => { if (!btn.Capture) stopRepeat(); };
                btn.EnabledChanged += (s, e) => { if (!btn.Enabled) stopRepeat(); };
                btn.Click += (s, e) => { if (!(e is MouseEventArgs)) stepTone(delta); };
            };
            wireStepButton(toneDown, -1);
            wireStepButton(toneUp, 1);
            group.Disposed += (s, e) => repeatTimer.Dispose();

            group.Controls.Add(toneEnabled);
            group.Controls.Add(toneSlider);
            group.Controls.Add(toneDown);
            group.Controls.Add(toneUp);
            group.Controls.Add(toneValue);
            parent.Controls.Add(group);
        }

        // Save after the slider stops moving for 500 ms, not on every step.
        private static void ScheduleSave()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _saveTimer.Tick += (s, e) =>
                {
                    _saveTimer.Stop();
                    SaveSettings();
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "Audio settings: Output 1 tone {0}, EQ {2}; Output 2 tone {1}, EQ {3}",
                        _out1.ToneOn ? FormatHz(_out1.ToneFrequencyHz) : "off",
                        _out2.ToneOn ? FormatHz(_out2.ToneFrequencyHz) : "off",
                        _out1.Eq.Describe(), _out2.Eq.Describe()));
                };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        #endregion

        private static void OnDeviceSelected(int slot, string id)
        {
            if (slot == 0) _selectedInputId = id;
            else if (slot == 1) _selectedOut1Id = id;
            else _selectedOut2Id = id;

            Log(string.Format(CultureInfo.InvariantCulture, "Device selection changed (slot {0}) -> {1}", slot, id));
            SaveSettings();

            // Apply immediately by restarting the pipeline if it is already running.
            lock (_audioLock)
            {
                if (_wantRunning)
                {
                    StopDuplicationInternal();
                    StartDuplicationInternal();
                }
            }
        }

        private static void ToggleDuplicationFromTray()
        {
            lock (_audioLock)
            {
                if (_wantRunning)
                {
                    _wantRunning = false;
                    StopDuplicationInternal();
                }
                else
                {
                    if (string.IsNullOrEmpty(_selectedInputId) || string.IsNullOrEmpty(_selectedOut1Id) || string.IsNullOrEmpty(_selectedOut2Id))
                    {
                        MessageBox.Show("Please select the Input Device, Output 1, and Output 2 from the tray menu first.", "Configuration Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    StartDuplicationInternal();
                }
            }
        }

        #endregion

        #region Diagnostics

        private static async Task StatsLoop()
        {
            while (true)
            {
                await Task.Delay(1000);
                if (!_isRunning) continue;

                try
                {
                    long capBytes = Interlocked.Exchange(ref _captureBytesSinceTick, 0);
                    int callbacks = Interlocked.Exchange(ref _captureCallbacksSinceTick, 0);

                    double capturedMs = 0;
                    int avgBps = (_capture != null && _capture.WaveFormat != null) ? _capture.WaveFormat.AverageBytesPerSecond : 0;
                    if (avgBps > 0) capturedMs = (double)capBytes / avgBps * 1000.0;

                    var provider1 = _out1.Provider;
                    var provider2 = _out2.Provider;
                    double buf1 = provider1 != null ? provider1.BufferedDuration.TotalMilliseconds : -1;
                    double buf2 = provider2 != null ? provider2.BufferedDuration.TotalMilliseconds : -1;

                    Log(string.Format(CultureInfo.InvariantCulture,
                        "STATS captured={0:F0}ms/s ({1} bytes, {2} cb) | buf1={3:F0}ms buf2={4:F0}ms | prunes1={5} prunes2={6} | play1={7} play2={8}",
                        capturedMs, capBytes, callbacks, buf1, buf2,
                        Interlocked.Read(ref _out1.PruneCount),
                        Interlocked.Read(ref _out2.PruneCount),
                        DescribeOutputState(_out1),
                        DescribeOutputState(_out2)));
                }
                catch (Exception ex)
                {
                    Log("STATS exception: " + ex.Message);
                }
            }
        }

        private static string DescribeOutputState(OutputSlot slot)
        {
            var play = slot.Play;
            if (!slot.Live || play == null) return "disconnected";
            return play.PlaybackState.ToString();
        }

        #endregion

        #region Audio Duplication Core

        // Caller must hold _audioLock. A missing output does not stop the pipeline: the other
        // output plays and ReconnectLoop opens the missing one when it appears.
        private static void StartDuplicationInternal(bool notifyOnFailure = true)
        {
            _wantRunning = true;
            try
            {
                var enumerator = new MMDeviceEnumerator();

                // 1. Resolve Input
                MMDevice inputDevice;
                if (_selectedInputId == "default")
                {
                    inputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                else if (string.IsNullOrEmpty(_selectedInputId))
                {
                    throw new Exception("No input device is selected.");
                }
                else
                {
                    inputDevice = enumerator.GetDevice(_selectedInputId);
                    if (inputDevice.State != DeviceState.Active)
                    {
                        throw new Exception("Input device is not available yet (" + inputDevice.State + ").");
                    }
                }

                if (string.IsNullOrEmpty(_selectedOut1Id) || string.IsNullOrEmpty(_selectedOut2Id))
                {
                    throw new Exception("Output devices are not fully configured.");
                }

                Log("=== Starting duplication ===");
                Log("Input device : " + inputDevice.FriendlyName);

                // 2. Initialize Capture with 40ms buffer
                var capture = new WasapiLoopbackCaptureEx(inputDevice, 40);
                _capture = capture;
                _captureDeviceId = inputDevice.ID;
                _captureFaulted = false;
                Log("Capture format: " + DescribeFormat(capture.WaveFormat) + " (avgBytes/s=" + capture.WaveFormat.AverageBytesPerSecond + ")");

                // Reset diagnostic counters for this run
                _captureBytesSinceTick = 0;
                _captureCallbacksSinceTick = 0;
                _out1.PruneCount = 0;
                _out2.PruneCount = 0;

                // 3. Initialize Buffers with Loopback Format and bound to 2s buffer duration for safety headroom
                _out1.Provider = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(2) };
                _out2.Provider = new BufferedWaveProvider(capture.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(2) };

                // 4. Hook capture events
                capture.DataAvailable += (s, e) =>
                {
                    // Track capture throughput regardless of byte count so we can detect a starved/silent source
                    Interlocked.Add(ref _captureBytesSinceTick, e.BytesRecorded);
                    Interlocked.Increment(ref _captureCallbacksSinceTick);

                    if (e.BytesRecorded > 0)
                    {
                        FeedOutput(_out1, e.Buffer, e.BytesRecorded);
                        FeedOutput(_out2, e.Buffer, e.BytesRecorded);
                    }
                };
                capture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception == null) return;
                    Log("CAPTURE STOPPED with exception: " + e.Exception.Message);
                    if (_capture == capture) _captureFaulted = true;
                };

                // 5. Start Playback & Capture
                StartOutput(_out1, _selectedOut1Id);
                StartOutput(_out2, _selectedOut2Id);
                capture.StartRecording();

                _isRunning = true;
                _lastStartError = null;
                Log("Duplication started successfully.");
            }
            catch (Exception ex)
            {
                StopDuplicationInternal();
                if (ex.Message != _lastStartError)
                {
                    _lastStartError = ex.Message;
                    Log("Failed to start audio duplication, will retry every " + (ReconnectPollMillis / 1000) + " s: " + ex);
                }
                if (notifyOnFailure && _sysTrayIcon != null)
                {
                    _sysTrayIcon.ShowBalloonTip(3000, "Audio Duplicator Error", "Failed to start audio duplication: " + ex.Message, ToolTipIcon.Error);
                }
            }
        }

        private static void FeedOutput(OutputSlot slot, byte[] buffer, int count)
        {
            var provider = slot.Provider;
            if (provider == null || !slot.Live) return;

            provider.AddSamples(buffer, 0, count);

            // Keep buffer size under control to minimize latency and prevent stutter
            PruneBuffer(slot.Name, provider, 300, ref slot.PruneCount);
        }

        // Caller must hold _audioLock. Opens one output in shared mode with 50ms target latency in
        // timer-driven mode. Returns false (and leaves the slot closed) if the device cannot open.
        private static bool StartOutput(OutputSlot slot, string deviceId)
        {
            try
            {
                // Playing into the captured device feeds the output back into the input.
                // The menu blocks this choice, but "System Default" input can change later.
                if (deviceId == _captureDeviceId)
                {
                    throw new InvalidOperationException("the device is the same as the input; skipped to prevent a feedback loop");
                }

                var enumerator = new MMDeviceEnumerator();
                if (!IsDeviceActive(enumerator, deviceId))
                {
                    throw new InvalidOperationException("the device is not available yet");
                }
                var device = enumerator.GetDevice(deviceId);
                var format = slot.Provider.WaveFormat;

                var player = new WasapiOut(device, AudioClientShareMode.Shared, false, 50);
                slot.Play = player;
                slot.Faulted = false;

                // Device removal manifests here. The check ignores late events from a closed player.
                player.PlaybackStopped += (s, e) =>
                {
                    if (e.Exception == null) return;
                    Log(slot.Name + " PLAYBACK STOPPED with exception: " + e.Exception.Message);
                    if (slot.Play == player) slot.Faulted = true;
                };

                var mixFormat = device.AudioClient.MixFormat;
                // Tone, EQ and filter keep the capture format, so the resampling decision does not change.
                var tone = new ToneMixWaveProvider(slot.Provider, slot.EffectiveToneHz);
                if (!tone.IsSupported) Log(slot.Name + ": sine tone not available for format " + DescribeFormat(format));
                var eq = new EqWaveProvider(tone, () => slot.Eq);
                if (!eq.IsSupported) Log(slot.Name + ": equalizer not available for format " + DescribeFormat(format));
                // Always-on high cut; its corner follows the highest EQ point (250 Hz when EQ is off).
                var filter = new LowPassWaveProvider(eq, () => slot.Eq.CutoffHz);
                if (!filter.IsSupported) Log(slot.Name + ": high-cut filter not available for format " + DescribeFormat(format));
                IWaveProvider finalProvider = filter;
                string conversion = "passthrough (no resample, device accepts capture format)";
                if (RequiresResampling(device, format))
                {
                    slot.Resampler = new MediaFoundationResampler(filter, mixFormat) { ResamplerQuality = 60 };
                    finalProvider = slot.Resampler;
                    conversion = "RESAMPLING enabled";
                }
                player.Init(finalProvider);

                slot.Provider.ClearBuffer();
                slot.Live = true;
                player.Play();

                slot.LastError = null;
                Log(slot.Name + " opened: " + device.FriendlyName + ", mix format " + DescribeFormat(mixFormat) + " -> " + conversion);
                return true;
            }
            catch (Exception ex)
            {
                StopOutput(slot);
                if (slot.LastError != ex.Message)
                {
                    slot.LastError = ex.Message;
                    Log(slot.Name + " could not open, will retry: " + ex.Message);
                }
                return false;
            }
        }

        // Caller must hold _audioLock. Keeps the Provider so the slot can reopen on the same capture.
        private static void StopOutput(OutputSlot slot)
        {
            slot.Live = false;
            var player = slot.Play;
            var resampler = slot.Resampler;
            slot.Play = null;
            slot.Resampler = null;
            slot.Faulted = false;

            try { if (player != null) player.Stop(); } catch { }
            try { if (player != null) player.Dispose(); } catch { }
            try { if (resampler != null) resampler.Dispose(); } catch { }
        }

        // Checks immediately, then every ReconnectPollMillis. The enumerator is made again after a
        // failure, because at boot the Windows audio service can be not ready when the app starts.
        private static void ReconnectLoop()
        {
            MMDeviceEnumerator enumerator = null;
            while (true)
            {
                try
                {
                    if (enumerator == null) enumerator = new MMDeviceEnumerator();
                    lock (_audioLock)
                    {
                        if (_wantRunning) CheckDevices(enumerator);
                    }
                }
                catch (Exception ex)
                {
                    Log("Reconnect check exception: " + ex.Message);
                    enumerator = null;
                }
                Thread.Sleep(ReconnectPollMillis);
            }
        }

        // Caller must hold _audioLock.
        private static void CheckDevices(MMDeviceEnumerator enumerator)
        {
            // The capture owns the buffers, so losing it restarts the whole pipeline.
            bool inputAvailable = IsInputAvailable(enumerator);
            if (_capture != null && !inputAvailable) _captureFaulted = true;
            if (_capture == null || _captureFaulted)
            {
                if (!inputAvailable)
                {
                    if (!_inputWaitLogged)
                    {
                        _inputWaitLogged = true;
                        Log("Waiting for the input device to appear: " + _selectedInputId);
                    }
                    return;
                }
                _inputWaitLogged = false;
                Log("Input device available. Starting duplication.");
                StopDuplicationInternal();
                StartDuplicationInternal(false);
                return;
            }

            CheckOutput(enumerator, _out1, _selectedOut1Id);
            CheckOutput(enumerator, _out2, _selectedOut2Id);
        }

        private static void CheckOutput(MMDeviceEnumerator enumerator, OutputSlot slot, string deviceId)
        {
            bool active = IsDeviceActive(enumerator, deviceId);
            if (slot.Live && (slot.Faulted || !active))
            {
                Log(slot.Name + " lost. Waiting for the device to return.");
                StopOutput(slot);
            }
            if (!slot.Live && active && StartOutput(slot, deviceId))
            {
                Log(slot.Name + " reconnected.");
            }
        }

        private static bool IsInputAvailable(MMDeviceEnumerator enumerator)
        {
            if (string.IsNullOrEmpty(_selectedInputId)) return false; // nothing selected: never use the default
            if (_selectedInputId != "default")
            {
                return IsDeviceActive(enumerator, _selectedInputId);
            }
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).State == DeviceState.Active;
            }
            catch
            {
                return false; // no default render endpoint exists right now
            }
        }

        private static bool IsDeviceActive(MMDeviceEnumerator enumerator, string deviceId)
        {
            try
            {
                return enumerator.GetDevice(deviceId).State == DeviceState.Active;
            }
            catch
            {
                return false; // the endpoint is not known to Windows right now
            }
        }

        private static bool CompareFormats(WaveFormat f1, WaveFormat f2)
        {
            return f1.SampleRate == f2.SampleRate &&
                   f1.Channels == f2.Channels &&
                   f1.Encoding == f2.Encoding &&
                   f1.BitsPerSample == f2.BitsPerSample;
        }

        // Decide whether a resampler is genuinely required between the capture stream and a target output.
        // A same rate/channel/bit "Extensible" mix format is typically just float wrapped in an extensible
        // header, so the encoding-label mismatch (IeeeFloat vs Extensible) does NOT mean conversion is needed.
        // Ask the device's AudioClient directly via IsFormatSupported before paying for a resampler, since an
        // unnecessary same-rate MediaFoundationResampler can under-consume the buffer and cause stutter.
        private static bool RequiresResampling(MMDevice outDevice, WaveFormat captureFormat)
        {
            var mixFormat = outDevice.AudioClient.MixFormat;
            if (CompareFormats(captureFormat, mixFormat))
            {
                return false;
            }
            try
            {
                if (outDevice.AudioClient.IsFormatSupported(AudioClientShareMode.Shared, captureFormat))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log("IsFormatSupported check failed for " + outDevice.FriendlyName + ": " + ex.Message + " -> falling back to resampler");
            }
            return true;
        }

        private static void PruneBuffer(string name, BufferedWaveProvider provider, int maxMillis, ref long pruneCounter)
        {
            try
            {
                if (provider == null) return;

                double ms = provider.BufferedDuration.TotalMilliseconds;
                if (ms > maxMillis)
                {
                    provider.ClearBuffer();
                    Interlocked.Increment(ref pruneCounter);
                    Log(string.Format(CultureInfo.InvariantCulture, "{0} latency exceeded ({1:F1}ms). Buffer cleared to catch up.", name, ms));
                }
            }
            catch (Exception ex)
            {
                Log("PruneBuffer exception: " + ex.Message);
            }
        }

        private static void StopDuplicationInternal()
        {
            bool wasOpen = _capture != null || _out1.Live || _out2.Live;
            _isRunning = false;

            try { if (_capture != null) _capture.StopRecording(); } catch { }
            StopOutput(_out1);
            StopOutput(_out2);
            try { if (_capture != null) _capture.Dispose(); } catch { }

            _capture = null;
            _captureDeviceId = null;
            _captureFaulted = false;
            _out1.Provider = null;
            _out2.Provider = null;
            _out1.LastError = null;
            _out2.LastError = null;

            if (wasOpen) Log("Duplication stopped.");
        }

        #endregion

        #region Settings Storage

        private static void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string content = File.ReadAllText(SettingsFile);
                    _selectedInputId = ExtractJsonValue(content, "inputId");
                    _selectedOut1Id = ExtractJsonValue(content, "out1Id");
                    _selectedOut2Id = ExtractJsonValue(content, "out2Id");
                    LoadTone(content, "tone1", _out1);
                    LoadTone(content, "tone2", _out2);
                    LoadEq(content, "eq1", _out1);
                    LoadEq(content, "eq2", _out2);
                }
            }
            catch { }
        }

        // Values stay strings so the file stays flat and string-valued (see ExtractJsonValue).
        private static void LoadTone(string content, string prefix, OutputSlot slot)
        {
            slot.ToneOn = ExtractJsonValue(content, prefix + "On") == "true";
            int hz;
            if (int.TryParse(ExtractJsonValue(content, prefix + "Hz"), NumberStyles.Integer, CultureInfo.InvariantCulture, out hz))
            {
                slot.ToneFrequencyHz = Math.Max(ToneMinHz, Math.Min(ToneMaxHz, hz));
            }
        }

        // Keys: <prefix>On, <prefix>F1..F3 (Hz), <prefix>G1..G3 (dB). Missing keys keep the defaults.
        private static void LoadEq(string content, string prefix, OutputSlot slot)
        {
            var freq = new int[EqSettings.Bands];
            var gain = new double[EqSettings.Bands];
            for (int i = 0; i < EqSettings.Bands; i++)
            {
                string n = (i + 1).ToString(CultureInfo.InvariantCulture);
                int hz;
                // A saved frequency outside the range (from an older, wider range) goes back to the
                // band default, so the points do not all pile up at the range limit.
                freq[i] = int.TryParse(ExtractJsonValue(content, prefix + "F" + n), NumberStyles.Integer, CultureInfo.InvariantCulture, out hz)
                          && hz >= EqSettings.MinHz && hz <= EqSettings.MaxHz
                    ? hz : EqSettings.Default.FreqHz(i);
                double db;
                gain[i] = double.TryParse(ExtractJsonValue(content, prefix + "G" + n), NumberStyles.Float, CultureInfo.InvariantCulture, out db)
                    ? db : 0;
            }
            slot.Eq = new EqSettings(ExtractJsonValue(content, prefix + "On") == "true", freq, gain);
        }

        private static string EqJson(string prefix, EqSettings eq)
        {
            var lines = new List<string> { string.Format("  \"{0}On\": \"{1}\"", prefix, eq.On ? "true" : "false") };
            for (int i = 0; i < EqSettings.Bands; i++)
            {
                lines.Add(string.Format(CultureInfo.InvariantCulture, "  \"{0}F{1}\": \"{2}\"", prefix, i + 1, eq.FreqHz(i)));
                lines.Add(string.Format(CultureInfo.InvariantCulture, "  \"{0}G{1}\": \"{2:0.0}\"", prefix, i + 1, eq.GainDb(i)));
            }
            return string.Join(",\n", lines.ToArray());
        }

        private static void SaveSettings()
        {
            try
            {
                string json = string.Format(CultureInfo.InvariantCulture,
                    "{{\n  \"inputId\": \"{0}\",\n  \"out1Id\": \"{1}\",\n  \"out2Id\": \"{2}\",\n" +
                    "  \"tone1On\": \"{3}\",\n  \"tone1Hz\": \"{4}\",\n  \"tone2On\": \"{5}\",\n  \"tone2Hz\": \"{6}\",\n" +
                    "{7},\n{8}\n}}",
                    _selectedInputId.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    _selectedOut1Id.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    _selectedOut2Id.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    _out1.ToneOn ? "true" : "false", _out1.ToneFrequencyHz,
                    _out2.ToneOn ? "true" : "false", _out2.ToneFrequencyHz,
                    EqJson("eq1", _out1.Eq), EqJson("eq2", _out2.Eq)
                );
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }

        private static string ExtractJsonValue(string json, string key)
        {
            string pattern = "\"" + key + "\":\\s*\"([^\"]+)\"";
            var match = Regex.Match(json, pattern);
            return match.Success ? match.Groups[1].Value : "";
        }

        #endregion

        private static void Shutdown()
        {
            lock (_audioLock)
            {
                _wantRunning = false;
                StopDuplicationInternal();
            }

            try
            {
                if (_sysTrayIcon != null)
                {
                    _sysTrayIcon.Visible = false;
                    _sysTrayIcon.Dispose();
                }
            }
            catch { }

            try
            {
                MediaFoundationApi.Shutdown();
            }
            catch { }
        }
    }
}
