namespace PdfApi.Examples;

/// <summary>docx, xlsx или pptx в PDF (тариф premium и выше).</summary>
internal static class Office
{
    public static async Task<int> RunAsync(string[] args)
    {
        var source = args.Length > 0 ? args[0] : "report.docx";
        using var client = new PdfApiClient();
        var pdf = await client.OfficeAsync(source); // из памяти — OfficeAsync(bytes, "report.docx")
        var target = Path.ChangeExtension(source, ".pdf");
        await pdf.SaveAsync(target);
        Console.WriteLine($"{target}: {pdf.Pages} стр.");
        return 0;
    }
}
