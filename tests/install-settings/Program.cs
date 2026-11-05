using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using PursovStudio;

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Run this isolated ACL regression with administrator rights.");

string root = Path.Combine(Path.GetTempPath(), "pursov-install-settings-" + Guid.NewGuid().ToString("N"));
var security = new DirectorySecurity();
var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
security.SetOwner(admins);
security.SetAccessRuleProtection(true, false);
foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
new DirectoryInfo(root).Create(security);

MethodInfo install = typeof(InstallService).GetMethod("InstallPayload", BindingFlags.Static | BindingFlags.NonPublic)!;
try
{
    string bundled = Path.Combine(root, "bundled");
    string installed = Path.Combine(root, "installed");
    foreach (string folder in new[] { bundled, installed })
        foreach (string name in new[] { "lists", "utils", "bin" }) Directory.CreateDirectory(Path.Combine(folder, name));
    string[] settings = { "lists/ipset-exclude-user.txt", "lists/list-general-user.txt", "lists/list-exclude-user.txt", "lists/ipset-all.txt",
        "lists/ipset-all.txt.backup", "utils/targets.txt" };
    foreach (string relative in settings)
    {
        File.WriteAllText(Path.Combine(bundled, relative), "default");
        File.WriteAllText(Path.Combine(installed, relative), "custom:" + relative);
    }
    File.WriteAllText(Path.Combine(bundled, "utils/game_filter.enabled"), "mode=all");
    File.WriteAllText(Path.Combine(bundled, "bin/winws.exe"), "new executable fixture");
    File.WriteAllText(Path.Combine(installed, "bin/winws.exe"), "old executable fixture");
    File.WriteAllText(Path.Combine(installed, "bin/resource.bin"), "old resource");
    File.WriteAllText(Path.Combine(installed, "bin/ACTIVE_DISCORD_UDP.bin"), "old resource");
    File.WriteAllText(Path.Combine(bundled, "bin/resource.bin"), "new resource");

    install.Invoke(null, new object?[] { bundled, installed, installed });
    foreach (string relative in settings) Assert(File.ReadAllText(Path.Combine(installed, relative)) == "custom:" + relative, relative);
    Assert(!File.Exists(Path.Combine(installed, "utils/game_filter.enabled")), "disabled game filter preserved");
    Assert(File.ReadAllText(Path.Combine(installed, "bin/ACTIVE_DISCORD_UDP.bin")) == "new resource", "selected resource mapped to new payload");
    Assert(File.ReadAllText(Path.Combine(installed, "bin/winws.exe")) == "new executable fixture", "executable replaced");
    Console.WriteLine("PASS same-version reinstall preserves user lists, disabled filter and selected resource");

    File.WriteAllText(Path.Combine(installed, "utils/game_filter.enabled"), "mode=tcp\ntcp=443");
    string next = Path.Combine(root, "next-version");
    install.Invoke(null, new object?[] { bundled, next, installed });
    foreach (string relative in settings) Assert(File.ReadAllText(Path.Combine(next, relative)) == "custom:" + relative, relative);
    Assert(File.ReadAllText(Path.Combine(next, "utils/game_filter.enabled")) == "mode=tcp\ntcp=443", "enabled filter preserved");
    Console.WriteLine("PASS new-version install preserves active version settings");

    string retiredFixture = Path.Combine(root, ".previous-lock-fixture");
    Directory.CreateDirectory(Path.Combine(retiredFixture, "bin"));
    string retiredDriver = Path.Combine(retiredFixture, "bin/WinDivert64.sys");
    File.WriteAllText(retiredDriver, "retired driver fixture");
    MethodInfo cleanup = typeof(InstallService).GetMethod("CleanupRetiredPayload", BindingFlags.Static | BindingFlags.NonPublic)!;
    using (var locked = new FileStream(retiredDriver, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        cleanup.Invoke(null, new object[] { retiredFixture });
        Assert(File.Exists(retiredDriver), "locked retired driver retained");
        try { Directory.Delete(retiredFixture, true); throw new Exception("fixture did not hold a real delete lock"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    cleanup.Invoke(null, new object[] { retiredFixture });
    Assert(!Directory.Exists(retiredFixture), "retired payload cleaned after handle release");
    Console.WriteLine("PASS real locked retired file is retained and cleanup succeeds after release");

    File.Delete(Path.Combine(bundled, "bin/resource.bin"));
    try { install.Invoke(null, new object?[] { bundled, installed, installed }); throw new Exception("unsupported resource accepted"); }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
    Assert(File.ReadAllText(Path.Combine(installed, "bin/ACTIVE_DISCORD_UDP.bin")) == "new resource", "failed preparation leaves installed payload intact");
    Assert(!Directory.EnumerateDirectories(root, ".prepare-*").Any(), "staging cleaned");
    Console.WriteLine("PASS failed preparation retains previous payload");
}
finally
{
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new Exception("cleanup boundary");
    Directory.Delete(root, true);
}

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
