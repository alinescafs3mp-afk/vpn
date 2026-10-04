using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Fetch;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Probe;
using AutoVpn.Infrastructure.Refresh;

namespace AutoVpn.Desktop;

public partial class MainWindow : Window
{
    private readonly NotifyIcon _tray;
    private readonly ICatalogue _catalogue;
    private readonly SourceLedger _ledger;
    private readonly string _root;
    private readonly SessionMailbox _mailbox = new();
    private readonly ProbeByteBudget _probeBudget;
    private readonly RefreshFence _fence = new();
    private readonly RefreshScheduler _scheduler;
    private readonly DispatcherTimer _scheduleTimer;
    private readonly DispatcherTimer _maintenanceTimer;
    private readonly DispatcherTimer _statusTimer;
    private CancellationTokenSource _statusCts = new();
    private Task? _statusTask;
    private int _statusBusy;
    private readonly CatalogueMaintenance? _maintenance;
    private bool _checksPaused;
    private string _serverView = "working";
    private readonly UiOperationLease _connectLease = new();
    private CancellationTokenSource? _connectCts;
    private Task? _connectTask;
    private CancellationTokenSource? _refresh;
    private Task? _refreshTask;
    private int _refreshRun;
    private int _shuttingDown;
    private bool _refreshActive;
    private bool _exit;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVPN");
        Directory.CreateDirectory(_root);
        _catalogue = OpenCatalogue(_root);
        _ledger = SourceLedger.Load(Path.Combine(_root, "sources.json"));
        _probeBudget = ProbeByteBudget.Load(Path.Combine(_root, "probe-budget.txt"), ProductLimits.DailyHealthBudgetBytes, DateOnly.FromDateTime(DateTime.UtcNow));
        try
        {
            var registry = ReviewedRegistryLoader.Load(ConfigDirectory());
            _maintenance = new CatalogueMaintenance(_catalogue,
                new TwoTargetProbeTransport(_catalogue, new NonTunCoreProbeTransport(CorePath(), ExpectedCoreHash()), registry.ProbeTargets),
                _probeBudget, budgetPath: Path.Combine(_root, "probe-budget.txt"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            MaintenanceStatus.Text = "Проверки недоступны: требуется корректный набор двух HTTPS-адресов.";
        }
        _maintenanceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _maintenanceTimer.Tick += (_, _) => ShowMaintenance();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) =>
        {
            if (Volatile.Read(ref _shuttingDown) == 0 && !_mailbox.OperationPending && Volatile.Read(ref _statusBusy) == 0)
                _statusTask = PollStatusAsync();
        };
        _scheduler = new RefreshScheduler(
            () => _catalogue.Settings,
            () => _ledger.LiveSuccessStamps(DateTimeOffset.UtcNow),
            (_, token) =>
            {
                if (Volatile.Read(ref _shuttingDown) == 1 || token.IsCancellationRequested) return Task.CompletedTask;
                _refreshTask = RunRefreshAsync();
                return _refreshTask;
            }, Environment.ProcessId);
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _scheduleTimer.Tick += async (_, _) =>
        {
            if (_refreshActive || Volatile.Read(ref _shuttingDown) == 1) return;
            await _scheduler.PulseAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(true);
        };
        _tray = new NotifyIcon { Text = "AutoVPN", Visible = true, Icon = System.Drawing.SystemIcons.Shield };
        var menu = new ContextMenuStrip();
        menu.Items.Add(Ru.ShowWindow, null, (_, _) => RestoreWindow());
        menu.Items.Add(Ru.Exit, null, (_, _) => ExitApplication());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow();
        Closing += OnClosing;
        Loaded += OnLoaded;
        DisclosureBox.IsChecked = _catalogue.Settings.DisclosureAccepted;
        ProtectionBox.IsChecked = _catalogue.Settings.ProtectionOnConnect;
        LanBox.IsChecked = _catalogue.Settings.LanAccess;
        InsecureBox.IsChecked = _catalogue.Settings.AllowInsecureCertificates;
        _mailbox.NoteDisclosure(_catalogue.Settings.DisclosureAccepted);
        ShowSession();
        _ready = true;
    }

    private static ICatalogue OpenCatalogue(string root)
    {
        if (!OperatingSystem.IsWindows()) return new MemoryCatalogue();
        return SqliteCatalogue.Open(Path.Combine(root, "catalogue.sqlite"), SecretProtectors.ForProductionHost());
    }

