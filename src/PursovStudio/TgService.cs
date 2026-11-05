using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

public sealed class TgService : ITgService
{
    private Process? _process;
    private ProcessJob? _job;
    private JsonObject? _configuration;
    private InstalledVersion _active;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private static string ActiveFile => Path.Combine(AppPaths.DataRoot, "tg-active.json");
    public string Version => _active.Version;
    public event Action? StateChanged;
    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public int Port => Configuration?["port"]?.GetValue<int>() ?? 1443;
    public bool HasConfiguration => Configuration != null;
    private JsonObject? Configuration => _configuration ??= ReadProtectedConfiguration();

    public TgService()
    {
        // The installed release owns its core version; a legacy per-user pointer must not pin an older core.
        _active = BundledVersion(AppPaths.AppRoot);
    }

    internal static InstalledVersion BundledVersion(string appRoot)
    {
        string root = Path.Combine(appRoot, "payload", "tg");
        string version = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Select(Path.GetFileName)
                .Where(x => x != null && Regex.IsMatch(x, "\\A[0-9]+\\.[0-9]+\\.[0-9]+\\z"))
                .OrderByDescending(x => System.Version.Parse(x!)).FirstOrDefault() ?? "1.11.4"
            : "1.11.4";
        return new InstalledVersion(version, Path.Combine(root, version));
    }

