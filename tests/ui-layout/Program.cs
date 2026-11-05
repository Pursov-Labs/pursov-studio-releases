using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using PursovStudio;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!args.Contains("--preview")) throw new InvalidOperationException("UI tests require --preview.");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string output = Path.Combine(root, "output", "qa-v2");
        Directory.CreateDirectory(output);
        var application = new Application { ThemeMode = ThemeMode.System, ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var document = XDocument.Load(Path.Combine(root, "src/PursovStudio/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            document.Root!.Element(ns + "Application.Resources")!.Elements());
        application.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        var window = new MainWindow { Left = 20, Top = 20 };
        var checks = new List<object>();
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await Task.Delay(100);
                var core = Field<CoreController>(window, "core");
                object contrastPalette = Field<object>(window, "palette");
                contrastPalette.GetType().GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(contrastPalette, new object[] { "contrast" });
                if (((SolidColorBrush)application.Resources["Accent"]).Color != SystemColors.WindowTextColor
                    || ((SolidColorBrush)application.Resources["ActionFill"]).Color != SystemColors.HighlightColor
                    || ((SolidColorBrush)application.Resources["AccentText"]).Color != SystemColors.HighlightTextColor)
                    throw new InvalidOperationException("High contrast must use paired foreground/background system colors.");
                foreach (string theme in new[] { "light", "dark" })
                {
                    application.ThemeMode = theme == "light" ? ThemeMode.Light : ThemeMode.Dark;
                    object palette = Field<object>(window, "palette");
                    palette.GetType().GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(palette, new object[] { theme });
                    window.Width = 620; window.Height = 650;
                    Refresh(window);
                    await Idle(window);
                    Capture(window, Path.Combine(output, "ui-render-main-" + theme + ".png"));
                    checks.Add(CheckButtonBounds(window, theme + " main"));
                    var details = (Button)window.FindName("DetailsButton");
                    details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var picker = (ComboBox)window.FindName("SectionPicker");
                    for (int section = 0; section < 5; section++)
                    {
                        picker.SelectedIndex = section;
                        await Idle(window);
                        ((FrameworkElement)window.FindName("DetailsPanel")).BringIntoView();
                        await Idle(window);
                        Capture(window, Path.Combine(output, $"ui-render-{theme}-details-{section}.png"));
                    }
                    details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((ScrollViewer)window.FindName("ShellScroll")).ScrollToTop();
                }
                typeof(CoreController).GetProperty("Status")!.SetValue(core, "Подключено");
                typeof(CoreController).GetProperty("Detail")!.SetValue(core, "Приложение работает в фоне.");
                typeof(CoreController).GetProperty("LastChecked")!.SetValue(core, DateTimeOffset.UtcNow);
                typeof(CoreController).GetField("desiredConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(core, true);
                typeof(CoreController).GetProperty("Results")!.SetValue(core, new[]
                {
                    new ProbeResult("Telegram", "Прокси отвечает", true),
                    new ProbeResult("Discord · сайт/API", "Доступен", true),
                    new ProbeResult("Discord · соединение", "Доступен", true),
                    new ProbeResult("Discord · голос", "Не проверен", null, 0, "Нужен реальный звонок"),
                    new ProbeResult("YouTube · сайт", "Доступен", true),
                    new ProbeResult("YouTube · видео", "Не проверено", null)
                });
                window.Width = 360; window.Height = 620;
                Refresh(window);
                await Idle(window);
                Capture(window, Path.Combine(output, "ui-render-compact-partial.png"));
                checks.Add(CheckButtonBounds(window, "compact partial"));
                File.WriteAllText(Path.Combine(output, "ui-layout-checks.json"), JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Note = "Actual WPF controls rendered in a preview test process. Theme and compact layout checks do not simulate physical DPI or Narrator.",
                    Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("WPF UI layout: 13 captures, 3 button visibility checks passed.");
                application.Shutdown();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; application.Shutdown(1); }
        }, DispatcherPriority.ApplicationIdle);
        application.Run(window);
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Refresh(MainWindow window) => typeof(MainWindow).GetMethod("RefreshView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    private static async Task Idle(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(200);
    }
    private static object CheckButtonBounds(MainWindow window, string name)
    {
        var button = (Button)window.FindName("MainButton");
        var root = (FrameworkElement)window.Content;
        Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
        if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > root.ActualWidth || bounds.Bottom > root.ActualHeight)
            throw new Exception("Main button clipped in " + name);
        return new { Name = name, Width = root.ActualWidth, Height = root.ActualHeight, Button = bounds.ToString() };
    }
    private static void Capture(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(content.RenderSize);
            // The native window backdrop is outside WPF's captured child visual.
            drawing.DrawRectangle((Brush)window.FindResource("Ground"), null, bounds);
        }
        bitmap.Render(visual);
        bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
}
