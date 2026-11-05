using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PursovStudio;

AppPaths.ProjectRoot = Path.GetFullPath(args[0]);
AppPaths.TestRoot = Path.Combine(Path.GetTempPath(), "pursov-update-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(AppPaths.TestRoot);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
CancellationToken token = timeout.Token;
TgService? tg = null;
try
{
    // All network requests below contain only official public release metadata/source IDs.
    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("PursovStudio-IntegrationTest/1.0");
    async Task<UpdateCandidate> Candidate(string version)
    {
        string metadata = await http.GetStringAsync("https://api.github.com/repos/Flowseal/tg-ws-proxy/commits/v" + version, token);
        using var doc = JsonDocument.Parse(metadata);
        string commit = doc.RootElement.GetProperty("sha").GetString()!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(commit, "^[0-9a-f]{40}$")) throw new Exception("Invalid official commit");
        return new("tg", version, "source.zip", "https://api.github.com/repos/Flowseal/tg-ws-proxy/zipball/" + commit, null, 0, commit);
    }

    using var updater = new UpdateService();
    UpdateCandidate previous = await Candidate("1.11.3");
    UpdateCandidate current = await Candidate("1.11.4");
    foreach (UpdateCandidate candidate in new[] { previous, current })
    {
        await updater.StageAsync(candidate, _ => { }, token);
        if (!File.Exists(Path.Combine(AppPaths.TgRoot, candidate.Version, "proxy", "tg_ws_proxy.py"))) throw new Exception("Staged source absent");
        Console.WriteLine("PASS: production UpdateService fetched official " + candidate.Version + " ZIP and verified complete Git blob tree");
    }

    try
    {
        await updater.StageAsync(current with { Digest = "sha256:" + new string('0', 64) }, _ => { }, token);
        throw new Exception("Corrupt digest accepted");
    }
    catch (IOException ex) when (ex.Message.Contains("Контрольная сумма"))
    { Console.WriteLine("PASS: production updater rejects incorrect download SHA256 before activation"); }

    foreach (string[] names in new[] { new[] { "../../escape.py" }, new[] { "proxy/a.py", "proxy/a.py" } })
    {
        string zip = Path.Combine(AppPaths.TestRoot, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (string name in names) using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("test");
        try { UpdateService.ExtractZip(zip, zip + ".out", false); throw new Exception("Malformed archive accepted"); }
        catch (IOException) { }
    }
    Console.WriteLine("PASS: production ZIP extractor rejects traversal and duplicate entries");

    int port;
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    var config = new JsonObject { ["host"] = "127.0.0.1", ["port"] = port, ["secret"] = secret, ["dc_ip"] = new JsonArray("2:149.154.167.220", "4:149.154.167.220"), ["cfproxy"] = true, ["pool_size"] = 0 };
    typeof(TgService).GetMethod("SaveProtectedConfiguration", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { config });
    byte[] originalConfig = File.ReadAllBytes(AppPaths.TgConfigFile);
    AppPaths.WriteJson(Path.Combine(AppPaths.DataRoot, "tg-active.json"), new InstalledVersion(previous.Version, Path.Combine(AppPaths.TgRoot, previous.Version)));
    tg = new TgService();
    string originalUri = await tg.GetProxyUriAsync(token);

    async Task AssertLive(string label)
    {
        await tg.StartAsync(token);
        ProbeResult result = await tg.ProbeAsync(token);
        if (result.Success != true) throw new Exception(label + ": no verified MTProto roundtrip");
        Console.WriteLine("PASS: " + label + ", real MTProto exchange in " + result.Milliseconds + " ms");
        await tg.StopAsync();
        if (tg.IsRunning) throw new Exception("Owned process still running");
    }

    await AssertLive("previous official core " + previous.Version);
    await tg.ActivateVersionAsync(current.Version, token);
    await AssertLive("production activation of official update " + current.Version);

    // Fault is deliberately injected only into a disposable candidate; previous source stays intact.
    const string brokenVersion = "9.9.9";
    string brokenProxy = Path.Combine(AppPaths.TgRoot, brokenVersion, "proxy");
    Directory.CreateDirectory(brokenProxy);
    File.WriteAllText(Path.Combine(brokenProxy, "__init__.py"), "");
    File.WriteAllText(Path.Combine(brokenProxy, "config.py"), "");
    File.WriteAllText(Path.Combine(brokenProxy, "tg_ws_proxy.py"), "import pursov_deliberately_missing_dependency\n");
    await tg.ActivateVersionAsync(brokenVersion, token);
    bool rejected = false;
    try { await tg.StartAsync(token); }
    catch (InvalidOperationException) { rejected = true; }
    if (!rejected) throw new Exception("Broken worker unexpectedly started");
    await tg.StopAsync();
    await tg.ActivateVersionAsync(current.Version, token);
    await AssertLive("real rollback after candidate ImportError");

    if (tg.Port != port || await tg.GetProxyUriAsync(token) != originalUri || !File.ReadAllBytes(AppPaths.TgConfigFile).SequenceEqual(originalConfig)) throw new Exception("Rollback changed protected local configuration");
    Console.WriteLine("PASS: configuration bytes, local port and secret remain unchanged; owned workers stopped");
    Console.WriteLine("NOTE: CoreController catch/journal orchestration is not covered by this service-level integration test.");
}
finally
{
    if (tg != null) await tg.DisposeAsync();
    string root = Path.GetFullPath(AppPaths.TestRoot);
    string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("pursov-update-test-", StringComparison.Ordinal)) throw new Exception("Unsafe test cleanup path");
    Directory.Delete(root, true);
}
