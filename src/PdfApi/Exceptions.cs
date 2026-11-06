using System;

namespace PdfApi
{
    /// <summary>Ошибка pdfapi: код из ответа API, HTTP-статус и текст (см. sdk/SPEC.md).</summary>
    public class PdfApiException : Exception
    {
        /// <summary>Создаёт ошибку.</summary>
        public PdfApiException(string message, string code, int? status = null, Exception? inner = null)
            : base(message, inner)
        {
            Code = code;
            Status = status;
        }

        /// <summary>Код ошибки API: <c>quota_exceeded</c>, <c>render_failed</c>, …</summary>
        public string Code { get; }

        /// <summary>HTTP-статус; <c>null</c> — ответа не было (сеть, таймаут).</summary>
        public int? Status { get; }

        /// <inheritdoc/>
        public override string ToString()
        {
            var where = Status.HasValue ? $"HTTP {Status}, " : "";
            return $"{GetType().FullName}: {Message} ({where}{Code})";
        }
    }

    /// <summary>401: ключа нет, он неверный или отозван.</summary>
    public class AuthenticationException : PdfApiException
    {
        /// <summary>Создаёт ошибку.</summary>
        public AuthenticationException(string message, string code, int? status = null) : base(message, code, status) { }
    }

    /// <summary>403: почта не подтверждена или формат недоступен на тарифе.</summary>
    public class PermissionDeniedException : PdfApiException
    {
        /// <summary>Создаёт ошибку.</summary>
        public PermissionDeniedException(string message, string code, int? status = null) : base(message, code, status) { }
    }

    /// <summary>402: страницы периода кончились или документ больше остатка.</summary>
    public class QuotaExceededException : PdfApiException
    {
        /// <summary>Создаёт ошибку.</summary>
        public QuotaExceededException(string message, string code, int? status = null) : base(message, code, status) { }
    }

    /// <summary>400: неверный запрос, параметр или адрес во внутреннюю сеть.</summary>
    public class InvalidRequestException : PdfApiException
    {
        /// <summary>Создаёт ошибку.</summary>
        public InvalidRequestException(string message, string code, int? status = null) : base(message, code, status) { }
    }

    /// <summary>413: тело запроса больше 5 МБ.</summary>
    public class PayloadTooLargeException : InvalidRequestException
    {
        /// <summary>Создаёт ошибку.</summary>
        public PayloadTooLargeException(string message, string code, int? status = null) : base(message, code, status) { }
    }

    /// <summary>429: слишком часто. <see cref="RetryAfter"/> — через сколько можно снова.</summary>
    public class RateLimitException : PdfApiException
    {
        /// <summary>Создаёт ошибку.</summary>
        public RateLimitException(string message, string code, int? status = null, TimeSpan? retryAfter = null)
            : base(message, code, status)
        {
            RetryAfter = retryAfter;
        }

        /// <summary>Через сколько повторить.</summary>
        public TimeSpan? RetryAfter { get; }
    }

    /// <summary>502: документ не отрендерился — обычно дело в разметке.</summary>
    public class RenderException : PdfApiException
    {
        /// <summary>Создаёт ошибку.</summary>
        public RenderException(string message, string code, int? status = null) : base(message, code, status) { }
    }

    /// <summary>504: рендер не уложился в 30 секунд.</summary>
    public class RenderTimeoutException : RenderException
    {
        /// <summary>Создаёт ошибку.</summary>
        public RenderTimeoutException(string message, string code, int? status = null) : base(message, code, status) { }
    }

    /// <summary>503: рендер временно недоступен или очередь полна.</summary>
    public class UnavailableException : PdfApiException
    {
        /// <summary>Создаёт ошибку.</summary>
        public UnavailableException(string message, string code, int? status = null) : base(message, code, status) { }
    }

    /// <summary>Нет связи с API: сеть, DNS, обрыв соединения.</summary>
    public class ApiConnectionException : PdfApiException
    {
        /// <summary>Создаёт ошибку.</summary>
        public ApiConnectionException(string message, string code, Exception? inner = null) : base(message, code, null, inner) { }
    }

    /// <summary>API не ответил за отведённое время.</summary>
    public class ApiTimeoutException : ApiConnectionException
    {
        /// <summary>Создаёт ошибку.</summary>
        public ApiTimeoutException(string message, string code, Exception? inner = null) : base(message, code, inner) { }
    }
}
