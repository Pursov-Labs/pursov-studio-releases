using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

public sealed class CoreController : IAsyncDisposable
{
    private readonly IZapretService zapret;
    private readonly ITgService tg;
    private readonly ProbeService probes = new();
    private readonly AppUpdateService updates = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private CancellationTokenSource? backgroundOperation;
    private AppSettings settings = new();
    private Task? monitor;
    private Task? updateCheck;
    private Task? startupConnection;
    private DateTimeOffset nextProbe;
    private DateTimeOffset nextRecovery;
    private DateTimeOffset? stableSince;
    private DateTimeOffset nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(15);
    private volatile bool networkChanged;
    private bool corruptSettings;
    private bool corruptTg;
    private SynchronizationContext? uiContext;

    public CoreController() : this(new ZapretService(), new TgService()) { }
    internal CoreController(IZapretService zapret, ITgService tg)
    {
        this.zapret = zapret;
        this.tg = tg;
        updates.Changed += Notify;
    }

    public bool AutoStart => settings.AutoStart;
    public bool AutoConnect => settings.AutoConnect;
    public bool DesiredConnected => desiredConnected;
    public bool UpdateReady => updates.Ready;
    public bool NeedsSettingsRecovery => corruptSettings || corruptTg;
    internal Task StartupConnection => startupConnection ?? Task.CompletedTask;
    public DateTimeOffset? LastChecked { get; private set; }
    public ConnectionPhase Phase { get; private set; }
    public ConnectionSnapshot Snapshot => new(Phase, desiredConnected, zapret.IsRunning, tg.IsRunning, LastChecked);

    public void SavePreferences(bool autoStart, bool autoConnect)
    {
        if (Preview || NeedsSettingsRecovery) return;
        settings.AutoStart = autoStart;
        settings.AutoConnect = autoConnect;
        Save(); Notify();
    }

    private bool disposed;
    private int failures;
    private bool desiredConnected;
    private bool Preview => Environment.GetCommandLineArgs().Contains("--preview");

    public event Action? Changed;
    public event Action? InstallationCompleted;
    public string Status { get; private set; } = "Готов к подключению";
    public string Detail { get; private set; } = "Доступ к Telegram, Discord и YouTube";
    public string MainAction => IsBusy ? "Отменить" : (desiredConnected || IsConnected) ? "Отключить" : "Подключить";
    public string ProgressText { get; private set; } = "";
    public bool IsBusy { get; private set; }
    public bool IsConnected => zapret.IsRunning || tg.IsRunning;
    public bool IsInstalled => InstallService.IsInstalled;
    public IReadOnlyList<ProbeResult> Results { get; private set; } = Array.Empty<ProbeResult>();
    public IReadOnlyList<StrategyResult> Strategies => settings.LastResults;
    public IReadOnlyList<ConflictInfo> Conflicts { get; private set; } = Array.Empty<ConflictInfo>();
    public string UpdateStatus => Preview ? "Предпросмотр интерфейса" : updates.Status;

    public async Task InitializeAsync()
    {
        uiContext = SynchronizationContext.Current;
        Results = EmptyResults();
        if (Preview) { Notify(); return; }
        try { settings = LoadSettings(AppPaths.SettingsFile); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            corruptSettings = true;
            Log.Write("Настройки приложения не прочитаны: " + ex.GetType().Name);
        }
        if (!IsInstalled)
        {
            Status = "Откройте установщик";
            Detail = "Запустите MSI-файл PURSOV STUDIO. Он установит всё необходимое.";
            Notify(); return;
        }
        bool telegramReady = false;
        if (!corruptSettings)
        {
            try
            {
                await ImportTelegramAsync(lifetime.Token);
                telegramReady = true;
                RefreshConflicts();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Detail = ex.Message;
                Log.Write("Настройка Telegram: " + ex.GetType().Name);
            }
        }
        if (disposed) return;
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        if (tg is TgService telegram) telegram.StateChanged += ComponentChanged;
        if (zapret is ZapretService engine) engine.StateChanged += ComponentChanged;
        monitor = MonitorAsync();
        updateCheck = CheckUpdatesAsync(false);
        if (NeedsSettingsRecovery)
        {
            Status = "Нужно восстановить настройки";
            Detail = "Откройте «Подробнее». Исходный файл будет сохранён перед восстановлением.";
            Phase = ConnectionPhase.Error; Notify(); return;
        }
        bool resume = Environment.GetCommandLineArgs().Contains("--resume");
        bool stayOff = Environment.GetCommandLineArgs().Any(x => x is "--no-connect" or "--stay-off");
        if (!stayOff && telegramReady && (AutoConnect || resume) && !Conflicts.Any(x => x.BlocksStart)) startupConnection = ToggleAsync();
        else { Status = telegramReady ? "Готово к подключению" : "Нужна настройка Telegram"; Notify(); }
    }

