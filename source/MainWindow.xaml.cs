using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CodexTokenMonitor;

public partial class MainWindow : Window
{
    private const int RecentConversationLimit = 5;
    private readonly SessionParser _parser = new();
    private readonly DispatcherTimer _eventDebounce = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _expiryTimer = new();
    private AccountUsageSnapshot? _accountSnapshot;
    private readonly bool _startMonitoring;
    private FileSystemWatcher? _watcher;
    private bool _forceEventRefresh;
    private bool _refreshing;
    private bool _refreshPending;
    private string _lastPath = "";
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private SessionSummary? _currentData;
    private string? _selectedScopeKey;
    private string? _loadedSessionId;
    private bool _modelSelectionPinned;
    private bool _updatingModelSelector;
    private string? _selectedSessionPath;
    private bool _updatingConversationSelector;
    private bool _closed;
    private int _selectionVersion;
    private DetailWindow? _details;
    private readonly MonitorSettings _settings = MonitorSettings.Load();
    private List<SessionChoice> _recentChoices = [];
    private List<SessionChoice> _searchChoices = [];
    private SessionChoice? _selectedChoice;
    private TextBox? _taskSearchBox;
    private bool _searchLoading;
    private bool _searchFailed;
    private int _searchGeneration;
    private string? _lastReadError;
    private string? _watcherError;
    private DateTime? _lastSuccessfulRead;

