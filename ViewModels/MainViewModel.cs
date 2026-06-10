using System;
using System.Net;
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
        private CancellationTokenSource? _connectCts;

        // ── View-supplied interaction hooks ───────────────────────────
        public Func<string, (string User, string Pass)?>? PromptCredentials { get; set; }
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
                }
            }
        }

        public bool IsLayoutVertical => Layout == LayoutMode.VerticalStack;
        public bool IsLayoutSideBySide => Layout == LayoutMode.SideBySide;
        public bool IsLayoutTopOnly => Layout == LayoutMode.TopOnly;
        public bool IsLayoutBottomOnly => Layout == LayoutMode.BottomOnly;

        public bool IsThemeDark => ThemeService.Current == AppTheme.Dark;
        public bool IsThemeLight => ThemeService.Current == AppTheme.Light;

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

            var creds = PromptCredentials?.Invoke(Settings.SshUsername);
            if (creds == null) return;

            string localIp = GetLocalIpAddress();
            if (localIp == "127.0.0.1")
            {
                AppendLog("Could not detect local IP. Are you on a network?", true);
                return;
            }

            Connection = ConnectionState.Connecting;
            _topTracker.Reset();
            _bottomTracker.Reset();
            Top.Health = StreamHealth.Waiting;
            Bottom.Health = StreamHealth.Waiting;

            try
            {
                StartReceivers();
            }
            catch (Exception ex)
            {
                AppendLog($"Receiver init failed: {ex.Message}", true);
                StopReceivers();
                Connection = ConnectionState.Disconnected;
                return;
            }

            _connectCts = new CancellationTokenSource();
            bool ok;
            try
            {
                ok = await _ssh.ConnectAsync(
                    ip, port, creds.Value.User, creds.Value.Pass, localIp, _connectCts.Token);
            }
            catch (Exception ex)
            {
                AppendLog($"Connect exception: {ex.Message}", true);
                ok = false;
            }

            if (ok)
            {
                Connection = ConnectionState.Connected;
                _healthTimer.Start();

                Settings.DeviceIp = ip;
                Settings.SshPort = port;
                Settings.SshUsername = creds.Value.User;
                _settingsService.Save();

                AppendLog($"Connected to {ip}. Video via RTP. Audio: connect 3.5mm and click ▶ Audio.");
                if (!Log.IsOpen) Log.IsOpen = true;
            }
            else
            {
                StopReceivers();
                Connection = ConnectionState.Disconnected;
            }
        }

        public async Task DisconnectAsync()
        {
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = null;

            _healthTimer.Stop();
            await Top.StopRecordingAsync();
            await Bottom.StopRecordingAsync();
            StopReceivers();
            await _ssh.DisconnectAsync();

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
        }

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
            _connectCts?.Cancel();
            _healthTimer.Stop();
            _renderTimer.Stop();
            Timer.Shutdown();

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
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
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