    internal static AppSettings LoadSettings(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Файл настроек слишком большой.");
        var loaded = AppPaths.ReadJson<AppSettings>(path) ?? throw new InvalidDataException("Файл настроек пуст.");
        if (loaded.Schema is < 1 or > 2 || loaded.LastResults == null || loaded.LastResults.Count > 256
            || loaded.LastResults.Any(x => x == null))
            throw new InvalidDataException("Формат настроек не поддерживается.");
        return loaded;
    }

    private async Task ImportTelegramAsync(CancellationToken token)
    {
        try
        {
            var messages = await Task.Run(() => tg.DiscoverAndImportAsync(token), token);
            foreach (string message in messages) Log.Write(message);
            if (!tg.HasConfiguration)
                throw new InvalidOperationException(messages.LastOrDefault() ?? "Не удалось создать настройки Telegram.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            corruptTg = File.Exists(AppPaths.TgConfigFile);
            throw;
        }
    }

    public async Task ResetSettingsAsync()
    {
        if (Preview) return;
        await Run(async token =>
        {
            await DisconnectAsync();
            string suffix = ".backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
            if (corruptSettings)
            {
                if (File.Exists(AppPaths.SettingsFile)) File.Move(AppPaths.SettingsFile, AppPaths.SettingsFile + suffix, false);
                settings = new(); Save(); corruptSettings = false;
            }
            if (corruptTg)
            {
                if (File.Exists(AppPaths.TgConfigFile)) File.Move(AppPaths.TgConfigFile, AppPaths.TgConfigFile + suffix, false);
                corruptTg = false;
            }
            await ImportTelegramAsync(token);
            Status = "Настройки восстановлены";
            Detail = "Нажмите «Подключить», затем подтвердите настройку в Telegram.";
            RefreshConflicts();
        });
    }

    public async Task ToggleAsync()
    {
        if (Preview) return;
        if (IsBusy) { Cancel(); return; }
        await Run(async token =>
        {
            if (desiredConnected || IsConnected) { await DisconnectAsync(); return; }
            if (!IsInstalled) throw new InvalidOperationException("Сначала откройте MSI-файл PURSOV STUDIO.");
            if (NeedsSettingsRecovery) throw new IOException("Сначала восстановите настройки в разделе «Подробнее».");
            RefreshConflicts();
            var block = Conflicts.FirstOrDefault(x => x.BlocksStart);
            if (block != null) throw new IOException(block.Detail);
            Phase = ConnectionPhase.Starting; Status = "Подключаюсь"; Detail = "Запускаю соединение."; Notify();
            try
            {
                desiredConnected = true; failures = 0; stableSince = null;
                await tg.StartAsync(token);
                string network = NetworkKey();
                if (zapret.GetStrategies().Count == 0) throw new IOException("В поставке нет способов подключения. Переустановите PURSOV STUDIO.");
                bool cached = settings.NetworkKey == network && zapret.GetStrategies().Any(x => x.Id == settings.LastStrategy);
                string strategy = cached ? settings.LastStrategy! : zapret.GetStrategies().First().Id;
                await zapret.StartAsync(strategy, token);

                if (cached)
                {
                    Results = await probes.CheckAsync(tg, token); Notify();
                    LastChecked = DateTimeOffset.Now; nextProbe = DateTimeOffset.UtcNow.AddMinutes(5);
                }
                else await SelectStrategyAsync(token);
                settings.SetupComplete = true;
                settings.NetworkKey = network;
                settings.LastSuccess = TransportHealthy(Results) ? DateTimeOffset.UtcNow : null;
                networkChanged = false;
                Save(); SetConnectionStatus();
            }
            catch { desiredConnected = false; await StopComponentsAsync(); throw; }
        });
    }

    private async Task SelectStrategyAsync(CancellationToken token)
    {
        var strategies = zapret.GetStrategies();
        if (strategies.Count == 0) throw new IOException("В установленном Zapret нет поддерживаемых стратегий.");
        Phase = ConnectionPhase.Checking;
        settings.LastResults.Clear();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(10));
        var resultsByStrategy = new Dictionary<string, IReadOnlyList<ProbeResult>>();
        string? winner = null;
        int index = 0;
        foreach (var strategy in strategies)
        {
            token.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested) break;
            Status = "Подбираю способ подключения"; ProgressText = (++index) + " из " + strategies.Count + " · " + strategy.Name;
            Detail = "Варианты проверяются последовательно; во время переключения возможен короткий перерыв."; Notify();
            using var candidate = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            candidate.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                await zapret.StartAsync(strategy.Id, candidate.Token);
                var result = await probes.CheckAsync(tg, candidate.Token);
                Results = result; resultsByStrategy[strategy.Id] = result;
                settings.LastResults.Add(Score(strategy.Id, result));
                if (Score(strategy.Id, result).Passed == 4)
                {
                    var confirmation = await probes.CheckAsync(tg, candidate.Token);
                    if (Score(strategy.Id, confirmation).Passed == 4)
                    {
                        Results = confirmation;
                        settings.LastStrategy = strategy.Id;
                        settings.NetworkKey = NetworkKey();
                        settings.LastSuccess = TransportHealthy(Results) ? DateTimeOffset.UtcNow : null;
                        LastChecked = DateTimeOffset.Now;
                        nextProbe = DateTimeOffset.UtcNow.AddMinutes(5);
                        ProgressText = "Способ подключения проверен";
                        Save(); Notify(); return;
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { settings.LastResults.Add(new(strategy.Id, 0, 4, 25000, DateTimeOffset.UtcNow)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write(strategy.Name + ": " + ex.Message); settings.LastResults.Add(new(strategy.Id, 0, 4, 25000, DateTimeOffset.UtcNow)); }
            Notify();
        }
        token.ThrowIfCancellationRequested();
        var ranked = settings.LastResults.OrderByDescending(x => x.Passed).ThenBy(x => x.Milliseconds).ToArray();
        if (ranked.Length == 0 || ranked[0].Passed == 0) throw new IOException("Ни один проверенный способ не подтвердил связь. Проверьте интернет и журнал.");
        var finalists = ranked.Take(2).ToArray();
        var verified = new List<(StrategyResult Score, IReadOnlyList<ProbeResult> Results, int Successes)>();
        foreach (var f in finalists)
        {
            token.ThrowIfCancellationRequested();
            await zapret.StartAsync(f.Id, token);
            var samples = new List<IReadOnlyList<ProbeResult>>();
            for (int repeat = 0; repeat < 3; repeat++)
            {
                Status = "Перепроверяю лучшие варианты"; ProgressText = f.Id + " · проверка " + (repeat + 1) + " из 3"; Notify();
                samples.Add(await probes.CheckAsync(tg, token));
            }
            var scores = samples.Select(x => Score(f.Id, x)).ToArray();
            int stable = samples.Count(TransportHealthy);
            var median = scores.OrderBy(x => x.Passed).ThenByDescending(x => x.Milliseconds).ElementAt(1);
            verified.Add((median, samples[2], stable));
        }
        var selected = verified.OrderByDescending(x => x.Score.Passed).ThenByDescending(x => x.Successes).ThenBy(x => x.Score.Milliseconds).First();
        winner = selected.Score.Id;
        await zapret.StartAsync(winner, token);
        Results = await probes.CheckAsync(tg, token);
        settings.LastStrategy = winner;
        settings.NetworkKey = NetworkKey();
        settings.LastSuccess = TransportHealthy(Results) ? DateTimeOffset.UtcNow : null;
        LastChecked = DateTimeOffset.Now; nextProbe = DateTimeOffset.UtcNow.AddMinutes(5);
        ProgressText = "Выбран " + winner + " · проверено " + index + " из " + strategies.Count;
        Log.Write(ProgressText); Save(); Notify();
    }

    public async Task CheckAsync()
    {
        if (Preview) return;
        await Run(async token =>
        {
            if (!IsConnected) throw new InvalidOperationException("Сначала нажмите «Подключить».");
            Phase = ConnectionPhase.Checking; Status = "Проверяю связь"; Notify();
            Results = await probes.CheckAsync(tg, token);
            LastChecked = DateTimeOffset.Now; nextProbe = DateTimeOffset.UtcNow.AddMinutes(5);
            RefreshConflicts(); SetConnectionStatus();
        });
    }

    public async Task FindStrategyAsync()
    {
        if (Preview) return;
        await Run(async token =>
        {
            if (!desiredConnected) throw new IOException("Сначала нажмите «Подключить».");
            string? previous = settings.LastStrategy;
            try { await SelectStrategyAsync(token); SetConnectionStatus(); }
            catch (OperationCanceledException)
            {
                if (previous != null)
                {
                    using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await zapret.StartAsync(previous, restore.Token);
                    Results = EmptyResults(); LastChecked = null;
                }
                throw;
            }
        });
    }

    public Task CheckUpdatesAsync() => CheckUpdatesAsync(true);

    private Task CheckUpdatesAsync(bool force)
    {
        if (Preview || disposed || !IsInstalled || IsBusy) return Task.CompletedTask;
        if (updateCheck?.IsCompleted == false) return updateCheck;
        updateCheck = RunUpdateCheckAsync(force);
        return updateCheck;
    }

    private async Task RunUpdateCheckAsync(bool force)
    {
        try { await updates.CheckAndDownloadAsync(lifetime.Token, force); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Log.Write("Обновление пока недоступно: " + ex.GetType().Name); }
        Notify();
    }

    public async Task ApplyUpdatesAsync()
    {
        if (Preview || !UpdateReady) return;
        bool accepted = false;
        bool reconnect = false;
        await Run(async token =>
        {
            bool resume = desiredConnected;
            await updates.PrepareRestartWaiterAsync(token);
            string transactionId = updates.TransactionId!;
            await DisconnectAsync();
            Status = "Устанавливаю обновление";
            Detail = "Приложение откроется после установки."; Notify();
            try
            {
                updates.LaunchRestartWaiter(resume);
                await BrokerClient.ApplyUpdateAsync(transactionId, token);
                accepted = true;
            }
            catch
            {
                updates.CancelRestartWaiter();
                updates.InvalidateTransaction();
                reconnect = resume;
                throw;
            }
        });
        if (accepted) InstallationCompleted?.Invoke();
        else if (reconnect) await ToggleAsync();
    }

    public async Task ConfigureTelegramAsync()
    {
        if (Preview) return;
        await Run(async token =>
        {
            await ImportTelegramAsync(token);
            string uri = await tg.GetProxyUriAsync(token);
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception)
            {
                Detail = "Установите Telegram Desktop, затем снова нажмите «Настроить Telegram».";
                Notify(); return;
            }
            Detail = "Подтвердите MTProxy в Telegram один раз. При выключенном локальном прокси эта настройка остаётся в Telegram.";
            Notify();
        });
    }

    public Task InstallAsync()
    {
        Detail = "Закройте приложение и откройте MSI-файл PURSOV STUDIO.";
        Notify(); return Task.CompletedTask;
    }

    public void Cancel() { operation?.Cancel(); ProgressText = "Отменяю и завершаю текущие операции…"; Notify(); }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (disposed || IsBusy) return;
        IsBusy = true;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        bool acquired = false;
        Notify();
        backgroundOperation?.Cancel();
        try
        {
            await gate.WaitAsync(operation.Token);
            acquired = true;
            operation.Token.ThrowIfCancellationRequested();
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            SetConnectionStatus();
            ProgressText = "Операция отменена";
        }
        catch (Exception ex) { Phase = IsConnected ? ConnectionPhase.Partial : ConnectionPhase.Error; Status = IsConnected ? "Подключено частично" : "Не удалось подключиться"; Detail = ex.Message; Log.Write(ex.Message); }
        finally
        {
            operation.Dispose(); operation = null; IsBusy = false;
            if (acquired) gate.Release();
            Notify();
        }
    }

