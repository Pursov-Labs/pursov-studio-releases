using System.Collections.Generic;
using System.Linq;

namespace PursovStudio;

public sealed record ServiceSummary(string Name, string Text, bool? Success);

public static class UiPresentation
{
    public static ServiceSummary[] Summarize(IReadOnlyList<ProbeResult> results)
    {
        var telegram = results.FirstOrDefault(x => x.Service == "Telegram");
        var discord = results.Where(x => x.Service is "Discord · сайт/API" or "Discord · соединение").ToArray();
        var youtube = results.Where(x => x.Service is "YouTube · сайт" or "YouTube · видео").ToArray();
        return new[]
        {
            new ServiceSummary("Telegram", Label(telegram?.Success, "Прокси отвечает"), telegram?.Success),
            Group("Discord", discord, "Соединение отвечает"),
            Group("YouTube", youtube, "Видеофрагмент получен")
        };
    }

    private static ServiceSummary Group(string name, ProbeResult[] results, string passed)
    {
        bool? success = results.Any(x => x.Success == false) ? false
            : results.Length == 2 && results.All(x => x.Success == true) ? true : null;
        string text = Label(success, passed);
        if (success is null && results.Any(x => x.Success == true)) text = "Проверен частично";
        return new(name, text, success);
    }

    private static string Label(bool? success, string passed) => success switch
    {
        true => passed,
        false => "Нет ответа",
        null => "Не проверен"
    };
}
