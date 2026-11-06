using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PursovStudio;

internal sealed record BrokerRequest(string Command, string Strategy = "", string Transaction = "");
internal sealed record BrokerResponse(bool Success, string Error = "", int Pid = 0, long StartedTicks = 0, long WaitHandle = 0);
internal sealed record BrokerServiceConfiguration(uint Type, string BinaryPath, string Account);
public sealed record BrokerInstallReceipt(int ProcessId, long StartedTicks);

internal static class BrokerProtocol
{
    internal const string ServiceName = "PursovStudioBroker";
    internal const string PipeName = "PursovStudio.Broker.v2";
    internal const int FrameLimit = 4096;
    internal const uint ServiceClientAccess = 0x0001 | 0x0004 | 0x0010;
    internal const string ServiceSecurityDescriptor = "D:P(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRP;;;IU)";

    internal static void ValidateServiceConfiguration(BrokerServiceConfiguration configuration)
    {
        string executable = "\"" + Path.Combine(AppPaths.InstallRoot, "PursovStudio.exe") + "\"";
        if (configuration.Type != 0x10 || !string.Equals(configuration.Account, "LocalSystem", StringComparison.OrdinalIgnoreCase)
            || !configuration.BinaryPath.StartsWith(executable, StringComparison.OrdinalIgnoreCase)
            || configuration.BinaryPath.Substring(executable.Length) != " --service")
            throw new UnauthorizedAccessException("Служба подключения зарегистрирована неверно. Повторите установку PURSOV STUDIO.");
    }

    internal static void Validate(BrokerRequest request)
    {
        if (request.Command == null || request.Strategy == null || request.Transaction == null)
            throw new InvalidDataException("Неполная команда службы.");
        if (request.Command == "start" && request.Transaction == "" && request.Strategy.Length is > 0 and <= 150
            && !request.Strategy.Contains('/') && !request.Strategy.Contains('\\') && !request.Strategy.Contains('\0')) return;
        if (request.Command is "stop" or "status" && request.Strategy == "" && request.Transaction == "") return;
        if (request.Command == "apply-update" && request.Strategy == ""
            && Regex.IsMatch(request.Transaction, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant)) return;
        throw new InvalidDataException("Недопустимая команда службы.");
    }
}

