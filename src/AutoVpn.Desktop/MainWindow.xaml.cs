using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;

namespace AutoVpn.Desktop;

public partial class MainWindow : Window
{
    private readonly NotifyIcon _tray;
    private bool _exit;

    public MainWindow()
    {
        InitializeComponent();
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
    }

    private void ShowConnection(object sender, RoutedEventArgs e) => ShowPage(ConnectionPage);

    private void ShowServers(object sender, RoutedEventArgs e) => ShowPage(ServersPage);

    private void ShowSubscriptions(object sender, RoutedEventArgs e) => ShowPage(SubscriptionsPage);

    private void ShowSettings(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);

    private void ShowWorking(object sender, RoutedEventArgs e)
    {
        ServerList.Text = "Рабочие серверы появятся после успешной локальной проверки. Сейчас таких записей нет.";
    }

    private void ShowFavorites(object sender, RoutedEventArgs e)
    {
        ServerList.Text = "Избранное пусто.";
    }

    private void ShowAll(object sender, RoutedEventArgs e)
    {
        ServerList.Text = "Каталог ещё не загружен. Названия и страны из подписки не считаются доказательством доступности.";
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            if (DisclosureBox.IsChecked != true)
            {
                DetailText.Text = "Сначала подтвердите предупреждение о публичных серверах.";
                return;
            }

            if (SessionText.IsConnected(PhaseText.Tag as string))
            {
                await SendAsync(IpcOperations.Disconnect, new DisconnectPayload()).ConfigureAwait(true);
                return;
            }

            await SendAsync(IpcOperations.Connect, new ConnectPayload
            {
                NodeId = "",
                Digest = "",
                NetworkEpoch = 0,
                ProtectionRequired = ProtectionBox.IsChecked == true,
                LanAccess = LanBox.IsChecked == true,
            }).ConfigureAwait(true);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task SendAsync(string operation, object payload)
    {
        try
        {
            var response = await LocalIpcServer.RoundTripAsync("autovpn-broker", new IpcRequest
            {
                ProtocolVersion = ProductLimits.IpcProtocolVersion,
                RequestId = Guid.NewGuid().ToString("N"),
                Operation = operation,
                Payload = JsonSerializer.SerializeToElement(payload, IpcJson.Options),
            }, CancellationToken.None).ConfigureAwait(true);
            if (response?.Snapshot is null)
            {
                PhaseText.Text = Ru.Disconnected;
                PhaseText.Tag = nameof(TunnelPhase.Disconnected);
                DetailText.Text = Ru.ServiceMissing;
                ConnectButton.Content = Ru.Connect;
                return;
            }

            var phase = response.Snapshot.Phase;
            PhaseText.Tag = phase;
            PhaseText.Text = SessionText.Phase(phase);
            DetailText.Text = response.Message ?? Ru.WindowsGate;
            ConnectButton.Content = SessionText.IsConnected(phase) ? Ru.Disconnect : Ru.Connect;
        }
        catch (TimeoutException)
        {
            PhaseText.Text = Ru.Disconnected;
            PhaseText.Tag = nameof(TunnelPhase.Disconnected);
            DetailText.Text = Ru.ServiceMissing;
        }
        catch (IOException)
        {
            PhaseText.Text = Ru.Disconnected;
            PhaseText.Tag = nameof(TunnelPhase.Disconnected);
            DetailText.Text = Ru.ServiceMissing;
        }
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

    private void ExitApplication()
    {
        _exit = true;
        System.Windows.Application.Current.Shutdown();
    }
}
