using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

public sealed record UpdateTransaction(string Component, string PreviousVersion, string NewVersion, string State, DateTimeOffset Time);

public sealed class UpdateService : IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = TimeSpan.FromMinutes(3) };
    public UpdateService()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PursovStudio/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<IReadOnlyList<UpdateCandidate>> CheckAsync(string zapretVersion, string tgVersion, CancellationToken token)
    {
        var tasks = new[] { GetCandidate("zapret", "zapret-discord-youtube", zapretVersion, token), GetCandidate("tg", "tg-ws-proxy", tgVersion, token) };
        return (await Task.WhenAll(tasks)).Where(x => x != null).Cast<UpdateCandidate>().ToArray();
    }

    private async Task<UpdateCandidate?> GetCandidate(string component, string repo, string current, CancellationToken token)
    {
        using var release = await GetJson("https://api.github.com/repos/Flowseal/" + repo + "/releases/latest", token);
        string tag = release.RootElement.GetProperty("tag_name").GetString()!;
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$")) throw new IOException("Неизвестный формат версии " + component);
        if (!IsNewer(tag, current)) return null;
        if (release.RootElement.GetProperty("draft").GetBoolean() || release.RootElement.GetProperty("prerelease").GetBoolean()) return null;
        if (component == "zapret")
        {
            string expected = "zapret-discord-youtube-" + tag.TrimStart('v') + ".zip";
            foreach (var asset in release.RootElement.GetProperty("assets").EnumerateArray())
                if (asset.GetProperty("name").GetString() == expected)
                {
                    string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) throw new IOException("У релиза Zapret нет опубликованного SHA256; установка отложена.");
                    return new(component, tag.TrimStart('v'), expected, asset.GetProperty("browser_download_url").GetString()!, digest, asset.GetProperty("size").GetInt64());
                }
            throw new IOException("Официальный ZIP Zapret не найден.");
        }
        using var commit = await GetJson("https://api.github.com/repos/Flowseal/" + repo + "/commits/" + Uri.EscapeDataString(tag), token);
        string sha = commit.RootElement.GetProperty("sha").GetString()!;
        if (!Regex.IsMatch(sha, "^[0-9a-f]{40}$")) throw new IOException("Некорректный commit TG.");
        return new(component, tag.TrimStart('v'), "source.zip", "https://api.github.com/repos/Flowseal/tg-ws-proxy/zipball/" + sha, null, 0, sha);
    }

    public static bool IsNewer(string candidate, string current) => Version.TryParse(candidate.TrimStart('v'), out var a) && Version.TryParse(current.TrimStart('v'), out var b) && a > b;

    public async Task<string> StageAsync(UpdateCandidate candidate, Action<string> progress, CancellationToken token)
    {
        string transaction = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(AppPaths.StagingRoot, transaction);
        Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, "payload.zip");
        using var response = await Send(candidate.DownloadUrl, token);
        response.EnsureSuccessStatusCode();
        long limit = candidate.Component == "zapret" ? 30_000_000 : 80_000_000;
        using (var input = await response.Content.ReadAsStreamAsync(token))
        using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            byte[] buffer = new byte[65536]; long total = 0;
            while (true)
            {
                int count = await input.ReadAsync(buffer, token); if (count == 0) break;
                total += count; if (total > limit) throw new IOException("Пакет превышает допустимый размер.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                progress("Загружаю " + candidate.Component + ": " + (total / 1024) + " КБ");
            }
            if (candidate.Size > 0 && total != candidate.Size) throw new IOException("Размер пакета не совпадает с официальным релизом.");
        }
        using (var input = File.OpenRead(zip))
        {
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
            if (candidate.Digest != null && !string.Equals("sha256:" + actual, candidate.Digest, StringComparison.OrdinalIgnoreCase)) throw new IOException("Контрольная сумма пакета не совпадает.");
            AppPaths.WriteJson(Path.Combine(folder, "download.json"), new { Candidate = candidate, Sha256 = actual, Downloaded = DateTimeOffset.UtcNow });
        }
        if (candidate.Component == "tg")
        {
            progress("Проверяю исходники TG по официальному Git tree…");
            string unpack = Path.Combine(folder, "unpacked");
            ExtractZip(zip, unpack, true);
            await VerifyTgTree(unpack, candidate.Commit, token);
            if (!File.Exists(Path.Combine(unpack, "proxy", "tg_ws_proxy.py"))) throw new IOException("В пакете нет ядра TG.");
            string target = Path.Combine(AppPaths.TgRoot, candidate.Version);
            string ready = Path.Combine(AppPaths.TgRoot, ".prepare-" + transaction);
            if (Directory.Exists(target)) throw new IOException("Версия TG уже находится в каталоге. Повторная активация выполняется после проверки предыдущей транзакции.");
            try
            {
                Directory.CreateDirectory(ready);
                InstallService.CopyTree(Path.Combine(unpack, "proxy"), Path.Combine(ready, "proxy"), false);
                if (File.Exists(Path.Combine(unpack, "LICENSE"))) File.Copy(Path.Combine(unpack, "LICENSE"), Path.Combine(ready, "LICENSE"));
                AppPaths.WriteJson(Path.Combine(ready, "version.json"), new { Version = candidate.Version, Commit = candidate.Commit });
                Directory.Move(ready, target);
            }
            finally { if (Directory.Exists(ready)) Directory.Delete(ready, true); }
        }
        return transaction;
    }

    private async Task VerifyTgTree(string unpack, string commit, CancellationToken token)
    {
        using var tree = await GetJson("https://api.github.com/repos/Flowseal/tg-ws-proxy/git/trees/" + commit + "?recursive=1", token);
        if (tree.RootElement.GetProperty("truncated").GetBoolean()) throw new IOException("Неполный Git tree TG.");
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in tree.RootElement.GetProperty("tree").EnumerateArray())
        {
            string path = item.GetProperty("path").GetString()!;
            if (!path.StartsWith("proxy/", StringComparison.Ordinal) && path != "LICENSE") continue;
            if (item.GetProperty("type").GetString() != "blob") continue;
            if (item.GetProperty("mode").GetString() == "120000") throw new IOException("Ссылки в пакете TG запрещены.");
            expected[path] = item.GetProperty("sha").GetString()!;
        }
        foreach (string file in Directory.EnumerateFiles(Path.Combine(unpack, "proxy"), "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(unpack, file).Replace('\\', '/');
            if (!expected.Remove(relative, out string? sha)) throw new IOException("Неизвестный исполняемый исходник TG: " + relative);
            if (!GitBlobHash(File.ReadAllBytes(file)).Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new IOException("Git blob не совпадает: " + relative);
        }
        expected.Remove("LICENSE", out string? licenseSha);
        if (licenseSha != null && (!File.Exists(Path.Combine(unpack, "LICENSE")) || GitBlobHash(File.ReadAllBytes(Path.Combine(unpack, "LICENSE"))) != licenseSha)) throw new IOException("Не совпадает лицензия TG.");
        if (expected.Count != 0) throw new IOException("В архиве отсутствует часть исходников TG.");
    }

    public static string GitBlobHash(byte[] bytes)
    {
        byte[] header = Encoding.UTF8.GetBytes("blob " + bytes.Length + "\0");
        byte[] data = new byte[header.Length + bytes.Length]; header.CopyTo(data, 0); bytes.CopyTo(data, header.Length);
        return Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
    }

    public static void ExtractZip(string zip, string destination, bool stripRoot)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 10000) throw new IOException("Слишком много файлов в архиве.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (stripRoot) { int slash = name.IndexOf('/'); if (slash < 0) continue; name = name[(slash + 1)..]; }
            if (name.Length == 0) continue;
            if (name.Split('/').Any(p => p == ".." || p.Contains(':')) || name.StartsWith('/') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new IOException("Небезопасный путь в ZIP.");
            string path = Path.GetFullPath(Path.Combine(destination, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("ZIP выходит за каталог распаковки.");
            total += entry.Length; if (total > 150_000_000 || entry.Length > 30_000_000) throw new IOException("Слишком большой распакованный архив.");
            if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open(); using var output = new FileStream(path, FileMode.CreateNew);
            input.CopyTo(output);
        }
    }

    private async Task<JsonDocument> GetJson(string url, CancellationToken token)
    {
        using var response = await Send(url, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 10_000_000) throw new IOException("Слишком большой ответ API.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }

    private async Task<HttpResponseMessage> Send(string url, CancellationToken token)
    {
        for (int i = 0; i < 6; i++)
        {
            var uri = new Uri(url);
            bool host = uri.Host == "api.github.com" || uri.Host == "github.com" || uri.Host == "codeload.github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
            if (uri.Scheme != "https" || !host) throw new IOException("Недопустимый источник обновления.");
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location == null) throw new IOException("Пустое перенаправление обновления.");
                url = new Uri(uri, location).AbsoluteUri; continue;
            }
            return response;
        }
        throw new IOException("Слишком много перенаправлений.");
    }

    public static string JournalFile => Path.Combine(AppPaths.DataRoot, "update-transaction.json");
    public static void Journal(UpdateTransaction value) => AppPaths.WriteJson(JournalFile, value);
    public void Dispose() => http.Dispose();
}
