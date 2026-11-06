namespace PdfApi.Examples;

/// <summary>Страница по адресу в PDF (тариф premium и выше).</summary>
internal static class FromUrl
{
    public static async Task<int> RunAsync(string[] args)
    {
        var url = args.Length > 0 ? args[0] : "https://example.com";
        using var client = new PdfApiClient();
        try
        {
            // WaitDelay — подождать, пока скрипты на странице дорисуют содержимое.
            var pdf = await client.UrlAsync(url, new PageOptions { Landscape = true, WaitDelay = "1s" });
            await pdf.SaveAsync("page.pdf");
            Console.WriteLine($"page.pdf: {pdf.Pages} стр.");
            return 0;
        }
        catch (PermissionDeniedException e)
        {
            Console.Error.WriteLine($"{e.Message} — нужен тариф premium");
        }
        catch (InvalidRequestException e)
        {
            Console.Error.WriteLine(e.Message); // например, url_not_allowed: адрес во внутреннюю сеть
        }
        return 1;
    }
}
