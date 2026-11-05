using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PursovStudio;

public sealed class ProbeService : IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.Zero, ConnectTimeout = TimeSpan.FromSeconds(5), AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(12) };
    private sealed record MediaTarget(Uri Url, bool IsVideo, string MimeType, DateTimeOffset Expires);
    private MediaTarget? mediaTarget;
    private const string MediaAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
    public ProbeService() { http.DefaultRequestHeaders.UserAgent.ParseAdd("PursovStudio/2.0"); }

    public async Task<IReadOnlyList<ProbeResult>> CheckLightAsync(IReadOnlyList<ProbeResult> previous, CancellationToken token)
    {
        // Only refresh the small API check in the background. Media/MTProto remain explicit diagnostics.
        var check = await Measure("Discord · сайт/API", async ct =>
        {
            using var response = await http.GetAsync("https://discord.com/api/v10/gateway", ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            bool valid = json.RootElement.TryGetProperty("url", out var url)
                && url.GetString()?.StartsWith("wss://gateway.discord.gg", StringComparison.Ordinal) == true;
            return (valid, valid ? "Публичный API отвечает · " + DateTimeOffset.Now.ToString("HH:mm") : "Неожиданный ответ API");
        }, token);
        return previous.Select(x => x.Service == check.Service ? check : x).ToArray();
    }

    public async Task<IReadOnlyList<ProbeResult>> CheckAsync(ITgService tg, CancellationToken token)
    {
        var tasks = new[]
        {
            Measure("Интернет", async ct => { using var r = await http.GetAsync("https://www.gstatic.com/generate_204", ct); return r.StatusCode == HttpStatusCode.NoContent ? (true, "HTTPS-контроль пройден") : (false, "Неожиданный HTTP " + (int)r.StatusCode); }, token),
            TgProbe(tg, token),
            Measure("Discord · сайт/API", async ct => { using var r = await http.GetAsync("https://discord.com/api/v10/gateway", ct); r.EnsureSuccessStatusCode(); using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)); return j.RootElement.TryGetProperty("url", out var u) && u.GetString()!.StartsWith("wss://gateway.discord.gg", StringComparison.Ordinal) ? (true, "Публичный API отвечает корректно") : (false, "Неожиданный ответ API"); }, token),
            Measure("Discord · соединение", DiscordGateway, token),
            Task.FromResult(new ProbeResult("Discord · голос", "Не проверен", null, 0, "Требуется реальный двусторонний звонок. HTTPS и Gateway не проверяют UDP-голос.")),
            Measure("YouTube · сайт", async ct => { using var r = await http.GetAsync("https://www.youtube.com/", ct); r.EnsureSuccessStatusCode(); string s = await r.Content.ReadAsStringAsync(ct); return s.Contains("ytInitialData", StringComparison.Ordinal) || s.Contains("INNERTUBE", StringComparison.Ordinal) ? (true, "Получена страница YouTube") : (false, "Не распознана страница YouTube"); }, token),
            CheckVideoAsync(token)
        };
        return await Task.WhenAll(tasks);
    }

    private static async Task<ProbeResult> TgProbe(ITgService tg, CancellationToken token)
    {
        try { return await tg.ProbeAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new("Telegram", "Нет связи", false, 0, ex.Message); }
    }

    private static async Task<ProbeResult> Measure(string name, Func<CancellationToken, Task<(bool Ok, string Detail)>> test, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var (ok, detail) = await test(timeout.Token);
            return new(name, ok ? "Доступен" : "Нет связи", ok, sw.Elapsed.TotalMilliseconds, detail);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(name, "Нет связи", false, sw.Elapsed.TotalMilliseconds, ex is OperationCanceledException ? "Превышено время ожидания" : ex.Message); }
    }

    private static async Task<(bool, string)> DiscordGateway(CancellationToken token)
    {
        using var ws = new ClientWebSocket();
        ws.Options.Proxy = null;
        await ws.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), token);
        byte[] buffer = new byte[16384];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
        if (!result.EndOfMessage || result.Count == 0) return (false, "Неполный ответ Gateway");
        using var j = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        bool valid = j.RootElement.GetProperty("op").GetInt32() == 10 && j.RootElement.GetProperty("d").GetProperty("heartbeat_interval").GetInt32() > 0;
        if (valid) { byte[] heartbeat = Encoding.UTF8.GetBytes("{\"op\":1,\"d\":null}"); await ws.SendAsync(new ArraySegment<byte>(heartbeat), WebSocketMessageType.Text, true, token); }
        ws.Abort();
        return (valid, valid ? "WebSocket Hello получен; пользовательский токен не используется" : "Неожиданный ответ Gateway");
    }

    private async Task<ProbeResult> CheckVideoAsync(CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false, UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5), AutomaticDecompression = DecompressionMethods.All
            }) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(MediaAgent);
            var target = mediaTarget;
            if (target == null || target.Expires <= DateTimeOffset.UtcNow)
                target = await FindMediaTargetAsync(client, false, timeout.Token);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (target == null)
                    return new("YouTube · видео", "Не проверено", null, sw.Elapsed.TotalMilliseconds, "YouTube не выдал поддерживаемый прямой media URL. Доступность сайта не считается проверкой видео.");
                using var response = await SendMediaRequestAsync(client, target.Url, timeout.Token);
                if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Gone)
                {
                    mediaTarget = null;
                    if (attempt == 0) { target = await FindMediaTargetAsync(client, true, timeout.Token); continue; }
                    return new("YouTube · видео", "Не проверено", null, sw.Elapsed.TotalMilliseconds, "CDN отклонил диагностическую ссылку; это может быть ограничением проигрывателя, а не сети.");
                }
                if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
                    return new("YouTube · видео", "Нет связи", false, sw.Elapsed.TotalMilliseconds, "Media CDN HTTP " + (int)response.StatusCode);
                string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                byte[] buffer = new byte[65536]; int count = 0;
                while (count < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token);
                    if (read == 0) break;
                    count += read;
                }
                bool valid = string.Equals(mediaType, target.MimeType, StringComparison.OrdinalIgnoreCase)
                    && IsMediaFragment(buffer.AsSpan(0, count), mediaType, target.IsVideo);
                if (!valid)
                    return new("YouTube · видео", "Нет связи", false, sw.Elapsed.TotalMilliseconds, "Ответ CDN не содержит ожидаемый фрагмент MP4/WebM.");
                mediaTarget = target with { Url = response.RequestMessage?.RequestUri ?? target.Url };
                return new("YouTube · видео", target.IsVideo ? "Видеофрагмент получен" : "Аудиофрагмент получен", target.IsVideo ? true : null,
                    sw.Elapsed.TotalMilliseconds, "Получено " + count + " байт " + mediaType + (target.IsVideo
                        ? "; проверены данные видеоконтейнера, длительное воспроизведение проверяется отдельно."
                        : "; проверена доставка аудио, доставка видео не подтверждена."));
            }
            return new("YouTube · видео", "Не проверено", null, sw.Elapsed.TotalMilliseconds, "Media-проверка не завершена.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            mediaTarget = null;
            return new("YouTube · видео", ex is JsonException or InvalidDataException ? "Не проверено" : "Нет связи",
                ex is JsonException or InvalidDataException ? null : false, sw.Elapsed.TotalMilliseconds,
                ex is OperationCanceledException ? "Превышено время media-проверки" : ex is JsonException or InvalidDataException
                    ? "Формат данных проигрывателя изменился; проверьте воспроизведение в браузере." : "Не удалось получить данные media CDN.");
        }
    }

    private static async Task<HttpResponseMessage> SendMediaRequestAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        if (!IsMediaUri(uri)) throw new InvalidDataException("Недопустимый адрес media CDN.");
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 65535);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            Uri? next = ResolveMediaRedirect(uri, response.Headers.Location?.OriginalString);
            response.Dispose();
            if (redirects == 4 || next == null) throw new InvalidDataException("Перенаправление media CDN не прошло проверку.");
            uri = next;
        }
        throw new InvalidDataException("Превышено число перенаправлений media CDN.");
    }

    private static bool IsMediaUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);

    private static Uri? ResolveMediaRedirect(Uri current, string? location)
    {
        if (!IsMediaUri(current) || string.IsNullOrWhiteSpace(location) || location.Length > 16384
            || !Uri.TryCreate(current, location, out var target) || !IsMediaUri(target)) return null;
        return target;
    }

    private static async Task<MediaTarget?> FindMediaTargetAsync(HttpClient client, bool preferVision, CancellationToken token)
    {
        using var watch = await client.GetAsync("https://www.youtube.com/watch?v=jNQXAC9IVRw", HttpCompletionOption.ResponseHeadersRead, token);
        if (!watch.IsSuccessStatusCode) return null;
        string html = await ReadPlayerBodyAsync(watch.Content, 4 * 1024 * 1024, token);
        string? initial = ExtractPlayerJson(html);
        var format = initial != null ? SelectMediaUrl(initial) : (null, false, "");
        if (preferVision || format.Item1 == null)
        {
            var context = ExtractVideoContext(html);
            if (context.Visitor == null || context.Timestamp == null) return null;
            // shortcut: YouTube's private VISIONOS profile can change; update this probe when direct URLs stop being issued.
            var body = new
            {
                context = new
                {
                    client = new
                    {
                        clientName = "VISIONOS", clientVersion = "1.02", userAgent = MediaAgent, visitorData = context.Visitor,
                        hl = "en", gl = "US", deviceMake = "Apple", deviceModel = "RealityDevice17,1", osName = "visionOS", osVersion = "26.5.23O471"
                    }
                },
                videoId = "jNQXAC9IVRw", contentCheckOk = true,
                playbackContext = new { contentPlaybackContext = new { html5Preference = "HTML5_PREF_WANTS", signatureTimestamp = context.Timestamp.Value } }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", "https://www.youtube.com");
            request.Headers.Add("X-Youtube-Client-Name", "101");
            request.Headers.Add("X-Youtube-Client-Version", "1.02");
            request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", context.Visitor);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return null;
            format = SelectMediaUrl(await ReadPlayerBodyAsync(response.Content, 1024 * 1024, token));
        }
        return format.Item1 == null ? null : new MediaTarget(format.Item1, format.Item2, format.Item3, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static async Task<string> ReadPlayerBodyAsync(HttpContent content, int limit, CancellationToken token)
    {
        using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Ответ проигрывателя слишком велик.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static (string? Visitor, int? Timestamp) ExtractVideoContext(string html)
    {
        string? visitor = null;
        foreach (string key in new[] { "VISITOR_DATA", "visitorData" })
        {
            var match = Regex.Match(html, "\"" + key + "\"\\s*:\\s*(\"(?:\\\\.|[^\"\\\\])*\")", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) continue;
            try { visitor = JsonSerializer.Deserialize<string>(match.Groups[1].Value); } catch (JsonException) { }
            if (visitor != null) break;
        }
        if (visitor == null || visitor.Length < 1 || visitor.Length > 4096 || visitor.Any(char.IsControl)) visitor = null;
        var stamp = Regex.Match(html, "\"STS\"\\s*:\\s*([0-9]{1,6})(?![0-9])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        int? timestamp = stamp.Success && int.TryParse(stamp.Groups[1].Value, out int value) && value > 0 ? value : null;
        return (visitor, timestamp);
    }

    public static (Uri? Url, bool IsVideo, string MimeType) SelectMediaUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, false, "");
            if (!document.RootElement.TryGetProperty("streamingData", out var streaming) || streaming.ValueKind != JsonValueKind.Object) return (null, false, "");
            var found = new List<(Uri Url, bool IsVideo, string MimeType, long Bitrate)>();
            foreach (string kind in new[] { "formats", "adaptiveFormats" })
            {
                if (!streaming.TryGetProperty(kind, out var formats) || formats.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in formats.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String
                        || !item.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String) continue;
                    string type = mime.GetString()!.Split(';')[0].Trim().ToLowerInvariant();
                    if (type is not ("video/mp4" or "video/webm" or "audio/mp4" or "audio/webm")) continue;
                    if (!Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || !IsMediaUri(uri)) continue;
                    long bitrate = item.TryGetProperty("bitrate", out var rate) && rate.ValueKind == JsonValueKind.Number
                        && rate.TryGetInt64(out long parsed) && parsed > 0 ? parsed : long.MaxValue;
                    found.Add((uri, type.StartsWith("video/", StringComparison.Ordinal), type, bitrate));
                }
            }
            var selected = found.OrderByDescending(f => f.IsVideo).ThenBy(f => f.Bitrate).FirstOrDefault();
            return selected.Url == null ? (null, false, "") : (selected.Url, selected.IsVideo, selected.MimeType);
        }
        catch (JsonException) { return (null, false, ""); }
    }

    private static bool IsMediaFragment(ReadOnlySpan<byte> bytes, string type, bool video)
    {
        if (bytes.Length < 4096 || !(video ? type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) : type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))) return false;
        return type.EndsWith("/webm", StringComparison.OrdinalIgnoreCase)
            ? bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3
            : type.EndsWith("/mp4", StringComparison.OrdinalIgnoreCase) && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p';
    }

    public static IReadOnlyList<string> VideoSelfTest()
    {
        var context = ExtractVideoContext("x {\"VISITOR_DATA\":\"ab\\u003d\",\"STS\":20758} y");
        if (context.Visitor != "ab=" || context.Timestamp != 20758 || ExtractVideoContext("{}").Visitor != null
            || ExtractVideoContext("{\"VISITOR_DATA\":\"a\\r\\nb\",\"STS\":0}").Visitor != null) throw new Exception("Ошибка контекста проигрывателя.");
        var source = new
        {
            streamingData = new
            {
                adaptiveFormats = new[]
                {
                    new { url = "https://rr1.googlevideo.com/audio", mimeType = "audio/webm", bitrate = 1 },
                    new { url = "https://rr1.googlevideo.com/video", mimeType = "video/mp4; codecs=avc1", bitrate = 2 },
                    new { url = "https://rr1.googlevideo.com/large", mimeType = "video/mp4", bitrate = 3 }
                }
            }
        };
        var chosen = SelectMediaUrl(JsonSerializer.Serialize(source));
        if (!chosen.IsVideo || chosen.Url?.AbsolutePath != "/video") throw new Exception("Видео не имеет приоритета перед аудио.");
        foreach (string url in new[] { "http://rr1.googlevideo.com/a", "https://rr1.googlevideo.com.evil.test/a", "https://name@rr1.googlevideo.com/a", "https://rr1.googlevideo.com:8443/a" })
        {
            string json = JsonSerializer.Serialize(new { streamingData = new { formats = new[] { new { url, mimeType = "video/mp4" } } } });
            if (SelectMediaUrl(json).Url != null) throw new Exception("Небезопасный CDN URL принят.");
        }
        foreach (string json in new[] { "{}", "null", "[]", "broken", "{\"streamingData\":{\"formats\":[{\"signatureCipher\":\"x\"}]}}" })
            if (SelectMediaUrl(json).Url != null) throw new Exception("Неподдерживаемый ответ принят как media URL.");
        var origin = new Uri("https://rr1.googlevideo.com/videoplayback?x=1");
        if (ResolveMediaRedirect(origin, "/videoplayback?x=2")?.Query != "?x=2"
            || ResolveMediaRedirect(origin, "https://rr2.googlevideo.com/media")?.Host != "rr2.googlevideo.com")
            throw new Exception("Допустимое перенаправление CDN не распознано.");
        foreach (string location in new[] { "https://outside.test/media", "//outside.test/media", "http://rr2.googlevideo.com/media",
            "https://name@rr2.googlevideo.com/media", "https://rr2.googlevideo.com:8443/media", "#fragment", "https://rr2.googlevideo.com.evil.test/media", "" })
            if (ResolveMediaRedirect(origin, location) != null) throw new Exception("Недопустимое перенаправление CDN принято.");
        byte[] fragment = new byte[4096]; fragment[0] = 0x1A; fragment[1] = 0x45; fragment[2] = 0xDF; fragment[3] = 0xA3;
        if (!IsMediaFragment(fragment, "video/webm", true) || IsMediaFragment(fragment, "audio/webm", true)
            || IsMediaFragment(fragment.AsSpan(0, 100), "video/webm", true) || IsMediaFragment(new byte[4096], "video/webm", true))
            throw new Exception("Неверная проверка media-контейнера.");
        return new[] { "YouTube: JSON-контекст, приоритет видео, безопасный CDN и перенаправления, отсутствие cipher fallback и сигнатура контейнера" };
    }

    public static string? ExtractPlayerJson(string html)
    {
        const string marker = "ytInitialPlayerResponse";
        int at = html.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = html.IndexOf('{', at + marker.Length);
        if (start < 0) return null;
        bool quoted = false, escaped = false; int depth = 0;
        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];
            if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; }
            else if (c == '"') quoted = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return html[start..(i + 1)];
        }
        return null;
    }
    public void Dispose() => http.Dispose();
}
