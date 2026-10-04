using System.Windows;
using AutoVpn.Infrastructure.Persistence;

namespace AutoVpn.Desktop;

public partial class MainWindow
{
    private async void DisclosureConsentChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (DisclosureBox.IsChecked == true)
        {
            DisclosureChanged(sender, e);
            if (!_checksPaused) _maintenance?.Resume();
            return;
        }
        // Revoke authority before canceling I/O; a late result cannot restore consent.
        var revoked = false;
        try
        {
            _catalogue.Settings = _catalogue.Settings with
            {
                DisclosureAccepted = false,
                Revision = _catalogue.Settings.Revision + 1,
            };
            revoked = true;
            _mailbox.NoteDisclosure(false);
            _maintenance?.Pause();
            _connectCts?.Cancel();
            _refresh?.Cancel();
            _fence.Begin();
            // Revocation is an explicit user stop, including a Connect whose
            // reply was canceled locally but whose broker work may still exist.
            await DisconnectLocalAsync().ConfigureAwait(true);
            DetailText.Text = _mailbox.Session.ClaimsVerifiedDisconnect
                ? "Согласие отозвано. Отключение подтверждено; сохранённый каталог не удалён."
                : "Согласие отозвано. Отключение ещё не подтверждено; состояние защиты показано отдельно.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or OperationCanceledException)
        {
            _maintenance?.Pause();
            _connectCts?.Cancel();
            _refresh?.Cancel();
            _fence.Begin();
            DetailText.Text = revoked
                ? "Согласие отозвано, но отключение ещё не подтверждено. Повторите отключение."
                : "Не удалось сохранить отзыв согласия. Проверки приостановлены: " + ex.GetType().Name;
        }
    }
}
