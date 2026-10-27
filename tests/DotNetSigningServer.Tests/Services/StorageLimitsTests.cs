using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services;

/// <summary>#74: files with no row are swept, and the per-account caps hold (when set).</summary>
public class StorageLimitsTests : IDisposable
{
    private readonly ApplicationDbContext _db = TestHelpers.CreateInMemoryDbContext();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"p4-sweep-{Guid.NewGuid():N}");

    public StorageLimitsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Old_presign_files_without_a_row_are_removed_and_fresh_ones_kept()
    {
        var old = Path.Combine(_dir, "presigned_old.pdf");
        var fresh = Path.Combine(_dir, "presigned_fresh.pdf");
        var other = Path.Combine(_dir, "something_else.pdf");
        foreach (var f in new[] { old, fresh, other }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddHours(-2));

        var service = new PresignCleanupService(null!, NullLogger<PresignCleanupService>.Instance);
        service.SweepOrphanFiles(DateTime.UtcNow.AddMinutes(-30), _dir);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(other)); // not ours: only presigned_*.pdf is touched
    }

    private PdfTemplateService Templates(int max)
    {
        var options = new LimitsOptions { MaxTemplatesPerUser = max };
        var guard = new ContentLimitGuard(TestHelpers.WrapOptions(options));
        return new PdfTemplateService(_db, NullLogger<PdfTemplateService>.Instance, guard, new KeyEchoLocalizerFactory());
    }

    private static CreateTemplateInput Input(string name) => new()
    {
        PdfContent = TestHelpers.CreateMinimalPdfBase64(),
        Fields = new List<PdfFieldDefinition>
        {
            new() { FieldName = "F", Type = PdfFieldType.Text, Rect = new SignRect { X = 10, Y = 10, Width = 100, Height = 20 }, Page = 1, FontSize = 12 },
        },
        TemplateName = name,
    };

    [Fact]
    public async Task The_template_cap_refuses_the_one_over_and_is_per_account()
    {
        var sut = Templates(max: 2);
        var user = Guid.NewGuid();
        await sut.CreateTemplateAsync(Input("a"), user);
        await sut.CreateTemplateAsync(Input("b"), user);

        var ex = await Assert.ThrowsAsync<ApiValidationException>(() => sut.CreateTemplateAsync(Input("c"), user));
        Assert.Equal("TOO_MANY_TEMPLATES", ex.Code);

        await sut.CreateTemplateAsync(Input("other-account"), Guid.NewGuid());
    }

    [Fact]
    public async Task A_trusted_caller_is_not_held_to_the_template_cap()
    {
        var sut = Templates(max: 1);
        var user = Guid.NewGuid();
        await sut.CreateTemplateAsync(Input("a"), user);
        await sut.CreateTemplateAsync(Input("b"), user, default, trustedCaller: true);
    }

    [Fact]
    public void The_caps_have_defaults()
    {
        var o = new LimitsOptions();
        Assert.Equal(20, o.MaxPendingPresignsPerUser);
        Assert.Equal(100, o.MaxTemplatesPerUser);
    }

    [Fact]
    public async Task Without_a_cap_templates_are_not_counted()
    {
        var sut = Templates(max: 0);
        var user = Guid.NewGuid();
        for (var i = 0; i < 5; i++) await sut.CreateTemplateAsync(Input($"t{i}"), user);
    }
}
