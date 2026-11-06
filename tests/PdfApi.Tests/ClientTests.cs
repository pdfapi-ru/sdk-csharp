using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PdfApi.Tests
{
    [Collection("env")] // тесты с переменными окружения — не параллельно
    public class ClientTests
    {
        private static FakeHandler Script(params Func<HttpResponseMessage>[] steps) => new(steps);

        [Fact]
        public void NoKeyFailsAtConstruction()
        {
            Environment.SetEnvironmentVariable("PDFAPI_KEY", null);
            var e = Assert.Throws<ArgumentException>(() => new PdfApiClient());
            Assert.Contains("PDFAPI_KEY", e.Message);
        }

        [Fact]
        public void SettingsFromEnv()
        {
            try
            {
                Environment.SetEnvironmentVariable("PDFAPI_KEY", "pdf_live_env");
                Environment.SetEnvironmentVariable("PDFAPI_BASE_URL", "http://localhost:18080/api/");
                using var c = new PdfApiClient();
                Assert.Equal("http://localhost:18080/api", c.BaseUrl);
                Environment.SetEnvironmentVariable("PDFAPI_BASE_URL", null);
                using var d = new PdfApiClient("explicit");
                Assert.Equal(PdfApiClient.DefaultBaseUrl, d.BaseUrl);
            }
            finally
            {
                Environment.SetEnvironmentVariable("PDFAPI_KEY", null);
                Environment.SetEnvironmentVariable("PDFAPI_BASE_URL", null);
            }
        }

        [Fact]
        public async Task HeadersAndKeyNeverLeaks()
        {
            var h = Script(() => FakeHandler.Json(401, "{\"error\":\"invalid_api_key\"}"));
            using var c = Clients.With(h);
            var e = await Assert.ThrowsAsync<AuthenticationException>(() => c.HtmlAsync("<p>x</p>"));
            var r = h.Requests[0];
            Assert.Equal("https://pdfapi.test/api/v1/convert", r.Uri.ToString());
            Assert.Equal("Bearer " + Clients.Key, r.Headers["Authorization"]);
            Assert.StartsWith($"pdfapi-dotnet/{PdfApiClient.Version} (.NET", r.Headers["User-Agent"]);
            Assert.DoesNotContain(Clients.Key, e.ToString());
            Assert.DoesNotContain(Clients.Key, c.ToString());
        }

        [Fact]
        public async Task CyrillicAndHtmlAreNotEscaped()
        {
            var h = Script(() => FakeHandler.Pdf());
            using var c = Clients.With(h);
            await c.HtmlAsync("<h1>Счёт</h1>");
            Assert.Contains("\"html\":\"<h1>Счёт</h1>\"", h.Requests[0].Body);
        }

        [Fact]
        public void ExceptionHierarchy()
        {
            Assert.IsAssignableFrom<InvalidRequestException>(new PayloadTooLargeException("x", "payload_too_large"));
            Assert.IsAssignableFrom<RenderException>(new RenderTimeoutException("x", "render_timeout"));
            Assert.IsAssignableFrom<ApiConnectionException>(new ApiTimeoutException("x", "timeout"));
        }

        [Fact]
        public async Task OptionsAreCamelCaseAndOnlyGiven()
        {
            var h = Script(() => FakeHandler.Pdf());
            using var c = Clients.With(h);
            await c.HtmlAsync("<p>x</p>", new PageOptions { Landscape = false, MarginTop = "20mm", WaitDelay = "1s" });
            var options = JsonDocument.Parse(h.Requests[0].Body!).RootElement.GetProperty("options");
            Assert.Equal("{\"landscape\":false,\"marginTop\":\"20mm\",\"waitDelay\":\"1s\"}", options.GetRawText());
        }

        [Fact]
        public async Task BadArgumentsFailBeforeRequest()
        {
            var h = Script(() => FakeHandler.Pdf());
            using var c = Clients.With(h);
            await Assert.ThrowsAsync<ArgumentException>(() => c.HtmlAsync("<p>x</p>", new PageOptions { MarginTop = "  " }));
            await Assert.ThrowsAsync<ArgumentException>(() => c.MarkdownAsync(" "));
            await Assert.ThrowsAsync<ArgumentException>(() => c.OfficeAsync(new byte[] { 1 }, "scan.pdf"));
            var big = new byte[PdfApiClient.OfficeMaxBytes + 1];
            new byte[] { 0x50, 0x4B, 3, 4 }.CopyTo(big, 0);
            var tooBig = await Assert.ThrowsAsync<ArgumentException>(() => c.OfficeAsync(big, "big.pptx"));
            Assert.Contains("3,7 МБ", tooBig.Message);
            await Assert.ThrowsAsync<ArgumentException>(() => c.HtmlAsync("<p>x</p>", new PageOptions { MarginTop = "20" }));
            await Assert.ThrowsAsync<ArgumentException>(() => c.HtmlAsync("<p>x</p>", new PageOptions { WaitDelay = "11s" }));
            Assert.Empty(h.Requests);
        }

        [Fact]
        public async Task OfficeFromFile()
        {
            var path = Path.Combine(Path.GetTempPath(), $"Отчёт-{Guid.NewGuid():N}.docx");
            File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 3, 4 });
            var h = Script(() => FakeHandler.Pdf());
            using var c = Clients.With(h);
            await c.OfficeAsync(path, landscape: true);
            File.Delete(path);
            var body = JsonDocument.Parse(h.Requests[0].Body!).RootElement;
            Assert.Equal(Path.GetFileName(path), body.GetProperty("filename").GetString());
            Assert.Equal(Convert.ToBase64String(new byte[] { 0x50, 0x4B, 3, 4 }), body.GetProperty("fileBase64").GetString());
            Assert.True(body.GetProperty("options").GetProperty("landscape").GetBoolean());
        }

        [Fact]
        public void BadSettingsFailAtConstruction()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PdfApiClient(Clients.Key, new PdfApiClientOptions { Timeout = TimeSpan.Zero }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PdfApiClient(Clients.Key, new PdfApiClientOptions { Timeout = System.Threading.Timeout.InfiniteTimeSpan }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PdfApiClient(Clients.Key, new PdfApiClientOptions { MaxRetries = -1 }));
        }

        private static string TempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"pdfapi-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static PdfResult NewPdf() => new PdfResult(System.Text.Encoding.ASCII.GetBytes("%PDF-new"), 1, null);

        [Fact]
        public async Task SaveReplacesFileAndLeavesNoTemp()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "out.pdf");
            File.WriteAllText(path, "old content that is longer than the new one");
            NewPdf().Save(path);
            Assert.Equal("%PDF-new", File.ReadAllText(path));
            File.WriteAllText(path, "old content that is longer than the new one");
            await NewPdf().SaveAsync(path);
            Assert.Equal("%PDF-new", File.ReadAllText(path));
            Assert.Equal(new[] { "out.pdf" }, Directory.GetFiles(dir).Select(Path.GetFileName));
        }

        [Fact]
        public async Task SaveFailureKeepsOldFile()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "out.pdf");
            File.WriteAllText(path, "%PDF-old");
            var flush = PdfResult.FlushToDisk;
            PdfResult.FlushToDisk = _ => throw new IOException("диск отвалился");
            try
            {
                Assert.Equal("диск отвалился", Assert.Throws<IOException>(() => NewPdf().Save(path)).Message);
                await Assert.ThrowsAsync<IOException>(() => NewPdf().SaveAsync(path));
            }
            finally
            {
                PdfResult.FlushToDisk = flush;
            }
            Assert.Equal("%PDF-old", File.ReadAllText(path));
            Assert.Equal(new[] { "out.pdf" }, Directory.GetFiles(dir).Select(Path.GetFileName));
        }

        [Fact]
        public void SavePermissionsAndMissingDirectory()
        {
            var dir = TempDir();
            var path = Path.Combine(dir, "out.pdf");
            NewPdf().Save(path);
            if (!OperatingSystem.IsWindows())
            {
                // Права — как у обычного нового файла.
                var reference = Path.Combine(dir, "reference");
                File.WriteAllBytes(reference, new byte[] { 1 });
                Assert.Equal(File.GetUnixFileMode(reference), File.GetUnixFileMode(path));
            }
            Assert.Throws<DirectoryNotFoundException>(() => NewPdf().Save(Path.Combine(dir, "нет", "out.pdf")));
            Assert.False(Directory.Exists(Path.Combine(dir, "нет")));
        }

        [Fact]
        public async Task SaveWritesFile()
        {
            using var c = Clients.With(Script(() => FakeHandler.Pdf()));
            var pdf = await c.HtmlAsync("<p>x</p>");
            var path = Path.GetTempFileName();
            pdf.Save(path);
            Assert.Equal("%PDF-1.4", File.ReadAllText(path));
            File.Delete(path);
            Assert.DoesNotContain("%PDF", pdf.ToString());
        }

        private static HttpRequestException Refused() =>
            new("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

        [Fact]
        public async Task ConnectionRefusedIsRetried()
        {
            var sleeps = new List<TimeSpan>();
            var h = Script(() => throw Refused(), () => FakeHandler.Pdf());
            using var c = Clients.With(h, sleeps);
            Assert.Equal(1, (await c.HtmlAsync("<p>x</p>")).Pages);
            Assert.Equal(2, h.Requests.Count);
            Assert.InRange(Assert.Single(sleeps).TotalSeconds, 0.375, 0.625);

            var h2 = Script(() => throw Refused());
            using var c2 = Clients.With(h2);
            var e = await Assert.ThrowsAsync<ApiConnectionException>(() => c2.HtmlAsync("<p>x</p>"));
            Assert.Equal("connection_error", e.Code);
            Assert.Equal(3, h2.Requests.Count);
        }

        [Fact]
        public async Task BrokenResponseIsNotRetried()
        {
            // Сервер мог успеть отрендерить и списать страницы — повтор списал бы их дважды.
            var sleeps = new List<TimeSpan>();
            var h = Script(() => throw new HttpRequestException("reset", new IOException("connection reset")), () => FakeHandler.Pdf());
            using var c = Clients.With(h, sleeps);
            var e = await Assert.ThrowsAsync<ApiConnectionException>(() => c.HtmlAsync("<p>x</p>"));
            Assert.IsNotType<ApiTimeoutException>(e);
            Assert.Single(h.Requests);
            Assert.Empty(sleeps);
        }

        private sealed class SlowHandler : HttpMessageHandler
        {
            public int Calls;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return FakeHandler.Pdf();
            }
        }

        [Fact]
        public async Task TimeoutIsNotRetried()
        {
            var h = new SlowHandler();
            using var c = new PdfApiClient(Clients.Key, new PdfApiClientOptions { BaseUrl = "https://pdfapi.test", Timeout = TimeSpan.FromMilliseconds(100), HttpClient = new HttpClient(h) });
            var e = await Assert.ThrowsAsync<ApiTimeoutException>(() => c.HtmlAsync("<p>x</p>"));
            Assert.Equal("timeout", e.Code);
            Assert.Equal(1, h.Calls);
        }

        [Fact]
        public async Task CallerCancellationIsRethrown()
        {
            using var c = new PdfApiClient(Clients.Key, new PdfApiClientOptions { BaseUrl = "https://pdfapi.test", HttpClient = new HttpClient(new SlowHandler()) });
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.HtmlAsync("<p>x</p>", cancellationToken: cts.Token));
        }

        [Fact]
        public async Task MaxRetriesZero()
        {
            var h = Script(() => FakeHandler.Json(503, "{\"error\":\"unavailable\"}"), () => FakeHandler.Pdf());
            using var c = Clients.With(h, maxRetries: 0);
            await Assert.ThrowsAsync<UnavailableException>(() => c.HtmlAsync("<p>x</p>"));
            Assert.Single(h.Requests);
        }
    }
}
