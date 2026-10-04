using System.Windows;
using AutoVpn.Infrastructure.WindowsService;

namespace AutoVpn.Desktop;

public partial class MainWindow
{
    private Task? _serviceCheckTask;
    private async void CheckServiceClick(object sender, RoutedEventArgs e)
    {
        if (_serviceCheckTask is { IsCompleted: false } || Volatile.Read(ref _shuttingDown) != 0) return;
        _serviceCheckTask = CheckServiceAsync();
        await _serviceCheckTask.ConfigureAwait(true);
    }
    private async Task CheckServiceAsync()
    {
        CheckServiceButton.IsEnabled = false;
        InstalledServiceText.Text = "Проверяется установленная служба Windows…";
        try
        {
            var check = await InstalledServiceClient.QueryAsync(_statusCts.Token).ConfigureAwait(true);
            if (Volatile.Read(ref _shuttingDown) == 0 && !_exit) InstalledServiceText.Text = check.Message;
        }
        catch (OperationCanceledException) { }
        finally { if (!_exit) CheckServiceButton.IsEnabled = true; }
    }
}
