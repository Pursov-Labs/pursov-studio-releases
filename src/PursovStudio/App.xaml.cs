using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PursovStudio;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    private EventWaitHandle? _showWindow;
    private RegisteredWaitHandle? _showRegistration;

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            if (BrokerService.TryRun(e.Args)) { Shutdown(Environment.ExitCode); return; }
            base.OnStartup(e);
            if (await ElevatedWorker.TryRunAsync(e.Args) || await InstallService.TryRunAsync(e.Args) || await SelfTests.RunAsync(e.Args))
            {
                Shutdown(Environment.ExitCode);
                return;
            }

            await WaitForPreviousInstanceAsync(e.Args);
            bool startHidden = e.Args.Contains("--signin");
            if (startHidden)
            {
                try
                {
                    if (!CoreController.LoadSettings(AppPaths.SettingsFile).AutoStart)
                    { Shutdown(); return; }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
                { startHidden = false; }
            }
            string identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            string suffix = e.Args.Contains("--preview") ? "_PREVIEW" : "";
            string eventName = @"Local\PURSOV_STUDIO_SHOW_" + identity + suffix;
            _instance = new Mutex(true, @"Local\PURSOV_STUDIO_" + identity + suffix, out _ownsMutex);
            if (!_ownsMutex)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(eventName);
                    if (!startHidden) signal.Set();
                }
                catch (WaitHandleCannotBeOpenedException) { }
                Shutdown();
                return;
            }

            _showWindow = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showWindow, (_, _) =>
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray()), null, Timeout.Infinite, false);
            MainWindow = new MainWindow(startHidden);
            MainWindow.Show();
        }
        catch (TimeoutException ex)
        {
            Log.Write(ex.Message);
            MessageBox.Show(ex.Message, "PURSOV STUDIO", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
        catch (Exception ex)
        {
            Log.Write("Не удалось запустить оболочку: " + ex.Message);
            MessageBox.Show("Не удалось запустить PURSOV STUDIO. Подробности сохранены в журнале приложения.",
                "Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task WaitForPreviousInstanceAsync(string[] args)
    {
        int index = Array.IndexOf(args, "--wait-parent");
        if (index < 0) return;
        if (index + 2 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || !long.TryParse(args[index + 2], NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks) || processId == Environment.ProcessId)
            throw new InvalidOperationException("Некорректные параметры перехода к установленному приложению.");

        Process parent;
        try { parent = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            try
            {
                if (parent.StartTime.ToUniversalTime().Ticks != startTicks) return;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await parent.WaitForExitAsync(timeout.Token);
            }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Прежнее окно PURSOV STUDIO не закрылось за 45 секунд. Закройте его через «Выход» в трее, затем снова откройте установленную версию.");
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        _showWindow?.Dispose();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
