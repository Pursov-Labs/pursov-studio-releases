using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;

using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PursovStudio;

public static class ElevatedWorker
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    public static Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--worker") return Task.FromResult(false);
        // Version 2 uses the authenticated SCM broker for every privileged operation.
        Environment.ExitCode = 5;
        return Task.FromResult(true);
    }

    internal static void WriteDiagnostic(string phase, Exception error)
    {
        try
        {
            AssertNoReparseAncestors(AppPaths.SharedRoot);
            AssertProtectedEntry(AppPaths.SharedRoot);
            string message = Regex.Replace(error.Message, @"(?i)\b[a-f0-9]{64}\b", "[ключ скрыт]");
            WriteProtectedJson(Path.Combine(AppPaths.SharedRoot, "worker-last-error.json"), new
            {
                Time = DateTimeOffset.UtcNow, Phase = phase, Type = error.GetType().FullName,
                HResult = error.HResult, Message = message
            });
        }
        catch (Exception) { }
    }

    internal static void StopDriver(ref Process? driver)
    {
        if (driver == null) return;
        try
        {
            if (!driver.HasExited)
            {
                driver.Kill();
                if (!driver.WaitForExit(5000)) throw new IOException("Zapret не подтвердил остановку.");
            }
        }
        catch (InvalidOperationException) { }
        driver.Dispose(); driver = null;
    }

    internal static async Task<string?> ReadLineAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= limit) throw new InvalidDataException("Сообщение канала слишком велико.");
            result.Append(buffer[0]);
        }
        return result.Length == 0 ? null : throw new InvalidDataException("Оборванное сообщение канала.");
    }

    internal static InstalledVersion ReadActive()
    {
        var active = AppPaths.ReadJson<InstalledVersion>(AppPaths.ZapretActiveFile)
            ?? throw new InvalidDataException("Zapret ещё не установлен. Выполните установку приложения.");
        string expected = VersionDirectory(active.Version);
        if (!string.Equals(Path.GetFullPath(active.Directory), expected, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(expected))
            throw new InvalidDataException("Повреждена запись установленной версии Zapret.");
        return active with { Directory = expected };
    }

    internal static string VersionDirectory(string version)
    {
        if (version == null || !Regex.IsMatch(version, @"^\d{1,3}(?:\.\d{1,3}){1,3}(?:-[A-Za-z0-9.-]{1,32})?$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Недопустимый номер версии.");
        return Path.GetFullPath(Path.Combine(AppPaths.ZapretRoot, version));
    }

    internal static void AssertInstallation()
    {
        string root = Path.GetFullPath(AppPaths.InstallRoot).TrimEnd(Path.DirectorySeparatorChar);
        string app = Path.GetFullPath(AppPaths.AppRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(app, root, StringComparison.OrdinalIgnoreCase) && !app.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Сначала установите приложение в Program Files с помощью установщика.");
        AssertNoReparseAncestors(root);
        AssertNoReparseAncestors(AppPaths.SharedRoot);
        AssertProtectedTree(root);
        AssertProtectedTree(AppPaths.SharedRoot);
    }

    private static void AssertNoReparseAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Перенаправления в пути установки недопустимы.");
    }

    internal static void AssertProtectedTree(string path)
    {
        AssertProtectedEntry(path);
        foreach (string file in Directory.EnumerateFileSystemEntries(path))
        {
            AssertProtectedEntry(file);
            if (Directory.Exists(file)) AssertProtectedTree(file);
        }
    }

    private static void AssertProtectedEntry(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Ссылки и перенаправления в защищённой папке недопустимы.");
        FileSystemSecurity security = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = (SecurityIdentifier?)security.GetOwner(typeof(SecurityIdentifier));
        if (owner == null || (!owner.Equals(Administrators) && !owner.Equals(SystemSid)))
            throw new UnauthorizedAccessException("Владелец установленного файла не защищён. Повторите установку.");
        const FileSystemRights writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & writes) != 0
                && !rule.IdentityReference.Equals(Administrators) && !rule.IdentityReference.Equals(SystemSid))
                throw new UnauthorizedAccessException("Папка приложения доступна для записи без администратора. Повторите установку.");
    }

    private static void ExtractArchive(string archive, string destination, string version, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 1000) throw new InvalidDataException("Слишком много файлов в архиве.");
        string prefix = "zapret-discord-youtube-" + version + "/";
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = entry.FullName;
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Contains('\\') || name.Contains(':')
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Небезопасный путь в архиве.");
            string relative = name.Substring(prefix.Length);
            if (relative.Length == 0) continue;
            if (relative.Split('/').Any(s => s == "." || s == ".." || s.EndsWith(' ') || s.EndsWith('.')))
                throw new InvalidDataException("Недопустимый путь в архиве.");
            string path = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !seen.Add(path))
                throw new InvalidDataException("Повторный путь или выход за папку архива.");
            if (name.EndsWith('/')) { CreateProtectedDirectory(path); continue; }
            total += entry.Length;
            if (entry.Length > 32 * 1024 * 1024 || total > 128 * 1024 * 1024)
                throw new InvalidDataException("Слишком большой распакованный архив.");
            CreateProtectedDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = CreateProtectedFile(path);
            byte[] buffer = new byte[64 * 1024];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                token.ThrowIfCancellationRequested();
                written += read;
                if (written > entry.Length || written > 32 * 1024 * 1024) throw new InvalidDataException("Размер файла архива неверен.");
                output.Write(buffer, 0, read);
            }
            if (written != entry.Length) throw new InvalidDataException("Архив повреждён.");
        }
    }

    private static void PreserveSettings(string current, string destination)
    {
        foreach (string relative in new[]
        {
            "lists/ipset-exclude-user.txt", "lists/list-general-user.txt", "lists/list-exclude-user.txt",
            "lists/ipset-all.txt", "lists/ipset-all.txt.backup", "utils/targets.txt", "utils/game_filter.enabled"
        })
        {
            string source = Path.Combine(current, relative);
            string target = Path.Combine(destination, relative);
            if (File.Exists(source))
            {
                if (new FileInfo(source).Length > 4 * 1024 * 1024) throw new InvalidDataException("Локальная конфигурация слишком велика: " + relative);
                CopyProtectedFileAtomic(source, target);
            }
            else if (relative == "utils/game_filter.enabled" && File.Exists(target)) File.Delete(target);
        }
        foreach (string active in new[] { "ACTIVE_DISCORD_UDP.bin", "ACTIVE_GAME_UDP.bin" })
        {
            string source = Path.Combine(current, "bin", active);
            if (!File.Exists(source)) continue;
            byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
            string? selected = Directory.EnumerateFiles(Path.Combine(current, "bin"), "*.bin")
                .FirstOrDefault(p => !Path.GetFileName(p).StartsWith("ACTIVE_", StringComparison.Ordinal)
                    && CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(File.ReadAllBytes(p))));
            if (selected == null || !File.Exists(Path.Combine(destination, "bin", Path.GetFileName(selected))))
                throw new InvalidDataException("Новый релиз не поддерживает выбранный UDP-ресурс. Текущая версия сохранена.");
            CopyProtectedFileAtomic(Path.Combine(destination, "bin", Path.GetFileName(selected)), Path.Combine(destination, "bin", active));
        }
    }

    // Owner is set at creation: a user SID must never acquire WRITE_DAC on a transient elevated output.
    internal static void CreateProtectedDirectory(string path, bool installationRoot = false)
    {
        if (Directory.Exists(path)) { AssertProtectedEntry(path); return; }
        string parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Недопустимый каталог.");
        if (!Directory.Exists(parent))
        {
            if (installationRoot) throw new DirectoryNotFoundException("Системная папка установки не найдена.");
            CreateProtectedDirectory(parent);
        }
        AssertNoReparseAncestors(parent);
        if (installationRoot)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(full, Path.GetFullPath(AppPaths.InstallRoot), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(full, Path.GetFullPath(AppPaths.SharedRoot), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Недопустимая корневая папка установки.");
        }
        else
        {
            AssertProtectedEntry(parent);
        }
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    internal static FileStream CreateProtectedFile(string path, bool readableByUsers = true)
    {
        string parent = Path.GetDirectoryName(path) ?? throw new InvalidDataException("Недопустимый путь файла.");
        AssertNoReparseAncestors(parent);
        AssertProtectedEntry(parent);
        var security = new FileSecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        if (readableByUsers)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous, security);
    }

    internal static void WriteProtectedJson<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = CreateProtectedFile(temporary))
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, AppPaths.JsonOptions);
                file.Write(json);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void CopyProtectedFileAtomic(string source, string target)
    {
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = CreateProtectedFile(temporary)) input.CopyTo(output);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<string> SelfTest()
    {
        var passed = new List<string>();
        string temporary = Path.Combine(Path.GetTempPath(), "pursov-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            bool refused = false;
            try { AssertProtectedTree(temporary); }
            catch (UnauthorizedAccessException) { refused = true; }
            if (!refused) throw new Exception("Папка с обычным пользовательским ACL принята как защищённая.");
            passed.Add("Отказ от пользовательской папки для повышенного запуска");
            foreach (string entry in new[] { "zapret-discord-youtube-1.10.4/../escape.txt", "zapret-discord-youtube-1.10.4/C:/escape.txt",
                "zapret-discord-youtube-1.10.4/path\\escape.txt", "wrong-root/file.txt", "zapret-discord-youtube-1.10.4/link" })
            {
                string archive = Path.Combine(temporary, Guid.NewGuid().ToString("N") + ".zip");
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                {
                    var item = zip.CreateEntry(entry);
                    if (entry.EndsWith("/link", StringComparison.Ordinal)) item.ExternalAttributes = unchecked((int)(0xA1FF << 16));
                    using var stream = new StreamWriter(item.Open());
                    stream.Write("fixture");
                }
                refused = false;
                try { ExtractArchive(archive, temporary, "1.10.4", CancellationToken.None); }
                catch (InvalidDataException) { refused = true; }
                if (!refused) throw new Exception("Опасная запись ZIP не отклонена.");
            }
            passed.Add("ZIP: обход пути, абсолютный путь, обратные разделители, чужой корень и symlink отклонены");
            if (ProcessJob.Quote("C:\\a b\\") != "\"C:\\a b\\\\\"") throw new Exception("Ошибка завершающей обратной косой черты аргумента.");
            string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
            var job = new ProcessJob();
            Process? own = null;
            try
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                refused = false;
                try { using var unexpected = job.Start(ping, new[] { "-n", "60", "127.0.0.1" }, Environment.SystemDirectory, cancelled.Token); }
                catch (OperationCanceledException) { refused = true; }
                if (!refused) throw new Exception("Отмена до старта не сработала.");
                own = job.Start(ping, new[] { "-n", "60", "127.0.0.1" }, Environment.SystemDirectory);
                if (own.HasExited) throw new Exception("Проверочный процесс не запустился.");
                job.Dispose();
                if (!own.WaitForExit(5000)) throw new Exception("Закрытие Job Object не остановило собственный проверочный процесс.");
            }
            finally
            {
                job.Dispose();
                if (own != null)
                {
                    if (!own.HasExited) { own.Kill(); own.WaitForExit(5000); }
                    own.Dispose();
                }
            }
            passed.Add("Job Object: отмена до старта и остановка собственного ping при закрытии job (драйвер не запускается)");
        }
        finally { Directory.Delete(temporary, true); }
        return passed;
    }
}

