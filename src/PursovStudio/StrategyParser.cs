using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PursovStudio;

public static class StrategyParser
{
    private static readonly HashSet<string> Options = new(StringComparer.Ordinal)
    {
        "wf-tcp", "wf-udp", "filter-tcp", "filter-udp", "filter-l3", "filter-l7", "hostlist", "hostlist-domains",
        "hostlist-exclude", "hostlist-exclude-domains", "ipset", "ipset-exclude", "ip-id", "new", "dpi-desync",
        "dpi-desync-any-protocol", "dpi-desync-badseq-increment", "dpi-desync-cutoff", "dpi-desync-fake-discord",
        "dpi-desync-fake-http", "dpi-desync-fake-quic", "dpi-desync-fake-stun", "dpi-desync-fake-tls",
        "dpi-desync-fake-tls-mod", "dpi-desync-fake-unknown", "dpi-desync-fake-unknown-udp", "dpi-desync-fakedsplit-pattern",
        "dpi-desync-fooling", "dpi-desync-hostfakesplit-mod", "dpi-desync-repeats", "dpi-desync-split-pos",
        "dpi-desync-split-seqovl", "dpi-desync-split-seqovl-pattern"
    };
    private static readonly HashSet<string> FileOptions = new(StringComparer.Ordinal)
    {
        "hostlist", "hostlist-exclude", "ipset", "ipset-exclude", "dpi-desync-fake-discord", "dpi-desync-fake-http",
        "dpi-desync-fake-quic", "dpi-desync-fake-stun", "dpi-desync-fake-tls", "dpi-desync-fake-unknown",
        "dpi-desync-fake-unknown-udp", "dpi-desync-fakedsplit-pattern", "dpi-desync-split-seqovl-pattern"
    };
    private static readonly HashSet<string> Header = new(StringComparer.Ordinal)
    {
        "@echo off", "chcp 65001 > nul", ":: 65001 - UTF-8", "cd /d \"%~dp0\"", "call service.bat status_zapret",
        "call service.bat check_updates", "call service.bat load_game_filter", "call service.bat load_user_lists",
        "echo:", "set \"BIN=%~dp0bin\\\"", "set \"LISTS=%~dp0lists\\\"", "cd /d %BIN%"
    };
    private const string Start = "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" ";

