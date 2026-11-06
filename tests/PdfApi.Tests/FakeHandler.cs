using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PdfApi.Tests
{
    /// <summary>Записанный запрос: тело читается сразу, пока сообщение живо.</summary>
    public sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body);

    /// <summary>Подменный HTTP: отвечает по очереди (ответ или исключение) и запоминает запросы.</summary>
    public sealed class FakeHandler : HttpMessageHandler
    {
        private readonly List<Func<HttpResponseMessage>> _script;
        public List<Recorded> Requests { get; } = new();

        public FakeHandler(IEnumerable<Func<HttpResponseMessage>> script) => _script = script.ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // User-Agent HttpClient хранит частями — на проводе они через пробел.
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(h.Key == "User-Agent" ? " " : ", ", h.Value));
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Recorded(request.Method, request.RequestUri!, headers, body));
            return _script[Math.Min(Requests.Count - 1, _script.Count - 1)]();
        }

        public static HttpResponseMessage Pdf(string body = "%PDF-1.4", int? pages = 1)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(body)) };
            if (pages.HasValue) r.Headers.TryAddWithoutValidation("X-Pages-Rendered", pages.Value.ToString());
            return r;
        }

        public static HttpResponseMessage Json(int status, string json) =>
            new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        /// <summary>Ответ из спецификации общих кейсов (sdk/tests/cases.json).</summary>
        public static HttpResponseMessage FromSpec(JsonElement spec)
        {
            var status = (HttpStatusCode)spec.GetProperty("status").GetInt32();
            HttpContent content;
            if (spec.TryGetProperty("json", out var json)) content = new StringContent(json.GetRawText(), Encoding.UTF8, "application/json");
            else if (spec.TryGetProperty("text", out var text)) content = new StringContent(text.GetString()!);
            else content = new ByteArrayContent(Convert.FromBase64String(spec.TryGetProperty("body_base64", out var b) ? b.GetString()! : ""));
            var r = new HttpResponseMessage(status) { Content = content };
            if (spec.TryGetProperty("headers", out var hs))
                foreach (var h in hs.EnumerateObject())
                    if (!r.Headers.TryAddWithoutValidation(h.Name, h.Value.GetString()))
                    {
                        r.Content.Headers.Remove(h.Name);
                        r.Content.Headers.TryAddWithoutValidation(h.Name, h.Value.GetString());
                    }
            return r;
        }
    }

    internal static class Clients
    {
        public const string Key = "pdf_live_secret_test_key_123";

        public static PdfApiClient With(FakeHandler handler, List<TimeSpan>? sleeps = null, int maxRetries = 2)
        {
            var client = new PdfApiClient(Key, new PdfApiClientOptions
            {
                BaseUrl = "https://pdfapi.test/api/",
                MaxRetries = maxRetries,
                HttpClient = new HttpClient(handler),
            });
            client.Delay = (d, _) => { sleeps?.Add(d); return Task.CompletedTask; };
            return client;
        }
    }
}
