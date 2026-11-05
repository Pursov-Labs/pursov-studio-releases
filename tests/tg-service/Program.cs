using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using PursovStudio;

var protect = typeof(TgService).GetMethod("Protect", BindingFlags.NonPublic | BindingFlags.Static)!;
byte[] original = RandomNumberGenerator.GetBytes(48);
byte[] encrypted = (byte[])protect.Invoke(null, new object[] { original, true })!;
byte[] restored = (byte[])protect.Invoke(null, new object[] { encrypted, false })!;
if (encrypted.SequenceEqual(original) || !restored.SequenceEqual(original)) throw new Exception("DPAPI roundtrip failed");
Console.WriteLine("PASS: Windows DPAPI protects and restores config bytes");

string python = Path.GetFullPath(args[0]);
var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
start.ArgumentList.Add("-c"); start.ArgumentList.Add("import time;time.sleep(30)");
using var process = Process.Start(start)!;
var jobType = typeof(TgService).GetNestedType("ProcessJob", BindingFlags.NonPublic)!;
using var job = (IDisposable)Activator.CreateInstance(jobType, new object[] { process })!;
job.Dispose();
if (!process.WaitForExit(3000)) { process.Kill(true); throw new Exception("Job object left worker running"); }
Console.WriteLine("PASS: job-object close terminates owned Python worker");

var validate = typeof(TgService).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static)!;
var cfg = System.Text.Json.Nodes.JsonNode.Parse("{\"host\":\"0.0.0.0\",\"port\":1443,\"secret\":\"00112233445566778899aabbccddeeff\"}")!.AsObject();
try { validate.Invoke(null, new object[] { cfg }); throw new Exception("Non-loopback config accepted"); }
catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { }
Console.WriteLine("PASS: non-loopback TG listener rejected");
