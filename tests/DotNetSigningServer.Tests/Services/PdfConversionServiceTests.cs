using DotNetSigningServer.Services;

namespace DotNetSigningServer.Tests.Services;

public class PdfConversionServiceTests
{
    [Theory]
    [InlineData("PDF/A-1A", "PDF/A-1A")]
    [InlineData("PDFA-1A", "PDF/A-1A")]
    [InlineData("PDF_A_1A", "PDF/A-1A")]
    [InlineData("PDF/A-1B", "PDF/A-1B")]
    [InlineData("PDFA-1B", "PDF/A-1B")]
    [InlineData("PDF_A_1B", "PDF/A-1B")]
    [InlineData("PDF/A-2A", "PDF/A-2A")]
    [InlineData("PDFA-2A", "PDF/A-2A")]
    [InlineData("PDF_A_2A", "PDF/A-2A")]
    [InlineData("PDF/A-2B", "PDF/A-2B")]
    [InlineData("PDFA-2B", "PDF/A-2B")]
    [InlineData("PDF_A_2B", "PDF/A-2B")]
    [InlineData("PDF/A-2U", "PDF/A-2U")]
    [InlineData("PDFA-2U", "PDF/A-2U")]
    [InlineData("PDF_A_2U", "PDF/A-2U")]
    [InlineData("PDF/A-3A", "PDF/A-3A")]
    [InlineData("PDFA-3A", "PDF/A-3A")]
    [InlineData("PDF_A_3A", "PDF/A-3A")]
    [InlineData("PDF/A-3B", "PDF/A-3B")]
    [InlineData("PDFA-3B", "PDF/A-3B")]
    [InlineData("PDF_A_3B", "PDF/A-3B")]
    [InlineData("PDF/A-3U", "PDF/A-3U")]
    [InlineData("PDFA-3U", "PDF/A-3U")]
    [InlineData("PDF_A_3U", "PDF/A-3U")]
    [InlineData("PDF/A-4", "PDF/A-4")]
    [InlineData("PDFA-4", "PDF/A-4")]
    [InlineData("PDF_A_4", "PDF/A-4")]
    [InlineData("PDF/A-4E", "PDF/A-4E")]
    [InlineData("PDFA-4E", "PDF/A-4E")]
    [InlineData("PDF_A_4E", "PDF/A-4E")]
    [InlineData("PDF/A-4F", "PDF/A-4F")]
    [InlineData("PDFA-4F", "PDF/A-4F")]
    [InlineData("PDF_A_4F", "PDF/A-4F")]
    public void FormatConformance_RecognizedFormats_ReturnsCanonical(string input, string expected)
    {
        Assert.Equal(expected, PdfConversionService.FormatConformance(input));
    }

    [Theory]
    [InlineData("pdf/a-1a", "PDF/A-1A")]
    [InlineData("pdfa-2b", "PDF/A-2B")]
    [InlineData("pdf_a_3u", "PDF/A-3U")]
    public void FormatConformance_CaseInsensitive(string input, string expected)
    {
        Assert.Equal(expected, PdfConversionService.FormatConformance(input));
    }

    [Fact]
    public void FormatConformance_Null_ReturnsDefault()
    {
        Assert.Equal("PDF/A-2B", PdfConversionService.FormatConformance(null));
    }

    [Fact]
    public void FormatConformance_Empty_ReturnsDefault()
    {
        Assert.Equal("PDF/A-2B", PdfConversionService.FormatConformance(""));
    }

    [Fact]
    public void FormatConformance_Whitespace_ReturnsDefault()
    {
        Assert.Equal("PDF/A-2B", PdfConversionService.FormatConformance("   "));
    }

    [Theory]
    [InlineData("PDF/A-5")]
    [InlineData("garbage")]
    [InlineData("PDF/X-1A")]
    [InlineData("PDFA")]
    public void FormatConformance_Unrecognized_ReturnsDefault(string input)
    {
        Assert.Equal("PDF/A-2B", PdfConversionService.FormatConformance(input));
    }

    [Theory]
    [InlineData(" PDF/A-1A ", "PDF/A-1A")]
    [InlineData("  PDFA-3B  ", "PDF/A-3B")]
    public void FormatConformance_TrimsWhitespace(string input, string expected)
    {
        Assert.Equal(expected, PdfConversionService.FormatConformance(input));
    }
}
