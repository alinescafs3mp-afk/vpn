using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Broker;
using AutoVpn.Infrastructure.Import;
using AutoVpn.Infrastructure.Persistence;
using AutoVpn.Infrastructure.Runtime;

namespace AutoVpn.Desktop;

public partial class MainWindow
{
    private sealed record ServerRow(string Id,string Caption);
    private string? _selectedServer;
    private LiveSessionClient? _liveClient;
    private CancellationTokenSource? _liveCts;
    private bool _liveBusy;
    private bool _pollBusy;
    private bool _stopLiveBusy;
    private async void ConnectLiveClick(object sender,RoutedEventArgs e)
    {
        if(_liveClient is not null||_liveBusy) { _liveCts?.Cancel();await StopLiveAsync();return; }
        if(!_catalogue.Settings.DisclosureAccepted) {DetailText.Text="Подтвердите предупреждение о публичных серверах.";return;}
        var tun=LiveMode.SelectedIndex==1;
        if(tun&&_catalogue.Settings.ProtectionOnConnect)
        {DetailText.Text="TUN V3 не имеет аварийной блокировки. Для отдельного теста отключите «Защита при обрыве» в настройках. Защищённый режим не подменяется незащищённым.";return;}
        if(tun&&System.Windows.MessageBox.Show("Экспериментальный TUN изменит маршруты Windows. При падении ядра трафик может пойти напрямую. Не используйте для чувствительных данных или через единственный канал удалённого управления. Продолжить без аварийной блокировки?",
            "AutoVPN V3: тест TUN",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        _liveBusy=true;_liveCts=new CancellationTokenSource();var token=_liveCts.Token;
        try
        {
            CatalogueNode? node;
            lock(_catalogue.SyncRoot)
            {
                node=_selectedServer is null?_catalogue.Eligible(new EligibilityContext
                {
                    NowUtc=DateTimeOffset.UtcNow,NetworkEpoch=_catalogue.NetworkEpoch,
                    AllowInsecureCertificates=_catalogue.Settings.AllowInsecureCertificates,
                    RequiredTargetSetId=_maintenance?.TargetSetId,
                }).FirstOrDefault():_catalogue.Nodes.FirstOrDefault(n=>n.NodeId==_selectedServer);
            }
            if(node is null){DetailText.Text="Выберите сервер во вкладке «Серверы» или обновите подписки.";return;}
            var host=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","session","AutoVpn.SessionHost.exe"));
            if(!File.Exists(host)){DetailText.Text="Нет session/AutoVpn.SessionHost.exe. Распакуйте полный комплект V3, не только desktop.exe.";return;}
            PhaseText.Text="Проверка выбранного сервера";ConnectButton.Content="Отменить";
            if(_maintenance is null||!await _maintenance.CheckNowAsync(node.NodeId,token))
            {DetailText.Text="Не получены два успешных HTTPS-ответа. Подключение не начато.";return;}
            token.ThrowIfCancellationRequested();
            if(!_catalogue.Settings.DisclosureAccepted)throw new OperationCanceledException();
            _maintenance.Pause();
            if(!await _maintenance.WaitForIdleAsync(TimeSpan.FromSeconds(15)))throw new IOException("Проверяющие процессы ещё не остановились.");
            token.ThrowIfCancellationRequested();
            lock(_catalogue.SyncRoot)
            {
                node=_catalogue.Nodes.First(n=>n.NodeId==node.NodeId);
                var wire=NodeWireFactory.FromCatalogue(node);_ = LiveNodePolicy.Validate(wire,_catalogue.Settings.AllowInsecureCertificates);
            }
            _liveClient=new LiveSessionClient();
            await _liveClient.StartAsync(host,new(){Node=NodeWireFactory.FromCatalogue(node),Tun=tun,
                AllowUnprotectedTun=tun,AllowInsecureProxy=_catalogue.Settings.AllowInsecureCertificates,LanAccess=_catalogue.Settings.LanAccess},token);
            ShowLiveSession();
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or OperationCanceledException or Win32Exception or UnauthorizedAccessException)
        {
            var message=ex is Win32Exception {NativeErrorCode:1223}?"Запрос UAC отменён.":ex is OperationCanceledException?"Подключение отменено.":"Запуск не выполнен: "+ex.GetType().Name;
            await StopLiveAsync();DetailText.Text=message;
        }
        finally
        {
            _liveBusy=false;
            if(_liveClient is null){ConnectButton.Content="Подключить";PhaseText.Text="Не подключён";if(!_checksPaused)_maintenance?.Resume();}
        }
    }
    private async Task<bool> StopLiveAsync()
    {
        if(_stopLiveBusy)return false;
        _stopLiveBusy=true;_liveCts?.Cancel();
        try
        {
            if(_liveClient is not null)
            {
                PhaseText.Text="Отключение и очистка";
                await _liveClient.StopAsync();_liveClient=null;
            }
            ShowLiveSession();if(!_checksPaused&&_catalogue.Settings.DisclosureAccepted)_maintenance?.Resume();return true;
        }
        catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {DetailText.Text="Отключение не подтверждено: "+ex.GetType().Name+". Проверьте процесс и интерфейс AutoVPN-V3; выход не объявлен безопасным.";return false;}
        finally{_stopLiveBusy=false;}
    }
    private async Task PollLiveAsync()
    {
        if(_pollBusy||_liveClient is null||_stopLiveBusy)return;
        _pollBusy=true;
        try{await _liveClient.StatusAsync(CancellationToken.None);ShowLiveSession();}
        catch(Exception ex) when(ex is IOException or OperationCanceledException or InvalidOperationException)
        {PhaseText.Text="Связь с сеансом потеряна";DetailText.Text="Состояние сети не подтверждено. Нажмите отключение для сверки.";}
        finally{_pollBusy=false;}
    }
    private void ShowLiveSession()
    {
        if(!_ready)return;
        var s=_liveClient?.Snapshot;
        if(s is null){PhaseText.Text="Не подключён";ConnectButton.Content="Подключить";return;}
        PhaseText.Text=s.Phase switch {"Connected"=>s.Tun?"TUN работает (без блокировки)":"Прокси работает","Starting"=>"Запуск и проверка трафика","Failed"=>"Сеанс не подтверждён",_=>"Отключён"};
        DetailText.Text=s.Phase=="Connected"?$"HTTPS: {s.HttpsLatencyMs} мс. Получено: {s.DownloadBytes/1024.0:F1} КиБ; отправлено: {s.UploadBytes/1024.0:F1} КиБ. "+
            (s.Tun?"Тестовый TUN. Kill switch отсутствует.":$"HTTP/SOCKS5: 127.0.0.1:{s.ProxyPort}. Укажите этот адрес в приложении. Системный прокси не изменяется."):
            s.Error??"Проверяется настоящий канал через выбранный сервер. До успешного ответа состояние не считается подключённым.";
        ConnectButton.Content="Отключить";
    }
    private void UpdateServerRows()
    {
        if(!_ready)return;
        lock(_catalogue.SyncRoot)
        {
            IEnumerable<CatalogueNode> nodes=_catalogue.Nodes;
            if(_serverView=="favorites")nodes=nodes.Where(n=>n.Favorite);
            if(_serverView=="working")nodes=_catalogue.Eligible(new(){NowUtc=DateTimeOffset.UtcNow,NetworkEpoch=_catalogue.NetworkEpoch,
                RequiredTargetSetId=_maintenance?.TargetSetId,AllowInsecureCertificates=_catalogue.Settings.AllowInsecureCertificates});
            var rows=nodes.Take(500).Select(n=>new ServerRow(n.NodeId,$"{(n.Favorite?"★ ":"")}{n.Label} | {n.Semantics.Protocol} | {n.AdvertisedCountry??"?"} | {n.Assessment?.Health} | {n.Assessment?.MedianLatencyMs?.ToString()??"?"} мс")).ToArray();
            ServerRows.ItemsSource=rows;
            if(_selectedServer is not null)ServerRows.SelectedItem=rows.FirstOrDefault(r=>r.Id==_selectedServer);
        }
    }
    private void ServerSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(ServerRows.SelectedItem is not ServerRow row)return;
        _selectedServer=row.Id;SelectedServerText.Text="Сервер: "+row.Caption;
    }
    private async void VerifySelectedClick(object sender,RoutedEventArgs e)
    {
        if(_selectedServer is null||_maintenance is null)return;
        if(_liveClient is not null){MaintenanceStatus.Text="Отключите сеанс перед проверкой кандидатов: обход через активный туннель недопустим.";return;}
        try{var ok=await _maintenance.CheckNowAsync(_selectedServer,CancellationToken.None);UpdateServerRows();MaintenanceStatus.Text=ok?"Два HTTPS-ответа подтверждены.":"Проверка не подтверждена; сервер не добавлен в рабочие.";}
        catch(Exception ex) when(ex is IOException or OperationCanceledException or InvalidOperationException){MaintenanceStatus.Text="Проверка остановлена: "+ex.GetType().Name;}
    }
    private void ToggleFavoriteClick(object sender,RoutedEventArgs e)
    {
        if(_selectedServer is null)return;
        try{var node=_catalogue.Nodes.FirstOrDefault(n=>n.NodeId==_selectedServer);if(node is not null)_catalogue.TrySetFavorite(node.NodeId,!node.Favorite);UpdateServerRows();}
        catch(CatalogueStoreException){MaintenanceStatus.Text="Не удалось сохранить избранное.";}
    }
    private async void ImportFileClick(object sender,RoutedEventArgs e)
    {
        var picker=new Microsoft.Win32.OpenFileDialog{Filter="Подписки|*.txt;*.yaml;*.yml;*.json|Все файлы|*.*"};
        if(picker.ShowDialog()!=true)return;
        try
        {
            if(new FileInfo(picker.FileName).Length>ProductLimits.MaxArtifactBytes)throw new InvalidDataException("SIZE_LIMIT");
            var text=await File.ReadAllTextAsync(picker.FileName,new UTF8Encoding(false,true));
            var batch=SubscriptionImporter.Import(text,new(){AllowInsecureCertificates=_catalogue.Settings.AllowInsecureCertificates});
            if(!batch.DocumentValid||batch.LimitExceeded||batch.Pending==0){MaintenanceStatus.Text="В файле нет поддерживаемых разрешённых серверов.";return;}
            var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();var artifact="manual:"+hash;
            _catalogue.ApplySnapshot(new(){ArtifactId=artifact,FamilyId="manual",ContentHash=hash,Complete=true,NowUtc=DateTimeOffset.UtcNow,
                Nodes=batch.Records.Where(r=>r.Disposition==RecordDisposition.Pending&&r.Semantics is not null&&r.Digest is not null)
                .Select(r=>new SnapshotNode{ArtifactId=artifact,FamilyId="manual",Digest=r.Digest!,Semantics=r.Semantics!,Label=r.DisplayName,AdvertisedCountry=r.AdvertisedCountry}).ToArray()});
            _serverView="all";UpdateServerRows();MaintenanceStatus.Text="Импортировано: "+batch.Pending+". Это кандидаты, а не уже проверенные серверы.";
        }
        catch(Exception ex) when(ex is IOException or DecoderFallbackException or CatalogueStoreException or InvalidOperationException)
        {MaintenanceStatus.Text="Импорт не выполнен: "+ex.GetType().Name;}
    }
}
