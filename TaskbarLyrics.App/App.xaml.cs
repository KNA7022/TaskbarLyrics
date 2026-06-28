using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace TaskbarLyrics.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\ANYNC.TaskbarLyrics.SingleInstance";
    private const string ActivationPipeName = "ANYNC.TaskbarLyrics.Activation";
    private SettingsStore? _settingsStore;
    private TrayService? _trayService;
    private SettingsWindow? _settingsWindow;
    private SpectrumTuningWindow? _spectrumTuningWindow;
    private LyricsWindowHost? _lyricsWindowHost;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private CancellationTokenSource? _activationServerCancellation;
    private SpectrumTuningSettings _spectrumTuningSettings = SpectrumTuningSettings.CreateDefault();

    public AppSettings Settings { get; private set; } = new();

    public bool IsExiting { get; private set; }
    public bool UserWantsLyricsVisible { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            SignalRunningInstanceAsync().GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Wpf.Ui.Appearance.ApplicationAccentColorManager.ApplySystemAccent();

        // 初始化 SQLite 别名与纯音乐映射库
        TaskbarLyrics.Core.Database.SongSearchMapDbContext.InitializeDatabase();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TaskbarLyrics",
            "settings.json");

        _settingsStore = new SettingsStore(settingsPath);
        Settings = _settingsStore.Load();
        ApplyStartupForegroundColor(Settings);
        Settings.StartWithWindows = Settings.StartWithWindows || StartupService.IsEnabled();
        StartupService.SetEnabled(Settings.StartWithWindows);

        _lyricsWindowHost = new LyricsWindowHost(Settings);

        if (Settings.ShowLyricsOnStartup)
        {
            _lyricsWindowHost.Show();
        }
        UserWantsLyricsVisible = Settings.ShowLyricsOnStartup;

        _lyricsWindowHost.ApplySpectrumTuning(_spectrumTuningSettings);
        _trayService = new TrayService(ToggleLyricsWindow, OpenSettingsWindow, ExitApplication);
        StartActivationServer();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationServerCancellation?.Cancel();
        _activationServerCancellation?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settingsStore?.Save(Settings);
        _spectrumTuningWindow?.Close();
        _lyricsWindowHost?.Dispose();
        _trayService?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        _settingsStore?.Save(Settings);
        _lyricsWindowHost?.ApplySettings(Settings);
    }

    internal static void ApplyStartupForegroundColor(AppSettings settings)
    {
        ApplySystemThemeForegroundColor(settings, migrateLegacyCustomColor: true);
    }

    internal static bool ApplySystemThemeForegroundColor(AppSettings settings, bool migrateLegacyCustomColor = false)
    {
        if (migrateLegacyCustomColor && IsLegacyCustomForeground(settings.ForegroundColor))
        {
            settings.ForegroundColorMode = ForegroundColorMode.Custom;
            return false;
        }

        if (settings.ForegroundColorMode == ForegroundColorMode.Custom)
        {
            return false;
        }

        var nextMode = IsSystemUsingLightTheme()
            ? ForegroundColorMode.Dark
            : ForegroundColorMode.Light;
        var nextColor = nextMode == ForegroundColorMode.Dark
            ? AppSettings.DarkForegroundColor
            : AppSettings.LightForegroundColor;

        var changed = settings.ForegroundColorMode != nextMode ||
            !string.Equals(settings.ForegroundColor, nextColor, StringComparison.OrdinalIgnoreCase);
        settings.ForegroundColorMode = nextMode;
        settings.ForegroundColor = nextColor;
        return changed;
    }

    private static bool IsLegacyCustomForeground(string? color)
    {
        var normalized = NormalizeColor(color);
        return !string.Equals(normalized, AppSettings.DarkForegroundColor, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, AppSettings.LightForegroundColor, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return AppSettings.LightForegroundColor;
        }

        var trimmed = color.Trim();
        return trimmed.Length == 7 && trimmed.StartsWith('#')
            ? $"#FF{trimmed[1..]}"
            : trimmed;
    }

    internal static bool IsSystemUsingLightTheme()
    {
        const string personalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key = Registry.CurrentUser.OpenSubKey(personalizeKey);
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (ApplySystemThemeForegroundColor(Settings))
            {
                _settingsStore?.Save(Settings);
                _lyricsWindowHost?.ApplySettings(Settings);
                _settingsWindow?.ApplyExternalSettings(Settings.Clone());
            }
        });
    }

    private void ToggleLyricsWindow()
    {
        if (_lyricsWindowHost is null)
        {
            return;
        }

        if (_lyricsWindowHost.IsVisible)
        {
            UserWantsLyricsVisible = false;
            _lyricsWindowHost.Hide();
        }
        else
        {
            UserWantsLyricsVisible = true;
            _lyricsWindowHost.Show();
        }
    }

    public void MarkLyricsHiddenByUser()
    {
        UserWantsLyricsVisible = false;
    }

    public void MarkLyricsVisibleBySystem()
    {
        UserWantsLyricsVisible = true;
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(Settings.Clone());
        _settingsWindow.Closed += SettingsWindow_Closed;
        _settingsWindow.Show();
    }

    private void StartActivationServer()
    {
        _activationServerCancellation = new CancellationTokenSource();
        _ = Task.Run(() => ListenForActivationAsync(_activationServerCancellation.Token));
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);
                await Dispatcher.InvokeAsync(OpenSettingsWindow);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task SignalRunningInstanceAsync()
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await client.ConnectAsync(500);
        }
        catch (TimeoutException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Closed -= SettingsWindow_Closed;
            _settingsWindow = null;
        }
    }

    public void OpenSpectrumTuningWindow()
    {
        if (_spectrumTuningWindow is { IsVisible: true })
        {
            _spectrumTuningWindow.Activate();
            return;
        }

        _spectrumTuningWindow = new SpectrumTuningWindow(_spectrumTuningSettings, ApplySpectrumTuning);
        _spectrumTuningWindow.Closed += SpectrumTuningWindow_Closed;
        _spectrumTuningWindow.Show();
    }

    private void ApplySpectrumTuning(SpectrumTuningSettings settings)
    {
        _spectrumTuningSettings = settings.Clone();
        _lyricsWindowHost?.ApplySpectrumTuning(_spectrumTuningSettings);
    }

    private void SpectrumTuningWindow_Closed(object? sender, EventArgs e)
    {
        if (_spectrumTuningWindow is not null)
        {
            _spectrumTuningWindow.Closed -= SpectrumTuningWindow_Closed;
            _spectrumTuningWindow = null;
        }
    }

    private void ExitApplication()
    {
        IsExiting = true;
        _lyricsWindowHost?.Close();
        Shutdown();
    }
}
