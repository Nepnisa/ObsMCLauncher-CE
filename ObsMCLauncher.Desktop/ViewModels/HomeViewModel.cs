using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using ObsMCLauncher.Core.Models;
using ObsMCLauncher.Core.Plugins;
using ObsMCLauncher.Core.Services;
using ObsMCLauncher.Core.Services.Minecraft;
using ObsMCLauncher.Core.Services.Ui;
using ObsMCLauncher.Core.Utils;
using ObsMCLauncher.Desktop.ViewModels.Notifications;
using ObsMCLauncher.Desktop.ViewModels.Dialogs;
using ObsMCLauncher.Desktop.Views;

namespace ObsMCLauncher.Desktop.ViewModels;

public partial class HomeViewModel : ViewModelBase, IDisposable
{
    private readonly ObsMCLauncher.Core.Services.Ui.IDispatcher _dispatcher;
    private readonly NotificationService _notificationService;
    private readonly DialogService _dialogService;

    public ObservableCollection<ObsMCLauncher.Core.Services.Minecraft.InstalledVersion> InstalledVersions { get; } = new();

    public ObservableCollection<GameAccount> Accounts { get; } = new();

    public ObservableCollection<HomeCardInfo> HomeCards { get; } = new();

    public ObservableCollection<HomeRowViewModel> HomeRows { get; } = new();

    private bool _hasAccounts = true;
    public bool HasAccounts
    {
        get => _hasAccounts;
        private set
        {
            if (SetProperty(ref _hasAccounts, value))
            {
                OnPropertyChanged(nameof(CanLaunch));
            }
        }
    }

    public bool HasInstalledVersions => InstalledVersions.Count > 0;

    public bool CanLaunch => HasAccounts && SelectedInstalledVersion != null && !IsLaunching;

    private string _persistedAccountId = "";

    private readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap> _avatarCache = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _avatarLoading = new();

