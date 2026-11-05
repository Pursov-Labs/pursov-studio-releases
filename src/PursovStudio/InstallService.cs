using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;

namespace PursovStudio;

public static class InstallService
{
    public static bool IsInstalled => Path.GetFullPath(AppPaths.AppRoot).TrimEnd('\\').Equals(Path.GetFullPath(AppPaths.InstallRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    public static Task InstallAsync() => throw new InvalidOperationException("Откройте MSI-файл PURSOV STUDIO для установки.");

    public static Task<bool> TryRunAsync(string[] args)
    {
        if (!args.Contains("--initialize-install")) return Task.FromResult(false);
        try
        {
            if (!IsInstalled || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Инициализация доступна только установщику.");
            SecureDirectory(AppPaths.InstallRoot);
            SecureDirectory(AppPaths.SharedRoot);
            SecureDirectory(AppPaths.ZapretRoot);
            string payload = Path.Combine(AppPaths.InstallRoot, "payload", "zapret");
            string bundled = Directory.EnumerateDirectories(payload)
                .Where(x => Version.TryParse(Path.GetFileName(x), out _))
                .OrderByDescending(x => Version.Parse(Path.GetFileName(x))).First();
            string version = Path.GetFileName(bundled);
            string destination = Path.Combine(AppPaths.ZapretRoot, version);
            string? current = File.Exists(AppPaths.ZapretActiveFile) ? ElevatedWorker.ReadActive().Directory
                : Directory.Exists(destination) ? destination : null;
            InstallPayload(bundled, destination, current);
            ElevatedWorker.WriteProtectedJson(AppPaths.ZapretActiveFile, new InstalledVersion(version, destination));
            BrokerService.ConfigureServiceSecurity();
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Log.Write("Инициализация установки: " + ex.Message);
            Environment.ExitCode = 1;
        }
        return Task.FromResult(true);
    }

    public static void SecureDirectory(string path)
    {
        ElevatedWorker.CreateProtectedDirectory(path, path.Equals(AppPaths.InstallRoot, StringComparison.OrdinalIgnoreCase) || path.Equals(AppPaths.SharedRoot, StringComparison.OrdinalIgnoreCase));
    }

    internal static void InstallPayload(string bundled, string destination, string? current)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        string transaction = Guid.NewGuid().ToString("N");
        string staged = Path.Combine(parent, ".prepare-" + transaction);
        string previous = Path.Combine(parent, ".previous-" + transaction);
        try
        {
            CopyTree(bundled, staged, false, true);
            if (current != null)
            {
                ElevatedWorker.AssertProtectedTree(current);
                ElevatedWorker.PreserveSettings(current, staged);
            }
            ElevatedWorker.AssertProtectedTree(staged);
            if (Directory.Exists(destination)) Directory.Move(destination, previous);
            try { Directory.Move(staged, destination); }
            catch
            {
                if (Directory.Exists(previous)) Directory.Move(previous, destination);
                throw;
            }
            if (Directory.Exists(previous))
            {
                ElevatedWorker.AssertProtectedTree(previous);
                Directory.Delete(previous, true);
            }
        }
        finally
        {
            if (Directory.Exists(staged))
            {
                ElevatedWorker.AssertProtectedTree(staged);
                Directory.Delete(staged, true);
            }
        }
    }

    private static void RejectReparseChain(string path)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(path)); directory != null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки в пути назначения запрещены.");
        }
    }

    public static void CopyTree(string source, string destination, bool overwrite, bool protectedCopy = false)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("В пакете отсутствует каталог " + Path.GetFileName(source));
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Копирование ссылок запрещено.");
        RejectReparseChain(destination);
        if (protectedCopy) ElevatedWorker.CreateProtectedDirectory(destination);
        else Directory.CreateDirectory(destination);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Копирование ссылок запрещено.");
            string target = Path.Combine(destination, Path.GetFileName(entry));
            if ((File.Exists(target) || Directory.Exists(target)) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылка в каталоге назначения запрещена.");
            if (Directory.Exists(entry)) CopyTree(entry, target, overwrite, protectedCopy);
            else if (protectedCopy)
            {
                if (!overwrite && File.Exists(target)) throw new IOException("Файл уже существует: " + Path.GetFileName(target));
                ElevatedWorker.CopyProtectedFileAtomic(entry, target);
            }
            else File.Copy(entry, target, overwrite);
        }
    }

}
