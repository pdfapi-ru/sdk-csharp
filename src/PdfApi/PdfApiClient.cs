using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PdfApi
{
    /// <summary>Настройки клиента. Всё необязательно.</summary>
    public sealed class PdfApiClientOptions
    {
        /// <summary>Адрес API; по умолчанию PDFAPI_BASE_URL или https://pdfapi.ru/api.</summary>
        public string? BaseUrl { get; set; }
        /// <summary>Таймаут одного запроса; по умолчанию 40 с. Для одного вызова — CancellationToken.</summary>
        public TimeSpan Timeout { get; set; } = PdfApiClient.DefaultTimeout;
        /// <summary>Сколько раз повторять 429, 503 и ошибки соединения; по умолчанию 2.</summary>
        public int MaxRetries { get; set; } = PdfApiClient.DefaultMaxRetries;
        /// <summary>Свой HttpClient (например, из IHttpClientFactory). Освобождать его — ваша забота.</summary>
        public HttpClient? HttpClient { get; set; }
    }

    /// <summary>
    /// Клиент pdfapi. Создайте один и переиспользуйте; потокобезопасен.
    /// <code>
    /// using var client = new PdfApiClient();                // ключ из PDFAPI_KEY
    /// var pdf = await client.HtmlAsync("&lt;h1&gt;Счёт №42&lt;/h1&gt;", new PageOptions { MarginTop = "20mm" });
    /// pdf.Save("invoice.pdf");
    /// </code>
    /// </summary>
    public sealed class PdfApiClient : IDisposable
    {
        /// <summary>Версия SDK.</summary>
        public const string Version = "0.1.0";
        /// <summary>Адрес API по умолчанию.</summary>
        public const string DefaultBaseUrl = "https://pdfapi.ru/api";
        /// <summary>Повторы по умолчанию.</summary>
        public const int DefaultMaxRetries = 2;
        /// <summary>Предел файла для Office: тело ограничено 5 МБ, а base64 раздувает файл на треть.</summary>
        public const int OfficeMaxBytes = 3_700_000;
        /// <summary>Таймаут по умолчанию: рендер — до 30 с, плюс запас.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(40);
        private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
        private static readonly string[] OfficeExtensions = { ".docx", ".xlsx", ".pptx" };
        // docx, xlsx и pptx — ZIP-архивы, начинаются с «PK\x03\x04».
        private static readonly byte[] ZipMagic = { 0x50, 0x4b, 0x03, 0x04 };
        private static readonly System.Text.RegularExpressions.Regex HttpUrl =
            new System.Text.RegularExpressions.Regex(@"^https?://\S", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // Кириллицу и < > не экранируем: русский HTML в \uXXXX вырос бы втрое и быстрее
        // упёрся бы в лимит 5 МБ. Тело — это JSON для API, в HTML-страницу оно не встраивается.
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly string _apiKey;
        private readonly HttpClient _http;
        private readonly bool _ownHttp;
        private readonly TimeSpan _timeout;
        private readonly int _maxRetries;
        private readonly string _userAgent;

        /// <summary>Пауза между повторами — подменяется в тестах.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

        /// <summary>Создаёт клиент. Без ключа — ошибка сразу, а не 401 при первом запросе.</summary>
        /// <param name="apiKey">Ключ API; по умолчанию — переменная окружения PDFAPI_KEY.</param>
        /// <param name="options">Настройки.</param>
        public PdfApiClient(string? apiKey = null, PdfApiClientOptions? options = null)
        {
            options ??= new PdfApiClientOptions();
            var key = (apiKey ?? Environment.GetEnvironmentVariable("PDFAPI_KEY") ?? "").Trim();
            if (key.Length == 0)
                throw new ArgumentException(
                    "Не задан ключ API: передайте apiKey или задайте переменную окружения PDFAPI_KEY.", nameof(apiKey));
            _apiKey = key;
            var url = options.BaseUrl;
            if (string.IsNullOrEmpty(url)) url = Environment.GetEnvironmentVariable("PDFAPI_BASE_URL");
            if (string.IsNullOrEmpty(url)) url = DefaultBaseUrl;
            BaseUrl = url!.TrimEnd('/');
            if (options.Timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), options.Timeout, "Timeout: нужен больше нуля, например 40 секунд.");
            if (options.MaxRetries < 0)
                throw new ArgumentOutOfRangeException(nameof(options), options.MaxRetries, "MaxRetries: нужно от 0.");
            _timeout = options.Timeout;
            _maxRetries = options.MaxRetries;
            _ownHttp = options.HttpClient == null;
            // Таймаут ставим на каждый запрос сами — у HttpClient свой отключаем.
            _http = options.HttpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _userAgent = $"pdfapi-dotnet/{Version} ({RuntimeInformation.FrameworkDescription})";
        }

        /// <summary>Адрес API, к которому обращается клиент.</summary>
        public string BaseUrl { get; }

        /// <summary>HTML → PDF.</summary>
        public Task<PdfResult> HtmlAsync(string html, PageOptions? options = null, CancellationToken cancellationToken = default)
            => ConvertAsync(TextBody("html", html, options), cancellationToken);

        /// <summary>Страница по публичному адресу → PDF (тариф premium и выше).</summary>
        public Task<PdfResult> UrlAsync(string url, PageOptions? options = null, CancellationToken cancellationToken = default)
            => ConvertAsync(TextBody("url", url, options), cancellationToken);

        /// <summary>Markdown → PDF (тариф premium и выше).</summary>
        public Task<PdfResult> MarkdownAsync(string markdown, PageOptions? options = null, CancellationToken cancellationToken = default)
            => ConvertAsync(TextBody("markdown", markdown, options), cancellationToken);

        /// <summary>docx, xlsx или pptx с диска → PDF.</summary>
        public Task<PdfResult> OfficeAsync(string path, bool? landscape = null, CancellationToken cancellationToken = default)
            => OfficeAsync(File.ReadAllBytes(path), Path.GetFileName(path), landscape, cancellationToken);

        /// <summary>docx, xlsx или pptx из памяти → PDF. По расширению имени сервер понимает формат.</summary>
        public Task<PdfResult> OfficeAsync(byte[] data, string filename, bool? landscape = null, CancellationToken cancellationToken = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var ext = Path.GetExtension(filename ?? "").ToLowerInvariant();
            if (!OfficeExtensions.Contains(ext))
                throw new ArgumentException($"{filename}: поддерживаются только .docx, .xlsx и .pptx", nameof(filename));
            if (data.Length == 0) throw new ArgumentException($"{filename}: файл пустой", nameof(data));
            if (data.Length < ZipMagic.Length || !data.Take(ZipMagic.Length).SequenceEqual(ZipMagic))
                // Старый .doc, PDF или картинка с чужим расширением: сервер всё равно отклонит.
                throw new ArgumentException($"{filename}: не похож на docx, xlsx или pptx — это не ZIP-архив", nameof(data));
            if (data.Length > OfficeMaxBytes)
                throw new ArgumentException(
                    $"{filename}: {(data.Length / 1_000_000.0).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU"))} МБ — " +
                    "больше предела 3,7 МБ (после base64 тело запроса превысило бы 5 МБ)", nameof(data));
            var body = new Dictionary<string, object?>
            {
                ["source"] = "office",
                ["filename"] = Path.GetFileName(filename),
                ["fileBase64"] = Convert.ToBase64String(data),
            };
            if (landscape.HasValue) body["options"] = new Dictionary<string, object> { ["landscape"] = landscape.Value };
            return ConvertAsync(body, cancellationToken);
        }

        /// <summary>Низкоуровневый POST /v1/convert с телом как есть.</summary>
        public async Task<PdfResult> ConvertAsync(IDictionary<string, object?> body, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(body, Json);
            var (response, data) = await SendAsync(HttpMethod.Post, "/v1/convert", json, cancellationToken).ConfigureAwait(false);
            using (response)
            {
                int? Header(string name) =>
                    (response.Headers.TryGetValues(name, out var v) || response.Content.Headers.TryGetValues(name, out v))
                    && int.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
                int? limit = Header("X-Quota-Limit"), used = Header("X-Quota-Used"), remaining = Header("X-Quota-Remaining");
                var quota = limit.HasValue && used.HasValue && remaining.HasValue ? new Quota(limit.Value, used.Value, remaining.Value) : null;
                return new PdfResult(data, Header("X-Pages-Rendered"), quota);
            }
        }

        /// <summary>Тариф, расход и остаток страниц в текущем периоде.</summary>
        public async Task<Account> MeAsync(CancellationToken cancellationToken = default)
        {
            var (response, data) = await SendAsync(HttpMethod.Get, "/v1/me", null, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(data);
            }
            catch (JsonException ex)
            {
                throw new PdfApiException("Ответ /v1/me не JSON.", "server_error", 200, ex);
            }
            using (doc)
            {
                var root = doc.RootElement;
                JsonElement Obj(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
                string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                double Num(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                DateTimeOffset? Date(string text) =>
                    DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : (DateTimeOffset?)null;
                var usage = Obj("usage");
                var limits = Obj("limits");
                // Старый API присылал только дату начала — «2026-10-01», это полночь UTC.
                var period = Str(root, "period");
                var start = Date(Str(root, "period_start")) ?? (System.Text.RegularExpressions.Regex.IsMatch(period, @"^\d{4}-\d{2}-\d{2}$") ? Date(period + "T00:00:00Z") : null);
                return new Account(Str(root, "user_id"), Str(root, "plan"), start, Date(Str(root, "period_end")),
                    (int)Num(usage, "pages_used"), (int)Num(usage, "pages_limit"), (int)Num(usage, "pages_remaining"),
                    Num(limits, "requests_per_second"), (int)Num(limits, "burst"));
            }
        }

        private static Dictionary<string, object?> TextBody(string source, string value, PageOptions? options)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{source}: пустое содержимое", source);
            if (source == "url")
            {
                value = value.Trim(); // пробелы по краям рендер не прощает
                if (!HttpUrl.IsMatch(value))
                    throw new ArgumentException($"url: \"{value}\" — нужен полный адрес с http:// или https://, например https://example.com", source);
            }
            var body = new Dictionary<string, object?> { ["source"] = source, [source] = value };
            var o = options?.ToDictionary();
            if (o != null && o.Count > 0) body["options"] = o;
            return body;
        }

        // ------------------------------------------------------------------ сеть

        private async Task<(HttpResponseMessage, byte[])> SendAsync(HttpMethod method, string path, byte[]? json, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, BaseUrl + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                request.Headers.Accept.ParseAdd("application/pdf");
                request.Headers.Accept.ParseAdd("application/json");
                if (json != null)
                {
                    request.Content = new ByteArrayContent(json);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_timeout);
                HttpResponseMessage response;
                byte[] data;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                    data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // отменил вызывающий — это не ошибка API
                }
                catch (OperationCanceledException ex)
                {
                    throw new ApiTimeoutException(
                        $"API не ответил за {_timeout.TotalSeconds:0.#} с. Запрос мог быть выполнен — повторять его " +
                        "автоматически небезопасно: страницы списались бы дважды.", "timeout", ex);
                }
                catch (HttpRequestException ex) when (NotSent(ex) && attempt < _maxRetries)
                {
                    await Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
                {
                    throw new ApiConnectionException($"Нет связи с {BaseUrl}: {ex.Message}", "connection_error", ex);
                }

                var status = (int)response.StatusCode;
                if (status >= 200 && status < 300) return (response, data);
                using (response)
                {
                    var delay = RetryDelay(attempt, status, response, data);
                    if (delay.HasValue && attempt < _maxRetries)
                    {
                        await Delay(delay.Value, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw ErrorFactory.FromResponse(response, data);
                }
            }
        }

        /// <summary>
        /// Запрос точно не ушёл: не удалось соединиться или найти адрес. Таймаут чтения и
        /// обрыв посреди ответа сюда не входят: сервер мог успеть отрендерить и списать
        /// страницы, повтор списал бы их второй раз.
        /// </summary>
        private static bool NotSent(HttpRequestException ex)
        {
#if NET8_0_OR_GREATER
            if (ex.HttpRequestError == HttpRequestError.NameResolutionError) return true;
#endif
            return ex.InnerException is SocketException se && (
                se.SocketErrorCode == SocketError.ConnectionRefused || se.SocketErrorCode == SocketError.HostNotFound ||
                se.SocketErrorCode == SocketError.TryAgain || se.SocketErrorCode == SocketError.NetworkUnreachable ||
                se.SocketErrorCode == SocketError.HostUnreachable);
        }

        private static readonly object RandomLock = new object();
        private static readonly Random Random = new Random();

        /// <summary>0,5 → 1 → 2 с … с разбросом ±25%.</summary>
        private static TimeSpan Backoff(int attempt)
        {
            double jitter;
            lock (RandomLock) jitter = 0.75 + Random.NextDouble() * 0.5;
            return TimeSpan.FromSeconds(0.5 * Math.Pow(2, attempt) * jitter);
        }

        /// <summary>Повторяются только 429 и 503: сервер ничего не сделал и страниц не списал.</summary>
        private static TimeSpan? RetryDelay(int attempt, int status, HttpResponseMessage response, byte[] body)
        {
            if (status == 429)
            {
                var after = ErrorFactory.RetryAfterOrBody(response, body) ?? Backoff(attempt);
                return after > MaxRetryAfter ? MaxRetryAfter : after;
            }
            return status == 503 ? Backoff(attempt) : (TimeSpan?)null;
        }

        /// <inheritdoc/>
        public override string ToString() => $"PdfApiClient(BaseUrl={BaseUrl})"; // ключ — никогда

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_ownHttp) _http.Dispose();
        }
    }
}