    private GameAccount? _selectedAccount;
    public GameAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                if (value != null && (!value.IsDefault || _persistedAccountId != value.Id))
                {
                    if (!value.IsDefault)
                    {
                        ObsMCLauncher.Core.Services.Accounts.AccountService.Instance.SetDefaultAccount(value.Id);
                    }

                    var config = LauncherConfig.Load();
                    config.SelectedAccountId = value.Id;
                    config.Save();
                    _persistedAccountId = value.Id;

                    if (NavigationStore.MainWindow?.AccountManagement is { } accountVm)
                    {
                        foreach (var w in accountVm.Items)
                        {
                            w.Account.IsDefault = w.Account.Id == value.Id;
                        }
                    }
                }
            }
        }
    }

    private bool _showGameLog;
    public bool ShowGameLog
    {
        get => _showGameLog;
        set
        {
            if (SetProperty(ref _showGameLog, value))
            {
                var config = LauncherConfig.Load();
                config.ShowGameLogOnLaunch = value;
                config.Save();
            }
        }
    }

    private ObsMCLauncher.Core.Services.Minecraft.InstalledVersion? _selectedInstalledVersion;
    public ObsMCLauncher.Core.Services.Minecraft.InstalledVersion? SelectedInstalledVersion
    {
        get => _selectedInstalledVersion;
        set
        {
            if (SetProperty(ref _selectedInstalledVersion, value))
            {
                OnPropertyChanged(nameof(CanLaunch));

                if (value != null)
                {
                    try
                    {
                        ObsMCLauncher.Core.Services.Minecraft.LocalVersionService.SetSelectedVersion(value.Id);
                        var config = LauncherConfig.Load();
                        config.SelectedVersion = value.Id;
                        config.Save();
                        SelectedVersionId = value.Id;
                        OpenVersionDetailCommand.NotifyCanExecuteChanged();
                        LaunchCommand.NotifyCanExecuteChanged();
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Error("Home", $"选择版本失败: {ex.Message}");
                    }
                }
            }
        }
    }

    private string? _selectedVersionId;
    public string? SelectedVersionId
    {
        get => _selectedVersionId;
        set
        {
            if (SetProperty(ref _selectedVersionId, value))
            {
                OpenVersionDetailCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool _isLaunching;
    public bool IsLaunching
    {
        get => _isLaunching;
        set
        {
            if (SetProperty(ref _isLaunching, value))
            {
                LaunchCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanLaunch));
            }
        }
    }

    public IRelayCommand OpenVersionDetailCommand { get; }
    public IAsyncRelayCommand LaunchCommand { get; }

    public InstanceViewModel InstanceViewModel { get; }

    internal const string IconRocket = "M12 2C12 2 6 6 6 12C6 15.31 7.79 18.17 10.5 19.71L12 23L13.5 19.71C16.21 18.17 18 15.31 18 12C18 6 12 2 12 2M12 10C10.9 10 10 9.1 10 8C10 6.9 10.9 6 12 6C13.1 6 14 6.9 14 8C14 9.1 13.1 10 12 10M12 20C12 20 8 17.86 8 12C8 10.5 8.5 9.24 9.3 8.17C9.86 8.69 10.42 9.12 11.16 9.44C12.62 10.08 14.55 10.37 15.5 10.05C15.76 11.5 15.37 12.6 15 13.43C14.5 14.53 12 20 12 20Z";
    internal const string IconNews = "M20 2H4C2.9 2 2 2.9 2 4V22L6 18H20C21.1 18 22 17.1 22 16V4C22 2.9 21.1 2 20 2M20 16H5.17L4 17.17V4H20V16M7 9H17V7H7V9M7 13H14V11H7V13Z";
    internal const string IconGlobe = "M12 2C6.48 2 2 6.48 2 12S6.48 22 12 22 22 17.52 22 12 17.52 2 12 2M12 20C11.1 20 10.21 19.88 9.36 19.67L10 18L12 16L13.34 13.09L14.35 12H17.5C18.2 12 18.85 12.26 19.35 12.67C18.37 16.8 15.48 20 12 20M7 9L5.77 11.13L5.25 11.77C5.08 11.23 5 10.65 5 10C5 8.94 5.26 7.94 5.71 7.06L7 9M19 10.25C18.03 9.21 16.57 8.5 15 8.5H12.86L10 9.63V12.38L12.41 14.79L13.07 13.25L15 12.5L17 14.5V17.13C14.24 18.37 11 18.37 8.24 17.13L6.83 16.71L6.4 16.29L5.03 16.72C5.16 17.5 5.41 18.25 5.75 18.94C7.21 20.91 9.43 22.02 12 22C16.42 22 20 18.42 20 14C20 12.72 19.65 11.52 19.03 10.5L19 10.25Z";
    internal const string IconDownload = "M19 9H15V3H9V9H5L12 16L19 9M5 18V20H19V18H5Z";

    public HomeViewModel(ObsMCLauncher.Core.Services.Ui.IDispatcher dispatcher, NotificationService notificationService)
    {
        _dispatcher = dispatcher;
        _notificationService = notificationService;
        _dialogService = new DialogService();

        AccountEvents.AccountsChanged += OnAccountsChanged;

        InstanceViewModel = new InstanceViewModel(notificationService);

        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => CanLaunch);

        OpenVersionDetailCommand = new RelayCommand(OpenVersionDetail, CanOpenVersionDetail);

        var config = LauncherConfig.Load();
        SelectedVersionId = config.SelectedVersion;
        _showGameLog = config.ShowGameLogOnLaunch;
        _persistedAccountId = config.SelectedAccountId ?? "";

        InitializeHomeData();

        _ = LoadLocalAsync();
    }

    public void Dispose()
    {
        AccountEvents.AccountsChanged -= OnAccountsChanged;
        GC.SuppressFinalize(this);
    }

    private void OnAccountsChanged() => RefreshAccounts();

    private void InitializeHomeData()
    {
        HomeCards.Clear();

        var config = LauncherConfig.Load();
        var cardConfigs = config.HomeCards ?? new();

        var defaultCards = new List<HomeCardInfo>
        {
            new HomeCardInfo { CardId = HomeCardInfo.WelcomeCardId, Title = "欢迎使用 ObsMCLauncher-CE", Description = "开始你的 Minecraft 之旅 (v1.0.0)", Icon = IconRocket, Order = 0 },
            new HomeCardInfo { CardId = "news", Title = "查看最新的 Minecraft 新闻", Description = "了解游戏动态", Icon = IconNews, CommandId = "url:https://zh.minecraft.wiki/", Order = 1 },
            new HomeCardInfo { CardId = "multiplayer", Title = "多人联机", Description = "加入服务器与好友一起游戏", Icon = IconGlobe, CommandId = "navigate:multiplayer", Order = 2 },
            new HomeCardInfo { CardId = "mods", Title = "资源下载", Description = "下载Mod、材质包等资源", Icon = IconDownload, CommandId = "navigate:resources", Order = 3 }
        };

        foreach (var card in defaultCards)
        {
            var cardConfig = cardConfigs.FirstOrDefault(c => c.CardId == card.CardId);
            card.IsEnabled = cardConfig?.IsEnabled ?? true;
            card.Order = cardConfig?.Order ?? defaultCards.IndexOf(card);
        }

        foreach (var card in defaultCards.OrderBy(c => c.Order))
        {
            HomeCards.Add(card);
        }

        BuildHomeRows();
        LoadAccounts();
    }

    private void BuildHomeRows()
    {
        HomeRows.Clear();

        var layout = LauncherConfig.Load().GetHomeLayout();
        DebugLogger.Info("Home", $"BuildHomeRows: layout has {layout.Rows.Count} rows, {layout.Rows.Sum(r => r.Components.Count)} components total");

        foreach (var row in layout.Rows)
        {
            var rowVm = new HomeRowViewModel();
            foreach (var comp in row.Components)
            {
                var vm = CreateComponentVM(comp.Id, comp.Size);
                if (vm != null)
                    rowVm.Components.Add(vm);
                else
                    DebugLogger.Warn("Home", $"BuildHomeRows: skipped component '{comp.Id}'");
            }
            HomeRows.Add(rowVm);
        }

        DebugLogger.Info("Home", $"BuildHomeRows: {HomeRows.Count} rows built, {HomeRows.Sum(r => r.Components.Count)} components");
    }

    private HomeComponentViewModel? CreateComponentVM(string id, HomeCardSize size)
    {
        var vm = CreateDataComponentVM(id);
        if (vm == null) return null;
        vm.Id = id;
        vm.Owner = this;
        vm.Size = size;
        return vm;
    }

    private HomeComponentViewModel? CreateDataComponentVM(string id)
    {
        var card = HomeCards.FirstOrDefault(c => c.CardId == id);
        if (id == HomeComponentRegistry.WelcomeId)
            return card != null ? new WelcomeComponentViewModel { Card = card } : null;
        return card != null ? new CardComponentViewModel { Card = card } : null;
    }

    private void RemoveComponentFromRows(string id)
    {
        foreach (var row in HomeRows)
        {
            var vm = row.Components.FirstOrDefault(c => c.Id == id);
            if (vm != null)
                row.Components.Remove(vm);
        }

        var config = LauncherConfig.Load();
        if (config.GetHomeLayout().Remove(id))
            config.Save();
    }

    public void PersistHomeLayout()
    {
        var config = LauncherConfig.Load();
        config.HomeLayout = new HomeLayoutConfig
        {
            Rows = HomeRows.Select(r => new HomeRowConfig
            {
                Components = r.Components.Select(c => new HomeComponentConfig { Id = c.Id, Size = c.Size }).ToList()
            }).ToList()
        };

        config.HomeCards = HomeCards.Select((c, i) => new HomeCardConfig
        {
            CardId = c.CardId,
            IsEnabled = c.IsEnabled,
            Order = i,
            IsPluginCard = c.IsPluginCard,
            PluginId = c.PluginId
        }).ToList();

        config.Save();
    }

    public HomeComponentViewModel? AddComponentToRow(string componentId, HomeRowViewModel row, int index)
    {
        var descriptor = HomeComponentRegistry.TryGet(componentId);
        var size = descriptor?.DefaultSize ?? HomeCardSize.Medium;
        if (!row.CanAccept(size))
        {
            DebugLogger.Warn("Home", $"row cannot accept component '{componentId}' (size={size})");
            return null;
        }

        var vm = CreateComponentVM(componentId, size);
        if (vm == null) return null;

        if (index < 0 || index > row.Components.Count)
            index = row.Components.Count;
        row.Components.Insert(index, vm);
        PersistHomeLayout();
        return vm;
    }

    public void RemoveComponent(HomeComponentViewModel component)
    {
        var row = HomeRows.FirstOrDefault(r => r.Components.Contains(component));
        row?.Components.Remove(component);
        PersistHomeLayout();
    }

    public void MoveComponent(HomeComponentViewModel component, HomeRowViewModel targetRow, int targetIndex)
    {
        var sourceRow = HomeRows.FirstOrDefault(r => r.Components.Contains(component));
        if (sourceRow == null) return;

        if (!targetRow.CanAccept(component.Size, component))
        {
            DebugLogger.Warn("Home", "move rejected: target row is full");
            return;
        }

        sourceRow.Components.Remove(component);
        if (ReferenceEquals(sourceRow, targetRow) && targetIndex > sourceRow.Components.Count)
            targetIndex = sourceRow.Components.Count;
        if (targetIndex < 0 || targetIndex > targetRow.Components.Count)
            targetIndex = targetRow.Components.Count;
        targetRow.Components.Insert(targetIndex, component);
        PersistHomeLayout();
    }

    public HomeRowViewModel InsertRow(int index)
    {
        var row = new HomeRowViewModel();
        if (index < 0 || index > HomeRows.Count)
            index = HomeRows.Count;
        HomeRows.Insert(index, row);
        PersistHomeLayout();
        return row;
    }

    public bool RemoveRow(HomeRowViewModel row)
    {
        if (HomeRows.Count <= 1) return false;
        if (!HomeRows.Remove(row)) return false;
        PersistHomeLayout();
        return true;
    }

    public void SetComponentSize(HomeComponentViewModel component, HomeCardSize size)
    {
        if (component.Size == size) return;
        var row = HomeRows.FirstOrDefault(r => r.Components.Contains(component));
        if (row == null)
        {
            component.Size = size;
            PersistHomeLayout();
            return;
        }

        if (size == HomeCardSize.Fill && row.Components.Count > 1)
        {
            row.Components.Remove(component);
            var idx = HomeRows.IndexOf(row);
            var newRow = InsertRow(idx + 1);
            newRow.Components.Add(component);
            component.Size = size;
            PersistHomeLayout();
            return;
        }

        if (!row.CanAccept(size, component))
        {
            if ((int)size > (int)component.Size)
            {
                row.Components.Remove(component);
                var idx = HomeRows.IndexOf(row);
                var newRow = InsertRow(idx + 1);
                newRow.Components.Add(component);
            }
            else
            {
                DebugLogger.Warn("Home", $"size change rejected: row cannot fit size={size}");
                return;
            }
        }

        component.Size = size;
        PersistHomeLayout();
    }

    public void ResetHomeLayout()
    {
        var config = LauncherConfig.Load();
        config.HomeLayout = HomeLayoutConfig.CreateDefault(config.HomeCards);
        config.Save();
        BuildHomeRows();
    }

    public void ForceRebuildRows() => BuildHomeRows();

    public void OnPluginCardRegistered(string cardId, string title, string description, string? icon, string? commandId, object? payload)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var config = LauncherConfig.Load();
            var cardConfig = config.HomeCards.FirstOrDefault(c => c.CardId == cardId);
            var isEnabled = cardConfig?.IsEnabled ?? true;

            var existing = HomeCards.FirstOrDefault(c => c.CardId == cardId);
            if (existing != null)
            {
                existing.Title = title;
                existing.Description = description;
                existing.Icon = icon;
                existing.CommandId = commandId;
                existing.Payload = payload;
                existing.IsEnabled = isEnabled;
            }
            else
            {
                var newCard = new HomeCardInfo
                {
                    CardId = cardId,
                    Title = title,
                    Description = description,
                    Icon = icon,
                    CommandId = commandId,
                    Payload = payload,
                    IsPluginCard = true,
                    PluginId = cardId.Split('.')[0],
                    IsEnabled = isEnabled
                };
                HomeCards.Add(newCard);
            }

            NotifySettingsViewModelRefreshPluginCards();
        });
    }

    private void NotifySettingsViewModelRefreshPluginCards()
    {
        NavigationStore.MainWindow?.Settings?.SettingsHome.RefreshLibrary();
    }

    public void OnPluginCardUnregistered(string cardId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var card = HomeCards.FirstOrDefault(c => c.CardId == cardId);
            if (card != null && card.IsPluginCard)
                HomeCards.Remove(card);
            RemoveComponentFromRows(cardId);
        });
    }

    public void RemoveAllPluginCards(string pluginId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var cardsToRemove = HomeCards.Where(c => c.IsPluginCard && c.PluginId == pluginId).ToList();
            foreach (var card in cardsToRemove)
                HomeCards.Remove(card);

            var prefix = pluginId + ".";
            foreach (var row in HomeRows)
            {
                foreach (var vm in row.Components.Where(c => c.Id.StartsWith(prefix)).ToList())
                    row.Components.Remove(vm);
            }

            var config = LauncherConfig.Load();
            var configToRemove = config.HomeCards.Where(c => c.IsPluginCard && c.PluginId == pluginId).ToList();
            foreach (var cfg in configToRemove)
                config.HomeCards.Remove(cfg);

            var layout = config.GetHomeLayout();
            var layoutChanged = false;
            foreach (var row in layout.Rows)
            {
                if (row.Components.RemoveAll(c => c.Id.StartsWith(prefix)) > 0)
                    layoutChanged = true;
            }
            if (layoutChanged)
                layout.RemoveEmptyRows();
            config.Save();

            DebugLogger.Info("Home", $"已移除插件 {pluginId} 的所有卡片，共 {cardsToRemove.Count} 个");
        });
    }

    [RelayCommand]
    private void CardClick(HomeCardInfo? card)
    {
        if (card == null || string.IsNullOrEmpty(card.CommandId)) return;

        if (card.CommandId.StartsWith("navigate:"))
            NavigateToNavPage(card.CommandId.Substring(9));
        else if (card.CommandId.StartsWith("url:"))
        {
            var url = card.CommandId.Substring(4);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true }); }
            catch { }
        }
        else if (card.CommandId.StartsWith("command:"))
        {
            var commandId = card.CommandId.Substring(8);
            PluginContext.ExecuteCommand(commandId, card.Payload);
        }
    }

    [RelayCommand]
    private void GoTo(string page)
    {
        if (string.IsNullOrWhiteSpace(page)) return;
        NavigateToNavPage(page);
    }

    private void NavigateToNavPage(string page)
    {
        if (string.IsNullOrWhiteSpace(page)) return;
        NavigationStore.MainWindow?.NavToPage(page);
    }

    private void LoadAccounts()
    {
        var accounts = ObsMCLauncher.Core.Services.Accounts.AccountService.Instance.GetAllAccounts();
        var newIds = new HashSet<string>(accounts.Select(a => a.Id));

        foreach (var id in _avatarCache.Keys.ToList())
        {
            if (!newIds.Contains(id) && _avatarCache[id] is IDisposable d)
            {
                d.Dispose();
                _avatarCache.Remove(id);
            }
        }

        Accounts.Clear();
        foreach (var acc in accounts)
            Accounts.Add(acc);

        HasAccounts = Accounts.Count > 0;
        SelectLastAccount();
        LoadAccountAvatars();
    }

    public void RefreshAccounts() => LoadAccounts();

    public void RefreshHomeCards()
    {
        var pluginCards = HomeCards.Where(c => c.IsPluginCard).ToList();
        InitializeHomeData();
        foreach (var pluginCard in pluginCards)
        {
            var existingCard = HomeCards.FirstOrDefault(c => c.CardId == pluginCard.CardId);
            if (existingCard == null)
                HomeCards.Add(pluginCard);
            else
                existingCard.IsEnabled = pluginCard.IsEnabled;
        }
        BuildHomeRows();

        _dispatcher.InvokeAsync(() =>
        {
            var config = LauncherConfig.Load();
            var cardConfigs = config.HomeCards.Where(c => c.IsPluginCard).ToList();
            foreach (var cardConfig in cardConfigs)
            {
                var existingCard = HomeCards.FirstOrDefault(c => c.CardId == cardConfig.CardId);
                if (existingCard != null)
                    existingCard.IsEnabled = cardConfig.IsEnabled;
            }
            BuildHomeRows();
        });
    }

    private void SelectLastAccount()
    {
        if (!string.IsNullOrEmpty(_persistedAccountId))
            SelectedAccount = Accounts.FirstOrDefault(a => a.Id == _persistedAccountId);
        if (SelectedAccount == null)
            SelectedAccount = Accounts.FirstOrDefault(a => a.IsDefault) ?? Accounts.FirstOrDefault();
    }

    private void LoadAccountAvatars()
    {
        foreach (var acc in Accounts)
        {
            if (_avatarCache.TryGetValue(acc.Id, out var cached))
            {
                SetAvatar(acc, cached);
                continue;
            }

            if (!_avatarLoading.TryAdd(acc.Id, 0)) continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    var skinPath = await SkinService.Instance.GetSkinPathAsync(acc);
                    if (!string.IsNullOrEmpty(skinPath) && File.Exists(skinPath))
                    {
                        var bitmap = SkinHeadRenderer.GetHeadFromSkin(skinPath);
                        if (bitmap != null)
                        {
                            await _dispatcher.InvokeAsync(() =>
                            {
                                _avatarCache[acc.Id] = bitmap;
                                SetAvatar(acc, bitmap);
                            });
                            return;
                        }
                    }

                    await _dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            using var defaultAvatar = AssetLoader.Open(new Uri("avares://ObsMCLauncher.Desktop/Assets/logo.png"));
                            if (defaultAvatar != null)
                            {
                                var bitmap = new Avalonia.Media.Imaging.Bitmap(defaultAvatar);
                                _avatarCache[acc.Id] = bitmap;
                                SetAvatar(acc, bitmap);
                            }
                        }
                        catch { }
                    });
                }
                catch { }
                finally
                {
                    _avatarLoading.TryRemove(acc.Id, out _);
                }
            });
        }
    }

    private void SetAvatar(GameAccount acc, object? newAvatar)
    {
        var old = acc.Avatar;
        if (!ReferenceEquals(old, newAvatar))
        {
            if (old is IDisposable oldDisposable)
                oldDisposable.Dispose();
            acc.Avatar = newAvatar;
        }
    }

    private bool CanOpenVersionDetail() => SelectedInstalledVersion != null;

    private void OpenVersionDetail()
    {
        if (SelectedInstalledVersion == null) return;
        InstanceViewModel.SetVersion(SelectedInstalledVersion);
    }

    public async Task LoadLocalAsync()
    {
        try
        {
            var config = LauncherConfig.Load();
            var gameDir = config.GameDirectory;
            var list = ObsMCLauncher.Core.Services.Minecraft.LocalVersionService.GetInstalledVersions(gameDir);

            await _dispatcher.InvokeAsync(() =>
            {
                InstalledVersions.Clear();
                foreach (var v in list) InstalledVersions.Add(v);
                OnPropertyChanged(nameof(HasInstalledVersions));

                var selectedId = ObsMCLauncher.Core.Services.Minecraft.LocalVersionService.GetSelectedVersion();
                SelectedVersionId = selectedId;
                SelectedInstalledVersion = InstalledVersions.FirstOrDefault(x => x.Id == selectedId);
            });
        }
        catch (Exception ex)
        {
            DebugLogger.Error("Home", $"本地版本扫描失败: {ex.Message}");
        }
    }

    // ===== 总游玩时长 =====
    public string TotalPlayTimeDisplay
    {
        get
        {
            var config = LauncherConfig.Load();
            var seconds = config.TotalPlayTimeSeconds;
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalDays >= 1)
                return $"🎮 总游玩 {ts.Days} 天 {ts.Hours} 小时";
            if (ts.TotalHours >= 1)
                return $"🎮 总游玩 {ts.Hours} 小时 {ts.Minutes} 分钟";
            if (ts.TotalMinutes >= 1)
                return $"🎮 总游玩 {ts.Minutes} 分钟";
            return "🎮 还没有游戏记录";
        }
    }

    private async Task LaunchAsync()
    {
        if (SelectedInstalledVersion == null || SelectedAccount == null)
        {
            _notificationService.Show("无法启动", "请先选择游戏版本和账号", NotificationType.Warning);
            return;
        }

        var launchCts = new System.Threading.CancellationTokenSource();
        var versionId = SelectedInstalledVersion.Id;
        var account = SelectedAccount;

        try
        {
            IsLaunching = true;
            var config = LauncherConfig.Load();

            var notifId = _notificationService.Show("正在启动", "正在检查游戏完整性...", NotificationType.Progress, cts: launchCts);

            var integrity = await ObsMCLauncher.Core.Services.GameLauncher.CheckGameIntegrityAsync(
                versionId,
                config,
                (msg) =>
                {
                    if (msg.Contains("|"))
                    {
                        var parts = msg.Split('|');
                        if (double.TryParse(parts[1], out double p))
                        {
                            _notificationService.Update(notifId, parts[0], p);
                            return;
                        }
                    }
                    _notificationService.Update(notifId, msg);
                },
                launchCts.Token);

            if (integrity.HasIssue && integrity.MissingLibraries.Count > 0)
            {
                var missingCount = integrity.MissingLibraries.Count;
                _notificationService.Update(notifId, $"正在补全 {missingCount} 个缺失依赖...", 0);

                try
                {
                    var (successCount, failedCount) = await ObsMCLauncher.Core.Services.LibraryDownloader.DownloadMissingLibrariesAsync(
                        config.GameDirectory,
                        versionId,
                        integrity.MissingLibraries,
                        (progress, current, total) =>
                        {
                            _notificationService.Update(notifId, progress, current * 100.0 / Math.Max(1, total));
                        },
                        launchCts.Token);

                    if (failedCount > 0)
                    {
                        _notificationService.Show("依赖补全失败", $"{failedCount} 个必需库文件下载失败，请检查网络后重试", NotificationType.Error);
                        _notificationService.Remove(notifId);
                        return;
                    }

                    _notificationService.Update(notifId, $"已成功补全 {successCount} 个依赖", 100);
                }
                catch (Exception dlEx)
                {
                    _notificationService.Show("依赖补全失败", dlEx.Message, NotificationType.Error);
                    _notificationService.Remove(notifId);
                    return;
                }
            }

            var modsDir = Path.Combine(config.GetRunDirectory(versionId), "mods");
            var conflicts = ObsMCLauncher.Core.Services.ModConflictDetector.DetectConflicts(modsDir);
            var errors = conflicts.Where(c => c.Severity == ObsMCLauncher.Core.Services.ConflictSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                var conflictMsg = string.Join("\n", errors.Select(c => c.Description));
                var result = await _dialogService.ShowQuestion(
                    "检测到模组冲突",
                    $"发现 {errors.Count} 个严重冲突，可能导致游戏崩溃：\n\n{conflictMsg}\n\n是否仍要启动游戏？");
                if (result != DialogResult.Yes)
                {
                    _notificationService.Remove(notifId);
                    return;
                }
            }

            GameLogWindow? logWindow = null;
            if (config.ShowGameLogOnLaunch)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    logWindow = new GameLogWindow(versionId);
                    logWindow.Show();
                });
            }

            _notificationService.Update(notifId, "正在启动 Minecraft...");

            var launchResult = await ObsMCLauncher.Core.Services.GameLauncher.LaunchGameAsync(
                versionId,
                account,
                config,
                (progress) => _notificationService.Update(notifId, progress),
                (output) => logWindow?.AppendGameOutput(output),
                (exitCode) =>
                {
                    logWindow?.OnGameExit(exitCode);
                    // 记录游玩时长
                    if (config.LastGameStartTime.HasValue)
                    {
                        var elapsed = (DateTime.Now - config.LastGameStartTime.Value).TotalSeconds;
                        config.TotalPlayTimeSeconds += (long)elapsed;
                        config.LastGameStartTime = null;
                        config.Save();
                        // 刷新主页显示
                        OnPropertyChanged(nameof(TotalPlayTimeDisplay));
                    }
                    _dispatcher.InvokeAsync(() =>
                        _notificationService.Show(
                            "游戏退出",
                            $"游戏已退出，退出代码: {exitCode}",
                            exitCode == 0 ? NotificationType.Info : NotificationType.Warning));
                },
                launchCts.Token);

            _notificationService.Remove(notifId);

            if (launchResult.Success)
            {
                _notificationService.Show("启动成功", $"Minecraft {versionId} 已成功拉起", NotificationType.Success);

                if (config.CloseAfterLaunch)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                            desktop.MainWindow?.Close();
                    });
                }
            }
            else
            {
                _notificationService.Show("启动失败", string.IsNullOrEmpty(launchResult.ErrorMessage) ? "请检查日志或Java配置" : launchResult.ErrorMessage, NotificationType.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _notificationService.Show("已取消", "启动流程已取消", NotificationType.Info);
        }
        catch (Exception ex)
        {
            _notificationService.Show("启动异常", ex.Message, NotificationType.Error);
        }
        finally
        {
            IsLaunching = false;
            launchCts.Dispose();
        }
    }
}