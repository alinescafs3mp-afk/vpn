using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutoVpn.Desktop;
using AutoVpn.Application;
using AutoVpn.Domain;
using AutoVpn.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "audit-wpf");
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow? window = null;
        try
        {
            Pump(200);
            window = app.MainWindow as MainWindow ?? throw new InvalidOperationException("Actual WPF startup did not create MainWindow.");
            if (((CheckBox)window.FindName("DisclosureBox")).IsChecked == true)
                throw new InvalidOperationException("This audit requires a fresh unconsented runner profile.");
            var pages = new[] { "Подключение", "Серверы", "Подписки", "Настройки" };
            var records = new List<object>();
            foreach (var size in new[] { (880, 640), (1120, 760) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                foreach (var page in pages)
                {
                    var button = Descendants(window).OfType<Button>().First(b => Equals(b.Content, page));
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(50); window.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    var filename = Array.IndexOf(pages,page).ToString(CultureInfo.InvariantCulture) + "-" + size.Item1.ToString(CultureInfo.InvariantCulture) + ".png";
                    using (var file = File.Create(Path.Combine(output,filename))) encoder.Save(file);
                    records.Add(new { page, width=window.ActualWidth, height=window.ActualHeight, dpiX=dpi.PixelsPerInchX, dpiY=dpi.PixelsPerInchY, image=filename,
                        visibleText=Descendants(window).OfType<TextBlock>().Where(t=>t.IsVisible).Select(t=>t.Text).ToArray() });
                }
            }
            var lan=(CheckBox)window.FindName("LanBox"); var protection=(CheckBox)window.FindName("ProtectionBox");
            var originalLan=lan.IsChecked;var originalProtection=protection.IsChecked;
            lan.IsChecked=!originalLan;protection.IsChecked=!originalProtection;Pump(20);
            CloseHarnessWindow(window);window=null;
            string? reopenError=null;
            try { window=new MainWindow(); }
            catch(IOException ex)
            {
                reopenError=ex.ToString();
                // Diagnostic only: never count a pool-cleared reopen as an ordinary product pass.
                SqliteConnection.ClearAllPools();
                window=new MainWindow();
            }
            app.MainWindow=window;window.Show();Pump(50);
            var reopenedLan=((CheckBox)window.FindName("LanBox")).IsChecked;var reopenedProtection=((CheckBox)window.FindName("ProtectionBox")).IsChecked;
            var restored=reopenedLan==!originalLan && reopenedProtection==!originalProtection;
            ((CheckBox)window.FindName("LanBox")).IsChecked=originalLan;
            ((CheckBox)window.FindName("ProtectionBox")).IsChecked=originalProtection;
            if (((CheckBox)window.FindName("DisclosureBox")).IsChecked==true) throw new InvalidOperationException("Audit accidentally granted consent.");
            var serviceControl=ValidateServiceControl(window);
            var catalogueHandoff=ValidateCatalogueReadHandoff();
            var report=new { installedServiceControl=serviceControl, sameUserDpapiCatalogueHandoff=catalogueHandoff, sourceCommit=Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "UNBOUND_LOCAL_BUILD", os=Environment.OSVersion.ToString(), pages=records,
                normalWindowCatalogueReopenSucceeded=reopenError is null, reopenError,
                settingsPersisted=restored, diagnosticPoolClearRequired=reopenError is not null, publicNetworkConsent=false,tun=false,
                scope="Actual WPF navigation and two settings across window/catalogue reopen in one process. Any pool clear is diagnostic only. Harness cleanup, not real tray Exit, process restart, installer, theme or multi-DPI acceptance." };
            File.WriteAllText(Path.Combine(output,"wpf.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("ROUND6_WPF: 8 actual renders; normal reopen="+(reopenError is null)+"; persisted settings="+restored+"; no consent or TUN.");
            return reopenError is null && restored ? 0 : 1;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());Console.Error.WriteLine(ex);return 1; }
        finally { if(window is not null) CloseHarnessWindow(window);SqliteConnection.ClearAllPools();app.Shutdown(); }
    }
    private static object ValidateServiceControl(MainWindow window)
    {
        var expectedVersion = "Windows-клиент · " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        var actualVersion = ((TextBlock)window.FindName("VersionText")).Text;
        if (actualVersion != expectedVersion) throw new InvalidOperationException("Displayed version does not match the assembly.");
        Descendants(window).OfType<Button>().First(b => Equals(b.Content, "Настройки"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var button = (Button)window.FindName("CheckServiceButton");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!button.IsEnabled && deadline.Elapsed < TimeSpan.FromSeconds(10)) Pump(50);
        var status = ((TextBlock)window.FindName("InstalledServiceText")).Text;
        if (!button.IsEnabled || status != "Служба Windows не установлена.")
            throw new InvalidOperationException("Fresh-runner service query did not report NotInstalled.");
        Descendants(window).OfType<Button>().First(b => Equals(b.Content, "Серверы"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // Refresh the real maintenance caption without waiting for the five-second timer.
        typeof(MainWindow).GetMethod("ShowMaintenance", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        var maintenance = ((TextBlock)window.FindName("MaintenanceStatus")).Text;
        if (!maintenance.StartsWith("Ожидается согласие", StringComparison.Ordinal))
            throw new InvalidOperationException("Initial maintenance caption did not respect consent.");
        return new { passed = true, actualVersion, status, maintenance, serviceInstalled = false,
            privilegedConnection = "NOT_RUN", protectionEnabled = false };
    }

    private static object ValidateCatalogueReadHandoff()
    {
        var directory=Directory.CreateTempSubdirectory("autovpn-windows-handoff-");
        try
        {
            var path=Path.Combine(directory.FullName,"catalogue.sqlite");
            using var writer=SqliteCatalogue.Open(path,new DpapiSecretProtector());
            var now=DateTimeOffset.UtcNow;
            var semantics=new NodeSemantics {Protocol=ProtocolKind.Vless,Host="203.0.113.80",Port=443,
                UserId="11111111-1111-4111-8111-111111111111",Security="tls",Transport="tcp",Encryption="none"};
            writer.ApplySnapshot(new SnapshotCommit {ArtifactId="windows-synthetic",FamilyId="black-vless",ContentHash="synthetic",Complete=true,NowUtc=now,
                Nodes=[new SnapshotNode {ArtifactId="windows-synthetic",FamilyId="black-vless",Label="synthetic",Semantics=semantics,Digest=CanonicalIdentity.Digest(semantics)}]});
            using var reader=SqliteCatalogue.OpenReadOnly(path,new DpapiSecretProtector());
            var id=writer.Nodes[0].NodeId;
            if(reader.Nodes.Count!=1 || reader.Nodes[0].Semantics.UserId!=semantics.UserId)
                throw new InvalidOperationException("Windows same-user DPAPI read failed.");
            // Synthetic metadata only. No probe, public source, runtime or network changes.
            writer.ApplyAssessment(id,writer.Nodes[0].Assessment! with {MedianLatencyMs=123});
            reader.Refresh();
            if(reader.Nodes[0].Assessment?.MedianLatencyMs!=123)throw new InvalidOperationException("Live assessment handoff failed.");
            reader.SetActiveNode(id);writer.TrySetFavorite(id,true);reader.Refresh();
            if(!reader.Nodes[0].Favorite || !reader.Nodes[0].ActiveSession || writer.Nodes[0].ActiveSession)
                throw new InvalidOperationException("Read-only ownership or writer separation failed.");
            var refused=false;
            try {reader.Settings=reader.Settings with {Revision=reader.Settings.Revision+1};}
            catch(CatalogueStoreException ex) when(ex.Message=="CATALOGUE_READ_ONLY") {refused=true;}
            if(!refused)throw new InvalidOperationException("Read-only catalogue accepted a write.");
            var secret=System.Text.Encoding.UTF8.GetBytes(semantics.UserId!);
            foreach(var file in Directory.GetFiles(directory.FullName))
            {
                using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                using var bytes=new MemoryStream();stream.CopyTo(bytes);
                if(bytes.ToArray().AsSpan().IndexOf(secret)>=0)throw new InvalidOperationException("Synthetic credential persisted as plaintext.");
            }
            return new {passed=true,dpapiCurrentUser=true,separateReadOnlyConnection=true,liveAssessmentRefresh=true,
                writerConflict=false,persistentReadOnlyMutationRefused=true,plainCredentialAbsent=true,
                crossProcess=false,privilegedService=false,syntheticOnly=true,publicTraffic=false};
        }
        finally {directory.Delete(true);}
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested; }
    }
    private static void Pump(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background) { Interval=TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick+=(_,_)=> { timer.Stop();frame.Continue=false; };timer.Start();Dispatcher.PushFrame(frame);
    }
    private static void CloseHarnessWindow(MainWindow window)
    {
        var type=typeof(MainWindow);
        (type.GetField("_scheduleTimer",BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(window) as DispatcherTimer)?.Stop();
        type.GetField("_exit",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,true);window.Close();
    }
}