    private async Task DisconnectAsync()
    {
        desiredConnected = false; failures = 0; stableSince = null;
        Phase = ConnectionPhase.Stopping; Status = "Отключаюсь"; Notify();
        bool stopped = await StopComponentsAsync();
        Phase = stopped ? ConnectionPhase.Off : ConnectionPhase.Error;
        Status = stopped ? "Отключено" : "Не удалось завершить соединение";
        Detail = stopped ? "Чтобы включить снова, нажмите «Подключить»." : "Откройте «Подробнее» и проверьте журнал.";
        ProgressText = ""; Results = EmptyResults(); LastChecked = null; Notify();
        if (!stopped) throw new IOException(Detail);
    }

    internal async Task<bool> StopComponentsAsync()
    {
        // Attempt both stops even if one component fails to answer.
        bool[] stopped = await Task.WhenAll(StopOneAsync(zapret.StopAsync), StopOneAsync(tg.StopAsync));
        return stopped.All(x => x);
    }

    private static async Task<bool> StopOneAsync(Func<Task> stop)
    {
        try { await stop().WaitAsync(TimeSpan.FromSeconds(15)); return true; }
        catch (Exception ex) { Log.Write("Остановка компонента: " + ex.Message); return false; }
    }

    private void SetConnectionStatus()
    {
        if (!IsConnected)
        {
            bool exhausted = desiredConnected && failures >= 3;
            Phase = exhausted ? ConnectionPhase.Error : desiredConnected ? ConnectionPhase.Recovering : ConnectionPhase.Off;
            Status = exhausted ? "Не удалось восстановить связь" : desiredConnected ? "Восстанавливаю соединение" : "Отключено";
            Detail = exhausted ? "Отключите и включите соединение, чтобы повторить." : desiredConnected ? "Повторю попытку автоматически." : "Нажмите «Подключить», чтобы начать.";
        }
        else
        {
            bool partial = !zapret.IsRunning || !tg.IsRunning || Results.Any(x => x.Success == false);
            Phase = partial ? ConnectionPhase.Partial : ConnectionPhase.Connected;
            Status = partial ? "Подключено частично" : "Подключено";
            Detail = partial ? "Некоторые проверки не прошли. Подробности доступны ниже." : "Соединение включено.";
        }
        Notify();
    }

