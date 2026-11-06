# Изменения

## 0.1.0

Первая версия: `HtmlAsync`, `UrlAsync`, `MarkdownAsync`, `OfficeAsync`, `ConvertAsync`, `MeAsync`; net8.0 и netstandard2.0; исключения по кодам API; повторы при 429, 503 и ошибках соединения; таймауты не повторяются.

Проверки до запроса: размеры — число с единицей `pt`, `px`, `in`, `mm`, `cm` или `pc`; `WaitDelay` — до 10 секунд; адрес — с `http(s)://`; Office-файл — ZIP-архив не больше 3,7 МБ. 429 повторяется по `Retry-After` или `retry_after_sec`. Ответы не от API (страница 404, формат Fastify, текст от прокси) разбираются по статусу; 404 — `not_found` с подсказкой про базовый адрес. Запись в файл атомарная: прерванная запись не портит прежний файл. `SaveAsync`; `MeAsync` отдаёт `UserId`, `PeriodStart`, `PeriodEnd` — когда обнулится квота; неверные `Timeout` и `MaxRetries` — ошибка конструктора.

Примеры: `invoice`, `from-url`, `office`, `batch`, `quota` (для cron) и `PdfApi.WebExample` (ASP.NET Core).
