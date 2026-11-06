using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PdfApi
{
    /// <summary>Ответ API с ошибкой → исключение нужного класса (таблица — sdk/SPEC.md).</summary>
    internal static class ErrorFactory
    {
        // Ответ не от API (Caddy отдаёт 413 текстом, прокси — 502 в HTML, сайт — 404
        // страницей, Fastify — {"error": "Bad Request"}): код по статусу.
        private static readonly Dictionary<int, string> CodeByStatus = new Dictionary<int, string>
        {
            [400] = "invalid_request", [401] = "invalid_api_key", [402] = "quota_exceeded",
            [403] = "source_not_allowed", [404] = "not_found", [413] = "payload_too_large",
            [429] = "rate_limit_exceeded", [502] = "render_failed", [503] = "unavailable", [504] = "render_timeout",
        };

        /// <summary>Код API — quota_exceeded, а не название статуса вроде «Bad Request».</summary>
        private static readonly Regex ApiCode = new Regex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> Messages = new Dictionary<string, string>
        {
            ["missing_api_key"] = "Не передан ключ API.",
            ["invalid_api_key"] = "Неверный или отозванный ключ API.",
            ["email_not_verified"] = "Почта аккаунта не подтверждена.",
            ["source_not_allowed"] = "Этот формат недоступен на вашем тарифе.",
            ["quota_exceeded"] = "Лимит страниц исчерпан.",
            ["invalid_request"] = "Некорректный запрос.",
            ["url_not_allowed"] = "Адрес ведёт во внутреннюю сеть и запрещён.",
            ["payload_too_large"] = "Тело запроса больше 5 МБ.",
            ["rate_limit_exceeded"] = "Слишком много запросов.",
            ["render_failed"] = "Не удалось создать PDF.",
            ["render_timeout"] = "Рендер не уложился в 30 секунд.",
            ["unavailable"] = "Сервис временно недоступен.",
            ["server_error"] = "Внутренняя ошибка сервера.",
            ["not_found"] = "Адрес не найден (HTTP 404). Проверьте базовый адрес: он ведёт на API, например https://pdfapi.ru/api.",
        };

        private static (string? Code, string? Message, double? RetryAfterSec) Parse(byte[] body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null);
                var root = doc.RootElement;
                string? code = null, message = null;
                double? retryAfterSec = null;
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) code = e.GetString();
                if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                if (root.TryGetProperty("retry_after_sec", out var r) && r.ValueKind == JsonValueKind.Number) retryAfterSec = r.GetDouble();
                return (code, message, retryAfterSec);
            }
            catch (JsonException)
            {
                return (null, null, null); // не JSON — разберёмся по статусу
            }
        }

        /// <summary>Через сколько можно снова: заголовок Retry-After, иначе retry_after_sec из тела.</summary>
        public static TimeSpan? RetryAfterOrBody(HttpResponseMessage response, byte[] body)
        {
            var after = RetryAfter(response);
            if (after.HasValue) return after;
            var sec = Parse(body).RetryAfterSec;
            return sec.HasValue && sec.Value >= 0 ? TimeSpan.FromSeconds(sec.Value) : (TimeSpan?)null;
        }

        public static PdfApiException FromResponse(HttpResponseMessage response, byte[] body)
        {
            var status = (int)response.StatusCode;
            var (code, message, _) = Parse(body);
            if (code == null || !ApiCode.IsMatch(code))
                code = CodeByStatus.TryGetValue(status, out var c) ? c : status >= 500 ? "server_error" : "unknown_error";
            if (string.IsNullOrWhiteSpace(message))
                message = Messages.TryGetValue(code!, out var t) ? t : $"Ошибка API (HTTP {status}).";

            switch (code)
            {
                case "missing_api_key":
                case "invalid_api_key": return new AuthenticationException(message!, code!, status);
                case "email_not_verified":
                case "source_not_allowed": return new PermissionDeniedException(message!, code!, status);
                case "quota_exceeded": return new QuotaExceededException(message!, code!, status);
                case "invalid_request":
                case "url_not_allowed": return new InvalidRequestException(message!, code!, status);
                case "payload_too_large": return new PayloadTooLargeException(message!, code!, status);
                case "render_failed": return new RenderException(message!, code!, status);
                case "render_timeout": return new RenderTimeoutException(message!, code!, status);
                case "unavailable": return new UnavailableException(message!, code!, status);
                case "rate_limit_exceeded": return new RateLimitException(message!, code!, status, RetryAfterOrBody(response, body));
                default: return new PdfApiException(message!, code!, status);
            }
        }

        public static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta) return delta;
            // Retry-After с дробью HttpClient не разбирает — читаем сами.
            if (response.Headers.TryGetValues("Retry-After", out var values))
                foreach (var v in values)
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0)
                        return TimeSpan.FromSeconds(s);
            return null;
        }
    }
}
