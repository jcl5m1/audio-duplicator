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

    static class Program
    {
        // Settings & State
        private static string _selectedInputId = "default";
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

            // Low-pass filter settings. Written by the UI thread, read by the playback thread.
            public volatile bool LowPassOn;
            public volatile int LowPassCutoffHz = 2000;
            public int EffectiveCutoffHz() { return LowPassOn ? LowPassCutoffHz : 0; }
        }

        private const int LowPassMinHz = 20;
        private const int LowPassMaxHz = 20000;
        private static string _captureDeviceId; // ID of the device the running capture uses; guarded by _audioLock
        private static Form _filterForm;
        private static System.Windows.Forms.Timer _saveTimer;

        // Audio Objects
        private static WasapiCapture _capture;
        private static volatile bool _captureFaulted = false;
        private static readonly OutputSlot _out1 = new OutputSlot("Output 1");
        private static readonly OutputSlot _out2 = new OutputSlot("Output 2");
        private const int ReconnectPollMillis = 2000;

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

            // Start duplication if settings exist
            lock (_audioLock)
            {
                if (!string.IsNullOrEmpty(_selectedOut1Id) && !string.IsNullOrEmpty(_selectedOut2Id))
                {
                    StartDuplicationInternal();
                }
            }

            // Watch for lost devices and reopen them when they return
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
            _sysTrayMenu.MenuItems.Add("Low-Pass Filter...", (s, e) => ShowFilterWindow());

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
                bool isDefault = selectedId == "default" || string.IsNullOrEmpty(selectedId);
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
            if (_selectedInputId == "default" || string.IsNullOrEmpty(_selectedInputId)) return DefaultRenderDeviceId();
            return _selectedInputId;
        }

        #region Low-Pass Filter Window

        private static string FormatHz(int hz)
        {
            return hz < 1000
                ? string.Format(CultureInfo.InvariantCulture, "{0} Hz", hz)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.0#} kHz", hz / 1000.0);
        }

        // The slider is logarithmic: 0..1000 maps to 20 Hz..20 kHz.
        private static int SliderToHz(int value)
        {
            return (int)Math.Round(LowPassMinHz * Math.Pow((double)LowPassMaxHz / LowPassMinHz, value / 1000.0));
        }

        private static int HzToSlider(int hz)
        {
            hz = Math.Max(LowPassMinHz, Math.Min(LowPassMaxHz, hz));
            return (int)Math.Round(1000 * Math.Log((double)hz / LowPassMinHz) / Math.Log((double)LowPassMaxHz / LowPassMinHz));
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
                Text = "Low-Pass Filter",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.CenterScreen,
                ClientSize = new Size(460, 268),
                Font = new Font("Segoe UI", 9F)
            };

            AddFilterGroup(form, _out1, "Output 1", _selectedOut1Id, 10);
            AddFilterGroup(form, _out2, "Output 2", _selectedOut2Id, 120);

            form.Controls.Add(new Label
            {
                Text = "12 dB/octave. Changes apply immediately and are saved.",
                Location = new Point(12, 236),
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

        private static void AddFilterGroup(Form form, OutputSlot slot, string label, string deviceId, int top)
        {
            var group = new GroupBox
            {
                Text = label + ": " + DeviceName(deviceId),
                Location = new Point(10, top),
                Size = new Size(440, 100)
            };

            var enabled = new CheckBox { Text = "On", Location = new Point(14, 40), AutoSize = true, Checked = slot.LowPassOn };
            var slider = new TrackBar
            {
                Location = new Point(70, 30),
                Width = 270,
                Minimum = 0,
                Maximum = 1000,
                TickFrequency = 100,
                SmallChange = 10,
                LargeChange = 100,
                Value = HzToSlider(slot.LowPassCutoffHz),
                Enabled = slot.LowPassOn
            };
            var value = new Label
            {
                Location = new Point(345, 40),
                Width = 85,
                Text = FormatHz(slot.LowPassCutoffHz),
                Enabled = slot.LowPassOn
            };

            enabled.CheckedChanged += (s, e) =>
            {
                slot.LowPassOn = enabled.Checked;
                slider.Enabled = enabled.Checked;
                value.Enabled = enabled.Checked;
                ScheduleSave();
            };
            slider.ValueChanged += (s, e) =>
            {
                int hz = SliderToHz(slider.Value);
                slot.LowPassCutoffHz = hz;
                value.Text = FormatHz(hz);
                ScheduleSave();
            };

            group.Controls.Add(enabled);
            group.Controls.Add(slider);
            group.Controls.Add(value);
            form.Controls.Add(group);
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
                    Log(string.Format(CultureInfo.InvariantCulture, "Low-pass filter: Output 1 {0}, Output 2 {1}",
                        _out1.LowPassOn ? FormatHz(_out1.LowPassCutoffHz) : "off",
                        _out2.LowPassOn ? FormatHz(_out2.LowPassCutoffHz) : "off"));
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
                    if (string.IsNullOrEmpty(_selectedOut1Id) || string.IsNullOrEmpty(_selectedOut2Id))
                    {
                        MessageBox.Show("Please select both Output 1 and Output 2 devices from the tray menu first.", "Configuration Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                if (_selectedInputId == "default" || string.IsNullOrEmpty(_selectedInputId))
                {
                    inputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                else
                {
                    inputDevice = enumerator.GetDevice(_selectedInputId);
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
                Log("Duplication started successfully.");
            }
            catch (Exception ex)
            {
                StopDuplicationInternal();
                Log("Failed to start audio duplication: " + ex);
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

                var device = new MMDeviceEnumerator().GetDevice(deviceId);
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
                // The filter keeps the capture format, so the resampling decision does not change.
                var filter = new LowPassWaveProvider(slot.Provider, slot.EffectiveCutoffHz);
                if (!filter.IsSupported) Log(slot.Name + ": low-pass filter not available for format " + DescribeFormat(format));
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

        private static void ReconnectLoop()
        {
            var enumerator = new MMDeviceEnumerator();
            while (true)
            {
                Thread.Sleep(ReconnectPollMillis);
                try
                {
                    lock (_audioLock)
                    {
                        if (_wantRunning) CheckDevices(enumerator);
                    }
                }
                catch (Exception ex)
                {
                    Log("Reconnect check exception: " + ex.Message);
                }
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
                if (!inputAvailable) return;
                Log("Input device available. Restarting duplication.");
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
            if (_selectedInputId != "default" && !string.IsNullOrEmpty(_selectedInputId))
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

            Log("Duplication stopped.");
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
                    LoadLowPass(content, "lp1", _out1);
                    LoadLowPass(content, "lp2", _out2);
                }
            }
            catch { }
        }

        // Values stay strings so the file stays flat and string-valued (see ExtractJsonValue).
        private static void LoadLowPass(string content, string prefix, OutputSlot slot)
        {
            slot.LowPassOn = ExtractJsonValue(content, prefix + "On") == "true";
            int hz;
            if (int.TryParse(ExtractJsonValue(content, prefix + "Hz"), NumberStyles.Integer, CultureInfo.InvariantCulture, out hz))
            {
                slot.LowPassCutoffHz = Math.Max(LowPassMinHz, Math.Min(LowPassMaxHz, hz));
            }
        }

        private static void SaveSettings()
        {
            try
            {
                string json = string.Format(CultureInfo.InvariantCulture,
                    "{{\n  \"inputId\": \"{0}\",\n  \"out1Id\": \"{1}\",\n  \"out2Id\": \"{2}\",\n" +
                    "  \"lp1On\": \"{3}\",\n  \"lp1Hz\": \"{4}\",\n  \"lp2On\": \"{5}\",\n  \"lp2Hz\": \"{6}\"\n}}",
                    _selectedInputId.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    _selectedOut1Id.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    _selectedOut2Id.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    _out1.LowPassOn ? "true" : "false", _out1.LowPassCutoffHz,
                    _out2.LowPassOn ? "true" : "false", _out2.LowPassCutoffHz
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
