// Примеры pdfapi. Ключ — из переменной окружения PDFAPI_KEY:
//
//     PDFAPI_KEY=pdf_live_... dotnet run --project examples/PdfApi.Examples -- invoice
//
// Веб-приложение — отдельный проект examples/PdfApi.WebExample.
using PdfApi.Examples;

var examples = new Dictionary<string, Func<string[], Task<int>>>
{
    ["invoice"] = Invoice.RunAsync,   // счёт из HTML-шаблона
    ["from-url"] = FromUrl.RunAsync,  // страница по адресу
    ["office"] = Office.RunAsync,     // docx, xlsx, pptx
    ["batch"] = Batch.RunAsync,       // пачка документов с ограничением параллельности
    ["quota"] = Quota.RunAsync,       // остаток страниц — для cron и мониторинга
};

if (args.Length == 0 || !examples.TryGetValue(args[0], out var run))
{
    Console.Error.WriteLine($"Какой пример запустить: {string.Join(", ", examples.Keys)}");
    return 1;
}
return await run(args[1..]);
