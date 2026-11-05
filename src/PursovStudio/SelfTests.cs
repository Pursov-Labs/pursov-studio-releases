using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

public static class SelfTests
{
    public static async Task<bool> RunAsync(string[] args)
    {
        bool self = args.Contains("--self-test"), network = args.Contains("--network-check"), components = args.Contains("--component-smoke"), selection = args.Contains("--selection-smoke");
        if (!self && !network && !components && !selection) return false;
        int reportAt = Array.IndexOf(args, "--report");
        string path = reportAt >= 0 && reportAt + 1 < args.Length ? args[reportAt + 1] : Path.Combine(AppPaths.DataRoot, self ? "self-test.json" : "network-check.json");
        var tests = new List<object>();
        int failed = 0;
        void Test(string name, Action check)
        {
            try { check(); tests.Add(new { Name = name, Passed = true, Error = "" }); }
            catch (Exception ex) { failed++; tests.Add(new { Name = name, Passed = false, Error = ex.Message }); }
        }
        if (self)
        {
            Test("independent bounded cleanup and manual stop intent", () => Task.Run(ControllerChecks.RunAsync).GetAwaiter().GetResult());
            Test("legacy settings keep opt-out defaults", () =>
            {
                var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Schema\":1,\"SetupComplete\":true}")!;
                Assert(old.AutoStart && old.AutoConnect && old.SetupComplete);
                var disabled = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"AutoStart\":false,\"AutoConnect\":false}")!;
                Assert(!disabled.AutoStart && !disabled.AutoConnect);
            });
            Test("settings validation and bundled TG version", ControllerChecks.CheckSettingsAndBundle);
            Test("semantic versions", () => { Assert(UpdateService.IsNewer("v1.11.4", "1.9.9")); Assert(!UpdateService.IsNewer("1.9.0", "1.10.0")); Assert(!UpdateService.IsNewer("latest", "1.0.0")); });
            Test("YouTube JSON with braces/escaped quotes", () =>
            {
                const string json = "{\"text\":\"} \\\" {\",\"streamingData\":{\"formats\":[]}}";
                Assert(ProbeService.ExtractPlayerJson("var ytInitialPlayerResponse = " + json + "; trailing") == json);
                Assert(ProbeService.ExtractPlayerJson("ytInitialPlayerResponse = {\"broken\":1") == null);
            });
            Test("YouTube media source and payload validation", () => { ProbeService.VideoSelfTest(); });
            Test("unknown voice never counts as passed", () =>
            {
                var score = CoreController.Score("test", new[] { new ProbeResult("Telegram", "ok", true, 10), new ProbeResult("Discord · голос", "не проверен", null), new ProbeResult("YouTube · видео", "не проверено", null) });
                Assert(score.Passed == 0 && score.Checked == 1);
                Assert(!CoreController.TransportHealthy(new[] { new ProbeResult("Интернет", "ok", true) }));
            });
            Test("Zapret ranking excludes independent Telegram success", () =>
            {
                var web = new[] { "Discord · сайт/API", "Discord · соединение", "YouTube · сайт", "YouTube · видео" };
                var failedWeb = web.Select(name => new ProbeResult(name, "нет связи", false)).Append(new("Telegram", "ok", true, 1)).ToArray();
                var failedScore = CoreController.Score("failed", failedWeb);
                Assert(failedScore.Passed == 0 && failedScore.Checked == 4 && failedScore.Milliseconds == double.MaxValue);
                var workingWeb = web.Select((name, index) => new ProbeResult(name, "ok", true, (index + 1) * 10))
                    .Append(new("Telegram", "нет связи", false)).Append(new("Discord · голос", "не проверен", null)).ToArray();
                var workingScore = CoreController.Score("working", workingWeb);
                Assert(workingScore.Passed == 4 && workingScore.Checked == 4 && workingScore.Milliseconds == 30);
            });
            Test("healthy transport requires confirmed YouTube video", () =>
            {
                var basic = new[] { "Telegram", "Discord · сайт/API", "Discord · соединение", "YouTube · сайт" }
                    .Select(name => new ProbeResult(name, "ok", true)).Append(new("Discord · голос", "не проверен", null)).ToArray();
                Assert(CoreController.TransportHealthy(basic.Append(new ProbeResult("YouTube · видео", "ok", true)).ToArray()));
                Assert(!CoreController.TransportHealthy(basic));
                Assert(!CoreController.TransportHealthy(basic.Append(new ProbeResult("YouTube · видео", "не проверено", null)).ToArray()));
                Assert(!CoreController.TransportHealthy(basic.Append(new ProbeResult("YouTube · видео", "нет связи", false)).ToArray()));
            });
            Test("Git blob hash", () => Assert(UpdateService.GitBlobHash(Array.Empty<byte>()) == "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391"));
            Test("ZIP rejects traversal and Unix symlink", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "pursov-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                try
                {
                    foreach (bool symlink in new[] { false, true })
                    {
                        string zip = Path.Combine(dir, Guid.NewGuid() + ".zip");
                        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) { var e = z.CreateEntry(symlink ? "link" : "../escape.txt"); if (symlink) e.ExternalAttributes = unchecked((int)(0xA1FF << 16)); using var w = new StreamWriter(e.Open()); w.Write("x"); }
                        bool refused = false; try { UpdateService.ExtractZip(zip, Path.Combine(dir, Guid.NewGuid().ToString()), false); } catch (IOException) { refused = true; }
                        Assert(refused);
                    }
                }
                finally { Directory.Delete(dir, true); }
            });
            Test("all bundled Zapret strategies", () =>
            {
                string root = Path.Combine(AppPaths.AppRoot, "payload", "zapret", "1.10.4");
                if (!Directory.Exists(root)) root = Path.GetFullPath(Path.Combine(AppPaths.AppRoot, "..", "..", "..", "..", "..", "ZAPRET"));
                StrategyParser.SelfTest(root);
            });
            Test("Zapret worker trust boundary and process ownership", () => { ElevatedWorker.SelfTest(); });
        }
        if (network)
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await using ITgService tg = new TgService();
            using var probes = new ProbeService();
            try
            {
                await tg.DiscoverAndImportAsync(token.Token);
                var results = await probes.CheckAsync(tg, token.Token);
                AppPaths.WriteJson(path, new { Time = DateTimeOffset.UtcNow, Mode = "read-only network check; existing processes untouched", Results = results });
                Environment.ExitCode = 0; return true;
            }
            catch (Exception ex) { failed++; tests.Add(new { Name = "network-check", Passed = false, Error = ex.Message }); }
        }
        if (components)
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using ITgService tg = new TgService();
            await using IZapretService zapret = new ZapretService();
            using var probes = new ProbeService();
            IReadOnlyList<ProbeResult> results = Array.Empty<ProbeResult>();
            bool started = false;
            try
            {
                if (!InstallService.IsInstalled) throw new InvalidOperationException("Component smoke requires the installed protected application.");
                if (System.Diagnostics.Process.GetProcessesByName("winws").Length != 0) throw new InvalidOperationException("An external winws is running; smoke refused.");
                await tg.DiscoverAndImportAsync(token.Token);
                await tg.StartAsync(token.Token);
                await zapret.StartAsync(zapret.GetStrategies().First().Id, token.Token);
                started = tg.IsRunning && zapret.IsRunning;
                results = await probes.CheckAsync(tg, token.Token);
            }
            catch (Exception ex) { failed++; tests.Add(new { Name = "component-start", Passed = false, Error = ex.Message }); }
            finally { await zapret.StopAsync(); await tg.StopAsync(); }
            bool stopped = !zapret.IsRunning && !tg.IsRunning;
            AppPaths.WriteJson(path, new { Time = DateTimeOffset.UtcNow, Passed = failed == 0 && started && stopped, Started = started, Stopped = stopped, Tests = tests, Results = results });
            Environment.ExitCode = failed == 0 && started && stopped ? 0 : 1;
            return true;
        }
        if (selection)
        {
            await using var core = new CoreController();
            await core.InitializeAsync();
            await core.StartupConnection;
            if (!core.IsConnected) await core.ToggleAsync();
            bool passed = core.IsConnected && CoreController.TransportHealthy(core.Results);
            AppPaths.WriteJson(path, new { Time = DateTimeOffset.UtcNow, Passed = passed, core.Status, core.Detail, core.ProgressText, core.Strategies, core.Results });
            Environment.ExitCode = passed ? 0 : 1;
            return true;
        }
        AppPaths.WriteJson(path, new { Time = DateTimeOffset.UtcNow, Passed = failed == 0, Failed = failed, Tests = tests });
        Environment.ExitCode = failed == 0 ? 0 : 1;
        return true;
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
}