    public MainWindow(bool startTimer)
    {
        _startMonitoring = startTimer;
        InitializeComponent();
        _expiryTimer.Tick += (_, _) => RefreshQuotaPresentation();
        Topmost = _settings.Topmost;
        if (_startMonitoring && _settings.Left is { } left && _settings.Top is { } top
            && left >= SystemParameters.VirtualScreenLeft && left + Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && top >= SystemParameters.VirtualScreenTop && top + Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        { WindowStartupLocation = WindowStartupLocation.Manual; Left = left; Top = top; }
        DetailsButton.Click += (_, _) => ShowDetails();
        PinItem.IsChecked = Topmost;
        PinItem.Click += (_, _) => { Topmost = PinItem.IsChecked; if (_startMonitoring) SaveSettings(); };
        RemainingQuotaItem.IsChecked = _settings.ShowRemainingQuota;
        RemainingQuotaItem.Click += (_, _) =>
        {
            _settings.ShowRemainingQuota = RemainingQuotaItem.IsChecked;
            RefreshQuotaPresentation();
            if (_startMonitoring) SaveSettings();
        };
        MenuButton.Click += (_, _) => { MenuButton.ContextMenu.PlacementTarget = MenuButton; MenuButton.ContextMenu.IsOpen = true; };
        CloseButton.Click += (_, _) => Close();
        RefreshButton.Click += async (_, _) => await ManualRefreshAsync();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F5 && !_refreshing) { e.Handled = true; await ManualRefreshAsync(); }
        };
        ModelSelector.SelectionChanged += (_, _) => SelectModelFromDropdown();
        ConversationSelector.SelectionChanged += async (_, _) => await SelectConversationFromDropdownAsync();
        ConversationSelector.DropDownOpened += async (_, _) => await OpenTaskSearchAsync();
        ConversationSelector.DropDownClosed += (_, _) =>
        {
            _searchGeneration++;
            _searchChoices = [];
            Dispatcher.BeginInvoke(() => { if (!_closed && !ConversationSelector.IsDropDownOpen) UpdateConversationSelector(_recentChoices.ToList()); });
        };
        _eventDebounce.Tick += async (_, _) =>
        {
            _eventDebounce.Stop();
            var force = _forceEventRefresh;
            _forceEventRefresh = false;
            await RefreshAsync(force);
        };
        Loaded += async (_, _) =>
        {
            await RefreshAsync(true);
            if (_startMonitoring && !_closed) StartFileWatcher();
        };
        Closed += (_, _) => { _closed = true; _expiryTimer.Stop(); StopFileWatcher(); _details?.Close(); if (_startMonitoring) SaveSettings(); };
    }

    private void SaveSettings() { _settings.Topmost = Topmost; _settings.Left = Left; _settings.Top = Top; _settings.Save(); }
    private async Task ManualRefreshAsync()
    {
        if (_startMonitoring && _watcher is null) StartFileWatcher();
        await RefreshAsync(true);
    }
    private void ShowDetails()
    {
        if (_currentData is null) return;
        if (_details is null) { _details = new DetailWindow { Owner = this }; _details.Closed += (_, _) => _details = null; }
        _details.SetData(_currentData); _details.Show(); _details.Activate();
    }

    private async Task<SessionSummary?> RefreshAsync(bool force)
    {
        if (_refreshing)
        {
            _refreshPending = true;
            return null;
        }
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var choices = await Task.Run(() => _parser.GetRecentSessionChoices(RecentConversationLimit).ToList());
            if (_closed) return null;
            UpdateConversationSelector(choices);
            var path = _selectedSessionPath;
            var version = _selectionVersion;
            var account = await Task.Run(() => _parser.GetLatestAccountUsage());
            if (_closed) return null;
            ApplyAccountUsage(account);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                _lastReadError = null;
                _lastSuccessfulRead = DateTime.UtcNow;
                _currentData = null; ModelSelector.ItemsSource = null; ApplySelectedScope(new SessionSummary(), null);
                ContextText.Text = "--"; ContextBar.Value = 0; ConversationTotalText.Text = "Waiting for Codex…";
                DetailsButton.IsEnabled = false; return null;
            }
            var writeUtc = File.GetLastWriteTimeUtc(path);
            if (!force && path == _lastPath && writeUtc == _lastWriteUtc)
            { _lastReadError = null; _lastSuccessfulRead = DateTime.UtcNow; return null; }
            var data = await Task.Run(() => _parser.Parse(path));
            if (_closed || version != _selectionVersion) { _refreshPending = !_closed; return null; }
            data.AccountUsage = account ?? data.AccountUsage;
            _lastPath = path;
            _lastWriteUtc = writeUtc;
            ApplyData(data);
            _lastReadError = null;
            _lastSuccessfulRead = DateTime.UtcNow;
            return data;
        }
        catch (Exception ex) { _lastReadError = ex.Message; return null; }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            UpdateMonitorStatus();
            if (_refreshPending && !_closed)
            {
                _refreshPending = false;
                ScheduleEventRefresh(true);
            }
        }
    }

    private void StartFileWatcher()
    {
        if (_watcher is not null) return;
        if (!Directory.Exists(_parser.WatchRoot)) return;
        _watcher = new FileSystemWatcher(_parser.WatchRoot, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 16 * 1024
        };
        _watcher.Changed += OnCodexFileChanged;
        _watcher.Created += OnCodexFileChanged;
        _watcher.Deleted += OnCodexFileChanged;
        _watcher.Renamed += OnCodexFileChanged;
        _watcher.Error += OnWatcherError;
        try { _watcher.EnableRaisingEvents = true; _watcherError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _watcher.Dispose(); _watcher = null; _watcherError = ex.Message; }
        UpdateMonitorStatus();
    }

    private void StopFileWatcher()
    {
        _eventDebounce.Stop();
        if (_watcher is null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnCodexFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_closed) return;
        var name = Path.GetFileName(e.FullPath);
        if (!name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase) && name != "session_index.jsonl") return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                // A just-created agent may initially have no metadata. Re-discover it
                // when its first complete record arrives; parsing itself is incremental.
                QueueEventRefresh(true);
            });
        }
        catch (InvalidOperationException) { }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                StopFileWatcher();
                StartFileWatcher();
                QueueEventRefresh(true);
            });
        }
        catch (InvalidOperationException) { }
    }

    private bool IsSelectedUsagePath(string path)
    {
        if (string.Equals(path, _selectedSessionPath, StringComparison.OrdinalIgnoreCase)) return true;
        return _currentData?.AgentPaths.Contains(path, StringComparer.OrdinalIgnoreCase) == true;
    }

    private void ScheduleEventRefresh(bool force)
    {
        try { Dispatcher.BeginInvoke(() => QueueEventRefresh(force)); }
        catch (InvalidOperationException) { }
    }

    private void QueueEventRefresh(bool force)
    {
        if (_closed) return;
        _forceEventRefresh |= force;
        if (!_eventDebounce.IsEnabled) _eventDebounce.Start();
    }

    private void UpdateConversationSelector(List<SessionChoice> choices)
    {
        var selected = choices.FirstOrDefault(choice => string.Equals(choice.Path, _selectedSessionPath, StringComparison.OrdinalIgnoreCase));
        if (selected is null && !string.IsNullOrWhiteSpace(_selectedSessionPath) && File.Exists(_selectedSessionPath) && _selectedChoice is not null)
        {
            selected = _selectedChoice;
            if (choices.Count >= RecentConversationLimit) choices.RemoveAt(choices.Count - 1);
            choices.Add(selected);
        }
        selected ??= choices.FirstOrDefault();
        _recentChoices = choices;
        _selectedChoice = selected;
        _selectedSessionPath = selected?.Path;
        ConversationSelector.Tag = selected;
        // Live log refreshes must not erase a query or move keyboard focus.
        if (ConversationSelector.IsDropDownOpen) return;
        _updatingConversationSelector = true;
        ConversationSelector.ItemsSource = choices;
        ConversationSelector.SelectedItem = selected;
        _updatingConversationSelector = false;
    }

    private async Task OpenTaskSearchAsync()
    {
        var generation = ++_searchGeneration;
        if (_taskSearchBox is null)
        {
            _taskSearchBox = (TextBox)ConversationSelector.Template.FindName("TaskSearchBox", ConversationSelector);
            _taskSearchBox.TextChanged += (_, _) => FilterTaskChoices();
            _taskSearchBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { ConversationSelector.IsDropDownOpen = false; ConversationSelector.Focus(); e.Handled = true; }
                else if (e.Key == Key.Down && ConversationSelector.Items.Count > 0)
                {
                    if (ConversationSelector.ItemContainerGenerator.ContainerFromIndex(0) is ComboBoxItem item) item.Focus();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && ConversationSelector.Items.Count > 0)
                {
                    ConversationSelector.SelectedItem = ConversationSelector.Items[0];
                    ConversationSelector.IsDropDownOpen = false;
                    ConversationSelector.Focus();
                    e.Handled = true;
                }
            };
            ((Button)ConversationSelector.Template.FindName("ClearSearchButton", ConversationSelector)).Click += (_, _) => { _taskSearchBox.Clear(); _taskSearchBox.Focus(); };
        }
        _searchLoading = true;
        _searchFailed = false;
        _searchChoices = _recentChoices.ToList();
        _taskSearchBox.Clear();
        FilterTaskChoices();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (ConversationSelector.IsDropDownOpen) _taskSearchBox.Focus(); });
        try
        {
            // Independent parser: the live incremental parser is used serially by RefreshAsync.
            var choices = await Task.Run(() => new SessionParser().GetRecentSessionChoices(int.MaxValue).ToList());
            if (_closed || generation != _searchGeneration) return;
            _searchChoices = choices;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation != _searchGeneration) return;
            _searchFailed = true;
        }
        if (_closed || generation != _searchGeneration) return;
        _searchLoading = false;
        FilterTaskChoices();
    }

    private void FilterTaskChoices()
    {
        if (_taskSearchBox is null) return;
        var query = _taskSearchBox.Text.Trim();
        var matches = query.Length == 0 ? _recentChoices : _searchChoices.Where(choice =>
            choice.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || choice.SessionId.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _updatingConversationSelector = true;
        try
        {
            ConversationSelector.ItemsSource = matches;
            ConversationSelector.SelectedItem = matches.FirstOrDefault(choice => string.Equals(choice.Path, _selectedSessionPath, StringComparison.OrdinalIgnoreCase));
        }
        finally { _updatingConversationSelector = false; }
        ((TextBlock)ConversationSelector.Template.FindName("SearchPlaceholder", ConversationSelector)).Visibility = _taskSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ((Button)ConversationSelector.Template.FindName("ClearSearchButton", ConversationSelector)).Visibility = _taskSearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ((TextBlock)ConversationSelector.Template.FindName("SearchStatus", ConversationSelector)).Text = _searchLoading ? "正在加载历史任务…"
            : _searchFailed ? "历史读取失败 · 重新展开可重试"
            : query.Length == 0 ? $"最近 {matches.Count} 个 · 可搜索 {_searchChoices.Count} 个任务"
            : matches.Count == 0 ? "没有匹配任务 · 换个关键词"
            : $"找到 {matches.Count} 个任务";
    }

    private async Task SelectConversationFromDropdownAsync()
    {
        if (_updatingConversationSelector || ConversationSelector.SelectedItem is not SessionChoice choice) return;
        _selectedChoice = choice;
        _selectedSessionPath = choice.Path;
        ConversationSelector.Tag = choice;
        ConversationSelector.IsDropDownOpen = false;
        _selectionVersion++;
        _lastPath = "";
        _lastWriteUtc = DateTime.MinValue;
        await RefreshAsync(true);
    }

    private void ApplyData(SessionSummary data)
    {
        _currentData = data;
        DetailsButton.IsEnabled = true;
        DetailsButton.Content = $"任务概览 · 主模型{data.ModelCheck}";
        DetailsButton.Foreground = (Brush)FindResource(data.ModelCheck == "名称不同" ? "PurpleBrush" : data.ModelCheck == "名称一致" ? "TealBrush" : "TextBrush");
        DetailsButton.ToolTip = new ToolTip { Content = new TextBlock { Text = data.ModelCheckDetail, MaxWidth = 210, TextWrapping = TextWrapping.Wrap } };
        var isNewSession = !string.Equals(_loadedSessionId, data.SessionId, StringComparison.Ordinal);
        if (isNewSession)
        {
            _loadedSessionId = data.SessionId;
            _selectedScopeKey = null;
            _modelSelectionPinned = false;
        }

        var scopes = BuildUsageScopes(data);
        var activeScope = scopes.FirstOrDefault(scope => !scope.IsAgent && ModelsMatch(scope.ModelId, data.CurrentModel));
        var desiredScope = _modelSelectionPinned
            ? scopes.FirstOrDefault(scope => string.Equals(scope.Key, _selectedScopeKey, StringComparison.OrdinalIgnoreCase))
            : activeScope;
        desiredScope ??= scopes.LastOrDefault(scope => !scope.IsAgent) ?? scopes.LastOrDefault();

        _updatingModelSelector = true;
        ModelSelector.ItemsSource = scopes;
        ModelSelector.SelectedItem = desiredScope;
        ModelSelector.IsEnabled = scopes.Count > 0;
        _updatingModelSelector = false;
        _selectedScopeKey = desiredScope?.Key;
        ModelSelector.ToolTip = desiredScope is null ? "No usage" : $"{desiredScope.Label}\n{desiredScope.ModelId}";

        ApplySelectedScope(data, desiredScope);
        ApplyAccountUsage(data.AccountUsage);
        ContextText.Text = data.LastRequestInput.HasValue && data.ContextWindow > 0
            ? $"{TokenFormatter.Compact(data.LastRequestInput.Value)} / {TokenFormatter.Compact(data.ContextWindow.Value)}" : "--";
        ContextBar.Value = data.LastRequestInput.HasValue && data.ContextWindow > 0 ? Math.Clamp(data.LastRequestInput.Value * 100d / data.ContextWindow.Value, 0, 100) : 0;
        ContextBar.Foreground = (Brush)FindResource(ContextBar.Value >= 85 ? "PurpleBrush" : "TealBrush");
        ContextPanel.ToolTip = $"Last request input, not cumulative tokens or a live context meter.\nObserved {data.ContextObservedAt?.ToLocalTime():MM-dd HH:mm:ss}\nCompaction records: {data.CompactionCount}";
        TotalLabelText.Text = data.AgentSessionCount > 0 ? "GRAND TOTAL" : "TOTAL";
        ConversationTotalText.Text = $"{(data.HasUnknownPricing ? "≥ " : "")}{data.GrandTotalCredits:N2} cr · ${data.GrandTotalUsd:N3}";
        ConversationTotalText.ToolTip = data.AgentSessionCount > 0
            ? $"Main {data.TotalCredits:N2} cr · ${data.TotalUsd:N3}\nAgents {data.AgentTotalCredits:N2} cr · ${data.AgentTotalUsd:N3}\nupdated {DateTime.Now:HH:mm}"
            : $"{data.Rows.Count} main turns · updated {DateTime.Now:HH:mm}";
        if (data.HasUnknownPricing) ConversationTotalText.ToolTip += "\nPartial estimate: some model rates are unknown.";
        if (data.MalformedLines > 0) ConversationTotalText.ToolTip += $"\nSkipped malformed records: {data.MalformedLines}";
        _details?.SetData(data);
    }

    private void ApplyAccountUsage(AccountUsageSnapshot? usage)
    {
        _accountSnapshot = usage;
        RefreshQuotaPresentation();
        if (_currentData is not null) { _currentData.AccountUsage = usage; _details?.SetData(_currentData); }
    }

    private void RefreshQuotaPresentation()
    {
        _expiryTimer.Stop();
        var usage = _accountSnapshot;
        var now = DateTimeOffset.UtcNow;
        var remaining = _settings.ShowRemainingQuota;
        var five = QuotaDisplay.Create(usage?.FiveHour, usage?.FiveHourResetsAt, usage?.ObservedAt, remaining, now);
        var week = QuotaDisplay.Create(usage?.Weekly, usage?.WeeklyResetsAt, usage?.ObservedAt, remaining, now);
        FiveHourLabel.Text = remaining ? "5h余" : "5h";
        WeeklyLabel.Text = remaining ? "周余" : "Wk";
        ApplyQuotaWindow(FiveHourText, FiveHourBar, five, "TealBrush");
        ApplyQuotaWindow(WeeklyText, WeeklyBar, week, "PurpleBrush");
        var mode = remaining ? "剩余" : "已用";
        System.Windows.Automation.AutomationProperties.SetName(FiveHourBar, $"账户 5 小时额度{mode}比例");
        System.Windows.Automation.AutomationProperties.SetName(WeeklyBar, $"账户周额度{mode}比例");
        var stamp = usage is null ? "本地日志尚无账户额度快照。" : $"账户级{mode}比例 · 快照 {usage.ObservedAt.ToLocalTime():MM-dd HH:mm:ss}\n~ 表示快照超过 15 分钟；不是实时余额。\n到期后显示 --，等待新的日志快照。";
        FiveHourPanel.ToolTip = BoundedTip(stamp + $"\n已用 {FormatPercent(five.UsedPercent)} · 剩余 {FormatPercent(five.UsedPercent.HasValue ? 100 - five.UsedPercent : null)}\n5h reset: {usage?.FiveHourResetsAt?.ToLocalTime():MM-dd HH:mm}");
        WeeklyPanel.ToolTip = BoundedTip(stamp + $"\n已用 {FormatPercent(week.UsedPercent)} · 剩余 {FormatPercent(week.UsedPercent.HasValue ? 100 - week.UsedPercent : null)}\nWeekly reset: {usage?.WeeklyResetsAt?.ToLocalTime():MM-dd HH:mm}");
        // Only update in-memory UI at expiry/freshness boundaries; no periodic I/O.
        DateTimeOffset? becomesStale = usage is not null && (five.DisplayPercent.HasValue || week.DisplayPercent.HasValue)
            ? new DateTimeOffset(DateTime.SpecifyKind(usage.ObservedAt, DateTimeKind.Utc)).AddMinutes(15).AddMilliseconds(1) : null;
        var nextBoundary = new[] { usage?.FiveHourResetsAt, usage?.WeeklyResetsAt, becomesStale }.Where(t => t > now).Min();
        if (_startMonitoring && !_closed && nextBoundary.HasValue)
        {
            _expiryTimer.Interval = nextBoundary.Value - now + TimeSpan.FromMilliseconds(50);
            _expiryTimer.Start();
        }
        UpdateMonitorStatus();
    }

    private void ApplyQuotaWindow(TextBlock text, System.Windows.Controls.ProgressBar bar, QuotaPresentation value, string normalBrush)
    {
        text.Text = (value.IsStale ? "~" : "") + FormatPercent(value.DisplayPercent);
        text.Foreground = (Brush)FindResource(value.UsedPercent >= 90 ? "CriticalBrush" : value.IsStale ? "WarningBrush" : "TextBrush");
        bar.Value = value.DisplayPercent ?? 0;
        bar.Foreground = (Brush)FindResource(value.UsedPercent >= 90 ? "CriticalBrush" : normalBrush);
        bar.Opacity = value.DisplayPercent is null ? .3 : value.IsStale ? .5 : 1;
    }

    private static ToolTip BoundedTip(string text) => new() { Content = new TextBlock { Text = text, MaxWidth = 210, TextWrapping = TextWrapping.Wrap } };
    private void UpdateMonitorStatus()
    {
        string brush, message;
        if (_lastReadError is not null) { brush = "CriticalBrush"; message = "读取失败，暂时保留上次数据。\n点击 Refresh 或按 F5 重试。\n" + _lastReadError; }
        else if (_currentData is null) { brush = "MutedBrush"; message = "等待可读取的 Codex 任务。\n创建任务后点击 Refresh 或按 F5。"; }
        else if (_startMonitoring && _watcher?.EnableRaisingEvents != true) { brush = "WarningBrush"; message = "实时监听尚未连接。\n点击 Refresh 或按 F5 重新连接。" + (_watcherError is null ? "" : "\n" + _watcherError); }
        else { brush = "TealBrush"; message = "本地日志监听正常（不代表任务正在运行）。" + (_lastSuccessfulRead.HasValue ? $"\n最近成功读取 {_lastSuccessfulRead.Value.ToLocalTime():MM-dd HH:mm:ss}" : ""); }
        MonitorStatusDot.Fill = (Brush)FindResource(brush);
        MonitorStatusDot.ToolTip = BoundedTip(message);
        System.Windows.Automation.AutomationProperties.SetName(MonitorStatusDot, message);
    }

    private static List<UsageScopeChoice> BuildUsageScopes(SessionSummary data)
    {
        var result = data.Rows
            .Select(row => PricingCatalog.CanonicalModel(row.ModelId))
            .Where(IsVisibleModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(model => new UsageScopeChoice { Label = $"Main · {DisplayModelName(model)}", ModelId = model })
            .ToList();

        result.AddRange(data.AgentRows
            .Where(row => IsVisibleModel(row.ModelId))
            .GroupBy(row => PricingCatalog.CanonicalModel(row.ModelId), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var count = group.Select(row => row.SourceSessionId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return new UsageScopeChoice { Label = $"Agents · {DisplayModelName(group.Key)}", ModelId = group.Key, IsAgent = true, AgentCount = count };
            }));
        return result;
    }

    private static bool IsVisibleModel(string model) =>
        !string.IsNullOrWhiteSpace(model);

    private void SelectModelFromDropdown()
    {
        if (_updatingModelSelector || _currentData is null || ModelSelector.SelectedItem is not UsageScopeChoice scope) return;
        _modelSelectionPinned = true;
        _selectedScopeKey = scope.Key;
        ModelSelector.ToolTip = $"{scope.Label}\n{scope.ModelId}";
        ApplySelectedScope(_currentData, scope);
    }

    private void ApplySelectedScope(SessionSummary data, UsageScopeChoice? scope)
    {
        if (scope is null)
        {
            LatestTimeText.Text = "--:--";
            InputText.Text = CachedText.Text = OutputText.Text = CreditsText.Text = UsdText.Text = "--";
            CacheHitText.Text = "--";
            CacheHitBar.Value = 0;
            return;
        }

        var modelRows = RowsForScope(data, scope);
        var modelCredits = modelRows.Sum(row => row.Credits);
        var modelUsd = modelRows.Sum(row => row.Usd);
        var latestModelRow = modelRows.LastOrDefault();
        LatestTimeText.Text = latestModelRow?.Time ?? "--:--";
        InputLabelText.Text = "Input";
        CachedLabelText.Text = "Cached";
        OutputLabelText.Text = "Output";
        InputText.Text = TokenFormatter.Compact(modelRows.Sum(row => row.Uncached));
        CachedText.Text = TokenFormatter.Compact(modelRows.Sum(row => row.Cached));
        OutputText.Text = TokenFormatter.Compact(modelRows.Sum(row => row.Output));
        CreditsLabelText.Text = "Est. credits";
        ApiLabelText.Text = "API eq.";
        var known = modelRows.All(row => row.PricingKnown);
        CreditsText.Text = known ? modelCredits.ToString("N2") : "--";
        UsdText.Text = known ? "$" + modelUsd.ToString("N3") : "--";
        CreditsText.ToolTip = UsdText.ToolTip = string.Join("\n", modelRows.Select(row => row.PricingNote).Distinct());
        InputText.ToolTip = $"Uncached input: {modelRows.Sum(row => row.Uncached):N0}\nIncludes cache writes: {modelRows.Sum(row => row.CacheWrite):N0}";
        CachedText.ToolTip = $"Cached input: {modelRows.Sum(row => row.Cached):N0}";
        OutputText.ToolTip = $"Output: {modelRows.Sum(row => row.Output):N0}\nReasoning included: {modelRows.Sum(row => row.Reasoning):N0}";
        var totalInput = modelRows.Sum(row => row.Input);
        var cacheHit = totalInput > 0 ? modelRows.Sum(row => row.Cached) * 100d / totalInput : 0;
        CacheHitText.Text = FormatPercent(cacheHit);
        CacheHitBar.Value = cacheHit;
        if (scope.IsAgent)
            ModelSelector.ToolTip = $"{scope.Label}\n{modelRows.Count} agent turns";
    }

    private static List<TurnRow> RowsForScope(SessionSummary data, UsageScopeChoice scope) =>
        (scope.IsAgent ? data.AgentRows : data.Rows).Where(row => ModelsMatch(row.ModelId, scope.ModelId)).ToList();

    private static string FormatPercent(double? value) => value.HasValue ? $"{value:0.#}%" : "--";

    private static bool ModelsMatch(string rowModel, string currentModel)
    {
        return SessionParser.SameModel(rowModel, currentModel);
    }

    private static string DisplayModelName(string model)
    {
        if (model.StartsWith("gpt-6-astra", StringComparison.OrdinalIgnoreCase)) return "6 Astra";
        if (model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) model = model[4..];
        if (model.EndsWith("-sol", StringComparison.OrdinalIgnoreCase)) return model[..^4] + " Sol";
        if (model.EndsWith("-terra", StringComparison.OrdinalIgnoreCase)) return model[..^6] + " Terra";
        if (model.EndsWith("-luna", StringComparison.OrdinalIgnoreCase)) return model[..^5] + " Luna";
        return model;
    }

    public async Task<object> RunSmokeTestAsync(string? requestedSessionId = null)
    {
        SelectRequestedSession(requestedSessionId);
        var choices = _parser.GetRecentSessionChoices(RecentConversationLimit).ToList();
        UpdateConversationSelector(choices);
        var path = _selectedSessionPath ?? throw new InvalidOperationException("No user Codex session found.");
        var data = await Task.Run(() => _parser.Parse(path));
        ApplyData(data);

        var selectedScope = ModelSelector.SelectedItem as UsageScopeChoice ?? throw new InvalidOperationException("No usage scope found.");
        var selectedRows = RowsForScope(data, selectedScope);
        var expectedModelCredits = selectedRows.All(r => r.PricingKnown) ? selectedRows.Sum(row => row.Credits).ToString("N2") : "--";
        var expectedModelApi = selectedRows.All(r => r.PricingKnown) ? "$" + selectedRows.Sum(row => row.Usd).ToString("N3") : "--";
        var expectedModelInput = TokenFormatter.Compact(selectedRows.Sum(row => row.Uncached));
        var expectedModelCached = TokenFormatter.Compact(selectedRows.Sum(row => row.Cached));
        var expectedModelOutput = TokenFormatter.Compact(selectedRows.Sum(row => row.Output));
        var selectedTotalInput = selectedRows.Sum(row => row.Input);
        var expectedCacheHit = FormatPercent(selectedTotalInput > 0 ? selectedRows.Sum(row => row.Cached) * 100d / selectedTotalInput : 0);
        var displayedModelInput = InputText.Text;
        var displayedModelCached = CachedText.Text;
        var displayedModelOutput = OutputText.Text;
        var displayedModelCredits = CreditsText.Text;
        var displayedModelApi = UsdText.Text;
        var displayedCacheHit = CacheHitText.Text;

        var selectorScopes = ModelSelector.Items.Cast<UsageScopeChoice>().ToList();
        var alternateScope = selectorScopes.FirstOrDefault(scope => !string.Equals(scope.Key, selectedScope.Key, StringComparison.OrdinalIgnoreCase));
        var alternateSwitchWorked = true;
        if (alternateScope is not null)
        {
            ModelSelector.SelectedItem = alternateScope;
            var alternateRows = RowsForScope(data, alternateScope);
            var alternateInput = alternateRows.Sum(row => row.Input);
            alternateSwitchWorked = InputText.Text == TokenFormatter.Compact(alternateRows.Sum(row => row.Uncached))
                && CachedText.Text == TokenFormatter.Compact(alternateRows.Sum(row => row.Cached))
                && OutputText.Text == TokenFormatter.Compact(alternateRows.Sum(row => row.Output))
                && CacheHitText.Text == FormatPercent(alternateInput > 0 ? alternateRows.Sum(row => row.Cached) * 100d / alternateInput : 0);
            ModelSelector.SelectedItem = selectedScope;
        }

        var breakdown = selectorScopes.Select(scope =>
        {
            var rows = RowsForScope(data, scope);
            return new { Scope = scope.Label, scope.ModelId, scope.IsAgent, scope.AgentCount, Turns = rows.Count, Input = TokenFormatter.Compact(rows.Sum(row => row.Uncached)), Cached = TokenFormatter.Compact(rows.Sum(row => row.Cached)), Output = TokenFormatter.Compact(rows.Sum(row => row.Output)), Credits = rows.Sum(row => row.Credits).ToString("N3"), Api = "$" + rows.Sum(row => row.Usd).ToString("N4") };
        }).ToList();

        var selectedConversation = ConversationSelector.SelectedItem as SessionChoice;
        var originalAccount = data.AccountUsage;
        ApplyAccountUsage(new AccountUsageSnapshot { FiveHour = 99, Weekly = 50, FiveHourResetsAt = DateTimeOffset.UtcNow.AddSeconds(-1), WeeklyResetsAt = DateTimeOffset.UtcNow.AddDays(1), ObservedAt = DateTime.UtcNow });
        var expiredSnapshotHidden = FiveHourText.Text == "--" && FiveHourBar.Value == 0 && WeeklyText.Text == "50%";
        var originalQuotaMode = _settings.ShowRemainingQuota;
        _settings.ShowRemainingQuota = true;
        ApplyAccountUsage(new AccountUsageSnapshot { FiveHour = 30, Weekly = 92, FiveHourResetsAt = DateTimeOffset.UtcNow.AddHours(1), WeeklyResetsAt = DateTimeOffset.UtcNow.AddDays(1), ObservedAt = DateTime.UtcNow });
        var remainingQuotaWorks = FiveHourText.Text == "70%" && FiveHourBar.Value == 70 && WeeklyText.Text == "8%" && WeeklyBar.Value == 8 && WeeklyLabel.Text == "周余";
        var highUsageHighlighted = WeeklyText.Foreground == FindResource("CriticalBrush");
        ApplyAccountUsage(new AccountUsageSnapshot { FiveHour = 30, Weekly = 50, WeeklyResetsAt = DateTimeOffset.UtcNow.AddDays(1), ObservedAt = DateTime.UtcNow.AddMinutes(-16) });
        var oldQuotaMarked = FiveHourText.Text == "~70%" && FiveHourBar.Opacity == .5;
        ApplyAccountUsage(null);
        var unknownQuotaNotFull = FiveHourText.Text == "--" && WeeklyText.Text == "--" && FiveHourBar.Value == 0;
        _settings.ShowRemainingQuota = originalQuotaMode;
        ApplyAccountUsage(originalAccount);
        StartFileWatcher();
        var watcherReady = _watcher?.EnableRaisingEvents == true;
        StopFileWatcher();
        var detailCheckWindow = new DetailWindow();
        detailCheckWindow.SetData(data);
        var allRows = data.Rows.Concat(data.AgentRows).ToList();
        var detailChecks = new
        {
            MatchesMainSize = detailCheckWindow.Width == Width && detailCheckWindow.Height == Height,
            FixedSize = detailCheckWindow.ResizeMode == ResizeMode.NoResize,
            AggregatesAllInput = detailCheckWindow.InputValue.Text == TokenFormatter.Compact(allRows.Sum(r => r.Uncached)),
            AggregatesAllCached = detailCheckWindow.CachedValue.Text == TokenFormatter.Compact(allRows.Sum(r => r.Cached)),
            AggregatesAllOutput = detailCheckWindow.OutputValue.Text == TokenFormatter.Compact(allRows.Sum(r => r.Output)),
            CreditsMatchTotal = detailCheckWindow.CreditsValue.Text == $"{(data.HasUnknownPricing ? "≥ " : "")}{data.GrandTotalCredits:N2}",
            ApiMatchesTotal = detailCheckWindow.ApiValue.Text == $"{(data.HasUnknownPricing ? "≥ " : "")}${data.GrandTotalUsd:N3}"
        };
        detailCheckWindow.Close();
        var pickerChecks = await CheckTaskPickerAsync();
        return new
        {
            CompactDetails = detailChecks,
            RequestedModel = data.RequestedModel,
            ReturnedModel = data.ModelEvidence?.ReturnedModel,
            ModelVerification = data.ModelCheck,
            TaskPicker = pickerChecks,
            data.SessionId,
            data.CurrentModel,
            SelectedScope = selectedScope.Label,
            SelectorScopes = selectorScopes.Select(scope => scope.Label).ToList(),
            AlternateScope = alternateScope?.Label,
            AlternateSwitchWorked = alternateSwitchWorked,
            data.AgentSessionCount,
            AgentTurns = data.AgentRows.Count,
            AgentModels = data.AgentRows.Select(row => row.ModelId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            MainCredits = data.TotalCredits.ToString("N3"),
            AgentCredits = data.AgentTotalCredits.ToString("N3"),
            GrandCredits = data.GrandTotalCredits.ToString("N3"),
            MainApi = "$" + data.TotalUsd.ToString("N4"),
            AgentApi = "$" + data.AgentTotalUsd.ToString("N4"),
            GrandApi = "$" + data.GrandTotalUsd.ToString("N4"),
            ConversationName = selectedConversation?.Label,
            ConversationTitleMatches = selectedConversation?.Label == data.Title.Replace('\r', ' ').Replace('\n', ' '),
            ConversationChoices = ConversationSelector.Items.Count,
            RecentConversationLimit,
            UpdateMode = "FileSystemWatcher",
            UsesIdlePolling = false,
            WatcherReady = watcherReady,
            ExpiredSnapshotHidden = expiredSnapshotHidden,
            RemainingQuotaWorks = remainingQuotaWorks,
            SavedRemainingPreference = originalQuotaMode,
            HighUsageHighlighted = highUsageHighlighted,
            OldQuotaMarked = oldQuotaMarked,
            UnknownQuotaNotFull = unknownQuotaNotFull,
            ConversationPinned = string.Equals(selectedConversation?.Path, _selectedSessionPath, StringComparison.OrdinalIgnoreCase),
            MainTurns = data.Rows.Count,
            SelectedScopeTurns = selectedRows.Count,
            WindowWidth = Width,
            WindowHeight = Height,
            ModelInput = displayedModelInput,
            ModelCached = displayedModelCached,
            ModelOutput = displayedModelOutput,
            ExpectedModelInput = expectedModelInput,
            ExpectedModelCached = expectedModelCached,
            ExpectedModelOutput = expectedModelOutput,
            CacheHit = displayedCacheHit,
            ExpectedCacheHit = expectedCacheHit,
            CacheHitMatches = displayedCacheHit == expectedCacheHit,
            ModelCredits = displayedModelCredits,
            ModelApi = displayedModelApi,
            ExpectedModelCredits = expectedModelCredits,
            ExpectedModelApi = expectedModelApi,
            MetricLabels = new[] { InputLabelText.Text, CachedLabelText.Text, OutputLabelText.Text, CreditsLabelText.Text, ApiLabelText.Text },
            CompactTime = LatestTimeText.Text == "--:--" || LatestTimeText.Text.Count(character => character == ':') == 1,
            SelectedChangeForcesFullRefresh = IsSelectedUsagePath(path),
            UnrelatedChangeForcesFullRefresh = IsSelectedUsagePath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", "rollout-unrelated.jsonl")),
            TotalLabel = TotalLabelText.Text,
            ConversationTotalText = ConversationTotalText.Text,
            ExpectedConversationTotalText = $"{(data.HasUnknownPricing ? "≥ " : "")}{data.GrandTotalCredits:N2} cr · ${data.GrandTotalUsd:N3}",
            Context = ContextText.Text,
            FiveHour = FiveHourText.Text,
            Weekly = WeeklyText.Text,
            ScopeBreakdown = breakdown,
            UiConstructed = data.Rows.Count > 0
        };
    }

    private void SelectRequestedSession(string? requestedSessionId)
    {
        if (string.IsNullOrWhiteSpace(requestedSessionId)) return;
        _selectedSessionPath = _parser.GetRecentSessionChoices(200)
            .FirstOrDefault(choice => string.Equals(choice.SessionId, requestedSessionId, StringComparison.OrdinalIgnoreCase))?.Path;
        if (_selectedSessionPath is null) throw new InvalidOperationException("Requested session was not found in the most recent 200 tasks.");
        _selectedChoice = _parser.GetRecentSessionChoices(200).First(choice => choice.Path == _selectedSessionPath);
    }

    private async Task<object> CheckTaskPickerAsync()
    {
        ConversationSelector.ApplyTemplate();
        var panel = (FrameworkElement)ConversationSelector.Template.FindName("PickerPanel", ConversationSelector);
        // A ComboBox coerces IsDropDownOpen to false before Loaded. Mount an invisible
        // window so popup/keyboard checks exercise the same lifetime as normal use.
        Opacity = 0; ShowInTaskbar = false; ShowActivated = false; panel.Opacity = 0;
        Show();
        while (_refreshing) await Task.Delay(10);
        await OpenTaskSearchAsync();
        var original = _selectedChoice!;
        var recentCount = ConversationSelector.Items.Count;
        var searchableCount = _searchChoices.Count;
        panel.Measure(new Size(210, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 210, panel.DesiredSize.Height));
        panel.UpdateLayout();
        var boundedWidth = Math.Abs(panel.ActualWidth - 210) <= 1 && panel.DesiredSize.Width <= Width;
        var boundedHeight = panel.ActualHeight <= 220;
        _taskSearchBox!.Text = "__no_matching_task_94723__";
        var emptyResults = ConversationSelector.Items.Count == 0 && _selectedSessionPath == original.Path;
        _taskSearchBox.Text = original.SessionId.ToUpperInvariant();
        var idSearch = ConversationSelector.Items.Count == 1;
        _taskSearchBox.Text = original.Label[..Math.Min(original.Label.Length, 4)];
        var titleSearch = ConversationSelector.Items.Cast<SessionChoice>().Any(c => c.Path == original.Path);
        _taskSearchBox.Clear();
        var clearRestores = ConversationSelector.Items.Count == recentCount && (ConversationSelector.SelectedItem as SessionChoice)?.Path == original.Path;

        var older = _searchChoices.FirstOrDefault(c => !_recentChoices.Any(r => r.Path == c.Path));
        bool? olderSwitch = null;
        if (older is not null)
        {
            _taskSearchBox.Text = older.SessionId;
            // Exercise the same selection handler as the UI, while awaiting completion.
            _updatingConversationSelector = true;
            ConversationSelector.SelectedItem = ConversationSelector.Items[0];
            _updatingConversationSelector = false;
            await SelectConversationFromDropdownAsync();
            olderSwitch = _selectedSessionPath == older.Path && _currentData?.SessionId == older.SessionId
                && (ConversationSelector.SelectedItem as SessionChoice)?.SessionId == older.SessionId;
            _selectedChoice = original;
            _selectedSessionPath = original.Path;
            _selectionVersion++;
            await RefreshAsync(true);
        }
        _taskSearchBox.Clear();
        UpdateConversationSelector(_recentChoices.ToList());
        // Route actual WPF key events through the search box without showing a test window.
        using var keySource = new HwndSource(new HwndSourceParameters("Task picker diagnostics") { Width = 1, Height = 1, WindowStyle = 0 });
        ConversationSelector.IsDropDownOpen = true;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_searchLoading && DateTime.UtcNow < deadline) await Task.Delay(10);
        _taskSearchBox.Text = "__no_matching_task_94723__";
        var emptySearchStaysOpen = ConversationSelector.IsDropDownOpen && ConversationSelector.Items.Count == 0;
        _taskSearchBox.Text = original.SessionId;
        var filteredItems = ConversationSelector.ItemsSource;
        UpdateConversationSelector(_recentChoices.ToList());
        var refreshKeepsQuery = _taskSearchBox.Text == original.SessionId && ReferenceEquals(filteredItems, ConversationSelector.ItemsSource);
        var keyboardTarget = older ?? original;
        _taskSearchBox.Text = keyboardTarget.SessionId;
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        _taskSearchBox.RaiseEvent(enter);
        while (_refreshing) await Task.Delay(10);
        var enterCloses = enter.Handled && !ConversationSelector.IsDropDownOpen && _selectedSessionPath == keyboardTarget.Path
            && _currentData?.SessionId == keyboardTarget.SessionId;
        _selectedChoice = original;
        _selectedSessionPath = original.Path;
        _selectionVersion++;
        await RefreshAsync(true);
        ConversationSelector.IsDropDownOpen = true;
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        _taskSearchBox.RaiseEvent(escape);
        var escapeCancels = escape.Handled && !ConversationSelector.IsDropDownOpen && _selectedSessionPath == original.Path;
        _taskSearchBox.Clear();
        UpdateConversationSelector(_recentChoices.ToList());
        return new { BoundedWidth = boundedWidth, BoundedHeight = boundedHeight, EmptyResultsPreserveTask = emptyResults,
            IdSearch = idSearch, TitleSearch = titleSearch, ClearRestoresSelection = clearRestores,
            OlderTaskSwitchWorked = olderSwitch, RefreshKeepsQuery = refreshKeepsQuery, EmptySearchStaysOpen = emptySearchStaysOpen, EnterCloses = enterCloses,
            EscapeCancels = escapeCancels, RecentCount = recentCount, SearchableCount = searchableCount };
    }

    public async Task RenderPreviewsAsync(string outputDirectory, string? requestedSessionId = null, string? requestedScopeKey = null)
    {
        Directory.CreateDirectory(outputDirectory);
        SelectRequestedSession(requestedSessionId);
        var choices = _parser.GetRecentSessionChoices(RecentConversationLimit).ToList();
        UpdateConversationSelector(choices);
        var path = _selectedSessionPath ?? throw new InvalidOperationException("No user Codex session found.");
        var data = await Task.Run(() => _parser.Parse(path));
        ApplyData(data);
        if (!string.IsNullOrWhiteSpace(requestedScopeKey))
        {
            var requestedScope = ModelSelector.Items.Cast<UsageScopeChoice>()
                .FirstOrDefault(scope => string.Equals(scope.Key, requestedScopeKey, StringComparison.OrdinalIgnoreCase));
            if (requestedScope is not null) ModelSelector.SelectedItem = requestedScope;
        }
        RenderElement((FrameworkElement)Content, Width, Height, Path.Combine(outputDirectory, "main.png"));
        var savedQuotaMode = _settings.ShowRemainingQuota;
        _settings.ShowRemainingQuota = true;
        RefreshQuotaPresentation();
        RenderElement((FrameworkElement)Content, Width, Height, Path.Combine(outputDirectory, "main-remaining.png"));
        _settings.ShowRemainingQuota = savedQuotaMode;
        RefreshQuotaPresentation();
        ConversationSelector.ApplyTemplate();
        await OpenTaskSearchAsync();
        var picker = (FrameworkElement)ConversationSelector.Template.FindName("PickerPanel", ConversationSelector);
        picker.Measure(new Size(210, double.PositiveInfinity));
        RenderElement(picker, 210, Math.Ceiling(picker.DesiredSize.Height), Path.Combine(outputDirectory, "task-picker.png"));
        _taskSearchBox!.Text = "__no_matching_task_94723__";
        picker.Measure(new Size(210, double.PositiveInfinity));
        RenderElement(picker, 210, Math.Ceiling(picker.DesiredSize.Height), Path.Combine(outputDirectory, "task-picker-empty.png"));
        _taskSearchBox.Clear();
        UpdateConversationSelector(_recentChoices.ToList());
        var details = new DetailWindow();
        details.SetData(data);
        RenderElement((FrameworkElement)details.Content, details.Width, details.Height, Path.Combine(outputDirectory, "details.png"));
        details.Close();
    }

    private static void RenderElement(FrameworkElement element, double width, double height, string path)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
