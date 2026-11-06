using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PdfApi
{
    /// <summary>
    /// Параметры страницы. <c>null</c> — не отправлять. Размеры — число и единица
    /// <c>pt</c>, <c>px</c>, <c>in</c>, <c>mm</c>, <c>cm</c> или <c>pc</c>: <c>"20mm"</c>, <c>"8.27in"</c>.
    /// </summary>
    public sealed class PageOptions
    {
        /// <summary>Альбомная ориентация.</summary>
        public bool? Landscape { get; set; }
        /// <summary>Ширина листа, например <c>"8.27in"</c>.</summary>
        public string? PaperWidth { get; set; }
        /// <summary>Высота листа, например <c>"297mm"</c>.</summary>
        public string? PaperHeight { get; set; }
        /// <summary>Верхнее поле, например <c>"20mm"</c>.</summary>
        public string? MarginTop { get; set; }
        /// <summary>Нижнее поле.</summary>
        public string? MarginBottom { get; set; }
        /// <summary>Левое поле.</summary>
        public string? MarginLeft { get; set; }
        /// <summary>Правое поле.</summary>
        public string? MarginRight { get; set; }
        /// <summary>Подождать перед печатью, например <c>"2s"</c> или <c>"500ms"</c>, до 10 секунд: страница дорисовывается скриптом.</summary>
        public string? WaitDelay { get; set; }

        internal Dictionary<string, object> ToDictionary()
        {
            var o = new Dictionary<string, object>();
            if (Landscape.HasValue) o["landscape"] = Landscape.Value;
            Add(o, "paperWidth", PaperWidth);
            Add(o, "paperHeight", PaperHeight);
            Add(o, "marginTop", MarginTop);
            Add(o, "marginBottom", MarginBottom);
            Add(o, "marginLeft", MarginLeft);
            Add(o, "marginRight", MarginRight);
            Add(o, "waitDelay", WaitDelay);
            return o;
        }

        // Число и единица, как понимает рендер. Без единицы сервер считает дюймами, но "20" почти всегда задумано как мм.
        private static readonly Regex Size = new Regex(@"^(\d+(\.\d*)?|\.\d+)(pt|px|in|mm|cm|pc)$", RegexOptions.CultureInvariant);
        // WaitDelay — как на сервере: число и s или ms, не больше 10 секунд.
        private static readonly Regex WaitDelayFormat = new Regex(@"^(\d+(?:\.\d+)?)(ms|s)$", RegexOptions.CultureInvariant);

        private static void Add(Dictionary<string, object> o, string name, string? value)
        {
            if (value == null) return;
            if (name == "waitDelay")
            {
                var m = WaitDelayFormat.Match(value);
                if (!m.Success || double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / (m.Groups[2].Value == "ms" ? 1000 : 1) > 10)
                    throw new ArgumentException($"waitDelay: \"{value}\" — нужно от 0 до 10 секунд в s или ms, например \"2s\" или \"500ms\"", name);
            }
            else if (!Size.IsMatch(value))
            {
                // Иначе рендер падает с 502, и опечатка выглядит как сбой сервиса.
                throw new ArgumentException(
                    $"{name}: \"{value}\" — нужно число с единицами pt, px, in, mm, cm или pc без пробела, например \"20mm\"", name);
            }
            o[name] = value;
        }
    }

    /// <summary>Квота периода после конвертации — из заголовков X-Quota-*.</summary>
    public sealed class Quota
    {
        /// <summary>Создаёт квоту.</summary>
        public Quota(int limit, int used, int remaining) { Limit = limit; Used = used; Remaining = remaining; }
        /// <summary>Лимит страниц в периоде.</summary>
        public int Limit { get; }
        /// <summary>Использовано.</summary>
        public int Used { get; }
        /// <summary>Осталось.</summary>
        public int Remaining { get; }
    }

    /// <summary>Готовый PDF.</summary>
    public sealed class PdfResult
    {
        internal PdfResult(byte[] content, int? pages, Quota? quota) { Content = content; Pages = pages; Quota = quota; }
        /// <summary>Байты PDF.</summary>
        public byte[] Content { get; }
        /// <summary>Сколько страниц списано; <c>null</c> — сервер не сообщил.</summary>
        public int? Pages { get; }
        /// <summary>Остаток квоты; <c>null</c> — сервер не сообщил.</summary>
        public Quota? Quota { get; }
        /// <summary>
        /// Записать PDF в файл. Атомарно: сначала во временный файл рядом, затем переименование поверх —
        /// оборвётся запись, на месте останется прежний файл, а не обрезанный PDF. Каталог не создаётся.
        /// </summary>
        public void Save(string path)
        {
            var tmp = TempPath(path);
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Content, 0, Content.Length);
                    FlushToDisk(stream);
                }
                Replace(tmp, path);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>То же, что <see cref="Save"/>, но запись асинхронная.</summary>
        public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
        {
            var tmp = TempPath(path);
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await stream.WriteAsync(Content, 0, Content.Length, cancellationToken).ConfigureAwait(false);
                    FlushToDisk(stream);
                }
                Replace(tmp, path);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>Сброс на диск — подменяется в тестах, чтобы изобразить сбой.</summary>
        internal static Action<FileStream> FlushToDisk { get; set; } = s => s.Flush(flushToDisk: true);

        private static string TempPath(string path)
        {
            var name = $".{Path.GetFileName(path)}.{Guid.NewGuid().ToString("N").Substring(0, 8)}.tmp";
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name);
        }

        private static void Replace(string tmp, string path)
        {
#if NET8_0_OR_GREATER
            File.Move(tmp, path, overwrite: true);
#else
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
#endif
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        /// <inheritdoc/>
        public override string ToString() => $"PdfResult(Pages={Pages}, Size={Content.Length} B)";
    }

    /// <summary>Тариф и расход — ответ GET /v1/me.</summary>
    public sealed class Account
    {
        internal Account(string userId, string plan, DateTimeOffset? periodStart, DateTimeOffset? periodEnd,
            int used, int limit, int remaining, double rps, int burst)
        {
            UserId = userId; Plan = plan; PeriodStart = periodStart; PeriodEnd = periodEnd;
            PagesUsed = used; PagesLimit = limit; PagesRemaining = remaining; RequestsPerSecond = rps; Burst = burst;
        }
        /// <summary>Идентификатор аккаунта.</summary>
        public string UserId { get; }
        /// <summary>Тариф: free, premium, business.</summary>
        public string Plan { get; }
        /// <summary>Начало периода, UTC; <c>null</c> — сервер не сообщил.</summary>
        public DateTimeOffset? PeriodStart { get; }
        /// <summary>Когда счётчик страниц начнётся с нуля, UTC; <c>null</c> — сервер не сообщил.</summary>
        public DateTimeOffset? PeriodEnd { get; }
        /// <summary>Использовано страниц.</summary>
        public int PagesUsed { get; }
        /// <summary>Лимит страниц.</summary>
        public int PagesLimit { get; }
        /// <summary>Осталось страниц.</summary>
        public int PagesRemaining { get; }
        /// <summary>Запросов в секунду.</summary>
        public double RequestsPerSecond { get; }
        /// <summary>Сколько запросов можно подряд.</summary>
        public int Burst { get; }
    }
}
