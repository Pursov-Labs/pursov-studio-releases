using System.Security.Cryptography;
using System.Text.Json;
using PursovStudio;

using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
byte[] publicKey = key.ExportSubjectPublicKeyInfo();
byte[] package = "a test MSI payload"u8.ToArray();
var release = new AppReleaseManifest(1, "PURSOV_STUDIO", "stable", "win-x64", "2.1.0", "PURSOV_STUDIO_2.1.0_x64.msi", Convert.ToHexStringLower(SHA256.HashData(package)), package.Length);
int passed = 0;
AppReleaseManifest Verify(AppReleaseManifest item)
{
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(item);
    byte[] signature = key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    return ReleaseTrust.VerifyManifest(json, signature, publicKey, new Version(2, 0, 0), true);
}
void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or CryptographicException or JsonException) { passed++; return; }
    throw new Exception("An invalid release was accepted");
}
if (Verify(release) != release) throw new Exception("Valid release changed"); passed++;
Reject(() => Verify(release with { Product = "another-app" }));
Reject(() => Verify(release with { Channel = "preview" }));
Reject(() => Verify(release with { Architecture = "arm64" }));
Reject(() => Verify(release with { Version = "2.0.0" }));
Reject(() => Verify(release with { Version = "1.9.0" }));
Reject(() => Verify(release with { Version = "2.01.0" }));
Reject(() => Verify(release with { AssetName = "../../evil.msi" }));
Reject(() => Verify(release with { Size = ReleaseTrust.MaximumPackageSize + 1 }));
Reject(() => Verify(release with { Sha256 = "abc" }));
byte[] validJson = JsonSerializer.SerializeToUtf8Bytes(release);
byte[] validSignature = key.SignData(validJson, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
validJson[10] ^= 1;
Reject(() => ReleaseTrust.VerifyManifest(validJson, validSignature, publicKey, new Version(2, 0, 0), true));
Reject(() => ReleaseTrust.TransactionDirectory("test", "../escape"));
foreach (string url in new[] { "http://github.com/file", "https://github.com.evil.example/file", "https://user:password@github.com/file", "https://github.com:444/file", "file:///C:/file" })
{
    if (AppUpdateService.AllowedDownloadUri(new Uri(url))) throw new Exception("Unsafe download address accepted"); passed++;
}
if (!AppUpdateService.AllowedDownloadUri(new Uri("https://release-assets.githubusercontent.com/test"))) throw new Exception("GitHub release address rejected"); passed++;
string directory = Path.Combine(Path.GetTempPath(), "PursovReleaseTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string path = Path.Combine(directory, "package.msi");
    await File.WriteAllBytesAsync(path, package);
    await ReleaseTrust.VerifyPackageAsync(path, release, default); passed++;
    package[0] ^= 1; await File.WriteAllBytesAsync(path, package);
    Reject(() => ReleaseTrust.VerifyPackageAsync(path, release, default).GetAwaiter().GetResult());
    await File.WriteAllBytesAsync(path, new byte[1]);
    Reject(() => ReleaseTrust.VerifyPackageAsync(path, release, default).GetAwaiter().GetResult());
}
finally { Directory.Delete(directory, true); }
Console.WriteLine("PASS " + passed + " app release trust checks");
if (args.Length == 2 && args[0] == "--release")
{
    string releaseDirectory = Path.GetFullPath(args[1]);
    var productionRelease = ReleaseTrust.VerifyManifest(await File.ReadAllBytesAsync(Path.Combine(releaseDirectory, "release.json")), await File.ReadAllBytesAsync(Path.Combine(releaseDirectory, "release.sig")), false);
    await ReleaseTrust.VerifyPackageAsync(Path.Combine(releaseDirectory, productionRelease.AssetName), productionRelease, default);
    Console.WriteLine("PASS production signature and MSI hash for " + productionRelease.Version);
}
if (args.Length == 3 && args[0] == "--upgrade")
{
    string previousDirectory = Path.GetFullPath(args[1]);
    string nextDirectory = Path.GetFullPath(args[2]);
    byte[] previousJson = await File.ReadAllBytesAsync(Path.Combine(previousDirectory, "release.json"));
    byte[] previousSignature = await File.ReadAllBytesAsync(Path.Combine(previousDirectory, "release.sig"));
    byte[] nextJson = await File.ReadAllBytesAsync(Path.Combine(nextDirectory, "release.json"));
    byte[] nextSignature = await File.ReadAllBytesAsync(Path.Combine(nextDirectory, "release.sig"));
    var previous = ReleaseTrust.VerifyManifest(previousJson, previousSignature, false);
    var next = ReleaseTrust.VerifyManifest(nextJson, nextSignature, Convert.FromBase64String(ReleaseSigningKey.PublicKey), Version.Parse(previous.Version), true);
    await ReleaseTrust.VerifyPackageAsync(Path.Combine(previousDirectory, previous.AssetName), previous, default);
    await ReleaseTrust.VerifyPackageAsync(Path.Combine(nextDirectory, next.AssetName), next, default);
    Reject(() => ReleaseTrust.VerifyManifest(previousJson, previousSignature, Convert.FromBase64String(ReleaseSigningKey.PublicKey), Version.Parse(next.Version), true));
    Console.WriteLine("PASS two-package signature, version, hash and downgrade checks " + previous.Version + " -> " + next.Version);
}

namespace PursovStudio
{
    public static class AppPaths
    {
        public static string DataRoot => Path.GetTempPath();
        public static string StagingRoot => Path.Combine(DataRoot, "staging");
        public static string AppRoot => AppContext.BaseDirectory;
        public static string SharedRoot => Path.GetTempPath();
        public static T? ReadJson<T>(string path) => default;
        public static void WriteJson<T>(string path, T value) { }
    }
    public static class Log { public static void Write(string text) { } }
}
