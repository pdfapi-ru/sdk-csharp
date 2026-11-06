using System.Globalization;

namespace PdfApi.Examples;

/// <summary>
/// Сколько страниц осталось и когда квота обнулится — для cron и мониторинга. Код выхода 2 — страниц
/// меньше порога (по умолчанию 10%, иначе — первый аргумент). Запрос страниц не тратит.
/// </summary>
internal static class Quota
{
    public static async Task<int> RunAsync(string[] args)
    {
        var threshold = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 10;
        using var client = new PdfApiClient();
        var me = await client.MeAsync();
        var left = me.PagesLimit > 0 ? 100.0 * me.PagesRemaining / me.PagesLimit : 0;
        var reset = me.PeriodEnd?.ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "неизвестно когда";

        Console.WriteLine($"Тариф {me.Plan}: использовано {me.PagesUsed} из {me.PagesLimit}, осталось {me.PagesRemaining} ({Math.Floor(left)}%).");
        Console.WriteLine($"Счётчик обнулится {reset}.");
        if (left < threshold)
        {
            Console.Error.WriteLine($"Осталось меньше {threshold}% — пора докупить страницы или сменить тариф.");
            return 2;
        }
        return 0;
    }
}
