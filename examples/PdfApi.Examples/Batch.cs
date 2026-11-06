namespace PdfApi.Examples;

/// <summary>
/// Пачка документов параллельно, с ограничением одновременных запросов. Тариф ограничивает частоту
/// (Free — 1 запрос в секунду и до 3 подряд, Premium — 10 и 30). SDK сам подождёт и повторит при 429,
/// но ровнее — не отправлять больше, чем позволяет тариф.
/// </summary>
internal static class Batch
{
    private const int Parallel = 3;

    public static async Task<int> RunAsync(string[] args)
    {
        using var client = new PdfApiClient(); // один на все задачи, потокобезопасен
        using var limit = new SemaphoreSlim(Parallel);

        async Task Render(int n)
        {
            var name = $"invoice-{n}.pdf";
            await limit.WaitAsync();
            try
            {
                var pdf = await client.HtmlAsync($"<h1>Счёт №{n}</h1>");
                await pdf.SaveAsync(name);
                Console.WriteLine($"{name}: {pdf.Pages} стр.");
            }
            catch (PdfApiException e)
            {
                Console.Error.WriteLine($"{name}: ошибка {e.Code} — {e.Message}");
            }
            finally
            {
                limit.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(1, 10).Select(Render));
        return 0;
    }
}