    private void RefreshConflicts()
    {
        var found = new List<ConflictInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string name = process.ProcessName;
                    if (name.StartsWith("TgWsProxy", StringComparison.OrdinalIgnoreCase)) found.Add(new("Отдельный TG Proxy", "Закройте старый TG WS Proxy через его меню перед подключением PURSOV STUDIO. Настройки уже будут сохранены.", true));
                    else if ((name.Equals("winws", StringComparison.OrdinalIgnoreCase) || name.Equals("winws2", StringComparison.OrdinalIgnoreCase)) && !zapret.IsRunning) found.Add(new("Отдельный Zapret", "Обнаружен запущенный winws. Завершите старый экземпляр штатно; оболочка не будет останавливать чужой процесс.", true));
                    else if (name.Equals("xray", StringComparison.OrdinalIgnoreCase)) found.Add(new("Другой сетевой инструмент", "Работает xray. Его маршрут может влиять на результаты; процесс сохранён.", false));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        if (!tg.IsRunning && IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == tg.Port && System.Net.IPAddress.IsLoopback(x.Address)))
            found.Add(new("Порт Telegram занят", "Локальный порт " + tg.Port + " занят. Закройте прежний прокси или освободите этот порт; адрес Telegram не меняется автоматически.", true));
        Conflicts = found.DistinctBy(x => x.Name).ToArray(); Notify();
    }

    private void NetworkChanged(object? sender, EventArgs args) => networkChanged = true;

    private void ComponentChanged() => uiContext?.Post(_ =>
    {
        if (disposed || IsBusy) return;
        if (!zapret.IsRunning || !tg.IsRunning) { Results = EmptyResults(); LastChecked = null; }
        SetConnectionStatus();
    }, null);

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                if (disposed) break;
                if (DateTimeOffset.UtcNow >= nextUpdateCheck && updateCheck?.IsCompleted != false)
                {
                    nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(15);
                    updateCheck = CheckUpdatesAsync(false);
                }
                if (!desiredConnected || IsBusy || !await gate.WaitAsync(0)) continue;
                backgroundOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                try
                {
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (networkChanged)
                    {
                        networkChanged = false;
                        Results = EmptyResults(); LastChecked = null;
                        nextProbe = now.AddMinutes(5); failures = 0; stableSince = null; Notify();
                    }
                    if (!zapret.IsRunning || !tg.IsRunning)
                    {
                        stableSince = null;
                        if (failures >= 3 || now < nextRecovery) continue;
                        failures++;
                        nextRecovery = now.AddSeconds(15 * Math.Pow(2, failures - 1));
                        Phase = ConnectionPhase.Recovering; Status = "Восстанавливаю соединение"; Notify();
                        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(backgroundOperation.Token);
                        recovery.CancelAfter(TimeSpan.FromSeconds(30));
                        if (!tg.IsRunning) await tg.StartAsync(recovery.Token);
                        if (!zapret.IsRunning && settings.LastStrategy != null) await zapret.StartAsync(settings.LastStrategy, recovery.Token);
                        SetConnectionStatus();
                    }
                    else
                    {
                        stableSince ??= now;
                        if (now - stableSince.Value >= TimeSpan.FromMinutes(5)) failures = 0;
                        if (now >= nextProbe)
                        {
                            nextProbe = now.AddMinutes(5);
                            Results = await probes.CheckLightAsync(Results, backgroundOperation.Token);
                            SetConnectionStatus();
                        }
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (OperationCanceledException) when (backgroundOperation?.IsCancellationRequested == true) { }
                catch (Exception ex)
                {
                    Phase = ConnectionPhase.Partial; Status = "Не удалось восстановить связь";
                    Detail = failures >= 3 ? "Отключите и включите соединение, чтобы повторить." : "Повторю попытку автоматически.";
                    Log.Write(ex.Message); Notify();
                }
                finally
                {
                    backgroundOperation?.Dispose(); backgroundOperation = null;
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public static bool TransportHealthy(IReadOnlyList<ProbeResult> results) => new[] { "Telegram", "Discord · сайт/API", "Discord · соединение", "YouTube · сайт", "YouTube · видео" }.All(name => results.Any(x => x.Service == name && x.Success == true));
    public static StrategyResult Score(string id, IReadOnlyList<ProbeResult> results)
    {
        var scored = results.Where(x => x.Service != "Discord · голос" && x.Service != "Интернет" && x.Service != "Telegram").ToArray();
        double[] times = scored.Where(x => x.Success == true).Select(x => x.Milliseconds).Order().ToArray();
        return new(id, scored.Count(x => x.Success == true), scored.Length, times.Length == 0 ? double.MaxValue : times[times.Length / 2], DateTimeOffset.UtcNow);
    }
    private string NetworkKey()
    {
        string material = string.Join("|", NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback).OrderBy(x => x.Id).Select(x => x.Id + ":" + string.Join(",", x.GetIPProperties().GatewayAddresses.Select(y => y.Address.ToString())) + ":" + string.Join(",", x.GetIPProperties().DnsAddresses.Select(y => y.ToString()))));
        material += "|probes-v2|" + zapret.Version + "|" + tg.Version;
        try
        {
            var active = AppPaths.ReadJson<InstalledVersion>(AppPaths.ZapretActiveFile);
            if (active != null)
            {
                foreach (string file in Directory.EnumerateFiles(Path.Combine(active.Directory, "lists"), "*.txt").Order())
                    material += "|" + Path.GetFileName(file) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                material += "|GameFilter=" + File.Exists(Path.Combine(active.Directory, "utils", "game_filter.enabled"));
            }
        }
        catch (IOException) { material += "|lists-unavailable"; }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
    private void Save() => AppPaths.WriteJson(AppPaths.SettingsFile, settings);
    private void Notify() => Changed?.Invoke();
    private static IReadOnlyList<ProbeResult> EmptyResults() => new[] { new ProbeResult("Telegram", "Не проверен", null), new ProbeResult("Discord · сайт/API", "Не проверен", null), new ProbeResult("Discord · соединение", "Не проверен", null), new ProbeResult("Discord · голос", "Не проверен", null, 0, "Требуется реальный звонок"), new ProbeResult("YouTube · сайт", "Не проверен", null), new ProbeResult("YouTube · видео", "Не проверено", null) };

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; desiredConnected = false;
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        if (tg is TgService telegram) telegram.StateChanged -= ComponentChanged;
        if (zapret is ZapretService engine) engine.StateChanged -= ComponentChanged;
        lifetime.Cancel(); operation?.Cancel();
        await gate.WaitAsync();
        try
        {
            await StopComponentsAsync();
            try { await zapret.DisposeAsync(); } catch (Exception ex) { Log.Write(ex.Message); }
            try { await tg.DisposeAsync(); } catch (Exception ex) { Log.Write(ex.Message); }
            if (updateCheck != null) await updateCheck;
            probes.Dispose(); updates.Dispose();
        }
        finally { gate.Release(); }
        if (monitor != null) await monitor;
        if (startupConnection != null) await startupConnection;
        lifetime.Dispose();
    }
}