    private void ShowConnection(object sender, RoutedEventArgs e) => ShowPage(ConnectionPage);
    private void ShowServers(object sender, RoutedEventArgs e) { ShowPage(ServersPage); ShowWorking(sender, e); }
    private void ShowSubscriptions(object sender, RoutedEventArgs e) => ShowPage(SubscriptionsPage);
    private void ShowSettings(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);
    private void ShowWorking(object sender, RoutedEventArgs e) { _serverView = "working"; RefreshServerView(); }
    private void ShowFavorites(object sender, RoutedEventArgs e) { _serverView = "favorites"; RefreshServerView(); }
    private void ShowAll(object sender, RoutedEventArgs e) { _serverView = "all"; RefreshServerView(); }

    private void DisclosureChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || DisclosureBox.IsChecked != true) return;
        try
        {
            Consent.AcceptDisclosure(_catalogue);
            _mailbox.NoteDisclosure(true);
            if (Volatile.Read(ref _shuttingDown) == 0) _ = _scheduler.PulseAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or CatalogueStoreException) { DetailText.Text = ex.Message; }
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        try
        {
            _catalogue.Settings = _catalogue.Settings with
            {
                ProtectionOnConnect = ProtectionBox.IsChecked == true, LanAccess = LanBox.IsChecked == true,
                AllowInsecureCertificates = InsecureBox.IsChecked == true, Revision = _catalogue.Settings.Revision + 1,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or CatalogueStoreException) { DetailText.Text = ex.Message; }
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        try { var task = ConnectAsync(); _connectTask = task; await task.ConfigureAwait(true); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or CatalogueStoreException) { DetailText.Text = ex.Message; }
    }

    private async Task ConnectAsync()
    {
        if (_mailbox.OperationPending || _mailbox.Session.SafetyDisconnectAvailable)
        { await DisconnectLocalAsync().ConfigureAwait(true); return; }
        if (_mailbox.Session.PhaseCode == "Unknown" || !_mailbox.Session.BrokerReachable)
        {
            await ResyncAsync().ConfigureAwait(true);
            if (_mailbox.Session.SafetyDisconnectAvailable) { await DisconnectLocalAsync().ConfigureAwait(true); return; }
        }
        if (!_catalogue.Settings.DisclosureAccepted)
        { DetailText.Text = "Сначала подтвердите предупреждение о публичных серверах."; return; }
        _connectCts?.Cancel(); _connectCts?.Dispose(); _connectCts = new CancellationTokenSource();
        var token = _connectCts.Token;
        var generation = _connectLease.Start();
        _mailbox.OperationPending = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var eligible = _catalogue.Eligible(new EligibilityContext
            {
                NowUtc = now, NetworkEpoch = _catalogue.NetworkEpoch,
                AllowedAge = TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes),
                Purpose = SelectionPurpose.PreConnect, AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
            });
            if (!UiSessionReducer.ConnectAllowed(_catalogue.Settings.DisclosureAccepted, eligible.Count > 0))
            { DetailText.Text = Ru.NoServer; return; }
            var selected = eligible[0];
            if (_maintenance is null || !await _maintenance.CheckNowAsync(selected.NodeId, token).ConfigureAwait(true))
            {
                DetailText.Text = "Не удалось подтвердить сервер по двум HTTPS-адресам. Подключение не начато.";
                return;
            }
            if (!_connectLease.Owns(generation) || token.IsCancellationRequested) return;
            var fresh = _catalogue.Eligible(new EligibilityContext
            {
                NowUtc = DateTimeOffset.UtcNow, NetworkEpoch = _catalogue.NetworkEpoch,
                AllowedAge = TimeSpan.FromSeconds(ProductLimits.PreConnectFreshnessSeconds),
                Purpose = SelectionPurpose.PreConnect, AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
                RequiredTargetSetId = _maintenance.TargetSetId, MaxAcceptableLatencyMs = _catalogue.Settings.MaxAcceptableLatencyMs,
            });
            var current = fresh.FirstOrDefault(n => n.NodeId == selected.NodeId);
            if (current is null) { DetailText.Text = Ru.NoServer; return; }
            selected = current;
            await SendAsync(IpcOperations.Connect, new ConnectPayload
            {
                NodeId = selected.NodeId, Digest = selected.Digest, NetworkEpoch = _catalogue.NetworkEpoch,
                ProtectionRequired = _catalogue.Settings.ProtectionOnConnect, LanAccess = _catalogue.Settings.LanAccess,
                Node = NodeWireFactory.FromCatalogue(selected),
            }, token, generation).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
        finally { if (_connectLease.FinishIfCurrent(generation)) _mailbox.OperationPending = false; }
    }

    private async Task DisconnectLocalAsync()
    {
        _connectCts?.Cancel(); var generation = _connectLease.Supersede();
        try { await SendAsync(IpcOperations.Disconnect, new DisconnectPayload()).ConfigureAwait(true); }
        finally { if (_connectLease.FinishIfCurrent(generation)) _mailbox.OperationPending = false; }
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    { _refreshTask = RunRefreshAsync(); await _refreshTask.ConfigureAwait(true); }

    private async Task RunRefreshAsync()
    {
        if (!_catalogue.Settings.DisclosureAccepted)
        { SubscriptionStatus.Text = "Сначала подтвердите предупреждение о подписках. Загрузка источников не начата."; return; }
        if (Volatile.Read(ref _shuttingDown) == 1) return;
        var cycle = _fence.Begin(); var run = Interlocked.Increment(ref _refreshRun);
        _refresh?.Cancel(); _refresh?.Dispose(); _refresh = new CancellationTokenSource();
        var token = _refresh.Token; _refreshActive = true;
        try
        {
            SubscriptionStatus.Text = "Обновление подписок…";
            var registry = ReviewedRegistryLoader.Load(ConfigDirectory());
            using var fetcher = PolicyHttpFetcher.Create(registry.FetchOrigins);
            var coordinator = new CatalogueCoordinator(_catalogue, fetcher,
                new NonTunCoreProbeTransport(CorePath(), ExpectedCoreHash()), _ledger, registry.ProbeTargets, _fence, _probeBudget);
            var discovery = await coordinator.DiscoverAsync(registry, token).ConfigureAwait(true);
            if (!_fence.IsCurrent(cycle)) return;
            if (!discovery.Complete)
            {
                SubscriptionStatus.Text = "Список источников неполный: " + (discovery.ReasonCode ?? "DISCOVERY_INCOMPLETE") + ". Сохранённый каталог не удалён.";
                return;
            }
            var outcome = await coordinator.RefreshAsync(discovery.Items, DateTimeOffset.UtcNow, token).ConfigureAwait(true);
            if (!_fence.IsCurrent(cycle) || token.IsCancellationRequested) return;
            _ledger.Save(Path.Combine(_root, "sources.json"));
            SubscriptionStatus.Text = "Загрузка завершена. Записей: " + _catalogue.Nodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". Проверка серверов продолжается отдельно во вкладке «Серверы»."
                + (outcome.AnyFetchFailed ? " Часть источников недоступна, прежний состав сохранён." : "");
        }
        catch (OperationCanceledException)
        { if (_fence.IsCurrent(cycle)) SubscriptionStatus.Text = "Обновление отменено. Уже сохранённые записи не удалены."; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or CatalogueStoreException)
        { if (_fence.IsCurrent(cycle)) SubscriptionStatus.Text = "Обновление не выполнено: " + ex.Message; }
        finally { if (Volatile.Read(ref _refreshRun) == run) _refreshActive = false; }
    }

    private void CancelRefreshClick(object sender, RoutedEventArgs e) { _refresh?.Cancel(); _fence.Begin(); }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _maintenance?.Start(); _maintenanceTimer.Start(); _statusTimer.Start(); ShowMaintenance();
        await ResyncAsync().ConfigureAwait(true);
        _scheduleTimer.Start();
        if (_refreshActive) return;
        _refreshTask = _scheduler.PulseAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await _refreshTask.ConfigureAwait(true);
    }

    private async Task SendAsync(string operation, object payload, CancellationToken cancellationToken = default, int? ownerGeneration = null)
    {
        try
        {
            for (var attempt = 1; attempt <= DisconnectRetry.MaximumAttempts; attempt++)
            {
                var request = new IpcRequest
                {
                    ProtocolVersion = ProductLimits.IpcProtocolVersion, RequestId = Guid.NewGuid().ToString("N"),
                    ExpectedStateRevision = _mailbox.Session.StateRevision, Operation = operation,
                    Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options),
                };
                var response = await LocalIpcServer.RoundTripAsync("autovpn-broker", request, cancellationToken).ConfigureAwait(true);
                if (ownerGeneration is int generation && !_connectLease.Owns(generation)) return;
                if (response is null) { _mailbox.ApplyTransportLoss(); ShowSession(); return; }
                var accepted = ApplyBrokerResponse(response); ShowSession();
                // Never retry an ambiguous timeout, a started Connect or an uncertain
                // cleanup. A StaleRevision rejection explicitly performed no effect.
                if (!DisconnectRetry.ShouldRetry(request, response, accepted, attempt)) return;
            }
        }
        catch (OperationCanceledException) when (ownerGeneration is int generation && !_connectLease.Owns(generation)) { }
        catch (TimeoutException) { _mailbox.ApplyTransportLoss(); ShowSession(); }
        catch (IOException) { _mailbox.ApplyTransportLoss(); ShowSession(); }
    }

    private async Task PollStatusAsync()
    {
        if (Interlocked.Exchange(ref _statusBusy, 1) != 0) return;
        try { await ResyncAsync(_statusCts.Token).ConfigureAwait(true); }
        finally { Volatile.Write(ref _statusBusy, 0); }
    }

    private async Task ResyncAsync(CancellationToken token = default)
    {
        try
        {
            var response = await LocalIpcServer.RoundTripAsync("autovpn-broker", new IpcRequest
            {
                ProtocolVersion = ProductLimits.IpcProtocolVersion, RequestId = Guid.NewGuid().ToString("N"),
                ExpectedStateRevision = _mailbox.Session.StateRevision, Operation = IpcOperations.GetSnapshot,
                Payload = JsonSerializer.SerializeToElement(new Dictionary<string, string>(), IpcJson.Options),
            }, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;
            if (response is null) _mailbox.ApplyTransportLoss(); else ApplyBrokerResponse(response);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { _mailbox.ApplyTransportLoss(); }
        ShowSession();
    }

    private bool ApplyBrokerResponse(IpcResponse response)
    {
        if (!_mailbox.Apply(response, _catalogue.Settings.DisclosureAccepted) || response.Snapshot is not { } snapshot) return false;
        // Only a reachable accepted snapshot can clear an active retention mark.
        // No write on an unchanged poll; this avoids needless catalogue revisions.
        if (snapshot.CoreRunning || snapshot.OwnedResourceCount > 0 || _mailbox.Session.ClaimsVerifiedDisconnect)
        {
            var active = snapshot.CoreRunning || snapshot.OwnedResourceCount > 0 ? snapshot.ActiveNodeId : null;
            lock (_catalogue.SyncRoot)
            {
                if (_catalogue.Nodes.Any(node => node.ActiveSession != (node.NodeId == active)))
                {
                    try { _catalogue.SetActiveNode(active); }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException)
                    { DetailText.Text = "Не удалось сохранить отметку активного сервера: " + ex.GetType().Name; }
                }
            }
        }
        return true;
    }

    private void ShowSession()
    {
        PhaseText.Tag = _mailbox.Session.PhaseCode; PhaseText.Text = _mailbox.Session.PhaseLabel;
        DetailText.Text = _mailbox.Session.Detail; ConnectButton.Content = _mailbox.Session.PrimaryAction;
    }
    private void ShowPage(UIElement page)
    {
        ConnectionPage.Visibility = Visibility.Collapsed; ServersPage.Visibility = Visibility.Collapsed;
        SubscriptionsPage.Visibility = Visibility.Collapsed; SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
    }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exit)
        {
            _maintenanceTimer.Stop(); _statusTimer.Stop(); _statusCts.Cancel(); _maintenance?.RequestStop();
            _tray.Visible = false; _tray.Dispose(); (_catalogue as IDisposable)?.Dispose(); return;
        }
        e.Cancel = true; Hide();
    }
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; Activate(); }

    private async void ExitApplication()
    {
        if (Interlocked.Exchange(ref _shuttingDown, 1) == 1) return;
        _scheduleTimer.Stop(); _maintenanceTimer.Stop(); _statusTimer.Stop(); _statusCts.Cancel(); _maintenance?.Pause();
        _connectCts?.Cancel(); _refresh?.Cancel(); _fence.Begin();
        var generation = _connectLease.Supersede();
        if (_mailbox.OperationPending || _mailbox.Session.SafetyDisconnectAvailable || _mailbox.Session.LastKnownProtectionArmed)
            await SendAsync(IpcOperations.Disconnect, new DisconnectPayload()).ConfigureAwait(true);
        var maintenanceJoined = _maintenance is null || await _maintenance.WaitForIdleAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        var connectJoined = await JoinOwnedAsync(_connectTask).ConfigureAwait(true);
        var refreshJoined = await JoinOwnedAsync(_refreshTask).ConfigureAwait(true);
        var statusJoined = await JoinOwnedAsync(_statusTask).ConfigureAwait(true);
        if (connectJoined && _connectLease.FinishIfCurrent(generation)) _mailbox.OperationPending = false;
        var decision = UiSessionReducer.PlanExit(_mailbox.Session, _mailbox.OperationPending);
        if (!connectJoined || !refreshJoined || !maintenanceJoined || !statusJoined || !decision.CanClose)
        {
            if (statusJoined) { _statusCts.Dispose(); _statusCts = new CancellationTokenSource(); }
            Volatile.Write(ref _shuttingDown, 0); _scheduleTimer.Start(); _maintenanceTimer.Start(); _statusTimer.Start();
            if (!_checksPaused) _maintenance?.Resume();
            DetailText.Text = decision.CanClose ? "Выход остановлен: локальная операция не завершилась." : decision.Reason ?? "Выход не подтверждён.";
            Show(); return;
        }
        _maintenance?.RequestStop(); _exit = true; System.Windows.Application.Current.Shutdown();
    }

    private static async Task<bool> JoinOwnedAsync(Task? task)
    {
        if (task is null || task.IsCompleted) return true;
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true); return true; }
        catch (TimeoutException) { return false; }
        catch (Exception) { return task.IsCompleted; }
    }

    private void PauseChecksClick(object sender, RoutedEventArgs e)
    {
        _checksPaused = !_checksPaused;
        if (_checksPaused) _maintenance?.Pause(); else _maintenance?.Resume();
        PauseChecksButton.Content = _checksPaused ? "Продолжить проверки" : "Приостановить проверки";
        ShowMaintenance();
    }
    private void ShowMaintenance()
    {
        if (_maintenance is null) return;
        var state = _maintenance.Snapshot;
        var caption = state.Phase switch
        {
            "CONSENT_REQUIRED" => "Ожидается согласие на проверки публичных серверов",
            "PAUSED" => "Проверки приостановлены",
            "CHECKING" => "Проверка по двум HTTPS-адресам",
            "BACKOFF" => "Пауза после ошибки ядра или неопределённого результата сети",
            "ERROR" => "Ошибка обслуживания каталога",
            "BUDGET_EXHAUSTED" => "Суточный бюджет проверок исчерпан",
            "STOPPED" => "Проверки остановлены",
            _ => "Каталог поддерживается автоматически",
        };
        MaintenanceStatus.Text = caption + ". За последний цикл: проверено " + state.Attempted +
            ", подтверждено " + state.Succeeded + ", неуспешно " + state.Failed + ".";
        RefreshServerView();
    }
    private void RefreshServerView()
    {
        lock (_catalogue.SyncRoot)
        {
            if (_serverView != "working") { ServerList.Text = CataloguePresentation.Servers(_catalogue, _serverView, DateTimeOffset.UtcNow); return; }
            if (_maintenance is null) { ServerList.Text = "Двухэтапная проверка недоступна."; return; }
            var nodes = _catalogue.Eligible(new EligibilityContext
            {
                NowUtc = DateTimeOffset.UtcNow, NetworkEpoch = _catalogue.NetworkEpoch, RequiredTargetSetId = _maintenance.TargetSetId,
                MaxAcceptableLatencyMs = _catalogue.Settings.MaxAcceptableLatencyMs, AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
            });
            ServerList.Text = nodes.Count == 0 ? "Пока нет серверов, подтверждённых по двум HTTPS-адресам." :
                string.Join("\n", nodes.Take(100).Select(n => n.Label + " | " + (n.AdvertisedCountry ?? "страна не указана") +
                    " | HTTPS: " + n.Assessment?.MedianLatencyMs + " мс")) +
                (nodes.Count > 100 ? "\nПоказаны первые 100 из " + nodes.Count + "." : "");
        }
    }
    private static string? CorePath()
    {
        var explicitPath = Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        var packaged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "core", "mihomo.exe"));
        return File.Exists(packaged) ? packaged : null;
    }
    private static string ConfigDirectory() => Path.Combine(AppContext.BaseDirectory, "config");
    private static string? ExpectedCoreHash()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "core-manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var name = OperatingSystem.IsWindows() ? "mihomo-windows-amd64.exe" : "mihomo-linux-amd64";
            if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var assetName) && assetName.ValueKind == JsonValueKind.String &&
                    string.Equals(assetName.GetString(), name, StringComparison.Ordinal) &&
                    asset.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String) return sha.GetString();
            }
        }
        catch (JsonException) { return null; }
        return null;
    }
}
