using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

public sealed record AppReleaseManifest(int Schema, string Product, string Channel, string Architecture,
    string Version, string AssetName, string Sha256, long Size);

public static class ReleaseTrust
{
    public const string Repository = "Pursov-Labs/pursov-studio-releases";
    public const long MaximumPackageSize = 512L * 1024 * 1024;
    public static Version CurrentVersion
    {
        get
        {
            var version = typeof(ReleaseTrust).Assembly.GetName().Version ?? new Version(2, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    public static AppReleaseManifest VerifyManifest(byte[] json, byte[] signature, bool requireNewer = true) =>
        VerifyManifest(json, signature, Convert.FromBase64String(ReleaseSigningKey.PublicKey), CurrentVersion, requireNewer);

    internal static AppReleaseManifest VerifyManifest(byte[] json, byte[] signature, byte[] publicKey,
        Version currentVersion, bool requireNewer)
    {
        if (json.Length is 0 or > 16384 || signature.Length != 64)
            throw new InvalidDataException("Некорректный размер сведений об обновлении.");
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(publicKey, out int read);
        if (read != publicKey.Length || !verifier.VerifyData(json, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new CryptographicException("Подпись обновления не совпадает. Установка отменена.");
        var release = JsonSerializer.Deserialize<AppReleaseManifest>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            MaxDepth = 4,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Нет сведений об обновлении.");
        if (release.Schema != 1 || release.Product != "PURSOV_STUDIO" || release.Channel != "stable" || release.Architecture != "win-x64")
            throw new InvalidDataException("Обновление предназначено для другого приложения или компьютера.");
        if (release.Version == null || !Regex.IsMatch(release.Version, @"\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z") ||
            !Version.TryParse(release.Version, out var version) || version.Major > 255 || version.Minor > 255 || version.Build > 65535)
            throw new InvalidDataException("Некорректный номер версии.");
        if (requireNewer && version <= currentVersion)
            throw new InvalidDataException("Эта версия уже установлена или устарела.");
        if (release.AssetName != "PURSOV_STUDIO_" + release.Version + "_x64.msi" ||
            release.Sha256 == null || !Regex.IsMatch(release.Sha256, @"\A[a-f0-9]{64}\z") ||
            release.Size <= 0 || release.Size > MaximumPackageSize)
            throw new InvalidDataException("Некорректные сведения об установочном файле.");
        return release;
    }

    public static async Task VerifyPackageAsync(string path, AppReleaseManifest release, CancellationToken token)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Установочный файл не может быть ссылкой.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (stream.Length != release.Size) throw new InvalidDataException("Установочный файл загружен не полностью.");
        byte[] hash = await SHA256.HashDataAsync(stream, token);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(release.Sha256)))
            throw new CryptographicException("Установочный файл повреждён. Загрузите обновление ещё раз.");
    }

    public static string TransactionDirectory(string root, string transactionId)
    {
        if (!Guid.TryParseExact(transactionId, "N", out _)) throw new InvalidDataException("Некорректный номер обновления.");
        return Path.Combine(root, transactionId);
    }
}
