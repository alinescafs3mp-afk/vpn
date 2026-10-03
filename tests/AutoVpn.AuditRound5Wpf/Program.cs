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

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "audit-wpf-results");
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(); window.Show(); Pump(100);
            var consent = (CheckBox)window.FindName("DisclosureBox");
            if (consent.IsChecked == true) throw new InvalidOperationException("This audit requires a fresh unconsented runner profile.");
            var pages = new[] { "Подключение", "Серверы", "Подписки", "Настройки" };
            var sizes = new[] { (880, 640), (1120, 760) };
            var records = new List<object>();
            foreach (var size in sizes)
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
            CloseHarnessWindow(window); window=new MainWindow();window.Show();Pump(50);
            var reopenedLan=((CheckBox)window.FindName("LanBox")).IsChecked;var reopenedProtection=((CheckBox)window.FindName("ProtectionBox")).IsChecked;
            var restored=reopenedLan==!originalLan && reopenedProtection==!originalProtection;
            if (!restored) throw new InvalidOperationException("Settings did not survive catalogue/window reopen.");
            ((CheckBox)window.FindName("LanBox")).IsChecked=originalLan;
            ((CheckBox)window.FindName("ProtectionBox")).IsChecked=originalProtection;
            if (((CheckBox)window.FindName("DisclosureBox")).IsChecked==true) throw new InvalidOperationException("Audit accidentally granted consent.");
            var report=new { sourceCommit="49e5bd54e41731b9b96b83789def31e86c701ee6", os=Environment.OSVersion.ToString(), pages=records, settingsSurviveWindowAndCatalogueReopen=restored,
                publicNetworkConsent=false, tun=false, scope="Actual WPF navigation and two settings persisted across window/catalogue reopen in one process. Harness-controlled cleanup, not a real tray Exit, process restart, installer, theme or multi-DPI acceptance." };
            File.WriteAllText(Path.Combine(output,"wpf.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
            Console.WriteLine("ROUND5_WPF: 8 actual renders; 4 navigation handlers; 2 settings persisted across catalogue/window reopen; no consent or TUN.");
            return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(output,"failure.txt"),ex.ToString());Console.Error.WriteLine(ex);return 1; }
        finally { if(window is not null) CloseHarnessWindow(window);app.Shutdown(); }
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
