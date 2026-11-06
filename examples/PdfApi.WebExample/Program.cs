// Отдавать PDF из ASP.NET Core (minimal API).
//
//     PDFAPI_KEY=pdf_live_... dotnet run --project examples/PdfApi.WebExample --urls http://127.0.0.1:5000
//     curl -o invoice.pdf 'http://127.0.0.1:5000/invoice/42?customer=ООО%20Ромашка'
using System.Net;
using PdfApi;

var builder = WebApplication.CreateBuilder(args);

// Один клиент на приложение; HttpClient — из IHttpClientFactory, ключ — из настроек или PDFAPI_KEY.
builder.Services.AddHttpClient("pdfapi");
builder.Services.AddSingleton(sp => new PdfApiClient(
    builder.Configuration["PdfApi:Key"],
    new PdfApiClientOptions { HttpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("pdfapi") }));

var app = builder.Build();

app.MapGet("/invoice/{number:int}", async (int number, string? customer, PdfApiClient pdfapi, HttpContext http, CancellationToken ct) =>
{
    // Всё, что пришло от пользователя, — только через экранирование: иначе это HTML-инъекция в документ.
    var html = $"<h1>Счёт №{number}</h1><p>Покупатель: {WebUtility.HtmlEncode(customer ?? "Покупатель")}</p><p>К оплате: 12 000 ₽</p>";
    try
    {
        // ct — токен запроса: клиент ушёл, и запрос к pdfapi тоже отменяется.
        var pdf = await pdfapi.HtmlAsync(html, new PageOptions { MarginTop = "20mm" }, ct);
        return Results.File(pdf.Content, "application/pdf", $"invoice-{number}.pdf");
    }
    catch (RateLimitException e)
    {
        var seconds = (int)Math.Ceiling((e.RetryAfter ?? TimeSpan.FromSeconds(1)).TotalSeconds);
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Text("Слишком часто, повторите позже", statusCode: StatusCodes.Status429TooManyRequests);
    }
    catch (PdfApiException e)
    {
        return Results.Text($"PDF не получился: {e.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();
