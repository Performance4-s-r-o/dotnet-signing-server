using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;

namespace DotNetSigningServer.Tests.Services;

public class ContentLimitGuardTests
{
    private static ContentLimitGuard CreateGuard(LimitsOptions? options = null)
    {
        var opts = options ?? new LimitsOptions();
        return new ContentLimitGuard(TestHelpers.WrapOptions(opts));
    }

    [Fact]
    public void GetSizeBytes_EmptyString_ReturnsZero()
    {
        var sut = CreateGuard();
        Assert.Equal(0, sut.GetSizeBytes(""));
    }

    [Fact]
    public void GetSizeBytes_Whitespace_ReturnsZero()
    {
        var sut = CreateGuard();
        Assert.Equal(0, sut.GetSizeBytes("   "));
    }

    [Fact]
    public void GetSizeBytes_NoPadding_CalculatesCorrectly()
    {
        // "AAAA" = 4 chars, no padding → 4 * 3/4 = 3 bytes
        var sut = CreateGuard();
        Assert.Equal(3, sut.GetSizeBytes("AAAA"));
    }

    [Fact]
    public void GetSizeBytes_SinglePadding_CalculatesCorrectly()
    {
        // "AAA=" = 4 chars, 1 padding → 4*3/4 - 1 = 2 bytes
        var sut = CreateGuard();
        Assert.Equal(2, sut.GetSizeBytes("AAA="));
    }

    [Fact]
    public void GetSizeBytes_DoublePadding_CalculatesCorrectly()
    {
        // "AA==" = 4 chars, 2 padding → 4*3/4 - 2 = 1 byte
        var sut = CreateGuard();
        Assert.Equal(1, sut.GetSizeBytes("AA=="));
    }

    [Fact]
    public void GetSizeBytes_RealBase64_MatchesActualSize()
    {
        var original = new byte[1234];
        Random.Shared.NextBytes(original);
        var base64 = Convert.ToBase64String(original);
        var sut = CreateGuard();
        Assert.Equal(original.Length, sut.GetSizeBytes(base64));
    }

    [Fact]
    public void EnsurePdfWithinLimit_WithinLimit_DoesNotThrow()
    {
        var sut = CreateGuard(new LimitsOptions { PdfMaxBytes = 1024 });
        var base64 = TestHelpers.CreateBase64OfSize(512);
        sut.EnsurePdfWithinLimit(base64, "test");
    }

    [Fact]
    public void EnsurePdfWithinLimit_OverLimit_Throws()
    {
        var sut = CreateGuard(new LimitsOptions { PdfMaxBytes = 100 });
        var base64 = TestHelpers.CreateBase64OfSize(200);
        var ex = Assert.Throws<ApiValidationException>(() => sut.EnsurePdfWithinLimit(base64, "test"));
        Assert.Equal("PDF_SIZE_EXCEEDED", ex.Code);
    }

    [Fact]
    public void EnsurePdfWithinLimit_ExactlyAtLimit_DoesNotThrow()
    {
        long limit = 1024;
        var sut = CreateGuard(new LimitsOptions { PdfMaxBytes = limit });
        var base64 = TestHelpers.CreateBase64OfSize(limit);
        sut.EnsurePdfWithinLimit(base64, "test");
    }

    [Fact]
    public void EnsureImageWithinLimit_NullImage_DoesNotThrow()
    {
        var sut = CreateGuard(new LimitsOptions { ImageMaxBytes = 1 });
        sut.EnsureImageWithinLimit(null, "test");
    }

    [Fact]
    public void EnsureImageWithinLimit_EmptyImage_DoesNotThrow()
    {
        var sut = CreateGuard(new LimitsOptions { ImageMaxBytes = 1 });
        sut.EnsureImageWithinLimit("", "test");
    }

    [Fact]
    public void EnsureImageWithinLimit_WhitespaceImage_DoesNotThrow()
    {
        var sut = CreateGuard(new LimitsOptions { ImageMaxBytes = 1 });
        sut.EnsureImageWithinLimit("   ", "test");
    }

    [Fact]
    public void EnsureImageWithinLimit_OverLimit_Throws()
    {
        var sut = CreateGuard(new LimitsOptions { ImageMaxBytes = 100 });
        var base64 = TestHelpers.CreateBase64OfSize(200);
        var ex = Assert.Throws<ApiValidationException>(() => sut.EnsureImageWithinLimit(base64, "test"));
        Assert.Equal("IMAGE_SIZE_EXCEEDED", ex.Code);
    }

    [Fact]
    public void EnsureAttachmentWithinLimit_WithinLimit_DoesNotThrow()
    {
        var sut = CreateGuard(new LimitsOptions { AttachmentMaxBytes = 1024 });
        var base64 = TestHelpers.CreateBase64OfSize(512);
        sut.EnsureAttachmentWithinLimit(base64, "test");
    }

    [Fact]
    public void EnsureAttachmentWithinLimit_OverLimit_Throws()
    {
        var sut = CreateGuard(new LimitsOptions { AttachmentMaxBytes = 100 });
        var base64 = TestHelpers.CreateBase64OfSize(200);
        var ex = Assert.Throws<ApiValidationException>(() => sut.EnsureAttachmentWithinLimit(base64, "test"));
        Assert.Equal("ATTACHMENT_SIZE_EXCEEDED", ex.Code);
    }
}
