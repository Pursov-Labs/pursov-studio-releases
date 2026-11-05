using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PursovStudio;

public static class BrokerService
{
    private static readonly Native.ServiceMain ServiceMainCallback = ServiceMain;
    private static readonly Native.ControlHandler ControlCallback = Control;
    private static readonly CancellationTokenSource Lifetime = new();
    private static IntPtr statusHandle;

    public static bool TryRun(string[] args)
    {
        if (args.Length != 1 || args[0] != "--service") return false;
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem) { Environment.ExitCode = 5; return true; }
        var table = new[]
        {
            new Native.ServiceTable { Name = BrokerProtocol.ServiceName, Main = ServiceMainCallback },
            new Native.ServiceTable()
        };
        if (!Native.StartServiceCtrlDispatcher(table)) Environment.ExitCode = Marshal.GetLastWin32Error();
        return true;
    }

    private static void ServiceMain(uint count, IntPtr arguments)
    {
        statusHandle = Native.RegisterServiceCtrlHandlerEx(BrokerProtocol.ServiceName, ControlCallback, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero) return;
        Report(2, 0, 20000);
        int error = 0;
        try
        {
            ElevatedWorker.AssertInstallation();
            Report(4, 1 | 4);
            ServeAsync(Lifetime.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            error = 1066;
            ElevatedWorker.WriteDiagnostic("broker", ex);
        }
        finally { Report(1, 0, error: error); }
    }

    private static uint Control(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5)
        {
            Report(3, 0, 10000);
            Lifetime.Cancel();
        }
        return 0;
    }

    private static void Report(uint state, uint accepted, uint wait = 0, int error = 0)
    {
        var status = new Native.ServiceStatus { Type = 0x10, State = state, Controls = accepted,
            Win32ExitCode = (uint)error, SpecificExitCode = error == 0 ? 0u : 1u, WaitHint = wait };
        Native.SetServiceStatus(statusHandle, ref status);
    }

    private static async Task ServeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var pipe = CreatePipe();
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            try { await pipe.WaitForConnectionAsync(idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }
            try
            {
                if (await ServeClientAsync(pipe, token).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                or InvalidOperationException or JsonException or ArgumentException or OperationCanceledException)
            {
                ElevatedWorker.WriteDiagnostic("broker-client", ex);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        byte[] descriptor = CreatePipeSecurity().GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            var handle = Native.CreateNamedPipe(@"\\.\pipe\" + BrokerProtocol.PipeName,
                3 | 0x40000000 | 0x00080000, 0x00000008, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { pin.Free(); }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetOwner(system);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes
            | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    private static async Task<bool> ServeClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(10));
        // Named-pipe impersonation uses the security context of a message already read.
        string? nextLine = await ElevatedWorker.ReadLineAsync(reader, BrokerProtocol.FrameLimit, handshake.Token).ConfigureAwait(false);
        if (nextLine == null) return false;
        using var peer = Authorize(pipe);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task parentExit = WatchParentAsync(peer.Process, lifetime);
        using var job = new ProcessJob();
        Process? driver = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                if (driver == null) readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                string? line = nextLine ?? await ElevatedWorker.ReadLineAsync(reader, BrokerProtocol.FrameLimit, readTimeout.Token).ConfigureAwait(false);
                nextLine = null;
                if (line == null) break;
                var request = JsonSerializer.Deserialize<BrokerRequest>(line) ?? throw new InvalidDataException("Пустая команда.");
                BrokerProtocol.Validate(request);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                operation.CancelAfter(TimeSpan.FromSeconds(request.Command == "apply-update" ? 100 : 12));
                BrokerResponse response;
                bool finish = request.Command == "stop";
                try
                {
                    switch (request.Command)
                    {
                        case "start":
                            AssertNoUpdateInProgress(AppPaths.ReadJson<BrokerInstallReceipt>(Path.Combine(AppPaths.SharedRoot, "app-updates", "active.json")));
                            ElevatedWorker.StopDriver(ref driver);
                            AssertNoOtherDriver();
                            var active = ElevatedWorker.ReadActive();
                            ElevatedWorker.AssertProtectedTree(active.Directory);
                            var arguments = StrategyParser.Parse(active.Directory, request.Strategy);
                            driver = job.Start(Path.Combine(active.Directory, "bin", "winws.exe"), arguments,
                                Path.Combine(active.Directory, "bin"), operation.Token);
                            await Task.Delay(700, operation.Token).ConfigureAwait(false);
                            if (driver.HasExited) throw new IOException("Не удалось включить подключение. Откройте подробности и проверьте конфликтующие программы.");
                            long monitor = DuplicateWaitHandle(driver, peer.Process.Id);
                            response = new BrokerResponse(true, Pid: driver.Id, StartedTicks: driver.StartTime.ToUniversalTime().Ticks, WaitHandle: monitor);
                            break;
                        case "stop":
                            ElevatedWorker.StopDriver(ref driver);
                            response = new BrokerResponse(true);
                            break;
                        case "status":
                            bool running = driver is { HasExited: false };
                            response = new BrokerResponse(true, Pid: running ? driver!.Id : 0,
                                StartedTicks: running ? driver!.StartTime.ToUniversalTime().Ticks : 0);
                            break;
                        case "apply-update":
                            if (driver is { HasExited: false }) throw new IOException("Перед обновлением отключите подключение.");
                            using (var installer = await StartUpdateAsync(peer, request.Transaction, operation.Token).ConfigureAwait(false))
                                response = new BrokerResponse(true, Pid: installer.Id, StartedTicks: installer.StartTime.ToUniversalTime().Ticks);
                            finish = true;
                            break;
                        default: throw new InvalidDataException("Неизвестная команда.");
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                    or Win32Exception or JsonException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
                {
                    ElevatedWorker.WriteDiagnostic("broker-" + request.Command, ex);
                    response = new BrokerResponse(false, ex is OperationCanceledException ? "Истёк срок ожидания операции." : ex.Message);
                    finish = true;
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), lifetime.Token).ConfigureAwait(false);
                await writer.FlushAsync(lifetime.Token).ConfigureAwait(false);
                if (finish) return request.Command == "apply-update" && response.Success;
            }
        }
        finally
        {
            lifetime.Cancel();
            try { ElevatedWorker.StopDriver(ref driver); }
            finally { try { await parentExit.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        }
        return false;
    }

    private static void AssertNoUpdateInProgress(BrokerInstallReceipt? pending)
    {
        if (pending == null) return;
        try
        {
            using var running = Process.GetProcessById(pending.ProcessId);
            if (!running.HasExited && running.StartTime.ToUniversalTime().Ticks == pending.StartedTicks)
                throw new IOException("Идёт обновление PURSOV STUDIO. Подключение станет доступно после его завершения.");
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static void AssertNoOtherDriver()
    {
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
                if (!process.HasExited) throw new IOException("Уже работает другое подключение Zapret. Закройте его и повторите.");
        }
    }

    private static long DuplicateWaitHandle(Process driver, int clientPid)
    {
        using var target = BrokerNative.OpenProcess(0x0040, false, clientPid);
        if (target.IsInvalid || !BrokerNative.DuplicateHandle(new IntPtr(-1), driver.SafeHandle, target, out IntPtr monitor, 0x00100000, false, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return monitor.ToInt64();
    }

    private static Peer Authorize(NamedPipeServerStream pipe)
    {
        if (!WorkerNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)
            || !Native.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint session) || session == 0)
            throw new UnauthorizedAccessException("Допускается только локальный сеанс Windows.");
        var process = Process.GetProcessById(checked((int)pid));
        WindowsIdentity? identity = null;
        try
        {
            if (process.SessionId != session || process.HasExited
                || !string.Equals(WorkerNative.ImagePath(process.Id), Path.Combine(AppPaths.InstallRoot, "PursovStudio.exe"), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Программа управления не прошла проверку.");
            pipe.RunAsClient(() => identity = WindowsIdentity.GetCurrent(true));
            if (identity?.User == null || identity.IsSystem || identity.Groups == null
                || !identity.Groups.Contains(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null)))
                throw new UnauthorizedAccessException("Требуется локальная учётная запись пользователя.");
            if (!WorkerNative.OpenProcessToken(process.SafeHandle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var processIdentity = new WindowsIdentity(token.DangerousGetHandle()))
                if (processIdentity.User == null || !identity.User.Equals(processIdentity.User)) throw new UnauthorizedAccessException("Пользователь канала не совпадает с владельцем программы.");
            return new Peer(process, identity);
        }
        catch { identity?.Dispose(); process.Dispose(); throw; }
    }

    private static async Task WatchParentAsync(Process process, CancellationTokenSource lifetime)
    {
        try { await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false); lifetime.Cancel(); }
        catch (OperationCanceledException) { }
    }

    private static async Task<Process> StartUpdateAsync(Peer peer, string transaction, CancellationToken token)
    {
        string userRoot = UserLocalData(peer.Identity.AccessToken);
        string source = Path.Combine(userRoot, "PURSOV STUDIO", "staging", "app", transaction);
        string parent = Path.Combine(AppPaths.SharedRoot, "app-updates");
        ElevatedWorker.CreateProtectedDirectory(parent);
        string activeFile = Path.Combine(parent, "active.json");
        AssertNoUpdateInProgress(AppPaths.ReadJson<BrokerInstallReceipt>(activeFile));
        string destination = Path.Combine(parent, transaction);
        if (Directory.Exists(destination)) throw new IOException("Эта установка уже запускалась. Проверьте результат обновления.");
        ElevatedWorker.CreateProtectedDirectory(destination);
        try
        {
            foreach (var item in new[] { ("release.json", 65536L), ("release.sig", 1024L), ("package.msi", 512L * 1024 * 1024) })
            {
                await using var input = WindowsIdentity.RunImpersonated(peer.Identity.AccessToken,
                    () => new FileStream(Path.Combine(source, item.Item1), FileMode.Open, FileAccess.Read, FileShare.Read));
                if (input.Length == 0 || input.Length > item.Item2) throw new InvalidDataException("Недопустимый размер файла обновления.");
                await using var output = ElevatedWorker.CreateProtectedFile(Path.Combine(destination, item.Item1));
                await CopyBoundedAsync(input, output, item.Item2, token).ConfigureAwait(false);
            }
            var manifest = ReleaseTrust.VerifyManifest(await File.ReadAllBytesAsync(Path.Combine(destination, "release.json"), token).ConfigureAwait(false),
                await File.ReadAllBytesAsync(Path.Combine(destination, "release.sig"), token).ConfigureAwait(false));
            await ReleaseTrust.VerifyPackageAsync(Path.Combine(destination, "package.msi"), manifest, token).ConfigureAwait(false);
            string worker = Path.Combine(destination, "UpdateWorker.exe");
            ElevatedWorker.CopyProtectedFileAtomic(Path.Combine(AppPaths.InstallRoot, "UpdateWorker.exe"), worker);
            ElevatedWorker.AssertProtectedTree(destination);
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(worker) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = destination };
            foreach (string argument in new[] { "--apply", transaction, "--parent", peer.Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--ticks", peer.Process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new IOException("Не удалось запустить установку обновления.");
            ElevatedWorker.WriteProtectedJson(activeFile, new BrokerInstallReceipt(process.Id, process.StartTime.ToUniversalTime().Ticks));
            return process;
        }
        catch
        {
            // Preserve a failed protected transaction for diagnosis and never reuse it.
            ElevatedWorker.WriteProtectedJson(Path.Combine(destination, "result.json"), new
            { state = "failed", exitCode = -1, finishedAt = DateTimeOffset.UtcNow, message = "Проверка обновления не завершена. Установленная версия сохранена." });
            throw;
        }
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("Файл обновления превышает допустимый размер.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    private static string UserLocalData(SafeAccessTokenHandle token)
    {
        var folder = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        int result = Native.SHGetKnownFolderPath(ref folder, 0, token, out IntPtr path);
        if (result != 0) Marshal.ThrowExceptionForHR(result);
        try { return Marshal.PtrToStringUni(path) ?? throw new IOException("Не найдена папка данных пользователя."); }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    public static void ConfigureServiceSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
        using var manager = BrokerNative.OpenSCManager(null, null, 1);
        using var service = BrokerNative.OpenService(manager, BrokerProtocol.ServiceName, 0x00040000);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        const string sddl = "D:P(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;LCRP;;;IU)";
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { if (!Native.SetServiceObjectSecurity(service, 4, descriptor)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        finally { Native.LocalFree(descriptor); }
    }

    private sealed class Peer : IDisposable
    {
        internal Process Process { get; }
        internal WindowsIdentity Identity { get; }
        internal Peer(Process process, WindowsIdentity identity) { Process = process; Identity = identity; }
        public void Dispose() { Identity.Dispose(); Process.Dispose(); }
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ServiceMain(uint argc, IntPtr argv);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint ControlHandler(uint control, uint eventType, IntPtr eventData, IntPtr context);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct ServiceTable { internal string? Name; internal ServiceMain? Main; }
        [StructLayout(LayoutKind.Sequential)] internal struct ServiceStatus { internal uint Type, State, Controls, Win32ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool StartServiceCtrlDispatcher([In] ServiceTable[] table);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr RegisterServiceCtrlHandlerEx(string name, ControlHandler handler, IntPtr context);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, SafeAccessTokenHandle token, out IntPtr path);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint length);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool SetServiceObjectSecurity(ServiceHandle service, uint information, IntPtr descriptor);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr pointer);
    }
}
