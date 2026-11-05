using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PursovStudio;

Assembly assembly = typeof(BrokerService).Assembly;
Type protocol = assembly.GetType("PursovStudio.BrokerProtocol")!;
Type request = assembly.GetType("PursovStudio.BrokerRequest")!;
MethodInfo validate = protocol.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!;
var cases = new[]
{
    ("start", "general (FAKE TLS AUTO)", "", true),
    ("status", "", "", true),
    ("stop", "", "", true),
    ("apply-update", "", new string('a', 32), true),
    ("run", "", "", false),
    ("start", "../evil", "", false),
    ("start", @"C:\evil.exe", "", false),
    ("start", "", "", false),
    ("start", new string('a', 151), "", false),
    ("stop", "arbitrary", "", false),
    ("status", "", "junk", false),
    ("apply-update", "", "../payload", false),
    ("apply-update", "", new string('A', 32), false),
    ("apply-update", "arbitrary", new string('a', 32), false)
};
foreach (var test in cases)
{
    bool accepted = true;
    try { validate.Invoke(null, new[] { Activator.CreateInstance(request, test.Item1, test.Item2, test.Item3) }); }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { accepted = false; }
    Assert(accepted == test.Item4, "command boundary " + test.Item1 + "/" + test.Item2);
}
foreach (string json in new[] { "{\"Command\":null}", "{\"Command\":\"start\",\"Strategy\":null}", "{\"Command\":\"apply-update\",\"Transaction\":null}" })
{
    try { validate.Invoke(null, new[] { JsonSerializer.Deserialize(json, request) }); throw new Exception("null accepted"); }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
}
Console.WriteLine("PASS constrained commands and null/path rejection");

var security = (PipeSecurity)typeof(BrokerService).GetMethod("CreatePipeSecurity", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
Assert(security.AreAccessRulesProtected, "protected pipe ACL");
Assert(Equals(security.GetOwner(typeof(SecurityIdentifier)), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)), "SYSTEM pipe owner");
foreach (PipeAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
{
    if (rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))) continue;
    Assert(rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null)), "interactive clients only");
    Assert((rule.PipeAccessRights & (PipeAccessRights.CreateNewInstance | PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership)) == 0,
        "client cannot replace pipe server");
}
Console.WriteLine("PASS pipe ownership and server-creation denial");

MethodInfo read = typeof(ElevatedWorker).GetMethod("ReadLineAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (string text in new[] { new string('a', 4097) + "\n", "partial" })
{
    using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
    try { await (Task<string?>)read.Invoke(null, new object[] { reader, 4096, CancellationToken.None })!; throw new Exception("invalid frame accepted"); }
    catch (InvalidDataException) { }
}
using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("valid\r\n"))))
    Assert(await (Task<string?>)read.Invoke(null, new object[] { reader, 4096, CancellationToken.None })! == "valid", "valid frame");
Console.WriteLine("PASS bounded complete IPC frames");

Assert(!BrokerService.TryRun(new[] { "--service", "injected" }), "service accepts no arguments");
Console.WriteLine("PASS service entry argument boundary");

MethodInfo updateGuard = typeof(BrokerService).GetMethod("AssertNoUpdateInProgress", BindingFlags.Static | BindingFlags.NonPublic)!;
using (var current = System.Diagnostics.Process.GetCurrentProcess())
{
    long ticks = current.StartTime.ToUniversalTime().Ticks;
    try
    {
        updateGuard.Invoke(null, new object[] { new BrokerInstallReceipt(current.Id, ticks) });
        throw new Exception("active installer did not block connection");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
    updateGuard.Invoke(null, new object[] { new BrokerInstallReceipt(current.Id, ticks - 1) });
    updateGuard.Invoke(null, new object[] { new BrokerInstallReceipt(int.MaxValue, ticks) });
    updateGuard.Invoke(null, new object?[] { null });
}
Console.WriteLine("PASS pending installation blocks new sessions and ignores stale process identity");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
