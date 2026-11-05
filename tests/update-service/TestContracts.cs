using System.IO;
using System.Text.Json;

// Only paths/contracts are substituted. Production update, TG and copy code is linked verbatim.
namespace PursovStudio;

public static class AppPaths
{
    public static string ProjectRoot { get; set; } = "";
    public static string TestRoot { get; set; } = "";
    public static string AppRoot => ProjectRoot;
    public static string InstallRoot => Path.Combine(TestRoot, "install");
    public static string DataRoot => Path.Combine(TestRoot, "data");
    public static string SharedRoot => Path.Combine(TestRoot, "shared");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string TgConfigFile => Path.Combine(DataRoot, "tg-config.json");
    public static string StagingRoot => Path.Combine(DataRoot, "staging");
    public static string ZapretRoot => Path.Combine(SharedRoot, "zapret");
    public static string ZapretActiveFile => Path.Combine(SharedRoot, "zapret-active.json");
    public static string TgRoot => Path.Combine(DataRoot, "tg");
    public static string PythonExe => Path.Combine(ProjectRoot, "runtime", "python", "python.exe");
    public static string TgRunner => Path.Combine(ProjectRoot, "runtime", "tg_runner.py");
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static T? ReadJson<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default;
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record ProbeResult(string Service, string Status, bool? Success, double Milliseconds = 0, string Detail = "");
public sealed record StrategyInfo(string Id, string Name);
public sealed record InstalledVersion(string Version, string Directory);
public sealed record UpdateCandidate(string Component, string Version, string AssetName, string DownloadUrl, string? Digest, long Size, string Commit = "");
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
public static class Log { public static void Write(string message) => Console.WriteLine(message); }
