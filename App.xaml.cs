using System.Configuration;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NativeTavern.Data;
using NativeTavern.Data.Repositories;
using NativeTavern.Helpers;
using NativeTavern.Logging;
using NativeTavern.Providers;
using NativeTavern.Security;
using NativeTavern.Services;
using NativeTavern.ViewModels;

namespace NativeTavern;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static string DisplayVersion => Helpers.AppVersion.DisplayVersion;

    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Helpers.AppVersion.Use(typeof(App).Assembly.GetName().Version ?? new Version());
        AppPaths.EnsureCreated();
        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
        var logger = _services.GetRequiredService<ILogger<App>>();
        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogCritical(args.Exception, "Unhandled UI exception.");
            MessageBox.Show("NativeTavern 遇到未处理错误，详情已写入日志。", "NativeTavern", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            logger.LogInformation("NativeTavern {Version} starting.", DisplayVersion);
            await _services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            var storedSettings = await _services.GetRequiredService<SettingsService>().LoadAsync();
            _services.GetRequiredService<LocalizationService>().SetLanguage(storedSettings.LanguageCode);
            ThemeService.Apply(storedSettings.ThemeMode);
            var login = new Views.LoginWindow();
            if (login.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            var viewModel = _services.GetRequiredService<MainViewModel>();
            await viewModel.InitializeAsync();
            var window = _services.GetRequiredService<MainWindow>();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _services.GetRequiredService<TrayService>().Initialize(() =>
            {
                window.Show(); window.WindowState = WindowState.Normal; window.Activate();
            });
            window.Show();
            var hotkeys = _services.GetRequiredService<GlobalHotkeyService>();
            hotkeys.Attach(
                window,
                toggleWindow: () =>
                {
                    if (!window.IsVisible || window.WindowState == WindowState.Minimized)
                    {
                        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
                    }
                    else if (window.IsActive) window.Hide();
                    else window.Activate();
                },
                bossKey: () => window.Hide());
            hotkeys.Apply(storedSettings.HotkeysEnabled, storedSettings.ToggleWindowHotkey, storedSettings.BossKeyHotkey);
            _services.GetRequiredService<SettingsViewModel>().Saved += async () =>
            {
                var current = await _services.GetRequiredService<SettingsService>().LoadAsync();
                hotkeys.Apply(current.HotkeysEnabled, current.ToggleWindowHotkey, current.BossKeyHotkey);
            };
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Application startup failed.");
            MessageBox.Show("NativeTavern 启动失败，请查看日志：" + Environment.NewLine + AppPaths.LogFile, "NativeTavern", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    internal static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging(builder => builder.AddProvider(new FileLoggerProvider(AppPaths.LogFile)));
        services.AddSingleton<DatabaseConnectionFactory>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<ChatSessionRepository>();
        services.AddSingleton<ComposerDraftRepository>();
        services.AddSingleton<ChatMessageRepository>();
        services.AddSingleton<MessageSwipeRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<CharacterRepository>();
        services.AddSingleton<PromptRepository>();
        services.AddSingleton<KnowledgeRepository>();
        services.AddSingleton<ChatAttachmentRepository>();
        services.AddSingleton<CharacterStatusRepository>();
        services.AddSingleton<CharacterMemoryRepository>();
        services.AddSingleton<RegexScriptRepository>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<Importers.CharacterCardImporter>();
        services.AddSingleton<ICharacterService, CharacterService>();
        services.AddSingleton<PromptService>();
        services.AddSingleton<KnowledgeService>();
        services.AddSingleton<AttachmentService>();
        services.AddSingleton<ConversationSummaryService>();
        services.AddSingleton<CharacterStatusService>();
        services.AddSingleton<RegexScriptService>();
        services.AddSingleton<PromptResourceService>();
        services.AddSingleton<ReplySuggestionService>();
        services.AddSingleton<CharacterMemoryService>();
        services.AddSingleton<GlobalHotkeyService>();
        services.AddSingleton<TrayService>();
        services.AddHttpClient<OpenAICompatibleProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<ClaudeProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<ProviderRouter>();
        services.AddSingleton<ProviderDiscoveryService>();
        services.AddSingleton<LocalModelService>();
        services.AddHttpClient("PluginDownloads", client => client.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton(provider => new PluginService(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("PluginDownloads"),
            provider.GetRequiredService<ILogger<PluginService>>()));
        services.AddSingleton<ILLMProvider>(provider => provider.GetRequiredService<ProviderRouter>());
        services.AddSingleton<ChatService>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<CharactersViewModel>();
        services.AddSingleton<PromptStudioViewModel>();
        services.AddSingleton<KnowledgeViewModel>();
        services.AddSingleton<PromptInspectorViewModel>();
        services.AddSingleton<PluginsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }
}

