using PursovStudio;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var empty = UiPresentation.Summarize(Array.Empty<ProbeResult>());
Assert(empty.Length == 3 && empty.All(x => x.Success is null), "Empty results must remain unconfirmed.");
var partial = UiPresentation.Summarize(new[]
{
    new ProbeResult("Telegram", "OK", true),
    new ProbeResult("Discord · сайт/API", "OK", true),
    new ProbeResult("Discord · голос", "Не проверен", null),
    new ProbeResult("YouTube · сайт", "OK", true),
    new ProbeResult("YouTube · видео", "Не проверено", null)
});
Assert(partial[0].Text == "Прокси отвечает" && partial[0].Success == true, "Telegram label must describe proxy response.");
Assert(partial[1].Success is null && partial[2].Success is null, "One passing check does not prove full availability.");
Assert(partial[2].Text == "Проверен частично", "Partial result must be distinct from unchecked.");
var discord = UiPresentation.Summarize(new[]
{
    new ProbeResult("Discord · сайт/API", "OK", true),
    new ProbeResult("Discord · соединение", "OK", true),
    new ProbeResult("Discord · голос", "Не проверен", null)
});
Assert(discord[1].Success == true && discord[1].Text == "Соединение отвечает", "Voice uncertainty must not claim voice or erase confirmed connection.");
var failed = UiPresentation.Summarize(new[] { new ProbeResult("YouTube · сайт", "OK", true), new ProbeResult("YouTube · видео", "Ошибка", false) });
Assert(failed[2].Success == false, "A failed sub-check must be visible.");
Console.WriteLine("UI presentation: 6 checks passed.");

namespace PursovStudio
{
    public sealed record ProbeResult(string Service, string Status, bool? Success, double Milliseconds = 0, string Detail = "");
}
