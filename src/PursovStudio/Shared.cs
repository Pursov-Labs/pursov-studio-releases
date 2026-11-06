using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

public static class AppPaths
{
    public static string AppRoot => AppContext.BaseDirectory;
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PURSOV STUDIO");
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PURSOV STUDIO");
    public static string SharedRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PURSOV STUDIO");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string TgConfigFile => Path.Combine(DataRoot, "tg-config.json");
    public static string StagingRoot => Path.Combine(DataRoot, "staging");
    public static string ZapretRoot => Path.Combine(SharedRoot, "zapret");
    public static string ZapretActiveFile => Path.Combine(SharedRoot, "zapret-active.json");
    public static string TgRoot => Path.Combine(DataRoot, "tg");
    public static string PythonExe => Path.Combine(AppRoot, "runtime", "python", "python.exe");
    public static string TgRunner => Path.Combine(AppRoot, "runtime", "tg_runner.py");
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static T? ReadJson<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default;
}

public sealed class AppSettings
{
    public int Schema { get; set; } = 2;
    public bool AutoStart { get; set; } = true;
    public bool AutoConnect { get; set; } = true;
    public bool SetupComplete { get; set; }
    public string? LastStrategy { get; set; }
    public string? NetworkKey { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public bool UpdatesPending { get; set; }
    public string? RejectedZapretVersion { get; set; }
    public string? RejectedTgVersion { get; set; }
    public List<StrategyResult> LastResults { get; set; } = new();
}

public sealed record ProbeResult(string Service, string Status, bool? Success, double Milliseconds = 0, string Detail = "");
public sealed record StrategyInfo(string Id, string Name);
public sealed record StrategyResult(string Id, int Passed, int Checked, double Milliseconds, DateTimeOffset Time);
public sealed record ConflictInfo(string Name, string Detail, bool BlocksStart);
public sealed record InstalledVersion(string Version, string Directory);
public sealed record UpdateCandidate(string Component, string Version, string AssetName, string DownloadUrl, string? Digest, long Size, string Commit = "");

public enum ConnectionPhase { Off, Starting, Checking, Connected, Partial, Recovering, Stopping, Error }
public sealed record ConnectionSnapshot(ConnectionPhase Phase, bool Requested, bool ZapretRunning, bool TelegramRunning, DateTimeOffset? LastChecked);

public interface IZapretService : IAsyncDisposable
{
    string Version { get; }
    bool IsRunning { get; }
    IReadOnlyList<StrategyInfo> GetStrategies();
    Task StartAsync(string strategyId, CancellationToken token);
    Task StopAsync();
    Task InstallUpdateAsync(string transactionId, string version, CancellationToken token);
    Task ActivateVersionAsync(string version, CancellationToken token);
}

public interface ITgService : IAsyncDisposable
{
    string Version { get; }
    bool IsRunning { get; }
    int Port { get; }
    bool HasConfiguration { get; }
    Task<IReadOnlyList<string>> DiscoverAndImportAsync(CancellationToken token);
    Task StartAsync(CancellationToken token);
    Task StopAsync();
    Task<ProbeResult> ProbeAsync(CancellationToken token);
    Task<string> GetProxyUriAsync(CancellationToken token);
    Task ActivateVersionAsync(string version, CancellationToken token);
}

public static class Log
{
    private static readonly object Gate = new();
    internal static string? SelfTestFile { get; set; }
    public static event Action<string>? Written;
    public static void Write(string message)
    {
        string safe = System.Text.RegularExpressions.Regex.Replace(message, @"(?i)(secret|password|token)([=\""\s:]+)[^\s&,\""}]+", "$1$2[скрыто]");
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"tg://proxy\?\S+", "[ссылка Telegram скрыта]");
        string line = DateTimeOffset.Now.ToString("HH:mm:ss") + "  " + safe;
        lock (Gate)
        {
            try
            {
                string path = SelfTestFile ?? Path.Combine(AppPaths.DataRoot, "application.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Written?.Invoke(line);
    }
}