    public Task<IReadOnlyList<string>> DiscoverAndImportAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var messages = new List<string>();
        if (HasConfiguration) return Task.FromResult<IReadOnlyList<string>>(messages);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TgWsProxy", "config.json"));
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppPaths.AppRoot, Environment.CurrentDirectory })
            Discover(root, 0, candidates, token);
        var configs = new List<(string Path, JsonObject Config, string Identity)>();
        foreach (string path in candidates.Where(File.Exists))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                JsonObject cfg = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException();
                Validate(cfg);
                string identity = string.Join("|", ConnectionKeys.Select(k => k + "=" + cfg[k]?.ToJsonString()));
                configs.Add((path, cfg, identity));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or FormatException)
            { messages.Add("Конфигурация TG пропущена: " + path + ". " + (ex is InvalidDataException ? ex.Message : "Формат несовместим.")); }
        }
        if (configs.Select(c => c.Identity).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            messages.Add("Найдены разные настройки TG. Автоматический импорт остановлен: " + string.Join("; ", configs.Select(c => c.Path)));
            return Task.FromResult<IReadOnlyList<string>>(messages);
        }
        if (configs.Count > 0)
        {
            _configuration = configs[0].Config;
            SaveProtectedConfiguration(_configuration);
            messages.Add("Настройки TG скопированы из " + configs[0].Path + ". Исходный файл сохранён.");
        }
        else if (messages.Count == 0)
        {
            _configuration = new JsonObject { ["host"] = "127.0.0.1", ["port"] = 1443, ["secret"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), ["dc_ip"] = new JsonArray("2:149.154.167.220", "4:149.154.167.220"), ["cfproxy"] = true };
            SaveProtectedConfiguration(_configuration);
            messages.Add("Созданы локальные настройки TG. Подключите Telegram по кнопке в приложении один раз.");
        }
        return Task.FromResult<IReadOnlyList<string>>(messages);
    }

    private static readonly string[] ConnectionKeys = { "host", "port", "secret", "dc_ip", "cfproxy", "h2", "cfproxy_user_domain", "cfproxy_user_domain_enabled", "cfproxy_worker_domain", "cfproxy_worker_enabled", "no_secure", "force_test_dc", "fake_tls_domain", "pool_size" };

    private static void Discover(string root, int depth, HashSet<string> paths, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 3 || !Directory.Exists(root)) return;
        paths.Add(Path.Combine(root, "TgWsProxy_data", "config.json"));
        if (Path.GetFileName(root).Contains("TgWsProxy", StringComparison.OrdinalIgnoreCase)) paths.Add(Path.Combine(root, "config.json"));
        if (depth == 3) return;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                string name = Path.GetFileName(directory);
                if (name.StartsWith('.') || name is "node_modules" or "backups" or "runtime" or "payload") continue;
                Discover(directory, depth + 1, paths, token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void Validate(JsonObject cfg)
    {
        string host = cfg["host"]?.GetValue<string>() ?? "127.0.0.1";
        if (!IPAddress.TryParse(host, out IPAddress? address) || !IPAddress.IsLoopback(address)) throw new InvalidDataException("TG требует локальный host 127.0.0.1 или ::1.");
        int port = cfg["port"]?.GetValue<int>() ?? 1443;
        if (port is < 1024 or > 65535) throw new InvalidDataException("Порт TG вне диапазона 1024–65535.");
        if (!Regex.IsMatch(cfg["secret"]?.GetValue<string>() ?? "", "\\A[0-9a-fA-F]{32}\\z")) throw new InvalidDataException("Ключ TG имеет несовместимый формат.");
        if (cfg["proxy_protocol"]?.GetValue<bool>() == true) throw new InvalidDataException("Режим HAProxy не поддерживается локальным приложением.");
        if (cfg["force_test_dc"]?.GetValue<bool>() == true) throw new InvalidDataException("Тестовые DC Telegram не подходят для обычного аккаунта.");
        cfg["host"] = host;
        cfg["port"] = port;
    }

    public async Task StartAsync(CancellationToken token)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            if (IsRunning) return;
            if (Configuration is not { } cfg) throw new InvalidOperationException("Сначала импортируйте настройки TG.");
            Validate(cfg);
            EnsureBundledSource();
            using (var socket = new Socket(IPAddress.Parse(cfg["host"]!.GetValue<string>()).AddressFamily, SocketType.Stream, ProtocolType.Tcp))
            {
                try { socket.Bind(new IPEndPoint(IPAddress.Parse(cfg["host"]!.GetValue<string>()), Port)); }
                catch (SocketException) { throw new InvalidOperationException($"Порт TG {Port} занят другим приложением. Закройте прежний TG WS Proxy самостоятельно; приложение его не останавливает."); }
            }
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = MakePythonProcess(AppPaths.TgRunner);
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                try
                {
                    var item = JsonNode.Parse(e.Data);
                    string? kind = item?["event"]?.GetValue<string>();
                    if (kind == "ready") ready.TrySetResult();
                    else if (kind == "error") ready.TrySetException(new InvalidOperationException(item?["detail"]?.GetValue<string>() ?? "Ошибка запуска TG."));
                }
                catch (JsonException) { }
            };
            process.ErrorDataReceived += (_, _) => { };
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                ready.TrySetException(new InvalidOperationException("TG worker завершился до готовности."));
                StateChanged?.Invoke();
            };
            if (!process.Start()) throw new IOException("Не удалось запустить TG worker.");
            _process = process;
            _job?.Dispose();
            _job = new ProcessJob(process);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { source = _active.Directory, config = cfg }));
            await process.StandardInput.FlushAsync(token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(25), token);
            StateChanged?.Invoke();
            Log.Write("TG: локальный MTProxy запущен, порт " + Port);
        }
        catch
        {
            await StopCoreAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private static Process MakePythonProcess(string script)
    {
        if (!File.Exists(AppPaths.PythonExe) || !File.Exists(script)) throw new FileNotFoundException("В поставке отсутствует Python runtime TG. Пересоберите приложение.");
        var start = new ProcessStartInfo(AppPaths.PythonExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppPaths.AppRoot };
        start.ArgumentList.Add("-B"); start.ArgumentList.Add("-u"); start.ArgumentList.Add(script);
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return new Process { StartInfo = start };
    }

    private void EnsureBundledSource()
    {
        EnsureSource(_active);
        AppPaths.WriteJson(ActiveFile, _active);
    }

    private static void EnsureSource(InstalledVersion active)
    {
        if (File.Exists(Path.Combine(active.Directory, "proxy", "tg_ws_proxy.py"))) return;
        string payload = Path.Combine(AppPaths.AppRoot, "payload", "tg", active.Version);
        if (!Directory.Exists(payload)) throw new DirectoryNotFoundException("В поставке отсутствует ядро TG " + active.Version);
        if (Path.GetFullPath(active.Directory).Equals(Path.GetFullPath(payload), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Встроенное ядро Telegram повреждено. Переустановите PURSOV STUDIO.");
        string stage = active.Directory + "." + Guid.NewGuid().ToString("N") + ".staging";
        Directory.CreateDirectory(stage);
        try
        {
        foreach (string file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(stage, Path.GetRelativePath(payload, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, false);
        }
            if (Directory.Exists(active.Directory)) throw new IOException("Каталог TG существует, но ядро повреждено. Восстановите поставку.");
            Directory.Move(stage, active.Directory);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Process? process = _process;
        _process = null;
        try
        {
            if (process != null && !process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("{\"command\":\"stop\"}").WaitAsync(TimeSpan.FromSeconds(2));
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        { Log.Write("TG завершён через ограничитель процессов: " + ex.GetType().Name); }
        finally
        {
            _job?.Dispose(); _job = null;
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) when (process?.HasExited == true) { }
            finally { process?.Dispose(); }
            StateChanged?.Invoke();
        }
    }

    public async Task<ProbeResult> ProbeAsync(CancellationToken token)
    {
        if (Configuration is not { } cfg) return new ProbeResult("Telegram", "Не настроен", null, 0, "Настройки MTProxy ещё не импортированы.");
        using var process = MakePythonProcess(Path.Combine(Path.GetDirectoryName(AppPaths.TgRunner)!, "tg_probe.py"));
        bool external = !IsRunning;
        process.Start();
        using var job = new ProcessJob(process);
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.StandardInput.WriteLineAsync(cfg.ToJsonString());
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(12), token);
            string result = await output;
            await error;
            JsonNode parsed = JsonNode.Parse(result) ?? throw new JsonException();
            bool? success = parsed["success"]?.GetValue<bool>();
            string detail = parsed["detail"]?.GetValue<string>() ?? "MTProto тест завершён без результата.";
            if (external) detail = "Существующий локальный endpoint; " + detail;
            return new ProbeResult("Telegram", success == true ? "Доступен" : success == false ? "Не отвечает" : "Не проверен", success, parsed["milliseconds"]?.GetValue<double>() ?? 0, detail);
        }
        catch (Exception ex) when (ex is TimeoutException or JsonException or IOException)
        { return new ProbeResult("Telegram", "Не отвечает", false, 0, "MTProto проверка не завершилась: " + ex.GetType().Name); }
        finally
        {
            job.Dispose();
            if (!process.HasExited) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    public Task<string> GetProxyUriAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cfg = Configuration ?? throw new InvalidOperationException("Настройки TG отсутствуют.");
        if (!string.IsNullOrEmpty(cfg["fake_tls_domain"]?.GetValue<string>())) throw new InvalidOperationException("Fake TLS ссылка требует отдельной настройки Telegram.");
        return Task.FromResult("tg://proxy?server=" + Uri.EscapeDataString(cfg["host"]!.GetValue<string>()) + "&port=" + Port + "&secret=dd" + cfg["secret"]!.GetValue<string>());
    }

    public async Task ActivateVersionAsync(string version, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            if (IsRunning) throw new InvalidOperationException("Сначала остановите управляемый TG worker.");
            if (!Regex.IsMatch(version, "\\A[0-9]+\\.[0-9]+\\.[0-9]+\\z")) throw new InvalidDataException("Некорректная версия TG.");
            string directory = Path.Combine(AppPaths.TgRoot, version);
            EnsureSource(new InstalledVersion(version, directory));
            if (!File.Exists(Path.Combine(directory, "proxy", "tg_ws_proxy.py")) || !File.Exists(Path.Combine(directory, "proxy", "config.py"))) throw new InvalidDataException("Обновление TG не содержит требуемое ядро.");
            _active = new InstalledVersion(version, directory);
            AppPaths.WriteJson(ActiveFile, _active);
        }
        finally { _lifecycle.Release(); }
    }

    private static JsonObject? ReadProtectedConfiguration()
    {
        if (!File.Exists(AppPaths.TgConfigFile)) return null;
        var envelope = JsonNode.Parse(File.ReadAllText(AppPaths.TgConfigFile));
        string payload = envelope?["protectedData"]?.GetValue<string>() ?? throw new InvalidDataException("Ожидался зашифрованный файл настроек TG.");
        byte[] data = Protect(Convert.FromBase64String(payload), false);
        try
        {
            JsonObject cfg = JsonNode.Parse(data)?.AsObject() ?? throw new InvalidDataException("Некорректные настройки TG.");
            Validate(cfg);
            return cfg;
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static void SaveProtectedConfiguration(JsonObject config)
    {
        byte[] plain = Encoding.UTF8.GetBytes(config.ToJsonString());
        try { AppPaths.WriteJson(AppPaths.TgConfigFile, new { schema = 1, protection = "Windows DPAPI CurrentUser", protectedData = Convert.ToBase64String(Protect(plain, true)) }); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0); Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }

    public async ValueTask DisposeAsync() { await StopAsync(); _lifecycle.Dispose(); }

    private sealed class ProcessJob : IDisposable
    {
        private IntPtr _handle;
        public ProcessJob(Process process)
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000;
            int size = Marshal.SizeOf<ExtendedLimits>(); IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, pointer, false);
                if (_handle == IntPtr.Zero || !SetInformationJobObject(_handle, 9, pointer, (uint)size) || !AssignProcessToJobObject(_handle, process.Handle))
                { process.Kill(true); throw new IOException("Не удалось привязать TG worker к процессу приложения."); }
            }
            catch { Dispose(); throw; }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        public void Dispose() { if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr data, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
