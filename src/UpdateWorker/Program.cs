using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private static string SharedRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PURSOV STUDIO", "app-updates"); } }
    private static string UserRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PURSOV STUDIO", "staging", "app"); } }

    [STAThread]
    private static int Main(string[] args)
    {
        string directory = null;
        bool applying = args.Length > 0 && args[0] == "--apply";
        try
        {
            if (args.Length == 2 && (args[0] == "--backup-install" || args[0] == "--restore-install" || args[0] == "--commit-install"))
            {
                if (!WindowsIdentity.GetCurrent().IsSystem) throw new UnauthorizedAccessException("Миграцию запускает установщик Windows.");
                Guid installId;
                if (!Guid.TryParse(args[1], out installId)) throw new InvalidOperationException("Некорректный номер установки.");
                SnapshotInstall(args[0], installId.ToString("N"));
                return 0;
            }
            Guid id;
            if (args.Length < 6 || (args[0] != "--apply" && args[0] != "--wait") || !Guid.TryParseExact(args[1], "N", out id) || args[2] != "--parent" || args[4] != "--ticks")
                throw new InvalidOperationException("Некорректные параметры обновления.");
            int parentId = int.Parse(args[3], CultureInfo.InvariantCulture);
            long parentTicks = long.Parse(args[5], CultureInfo.InvariantCulture);
            if (parentId <= 0 || parentTicks <= 0 || parentId == Process.GetCurrentProcess().Id || args.Length > 7 || (args.Length == 7 && args[6] != "--resume"))
                throw new InvalidOperationException("Некорректные параметры перезапуска.");
            directory = Path.Combine(SharedRoot, args[1]);
            string expected = Path.Combine(applying ? SharedRoot : UserRoot, args[1]);
            if (!Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\').Equals(Path.GetFullPath(expected).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Помощник запущен из неподходящего каталога.");
            if (applying && !WindowsIdentity.GetCurrent().IsSystem)
                throw new UnauthorizedAccessException("Установку запускает системная служба приложения.");
            WaitForParent(parentId, parentTicks);
            if (applying) return Apply(directory);
            WaitAndRestart(directory, args[1], args.Length == 7);
            return 0;
        }
        catch (Exception ex)
        {
            if (applying && directory != null && WindowsIdentity.GetCurrent().IsSystem)
            {
                try { WriteResult(directory, new InstallResult { state = "failed", exitCode = -1, message = ex.Message, finishedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }); }
                catch { }
            }
            else if (!applying && !WindowsIdentity.GetCurrent().IsSystem)
                MessageBox.Show(ex.Message + "\nОткройте PURSOV STUDIO через меню «Пуск» после завершения установки.", "Обновление PURSOV STUDIO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
    }

    private static void SnapshotInstall(string operation, string id)
    {
        string shared = Path.GetDirectoryName(SharedRoot);
        string snapshotRoot = Path.Combine(shared, "installer-rollback");
        string snapshot = Path.Combine(snapshotRoot, id);
        string manifest = Path.Combine(snapshot, "snapshot.json");
        string zapret = Path.Combine(shared, "zapret");
        string active = Path.Combine(shared, "zapret-active.json");
        if (operation == "--backup-install")
        {
            string installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PURSOV STUDIO");
            foreach (var process in Process.GetProcessesByName("PursovStudio"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule != null && string.Equals(Path.GetFullPath(process.MainModule.FileName), Path.Combine(installRoot, "PursovStudio.exe"), StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Закройте PURSOV STUDIO через меню значка в трее и повторите установку.");
                    }
                    catch (InvalidOperationException) { }
                }
            }
            PrepareInstallRoot(installRoot);
            PrepareInstallRoot(shared);
            SecureDirectory(snapshotRoot);
            if (Directory.Exists(snapshot))
            {
                if (File.Exists(manifest)) throw new IOException("Предыдущая установка не завершена. Выполните её восстановление в Windows.");
                DeleteSnapshot(snapshotRoot, snapshot);
            }
            SecureDirectory(snapshot);
            var record = new InstallSnapshot { hadZapret = Directory.Exists(zapret), hadActive = File.Exists(active) };
            if (record.hadZapret) { AssertProtectedTree(zapret); CopyTree(zapret, Path.Combine(snapshot, "zapret"), false); }
            if (record.hadActive)
            {
                if ((File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылка вместо конфигурации запрещена.");
                File.Copy(active, Path.Combine(snapshot, "zapret-active.json"));
            }
            using (var stream = File.Create(manifest)) new DataContractJsonSerializer(typeof(InstallSnapshot)).WriteObject(stream, record);
            MigrateLegacy("--backup-legacy");
            return;
        }
        if (!File.Exists(manifest)) return;
        AssertProtectedTree(snapshot);
        if (operation == "--restore-install")
        {
            InstallSnapshot record;
            using (var stream = File.OpenRead(manifest)) record = (InstallSnapshot)new DataContractJsonSerializer(typeof(InstallSnapshot)).ReadObject(stream);
            if (Directory.Exists(zapret))
            {
                AssertProtectedTree(zapret);
                Directory.Move(zapret, Path.Combine(snapshot, "discarded-zapret"));
            }
            if (record.hadZapret) Directory.Move(Path.Combine(snapshot, "zapret"), zapret);
            if (File.Exists(active))
            {
                if ((File.GetAttributes(active) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылка вместо конфигурации запрещена.");
                File.Delete(active);
            }
            if (record.hadActive) File.Move(Path.Combine(snapshot, "zapret-active.json"), active);
            MigrateLegacy("--restore-legacy");
        }
        else MigrateLegacy("--commit-legacy");
        File.Delete(manifest);
        DeleteSnapshot(snapshotRoot, snapshot);
    }

    private static void PrepareInstallRoot(string path)
    {
        RejectReparseChain(path);
        if (Directory.Exists(path)) AssertProtectedTree(path);
        SecureDirectory(path);
    }

    private static void DeleteSnapshot(string root, string path)
    {
        string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Недопустимый путь резервной копии.");
        AssertProtectedTree(path);
        Directory.Delete(path, true);
    }

    [DataContract]
    private sealed class InstallSnapshot
    {
        [DataMember] public bool hadZapret;
        [DataMember] public bool hadActive;
    }

    private static void MigrateLegacy(string operation)
    {
        string installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PURSOV STUDIO");
        string shared = Path.GetDirectoryName(SharedRoot);
        string backupRoot = Path.Combine(shared, "legacy-backup");
        string pending = Path.Combine(backupRoot, "pending");
        if (operation == "--commit-legacy")
        {
            if (File.Exists(pending)) File.Delete(pending);
            return;
        }
        if (operation == "--restore-legacy")
        {
            if (!File.Exists(pending)) return;
            Guid snapshotId;
            if (!Guid.TryParseExact(File.ReadAllText(pending), "N", out snapshotId)) throw new IOException("Некорректная резервная копия прежней установки.");
            string backup = Path.Combine(backupRoot, snapshotId.ToString("N"));
            AssertProtectedTree(backup);
            CopyTree(backup, installRoot, true);
            File.Delete(pending);
            return;
        }
        string executable = Path.Combine(installRoot, "PursovStudio.exe");
        if (!File.Exists(executable)) return;
        var installedVersion = FileVersionInfo.GetVersionInfo(executable);
        if (installedVersion.FileMajorPart != 1 || installedVersion.FileMinorPart != 0 || installedVersion.FileBuildPart > 1) return;
        AssertProtectedTree(installRoot);
        SecureDirectory(shared);
        SecureDirectory(backupRoot);
        string newSnapshot = Guid.NewGuid().ToString("N");
        CopyTree(installRoot, Path.Combine(backupRoot, newSnapshot), false);
        File.WriteAllText(pending, newSnapshot);
    }

    private static void SecureDirectory(string path)
    {
        RejectReparseChain(path);
        if (Directory.Exists(path))
        {
            AssertProtectedDirectory(path);
            return;
        }
        var acl = new DirectorySecurity();
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        acl.SetOwner(administrators);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { administrators, system })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);
        AssertProtectedDirectory(path);
    }

    private static void RejectReparseChain(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки в каталогах установки запрещены.");
    }

    private static void AssertProtectedTree(string path)
    {
        RejectReparseChain(path);
        AssertProtectedDirectory(path);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (string item in Directory.GetFileSystemEntries(path))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки в каталоге установки запрещены.");
            if (Directory.Exists(item)) AssertProtectedTree(item);
            else
            {
                var fileAcl = File.GetAccessControl(item);
                var fileOwner = fileAcl.GetOwner(typeof(SecurityIdentifier));
                if (!fileOwner.Equals(administrators) && !fileOwner.Equals(system)) throw new UnauthorizedAccessException("Файл установки принадлежит обычному пользователю.");
                AssertAccess(fileAcl, administrators, system);
            }
        }
    }

    private static void AssertProtectedDirectory(string path)
    {
        RejectReparseChain(path);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = Directory.GetAccessControl(path);
        var owner = acl.GetOwner(typeof(SecurityIdentifier));
        if (!owner.Equals(administrators) && !owner.Equals(system)) throw new UnauthorizedAccessException("Каталог установки принадлежит обычному пользователю.");
        AssertAccess(acl, administrators, system);
    }

    private static void AssertAccess(FileSystemSecurity acl, SecurityIdentifier administrators, SecurityIdentifier system)
    {
        var write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0 && !rule.IdentityReference.Equals(administrators) && !rule.IdentityReference.Equals(system))
                throw new UnauthorizedAccessException("Обычный пользователь может менять файлы установки.");
    }

    private static void CopyTree(string source, string destination, bool overwrite)
    {
        SecureDirectory(destination);
        foreach (string path in Directory.GetFileSystemEntries(source))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Копирование ссылок запрещено.");
            string target = Path.Combine(destination, Path.GetFileName(path));
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Замена ссылки запрещена.");
            if (Directory.Exists(path)) CopyTree(path, target, overwrite);
            else File.Copy(path, target, overwrite);
        }
    }

    private static void WaitForParent(int id, long ticks)
    {
        try
        {
            using (var parent = Process.GetProcessById(id))
            {
                if (parent.StartTime.ToUniversalTime().Ticks != ticks) return;
                if (!parent.WaitForExit(60000)) throw new TimeoutException("Приложение не закрылось. Обновление отменено.");
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static int Apply(string directory)
    {
        using (var transactionLock = new FileStream(Path.Combine(directory, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (File.Exists(Path.Combine(directory, "result.json"))) throw new InvalidOperationException("Это обновление уже завершено.");
            string package = Path.Combine(directory, "package.msi");
            if (!File.Exists(package) || (File.GetAttributes(package) & FileAttributes.ReparsePoint) != 0) throw new IOException("Установочный файл отсутствует.");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory,
                Arguments = "/i \"" + package + "\" /qn /norestart /L*v \"" + Path.Combine(directory, "install.log") + "\""
            };
            using (var installer = Process.Start(start))
            {
                if (installer == null) throw new IOException("Не удалось запустить установку.");
                // MSI owns rollback and may wait for another installer. Do not kill its transaction on a UI timeout.
                installer.WaitForExit();
                int code = installer.ExitCode;
                var result = new InstallResult
                {
                    state = code == 0 ? "complete" : (code == 3010 || code == 1641 ? "reboot-required" : "failed"),
                    exitCode = code,
                    message = code == 0 ? "Обновление установлено" : (code == 3010 || code == 1641 ? "Перезагрузите Windows для завершения обновления" : "Обновление не установлено. Подробности сохранены в журнале."),
                    finishedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
                WriteResult(directory, result);
                return code == 0 || code == 3010 || code == 1641 ? 0 : 1;
            }
        }
    }

    private static void WaitAndRestart(string directory, string transactionId, bool resume)
    {
        string resultPath = Path.Combine(directory, "result.json");
        DateTime deadline = DateTime.UtcNow.AddMinutes(20);
        while (!File.Exists(resultPath))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Установка ещё не завершилась. Её состояние можно проверить в диспетчере задач Windows.");
            Thread.Sleep(500);
        }
        InstallResult result;
        using (var stream = File.OpenRead(resultPath))
            result = (InstallResult)new DataContractJsonSerializer(typeof(InstallResult)).ReadObject(stream);
        if (result.state == "reboot-required")
        {
            MessageBox.Show("Обновление установлено. Перезагрузите Windows, чтобы завершить его.", "PURSOV STUDIO", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PURSOV STUDIO", "PursovStudio.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable),
            Arguments = "--update-result " + transactionId + (resume ? " --resume" : " --stay-off")
        };
        using (var launched = Process.Start(start))
            if (launched == null) throw new IOException("Не удалось открыть обновлённое приложение.");
    }

    private static void WriteResult(string directory, InstallResult result)
    {
        string path = Path.Combine(directory, "result.json");
        string temporary = Path.Combine(directory, "result." + Guid.NewGuid().ToString("N") + ".tmp");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            new DataContractJsonSerializer(typeof(InstallResult)).WriteObject(stream, result);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    [DataContract]
    private sealed class InstallResult
    {
        [DataMember] public string state;
        [DataMember] public int exitCode;
        [DataMember] public string message;
        [DataMember] public string finishedAt;
    }
}
