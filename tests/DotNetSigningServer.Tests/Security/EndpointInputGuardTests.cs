using System.Text.RegularExpressions;

namespace DotNetSigningServer.Tests.Security;

/// <summary>
/// #70: every endpoint that takes a PDF goes through the same structural check.
/// A new endpoint that checks the byte size but forgets PdfSafetyGuard fails here,
/// which is the point: the nine endpoints that lacked it all looked finished.
/// </summary>
public class EndpointInputGuardTests
{
    private static string ControllersDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Controllers")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Controllers");
    }

    private static IEnumerable<(string File, string Action, string Body)> Actions()
    {
        foreach (var file in Directory.GetFiles(ControllersDir(), "*.cs"))
        {
            var text = File.ReadAllText(file);
            var parts = Regex.Split(text, @"(?=\[Http(?:Post|Put)\(""/api/)");
            foreach (var part in parts.Skip(1))
            {
                var route = Regex.Match(part, @"\[Http(?:Post|Put)\(""(/api/[^""]+)""").Groups[1].Value;
                yield return (Path.GetFileName(file), route, part);
            }
        }
    }

    [Fact]
    public void Every_action_that_limits_a_pdf_also_checks_its_structure()
    {
        var missing = Actions()
            .Where(a => a.Body.Contains("EnsurePdfWithinLimit("))
            .Where(a => !a.Body.Contains("EnsureSafePdf("))
            .Select(a => $"{a.File}: {a.Action}")
            .ToList();

        Assert.True(missing.Count == 0, "Missing PdfSafetyGuard: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_endpoints_that_take_a_pdf_are_the_ones_we_know_about()
    {
        var withPdf = Actions()
            .Where(a => a.Body.Contains("EnsurePdfWithinLimit(") || a.Body.Contains("input.PdfContent"))
            .Select(a => a.Action)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        // A new route that takes a PDF has to be added here — and has to call both guards.
        var expected = new[]
        {
            "/api/ai/detect-fields", "/api/ai/extract-data", "/api/attachment", "/api/convert/pdfa",
            "/api/extend-signature", "/api/fill-pdf", "/api/find-codes", "/api/inspect-signatures",
            "/api/pdf-template", "/api/pdf-template/{templateId:guid}", "/api/presign", "/api/seal",
            "/api/sign-pfx", "/api/timestamp", "/api/visual-sign",
        };
        Assert.Equal(expected, withPdf);
    }
}
