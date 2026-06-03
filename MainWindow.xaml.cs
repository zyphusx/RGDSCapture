using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RGDSCapture
{
    public partial class MainWindow : Window
    {
        // ── Connection state ──────────────────────────────────────────
        private bool        _isConnected  = false;
        private SshManager? _ssh;
        private CancellationTokenSource? _connectCts;

        private bool _topStreamActive    = false;
        private bool _bottomStreamActive = false;

        // ── Stream health / auto-recovery ─────────────────────────────
        private const double FreezeThresholdSec = 5.0;
        private const int    MaxAutoRetries     = 3;
        private int          _topRetries        = 0;
        private int          _bottomRetries     = 0;
        private bool         _topRecovering     = false;
        private bool         _bottomRecovering  = false;

        // ── RTP receivers ─────────────────────────────────────────────
        private RtpStreamReceiver? _topReceiver;
        private RtpStreamReceiver? _bottomReceiver;

        // ── Frame buffers ─────────────────────────────────────────────
        private readonly object _topLock    = new();
        private readonly object _bottomLock = new();
        private byte[]? _topPending    = null;
        private byte[]? _bottomPending = null;
        private int     _topW, _topH;
        private int     _bottomW, _bottomH;

        private WriteableBitmap? _topBitmap;
        private WriteableBitmap? _bottomBitmap;

        // ── Render timer ──────────────────────────────────────────────
        private DispatcherTimer? _renderTimer;

        // ── Audio (Line-In passthrough) ───────────────────────────────
        private AudioMonitor _audioMonitor = new();
        private bool         _audioRunning = false;
        private DispatcherTimer? _vuTimer;

        // ── Speedrun timer ────────────────────────────────────────────
        private readonly Stopwatch _speedrunWatch   = new();
        private DispatcherTimer?   _speedrunTimer;
        private TimeSpan           _speedrunOffset  = TimeSpan.Zero;
        private bool               _speedrunRunning = false;

        // ── Recording ─────────────────────────────────────────────────
        private Process? _topRecordProc;
        private Process? _bottomRecordProc;
        private bool     _topRecording    = false;
        private bool     _bottomRecording = false;

        private readonly string _recordingFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "RGDSCapture");
        private readonly string _screenshotFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "RGDSCapture");

        // ── Log ───────────────────────────────────────────────────────
        private const int MaxLogLines = 300;

        // ─────────────────────────────────────────────────────────────
        public MainWindow()
        {
            InitializeComponent();
            Closing += MainWindow_Closing;

            Directory.CreateDirectory(_screenshotFolder);
            Directory.CreateDirectory(_recordingFolder);

            // Apply saved theme before any UI is visible
            var theme = ThemeManager.Load();
            UpdateThemeButton(theme);

            PopulateAudioDevices();
            StartSpeedrunDisplayTimer();
            UpdateVolumeLabel(SliderVolume.Value);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                string msg = (e.ExceptionObject as Exception)?.Message ?? "Unknown error";
                Dispatcher.InvokeAsync(() => AppendLog($"[CRASH] {msg}", isError: true));
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Dispatcher.InvokeAsync(() =>
                    AppendLog($"[TASK] {e.Exception.InnerException?.Message}", isError: true));
                e.SetObserved();
            };
        }

        // ─────────────────────────────────────────────────────────────
        // AUDIO — device population
        // ─────────────────────────────────────────────────────────────
        private void PopulateAudioDevices()
        {
            var inputs  = AudioMonitor.GetInputDevices();
            var outputs = AudioMonitor.GetOutputDevices();

            CmbAudioInput.ItemsSource   = inputs;
            CmbAudioOutput.ItemsSource  = outputs;

            // Pre-select first device that looks like a Line-In / Stereo Mix
            int lineInIdx = 0;
            for (int i = 0; i < inputs.Count; i++)
            {
                string n = inputs[i].Name.ToLowerInvariant();
                if (n.Contains("line") || n.Contains("stereo mix") || n.Contains("aux"))
                { lineInIdx = i; break; }
            }
            CmbAudioInput.SelectedIndex  = lineInIdx;
            CmbAudioOutput.SelectedIndex = 0;

            if (inputs.Count == 0)
                AppendLog("[AUDIO] No input devices found — check sound settings.", isError: true);
        }

        // ─────────────────────────────────────────────────────────────
        // AUDIO — start / stop button
        // ─────────────────────────────────────────────────────────────
        private void BtnAudioStart_Click(object sender, RoutedEventArgs e)
        {
            if (!_audioRunning)
                StartAudio();
            else
                StopAudio();
        }

        private void StartAudio()
        {
            int inIdx  = CmbAudioInput.SelectedIndex;
            // GetOutputDevices() returns "System Default" at slot 0 (index=-1),
            // then real devices. SelectedIndex 0 = default (-1), 1 = device 0, etc.
            int outIdx = CmbAudioOutput.SelectedIndex <= 0
                            ? -1
                            : CmbAudioOutput.SelectedIndex - 1;

            if (inIdx < 0)
            {
                AppendLog("[AUDIO] No input device selected.", isError: true);
                return;
            }

            try
            {
                _audioMonitor.Start(inIdx, outIdx);
                _audioMonitor.Volume = (float)SliderVolume.Value;
                _audioRunning = true;
                BtnAudioStart.Content    = "⏹ Audio";
                BtnAudioStart.Background = new SolidColorBrush(Color.FromRgb(140, 30, 30));
                StartVuTimer();
                AppendLog($"[AUDIO] Line-In monitoring started — " +
                          $"{CmbAudioInput.Text} → {CmbAudioOutput.Text}");
            }
            catch (Exception ex)
            {
                AppendLog($"[AUDIO] Failed to start: {ex.Message}", isError: true);
            }
        }

        private void StopAudio()
        {
            _vuTimer?.Stop();
            _audioMonitor.Stop();
            _audioRunning = false;
            BtnAudioStart.Content    = "▶ Audio";
            BtnAudioStart.Background = new SolidColorBrush(Color.FromRgb(28, 42, 74));
            VuLeft.Width  = 0;
            VuRight.Width = 0;
            AppendLog("[AUDIO] Line-In monitoring stopped.");
        }

        // ── VU meter update timer ─────────────────────────────────────
        private void StartVuTimer()
        {
            _vuTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(40)  // ~25 fps meter refresh
            };
            _vuTimer.Tick += (_, _) => UpdateVuMeters();
            _vuTimer.Start();
        }

        private void UpdateVuMeters()
        {
            if (!_audioRunning) return;
            // VU bars are 8px wide containers — fill proportionally
            const double maxW = 8.0;
            VuLeft.Width  = Math.Clamp(_audioMonitor.LevelLeft  * maxW * 3.0, 0, maxW);
            VuRight.Width = Math.Clamp(_audioMonitor.LevelRight * maxW * 3.0, 0, maxW);

            // Colour: green → orange → red based on level
            float peak = Math.Max(_audioMonitor.LevelLeft, _audioMonitor.LevelRight);
            Color c = peak < 0.6f
                ? Color.FromRgb(0, 200, 100)
                : peak < 0.85f
                    ? Color.FromRgb(255, 165, 0)
                    : Color.FromRgb(233, 69, 96);
            var brush = new SolidColorBrush(c);
            VuLeft.Fill = VuRight.Fill = brush;
        }

        // ── Volume slider ─────────────────────────────────────────────
        private void SliderVolume_ValueChanged(object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (_audioRunning)
                _audioMonitor.Volume = (float)e.NewValue;
            UpdateVolumeLabel(e.NewValue);
        }

        private void UpdateVolumeLabel(double v)
        {
            if (TxtVolumeLabel != null)
                TxtVolumeLabel.Text = $"{(int)(v * 100)}%";
        }

        // ─────────────────────────────────────────────────────────────
        // RENDER TIMER
        // ─────────────────────────────────────────────────────────────
        private void StartRenderTimer()
        {
            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _renderTimer.Tick += RenderTick;
            _renderTimer.Start();
        }

        private void StopRenderTimer()
        {
            _renderTimer?.Stop();
            _renderTimer = null;
        }

        private void RenderTick(object? sender, EventArgs e)
        {
            RenderScreen(ref _topPending, ref _topW, ref _topH,
                         _topLock, ref _topBitmap,
                         ImgTopScreen, ImgTopScreenSide, ImgTopScreenOnly);

            RenderScreen(ref _bottomPending, ref _bottomW, ref _bottomH,
                         _bottomLock, ref _bottomBitmap,
                         ImgBottomScreen, ImgBottomScreenSide, ImgBottomScreenOnly);

            UpdateHealthIndicators();
            CheckFreezeAndAutoRecover();
        }

        private static void RenderScreen(
            ref byte[]?          pendingField,
            ref int              wField,
            ref int              hField,
            object               lockObj,
            ref WriteableBitmap? bitmapField,
            params System.Windows.Controls.Image[] targets)
        {
            byte[]? data;
            int w, h;

            lock (lockObj)
            {
                if (pendingField == null) return;
                data         = pendingField;
                w            = wField;
                h            = hField;
                pendingField = null;
            }

            if (bitmapField == null ||
                bitmapField.PixelWidth != w ||
                bitmapField.PixelHeight != h)
            {
                bitmapField = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                foreach (var img in targets)
                    img.Source = bitmapField;
            }

            var rect   = new System.Windows.Int32Rect(0, 0, w, h);
            int stride = w * 4;
            bitmapField.Lock();
            bitmapField.WritePixels(rect, data, stride, 0);
            bitmapField.Unlock();
        }

        // ─────────────────────────────────────────────────────────────
        // STREAM HEALTH + AUTO-RECOVERY
        // ─────────────────────────────────────────────────────────────
        private void UpdateHealthIndicators()
        {
            if (!_isConnected) return;

            if (_topReceiver != null)
            {
                float fps = _topReceiver.CurrentFps;
                TxtTopFps.Text = fps > 0 ? $"{fps:F1} fps" : "-- fps";

                if (_topRecovering)
                    SetStreamBadge(true, StreamBadgeState.Recovering);
                else if (_topReceiver.IsFrozen)
                    SetStreamBadge(true, StreamBadgeState.Frozen);
                else if (_topStreamActive)
                    SetStreamBadge(true, StreamBadgeState.Live);
                else
                    SetStreamBadge(true, StreamBadgeState.Waiting);
            }

            if (_bottomReceiver != null)
            {
                float fps = _bottomReceiver.CurrentFps;
                TxtBottomFps.Text = fps > 0 ? $"{fps:F1} fps" : "-- fps";

                if (_bottomRecovering)
                    SetStreamBadge(false, StreamBadgeState.Recovering);
                else if (_bottomReceiver.IsFrozen)
                    SetStreamBadge(false, StreamBadgeState.Frozen);
                else if (_bottomStreamActive)
                    SetStreamBadge(false, StreamBadgeState.Live);
                else
                    SetStreamBadge(false, StreamBadgeState.Waiting);
            }
        }

        private void CheckFreezeAndAutoRecover()
        {
            if (!_isConnected || _ssh == null) return;

            if (_topReceiver?.IsFrozen == true &&
                !_topRecovering && _topRetries < MaxAutoRetries)
            {
                _topRecovering = true;
                _topRetries++;
                AppendLog($"[AUTO] Top frozen — attempt {_topRetries}/{MaxAutoRetries}", isError: true);
                _ = AutoRecoverStream(StreamType.TopScreen);
            }

            if (_bottomReceiver?.IsFrozen == true &&
                !_bottomRecovering && _bottomRetries < MaxAutoRetries)
            {
                _bottomRecovering = true;
                _bottomRetries++;
                AppendLog($"[AUTO] Bottom frozen — attempt {_bottomRetries}/{MaxAutoRetries}", isError: true);
                _ = AutoRecoverStream(StreamType.BottomScreen);
            }
        }

        private async Task AutoRecoverStream(StreamType stream)
        {
            await Task.Delay(500);
            if (!_isConnected || _ssh == null)
            {
                if (stream == StreamType.TopScreen)    _topRecovering    = false;
                else                                   _bottomRecovering = false;
                return;
            }

            await _ssh.RestartStreamAsync(stream);

            if (stream == StreamType.TopScreen)
            { _topStreamActive = false; _topRecovering = false; }
            else
            { _bottomStreamActive = false; _bottomRecovering = false; }
        }

        private enum StreamBadgeState { Waiting, Live, Frozen, Recovering }

        private void SetStreamBadge(bool isTop, StreamBadgeState state)
        {
            var dot  = isTop ? DotTop    : DotBottom;
            var text = isTop ? TxtTopBadge : TxtBottomBadge;
            var fps  = isTop ? TxtTopFps   : TxtBottomFps;

            (string label, Color c) = state switch
            {
                StreamBadgeState.Live       => ("● LIVE",       Color.FromRgb(0, 200, 100)),
                StreamBadgeState.Frozen     => ("● FROZEN",     Color.FromRgb(233, 69, 96)),
                StreamBadgeState.Recovering => ("● RECOVERING", Color.FromRgb(255, 165, 0)),
                _                           => ("○ WAITING",    Color.FromRgb(58, 58, 85))
            };

            var brush = new SolidColorBrush(c);
            dot.Fill        = brush;
            text.Text       = label;
            text.Foreground = brush;
            fps.Foreground  = new SolidColorBrush(
                state == StreamBadgeState.Live
                    ? Color.FromRgb(60, 130, 90)
                    : Color.FromRgb(58, 74, 96));
        }

        // ─────────────────────────────────────────────────────────────
        // CONNECT / DISCONNECT
        // ─────────────────────────────────────────────────────────────
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected) await DoConnect();
            else               ConfirmAndDisconnect();
        }

        private async Task DoConnect()
        {
            string ip   = TxtIpAddress.Text.Trim();
            string port = TxtSshPort.Text.Trim();

            if (string.IsNullOrEmpty(ip))
            { AppendLog("Enter a device IP address first.", isError: true); return; }

            if (!int.TryParse(port, out int sshPort))
            { AppendLog("Port must be a number.", isError: true); return; }

            var dialog = new ConnectDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;

            string windowsIp = GetLocalIpAddress();
            if (windowsIp == "127.0.0.1")
            { AppendLog("Could not detect local IP. Are you on a network?", isError: true); return; }

            BtnConnect.IsEnabled = false;
            BtnConnect.Content   = "Connecting...";
            _topStreamActive = _bottomStreamActive = false;
            _topRetries = _bottomRetries = 0;
            _topRecovering = _bottomRecovering = false;
            ResetStreamBadges();

            _connectCts = new CancellationTokenSource();
            _ssh = new SshManager();
            _ssh.StatusChanged += OnSshStatusChanged;
            _ssh.StreamStarted += OnStreamStarted;
            _ssh.Disconnected  += OnSshDisconnected;

            try { StartReceivers(); }
            catch (Exception ex)
            {
                AppendLog($"Receiver init failed: {ex.Message}", isError: true);
                ResetToDisconnectedState();
                return;
            }

            bool success;
            try
            {
                success = await _ssh.ConnectAsync(
                    ip, sshPort,
                    dialog.SshUsername, dialog.SshPassword,
                    windowsIp,
                    _connectCts.Token);
            }
            catch (Exception ex)
            {
                AppendLog($"Connect exception: {ex.Message}", isError: true);
                StopReceivers();
                ResetToDisconnectedState();
                return;
            }

            if (success)
            {
                _isConnected = true;
                BtnConnect.Content   = "Disconnect";
                BtnConnect.IsEnabled = true;
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0, 200, 100));
                SetConnectedControls(true);
                AppendLog($"Connected to {ip}. Video streaming via RTP. " +
                          $"Audio: connect 3.5mm cable and click ▶ Audio.");
            }
            else
            {
                StopReceivers();
                ResetToDisconnectedState();
            }
        }

        private void ConfirmAndDisconnect()
        {
            var r = MessageBox.Show(
                "This will stop all GStreamer streams on the DS and close the SSH connection.\n\n" +
                "Are you sure you want to disconnect?",
                "Confirm Disconnect",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (r == MessageBoxResult.Yes)
            {
                StopAllRecording();
                DisconnectCleanup();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // RESTART STREAMS
        // ─────────────────────────────────────────────────────────────
        private async void BtnRestartTop_Click(object sender, RoutedEventArgs e)
            => await RestartStreamAsync(StreamType.TopScreen);

        private async void BtnRestartBottom_Click(object sender, RoutedEventArgs e)
            => await RestartStreamAsync(StreamType.BottomScreen);

        private async void BtnRestartAll_Click(object sender, RoutedEventArgs e)
            => await RestartAllStreamsAsync();

        private async Task RestartStreamAsync(StreamType stream)
        {
            if (!_isConnected || _ssh == null) return;
            bool isTop = stream == StreamType.TopScreen;
            if (isTop) { _topStreamActive = false; _topRetries = 0; }
            else       { _bottomStreamActive = false; _bottomRetries = 0; }
            AppendLog($"[MANUAL] Restarting {(isTop ? "top" : "bottom")} stream...");
            await _ssh.RestartStreamAsync(stream);
        }

        private async Task RestartAllStreamsAsync()
        {
            if (!_isConnected || _ssh == null) return;
            AppendLog("[MANUAL] Restarting ALL streams...");
            _topStreamActive = _bottomStreamActive = false;
            _topRetries = _bottomRetries = 0;
            await _ssh.RestartStreamAsync(StreamType.TopScreen);
            await _ssh.RestartStreamAsync(StreamType.BottomScreen);
        }

        // ─────────────────────────────────────────────────────────────
        // CONSOLE POWER
        // ─────────────────────────────────────────────────────────────
        private void BtnShutdown_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || _ssh == null) return;
            var r = MessageBox.Show(
                "Send 'sudo shutdown -h now' to the console?\n\nThe app will disconnect automatically.",
                "Shutdown Console", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
            {
                AppendLog("[POWER] Shutdown command sent.", isError: true);
                _ssh.ShutdownConsole();
                Task.Delay(1200).ContinueWith(_ => Dispatcher.Invoke(DisconnectCleanup));
            }
        }

        private void BtnReboot_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || _ssh == null) return;
            var r = MessageBox.Show(
                "Send 'sudo reboot' to the console?\n\nThe app will disconnect automatically.",
                "Reboot Console", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
            {
                AppendLog("[POWER] Reboot command sent.", isError: true);
                _ssh.RebootConsole();
                Task.Delay(1200).ContinueWith(_ => Dispatcher.Invoke(DisconnectCleanup));
            }
        }

        // ─────────────────────────────────────────────────────────────
        // SCREENSHOT
        // ─────────────────────────────────────────────────────────────
        private void BtnScreenshot_Click(object sender, RoutedEventArgs e) => TakeScreenshot();

        private void TakeScreenshot()
        {
            if (!_isConnected) return;
            int saved = 0;
            string ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            try
            {
                if (_topBitmap != null)
                { SaveBitmapToPng(_topBitmap, Path.Combine(_screenshotFolder, $"top_{ts}.png")); saved++; }
                if (_bottomBitmap != null)
                { SaveBitmapToPng(_bottomBitmap, Path.Combine(_screenshotFolder, $"bottom_{ts}.png")); saved++; }
                AppendLog($"[SCREENSHOT] {saved} image(s) → {_screenshotFolder}");
            }
            catch (Exception ex)
            { AppendLog($"[SCREENSHOT] Failed: {ex.Message}", isError: true); }
        }

        private static void SaveBitmapToPng(WriteableBitmap bmp, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            enc.Save(fs);
        }

        // ─────────────────────────────────────────────────────────────
        // RECORDING
        // ─────────────────────────────────────────────────────────────
        private void BtnRecordTop_Click(object sender, RoutedEventArgs e)
            => ToggleRecording(StreamType.TopScreen);

        private void BtnRecordBottom_Click(object sender, RoutedEventArgs e)
            => ToggleRecording(StreamType.BottomScreen);

        private void ToggleRecording(StreamType stream)
        {
            bool isTop = stream == StreamType.TopScreen;
            if (isTop ? _topRecording : _bottomRecording)
                StopRecording(stream);
            else
                StartRecording(stream);
        }

        private void StartRecording(StreamType stream)
        {
            bool   isTop   = stream == StreamType.TopScreen;
            int    port    = isTop ? 5000 : 5001;
            string label   = isTop ? "top" : "bottom";
            string ts      = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string outFile = Path.Combine(_recordingFolder, $"rg_{label}_{ts}.mp4");

            // Receive the live RTP that is already arriving on the local UDP port
            // and remux into MP4 with no re-encode (-c copy = zero extra CPU).
            // -movflags +faststart makes the MP4 playable during/after recording.
            string ffmpegArgs =
                $"-y -protocol_whitelist file,crypto,data,udp,rtp " +
                $"-i rtp://127.0.0.1:{port} " +
                $"-c copy -movflags +faststart " +
                $"\"{outFile}\"";

            string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                AppendLog($"[RECORD] ffmpeg.exe not found at {AppContext.BaseDirectory}", isError: true);
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName              = ffmpegPath,
                Arguments             = ffmpegArgs,
                UseShellExecute       = false,
                CreateNoWindow        = true,
                RedirectStandardInput = true
            };

            try
            {
                var proc = Process.Start(psi)!;
                if (isTop)
                {
                    _topRecordProc = proc;
                    _topRecording  = true;
                    BtnRecordTop.Content    = "⏹ Stop Top";
                    BtnRecordTop.Background = new SolidColorBrush(Color.FromRgb(160, 30, 30));
                }
                else
                {
                    _bottomRecordProc = proc;
                    _bottomRecording  = true;
                    BtnRecordBottom.Content    = "⏹ Stop Bottom";
                    BtnRecordBottom.Background = new SolidColorBrush(Color.FromRgb(160, 30, 30));
                }
                AppendLog($"[RECORD] {label} → {outFile}");
            }
            catch (Exception ex)
            { AppendLog($"[RECORD] Start failed: {ex.Message}", isError: true); }
        }

        private void StopRecording(StreamType stream)
        {
            bool isTop = stream == StreamType.TopScreen;
            var  proc  = isTop ? _topRecordProc : _bottomRecordProc;

            try
            {
                if (proc != null && !proc.HasExited)
                {
                    proc.StandardInput.Write('q');
                    proc.StandardInput.Flush();
                    proc.WaitForExit(3000);
                    if (!proc.HasExited) proc.Kill();
                }
            }
            catch { }
            finally { proc?.Dispose(); }

            var defaultBg = new SolidColorBrush(Color.FromRgb(28, 42, 74));
            if (isTop)
            {
                _topRecordProc = null; _topRecording = false;
                BtnRecordTop.Content    = "⏺ Rec Top";
                BtnRecordTop.Background = defaultBg;
            }
            else
            {
                _bottomRecordProc = null; _bottomRecording = false;
                BtnRecordBottom.Content    = "⏺ Rec Bottom";
                BtnRecordBottom.Background = defaultBg;
            }
            AppendLog($"[RECORD] {(isTop ? "Top" : "Bottom")} recording stopped.");
        }

        private void StopAllRecording()
        {
            if (_topRecording)    StopRecording(StreamType.TopScreen);
            if (_bottomRecording) StopRecording(StreamType.BottomScreen);
        }

        // ─────────────────────────────────────────────────────────────
        // SPEEDRUN TIMER
        // ─────────────────────────────────────────────────────────────
        private void StartSpeedrunDisplayTimer()
        {
            _speedrunTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _speedrunTimer.Tick += (_, _) => UpdateSpeedrunDisplay();
            _speedrunTimer.Start();
        }

        private void ToggleSpeedrunTimer()
        {
            if (!_speedrunRunning)
            {
                _speedrunWatch.Start();
                _speedrunRunning = true;
                BtnTimerStartStop.Content = "⏸ Pause";
                AppendLog("[TIMER] Started.");
            }
            else
            {
                _speedrunOffset += _speedrunWatch.Elapsed;
                _speedrunWatch.Reset();
                _speedrunRunning = false;
                BtnTimerStartStop.Content = "▶ Start";
                AppendLog($"[TIMER] Paused at {FormatTime(_speedrunOffset)}");
            }
        }

        private void BtnTimerStartStop_Click(object sender, RoutedEventArgs e)
            => ToggleSpeedrunTimer();

        private void BtnTimerReset_Click(object sender, RoutedEventArgs e)
        {
            _speedrunWatch.Reset();
            _speedrunOffset  = TimeSpan.Zero;
            _speedrunRunning = false;
            BtnTimerStartStop.Content = "▶ Start";
            TxtSpeedrunTimer.Text     = "00:00.000";
            AppendLog("[TIMER] Reset.");
        }

        private void BtnTimerLap_Click(object sender, RoutedEventArgs e)
        {
            var t = _speedrunOffset + _speedrunWatch.Elapsed;
            AppendLog($"[LAP]  {FormatTime(t)}");
        }

        private void UpdateSpeedrunDisplay()
        {
            var elapsed = _speedrunOffset + _speedrunWatch.Elapsed;
            TxtSpeedrunTimer.Text = FormatTime(elapsed);
        }

        private static string FormatTime(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        }

        // ─────────────────────────────────────────────────────────────
        // KEYBOARD SHORTCUTS
        // ─────────────────────────────────────────────────────────────
        private void MainWindow_KeyDown(object sender,
            System.Windows.Input.KeyEventArgs e)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.F5:
                    if (_isConnected) _ = RestartAllStreamsAsync();
                    break;
                case System.Windows.Input.Key.F6:
                    if (_isConnected) _ = RestartStreamAsync(StreamType.TopScreen);
                    break;
                case System.Windows.Input.Key.F7:
                    if (_isConnected) _ = RestartStreamAsync(StreamType.BottomScreen);
                    break;
                case System.Windows.Input.Key.F12:
                    if (_isConnected) TakeScreenshot();
                    break;
                case System.Windows.Input.Key.Space:
                    // Only trigger timer if focus is not in a text field
                    if (e.OriginalSource is not System.Windows.Controls.TextBox)
                        ToggleSpeedrunTimer();
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // RECEIVERS
        // ─────────────────────────────────────────────────────────────
        private void StartReceivers()
        {
            _topReceiver = new RtpStreamReceiver(5000)
            { FreezeThresholdSeconds = FreezeThresholdSec };
            _topReceiver.FrameReady += (data, w, h) =>
            {
                lock (_topLock) { _topPending = data; _topW = w; _topH = h; }
            };

            _bottomReceiver = new RtpStreamReceiver(5001)
            { FreezeThresholdSeconds = FreezeThresholdSec };
            _bottomReceiver.FrameReady += (data, w, h) =>
            {
                lock (_bottomLock) { _bottomPending = data; _bottomW = w; _bottomH = h; }
            };

            _topReceiver.Start();
            _bottomReceiver.Start();
            StartRenderTimer();
            AppendLog("UDP receivers open on ports 5000 / 5001. Waiting for frames...");
        }

        private void StopReceivers()
        {
            StopRenderTimer();
            _topReceiver?.Stop();    _topReceiver?.Dispose();    _topReceiver    = null;
            _bottomReceiver?.Stop(); _bottomReceiver?.Dispose(); _bottomReceiver = null;
        }

        // ─────────────────────────────────────────────────────────────
        // SSH EVENTS
        // ─────────────────────────────────────────────────────────────
        private void OnSshStatusChanged(string msg, bool isError)
            => Dispatcher.Invoke(() => AppendLog(msg, isError));

        private void OnStreamStarted(StreamType stream)
        {
            Dispatcher.Invoke(() =>
            {
                if (stream == StreamType.TopScreen)    _topStreamActive    = true;
                if (stream == StreamType.BottomScreen) _bottomStreamActive = true;
            });
        }

        private void OnSshDisconnected()
            => Dispatcher.Invoke(() => { if (_isConnected) DisconnectCleanup(); });

        // ─────────────────────────────────────────────────────────────
        // LAYOUT SELECTOR
        // ─────────────────────────────────────────────────────────────
        private void CmbLayout_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LayoutVertical == null) return;
            LayoutVertical.Visibility   = Visibility.Collapsed;
            LayoutSideBySide.Visibility = Visibility.Collapsed;
            LayoutTopOnly.Visibility    = Visibility.Collapsed;
            LayoutBottomOnly.Visibility = Visibility.Collapsed;

            switch (CmbLayout.SelectedIndex)
            {
                case 0: LayoutVertical.Visibility   = Visibility.Visible; break;
                case 1: LayoutSideBySide.Visibility = Visibility.Visible; break;
                case 2: LayoutTopOnly.Visibility    = Visibility.Visible; break;
                case 3: LayoutBottomOnly.Visibility = Visibility.Visible; break;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // LOG
        // ─────────────────────────────────────────────────────────────
        private void AppendLog(string message, bool isError = false)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss");
            TxtEventLog.AppendText($"[{ts}] {message}\n");

            // Trim excess lines
            while (TxtEventLog.LineCount > MaxLogLines)
            {
                int end = TxtEventLog.GetLineLength(0) + 1;
                TxtEventLog.Select(0, end);
                TxtEventLog.SelectedText = "";
            }
            TxtEventLog.ScrollToEnd();

            TxtStatus.Text = message;
            TxtStatus.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(233, 69, 96))
                : new SolidColorBrush(Color.FromRgb(96, 112, 160));
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtEventLog.Clear();
            AppendLog("Log cleared.");
        }

        // ─────────────────────────────────────────────────────────────
        // THEME
        // ─────────────────────────────────────────────────────────────
        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            var theme = ThemeManager.Toggle();
            UpdateThemeButton(theme);
            AppendLog($"[THEME] Switched to {theme} mode.");
        }

        private void UpdateThemeButton(ThemeManager.Theme theme)
        {
            BtnTheme.Content = theme == ThemeManager.Theme.Dark ? "☀ Light" : "🌙 Dark";
        }

        // ─────────────────────────────────────────────────────────────
        // DISCONNECT / CLEANUP
        // ─────────────────────────────────────────────────────────────
        private void DisconnectCleanup()
        {
            _connectCts?.Cancel();
            StopAllRecording();
            StopReceivers();
            _ssh?.Disconnect();
            _ssh?.Dispose();
            _ssh = null;

            _isConnected = false;
            _topStreamActive = _bottomStreamActive = false;
            _topRetries = _bottomRetries = 0;
            _topRecovering = _bottomRecovering = false;

            lock (_topLock)    { _topPending    = null; }
            lock (_bottomLock) { _bottomPending = null; }
            _topBitmap = _bottomBitmap = null;

            BtnConnect.Content   = "Connect";
            BtnConnect.IsEnabled = true;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(58, 58, 85));
            SetConnectedControls(false);
            ResetStreamBadges();
            AppendLog("Disconnected — streams stopped on device.");

            TxtTopFps.Text = TxtBottomFps.Text = "-- fps";
            foreach (var img in new[] {
                ImgTopScreen, ImgBottomScreen,
                ImgTopScreenSide, ImgBottomScreenSide,
                ImgTopScreenOnly, ImgBottomScreenOnly })
                img.Source = null;
        }

        // ─────────────────────────────────────────────────────────────
        // WINDOW CLOSE
        // ─────────────────────────────────────────────────────────────
        private void MainWindow_Closing(object? sender,
            System.ComponentModel.CancelEventArgs e)
        {
            if (_isConnected)
            {
                var r = MessageBox.Show(
                    "Streams are currently running on the DS.\n\n" +
                    "Closing will stop all GStreamer pipelines on the device and disconnect SSH.\n\n" +
                    "Exit anyway?",
                    "Confirm Exit",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (r != MessageBoxResult.Yes)
                { e.Cancel = true; return; }
            }

            _speedrunTimer?.Stop();
            _vuTimer?.Stop();
            _connectCts?.Cancel();
            StopAllRecording();
            StopAudio();
            StopReceivers();
            _ssh?.Disconnect();
            _ssh?.Dispose();
            _audioMonitor.Dispose();
        }

        // ─────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────
        private void SetConnectedControls(bool on)
        {
            BtnScreenshot.IsEnabled    = on;
            BtnRestartTop.IsEnabled    = on;
            BtnRestartBottom.IsEnabled = on;
            BtnRestartAll.IsEnabled    = on;
            BtnShutdown.IsEnabled      = on;
            BtnReboot.IsEnabled        = on;
            BtnRecordTop.IsEnabled     = on;
            BtnRecordBottom.IsEnabled  = on;
        }

        private void ResetStreamBadges()
        {
            SetStreamBadge(true,  StreamBadgeState.Waiting);
            SetStreamBadge(false, StreamBadgeState.Waiting);
        }

        private void ResetToDisconnectedState()
        {
            BtnConnect.Content   = "Connect";
            BtnConnect.IsEnabled = true;
            _ssh?.Dispose();
            _ssh = null;
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(233, 69, 96));
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                using var socket = new Socket(
                    AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                var ep = socket.LocalEndPoint as IPEndPoint;
                return ep?.Address.ToString() ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }
    }
}
