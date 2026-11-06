using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace PdfApi.Tests
{
    /// <summary>
    /// Против настоящего API:
    /// PDFAPI_IT_KEY=pdf_live_... PDFAPI_IT_BASE_URL=http://localhost:18080/api dotnet test --filter Category=Integration
    /// </summary>
    [Trait("Category", "Integration")]
    public class IntegrationTests
    {
        private static PdfApiClient Client()
        {
            var key = Environment.GetEnvironmentVariable("PDFAPI_IT_KEY");
            var url = Environment.GetEnvironmentVariable("PDFAPI_IT_BASE_URL");
            Skip.If(string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url), "нужны PDFAPI_IT_KEY и PDFAPI_IT_BASE_URL");
            return new PdfApiClient(key, new PdfApiClientOptions { BaseUrl = url });
        }

        [SkippableFact]
        public async Task HtmlToPdf()
        {
            using var c = Client();
            var pdf = await c.HtmlAsync("<h1>.NET SDK</h1><div style='page-break-before:always'>2</div>");
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 4));
            Assert.Equal(2, pdf.Pages);
            Assert.NotNull(pdf.Quota);
        }

        [SkippableFact]
        public async Task AllFormatsAndSave()
        {
            using var c = Client();
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures"))) dir = dir.Parent;
            Skip.If(dir == null, "нет sdk/tests/fixtures");
            string Fixture(string name) => Path.Combine(dir!.FullName, "tests", "fixtures", name);
            var results = new[]
            {
                await c.MarkdownAsync("# Отчёт\n\n- пункт один\n- пункт два"),
                await c.UrlAsync("https://example.com", new PageOptions { WaitDelay = "500ms" }),
                await c.OfficeAsync(Fixture("report.docx")),
                await c.OfficeAsync(Fixture("table.xlsx"), landscape: true),
                await c.OfficeAsync(File.ReadAllBytes(Fixture("slides.pptx")), "slides.pptx"),
            };
            foreach (var pdf in results)
            {
                Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 4));
                Assert.True(pdf.Pages >= 1);
            }
            var path = Path.Combine(Path.GetTempPath(), $"pdfapi-it-{Guid.NewGuid():N}.pdf");
            await results[0].SaveAsync(path);
            Assert.Equal(results[0].Content, File.ReadAllBytes(path));
            File.Delete(path);
        }

        [SkippableFact]
        public async Task MeAndErrors()
        {
            using var c = Client();
            var me = await c.MeAsync();
            Assert.True(me.PagesLimit > 0);
            Assert.NotEqual("", me.UserId);
            Assert.Equal(me.PagesLimit, me.PagesUsed + me.PagesRemaining);
            Assert.True(me.PeriodEnd > me.PeriodStart);
            using var bad = new PdfApiClient("pdf_live_wrong", new PdfApiClientOptions { BaseUrl = c.BaseUrl });
            await Assert.ThrowsAsync<AuthenticationException>(() => bad.HtmlAsync("<p>x</p>"));
            var e = await Assert.ThrowsAsync<InvalidRequestException>(() => c.ConvertAsync(new Dictionary<string, object?>
            {
                ["source"] = "html", ["html"] = "<p>x</p>", ["options"] = new Dictionary<string, object> { ["foo"] = "bar" },
            }));
            Assert.Contains("foo", e.Message);
        }
    }
}
