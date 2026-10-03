using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AutoVpn.AuditRound4Wpf;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var exit = 1; var report = new List<object>();
        var app = new AutoVpn.Desktop.App();
        app.DispatcherUnhandledException += (_, e) => { File.WriteAllText(Path.Combine(output, "wpf-error.txt"), e.Exception.ToString()); e.Handled = true; app.Shutdown(1); };
        try
        {
            app.InitializeComponent(); app.StartupUri = new Uri("pack://application:,,,/AutoVpn.Desktop;component/MainWindow.xaml");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var window = app.MainWindow ?? throw new InvalidOperationException("Window was not created.");
                    if (window.FindName("DisclosureBox") is not CheckBox disclosure || disclosure.IsChecked == true) throw new InvalidOperationException("A fresh unconsented runner profile is required.");
                    var captions = new[] { "Подключение", "Серверы", "Подписки", "Настройки" };
                    foreach (var size in new[] { (Width: 880, Height: 640), (Width: 1120, Height: 760) })
                    {
                        window.Width = size.Width; window.Height = size.Height;
                        for (var index = 0; index < captions.Length; index++)
                        {
                            var button = Descendants(window).OfType<Button>().First(b => b.Content?.ToString() == captions[index]);
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                            var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(window);
                            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                            using (var file = File.Create(Path.Combine(output, $"view-{size.Width}-{index}.png"))) encoder.Save(file);
                            report.Add(new { page = captions[index], width = window.ActualWidth, height = window.ActualHeight,
                                texts = Descendants(window).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToArray() });
                        }
                    }
                    foreach (var name in new[] { "ProtectionBox", "LanBox" })
                    {
                        var checkbox = window.FindName(name) as CheckBox ?? throw new InvalidOperationException("Setting missing: " + name);
                        var before = checkbox.IsChecked; checkbox.IsChecked = before != true; checkbox.IsChecked = before;
                    }
                    File.WriteAllText(Path.Combine(output, "wpf.json"), JsonSerializer.Serialize(new { boundary = "Actual App/MainWindow, four navigation handlers at two window sizes, two setting controls toggled and restored. No consent, public fetch, TUN, DPI/theme/keyboard/tray acceptance.", pages = report }, new JsonSerializerOptions { WriteIndented = true }));
                    exit = 0;
                }
                catch (Exception error) { File.WriteAllText(Path.Combine(output, "smoke-error.txt"), error.ToString()); }
                finally { app.Shutdown(exit); }
            };
            timer.Start(); app.Run();
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(output, "startup-error.txt"), error.ToString()); }
        return exit;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
