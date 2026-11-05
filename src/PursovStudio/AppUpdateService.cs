using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

public sealed class AppUpdateService : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdateCheckState state = new();
    private string? completionMessage;
    private string status = "Обновления ещё не проверялись";
    private Process? restartWaiter;
    private static string StatePath => Path.Combine(AppPaths.DataRoot, "app-update-state.json");
    public static string StagingRoot => Path.Combine(AppPaths.StagingRoot, "app");
    public event Action? Changed;
    public string Status
    {
        get => completionMessage != null && status != completionMessage ? completionMessage + ". " + status : status;
        private set => status = value;
    }
    public bool IsBusy { get; private set; }
    public bool Ready => TransactionId != null;
    public string? TransactionId { get; private set; }
    public string? AvailableVersion { get; private set; }

    public AppUpdateService()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PursovStudio/" + ReleaseTrust.CurrentVersion);
        try { state = AppPaths.ReadJson<UpdateCheckState>(StatePath) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Log.Write("Состояние обновлений будет проверено заново."); }
        ReadInstallResult();
    }

    public async Task CheckAndDownloadAsync(CancellationToken token, bool force = false)
    {
        if (!await gate.WaitAsync(0, token)) return;
        try
        {
            if (force) completionMessage = null;
            IsBusy = true;
            if (state.TransactionId != null && TransactionId == null) await RestoreDownloadAsync(token);
            if (!force && state.NextCheck > DateTimeOffset.UtcNow)
            {
                if (completionMessage != null) Status = completionMessage;
                return;
            }
            Status = "Проверяю обновления…"; Notify();
            using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            metadataTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            string feed = "https://github.com/" + ReleaseTrust.Repository + "/releases/latest/download/";
            byte[] manifestBytes = await ReadLimitedAsync(new Uri(feed + "release.json"), 16384, metadataTimeout.Token);
            byte[] signature = await ReadLimitedAsync(new Uri(feed + "release.sig"), 64, metadataTimeout.Token);
            var release = ReleaseTrust.VerifyManifest(manifestBytes, signature, false);
            if (Version.Parse(release.Version) <= ReleaseTrust.CurrentVersion)
            {
                Status = "Установлена последняя версия";
                TransactionId = state.TransactionId = null; AvailableVersion = null;
            }
            else if (AvailableVersion != release.Version || !Ready)
            {
                Status = "Загружаю обновление " + release.Version + "…"; Notify();
                string id = Guid.NewGuid().ToString("N");
                string directory = ReleaseTrust.TransactionDirectory(StagingRoot, id);
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(Path.Combine(directory, "release.json"), manifestBytes, token);
                await File.WriteAllBytesAsync(Path.Combine(directory, "release.sig"), signature, token);
                using var packageTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                packageTimeout.CancelAfter(TimeSpan.FromMinutes(15));
                string package = Path.Combine(directory, "package.msi");
                var download = new Uri("https://github.com/" + ReleaseTrust.Repository + "/releases/download/v" + release.Version + "/" + release.AssetName);
                await DownloadPackageAsync(download, package, release.Size, packageTimeout.Token);
                await ReleaseTrust.VerifyPackageAsync(package, release, token);
                TransactionId = state.TransactionId = id;
                AvailableVersion = release.Version;
            }
            if (Ready) Status = "Обновление " + AvailableVersion + " готово";
            state.Failures = 0;
            state.NextCheck = DateTimeOffset.UtcNow.AddDays(1);
            Save();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Status = Ready ? "Обновление готово" : "Проверка обновлений отменена";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or CryptographicException or JsonException or OperationCanceledException or UnauthorizedAccessException)
        {
            state.Failures = Math.Min(state.Failures + 1, 8);
            state.NextCheck = DateTimeOffset.UtcNow.AddMinutes(Math.Min(1440, 15 * Math.Pow(2, state.Failures - 1)));
            Status = Ready ? "Обновление готово. Сервер сейчас недоступен" : "Обновления пока недоступны. Попробую позже";
            Log.Write("Проверка обновлений: " + ex.Message);
            Save();
        }
        finally { IsBusy = false; gate.Release(); Notify(); }
    }

    private void ReadInstallResult()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, "--update-result");
        if (index < 0 || index + 1 >= arguments.Length) return;
        try
        {
            string directory = ReleaseTrust.TransactionDirectory(Path.Combine(AppPaths.SharedRoot, "app-updates"), arguments[index + 1]);
            string path = Path.Combine(directory, "result.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) throw new InvalidDataException("Нет результата установки.");
            var result = AppPaths.ReadJson<WorkerInstallResult>(path) ?? throw new InvalidDataException("Нет результата установки.");
            completionMessage = result.State switch
            {
                "complete" when result.ExitCode == 0 => "Обновление установлено",
                "reboot-required" when result.ExitCode is 3010 or 1641 => "Для завершения обновления перезагрузите Windows",
                "failed" => "Обновление не установлено. Подробности — в журнале",
                _ => throw new InvalidDataException("Некорректный результат установки.")
            };
            Status = completionMessage;
            state.NextCheck = result.State == "failed" ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddDays(1);
            state.TransactionId = null;
            Log.Write(completionMessage + " (код " + result.ExitCode + ")");
            Save();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Log.Write("Не удалось прочитать результат обновления: " + ex.Message); }
    }

    private async Task RestoreDownloadAsync(CancellationToken token)
    {
        try
        {
            string directory = ReleaseTrust.TransactionDirectory(StagingRoot, state.TransactionId!);
            var manifest = new FileInfo(Path.Combine(directory, "release.json"));
            var signature = new FileInfo(Path.Combine(directory, "release.sig"));
            if (!manifest.Exists || manifest.Length > 16384 || !signature.Exists || signature.Length != 64)
                throw new InvalidDataException("Сведения о скачанном обновлении повреждены.");
            var release = ReleaseTrust.VerifyManifest(await File.ReadAllBytesAsync(manifest.FullName, token), await File.ReadAllBytesAsync(signature.FullName, token));
            await ReleaseTrust.VerifyPackageAsync(Path.Combine(directory, "package.msi"), release, token);
            TransactionId = state.TransactionId;
            AvailableVersion = release.Version;
            Status = "Обновление " + release.Version + " готово";
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        {
            state.TransactionId = null; state.NextCheck = DateTimeOffset.MinValue;
            Log.Write("Скачанное обновление будет проверено заново: " + ex.Message);
        }
    }

    public async Task PrepareRestartWaiterAsync(CancellationToken token)
    {
        if (TransactionId == null) throw new InvalidOperationException("Сначала загрузите обновление.");
        completionMessage = null;
        string directory = ReleaseTrust.TransactionDirectory(StagingRoot, TransactionId);
        string source = Path.Combine(AppPaths.AppRoot, "UpdateWorker.exe");
        string destination = Path.Combine(directory, "UpdateWorker.exe");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, token);
    }

    public void LaunchRestartWaiter(bool resume)
    {
        if (TransactionId == null) throw new InvalidOperationException("Сначала загрузите обновление.");
        string directory = ReleaseTrust.TransactionDirectory(StagingRoot, TransactionId);
        using var parent = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(Path.Combine(directory, "UpdateWorker.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        start.ArgumentList.Add("--wait"); start.ArgumentList.Add(TransactionId);
        start.ArgumentList.Add("--parent"); start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--ticks"); start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (resume) start.ArgumentList.Add("--resume");
        restartWaiter = Process.Start(start) ?? throw new IOException("Не удалось подготовить перезапуск приложения.");
    }

    public void CancelRestartWaiter()
    {
        if (restartWaiter == null) return;
        try { if (!restartWaiter.HasExited) restartWaiter.Kill(); }
        catch (InvalidOperationException) { }
        finally { restartWaiter.Dispose(); restartWaiter = null; }
    }

    public void InvalidateTransaction()
    {
        TransactionId = state.TransactionId = null;
        AvailableVersion = null;
        state.NextCheck = DateTimeOffset.MinValue;
        Status = "Установка не началась. Проверьте обновления ещё раз";
        Save(); Notify();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            if (!AllowedDownloadUri(uri)) throw new IOException("Сервер обновлений перенаправил загрузку на неизвестный адрес.");
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location != null)
            {
                uri = new Uri(uri, response.Headers.Location); response.Dispose(); continue;
            }
            if (!response.IsSuccessStatusCode) { var code = response.StatusCode; response.Dispose(); throw new HttpRequestException("Сервер обновлений ответил " + (int)code + "."); }
            return response;
        }
        throw new IOException("Слишком много перенаправлений при загрузке обновления.");
    }

    internal static bool AllowedDownloadUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
        uri.UserInfo.Length == 0 && uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private async Task<byte[]> ReadLimitedAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await GetAsync(uri, token);
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Сведения об обновлении слишком велики.");
        using var output = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Сведения об обновлении слишком велики.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task DownloadPackageAsync(Uri uri, string path, long expectedSize, CancellationToken token)
    {
        using var response = await GetAsync(uri, token);
        if (response.Content.Headers.ContentLength is long length && length != expectedSize)
            throw new InvalidDataException("Размер обновления отличается от объявленного.");
        string partial = path + ".partial";
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            {
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    if (output.Length + count > expectedSize) throw new InvalidDataException("Размер обновления превышен.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (output.Length != expectedSize) throw new InvalidDataException("Загрузка обновления прервалась.");
            }
            File.Move(partial, path);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private void Save()
    {
        try { AppPaths.WriteJson(StatePath, state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Не удалось сохранить состояние обновлений: " + ex.Message); }
    }
    private void Notify() => Changed?.Invoke();
    public void Dispose() { http.Dispose(); restartWaiter?.Dispose(); }

    public sealed class UpdateCheckState
    {
        public DateTimeOffset NextCheck { get; set; }
        public int Failures { get; set; }
        public string? TransactionId { get; set; }
    }

    public sealed class WorkerInstallResult
    {
        public string State { get; set; } = "";
        public int ExitCode { get; set; }
    }
}
