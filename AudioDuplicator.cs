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
        }

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

                PopulateDeviceMenu(inputMenu, "Input Device", 0, _selectedInputId);
                PopulateDeviceMenu(out1Menu, "Output 1", 1, _selectedOut1Id);
                PopulateDeviceMenu(out2Menu, "Output 2", 2, _selectedOut2Id);
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
        private static void PopulateDeviceMenu(MenuItem parent, string baseLabel, int slot, string selectedId)
        {
            parent.MenuItems.Clear();
            string selectedName = null;

            if (slot == 0)
            {
                bool isDefault = selectedId == "default" || string.IsNullOrEmpty(selectedId);
                var def = new MenuItem("System Default Render Loopback", (s, e) => OnDeviceSelected(0, "default"));
                def.RadioCheck = true;
                def.Checked = isDefault;
                parent.MenuItems.Add(def);
                if (isDefault) selectedName = "System Default";
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
                    var item = new MenuItem(name, (s, e) => OnDeviceSelected(slot, id));
                    item.RadioCheck = true;
                    item.Checked = (selectedId == id);
                    if (item.Checked) selectedName = name;
                    parent.MenuItems.Add(item);
                }
            }
            catch (Exception ex)
            {
                Log("Failed to enumerate render devices for menu: " + ex.Message);
                parent.MenuItems.Add(new MenuItem("(error listing devices)") { Enabled = false });
            }

            parent.Text = selectedName != null ? baseLabel + ": " + selectedName : baseLabel;
        }

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
                IWaveProvider finalProvider = slot.Provider;
                string conversion = "passthrough (no resample, device accepts capture format)";
                if (RequiresResampling(device, format))
                {
                    slot.Resampler = new MediaFoundationResampler(slot.Provider, mixFormat) { ResamplerQuality = 60 };
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
                }
            }
            catch { }
        }

        private static void SaveSettings()
        {
            try
            {
                string json = string.Format(
                    "{{\n  \"inputId\": \"{0}\",\n  \"out1Id\": \"{1}\",\n  \"out2Id\": \"{2}\"\n}}",
                    _selectedInputId.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    _selectedOut1Id.Replace("\\", "\\\\").Replace("\"", "\\\""),
                    _selectedOut2Id.Replace("\\", "\\\\").Replace("\"", "\\\"")
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
