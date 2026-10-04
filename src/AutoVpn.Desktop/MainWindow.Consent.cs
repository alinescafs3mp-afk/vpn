using System.Windows;
using AutoVpn.Infrastructure.Persistence;

namespace AutoVpn.Desktop;

public partial class MainWindow
{
    private void DisclosureConsentChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (DisclosureBox.IsChecked == true)
        {
            DisclosureChanged(sender, e);
            if (!_checksPaused) _maintenance?.Resume();
            return;
        }
        // Revoke authority before canceling I/O; a late result cannot restore consent.
        try
        {
            _catalogue.Settings = _catalogue.Settings with
            {
                DisclosureAccepted = false,
                Revision = _catalogue.Settings.Revision + 1,
            };
            _mailbox.NoteDisclosure(false);
            _maintenance?.Pause();
            _connectCts?.Cancel();
            _refresh?.Cancel();
            _fence.Begin();
            DetailText.Text = "Согласие отозвано. Новые загрузки и проверки остановлены; сохранённый каталог не удалён.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or CatalogueStoreException)
        {
            _maintenance?.Pause();
            _connectCts?.Cancel();
            _refresh?.Cancel();
            _fence.Begin();
            DetailText.Text = "Не удалось сохранить отзыв согласия. Проверки приостановлены: " + ex.GetType().Name;
        }
    }
}