    public static IReadOnlyList<StrategyInfo> List(string directory) => Directory.EnumerateFiles(directory, "general*.bat")
        .Select(p => new StrategyInfo(Path.GetFileName(p), Path.GetFileNameWithoutExtension(p)))
        .OrderBy(p => p.Id == "general.bat" ? "" : p.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> Parse(string directory, string strategyId)
    {
        if (!Regex.IsMatch(strategyId, @"^general(?: \([A-Z0-9 ]+\))?\.bat$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Недопустимое имя стратегии.");
        string root = Path.GetFullPath(directory);
        string file = Path.Combine(root, strategyId);
        if (new FileInfo(file).Length > 64 * 1024) throw new InvalidDataException("Стратегия слишком велика.");
        try { return ParseText(File.ReadAllText(file), root, ReadGameFilter(root)); }
        catch (InvalidDataException ex) { throw new InvalidDataException(strategyId + ": " + ex.Message, ex); }
    }

    internal static IReadOnlyList<string> ParseText(string text, string root, (string Tcp, string Udp) game)
    {
        var command = new StringBuilder();
        bool started = false, complete = false;
        int lineNumber = 0;
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (!started && (Header.Contains(line) || line.StartsWith("::", StringComparison.Ordinal))) continue;
            if (!started && line.StartsWith(Start, StringComparison.Ordinal))
            {
                started = true;
                line = line.Substring(Start.Length);
            }
            else if (!started || complete || !line.StartsWith("--", StringComparison.Ordinal))
                throw new InvalidDataException("Новый формат стратегии в строке " + lineNumber + " не поддерживается: требуется обновление оболочки.");
            bool continuation = line.EndsWith("^", StringComparison.Ordinal);
            if (continuation) line = line.Substring(0, line.Length - 1).TrimEnd();
            line = line.Replace("^!", "!", StringComparison.Ordinal);
            if (line.IndexOfAny(new[] { '&', '|', '<', '>', '^', '\0', '\r', '\n', '`' }) >= 0)
                throw new InvalidDataException("В стратегии обнаружена неподдерживаемая конструкция.");
            command.Append(line).Append(' ');
            complete = !continuation;
        }
        if (!started || !complete) throw new InvalidDataException("Стратегия не содержит завершённой команды winws.");
        var arguments = Tokenize(command.ToString());
        if (arguments.Count < 2 || arguments.Count > 1024) throw new InvalidDataException("Неполная стратегия.");
        string bin = Path.Combine(root, "bin") + Path.DirectorySeparatorChar;
        string lists = Path.Combine(root, "lists") + Path.DirectorySeparatorChar;
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            var option = Regex.Match(argument, @"^--([a-z0-9-]+)(?:=(.*))?$", RegexOptions.CultureInvariant);
            if (!option.Success || !Options.Contains(option.Groups[1].Value))
                throw new InvalidDataException("Неподдерживаемый параметр стратегии.");
            string key = option.Groups[1].Value;
            string value = option.Groups[2].Value;
            if (key == "new")
            {
                if (option.Groups[2].Success) throw new InvalidDataException("Неверный разделитель стратегии.");
                continue;
            }
            if (value.Length == 0 || value.StartsWith('@')) throw new InvalidDataException("Неверное значение параметра.");
            bool path = value.StartsWith("%BIN%", StringComparison.Ordinal) || value.StartsWith("%LISTS%", StringComparison.Ordinal);
            if (path)
            {
                if (!FileOptions.Contains(key)) throw new InvalidDataException("Путь недопустим для этого параметра.");
                string relative = value.StartsWith("%BIN%", StringComparison.Ordinal) ? value.Substring(5) : value.Substring(7);
                if (!Regex.IsMatch(relative, @"^[A-Za-z0-9_.-]+\.(bin|txt)$", RegexOptions.CultureInvariant) || relative.Contains("..", StringComparison.Ordinal))
                    throw new InvalidDataException("Недопустимый путь ресурса.");
                value = Path.Combine(value.StartsWith("%BIN%", StringComparison.Ordinal) ? bin : lists, relative);
                if (!File.Exists(value)) throw new FileNotFoundException("Отсутствует ресурс стратегии: " + relative);
            }
            else
            {
                value = value.Replace("%GameFilterTCP%", game.Tcp, StringComparison.Ordinal)
                    .Replace("%GameFilterUDP%", game.Udp, StringComparison.Ordinal);
                if (!Regex.IsMatch(value, @"^[A-Za-z0-9_.,:=+!/-]+$", RegexOptions.CultureInvariant) || value.Contains('%'))
                    throw new InvalidDataException("Неизвестная переменная или значение стратегии.");
                if (FileOptions.Contains(key) && value != "!" && !Regex.IsMatch(value, @"^0x[0-9A-Fa-f]+$", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("Ресурс должен находиться в папке компонента.");
            }
            arguments[i] = "--" + key + "=" + value;
        }
        if (!arguments.Any(a => a.StartsWith("--wf-tcp=", StringComparison.Ordinal)) || !arguments.Any(a => a.StartsWith("--wf-udp=", StringComparison.Ordinal)))
            throw new InvalidDataException("Отсутствует фильтр перехвата стратегии.");
        return arguments;
    }

    private static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); }
            }
            else token.Append(c);
        }
        if (quoted) throw new InvalidDataException("Незакрытая кавычка в стратегии.");
        if (token.Length > 0) result.Add(token.ToString());
        return result;
    }

    internal static (string Tcp, string Udp) ReadGameFilter(string root)
    {
        string path = Path.Combine(root, "utils", "game_filter.enabled");
        if (!File.Exists(path)) return ("12", "12");
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Неверная конфигурация GameFilter.");
        string mode = "disabled", tcp = "1024-65535", udp = "1024-65535";
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int index = line.IndexOf('=');
            string key = index < 0 ? line : line.Substring(0, index);
            string value = index < 0 ? "" : line.Substring(index + 1);
            switch (key.ToLowerInvariant())
            {
                case "mode": mode = value; break;
                case "all": mode = "all"; break;
                case "tcp": if (value.Length == 0) mode = "tcp"; else tcp = ValidatePorts(value); break;
                case "udp": if (value.Length == 0) mode = "udp"; else udp = ValidatePorts(value); break;
                default: throw new InvalidDataException("Неподдерживаемая настройка GameFilter.");
            }
        }
        return mode.ToLowerInvariant() switch
        {
            "all" => (tcp, udp), "tcp" => (tcp, "12"), "udp" => ("12", udp), "disabled" => ("12", "12"),
            _ => throw new InvalidDataException("Неизвестный режим GameFilter.")
        };
    }

    internal static string ValidatePorts(string value)
    {
        value = value.Replace(" ", "", StringComparison.Ordinal);
        if (value.Length == 0 || value.Length > 1024) throw new InvalidDataException("Неверный диапазон портов.");
        foreach (string item in value.Split(','))
        {
            var match = Regex.Match(item, @"^([1-9][0-9]{0,4})(?:-([1-9][0-9]{0,4}))?$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new InvalidDataException("Неверный диапазон портов.");
            int start = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            int end = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : start;
            if (start > end || end > 65535) throw new InvalidDataException("Порт за допустимыми границами.");
        }
        return value;
    }

    public static IReadOnlyList<string> SelfTest(string sourceRoot)
    {
        var names = new List<string>();
        var all = List(sourceRoot);
        if (all.Count != 22) throw new Exception("Ожидались 22 исходные стратегии версии 1.10.4.");
        bool literal = false;
        foreach (var strategy in all)
        {
            var arguments = Parse(sourceRoot, strategy.Id);
            if (arguments.Any(a => a.Contains('%') || a.Contains('"'))) throw new Exception("Не развёрнуты аргументы.");
            literal |= arguments.Contains("--dpi-desync-fake-tls=!");
        }
        if (!literal) throw new Exception("Не проверен литерал ^!.");
        var quotedPath = Tokenize("--hostlist=\"C:\\Some Folder\\list.txt\" --new");
        if (quotedPath.Count != 2 || quotedPath[0] != "--hostlist=C:\\Some Folder\\list.txt") throw new Exception("Ошибка кавычек пути.");
        names.Add("Все 22 стратегии: переменные, пути с пробелами, литерал ^!");
        string sample = File.ReadAllText(Path.Combine(sourceRoot, "general.bat"));
        foreach (string bad in new[] { sample + "\nwhoami", sample.Replace("--wf-tcp=", "--debug=", StringComparison.Ordinal), sample.Replace("%GameFilterTCP%", "%UNKNOWN%", StringComparison.Ordinal), sample.Replace("list-general.txt", "../outside.txt", StringComparison.Ordinal), sample + "\n--new" })
        {
            try { ParseText(bad, sourceRoot, ("12", "12")); throw new Exception("Опасная стратегия принята."); }
            catch (InvalidDataException) { }
        }
        foreach (string bad in new[] { "0", "65536", "4-3", "1,,2", "01", "1&whoami", "-1" })
        {
            try { ValidatePorts(bad); throw new Exception("Недопустимый порт принят."); }
            catch (InvalidDataException) { }
        }
        if (ValidatePorts("1, 443,1024-65535") != "1,443,1024-65535") throw new Exception("Ошибка диапазона.");
        names.Add("Отказ при неизвестной команде, опции, переменной, выходе из каталога и неверных портах");
        return names;
    }
}