internal sealed class BrokerConnection : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    internal bool IsConnected => pipe.IsConnected;

    private BrokerConnection(NamedPipeClientStream stream)
    {
        pipe = stream;
        reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 4096, true);
        writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
    }

    internal static async Task<BrokerConnection> ConnectAsync(CancellationToken token)
    {
        string phase = "installation-acl";
        try
        {
            ElevatedWorker.AssertInstallation();
            phase = "open-scm";
            using var manager = BrokerNative.OpenSCManager(null, null, 1);
            if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            phase = "open-service";
            using var service = BrokerNative.OpenService(manager, BrokerProtocol.ServiceName, BrokerProtocol.ServiceClientAccess);
            if (service.IsInvalid) throw new IOException("Служба подключения не установлена. Повторите установку PURSOV STUDIO.", new Win32Exception(Marshal.GetLastWin32Error()));
            phase = "service-configuration";
            BrokerProtocol.ValidateServiceConfiguration(BrokerNative.ServiceConfiguration(service));
            phase = "start-service";
            if (!BrokerNative.StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056)
                throw new IOException("Не удалось запустить службу подключения.", new Win32Exception(Marshal.GetLastWin32Error()));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    phase = "open-pipe";
                    // GenericWrite would also grant creation of another server instance.
                    var handle = BrokerNative.CreateFile(@"\\.\pipe\" + BrokerProtocol.PipeName, 0x00120083, 0, IntPtr.Zero, 3,
                        0x40000000 | 0x00100000 | 0x00020000, IntPtr.Zero);
                    if (!handle.IsInvalid)
                    {
                        var stream = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
                        try
                        {
                            phase = "verify-pipe-server";
                            var status = BrokerNative.ServiceStatus(service);
                            if (!WorkerNative.GetNamedPipeServerProcessId(stream.SafePipeHandle, out uint serverPid)
                                || status.Type != 0x10 || status.State != 4 || status.ProcessId == 0 || serverPid != status.ProcessId
                                || !Equals(stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)))
                                throw new UnauthorizedAccessException("Служба подключения не прошла проверку подлинности.");
                            return new BrokerConnection(stream);
                        }
                        catch { stream.Dispose(); throw; }
                    }
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error is not 2 and not 231) throw new IOException("Канал службы подключения недоступен.", new Win32Exception(error));
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("Служба занята другим сеансом Windows или не отвечает. Закройте подключение в другом сеансе и повторите.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Write("Проверка службы, этап " + phase + ": " + ex.GetType().Name + " / " + ex.Message
                + (ex.InnerException == null ? "" : " / " + ex.InnerException.Message));
            if (ex is Win32Exception) throw new IOException("Не удалось подтвердить службу подключения. Откройте подробности или повторите установку.", ex);
            throw;
        }
    }

    internal async Task<BrokerResponse> SendAsync(BrokerRequest request, CancellationToken token)
    {
        BrokerProtocol.Validate(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Command == "apply-update" ? 120 : 15));
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token).ConfigureAwait(false);
            await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
            string? line = await ElevatedWorker.ReadLineAsync(reader, BrokerProtocol.FrameLimit, timeout.Token).ConfigureAwait(false);
            var response = line == null ? null : JsonSerializer.Deserialize<BrokerResponse>(line);
            if (response == null) throw new IOException("Соединение со службой закрыто.");
            if (!response.Success) throw new IOException(response.Error);
            return response;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("Служба не ответила вовремя. Подключение остановлено.");
        }
    }

    public void Dispose()
    {
        // Closing the pipe releases the service's lease and kill-on-close job.
        try { writer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        reader.Dispose();
        pipe.Dispose();
    }
}

public static class BrokerClient
{
    public static async Task<BrokerInstallReceipt> ApplyUpdateAsync(string transactionId, CancellationToken token)
    {
        using var connection = await BrokerConnection.ConnectAsync(token).ConfigureAwait(false);
        var response = await connection.SendAsync(new BrokerRequest("apply-update", Transaction: transactionId), token).ConfigureAwait(false);
        return new BrokerInstallReceipt(response.Pid, response.StartedTicks);
    }
}

internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private ServiceHandle() : base(true) { }
    protected override bool ReleaseHandle() => BrokerNative.CloseServiceHandle(handle);
}

internal static class BrokerNative
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeServiceConfiguration
    {
        internal uint Type, StartType, ErrorControl;
        internal IntPtr BinaryPath, LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies, Account, DisplayName;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ServiceStatusProcess
    {
        internal uint Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }
    internal static ServiceStatusProcess ServiceStatus(ServiceHandle service)
    {
        if (!QueryServiceStatusEx(service, 0, out ServiceStatusProcess status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }
    internal static BrokerServiceConfiguration ServiceConfiguration(ServiceHandle service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out uint needed);
        if (Marshal.GetLastWin32Error() != 122 || needed == 0 || needed > 8192)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfig(service, buffer, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var configuration = Marshal.PtrToStructure<NativeServiceConfiguration>(buffer);
            return new BrokerServiceConfiguration(configuration.Type, Marshal.PtrToStringUni(configuration.BinaryPath) ?? "",
                Marshal.PtrToStringUni(configuration.Account) ?? "");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool StartService(ServiceHandle service, uint argc, IntPtr argv);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(ServiceHandle service, IntPtr configuration, uint size, out uint needed);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafePipeHandle CreateFile(string name, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle source, SafeProcessHandle targetProcess, out IntPtr target, uint access, bool inherit, uint options);
}
