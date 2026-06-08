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
        private static bool _isRunning = false;
        private static readonly string SettingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        // Audio Objects
        private static WasapiCapture _capture;
        private static WasapiOut _play1;
        private static WasapiOut _play2;
        private static BufferedWaveProvider _provider1;
        private static BufferedWaveProvider _provider2;
        private static MediaFoundationResampler _resampler1;
        private static MediaFoundationResampler _resampler2;
        private static MMDevice _inputDevice;
        private static MMDevice _outDevice1;
        private static MMDevice _outDevice2;

        private static NotifyIcon _sysTrayIcon;
        private static ContextMenu _sysTrayMenu;
        private static readonly object _audioLock = new object();

        // Diagnostics / logging
        private static readonly object _logLock = new object();
        private static readonly string LogFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        private static long _captureBytesSinceTick = 0;   // bytes delivered by capture since last stats tick
        private static int _captureCallbacksSinceTick = 0; // number of DataAvailable callbacks since last stats tick
        private static long _pruneCount1 = 0;              // times provider 1 buffer was force-cleared
        private static long _pruneCount2 = 0;              // times provider 2 buffer was force-cleared

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
                toggleItem.Text = _isRunning ? "Stop Duplication" : "Start Duplication";
                toggleItem.Checked = _isRunning;

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
                if (_isRunning)
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
                if (_isRunning)
                {
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

                    double buf1 = _provider1 != null ? _provider1.BufferedDuration.TotalMilliseconds : -1;
                    double buf2 = _provider2 != null ? _provider2.BufferedDuration.TotalMilliseconds : -1;

                    Log(string.Format(CultureInfo.InvariantCulture,
                        "STATS captured={0:F0}ms/s ({1} bytes, {2} cb) | buf1={3:F0}ms buf2={4:F0}ms | prunes1={5} prunes2={6} | play1={7} play2={8}",
                        capturedMs, capBytes, callbacks, buf1, buf2,
                        Interlocked.Read(ref _pruneCount1),
                        Interlocked.Read(ref _pruneCount2),
                        _play1 != null ? _play1.PlaybackState.ToString() : "null",
                        _play2 != null ? _play2.PlaybackState.ToString() : "null"));
                }
                catch (Exception ex)
                {
                    Log("STATS exception: " + ex.Message);
                }
            }
        }

        #endregion

        #region Audio Duplication Core

        private static void StartDuplicationInternal()
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();

                // 1. Resolve Devices
                if (_selectedInputId == "default" || string.IsNullOrEmpty(_selectedInputId))
                {
                    _inputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                else
                {
                    _inputDevice = enumerator.GetDevice(_selectedInputId);
                }

                if (string.IsNullOrEmpty(_selectedOut1Id) || string.IsNullOrEmpty(_selectedOut2Id))
                {
                    throw new Exception("Output devices are not fully configured.");
                }

                _outDevice1 = enumerator.GetDevice(_selectedOut1Id);
                _outDevice2 = enumerator.GetDevice(_selectedOut2Id);

                Log("=== Starting duplication ===");
                Log("Input device : " + _inputDevice.FriendlyName);
                Log("Output 1     : " + _outDevice1.FriendlyName);
                Log("Output 2     : " + _outDevice2.FriendlyName);

                // 2. Initialize Capture with 40ms buffer
                _capture = new WasapiLoopbackCaptureEx(_inputDevice, 40);
                Log("Capture format: " + DescribeFormat(_capture.WaveFormat) + " (avgBytes/s=" + _capture.WaveFormat.AverageBytesPerSecond + ")");

                // Reset diagnostic counters for this run
                _captureBytesSinceTick = 0;
                _captureCallbacksSinceTick = 0;
                _pruneCount1 = 0;
                _pruneCount2 = 0;

                // 3. Initialize Buffers with Loopback Format and bound to 2s buffer duration for safety headroom
                _provider1 = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(2) };
                _provider2 = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(2) };

                // 4. Hook capture event
                _capture.DataAvailable += (s, e) =>
                {
                    // Track capture throughput regardless of byte count so we can detect a starved/silent source
                    Interlocked.Add(ref _captureBytesSinceTick, e.BytesRecorded);
                    Interlocked.Increment(ref _captureCallbacksSinceTick);

                    if (e.BytesRecorded > 0)
                    {
                        _provider1.AddSamples(e.Buffer, 0, e.BytesRecorded);
                        _provider2.AddSamples(e.Buffer, 0, e.BytesRecorded);

                        // Keep buffer size under control to minimize latency and prevent stutter
                        PruneBuffer("Provider 1 (Output 1)", _provider1, 300, ref _pruneCount1);
                        PruneBuffer("Provider 2 (Output 2)", _provider2, 300, ref _pruneCount2);
                    }
                };

                // Surface playback stopped events (driver glitches / device removal manifest here)
                EventHandler<StoppedEventArgs> onStopped = (s, e) =>
                {
                    if (e.Exception != null) Log("PLAYBACK STOPPED with exception: " + e.Exception.Message);
                };

                // 5. Initialize Output 1 with 50ms target latency in timer-driven mode
                _play1 = new WasapiOut(_outDevice1, AudioClientShareMode.Shared, false, 50);
                _play1.PlaybackStopped += onStopped;
                var mixFormat1 = _outDevice1.AudioClient.MixFormat;
                IWaveProvider finalProv1 = _provider1;
                if (RequiresResampling(_outDevice1, _capture.WaveFormat))
                {
                    _resampler1 = new MediaFoundationResampler(_provider1, mixFormat1) { ResamplerQuality = 60 };
                    finalProv1 = _resampler1;
                    Log("Output 1 mix format: " + DescribeFormat(mixFormat1) + " -> RESAMPLING enabled");
                }
                else
                {
                    Log("Output 1 mix format: " + DescribeFormat(mixFormat1) + " -> passthrough (no resample, device accepts capture format)");
                }
                _play1.Init(finalProv1);

                // 6. Initialize Output 2 with 50ms target latency in timer-driven mode
                _play2 = new WasapiOut(_outDevice2, AudioClientShareMode.Shared, false, 50);
                _play2.PlaybackStopped += onStopped;
                var mixFormat2 = _outDevice2.AudioClient.MixFormat;
                IWaveProvider finalProv2 = _provider2;
                if (RequiresResampling(_outDevice2, _capture.WaveFormat))
                {
                    _resampler2 = new MediaFoundationResampler(_provider2, mixFormat2) { ResamplerQuality = 60 };
                    finalProv2 = _resampler2;
                    Log("Output 2 mix format: " + DescribeFormat(mixFormat2) + " -> RESAMPLING enabled");
                }
                else
                {
                    Log("Output 2 mix format: " + DescribeFormat(mixFormat2) + " -> passthrough (no resample, device accepts capture format)");
                }
                _play2.Init(finalProv2);

                // 7. Start Playback & Capture
                _play1.Play();
                _play2.Play();
                _capture.StartRecording();

                _isRunning = true;
                Log("Duplication started successfully.");
            }
            catch (Exception ex)
            {
                StopDuplicationInternal();
                Log("Failed to start audio duplication: " + ex);
                if (_sysTrayIcon != null)
                {
                    _sysTrayIcon.ShowBalloonTip(3000, "Audio Duplicator Error", "Failed to start audio duplication: " + ex.Message, ToolTipIcon.Error);
                }
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
            try { if (_play1 != null) _play1.Stop(); } catch { }
            try { if (_play2 != null) _play2.Stop(); } catch { }

            try { if (_capture != null) _capture.Dispose(); } catch { }
            try { if (_play1 != null) _play1.Dispose(); } catch { }
            try { if (_play2 != null) _play2.Dispose(); } catch { }
            try { if (_resampler1 != null) _resampler1.Dispose(); } catch { }
            try { if (_resampler2 != null) _resampler2.Dispose(); } catch { }

            _capture = null;
            _play1 = null;
            _play2 = null;
            _resampler1 = null;
            _resampler2 = null;
            _provider1 = null;
            _provider2 = null;

            _inputDevice = null;
            _outDevice1 = null;
            _outDevice2 = null;

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
