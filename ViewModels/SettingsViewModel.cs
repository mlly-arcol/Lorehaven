using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using NativeTavern.Models;
using NativeTavern.Providers;
using NativeTavern.Services;

namespace NativeTavern.ViewModels;

public partial class SettingsViewModel(
    SettingsService settingsService,
    LocalizationService localizationService,
    ProviderRouter provider,
    ProviderDiscoveryService discoveryService,
    LocalModelService localModelService,
    BackupService backupService,
    ILogger<SettingsViewModel> logger) : ObservableObject
{
    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(LocalizationService.Chinese, "中文"),
        new(LocalizationService.English, "English")
    ];
    public IReadOnlyList<ThemeOption> Themes { get; private set; } = [];
    public IReadOnlyList<ProviderProfile> ProviderProfiles { get; } = ProviderProfile.All;
    public ObservableCollection<ModelInfo> AvailableModels { get; } = [];
    public ObservableCollection<DetectedProvider> DetectedServices { get; } = [];
    public ObservableCollection<LocalModelFile> LocalModels { get; } = [];

    [ObservableProperty] private ProviderProfile? _selectedProvider;
    [ObservableProperty] private LanguageOption? _selectedLanguage;
    [ObservableProperty] private ThemeOption? _selectedTheme;
    [ObservableProperty] private DetectedProvider? _selectedDetectedService;
    [ObservableProperty] private LocalModelFile? _selectedLocalModel;
    [ObservableProperty] private string _localModelDirectory = LocalModelService.DefaultModelDirectory;
    [ObservableProperty] private string _llamaCppPath = LocalModelService.DefaultLlamaCppPath;
    [ObservableProperty] private string _baseUrl = "https://api.openai.com/v1";
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private double _temperature = 0.8;
    [ObservableProperty] private double _topP = 1.0;
    [ObservableProperty] private int _maxTokens = 1024;
    [ObservableProperty] private int _contextLength = 8192;
    [ObservableProperty] private bool _trimHistoryToContext = true;
    [ObservableProperty] private bool _autoScanLocalModels = true;
    [ObservableProperty] private bool _includeCharacterContext;
    [ObservableProperty] private bool _includeKnowledgeContext;
    [ObservableProperty] private bool _includeImageContext;
    [ObservableProperty] private bool _characterMemoryEnabled;
    [ObservableProperty] private bool _hotkeysEnabled;
    [ObservableProperty] private string _toggleWindowHotkey = "Ctrl+Alt+T";
    [ObservableProperty] private string _bossKeyHotkey = "Ctrl+Alt+B";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isBusy;
    private bool _clearApiKey;
    private bool _initializing;
    private bool _rebuildingThemes;
    private bool _languageWatched;

    public event Action? Saved;

    public async Task CreateBackupAsync(string path)
    {
        IsBusy = true;
        StatusMessage = L("正在创建备份…", "Creating backup…");
        try
        {
            await backupService.CreateAsync(path);
            StatusMessage = L("备份已创建。", "Backup created.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Creating data backup failed.");
            StatusMessage = L($"备份失败：{ex.Message}", $"Backup failed: {ex.Message}");
            throw;
        }
        finally { IsBusy = false; }
    }

    public async Task<string> RestoreBackupAsync(string path)
    {
        IsBusy = true;
        StatusMessage = L("正在恢复备份…", "Restoring backup…");
        try
        {
            var safetyBackup = await backupService.RestoreAsync(path);
            StatusMessage = L("恢复完成，需要重新启动。", "Restore complete. Restart required.");
            return safetyBackup;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restoring data backup failed.");
            StatusMessage = L($"恢复失败：{ex.Message}", $"Restore failed: {ex.Message}");
            throw;
        }
        finally { IsBusy = false; }
    }

    public async Task InitializeAsync()
    {
        _initializing = true;
        var resolved = await settingsService.LoadResolvedAsync();
        WatchLanguageChanges();
        RefreshThemes(ThemeService.ToCode(ThemeService.ParseMode(resolved.Settings.ThemeMode)));
        SelectedLanguage = Languages.First(x => x.Code == LocalizationService.Normalize(resolved.Settings.LanguageCode));
        SelectedProvider = ProviderProfiles.FirstOrDefault(x => x.Id == resolved.Settings.ProviderId) ?? ProviderProfiles[0];
        BaseUrl = resolved.Settings.BaseUrl;
        ApiKey = resolved.ApiKey;
        Model = resolved.Settings.Model;
        Temperature = resolved.Settings.Temperature;
        TopP = resolved.Settings.TopP;
        MaxTokens = resolved.Settings.MaxTokens;
        ContextLength = resolved.Settings.ContextLength;
        TrimHistoryToContext = resolved.Settings.TrimHistoryToContext;
        AutoScanLocalModels = resolved.Settings.AutoScanLocalModels;
        LocalModelDirectory = string.IsNullOrWhiteSpace(resolved.Settings.LocalModelDirectory)
            || string.Equals(resolved.Settings.LocalModelDirectory, LocalModelService.LegacyModelDirectory, StringComparison.OrdinalIgnoreCase)
            ? LocalModelService.DefaultModelDirectory : resolved.Settings.LocalModelDirectory;
        LlamaCppPath = string.IsNullOrWhiteSpace(resolved.Settings.LlamaCppPath)
            ? LocalModelService.DefaultLlamaCppPath : resolved.Settings.LlamaCppPath;
        var defaultModelPath = Path.Combine(LocalModelDirectory, LocalModelService.DefaultModelFileName);
        var selectedModelPath = string.Equals(resolved.Settings.SelectedLocalModelPath,
            Path.Combine(LocalModelService.LegacyModelDirectory, LocalModelService.DefaultModelFileName),
            StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(resolved.Settings.SelectedLocalModelPath)
            ? defaultModelPath : resolved.Settings.SelectedLocalModelPath;
        RefreshLocalModelFiles(selectedModelPath);
        IncludeCharacterContext = resolved.Settings.IncludeCharacterContext;
        IncludeKnowledgeContext = resolved.Settings.IncludeKnowledgeContext;
        IncludeImageContext = resolved.Settings.IncludeImageContext;
        CharacterMemoryEnabled = resolved.Settings.CharacterMemoryEnabled;
        HotkeysEnabled = resolved.Settings.HotkeysEnabled;
        ToggleWindowHotkey = string.IsNullOrWhiteSpace(resolved.Settings.ToggleWindowHotkey)
            ? "Ctrl+Alt+T" : resolved.Settings.ToggleWindowHotkey;
        BossKeyHotkey = string.IsNullOrWhiteSpace(resolved.Settings.BossKeyHotkey)
            ? "Ctrl+Alt+B" : resolved.Settings.BossKeyHotkey;
        _initializing = false;
        if (AutoScanLocalModels) await ScanLocalAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryBuildSettings(out var settings)) return;
        IsBusy = true;
        try
        {
            await settingsService.SaveAsync(settings, ApiKey, _clearApiKey);
            _clearApiKey = false;
            localizationService.SetLanguage(settings.LanguageCode);
            StatusMessage = localizationService.Text("已保存。", "Saved.");
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save provider settings.");
            StatusMessage = localizationService.Text("保存失败，请查看日志。", "Save failed. Check the log.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!TryBuildSettings(out var settings)) return;
        IsBusy = true;
        StatusMessage = L("正在测试连接…", "Testing connection…");
        try
        {
            await provider.TestConnectionAsync(settings, ApiKey, CancellationToken.None);
            StatusMessage = L("连接成功。", "Connection successful.");
        }
        catch (ProviderException ex) { StatusMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected connection test failure.");
            StatusMessage = L("连接测试失败，请查看日志。", "Connection test failed. Check the log.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DiscoverModelsAsync()
    {
        if (!TryBuildSettings(out var settings, requireModel: false)) return;
        IsBusy = true;
        StatusMessage = L("正在获取模型列表…", "Loading models…");
        try
        {
            var models = await provider.GetModelsAsync(settings, ApiKey, CancellationToken.None);
            AvailableModels.Clear();
            foreach (var model in models) AvailableModels.Add(model);
            if (models.Count == 1) Model = models[0].Id;
            StatusMessage = L($"发现 {models.Count} 个模型。", $"Found {models.Count} models.");
        }
        catch (ProviderException ex) { StatusMessage = ex.Message; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Model discovery failed.");
            StatusMessage = L("获取模型列表失败，请查看日志。", "Failed to load models. Check the log.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ScanLocalAsync()
    {
        IsBusy = true;
        StatusMessage = L("正在扫描本地模型服务…", "Scanning local model services…");
        try
        {
            RefreshLocalModelFiles(SelectedLocalModel?.FilePath);
            var detected = await discoveryService.ScanLocalAsync(CancellationToken.None);
            DetectedServices.Clear();
            foreach (var service in detected) DetectedServices.Add(service);
            StatusMessage = L(
                $"发现 {LocalModels.Count} 个 GGUF 模型；检测到 {detected.Count} 个运行中的本地服务。",
                $"Found {LocalModels.Count} GGUF models and {detected.Count} running local services.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local provider scan failed.");
            StatusMessage = L("本地扫描失败，请查看日志。", "Local scan failed. Check the log.");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StartSelectedLocalModelAsync()
    {
        if (SelectedLocalModel is null)
        {
            StatusMessage = L("请先选择一个 GGUF 模型。", "Select a GGUF model first.");
            return;
        }

        IsBusy = true;
        StatusMessage = L($"正在加载 {SelectedLocalModel.Name}…", $"Loading {SelectedLocalModel.Name}…");
        try
        {
            await localModelService.StartAsync(
                LlamaCppPath.Trim(), SelectedLocalModel, ContextLength, CancellationToken.None);
            var profile = ProviderProfiles.First(x => x.Id == "llamacpp");
            _initializing = true;
            SelectedProvider = profile;
            BaseUrl = profile.DefaultBaseUrl;
            _initializing = false;

            var settings = BuildCurrentSettings();
            var models = await provider.GetModelsAsync(settings, string.Empty, CancellationToken.None);
            AvailableModels.Clear();
            foreach (var availableModel in models) AvailableModels.Add(availableModel);
            Model = models.FirstOrDefault()?.Id ?? Path.GetFileNameWithoutExtension(SelectedLocalModel.Name);

            settings = BuildCurrentSettings();
            await settingsService.SaveAsync(settings, apiKey: null);
            StatusMessage = L($"已连接 {SelectedLocalModel.Name}。", $"Connected to {SelectedLocalModel.Name}.");
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Starting local GGUF model failed.");
            StatusMessage = L($"模型启动失败：{ex.Message}", $"Failed to start model: {ex.Message}");
        }
        finally
        {
            _initializing = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void UseDetectedService()
    {
        if (SelectedDetectedService is null) return;
        SelectedProvider = SelectedDetectedService.Profile;
        BaseUrl = SelectedDetectedService.Profile.DefaultBaseUrl;
        AvailableModels.Clear();
        foreach (var model in SelectedDetectedService.Models) AvailableModels.Add(model);
        if (AvailableModels.Count > 0) Model = AvailableModels[0].Id;
        StatusMessage = L($"已选择 {SelectedDetectedService.Profile.DisplayName}，保存后生效。", $"Selected {SelectedDetectedService.Profile.DisplayName}. Save to apply.");
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        ApiKey = string.Empty;
        _clearApiKey = true;
        StatusMessage = L("API Key 将在下次保存时清除。", "The API key will be cleared on the next save.");
    }

    private bool TryBuildSettings(out ProviderSettings settings, bool requireModel = true)
    {
        settings = BuildCurrentSettings();
        if (!ProviderSettings.IsValidBaseUrl(settings.BaseUrl))
        {
            StatusMessage = L("请输入有效的 Base URL。", "Enter a valid Base URL.");
            return false;
        }
        if (requireModel && string.IsNullOrWhiteSpace(settings.Model))
        {
            StatusMessage = L("请填写模型名称。", "Enter a model name.");
            return false;
        }
        if (SelectedProvider?.RequiresApiKey == true && string.IsNullOrWhiteSpace(ApiKey))
        {
            StatusMessage = L($"{SelectedProvider.DisplayName} 需要 API Key。", $"{SelectedProvider.DisplayName} requires an API key.");
            return false;
        }
        if (Temperature is < 0 or > 2 || TopP is < 0 or > 1 || MaxTokens <= 0 || ContextLength <= 0)
        {
            StatusMessage = L("生成参数超出有效范围。", "Generation parameters are outside the valid range.");
            return false;
        }
        if (settings.HotkeysEnabled &&
            (!GlobalHotkeyService.TryParse(settings.ToggleWindowHotkey, out _, out _) ||
             !GlobalHotkeyService.TryParse(settings.BossKeyHotkey, out _, out _)))
        {
            StatusMessage = L("热键格式无效，示例：Ctrl+Alt+T。", "Invalid hotkey format, e.g. Ctrl+Alt+T.");
            return false;
        }
        return true;
    }

    private ProviderSettings BuildCurrentSettings()
    {
        return new ProviderSettings
        {
            LanguageCode = SelectedLanguage?.Code ?? LocalizationService.Chinese,
            ThemeMode = SelectedTheme?.Code ?? ThemeService.ToCode(AppThemeMode.System),
            ProviderId = SelectedProvider?.Id ?? "openai-compatible",
            BaseUrl = BaseUrl.Trim(),
            Model = Model.Trim(),
            Temperature = Temperature,
            TopP = TopP,
            MaxTokens = MaxTokens,
            ContextLength = ContextLength,
            TrimHistoryToContext = TrimHistoryToContext,
            AutoScanLocalModels = AutoScanLocalModels,
            LocalModelDirectory = LocalModelDirectory.Trim(),
            LlamaCppPath = LlamaCppPath.Trim(),
            SelectedLocalModelPath = SelectedLocalModel?.FilePath ?? string.Empty,
            IncludeCharacterContext = IncludeCharacterContext,
            IncludeKnowledgeContext = IncludeKnowledgeContext,
            IncludeImageContext = IncludeImageContext,
            CharacterMemoryEnabled = CharacterMemoryEnabled,
            HotkeysEnabled = HotkeysEnabled,
            ToggleWindowHotkey = ToggleWindowHotkey.Trim(),
            BossKeyHotkey = BossKeyHotkey.Trim()
        };
    }

    private void RefreshLocalModelFiles(string? selectedPath)
    {
        LocalModels.Clear();
        foreach (var localModel in localModelService.Scan(LocalModelDirectory.Trim())) LocalModels.Add(localModel);
        SelectedLocalModel = LocalModels.FirstOrDefault(x =>
            string.Equals(x.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? LocalModels.FirstOrDefault();
    }

    partial void OnApiKeyChanged(string value)
    {
        if (!string.IsNullOrEmpty(value)) _clearApiKey = false;
    }

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_initializing || value is null) return;
        localizationService.SetLanguage(value.Code);
        _ = PersistLanguageAsync(value.Code);
    }

    private async Task PersistLanguageAsync(string languageCode)
    {
        try
        {
            await settingsService.SaveLanguageAsync(languageCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save interface language.");
            StatusMessage = localizationService.Text("语言设置保存失败，请查看日志。", "Failed to save language setting. Check the log.");
        }
    }

    private void WatchLanguageChanges()
    {
        if (_languageWatched) return;
        _languageWatched = true;
        localizationService.LanguageChanged += () => RefreshThemes(SelectedTheme?.Code);
    }

    private void RefreshThemes(string? selectedCode)
    {
        _rebuildingThemes = true;
        try
        {
            Themes = new List<ThemeOption>
            {
                new(ThemeService.ToCode(AppThemeMode.System), L("跟随系统外观", "Follow system appearance")),
                new(ThemeService.ToCode(AppThemeMode.Light), L("浅色", "Light")),
                new(ThemeService.ToCode(AppThemeMode.Dark), L("深色", "Dark")),
            };
            SelectedTheme = Themes.FirstOrDefault(x => x.Code == selectedCode) ?? Themes[0];
        }
        finally
        {
            _rebuildingThemes = false;
        }
        OnPropertyChanged(nameof(Themes));
    }

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (_initializing || _rebuildingThemes || value is null) return;
        ThemeService.Apply(value.Code);
        _ = PersistThemeAsync(value.Code);
    }

    private async Task PersistThemeAsync(string themeCode)
    {
        try
        {
            await settingsService.SaveThemeModeAsync(themeCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save theme setting.");
            StatusMessage = L("外观设置保存失败，请查看日志。", "Failed to save appearance setting. Check the log.");
        }
    }

    partial void OnSelectedProviderChanged(ProviderProfile? value)
    {
        if (_initializing || value is null || string.IsNullOrWhiteSpace(value.DefaultBaseUrl)) return;
        BaseUrl = value.DefaultBaseUrl;
        AvailableModels.Clear();
        StatusMessage = L($"已选择 {value.DisplayName}。", $"Selected {value.DisplayName}.");
    }

    private string L(string chinese, string english) => localizationService.Text(chinese, english);
}
