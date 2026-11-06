# pdfapi — .NET SDK

Официальная библиотека [pdfapi](https://pdfapi.ru): HTML, страница по адресу, Markdown или docx/xlsx/pptx — в PDF одним вызовом. Работает на .NET 8 и на всём, что поддерживает .NET Standard 2.0: .NET Framework 4.6.1+, .NET Core 2.0+, Mono, Unity.

```bash
dotnet add package PdfApi
```

```csharp
using PdfApi;

using var client = new PdfApiClient();   // ключ из переменной окружения PDFAPI_KEY

var pdf = await client.HtmlAsync("<h1>Счёт №42</h1>", new PageOptions { MarginTop = "20mm" });
await pdf.SaveAsync("invoice.pdf");

Console.WriteLine($"{pdf.Pages} стр., осталось {pdf.Quota?.Remaining}");
```

Ключ выпускается в кабинете на [pdfapi.ru](https://pdfapi.ru). Держите его в переменной окружения или в секретах приложения, а не в коде:

```bash
export PDFAPI_KEY=pdf_live_...
```

Клиент потокобезопасен: создайте один (например, singleton в DI) и переиспользуйте.

## Методы

Все асинхронные и принимают `CancellationToken`.

| Метод | Что делает |
|---|---|
| `HtmlAsync(html, options)` | HTML → PDF |
| `UrlAsync(url, options)` | страница по адресу → PDF (тариф premium и выше). Адрес — полный, с `http://` или `https://` |
| `MarkdownAsync(text, options)` | Markdown → PDF (тариф premium и выше) |
| `OfficeAsync(path, landscape)` | docx, xlsx, pptx с диска → PDF (тариф premium и выше) |
| `OfficeAsync(bytes, filename, landscape)` | то же из памяти |
| `ConvertAsync(body)` | `POST /v1/convert` с телом как есть |
| `MeAsync()` | тариф, расход, остаток страниц и границы периода |

### Параметры страницы

`PageOptions` — все свойства необязательные:

| Свойство | Пример | По умолчанию |
|---|---|---|
| `Landscape` | `true` | `false` |
| `PaperWidth`, `PaperHeight` | `"8.27in"`, `"297mm"` | A4 |
| `MarginTop`, `MarginBottom`, `MarginLeft`, `MarginRight` | `"20mm"` | `"0.39in"` |
| `WaitDelay` | `"2s"`, `"500ms"` — подождать, пока скрипт на странице дорисует график; до 10 секунд | `"0s"` |

Размер — число и единица `pt`, `px`, `in`, `mm`, `cm` или `pc` без пробела: `"20mm"`, `"1.5cm"`, `".5in"`. Остальное — ошибка до запроса: `"20"` — это миллиметры или дюймы? А `"20 mm"` и `"20MM"` рендер не понимает.

### Office-документы

```csharp
var fromDisk = await client.OfficeAsync("report.docx");                                      // с диска
var fromMemory = await client.OfficeAsync(bytes, "table.xlsx", landscape: true);             // из памяти — имя с расширением
```

Файл — не больше 3,7 МБ: после base64 тело запроса не должно превысить 5 МБ. docx, xlsx и pptx — ZIP-архивы, поэтому старый `.doc` или PDF, переименованный в `.docx`, отклоняется сразу, не отправляя мегабайты на сервер.

## Результат

`PdfResult`:
- `Content` — `byte[]` PDF;
- `Pages` — сколько страниц списано;
- `Quota` — `Limit`, `Used`, `Remaining` после этой конвертации;
- `Save(path)` и `SaveAsync(path)` — записать в файл.

`Save` пишет атомарно: сначала во временный файл рядом, потом переименовывает поверх. Оборвётся запись или кончится место — на месте останется прежний файл, а не обрезанный PDF. Каталог не создаётся.

## Отдать PDF из веб-приложения

PDF уже в памяти — отдавайте байты, на диск сохранять незачем:

```csharp
// ASP.NET Core, minimal API или контроллер
app.MapGet("/invoice", async (PdfApiClient pdfapi, CancellationToken ct) =>
{
    var pdf = await pdfapi.HtmlAsync("<h1>Счёт №42</h1>", cancellationToken: ct);
    return Results.File(pdf.Content, "application/pdf", "invoice.pdf");
});
```

Данные пользователя в HTML — только через `WebUtility.HtmlEncode` или Razor, иначе это HTML-инъекция в документ. Целиком — регистрация в DI, ошибки API в HTTP-ответы — [`examples/PdfApi.WebExample`](examples/PdfApi.WebExample/Program.cs).

## Аккаунт и квота

```csharp
var me = await client.MeAsync();
Console.WriteLine($"{me.Plan}: осталось {me.PagesRemaining} из {me.PagesLimit}, обнулится {me.PeriodEnd:u}");
```

`Account`: `UserId`, `Plan` (`free`, `premium`, `business`), `PeriodStart` и `PeriodEnd` (когда счётчик начнётся с нуля; `DateTimeOffset` в UTC или `null`, если сервер не сообщил), `PagesUsed`, `PagesLimit`, `PagesRemaining`, `RequestsPerSecond`, `Burst`. Запрос страниц не тратит. Проверка для cron — [`examples/PdfApi.Examples/Quota.cs`](examples/PdfApi.Examples/Quota.cs).

## Ошибки

Все ошибки API — наследники `PdfApiException` с `Code`, `Status` и сообщением по-русски:

```csharp
try
{
    var pdf = await client.HtmlAsync(html);
}
catch (QuotaExceededException) { /* страницы кончились */ }
catch (RateLimitException e) { await Task.Delay(e.RetryAfter ?? TimeSpan.FromSeconds(1)); }
catch (RenderException) { /* и RenderTimeoutException тоже: разметка не отрендерилась */ }
catch (PdfApiException e) { logger.LogWarning("{Code} {Status}: {Message}", e.Code, e.Status, e.Message); }
```

| Исключение | Когда |
|---|---|
| `AuthenticationException` | 401: ключа нет, он неверный или отозван |
| `PermissionDeniedException` | 403: почта не подтверждена или формат недоступен на тарифе |
| `QuotaExceededException` | 402: страницы кончились |
| `InvalidRequestException` | 400: неверный параметр или адрес во внутреннюю сеть (`Code == "url_not_allowed"`) |
| `PayloadTooLargeException` | 413 (наследник `InvalidRequestException`) |
| `RateLimitException` | 429; `RetryAfter` — через сколько можно снова |
| `RenderException` | 502: документ не отрендерился |
| `RenderTimeoutException` | 504 (наследник `RenderException`) |
| `UnavailableException` | 503 |
| `ApiConnectionException` | нет связи |
| `ApiTimeoutException` | API не ответил за таймаут (наследник `ApiConnectionException`) |
| `PdfApiException` с `Code == "not_found"` | 404: неверный базовый адрес — обычно в нём забыли `/api` |

Неверные аргументы (пустой HTML, `"20 mm"` вместо `"20mm"`, адрес без `https://`, файл `.pdf` вместо `.docx`) — `ArgumentException`, ещё до создания `Task` и до запроса; лимит частоты на них не тратится.

## Повторы

SDK сам повторяет запрос, когда это безопасно — по умолчанию до 2 раз (`MaxRetries`):
- **429** — через `Retry-After` (или `retry_after_sec` из ответа), но не дольше минуты;
- **503** и **не удалось соединиться** — через 0,5 и 1 с (с небольшим разбросом).

**Таймаут и обрыв посреди ответа не повторяются:** сервер мог успеть сделать PDF и списать страницы, и повтор списал бы их второй раз.

## Таймауты и отмена

- Таймаут клиента — `PdfApiClientOptions.Timeout`, по умолчанию 40 с на одну попытку; не уложились — `ApiTimeoutException`.
- Для одного вызова — свой `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var pdf = await client.HtmlAsync(html, cancellationToken: cts.Token);
```

- Отмена через ваш токен — обычный `OperationCanceledException`, а не ошибка API.

## Настройки клиента и DI

```csharp
services.AddHttpClient("pdfapi");
services.AddSingleton(sp => new PdfApiClient(
    configuration["PdfApi:Key"],                 // иначе PDFAPI_KEY
    new PdfApiClientOptions
    {
        BaseUrl = "https://pdfapi.ru/api",       // иначе PDFAPI_BASE_URL
        Timeout = TimeSpan.FromSeconds(40),
        MaxRetries = 2,
        HttpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("pdfapi"),
    }));
```

`Timeout` не больше нуля или `MaxRetries` меньше нуля — `ArgumentOutOfRangeException` сразу. Ключа нет ни в `ToString()` клиента, ни в текстах ошибок.

## Примеры

В папке [`examples/`](examples):

| Пример | Что показывает |
|---|---|
| `invoice` | счёт из HTML-шаблона с экранированием данных |
| `from-url` | страница по адресу, альбомная, с ожиданием скриптов |
| `office` | docx, xlsx или pptx в PDF рядом с исходным файлом |
| `batch` | пачка документов параллельно через `SemaphoreSlim`, в пределах лимита частоты |
| `quota` | остаток страниц и дата обнуления — для cron, код выхода 2 при нехватке |
| `PdfApi.WebExample` | ASP.NET Core: клиент в DI, токен запроса, ошибки API — в HTTP-ответы |

```bash
PDFAPI_KEY=pdf_live_... dotnet run --project examples/PdfApi.Examples -- invoice
PDFAPI_KEY=pdf_live_... dotnet run --project examples/PdfApi.WebExample --urls http://127.0.0.1:5000
```

## Разработка

```bash
dotnet test tests/PdfApi.Tests       # юнит-тесты и общие кейсы (../tests/cases.json)
PDFAPI_IT_KEY=pdf_live_... PDFAPI_IT_BASE_URL=http://localhost:18080/api dotnet test tests/PdfApi.Tests --filter Category=Integration
```

Общий контракт SDK — [`../SPEC.md`](../SPEC.md).
