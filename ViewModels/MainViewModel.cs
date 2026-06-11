using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using RGDSCapture.Core;
using RGDSCapture.Services;

namespace RGDSCapture.ViewModels
{
    /// <summary>
    /// Application orchestrator: connection lifecycle, stream health,
    /// layout/theme, screenshots and console power. Views supply the
    /// interactive bits (credential prompt, confirmations, fullscreen)
    /// through delegates so this class stays UI-framework-light.
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        private static readonly Brush DotConnectedBrush =
            Frozen(new SolidColorBrush(Color.FromRgb(0x13, 0xA1, 0x0E)));
        private static readonly Brush DotLostBrush =
            Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)));
        private static readonly Brush DotIdleBrush =
            Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)));

        private readonly SettingsService _settingsService;
        private readonly SshService _ssh = new();
        private readonly DispatcherTimer _renderTimer;
        private readonly DispatcherTimer _healthTimer;
        private readonly StreamHealthTracker _topTracker;
        private readonly StreamHealthTracker _bottomTracker;
        private readonly ReplayBuffer _topReplay = new();
        private readonly ReplayBuffer _bottomReplay = new();
        private readonly AudioReplayBuffer _audioReplay = new();
        private AudioRecordingTap? _replayAudioTap;
        private CombinedRecordingSession? _combined;
        private CancellationTokenSource? _connectCts;

        // Auto-reconnect: last credentials used this session (memory only)
        // and the cancellation source for the backoff loop.
        private (string User, string Pass)? _sessionCreds;
        private bool _pendingRemember;
        private CancellationTokenSource? _reconnectCts;

        // Network stats deltas (previous cumulative counters per screen)
        private (long P, long L, long B) _topStatPrev, _bottomStatPrev;

        // Recording clock for the combined session's REC indicator
        private DateTime _combinedStartUtc;

        // GIF clips stay short — they balloon in size beyond ~10 s
        private const int GifSeconds = 10;

        // ── View-supplied interaction hooks ───────────────────────────
        public Func<string, (string User, string Pass, bool Remember)?>? PromptCredentials { get; set; }
        public Func<string, string, bool>? Confirm { get; set; }
        public event Action<ScreenViewModel>? FullscreenRequested;

        // ── Child view-models ─────────────────────────────────────────
        public ScreenViewModel Top { get; }
        public ScreenViewModel Bottom { get; }
        public AudioViewModel Audio { get; }
        public TimerViewModel Timer { get; }
        public LogViewModel Log { get; } = new();

        public AppSettings Settings => _settingsService.Current;

        // ── Connection state ──────────────────────────────────────────
        private ConnectionState _connection = ConnectionState.Disconnected;
        public ConnectionState Connection
        {
            get => _connection;
            private set
            {
                if (SetProperty(ref _connection, value))
                {
                    OnPropertyChanged(nameof(IsConnected));
                    OnPropertyChanged(nameof(IsDisconnected));
                    OnPropertyChanged(nameof(ConnectButtonText));
                    OnPropertyChanged(nameof(StatusDotBrush));
                }
            }
        }

        public bool IsConnected => Connection == ConnectionState.Connected;
        public bool IsDisconnected => Connection is ConnectionState.Disconnected or ConnectionState.Lost;

        public string ConnectButtonText => Connection switch
        {
            ConnectionState.Connecting => "Connecting...",
            ConnectionState.Connected => "Disconnect",
            ConnectionState.Lost => "Reconnect",
            _ => "Connect"
        };

        public Brush StatusDotBrush => Connection switch
        {
            ConnectionState.Connected => DotConnectedBrush,
            ConnectionState.Lost => DotLostBrush,
            _ => DotIdleBrush
        };

        private string _deviceIp;
        public string DeviceIp
        {
            get => _deviceIp;
            set => SetProperty(ref _deviceIp, value);
        }

        private string _sshPortText;
        public string SshPortText
        {
            get => _sshPortText;
            set => SetProperty(ref _sshPortText, value);
        }

        // ── Status bar ────────────────────────────────────────────────
        private string _statusMessage = "Not connected";
        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        private bool _statusIsError;
        public bool StatusIsError
        {
            get => _statusIsError;
            private set => SetProperty(ref _statusIsError, value);
        }

        // ── Layout / theme ────────────────────────────────────────────
        private LayoutMode _layout;
        public LayoutMode Layout
        {
            get => _layout;
            set
            {
                if (SetProperty(ref _layout, value))
                {
                    Settings.Layout = value.ToString();
                    OnPropertyChanged(nameof(IsLayoutVertical));
                    OnPropertyChanged(nameof(IsLayoutSideBySide));
                    OnPropertyChanged(nameof(IsLayoutTopOnly));
                    OnPropertyChanged(nameof(IsLayoutBottomOnly));
                    OnPropertyChanged(nameof(IsLayoutHybrid));
                }
            }
        }

        public bool IsLayoutVertical => Layout == LayoutMode.VerticalStack;
        public bool IsLayoutSideBySide => Layout == LayoutMode.SideBySide;
        public bool IsLayoutTopOnly => Layout == LayoutMode.TopOnly;
        public bool IsLayoutBottomOnly => Layout == LayoutMode.BottomOnly;
        public bool IsLayoutHybrid => Layout == LayoutMode.Hybrid;

        // ── Display preferences (swap / gap / rotation / filter) ─────
        public bool IsSwapped => Settings.SwapScreens;

        public int ScreenGap => Settings.ScreenGap;
        public bool IsGapNone => Settings.ScreenGap == 0;
        public bool IsGapSmall => Settings.ScreenGap == 4;
        public bool IsGapNormal => Settings.ScreenGap == 8;
        public bool IsGapWide => Settings.ScreenGap == 16;

        public double RotationAngle => Settings.Rotation;
        public bool IsRotation0 => Settings.Rotation == 0;
        public bool IsRotation90 => Settings.Rotation == 90;
        public bool IsRotation180 => Settings.Rotation == 180;
        public bool IsRotation270 => Settings.Rotation == 270;

        public BitmapScalingMode ScalingMode => Settings.SmoothScaling
            ? BitmapScalingMode.Fant
            : BitmapScalingMode.NearestNeighbor;
        public bool IsScalingSharp => !Settings.SmoothScaling;
        public bool IsScalingSmooth => Settings.SmoothScaling;

        public bool IsThemeDark => ThemeService.Current == AppTheme.Dark;
        public bool IsThemeLight => ThemeService.Current == AppTheme.Light;

        // ── Combined recording / instant replay ──────────────────────
        private bool _isCombinedRecording;
        public bool IsCombinedRecording
        {
            get => _isCombinedRecording;
            private set
            {
                if (SetProperty(ref _isCombinedRecording, value))
                    OnPropertyChanged(nameof(CombinedRecordButtonText));
            }
        }

        public string CombinedRecordButtonText =>
            IsCombinedRecording ? "⏹  Combined" : "⏺  Combined";

        public int ReplaySeconds => Settings.ReplaySeconds;
        public bool IsReplay15 => Settings.ReplaySeconds == 15;
        public bool IsReplay30 => Settings.ReplaySeconds == 30;
        public bool IsReplay60 => Settings.ReplaySeconds == 60;
        public bool IsReplay120 => Settings.ReplaySeconds == 120;

        // ── Quality preset / stats overlay ────────────────────────────
        public bool IsQualityLow => CurrentQuality == StreamQuality.Low;
        public bool IsQualityMedium => CurrentQuality == StreamQuality.Medium;
        public bool IsQualityHigh => CurrentQuality == StreamQuality.High;

        private StreamQuality CurrentQuality =>
            Enum.TryParse(Settings.Quality, out StreamQuality q) ? q : StreamQuality.Medium;

        private static int QualityToBps(StreamQuality q) => q switch
        {
            StreamQuality.Low => 1_000_000,
            StreamQuality.High => 4_000_000,
            _ => 2_000_000
        };

        public bool IsStatsVisible => Settings.ShowStats;

        // ── Commands ──────────────────────────────────────────────────
        public AsyncRelayCommand ConnectCommand { get; }
        public AsyncRelayCommand RestartTopCommand { get; }
        public AsyncRelayCommand RestartBottomCommand { get; }
        public AsyncRelayCommand RestartAllCommand { get; }
        public AsyncRelayCommand ShutdownCommand { get; }
        public AsyncRelayCommand RebootCommand { get; }
        public RelayCommand ScreenshotCommand { get; }
        public RelayCommand SetLayoutCommand { get; }
        public RelayCommand SetThemeCommand { get; }
        public RelayCommand FullscreenCommand { get; }
        public AsyncRelayCommand ToggleCombinedRecordingCommand { get; }
        public AsyncRelayCommand SaveReplayCommand { get; }
        public RelayCommand SetReplayLengthCommand { get; }
        public AsyncRelayCommand SetQualityCommand { get; }
        public RelayCommand ToggleStatsCommand { get; }
        public RelayCommand ForgetCredentialsCommand { get; }
        public RelayCommand ToggleSwapCommand { get; }
        public RelayCommand SetScreenGapCommand { get; }
        public RelayCommand SetRotationCommand { get; }
        public RelayCommand SetScalingCommand { get; }
        public AsyncRelayCommand SaveGifCommand { get; }

        // ─────────────────────────────────────────────────────────────
        public MainViewModel(SettingsService settingsService)
        {
            _settingsService = settingsService;
            var s = settingsService.Current;

            _deviceIp = s.DeviceIp;
            _sshPortText = s.SshPort.ToString();
            _layout = s.LayoutValue;

            Top = new ScreenViewModel(ScreenId.Top, 5000, AppendLog);
            Bottom = new ScreenViewModel(ScreenId.Bottom, 5001, AppendLog);
            Audio = new AudioViewModel(s, AppendLog);
            Timer = new TimerViewModel(AppendLog);

            _topTracker = new StreamHealthTracker(
                ScreenId.Top, Top.Receiver, RestartStreamCoreAsync, AppendLog);
            _bottomTracker = new StreamHealthTracker(
                ScreenId.Bottom, Bottom.Receiver, RestartStreamCoreAsync, AppendLog);

            // Replay buffers stay armed whenever the receivers run — no
            // pre-arming needed, which is the whole point of instant replay.
            _topReplay.CapacitySeconds = s.ReplaySeconds;
            _bottomReplay.CapacitySeconds = s.ReplaySeconds;
            _audioReplay.CapacitySeconds = s.ReplaySeconds;
            Top.Receiver.NalUnitReceived += _topReplay.OnNal;
            Bottom.Receiver.NalUnitReceived += _bottomReplay.OnNal;

            _ssh.VideoBitrateBps = QualityToBps(CurrentQuality);

            // Re-arm the replay audio tap if the input device changes mid-session.
            Audio.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AudioViewModel.SelectedInput) && IsConnected)
                {
                    _audioReplay.Clear();
                    StartReplayAudioTap();
                }
            };

            _ssh.StatusChanged += (msg, err) => AppendLog(msg, err);
            _ssh.ConnectionLost += OnConnectionLost;

            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _renderTimer.Tick += (_, _) =>
            {
                Top.RenderPendingFrame();
                Bottom.RenderPendingFrame();
            };

            _healthTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _healthTimer.Tick += (_, _) => HealthTick();

            ConnectCommand = new AsyncRelayCommand(ToggleConnectAsync);
            RestartTopCommand = new AsyncRelayCommand(
                () => ManualRestartAsync(ScreenId.Top), () => IsConnected);
            RestartBottomCommand = new AsyncRelayCommand(
                () => ManualRestartAsync(ScreenId.Bottom), () => IsConnected);
            RestartAllCommand = new AsyncRelayCommand(
                ManualRestartAllAsync, () => IsConnected);
            ShutdownCommand = new AsyncRelayCommand(ShutdownConsoleAsync, () => IsConnected);
            RebootCommand = new AsyncRelayCommand(RebootConsoleAsync, () => IsConnected);
            ScreenshotCommand = new RelayCommand(TakeScreenshot, () => IsConnected);
            SetLayoutCommand = new RelayCommand(p =>
            {
                if (Enum.TryParse(p?.ToString(), out LayoutMode mode)) Layout = mode;
            });
            SetThemeCommand = new RelayCommand(ApplyTheme);
            FullscreenCommand = new RelayCommand(p =>
            {
                if (p is ScreenViewModel screen) FullscreenRequested?.Invoke(screen);
            });
            ToggleCombinedRecordingCommand = new AsyncRelayCommand(
                ToggleCombinedRecordingAsync, () => IsConnected);
            SaveReplayCommand = new AsyncRelayCommand(SaveReplayAsync, () => IsConnected);
            SetReplayLengthCommand = new RelayCommand(SetReplayLength);
            SetQualityCommand = new AsyncRelayCommand(SetQualityAsync);
            ToggleStatsCommand = new RelayCommand(ToggleStats);
            ForgetCredentialsCommand = new RelayCommand(() =>
            {
                _sessionCreds = null;
                ForgetSavedCredentials(silent: false);
            });
            ToggleSwapCommand = new RelayCommand(ToggleSwap);
            SetScreenGapCommand = new RelayCommand(SetScreenGap);
            SetRotationCommand = new RelayCommand(SetRotation);
            SetScalingCommand = new RelayCommand(SetScaling);
            SaveGifCommand = new AsyncRelayCommand(SaveGifClipAsync, () => IsConnected);

            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Connection)) RefreshCommandStates();
            };
        }

        private void RefreshCommandStates()
        {
            RestartTopCommand.RaiseCanExecuteChanged();
            RestartBottomCommand.RaiseCanExecuteChanged();
            RestartAllCommand.RaiseCanExecuteChanged();
            ShutdownCommand.RaiseCanExecuteChanged();
            RebootCommand.RaiseCanExecuteChanged();
            ScreenshotCommand.RaiseCanExecuteChanged();
            ToggleCombinedRecordingCommand.RaiseCanExecuteChanged();
            SaveReplayCommand.RaiseCanExecuteChanged();
            SaveGifCommand.RaiseCanExecuteChanged();
        }

        // ─────────────────────────────────────────────────────────────
        // CONNECT / DISCONNECT
        // ─────────────────────────────────────────────────────────────
        private async Task ToggleConnectAsync()
        {
            if (IsConnected)
            {
                if (Confirm?.Invoke(
                        "This will stop all GStreamer streams on the DS and close the SSH connection.\n\nAre you sure?",
                        "Confirm Disconnect") != true)
                    return;
                await DisconnectAsync();
                return;
            }

            CancelAutoReconnect();

            string ip = DeviceIp.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                AppendLog("Enter a device IP address first.", true);
                return;
            }
            if (!int.TryParse(SshPortText.Trim(), out int port) || port < 1 || port > 65535)
            {
                AppendLog("Port must be a number between 1 and 65535.", true);
                return;
            }

            // Credentials, in preference order: DPAPI-saved → this session's
            // last-used → prompt the user.
            string user, pass;
            bool usedSaved = false;
            _pendingRemember = false;

            string? savedPass =
                Settings.RememberCredentials && !string.IsNullOrEmpty(Settings.ProtectedPassword)
                    ? CredentialStore.Unprotect(Settings.ProtectedPassword!)
                    : null;

            if (savedPass != null)
            {
                user = Settings.SshUsername;
                pass = savedPass;
                usedSaved = true;
            }
            else if (_sessionCreds != null)
            {
                (user, pass) = _sessionCreds.Value;
            }
            else
            {
                var creds = PromptCredentials?.Invoke(Settings.SshUsername);
                if (creds == null) return;
                (user, pass, _pendingRemember) = creds.Value;
            }

            string localIp = GetLocalIpAddress();
            if (localIp == "127.0.0.1")
            {
                AppendLog("Could not detect local IP. Are you on a network?", true);
                return;
            }

            bool ok = await ConnectCoreAsync(ip, port, user, pass, localIp, isReconnect: false);

            if (!ok && usedSaved && _ssh.LastFailureWasAuth)
            {
                ForgetSavedCredentials(silent: true);
                _sessionCreds = null;
                AppendLog("Saved credentials were rejected and have been cleared — click Connect to enter new ones.", true);
            }
        }

        /// <summary>Shared by manual connect and the auto-reconnect loop.</summary>
        private async Task<bool> ConnectCoreAsync(
            string ip, int port, string user, string pass, string localIp, bool isReconnect)
        {
            Connection = ConnectionState.Connecting;

            if (!isReconnect)
            {
                _topTracker.Reset();
                _bottomTracker.Reset();
                Top.Health = StreamHealth.Waiting;
                Bottom.Health = StreamHealth.Waiting;
            }

            try
            {
                StartReceivers();   // idempotent — already-running receivers are kept
            }
            catch (Exception ex)
            {
                AppendLog($"Receiver init failed: {ex.Message}", true);
                StopReceivers();
                Connection = ConnectionState.Disconnected;
                return false;
            }

            _connectCts = new CancellationTokenSource();
            bool ok;
            try
            {
                ok = await _ssh.ConnectAsync(ip, port, user, pass, localIp, _connectCts.Token);
            }
            catch (Exception ex)
            {
                AppendLog($"Connect exception: {ex.Message}", true);
                ok = false;
            }

            if (!ok)
            {
                if (isReconnect)
                {
                    // Keep receivers alive between retries — the device may
                    // still be streaming even though the SSH link dropped.
                    Connection = ConnectionState.Lost;
                }
                else
                {
                    StopReceivers();
                    Connection = ConnectionState.Disconnected;
                }
                return false;
            }

            Connection = ConnectionState.Connected;
            _sessionCreds = (user, pass);
            _healthTimer.Start();

            if (isReconnect)
            {
                // The connect sequence restarted the device pipelines, so give
                // the trackers a grace window before freeze detection resumes.
                _topTracker.NotifyManualRestart();
                _bottomTracker.NotifyManualRestart();
            }

            Settings.DeviceIp = ip;
            Settings.SshPort = port;
            Settings.SshUsername = user;
            if (_pendingRemember)
            {
                _pendingRemember = false;
                string? cipher = CredentialStore.Protect(pass);
                if (cipher != null)
                {
                    Settings.RememberCredentials = true;
                    Settings.ProtectedPassword = cipher;
                    AppendLog("Credentials saved for this PC (DPAPI-encrypted).");
                }
                else
                {
                    AppendLog("Could not encrypt credentials — not saved.", true);
                }
            }
            _settingsService.Save();

            StartReplayAudioTap();

            AppendLog($"Connected to {ip}. Video via RTP. Audio: connect 3.5mm and click ▶ Audio.");
            if (!Log.IsOpen) Log.IsOpen = true;
            return true;
        }

        // ── Auto-reconnect with backoff ───────────────────────────────
        private void StartAutoReconnect()
        {
            if (_sessionCreds == null) return;
            CancelAutoReconnect();
            var cts = new CancellationTokenSource();
            _reconnectCts = cts;
            _ = RunAutoReconnectAsync(cts.Token);
        }

        private async Task RunAutoReconnectAsync(CancellationToken ct)
        {
            int[] delaysSec = { 3, 6, 12, 24, 30 };
            for (int attempt = 0; attempt < delaysSec.Length; attempt++)
            {
                AppendLog($"[SSH] Reconnecting in {delaysSec[attempt]}s (attempt {attempt + 1}/{delaysSec.Length})...");
                try
                {
                    await Task.Delay(delaysSec[attempt] * 1000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (ct.IsCancellationRequested || Connection == ConnectionState.Connected) return;

                var creds = _sessionCreds;
                if (creds == null) return;
                if (!int.TryParse(SshPortText.Trim(), out int port)) return;

                string localIp = GetLocalIpAddress();
                if (localIp == "127.0.0.1") continue;

                bool ok = await ConnectCoreAsync(
                    DeviceIp.Trim(), port, creds.Value.User, creds.Value.Pass, localIp,
                    isReconnect: true);
                if (ok)
                {
                    AppendLog("[SSH] Reconnected.");
                    return;
                }
                if (ct.IsCancellationRequested) return;
                if (_ssh.LastFailureWasAuth)
                {
                    ForgetSavedCredentials(silent: true);
                    _sessionCreds = null;
                    AppendLog("[SSH] Credentials rejected during reconnect — stopped retrying.", true);
                    return;
                }
            }
            AppendLog("[SSH] Auto-reconnect failed — click Reconnect to try again.", true);
        }

        private void CancelAutoReconnect()
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }

        private void ForgetSavedCredentials(bool silent)
        {
            Settings.RememberCredentials = false;
            Settings.ProtectedPassword = null;
            _settingsService.Save();
            if (!silent) AppendLog("Saved credentials cleared.");
        }

        public async Task DisconnectAsync()
        {
            CancelAutoReconnect();
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;

            StopReplayAudioTap();
            _audioReplay.Clear();

            _healthTimer.Stop();
            await StopCombinedRecordingAsync();
            await Top.StopRecordingAsync();
            await Bottom.StopRecordingAsync();
            StopReceivers();
            await _ssh.DisconnectAsync();

            _topReplay.Clear();
            _bottomReplay.Clear();
            _topTracker.Reset();
            _bottomTracker.Reset();
            Top.Health = StreamHealth.Waiting;
            Bottom.Health = StreamHealth.Waiting;
            Top.FpsText = string.Empty;
            Bottom.FpsText = string.Empty;
            Top.ClearFrame();
            Bottom.ClearFrame();

            Connection = ConnectionState.Disconnected;
            AppendLog("Disconnected — streams stopped on device.");
        }

        private void OnConnectionLost()
        {
            var app = System.Windows.Application.Current;
            app?.Dispatcher.BeginInvoke(() =>
            {
                if (Connection != ConnectionState.Connected) return;
                AppendLog("[SSH] Connection lost.", true);
                _healthTimer.Stop();
                Connection = ConnectionState.Lost;
                Top.Health = StreamHealth.Frozen;
                Bottom.Health = StreamHealth.Frozen;
                StartAutoReconnect();
            });
        }

        private void StartReceivers()
        {
            Top.Receiver.Start();
            Bottom.Receiver.Start();
            _renderTimer.Start();
            AppendLog("UDP receivers open on ports 5000 / 5001. Waiting for frames...");
        }

        private void StopReceivers()
        {
            _renderTimer.Stop();
            Top.Receiver.Stop();
            Bottom.Receiver.Stop();
        }

        // ─────────────────────────────────────────────────────────────
        // STREAM HEALTH / RESTARTS
        // ─────────────────────────────────────────────────────────────
        private void HealthTick()
        {
            if (!IsConnected || !_ssh.IsConnected) return;

            _topTracker.Tick();
            _bottomTracker.Tick();
            Top.Health = _topTracker.Health;
            Bottom.Health = _bottomTracker.Health;

            Top.FpsText = Top.Health == StreamHealth.Live
                ? $"{Top.Receiver.CurrentFps:F0} fps" : string.Empty;
            Bottom.FpsText = Bottom.Health == StreamHealth.Live
                ? $"{Bottom.Receiver.CurrentFps:F0} fps" : string.Empty;

            UpdateStats(Top, ref _topStatPrev);
            UpdateStats(Bottom, ref _bottomStatPrev);

            var now = DateTime.UtcNow;
            UpdateRecIndicator(Top, now);
            UpdateRecIndicator(Bottom, now);
        }

        private void UpdateRecIndicator(ScreenViewModel screen, DateTime nowUtc)
        {
            if (IsCombinedRecording)
                screen.RecText = "● REC " + FormatElapsed(nowUtc - _combinedStartUtc);
            else if (screen.IsRecording)
                screen.RecText = "● REC " + FormatElapsed(nowUtc - screen.RecordingStartUtc);
            else if (screen.RecText.Length != 0)
                screen.RecText = string.Empty;
        }

        private static string FormatElapsed(TimeSpan t) =>
            t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";

        private Task RestartStreamCoreAsync(ScreenId screen)
            => _ssh.RestartStreamAsync(screen);

        private async Task ManualRestartAsync(ScreenId screen)
        {
            if (!IsConnected) return;
            AppendLog($"[MANUAL] Restarting {(screen == ScreenId.Top ? "top" : "bottom")} stream...");
            (screen == ScreenId.Top ? _topTracker : _bottomTracker).NotifyManualRestart();
            await RestartStreamCoreAsync(screen);
        }

        private async Task ManualRestartAllAsync()
        {
            if (!IsConnected) return;
            AppendLog("[MANUAL] Restarting ALL streams...");
            _topTracker.NotifyManualRestart();
            _bottomTracker.NotifyManualRestart();
            await RestartStreamCoreAsync(ScreenId.Top);
            await RestartStreamCoreAsync(ScreenId.Bottom);
        }

        // ─────────────────────────────────────────────────────────────
        // COMBINED RECORDING (both screens + audio, one MP4)
        // ─────────────────────────────────────────────────────────────
        private async Task ToggleCombinedRecordingAsync()
        {
            if (_combined != null)
            {
                await StopCombinedRecordingAsync();
                return;
            }

            var session = CombinedRecordingService.Start(
                Top.Receiver, Bottom.Receiver,
                Audio.SelectedInput?.Index, AppendLog);
            if (session == null) return;

            session.Failed += OnCombinedRecordingFailed;
            _combined = session;
            _combinedStartUtc = DateTime.UtcNow;
            IsCombinedRecording = true;
        }

        private async Task StopCombinedRecordingAsync()
        {
            var session = _combined;
            _combined = null;
            if (session != null)
            {
                session.Failed -= OnCombinedRecordingFailed;
                await session.StopAsync();
                session.Dispose();
            }
            IsCombinedRecording = false;

            var now = DateTime.UtcNow;
            UpdateRecIndicator(Top, now);
            UpdateRecIndicator(Bottom, now);
        }

        private void OnCombinedRecordingFailed()
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                if (_combined == null) return;
                AppendLog("[RECORD] Combined recording stopped unexpectedly.", true);
                var session = _combined;
                _combined = null;
                IsCombinedRecording = false;
                session.Failed -= OnCombinedRecordingFailed;
                await Task.Run(session.Dispose);
            });
        }

        // ─────────────────────────────────────────────────────────────
        // INSTANT REPLAY
        // ─────────────────────────────────────────────────────────────
        private async Task SaveReplayAsync()
        {
            if (!IsConnected) return;
            await ReplayService.SaveAsync(
                _topReplay, _bottomReplay, _audioReplay, Settings.ReplaySeconds, AppendLog);
        }

        private async Task SaveGifClipAsync()
        {
            if (!IsConnected) return;
            await ReplayService.SaveGifAsync(
                _topReplay, _bottomReplay,
                Math.Min(GifSeconds, Settings.ReplaySeconds), AppendLog);
        }

        /// <summary>
        /// Keeps an independent Line-In capture running while connected so
        /// instant replays include audio. Failure degrades to video-only.
        /// </summary>
        private void StartReplayAudioTap()
        {
            StopReplayAudioTap();

            var device = Audio.SelectedInput;
            if (device == null)
            {
                AppendLog("[REPLAY] No audio input device — replays will be video-only.");
                return;
            }

            try
            {
                _replayAudioTap = new AudioRecordingTap(device.Index);
                _replayAudioTap.DataAvailable += _audioReplay.OnPcm;
            }
            catch (Exception ex)
            {
                _replayAudioTap = null;
                AppendLog($"[REPLAY] Audio capture unavailable ({ex.Message}) — replays will be video-only.", true);
            }
        }

        private void StopReplayAudioTap()
        {
            if (_replayAudioTap == null) return;
            _replayAudioTap.DataAvailable -= _audioReplay.OnPcm;
            _replayAudioTap.Dispose();
            _replayAudioTap = null;
        }

        private void SetReplayLength(object? parameter)
        {
            if (!int.TryParse(parameter?.ToString(), out int seconds)) return;

            Settings.ReplaySeconds = seconds;
            _topReplay.CapacitySeconds = seconds;
            _bottomReplay.CapacitySeconds = seconds;
            _audioReplay.CapacitySeconds = seconds;
            _settingsService.Save();

            OnPropertyChanged(nameof(ReplaySeconds));
            OnPropertyChanged(nameof(IsReplay15));
            OnPropertyChanged(nameof(IsReplay30));
            OnPropertyChanged(nameof(IsReplay60));
            OnPropertyChanged(nameof(IsReplay120));
            AppendLog($"[REPLAY] Buffer length set to {seconds} seconds.");
        }

        // ─────────────────────────────────────────────────────────────
        // DISPLAY PREFERENCES (swap / gap / rotation / filter)
        // ─────────────────────────────────────────────────────────────
        private void ToggleSwap()
        {
            Settings.SwapScreens = !Settings.SwapScreens;
            _settingsService.Save();
            OnPropertyChanged(nameof(IsSwapped));
        }

        private void SetScreenGap(object? parameter)
        {
            if (!int.TryParse(parameter?.ToString(), out int gap)) return;
            Settings.ScreenGap = gap;
            _settingsService.Save();
            OnPropertyChanged(nameof(ScreenGap));
            OnPropertyChanged(nameof(IsGapNone));
            OnPropertyChanged(nameof(IsGapSmall));
            OnPropertyChanged(nameof(IsGapNormal));
            OnPropertyChanged(nameof(IsGapWide));
        }

        private void SetRotation(object? parameter)
        {
            if (!int.TryParse(parameter?.ToString(), out int degrees)) return;
            if (degrees is not (0 or 90 or 180 or 270)) return;
            Settings.Rotation = degrees;
            _settingsService.Save();
            OnPropertyChanged(nameof(RotationAngle));
            OnPropertyChanged(nameof(IsRotation0));
            OnPropertyChanged(nameof(IsRotation90));
            OnPropertyChanged(nameof(IsRotation180));
            OnPropertyChanged(nameof(IsRotation270));
        }

        private void SetScaling(object? parameter)
        {
            Settings.SmoothScaling = parameter?.ToString() == "Smooth";
            _settingsService.Save();
            OnPropertyChanged(nameof(ScalingMode));
            OnPropertyChanged(nameof(IsScalingSharp));
            OnPropertyChanged(nameof(IsScalingSmooth));
        }

        // ─────────────────────────────────────────────────────────────
        // QUALITY PRESET / STATS OVERLAY
        // ─────────────────────────────────────────────────────────────
        private async Task SetQualityAsync(object? parameter)
        {
            if (!Enum.TryParse(parameter?.ToString(), out StreamQuality quality)) return;
            if (quality == CurrentQuality) return;

            Settings.Quality = quality.ToString();
            _ssh.VideoBitrateBps = QualityToBps(quality);
            _settingsService.Save();

            OnPropertyChanged(nameof(IsQualityLow));
            OnPropertyChanged(nameof(IsQualityMedium));
            OnPropertyChanged(nameof(IsQualityHigh));

            double mbps = QualityToBps(quality) / 1_000_000.0;
            if (IsConnected)
            {
                AppendLog($"[QUALITY] {quality} ({mbps:F0} Mbps per screen) — restarting streams...");
                await ManualRestartAllAsync();
            }
            else
            {
                AppendLog($"[QUALITY] {quality} ({mbps:F0} Mbps per screen) — takes effect on next connect.");
            }
        }

        private void ToggleStats()
        {
            Settings.ShowStats = !Settings.ShowStats;
            _settingsService.Save();
            OnPropertyChanged(nameof(IsStatsVisible));
            if (!Settings.ShowStats)
            {
                Top.StatsText = string.Empty;
                Bottom.StatsText = string.Empty;
            }
        }

        private void UpdateStats(ScreenViewModel screen, ref (long P, long L, long B) prev)
        {
            var (packets, lost, bytes) = screen.Receiver.GetStats();
            long dp = packets - prev.P;
            long dl = lost - prev.L;
            long db = bytes - prev.B;
            prev = (packets, lost, bytes);

            if (!Settings.ShowStats)
            {
                if (screen.StatsText.Length != 0) screen.StatsText = string.Empty;
                return;
            }

            if (dp + dl <= 0)
            {
                screen.StatsText = "no data";
                return;
            }

            double lossPct = 100.0 * dl / (dp + dl);
            double mbps = db * 8 / 1_000_000.0;
            screen.StatsText = $"{mbps:F1} Mbps · {lossPct:F1}% loss";
        }

        // ─────────────────────────────────────────────────────────────
        // CONSOLE POWER
        // ─────────────────────────────────────────────────────────────
        private async Task ShutdownConsoleAsync()
        {
            if (Confirm?.Invoke("Shut down the console?", "Shutdown Console") != true) return;
            AppendLog("[POWER] Shutdown command sent.", true);
            await _ssh.ShutdownConsoleAsync();
            await Task.Delay(1000);
            await DisconnectAsync();
        }

        private async Task RebootConsoleAsync()
        {
            if (Confirm?.Invoke("Reboot the console?", "Reboot Console") != true) return;
            AppendLog("[POWER] Reboot command sent.", true);
            await _ssh.RebootConsoleAsync();
            await Task.Delay(1000);
            await DisconnectAsync();
        }

        // ─────────────────────────────────────────────────────────────
        // SCREENSHOT / THEME
        // ─────────────────────────────────────────────────────────────
        private void TakeScreenshot()
        {
            try
            {
                int saved = ScreenshotService.SaveAll(Top.Bitmap, Bottom.Bitmap);
                AppendLog(saved > 0
                    ? $"[SCREENSHOT] {saved} image(s) → {AppPaths.ScreenshotsDir}"
                    : "[SCREENSHOT] No frames to capture yet.");
            }
            catch (Exception ex)
            {
                AppendLog($"[SCREENSHOT] Failed: {ex.Message}", true);
            }
        }

        private void ApplyTheme(object? parameter)
        {
            if (!Enum.TryParse(parameter?.ToString(), out AppTheme theme)) return;
            ThemeService.Apply(theme);
            Settings.Theme = theme.ToString();
            _settingsService.Save();
            OnPropertyChanged(nameof(IsThemeDark));
            OnPropertyChanged(nameof(IsThemeLight));
            AppendLog($"[THEME] {theme}");
        }

        // ─────────────────────────────────────────────────────────────
        // SHUTDOWN
        // ─────────────────────────────────────────────────────────────
        /// <summary>True if it is safe to close without confirmation.</summary>
        public bool CanCloseSilently => !IsConnected;

        public async Task ShutdownAsync()
        {
            CancelAutoReconnect();
            _connectCts?.Cancel();
            _healthTimer.Stop();
            _renderTimer.Stop();
            Timer.Shutdown();
            StopReplayAudioTap();

            await StopCombinedRecordingAsync();
            await Top.StopRecordingAsync();
            await Bottom.StopRecordingAsync();
            Audio.Stop();
            StopReceivers();
            await _ssh.DisconnectAsync();

            Top.Dispose();
            Bottom.Dispose();
            Audio.Dispose();
            _ssh.Dispose();

            Settings.DeviceIp = DeviceIp.Trim();
            if (int.TryParse(SshPortText.Trim(), out int port)) Settings.SshPort = port;
            _settingsService.Save();
        }

        // ─────────────────────────────────────────────────────────────
        private void AppendLog(string message, bool isError = false)
        {
            Log.Append(message, isError);

            var app = System.Windows.Application.Current;
            if (app == null) return;
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(() => SetStatus(message, isError));
                return;
            }
            SetStatus(message, isError);
        }

        private void SetStatus(string message, bool isError)
        {
            StatusMessage = message;
            StatusIsError = isError;
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                // Find the first active, non-loopback IPv4 interface.
                // This avoids any external network calls.
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus != OperationalStatus.Up) continue;
                    if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var info = iface.GetIPProperties();
                    var ipv4 = info.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

                    if (ipv4 != null) return ipv4.Address.ToString();
                }

                return "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private static Brush Frozen(Brush b)
        {
            b.Freeze();
            return b;
        }
    }
}
