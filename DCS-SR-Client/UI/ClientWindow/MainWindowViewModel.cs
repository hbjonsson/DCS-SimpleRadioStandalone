using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Threading;
using Caliburn.Micro;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Audio.Managers;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Network.DCS;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Properties;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Settings.Favourites;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Singletons;
using Ciribob.DCS.SimpleRadio.Standalone.Client.UI.ClientWindow.ClientList;
using Ciribob.DCS.SimpleRadio.Standalone.Client.UI.ClientWindow.Favourites;
using Ciribob.DCS.SimpleRadio.Standalone.Client.UI.ClientWindow.RadioOverlayWindow;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Utils;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Audio.Utility;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Helpers;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.EventMessages;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Models.Player;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network.Client;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network.Singletons;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Settings.Setting;
using NLog;
using Application = System.Windows.Application;
using AwaRadioOverlayWindow =
    Ciribob.DCS.SimpleRadio.Standalone.Client.UI.ClientWindow.AwacsRadioOverlayWindow.AwaRadioOverlayWindow;
using LogManager = NLog.LogManager;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.UI.ClientWindow;

public class MainWindowViewModel : PropertyChangedBaseClass, IHandle<TCPClientStatusMessage>,
    IHandle<VOIPStatusMessage>, IHandle<ProfileChangedMessage>, IHandle<EAMConnectedMessage>,
    IHandle<EAMDisconnectMessage>, IHandle<ServerSettingsUpdatedMessage>, IHandle<AutoConnectMessage>,
    IHandle<ToogleAwacsRadioOverlayMessage>, IHandle<ToggleSingleStackRadioOverlayMessage>
{
    private static readonly long OVERLAY_DEBOUNCE = 500;
    private static readonly int ReconnectBackoffCapSeconds = 30;
    private readonly AudioManager _audioManager;

    private readonly GlobalSettingsStore _globalSettings = GlobalSettingsStore.Instance;

    private readonly DispatcherTimer _updateTimer;
    private readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly object _reconnectLock = new();

    private AudioPreview _audioPreview;
    private AwaRadioOverlayWindow _awacsRadioOverlay;
    private TCPClientHandler _client;

    private ClientListWindow _clientListWindow;
    private DCSRadioSyncManager _dcsManager;
    private ServerAddress _selectedServerAddress;

    private ServerSettingsWindow.ServerSettingsWindow _serverSettingsWindow;

    private RadioOverlayWindow.RadioOverlayWindow _singleRadioOverlay;

    private long lastAwacsToggleTime;
    private long lastRadioToggleTime;

    private bool _wantConnected;
    private bool _hadSuccessfulConnection;
    private IPEndPoint _lastEndpoint;
    private int _reconnectAttempt;
    private CancellationTokenSource _reconnectCts;
    private bool _isReconnecting;
    /// <summary>
    /// When true, connection lifecycle must not steal focus (no TaskDialogs / BringToFront).
    /// Used for auto-reconnect while the user may be in-cockpit.
    /// </summary>
    private bool _suppressForegroundDialogs;

    public MainWindowViewModel()
    {
        _audioManager = new AudioManager(AudioOutput.WindowsN);

        PositionClickCommand = new DelegateCommand(() =>
        {
            var pos = ClientState.PlayerCoaltionLocationMetadata.LngLngPosition;

            try
            {
                Process.Start(new ProcessStartInfo($"https://maps.google.com/maps?q=loc:{pos.lat},{pos.lng}")
                    { UseShellExecute = true });
            }
            catch (Exception)
            {
                // ignored
            }
        });
        PreviewCommand = new DelegateCommand(PreviewAudio);

        ConnectCommand = new DelegateCommand(async () => await ConnectAsync());

        TrayIconCommand = new DelegateCommand(() =>
        {
            App.Current.MainWindow.Show();
            App.Current.MainWindow.WindowState = WindowState.Normal;
        });

        SingleStackOverlayCommand = new DelegateCommand(SingleRadioStackOverlay);

        AwacsRadioOverlayCommand = new DelegateCommand(MultiRadioOverlay);

        TrayIconQuitCommand = new DelegateCommand(() => { App.Current.Shutdown(); });

        //TODO might not need to do this - should be triggered by notifyproperty
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _updateTimer.Tick += UpdatePlayerCountAndVUMeters;
        _updateTimer.Start();

        ClientStateSingleton.Instance.DcsPlayerRadioInfo.name = EAMName;

        EventBus.Instance.SubscribeOnUIThread(this);

        ServerSettingsCommand = new DelegateCommand(ToggleServerSettings);

        ClientListCommand = new DelegateCommand(ToggleClientList);

        EAMConnectCommand = new DelegateCommand(() =>
        {
            if (ClientStateSingleton.Instance.ExternalAWACSModeConnected)
            {
                EventBus.Instance.PublishOnUIThreadAsync(new EAMDisconnectMessage());
            }
            else
            {
                EventBus.Instance.PublishOnBackgroundThreadAsync(new EAMConnectRequestMessage()
                {
                    Password = EAMPassword,
                    Name = EAMName
                });
            }
        });

        DonateCommand = new DelegateCommand(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.patreon.com/ciribob")
                    { UseShellExecute = true });
            }
            catch (Exception)
            {
                // ignored
            }
        });
    }


    public ICommand ClientListCommand { get; set; }

    public ICommand ServerSettingsCommand { get; set; }

    public DelegateCommand AwacsRadioOverlayCommand { get; set; }

    public ClientStateSingleton ClientState { get; } = ClientStateSingleton.Instance;
    public ConnectedClientsSingleton Clients { get; } = ConnectedClientsSingleton.Instance;
    public AudioInputSingleton AudioInput { get; } = AudioInputSingleton.Instance;
    public AudioOutputSingleton AudioOutput { get; } = AudioOutputSingleton.Instance;

    public InputDeviceManager InputManager { get; set; }

    public bool IsEAMAvailable => ClientStateSingleton.Instance.IsConnected &&
                                  SyncedServerSettings.Instance.GetSettingAsBool(ServerSettingsKeys
                                      .EXTERNAL_AWACS_MODE);

    public bool IsConnected { get; set; }

    public bool IsReconnecting
    {
        get => _isReconnecting;
        set
        {
            if (_isReconnecting == value) return;
            _isReconnecting = value;
            NotifyPropertyChanged();
            NotifyPropertyChanged(nameof(ConnectText));
            NotifyPropertyChanged(nameof(IsNotConnected));
            NotifyPropertyChanged(nameof(PreviewEnabled));
            NotifyPropertyChanged(nameof(ReconnectStatusText));
        }
    }

    //WPF cant invert a binding - this controls the input box for address
    public bool IsNotConnected => !IsConnected && !IsReconnecting;

    public bool IsVoIPConnected { get; set; }

    public string ReconnectStatusText { get; set; } = string.Empty;

    public DelegateCommand TrayIconQuitCommand { get; set; }

    public DelegateCommand TrayIconCommand { get; set; }

    public DelegateCommand ConnectCommand { get; set; }

    public ICommand PreviewCommand { get; set; }

    public ICommand SingleStackOverlayCommand { get; set; }

    public bool PreviewEnabled => AudioInput.MicrophoneAvailable && !IsConnected && !IsReconnecting;

    public float SpeakerVU
    {
        get
        {
            if (_audioPreview != null && _audioPreview.IsPreviewing)
                return _audioPreview.SpeakerMax;
            if (_audioManager != null) return _audioManager.SpeakerMax;

            return -100;
        }
    }

    public float MicVU
    {
        get
        {
            if (_audioPreview != null && _audioPreview.IsPreviewing)
                return _audioPreview.MicMax;
            if (_audioManager != null) return _audioManager.MicMax;

            return -100;
        }
    }


    public string PreviewText
    {
        get
        {
            if (_audioPreview == null || !_audioPreview.IsPreviewing || IsConnected)
                return Resources.PreviewAudio;
            return Resources.PreviewAudioStop;
        }
    }

    public IReadOnlyList<string> PreviewAircraftOptions { get; } =
    [
        "f16", "f15", "a10", "fa18", "jet", "uh60", "uh1", "ah64", "mi8", "ch47",
        "hercules", "m2000", "jf17", "f14", "a4", "prop", "spitfire", "bf109"
    ];

    public string SelectedPreviewAircraft
    {
        get
        {
            var raw = _globalSettings.ProfileSettingsStore.GetClientSettingString(
                ProfileSettingsKeys.PreviewAircraft);
            return string.IsNullOrWhiteSpace(raw) ? "f16" : raw.Trim().ToLowerInvariant();
        }
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "f16" : value.Trim().ToLowerInvariant();
            _globalSettings.ProfileSettingsStore.SetClientSettingString(
                ProfileSettingsKeys.PreviewAircraft, normalized);
            if (_audioPreview != null)
                _audioPreview.PreviewAircraft = normalized;
            NotifyPropertyChanged(nameof(SelectedPreviewAircraft));
        }
    }

    public string ConnectText
    {
        get
        {
            if (IsConnected)
                return Resources.StartStopDisconnect;
            if (IsReconnecting)
                return Resources.StartStopConnecting;
            return Resources.StartStop;
        }
    }

    public double SpeakerBoost
    {
        get
        {
            var boost = _globalSettings.GetClientSetting(GlobalSettingsKeys.SpeakerBoost).DoubleValue;
            _audioManager.SpeakerBoost = VolumeConversionHelper.ConvertVolumeSliderToScale((float)boost);
            if (_audioPreview != null) _audioPreview.SpeakerBoost = _audioManager.SpeakerBoost;
            return boost;
        }
        set
        {
            _globalSettings.SetClientSetting(GlobalSettingsKeys.SpeakerBoost,
                value.ToString(CultureInfo.InvariantCulture));
            _audioManager.SpeakerBoost = VolumeConversionHelper.ConvertVolumeSliderToScale((float)value);

            if (_audioPreview != null) _audioPreview.SpeakerBoost = _audioManager.SpeakerBoost;
            // NotifyPropertyChanged();
            // NotifyPropertyChanged("SpeakerBoostText");
        }
    }

    public string SpeakerBoostText =>
        VolumeConversionHelper.ConvertLinearDiffToDB(
            VolumeConversionHelper.ConvertVolumeSliderToScale((float)SpeakerBoost));

    public ServerAddress SelectedServerAddress
    {
        get => _selectedServerAddress;
        set
        {
            ServerAddress = value.Address;
            EAMPassword = value.EAMCoalitionPassword;

            _selectedServerAddress = value;
        }
    }

    public string EAMPassword { get; set; }

    public string ServerAddress
    {
        get
        {
            var savedAddress = _globalSettings.GetClientSetting(GlobalSettingsKeys.LastServer);

            if (savedAddress == null)
                return "127.0.0.1:5002";
            return savedAddress.RawValue;
        }
        set
        {
            if (value != null)
            {
                _globalSettings.SetClientSetting(GlobalSettingsKeys.LastServer, value);
                NotifyPropertyChanged();
            }
        }
    }

    public string EAMName
    {
        get { return ClientState.LastSeenName; }
        set
        {
            if (value != null)
            {
                ClientState.LastSeenName = value;
            }
        }
    }

    public bool AudioSettingsEnabled
    {
        get
        {
            if ((_audioPreview != null && _audioPreview.IsPreviewing) || IsConnected) return false;

            return true;
        }
    }

    public String CurrentProfile
    {
        get { return _globalSettings.ProfileSettingsStore.CurrentProfileName; }
    }

    public bool ConnectIsEnabled { get; set; } = true;
    public FavouriteServersViewModel FavouriteServersViewModel { get; set; }

    public string CurrentUnit => ClientState.DcsPlayerRadioInfo.unit;

    public string LastKnownPosition =>
        ClientState.PlayerCoaltionLocationMetadata.LngLngPosition.ToString();

    public DelegateCommand PositionClickCommand { get; }
    public DelegateCommand EAMConnectCommand { get; }
    public DelegateCommand DonateCommand { get; }

    public string EAMConnectButtonText
    {
        get
        {
            if (ClientStateSingleton.Instance.ExternalAWACSModeConnected)
            {
                return Resources.DisconnectExternalAWACSMode;
            }

            return Resources.ConnectExternalAWACSMode;
        }
    }

    public async Task HandleAsync(AutoConnectMessage message, CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            if (ServerAddress == message.Address)
            {
                Logger.Info(
                    $"Ignoring auto connect message as we are already connected to the server {message.Address}");
            }
            else
            {
                // Show auto connect mismatch prompt if setting has been enabled (default), otherwise automatically switch server
                bool showPrompt = _globalSettings.GetClientSettingBool(GlobalSettingsKeys.AutoConnectMismatchPrompt);

                bool switchServer = !showPrompt;
                if (showPrompt)
                {
                    WindowHelper.BringProcessToFront(Process.GetCurrentProcess());
                    var result = await await Application.Current.Dispatcher.InvokeAsync(async () =>
                        await TaskDialog.ShowDialogAsync(WindowHelper.GetForegroundWindow(), new TaskDialogPage
                        {
                            Caption = Resources.MsgBoxMismatch,
                            // Would you like to connect to the advertised server?
                            Heading = $"{Resources.MsgBoxMismatchText4}",
                            // Detailed of wants to connect to vs which we are currently connected to.
                            Text =
                                $"{Resources.MsgBoxMismatchText1} {message.Address}\n{Resources.MsgBoxMismatchText2} {ServerAddress}\n{Resources.MsgBoxMismatchText3}",
                            Icon = TaskDialogIcon.Warning,
                            Buttons = [TaskDialogButton.Yes, TaskDialogButton.No],
                            SizeToContent = true,
                        })
                    );

                    switchServer = result == TaskDialogButton.Yes;
                }

                if (switchServer)
                {
                    Logger.Info($"Switching from {ServerAddress} to {message.Address}");
                    ConnectIsEnabled = false;
                    ServerAddress = message.Address;
                    await DisconnectUserInitiatedAsync();
                    ConnectIsEnabled = false;
                    await Task.Delay(2000);
                    await ConnectAsync();
                }
            }
        }
        else if (!IsReconnecting)
        {
            Logger.Info($"Received auto connect message for {message.Address} - Connecting");
            ServerAddress = message.Address;
            await ConnectAsync();
        }
    }

    public async Task HandleAsync(EAMConnectedMessage message, CancellationToken cancellationToken)
    {
        ClientStateSingleton.Instance.LastSeenName = EAMName;

        //Notify after it started - hack
        _ = Task.Run(async Task () =>
        {
            await Task.Delay(100);

            NotifyPropertyChanged(nameof(EAMConnectButtonText));
        });
    }

    public async Task HandleAsync(EAMDisconnectMessage message, CancellationToken cancellationToken)
    {
        ClientStateSingleton.Instance.ExternalAWACSModelSelected = false;
        NotifyPropertyChanged(nameof(EAMConnectButtonText));
    }

    public async Task HandleAsync(ProfileChangedMessage message, CancellationToken cancellationToken)
    {
        NotifyPropertyChanged(nameof(CurrentProfile));
    }

    public async Task HandleAsync(ServerSettingsUpdatedMessage message, CancellationToken cancellationToken)
    {
        NotifyPropertyChanged(nameof(IsEAMAvailable));
    }

    public async Task HandleAsync(TCPClientStatusMessage obj, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            if (obj.Connected)
                HandleConnected(obj);
            else
                HandleDisconnected(obj.Error);

            NotifyPropertyChanged(nameof(IsEAMAvailable));
            NotifyPropertyChanged(nameof(EAMConnectButtonText));
            NotifyPropertyChanged(nameof(ConnectText));
        });
    }

    private void HandleConnected(TCPClientStatusMessage obj)
    {
        CancelReconnectScheduling();
        _wantConnected = true;
        _hadSuccessfulConnection = true;
        if (obj.Address != null)
            _lastEndpoint = obj.Address;

        IsReconnecting = false;
        _reconnectAttempt = 0;
        ReconnectStatusText = string.Empty;
        NotifyPropertyChanged(nameof(ReconnectStatusText));

        ConnectIsEnabled = true;
        IsConnected = true;

        if (_globalSettings.GetClientSettingBool(GlobalSettingsKeys.PlayConnectionSounds))
            try
            {
                Sounds.BeepConnected.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to play connect sound");
            }

        var audioStarted = true;
        if (obj.Address != null)
            audioStarted = StartAudio(obj.Address);
        else
            Logger.Error("TCPClientStatusMessage - Connect sent without address and safely ignored.");

        // Clear after StartAudio so reconnect audio failures stay silent.
        _suppressForegroundDialogs = false;

        if (!audioStarted || !IsConnected)
            return;

        _dcsManager?.Stop();

        _dcsManager = new DCSRadioSyncManager(ClientStateSingleton.Instance.ShortGUID);
        _dcsManager.Start();

        TryRejoinExternalAwacsMode();
    }

    private void HandleDisconnected(TCPClientStatusMessage.ErrorCode error)
    {
        var shouldReconnect = _wantConnected
                              && _hadSuccessfulConnection
                              && error == TCPClientStatusMessage.ErrorCode.TIMEOUT
                              && _globalSettings.GetClientSettingBool(GlobalSettingsKeys.AutoReconnect);

        CleanupConnection(playDisconnectSound: IsConnected);

        if (shouldReconnect)
        {
            Logger.Info($"Uncommanded disconnect ({error}) - scheduling reconnect");
            ScheduleReconnect();
        }
        else
        {
            _wantConnected = false;
            CancelReconnectScheduling();
            IsReconnecting = false;
            _reconnectAttempt = 0;
            ReconnectStatusText = string.Empty;
            NotifyPropertyChanged(nameof(ReconnectStatusText));
            ResetRadioState();
            ConnectIsEnabled = true;
        }
    }

    private void TryRejoinExternalAwacsMode()
    {
        if (!ClientStateSingleton.Instance.ExternalAWACSModelSelected)
            return;
        if (string.IsNullOrEmpty(EAMPassword))
            return;

        EventBus.Instance.PublishOnBackgroundThreadAsync(new EAMConnectRequestMessage
        {
            Password = EAMPassword,
            Name = EAMName
        });
    }

    public Task HandleAsync(ToggleSingleStackRadioOverlayMessage message, CancellationToken cancellationToken)
    {
        if (TimeSpan.FromTicks(DateTime.Now.Ticks - lastRadioToggleTime).TotalMilliseconds > OVERLAY_DEBOUNCE)
        {
            lastRadioToggleTime = DateTime.Now.Ticks;
            // Even though it should be the UI thread - it wasnt working so this forces the UI / STA thread
            App.Current.Dispatcher.Invoke(DispatcherPriority.Normal, new Action(ToggleSingleRadioStack));
        }

        return Task.CompletedTask;
    }

    public Task HandleAsync(ToogleAwacsRadioOverlayMessage message, CancellationToken cancellationToken)
    {
        if (TimeSpan.FromTicks(DateTime.Now.Ticks - lastAwacsToggleTime).TotalMilliseconds > OVERLAY_DEBOUNCE)
        {
            lastAwacsToggleTime = DateTime.Now.Ticks;
            //Debounce
            // Even though it should be the UI thread - it wasnt working so this forces the UI / STA thread
            App.Current.Dispatcher.Invoke(DispatcherPriority.Normal, new Action(MultiRadioOverlay));
        }

        return Task.CompletedTask;
    }

    public async Task HandleAsync(VOIPStatusMessage message, CancellationToken cancellationToken)
    {
        IsVoIPConnected = message.Connected;
    }

    private void UpdatePlayerCountAndVUMeters(object sender, EventArgs e)
    {
        NotifyPropertyChanged(nameof(SpeakerVU));
        NotifyPropertyChanged(nameof(MicVU));
        NotifyPropertyChanged(nameof(CurrentUnit));
        NotifyPropertyChanged(nameof(LastKnownPosition));

        if (ClientStateSingleton.Instance.IsConnected)
            NotifyPropertyChanged(nameof(EAMName));

        ConnectedClientsSingleton.Instance.NotifyAll();
    }

    public async Task ConnectAsync()
    {
        if (IsConnected || IsReconnecting)
        {
            ConnectIsEnabled = false;
            await DisconnectUserInitiatedAsync();
            ConnectIsEnabled = true;
            return;
        }

        ConnectIsEnabled = false;
        //stop preview
        _audioPreview?.StopEncoding();
        _audioPreview = null;

        // Manual connect may show dialogs; auto-reconnect must never steal focus.
        _suppressForegroundDialogs = false;

        IsConnected = true;
        await ShowMicPassthroughWarningAsync();

        try
        {
            if (!TryResolveServerEndpoint(out var endpoint, out var errorHeading))
            {
                await TaskDialog.ShowDialogAsync(new TaskDialogPage
                {
                    Caption = "Host Name Error",
                    Heading = errorHeading,
                    Text = "Please check the IP address or host name you entered and try again.",
                    Icon = TaskDialogIcon.Error,
                    Buttons = [TaskDialogButton.OK]
                });

                IsConnected = false;
            }
            else
            {
                _lastEndpoint = endpoint;
                StartClientConnection(endpoint);
            }
        }
        catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
        {
            await TaskDialog.ShowDialogAsync(new TaskDialogPage
            {
                Caption = "Host Name Error",
                Heading = "Invalid IP or Host Name!",
                Text = "Please check the IP address or host name you entered and try again.",
                Icon = TaskDialogIcon.Error,
                Buttons = [TaskDialogButton.OK]
            });

            IsConnected = false;
        }

        ConnectIsEnabled = true;
        NotifyPropertyChanged(nameof(ConnectText));
    }

    private async Task DisconnectUserInitiatedAsync()
    {
        _wantConnected = false;
        _hadSuccessfulConnection = false;
        _suppressForegroundDialogs = false;
        CancelReconnectScheduling();
        IsReconnecting = false;
        _reconnectAttempt = 0;
        ReconnectStatusText = string.Empty;
        NotifyPropertyChanged(nameof(ReconnectStatusText));

        CleanupConnection(playDisconnectSound: IsConnected,
            disconnectReason: TCPClientStatusMessage.ErrorCode.USER_DISCONNECTED);
        ResetRadioState();
        ConnectIsEnabled = true;
        NotifyPropertyChanged(nameof(ConnectText));
        await Task.CompletedTask;
    }

    private void CleanupConnection(bool playDisconnectSound,
        TCPClientStatusMessage.ErrorCode disconnectReason = TCPClientStatusMessage.ErrorCode.TIMEOUT)
    {
        if (playDisconnectSound && _globalSettings.GetClientSettingBool(GlobalSettingsKeys.PlayConnectionSounds))
            try
            {
                Sounds.BeepDisconnected.Play();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to play disconnect sound");
            }

        IsConnected = false;

        try
        {
            _audioManager.StopEncoding();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Failed to stop enconding audio");
        }

        _dcsManager?.Stop();
        _dcsManager = null;

        var client = _client;
        _client = null;
        client?.RequestDisconnectAsync(disconnectReason);
    }

    private void ResetRadioState()
    {
        ClientState.DcsPlayerRadioInfo.Reset();
        ClientState.PlayerCoaltionLocationMetadata.Reset();
    }

    private void ScheduleReconnect()
    {
        if (!_wantConnected || !_hadSuccessfulConnection)
            return;
        if (!_globalSettings.GetClientSettingBool(GlobalSettingsKeys.AutoReconnect))
            return;

        CancellationToken token;
        int attempt;
        lock (_reconnectLock)
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();
            token = _reconnectCts.Token;
            _reconnectAttempt++;
            attempt = _reconnectAttempt;
        }

        IsReconnecting = true;
        ReconnectStatusText = string.Format(Resources.ReconnectStatus, attempt);
        NotifyPropertyChanged(nameof(ReconnectStatusText));
        NotifyPropertyChanged(nameof(ConnectText));
        ConnectIsEnabled = true;

        // attempt 1 -> 2s, 2 -> 4s, 3 -> 8s, ... capped at 30s
        var delaySeconds = Math.Min(ReconnectBackoffCapSeconds, 1 << Math.Min(attempt, 5));

        Logger.Info($"Scheduling reconnect attempt {attempt} in {delaySeconds}s");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                if (token.IsCancellationRequested || !_wantConnected)
                    return;

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!_wantConnected || token.IsCancellationRequested)
                        return;
                    AttemptReconnect();
                });
            }
            catch (OperationCanceledException)
            {
                // cancelled
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Reconnect schedule failed");
            }
        }, token);
    }

    private void AttemptReconnect()
    {
        if (!_wantConnected)
            return;

        Logger.Info($"Attempting reconnect #{_reconnectAttempt} to {ServerAddress}");

        // Never steal focus from DCS / the sim while recovering mid-flight.
        _suppressForegroundDialogs = true;

        if (!TryResolveServerEndpoint(out var endpoint, out _))
        {
            if (_lastEndpoint == null)
            {
                Logger.Warn("Reconnect aborted - could not resolve server address");
                ScheduleReconnect();
                return;
            }

            endpoint = _lastEndpoint;
        }
        else
        {
            _lastEndpoint = endpoint;
        }

        StartClientConnection(endpoint);
    }

    private void StartClientConnection(IPEndPoint endpoint)
    {
        _client = new TCPClientHandler(ClientState.ShortGUID,
            new SRClientBase
            {
                LatLngPosition = new LatLngPosition(),
                AllowRecord =
                    GlobalSettingsStore.Instance.GetClientSettingBool(GlobalSettingsKeys.AllowRecording),
                ClientGuid = ClientStateSingleton.Instance.ShortGUID,
                Coalition = 0,
                Name = ClientStateSingleton.Instance.LastSeenName,
                RadioInfo = ClientState.DcsPlayerRadioInfo.ConvertToRadioBase(),
                Seat = 0
            });
        _client.TryConnect(endpoint);
    }

    private bool TryResolveServerEndpoint(out IPEndPoint endpoint, out string errorHeading)
    {
        endpoint = null;
        errorHeading = "Invalid IP or Host Name!";

        try
        {
            var resolvedAddresses = Dns.GetHostAddresses(GetAddressFromTextBox());
            var ip = resolvedAddresses.FirstOrDefault(xa =>
                xa.AddressFamily == AddressFamily.InterNetwork);

            if (ip == null)
                return false;

            endpoint = new IPEndPoint(ip, GetPortFromTextBox());
            return true;
        }
        catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
        {
            return false;
        }
    }

    private void CancelReconnectScheduling()
    {
        lock (_reconnectLock)
        {
            try
            {
                _reconnectCts?.Cancel();
                _reconnectCts?.Dispose();
            }
            catch (Exception)
            {
                // ignored
            }

            _reconnectCts = null;
        }
    }

    // Kept for any remaining call sites that expect Stop()
    private void Stop(TCPClientStatusMessage.ErrorCode connectionError = TCPClientStatusMessage.ErrorCode.TIMEOUT)
    {
        _ = DisconnectUserInitiatedAsync();
    }

    private string GetAddressFromTextBox()
    {
        var addr = ServerAddress.Trim();

        if (addr.Contains(":")) return addr.Split(':')[0];

        return addr;
    }

    private int GetPortFromTextBox()
    {
        var addr = ServerAddress.Trim();

        if (addr.Contains(":"))
        {
            int port;
            if (int.TryParse(addr.Split(':')[1], out port)) return port;

            throw new ArgumentException("specified port is  valid");
        }

        return 5002;
    }


    private async Task ShowMicPassthroughWarningAsync()
    {
        if (_globalSettings.GetClientSetting(GlobalSettingsKeys.MicAudioOutputDeviceId).RawValue
            .Equals(_globalSettings.GetClientSetting(GlobalSettingsKeys.AudioOutputDeviceId).RawValue))
        {
            await TaskDialog.ShowDialogAsync(new TaskDialogPage
            {
                Caption = "Mic Output and Speaker Output Matching",
                Heading = "Warning!",
                Text =
                    "Mic Output and Speaker Output should not be set to the same device!\n\nMic Output is just for recording and not for use as a sidetone. You will hear yourself with a small delay!\n\nHit disconnect and change Mic Output / Passthrough",
                Icon = TaskDialogIcon.Warning,
                Buttons = [TaskDialogButton.OK]
            });
        }
    }

    private void PreviewAudio()
    {
        if (_audioPreview == null)
        {
            if (!AudioInput.MicrophoneAvailable)
            {
                Logger.Info("Unable to preview audio, no valid audio input device available or selected");
                return;
            }

            //get device
            try
            {
                Task.Run(async () => await ShowMicPassthroughWarningAsync()).Wait();

                _audioPreview = new AudioPreview();
                _audioPreview.PreviewAircraft = SelectedPreviewAircraft;
                _audioPreview.SpeakerBoost = VolumeConversionHelper.ConvertVolumeSliderToScale((float)SpeakerBoost);
                _audioPreview.StartPreview(AudioOutput.WindowsN);
            }
            catch (Exception ex)
            {
                Logger.Error(ex,
                    "Unable to preview audio - likely output device error - Pick another. Error:" + ex.Message);
            }
        }
        else
        {
            _audioPreview.StopEncoding();
            _audioPreview = null;
        }

        NotifyPropertyChanged(nameof(PreviewText));
        NotifyPropertyChanged(nameof(AudioSettingsEnabled));
    }

    private bool StartAudio(IPEndPoint endPoint)
    {
        var started = false;
        //Must be main thread
        App.Current.Dispatcher.Invoke(delegate
        {
            try
            {
                _audioManager.StartEncoding(ClientState.ShortGUID, InputManager, endPoint);
                started = true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Unable to start audio session. Error:" + ex.Message);

                // Auto-reconnect must never modal-popup over the sim.
                if (_suppressForegroundDialogs)
                {
                    _wantConnected = false;
                    _hadSuccessfulConnection = false;
                    CancelReconnectScheduling();
                    IsReconnecting = false;
                    CleanupConnection(playDisconnectSound: false,
                        disconnectReason: TCPClientStatusMessage.ErrorCode.USER_DISCONNECTED);
                    ResetRadioState();
                    ReconnectStatusText = Resources.ReconnectAudioFailed;
                    NotifyPropertyChanged(nameof(ReconnectStatusText));
                    NotifyPropertyChanged(nameof(ConnectText));
                    ConnectIsEnabled = true;
                    return;
                }

                Stop();

                var isMissingNative =
                    ex is DllNotFoundException
                    || ex.InnerException is DllNotFoundException
                    || (ex.Message?.Contains("WebRtcVad", StringComparison.OrdinalIgnoreCase) ?? false)
                    || (ex.Message?.Contains("webrtcvad", StringComparison.OrdinalIgnoreCase) ?? false)
                    || (ex.Message?.Contains("Unable to load DLL", StringComparison.OrdinalIgnoreCase) ?? false);

                var caption = isMissingNative ? "Installation Problem" : "Audio Device Problem";
                var heading = isMissingNative
                    ? "A required program file is missing"
                    : "Could not start the selected audio devices";
                var body = isMissingNative
                    ? "Run the latest 455th SRS installer to repair this install.\n\n" +
                      $"Details: {ex.Message}\n\nSee clientlog.txt in the Client install folder for support."
                    : "Try a different Output or Input device in 455th SRS settings, and check Windows privacy permissions for microphone/speakers if needed.\n\n" +
                      $"Details: {ex.Message}\n\nSee clientlog.txt in the Client install folder for support.";

                TaskDialog.ShowDialog(new TaskDialogPage
                {
                    Caption = caption,
                    Heading = heading,
                    Text = body,
                    Icon = TaskDialogIcon.Error,
                    Buttons =
                    {
                        new TaskDialogButton
                        {
                            Text = "Close",
                            Tag = 3
                        }
                    }
                });
            }
        });

        return started;
    }

    private void SingleRadioStackOverlay()
    {
        ToggleSingleRadioStack();
    }

    private void ToggleSingleRadioStack()
    {
        if (_singleRadioOverlay == null || !_singleRadioOverlay.IsVisible ||
            _singleRadioOverlay.WindowState == WindowState.Minimized)
        {
            //hide awacs panel
            _awacsRadioOverlay?.Close();
            _awacsRadioOverlay = null;

            _singleRadioOverlay?.Close();

            _singleRadioOverlay = new RadioOverlayWindow.RadioOverlayWindow();


            _singleRadioOverlay.ShowInTaskbar =
                !_globalSettings.GetClientSettingBool(GlobalSettingsKeys.RadioOverlayTaskbarHide);
            _singleRadioOverlay.Show();
        }
        else
        {
            _singleRadioOverlay?.Close();
            _singleRadioOverlay = null;
        }
    }

    private void MultiRadioOverlay()
    {
        ToggleMultiRadioOverlay();
    }

    private void ToggleMultiRadioOverlay()
    {
        if (_awacsRadioOverlay == null || !_awacsRadioOverlay.IsVisible ||
            _awacsRadioOverlay.WindowState == WindowState.Minimized)
        {
            //close normal overlay
            _singleRadioOverlay?.Close();
            _singleRadioOverlay = null;

            _awacsRadioOverlay?.Close();

            _awacsRadioOverlay = new AwaRadioOverlayWindow();
            _awacsRadioOverlay.ShowInTaskbar =
                !_globalSettings.GetClientSettingBool(GlobalSettingsKeys.RadioOverlayTaskbarHide);
            _awacsRadioOverlay.Show();
        }
        else
        {
            _awacsRadioOverlay?.Close();
            _awacsRadioOverlay = null;
        }
    }

    private void ToggleServerSettings()
    {
        if (_serverSettingsWindow == null || !_serverSettingsWindow.IsVisible ||
            _serverSettingsWindow.WindowState == WindowState.Minimized)
        {
            _serverSettingsWindow?.Close();

            _serverSettingsWindow = new ServerSettingsWindow.ServerSettingsWindow();
            _serverSettingsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _serverSettingsWindow.Owner = App.Current.MainWindow;
            _serverSettingsWindow.Show();
        }
        else
        {
            _serverSettingsWindow?.Close();
            _serverSettingsWindow = null;
        }
    }

    private void ToggleClientList()
    {
        if (_clientListWindow == null || !_clientListWindow.IsVisible ||
            _clientListWindow.WindowState == WindowState.Minimized)
        {
            _clientListWindow?.Close();

            _clientListWindow = new ClientListWindow();
            _clientListWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _clientListWindow.Owner = App.Current.MainWindow;
            _clientListWindow.Show();
        }
        else
        {
            _clientListWindow?.Close();
            _clientListWindow = null;
        }
    }


    public void OnClosing()
    {
        //stop timer
        _updateTimer?.Stop();

        _ = DisconnectUserInitiatedAsync();

        _audioPreview?.StopEncoding();
        _audioPreview = null;

        _singleRadioOverlay?.Close();
        _singleRadioOverlay = null;

        _awacsRadioOverlay?.Close();
        _awacsRadioOverlay = null;

        _clientListWindow?.Close();
        _clientListWindow = null;

        _serverSettingsWindow?.Close();
        _serverSettingsWindow = null;
    }
}