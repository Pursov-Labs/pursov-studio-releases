using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

internal static class ControllerChecks
{
    internal static async Task RunAsync()
    {
        var failing = new Component { FailStop = true };
        var healthy = new Component();
        await using var controller = new CoreController(failing, healthy);
        if (await controller.StopComponentsAsync() || failing.StopCalls != 1 || healthy.StopCalls != 1 || healthy.IsRunning)
            throw new Exception("A failed first stop must not prevent the second component from stopping.");
        await controller.ToggleAsync();
        if (controller.DesiredConnected || failing.StopCalls != 2 || healthy.StopCalls != 2)
            throw new Exception("Manual disconnect must clear recovery intent even when cleanup fails.");
        failing.FailStop = false;
        await controller.ToggleAsync();
        if (controller.IsConnected || controller.Snapshot.Phase != ConnectionPhase.Off)
            throw new Exception("Successful retry must report disconnected state.");

        await using var queued = new CoreController(new Component(), new Component());
        var gate = (SemaphoreSlim)typeof(CoreController).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queued)!;
        await gate.WaitAsync();
        try
        {
            Task checking = queued.CheckAsync();
            queued.Cancel();
            await checking.WaitAsync(TimeSpan.FromSeconds(2));
            if (queued.IsBusy || queued.LastChecked != null || queued.Snapshot.Phase != ConnectionPhase.Connected)
                throw new Exception("A queued canceled check must release its busy state without running probes.");
        }
        finally { gate.Release(); }
    }

    internal static void CheckSettingsAndBundle()
    {
        string root = Path.Combine(Path.GetTempPath(), "pursov-core-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "settings.json");
            File.WriteAllText(settings, "{\"Schema\":1,\"SetupComplete\":true}");
            if (!CoreController.LoadSettings(settings).SetupComplete) throw new Exception("Legacy settings were lost.");
            foreach (string invalid in new[] { "null", "{\"Schema\":99}", "{\"LastResults\":null}", "{\"LastResults\":[null]}" })
            {
                File.WriteAllText(settings, invalid);
                try { CoreController.LoadSettings(settings); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Unsupported settings were accepted.");
            }
            Directory.CreateDirectory(Path.Combine(root, "payload", "tg", "1.9.0"));
            Directory.CreateDirectory(Path.Combine(root, "payload", "tg", "1.11.4"));
            Directory.CreateDirectory(Path.Combine(root, "payload", "tg", "untrusted-name"));
            var bundled = TgService.BundledVersion(root);
            if (bundled.Version != "1.11.4" || bundled.Directory != Path.Combine(root, "payload", "tg", "1.11.4"))
                throw new Exception("Startup must select the core shipped by the app release.");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Component : IZapretService, ITgService
    {
        public bool FailStop { get; set; }
        public int StopCalls { get; private set; }
        public string Version => "1.0.0";
        public bool IsRunning { get; private set; } = true;
        public int Port => 1443;
        public bool HasConfiguration => true;
        public IReadOnlyList<StrategyInfo> GetStrategies() => Array.Empty<StrategyInfo>();
        public Task StartAsync(string id, CancellationToken token) => StartAsync(token);
        public Task StartAsync(CancellationToken token) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync()
        {
            StopCalls++;
            if (FailStop) throw new IOException("Expected controller regression test failure");
            IsRunning = false;
            return Task.CompletedTask;
        }
        public Task InstallUpdateAsync(string id, string version, CancellationToken token) => throw new NotSupportedException();
        public Task ActivateVersionAsync(string version, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DiscoverAndImportAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<ProbeResult> ProbeAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<string> GetProxyUriAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
