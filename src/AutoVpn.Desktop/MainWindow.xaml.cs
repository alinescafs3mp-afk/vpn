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
    private UiSession _session = UiSessionReducer.Initial();
    private readonly RefreshFence _fence = new();
    private readonly RefreshScheduler _scheduler;
    private readonly DispatcherTimer _scheduleTimer;
    private CancellationTokenSource? _refresh;
    private Task? _refreshTask;
    private int _refreshRun;
    private bool _refreshActive;
    private bool _exit;
    private bool _ready;
    private bool _connectPending;

    public MainWindow()
    {
        InitializeComponent();
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoVPN");
        Directory.CreateDirectory(_root);
        _catalogue = OpenCatalogue(_root);
        _ledger = SourceLedger.Load(Path.Combine(_root, "sources.json"));
        _scheduler = new RefreshScheduler(
            () => _catalogue.Settings,
            () => _ledger.Entries.Select(entry => entry.LastSuccessUtc).ToArray(),
            (_, _) =>
            {
                _refreshTask = RunRefreshAsync();
                return _refreshTask;
            },
            Environment.ProcessId);
        _scheduleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _scheduleTimer.Tick += async (_, _) =>
        {
            if (_refreshActive)
            {
                return;
            }

            await _scheduler.PulseAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(true);
        };
        _tray = new NotifyIcon
        {
            Text = "AutoVPN",
            Visible = true,
            Icon = System.Drawing.SystemIcons.Shield,
        };
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
        _session = _session with { DisclosureAccepted = _catalogue.Settings.DisclosureAccepted };
        ShowSession();
        _ready = true;
    }

    private static ICatalogue OpenCatalogue(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new MemoryCatalogue();
        }

        return SqliteCatalogue.Open(Path.Combine(root, "catalogue.sqlite"), SecretProtectors.ForProductionHost());
    }

    private void ShowConnection(object sender, RoutedEventArgs e) => ShowPage(ConnectionPage);

    private void ShowServers(object sender, RoutedEventArgs e)
    {
        ShowPage(ServersPage);
        ShowWorking(sender, e);
    }

    private void ShowSubscriptions(object sender, RoutedEventArgs e) => ShowPage(SubscriptionsPage);

    private void ShowSettings(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);

    private void ShowWorking(object sender, RoutedEventArgs e)
    {
        ServerList.Text = CataloguePresentation.Servers(_catalogue, "working", DateTimeOffset.UtcNow);
    }

    private void ShowFavorites(object sender, RoutedEventArgs e)
    {
        ServerList.Text = CataloguePresentation.Servers(_catalogue, "favorites", DateTimeOffset.UtcNow);
    }

    private void ShowAll(object sender, RoutedEventArgs e)
    {
        ServerList.Text = CataloguePresentation.Servers(_catalogue, "all", DateTimeOffset.UtcNow);
    }

    private void DisclosureChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || DisclosureBox.IsChecked != true)
        {
            return;
        }

        try
        {
            Consent.AcceptDisclosure(_catalogue);
            _session = _session with { DisclosureAccepted = true };
            _ = _scheduler.PulseAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or CatalogueStoreException)
        {
            DetailText.Text = ex.Message;
        }
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        try
        {
            _catalogue.Settings = _catalogue.Settings with
            {
                ProtectionOnConnect = ProtectionBox.IsChecked == true,
                LanAccess = LanBox.IsChecked == true,
                AllowInsecureCertificates = InsecureBox.IsChecked == true,
                Revision = _catalogue.Settings.Revision + 1,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or CatalogueStoreException)
        {
            DetailText.Text = ex.Message;
        }
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_connectPending || _session.SafetyDisconnectAvailable)
            {
                await SendAsync(IpcOperations.Disconnect, new DisconnectPayload()).ConfigureAwait(true);
                return;
            }

            if (!_catalogue.Settings.DisclosureAccepted)
            {
                DetailText.Text = "Сначала подтвердите предупреждение о публичных серверах.";
                return;
            }

            _connectPending = true;

            var now = DateTimeOffset.UtcNow;
            var eligible = _catalogue.Eligible(new EligibilityContext
            {
                NowUtc = now,
                NetworkEpoch = _catalogue.NetworkEpoch,
                AllowedAge = TimeSpan.FromMinutes(ProductLimits.CatalogueFreshnessMinutes),
                Purpose = SelectionPurpose.PreConnect,
                AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
            });
            if (!UiSessionReducer.ConnectAllowed(_catalogue.Settings.DisclosureAccepted, eligible.Count > 0))
            {
                DetailText.Text = Ru.NoServer;
                return;
            }

            var selected = eligible[0];
            if (ProbeCoordinator.NeedsOnDemandAdmission(selected, now, _catalogue.NetworkEpoch))
            {
                Uri? target = null;
                try
                {
                    var registry = ReviewedRegistryLoader.Load(ConfigDirectory());
                    target = registry.ProbeTargets.FirstOrDefault();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    target = null;
                }

                if (target is not null)
                {
                    var transport = new NonTunCoreProbeTransport(Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH"), ExpectedCoreHash());
                    await ProbeCoordinator.AdmitIfStaleAsync(_catalogue, transport, target, selected.NodeId, now, CancellationToken.None).ConfigureAwait(true);
                }

                var fresh = _catalogue.Eligible(new EligibilityContext
                {
                    NowUtc = DateTimeOffset.UtcNow,
                    NetworkEpoch = _catalogue.NetworkEpoch,
                    AllowedAge = TimeSpan.FromSeconds(ProductLimits.PreConnectFreshnessSeconds),
                    Purpose = SelectionPurpose.PreConnect,
                    AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
                });
                var admitted = fresh.FirstOrDefault(node => node.NodeId == selected.NodeId);
                if (admitted is null)
                {
                    DetailText.Text = Ru.NoServer;
                    return;
                }

                selected = admitted;
            }

            await SendAsync(IpcOperations.Connect, new ConnectPayload
            {
                NodeId = selected.NodeId,
                Digest = selected.Digest,
                NetworkEpoch = _catalogue.NetworkEpoch,
                ProtectionRequired = _catalogue.Settings.ProtectionOnConnect,
                LanAccess = _catalogue.Settings.LanAccess,
                Node = NodeWireFactory.FromCatalogue(selected),
            }).ConfigureAwait(true);
        }
        finally
        {
            _connectPending = false;
        }
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        _refreshTask = RunRefreshAsync();
        await _refreshTask.ConfigureAwait(true);
    }

    private async Task RunRefreshAsync()
    {
        if (!_catalogue.Settings.DisclosureAccepted)
        {
            SubscriptionStatus.Text = "Сначала подтвердите предупреждение о подписках. Загрузка источников не начата.";
            return;
        }

        var cycle = _fence.Begin();
        var run = Interlocked.Increment(ref _refreshRun);
        _refresh?.Cancel();
        _refresh?.Dispose();
        _refresh = new CancellationTokenSource();
        var token = _refresh.Token;
        _refreshActive = true;
        try
        {
            SubscriptionStatus.Text = "Обновление подписок…";
            var registry = ReviewedRegistryLoader.Load(ConfigDirectory());
            using var fetcher = PolicyHttpFetcher.Create(registry.FetchOrigins);
            var coordinator = new CatalogueCoordinator(
                _catalogue,
                fetcher,
                new NonTunCoreProbeTransport(Environment.GetEnvironmentVariable("AUTOVPN_MIHOMO_PATH"), ExpectedCoreHash()),
                _ledger,
                registry.ProbeTargets,
                _fence);
            var discovery = await coordinator.DiscoverAsync(registry, token).ConfigureAwait(true);
            if (!_fence.IsCurrent(cycle))
            {
                return;
            }

            if (!discovery.Complete)
            {
                SubscriptionStatus.Text = "Список источников неполный: " + (discovery.ReasonCode ?? "DISCOVERY_INCOMPLETE") + ". Сохранённый каталог не удалён.";
                return;
            }

            var outcome = await coordinator.RefreshAsync(discovery.Items, DateTimeOffset.UtcNow, token).ConfigureAwait(true);
            if (!_fence.IsCurrent(cycle) || token.IsCancellationRequested)
            {
                return;
            }

            if (registry.ProbeTargets.Count > 0)
            {
                await coordinator.ProbeAsync(registry.ProbeTargets[0], DateTimeOffset.UtcNow, token).ConfigureAwait(true);
            }

            if (!_fence.IsCurrent(cycle))
            {
                return;
            }

            _ledger.Save(Path.Combine(_root, "sources.json"));
            var eligible = _catalogue.Eligible(new EligibilityContext
            {
                NowUtc = DateTimeOffset.UtcNow,
                NetworkEpoch = _catalogue.NetworkEpoch,
                AllowInsecureCertificates = _catalogue.Settings.AllowInsecureCertificates,
            });
            SubscriptionStatus.Text = "Обновление завершено. Записей: " + _catalogue.Nodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". Рабочих после проверки: " + eligible.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (outcome.AnyFetchFailed ? ". Часть источников недоступна, прежний состав сохранён." : ".");
        }
        catch (OperationCanceledException)
        {
            if (_fence.IsCurrent(cycle))
            {
                SubscriptionStatus.Text = "Обновление отменено. Уже сохранённые записи не удалены.";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or CatalogueStoreException)
        {
            if (_fence.IsCurrent(cycle))
            {
                SubscriptionStatus.Text = "Обновление не выполнено: " + ex.Message;
            }
        }
        finally
        {
            if (Volatile.Read(ref _refreshRun) == run)
            {
                _refreshActive = false;
            }
        }
    }

    private void CancelRefreshClick(object sender, RoutedEventArgs e)
    {
        _refresh?.Cancel();
        _fence.Begin();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scheduleTimer.Start();
        if (_refreshActive)
        {
            return;
        }

        _refreshTask = _scheduler.PulseAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await _refreshTask.ConfigureAwait(true);
    }

    private async Task SendAsync(string operation, object payload)
    {
        try
        {
            var response = await LocalIpcServer.RoundTripAsync("autovpn-broker", new IpcRequest
            {
                ProtocolVersion = ProductLimits.IpcProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"),
                ExpectedStateRevision = _session.StateRevision,
                Operation = operation,
                Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options),
            }, CancellationToken.None).ConfigureAwait(true);
            if (response?.Snapshot is null)
            {
                _session = UiSessionReducer.BrokerUnreachable(_session);
                ShowSession();
                return;
            }

            _session = UiSessionReducer.FromSnapshot(_session, response.Snapshot, response.Message, _catalogue.Settings.DisclosureAccepted);
            ShowSession();
        }
        catch (TimeoutException)
        {
            _session = UiSessionReducer.BrokerUnreachable(_session);
            ShowSession();
        }
        catch (IOException)
        {
            _session = UiSessionReducer.BrokerUnreachable(_session);
            ShowSession();
        }
    }

    private void ShowSession()
    {
        PhaseText.Tag = _session.PhaseCode;
        PhaseText.Text = _session.PhaseLabel;
        DetailText.Text = _session.Detail;
        ConnectButton.Content = _session.PrimaryAction;
    }

    private void ShowPage(UIElement page)
    {
        ConnectionPage.Visibility = Visibility.Collapsed;
        ServersPage.Visibility = Visibility.Collapsed;
        SubscriptionsPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exit)
        {
            _tray.Visible = false;
            _tray.Dispose();
            (_catalogue as IDisposable)?.Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void ExitApplication()
    {
        if (_session.SafetyDisconnectAvailable || _session.LastKnownProtectionArmed)
        {
            await SendAsync(IpcOperations.Disconnect, new DisconnectPayload()).ConfigureAwait(true);
        }

        var decision = UiSessionReducer.PlanExit(_session);
        if (!decision.CanClose)
        {
            DetailText.Text = decision.Reason ?? "Выход не подтверждён.";
            Show();
            return;
        }

        _refresh?.Cancel();
        _fence.Begin();
        if (_refreshTask is not null)
        {
            try
            {
                await _refreshTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
            }
        }

        _exit = true;
        System.Windows.Application.Current.Shutdown();
    }

    private static string ConfigDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "config");
    }

    private static string? ExpectedCoreHash()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "core-manifest.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var name = OperatingSystem.IsWindows() ? "mihomo-windows-amd64.exe" : "mihomo-linux-amd64";
            if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var assetName) &&
                    assetName.ValueKind == JsonValueKind.String &&
                    string.Equals(assetName.GetString(), name, StringComparison.Ordinal) &&
                    asset.TryGetProperty("sha256", out var sha) &&
                    sha.ValueKind == JsonValueKind.String)
                {
                    return sha.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
