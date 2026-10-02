using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AutoVpn.AuditWpfSmoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Provide an audit output directory."); return 2; }
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var exit = 1;
        var report = new List<object>();
        var app = new AutoVpn.Desktop.App();
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(output, "startup-error.txt"), e.Exception.ToString());
            e.Handled = true; app.Shutdown(1);
        };
        try
        {
            app.InitializeComponent();
            app.StartupUri = new Uri("pack://application:,,,/AutoVpn.Desktop;component/MainWindow.xaml");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var window = app.MainWindow ?? throw new InvalidOperationException("No real WPF window was created.");
                    if (window.FindName("DisclosureBox") is not CheckBox disclosure || disclosure.IsChecked == true)
                    { throw new InvalidOperationException("The smoke test requires an unconsented fresh profile; it must not fetch public subscriptions."); }
                    var captions = new[] { "Подключение", "Серверы", "Подписки", "Настройки" };
                    for (var index = 0; index < captions.Length; index++)
                    {
                        var button = Descendants(window).OfType<Button>().FirstOrDefault(b => string.Equals(b.Content?.ToString(), captions[index], StringComparison.Ordinal))
                            ?? throw new InvalidOperationException("Navigation control missing: " + captions[index]);
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        var texts = Descendants(window).OfType<TextBlock>().Where(x => x.IsVisible).Select(x => x.Text).ToArray();
                        var controls = Descendants(window).OfType<Button>().Where(x => x.IsVisible).Select(x => x.Content?.ToString()).ToArray();
                        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        image.Render(window);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                        using (var file = File.Create(Path.Combine(output, "view-" + index + ".png"))) { encoder.Save(file); }
                        report.Add(new { page = captions[index], width = window.ActualWidth, height = window.ActualHeight, texts, controls });
                    }
                    File.WriteAllText(Path.Combine(output, "wpf-smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine("WPF_SMOKE: actual App/MainWindow created and all four navigation handlers exercised. No consent, node probing, TUN, service installation, or network changes.");
                    Console.WriteLine(JsonSerializer.Serialize(report));
                    exit = 0;
                }
                catch (Exception error)
                { File.WriteAllText(Path.Combine(output, "smoke-error.txt"), error.ToString()); Console.Error.WriteLine(error); }
                finally { app.Shutdown(exit); }
            };
            timer.Start(); app.Run();
        }
        catch (Exception error)
        { File.WriteAllText(Path.Combine(output, "startup-error.txt"), error.ToString()); Console.Error.WriteLine(error); }
        return exit;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) { yield return nested; }
        }
    }
}
