using System.Globalization;
using System.Net;
using System.Text;

namespace PdfApi.Examples;

/// <summary>Счёт из HTML-шаблона.</summary>
internal static class Invoice
{
    private static string Rub(int amount) =>
        amount.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = " " }) + " ₽"; // 12 000 ₽

    public static async Task<int> RunAsync(string[] args)
    {
        var items = new[] { (Name: "Подписка Premium, октябрь", Qty: 1, Price: 1990), (Name: "Дополнительные страницы", Qty: 2, Price: 500) };
        var rows = new StringBuilder();
        foreach (var (name, qty, price) in items)
            rows.Append($"<tr><td>{WebUtility.HtmlEncode(name)}</td><td>{qty}</td><td>{Rub(qty * price)}</td></tr>");
        var total = items.Sum(i => i.Qty * i.Price);

        // $$: одинарные { } — CSS, а {{…}} — подстановка.
        var html = $$"""
            <style>
              body { font-family: sans-serif; }
              table { width: 100%; border-collapse: collapse; }
              td, th { border-bottom: 1px solid #ddd; padding: 8px; text-align: left; }
            </style>
            <h1>Счёт №42</h1>
            <table><tr><th>Услуга</th><th>Кол-во</th><th>Сумма</th></tr>{{rows}}</table>
            <h2>Итого: {{Rub(total)}}</h2>
            """;

        using var client = new PdfApiClient();
        try
        {
            var pdf = await client.HtmlAsync(html, new PageOptions { MarginTop = "20mm", MarginBottom = "20mm" });
            await pdf.SaveAsync("invoice.pdf");
            Console.WriteLine($"invoice.pdf: {pdf.Pages} стр., осталось {pdf.Quota?.Remaining.ToString() ?? "?"}");
            return 0;
        }
        catch (PdfApiException e)
        {
            Console.Error.WriteLine($"Не получилось: {e.Message}");
            return 1;
        }
    }
}