internal sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;

    internal ProcessJob()
    {
        handle = WorkerNative.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception();
        var limits = new WorkerNative.ExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!WorkerNative.SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<WorkerNative.ExtendedLimitInformation>()))
        { handle.Dispose(); throw new Win32Exception(); }
    }

    internal Process Start(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var info = new WorkerNative.StartupInfo { cb = Marshal.SizeOf<WorkerNative.StartupInfo>() };
        var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
        string windows = Directory.GetParent(Environment.SystemDirectory)!.FullName;
        // Cygwin must not inherit user-controlled LD_PRELOAD, PATH or shell hooks after elevation.
        string environment = "COMSPEC=" + Path.Combine(Environment.SystemDirectory, "cmd.exe") + '\0'
            + "PATH=" + workingDirectory + ";" + Environment.SystemDirectory + ";" + windows + '\0'
            + "SystemDrive=" + Path.GetPathRoot(windows)!.TrimEnd('\\') + '\0'
            + "SystemRoot=" + windows + '\0' + "WINDIR=" + windows + "\0\0";
        IntPtr block = Marshal.StringToHGlobalUni(environment);
        WorkerNative.ProcessInformation process;
        try
        {
            if (!WorkerNative.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x00000004 | 0x08000000 | 0x00000400,
                block, workingDirectory, ref info, out process)) throw new Win32Exception();
        }
        finally { Marshal.FreeHGlobal(block); }
        try
        {
            if (!WorkerNative.AssignProcessToJobObject(handle, process.hProcess)) throw new Win32Exception();
            token.ThrowIfCancellationRequested();
            if (WorkerNative.ResumeThread(process.hThread) == uint.MaxValue) throw new Win32Exception();
            return Process.GetProcessById((int)process.dwProcessId);
        }
        catch { WorkerNative.TerminateProcess(process.hProcess, 1); throw; }
        finally { WorkerNative.CloseHandle(process.hThread); WorkerNative.CloseHandle(process.hProcess); }
    }

    internal static string Quote(string argument)
    {
        if (argument.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new InvalidDataException("Недопустимый символ аргумента.");
        var value = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') value.Append('\\', backslashes * 2 + 1).Append('"');
            else value.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return value.Append('\\', backslashes * 2).Append('"').ToString();
    }

    public void Dispose() => handle.Dispose();
}

internal static class WorkerNative
{
    internal static string ImagePath(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid);
        if (process.IsInvalid) throw new Win32Exception();
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new Win32Exception();
        return path.ToString();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string workingDirectory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimitInformation
    {
        internal long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
    {
        internal ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimitInformation
    {
        internal BasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
    {
        internal int cb;
        internal string? lpReserved, lpDesktop, lpTitle;
        internal uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        internal ushort wShowWindow, cbReserved2;
        internal IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation
    {
        internal IntPtr hProcess, hThread;
        internal uint dwProcessId, dwThreadId;
    }
}
