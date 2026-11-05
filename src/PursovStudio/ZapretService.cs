using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PursovStudio;

public sealed class ZapretService : IZapretService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private BrokerConnection? connection;
    private ProcessWaitHandle? driverExit;
    private RegisteredWaitHandle? driverWait;
    private bool disposed;

    public event Action? StateChanged;

    public string Version
    {
        get
        {
            try { return ElevatedWorker.ReadActive().Version; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return "не установлен"; }
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return connection?.IsConnected == true && driverExit != null && !driverExit.WaitOne(0); }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public IReadOnlyList<StrategyInfo> GetStrategies()
    {
        try { return StrategyParser.List(ElevatedWorker.ReadActive().Directory); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return Array.Empty<StrategyInfo>(); }
    }

    public async Task StartAsync(string strategyId, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            connection ??= await BrokerConnection.ConnectAsync(token).ConfigureAwait(false);
            var response = await connection.SendAsync(new BrokerRequest("start", Strategy: strategyId), token).ConfigureAwait(false);
            ClearDriverWait();
            if (response.WaitHandle <= 0 || response.Pid <= 0) throw new IOException("Служба не подтвердила запуск подключения.");
            driverExit = new ProcessWaitHandle(response.WaitHandle);
            driverWait = ThreadPool.RegisterWaitForSingleObject(driverExit, (_, _) => StateChanged?.Invoke(), null, Timeout.Infinite, true);
        }
        catch { CloseConnection(); throw; }
        finally { gate.Release(); }
        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool acquired = false;
        try
        {
            await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            if (!disposed && connection?.IsConnected == true)
                await connection.SendAsync(new BrokerRequest("stop"), timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) { CloseConnection(); gate.Release(); }
            else connection?.Dispose();
            StateChanged?.Invoke();
        }
    }

    public Task InstallUpdateAsync(string transactionId, string version, CancellationToken token) =>
        Task.FromException(new InvalidOperationException("Сетевые компоненты обновляются вместе с PURSOV STUDIO. Откройте раздел обновлений."));

    public Task ActivateVersionAsync(string version, CancellationToken token) =>
        Task.FromException(new InvalidOperationException("Версия компонента задаётся установщиком PURSOV STUDIO."));

    private void ClearDriverWait()
    {
        driverWait?.Unregister(null);
        driverWait = null;
        driverExit?.Dispose();
        driverExit = null;
    }

    private void CloseConnection()
    {
        ClearDriverWait();
        connection?.Dispose();
        connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            // Pipe closure stops the owned process even if a graceful stop cannot finish.
            CloseConnection();
        }
        finally { gate.Release(); }
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(long handle) { SafeWaitHandle = new SafeWaitHandle(new IntPtr(handle), true); }
    }
}
