using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using NapMLS.Core;
using NapMLS.NapCat;
using NapMLS.UI.Models;
using NapMLS.UI.Services;

namespace NapMLS.UI.ViewModels;

/// <summary>
/// Main window ViewModel with startup state machine.
/// Creates NapCatServer + MlsTransportBridge for real QQ messaging.
/// Isolation: pass --data-dir to use separate storage directories.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public NavigationService Navigation { get; } = new();

    [ObservableProperty]
    public partial string WindowTitle { get; set; } = "NapMLS - 加密子群";

    private AppConfig _config = new();
    private SqliteStorage? _storage;
    private MlsService? _mls;
    private MlsTransportBridge? _bridge;
    private NapCatServer? _server;
    private MessageBus? _bus;
    private readonly string _dataDir;

    public MainViewModel() : this(GetDataDirFromArgs()) { }

    public MainViewModel(string dataDir)
    {
        _dataDir = dataDir;
        _config = AppConfigService.Load(Path.Combine(dataDir, "config.json"));
        RunStartupStateMachine();
    }

    private static string GetDataDirFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--data-dir")
                return args[i + 1];
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NapMLS");
    }

    private string GetAppDir() => _dataDir;

    private SqliteStorage GetStorage()
    {
        if (_storage == null)
        {
            var dbPath = Path.Combine(GetAppDir(), "napmls.db");
            _storage = new SqliteStorage(dbPath);
        }
        return _storage;
    }

    private MlsService? GetMls()
    {
        if (_mls != null) return _mls;

        if (string.IsNullOrEmpty(_config.IdentityUsername)) return null;

        var dbPath = Path.Combine(GetAppDir(), "napmls.db");
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(_config.IdentityUsername));
        _mls = MlsService.Open(dbPath, key, _config.IdentityUsername);
        return _mls;
    }

    private (NapCatServer server, MlsTransportBridge bridge, MessageBus bus) GetTransport()
    {
        if (_server != null && _bridge != null && _bus != null)
            return (_server, _bridge, _bus);

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        var logger = loggerFactory.CreateLogger("NapMLS");

        _bus = new MessageBus();

        _server = new NapCatServer(
            host: _config.NapCatHost,
            port: _config.NapCatPort,
            token: _config.NapCatToken,
            logger: logger);

        _server.OnConnectionChanged += connected =>
        {
            Console.WriteLine(connected ? "[Bridge] NapCat CONNECTED" : "[Bridge] NapCat DISCONNECTED");
        };

        _server.OnEventReceived += json =>
        {
            var evt = OneBotParser.ParseEvent(json);
            if (evt == null) return;

            if (evt.PostType == "meta_event" && evt.MetaEventType == "heartbeat")
                _server.RecordHeartbeat();
        };

        var mls = GetMls();
        if (mls != null)
        {
            _bridge = new MlsTransportBridge(_server, mls, _bus, GetStorage());
        }

        return (_server, _bridge!, _bus);
    }

    private void RunStartupStateMachine()
    {
        if (!_config.RiskWarningAccepted)
        {
            Navigation.NavigateTo(new RiskWarningViewModel(OnRiskAccepted));
        }
        else if (string.IsNullOrEmpty(_config.IdentitySafetyCode))
        {
            var configPath = Path.Combine(_dataDir, "config.json");
            Navigation.NavigateTo(new SetupViewModel(_config, configPath, OnSetupCompleted));
        }
        else
        {
            NavigateToGroups();
        }
    }

    private void OnRiskAccepted()
    {
        _config.RiskWarningAccepted = true;
        var configPath = Path.Combine(_dataDir, "config.json");
        AppConfigService.Save(_config, configPath);
        Navigation.Clear();
        Navigation.NavigateTo(new SetupViewModel(_config, configPath, OnSetupCompleted));
    }

    private void OnSetupCompleted(AppConfig config)
    {
        _config = config;
        Navigation.Clear();
        NavigateToGroups();
    }

    private async void NavigateToGroups()
    {
        var storage = GetStorage();
        var fp = Convert.FromHexString(_config.IdentityFingerprint!);
        var mls = GetMls();

        MlsTransportBridge? bridge = null;
        MessageBus? bus = null;
        NapCatServer? server = null;

        try
        {
            (server, bridge, bus) = GetTransport();
            await server.StartAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainViewModel] Failed to start NapCat server: {ex.Message}");
        }

        var vm = new GroupListViewModel(_config, storage, fp, mls, bridge, bus)
        {
            Navigation = Navigation,
        };
        Navigation.NavigateTo(vm);
    }
}
