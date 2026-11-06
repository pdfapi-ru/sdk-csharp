using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace PdfApi.Tests
{
    /// <summary>Общие тест-кейсы SDK (sdk/tests/cases.json): что ответил сервер → что сделал SDK.</summary>
    public class CasesTests
    {
        private static JsonElement[] Load()
        {
            // Ищем sdk/tests/cases.json вверх от папки сборки тестов.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tests", "cases.json"))) dir = dir.Parent;
            if (dir == null) throw new FileNotFoundException("sdk/tests/cases.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "tests", "cases.json")));
            return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone()).ToArray();
        }

        public static IEnumerable<object[]> Cases() => Load().Select(c => new object[] { c.GetProperty("name").GetString()! });

        private static readonly Dictionary<string, string> ClassNames = new()
        {
            ["PdfApiError"] = nameof(PdfApiException), ["AuthenticationError"] = nameof(AuthenticationException),
            ["PermissionDeniedError"] = nameof(PermissionDeniedException), ["QuotaExceededError"] = nameof(QuotaExceededException),
            ["InvalidRequestError"] = nameof(InvalidRequestException), ["PayloadTooLargeError"] = nameof(PayloadTooLargeException),
            ["RateLimitError"] = nameof(RateLimitException), ["RenderError"] = nameof(RenderException),
            ["RenderTimeoutError"] = nameof(RenderTimeoutException), ["UnavailableError"] = nameof(UnavailableException),
            ["APIConnectionError"] = nameof(ApiConnectionException), ["APITimeoutError"] = nameof(ApiTimeoutException),
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task SharedCase(string name)
        {
            var c = Load().Single(x => x.GetProperty("name").GetString() == name);
            var handler = new FakeHandler(c.GetProperty("responses").EnumerateArray().Select(s => (Func<System.Net.Http.HttpResponseMessage>)(() => FakeHandler.FromSpec(s))));
            var sleeps = new List<TimeSpan>();
            using var client = Clients.With(handler, sleeps);

            var call = c.GetProperty("call");
            var method = call.GetProperty("method").GetString();
            var args = call.GetProperty("args");
            PageOptions? options = null;
            if (call.TryGetProperty("options", out var o))
                options = JsonSerializer.Deserialize<PageOptions>(o.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            object? outcome;
            try
            {
                outcome = method switch
                {
                    "html" => await client.HtmlAsync(args.GetProperty("html").GetString()!, options),
                    "url" => await client.UrlAsync(args.GetProperty("url").GetString()!, options),
                    "markdown" => await client.MarkdownAsync(args.GetProperty("markdown").GetString()!, options),
                    "office" => await client.OfficeAsync(Convert.FromBase64String(args.GetProperty("file_base64").GetString()!),
                        args.GetProperty("filename").GetString()!, options?.Landscape),
                    "me" => await client.MeAsync(),
                    _ => throw new InvalidOperationException(method),
                };
            }
            catch (PdfApiException e)
            {
                outcome = e;
            }
            catch (ArgumentException e)
            {
                outcome = e;
            }

            var expect = c.GetProperty("expect");
            Assert.Equal(expect.GetProperty("attempts").GetInt32(), handler.Requests.Count);

            if (expect.TryGetProperty("request", out var req))
            {
                var r = handler.Requests[0];
                Assert.Equal(req.GetProperty("method").GetString(), r.Method.Method);
                Assert.EndsWith(req.GetProperty("path").GetString()!, r.Uri.AbsolutePath);
                if (req.TryGetProperty("json", out var json))
                    Assert.True(JsonEquals(json, JsonDocument.Parse(r.Body!).RootElement), $"тело {r.Body}");
                if (req.TryGetProperty("raw_contains", out var raw))
                    Assert.Contains(raw.GetString()!, r.Body);
            }
            if (expect.TryGetProperty("result", out var result))
            {
                var pdf = Assert.IsType<PdfResult>(outcome);
                Assert.Equal(Convert.FromBase64String(result.GetProperty("content_base64").GetString()!), pdf.Content);
                var pages = result.GetProperty("pages");
                Assert.Equal(pages.ValueKind == JsonValueKind.Null ? null : pages.GetInt32(), pdf.Pages);
                var quota = result.GetProperty("quota");
                if (quota.ValueKind == JsonValueKind.Null) Assert.Null(pdf.Quota);
                else
                {
                    Assert.NotNull(pdf.Quota);
                    Assert.Equal(quota.GetProperty("limit").GetInt32(), pdf.Quota!.Limit);
                    Assert.Equal(quota.GetProperty("used").GetInt32(), pdf.Quota.Used);
                    Assert.Equal(quota.GetProperty("remaining").GetInt32(), pdf.Quota.Remaining);
                }
            }
            if (expect.TryGetProperty("me", out var me))
            {
                var a = Assert.IsType<Account>(outcome);
                var got = new Dictionary<string, object?>
                {
                    ["user_id"] = a.UserId, ["plan"] = a.Plan, ["period_start"] = a.PeriodStart, ["period_end"] = a.PeriodEnd,
                    ["pages_used"] = (double)a.PagesUsed, ["pages_limit"] = (double)a.PagesLimit,
                    ["pages_remaining"] = (double)a.PagesRemaining, ["requests_per_second"] = a.RequestsPerSecond, ["burst"] = (double)a.Burst,
                };
                foreach (var p in me.EnumerateObject())
                {
                    object? want = p.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.Number => p.Value.GetDouble(),
                        _ when p.Name.StartsWith("period_") => DateTimeOffset.Parse(p.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                        _ => p.Value.GetString(),
                    };
                    Assert.True(Equals(want, got[p.Name]), $"{p.Name}: {got[p.Name]}, ждали {want}");
                }
            }
            if (expect.TryGetProperty("argument_error", out var argError))
            {
                // Ошибка аргумента, а не API: запрос не ушёл.
                var e = Assert.IsAssignableFrom<ArgumentException>(outcome);
                Assert.Contains(argError.GetProperty("mentions").GetString()!, e.Message, StringComparison.OrdinalIgnoreCase);
            }
            if (expect.TryGetProperty("error", out var err))
            {
                var e = Assert.IsAssignableFrom<PdfApiException>(outcome);
                Assert.Equal(ClassNames[err.GetProperty("class").GetString()!], e.GetType().Name);
                Assert.Equal(err.GetProperty("code").GetString(), e.Code);
                Assert.Equal(err.GetProperty("status").GetInt32(), e.Status);
                if (err.TryGetProperty("message_contains", out var m)) Assert.Contains(m.GetString()!, e.Message);
                if (err.TryGetProperty("retry_after", out var ra))
                    Assert.Equal(TimeSpan.FromSeconds(ra.GetDouble()), Assert.IsType<RateLimitException>(e).RetryAfter);
            }
            if (expect.TryGetProperty("sleeps", out var exactSleeps))
                Assert.Equal(exactSleeps.EnumerateArray().Select(s => s.GetDouble()), sleeps.Select(s => s.TotalSeconds));
            if (expect.TryGetProperty("sleeps_between", out var between))
            {
                Assert.NotEmpty(sleeps);
                Assert.All(sleeps, s => Assert.InRange(s.TotalSeconds, between[0].GetDouble(), between[1].GetDouble()));
            }
        }

        private static bool JsonEquals(JsonElement a, JsonElement b)
        {
            if (a.ValueKind != b.ValueKind) return false;
            return a.ValueKind switch
            {
                JsonValueKind.Object => a.EnumerateObject().Count() == b.EnumerateObject().Count()
                    && a.EnumerateObject().All(p => b.TryGetProperty(p.Name, out var q) && JsonEquals(p.Value, q)),
                JsonValueKind.Array => a.GetArrayLength() == b.GetArrayLength()
                    && a.EnumerateArray().Zip(b.EnumerateArray()).All(t => JsonEquals(t.First, t.Second)),
                JsonValueKind.String => a.GetString() == b.GetString(),
                _ => a.GetRawText() == b.GetRawText(),
            };
        }
    }
}
