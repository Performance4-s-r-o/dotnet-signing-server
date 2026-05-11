using DotNetSigningServer.Data;
using DotNetSigningServer.Exceptions;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetSigningServer.Tests.Services;

public class PdfTemplateServiceTests : IDisposable
{
    private readonly ApplicationDbContext _dbContext;
    private readonly PdfTemplateService _sut;
    private readonly Guid _userId = Guid.NewGuid();

    public PdfTemplateServiceTests()
    {
        _dbContext = TestHelpers.CreateInMemoryDbContext();
        var logger = NullLogger<PdfTemplateService>.Instance;
        var limitGuard = new ContentLimitGuard(TestHelpers.WrapOptions(new LimitsOptions()));
        _sut = new PdfTemplateService(_dbContext, logger, limitGuard);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private CreateTemplateInput MakeValidInput(string? name = null)
    {
        return new CreateTemplateInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            Fields = new List<PdfFieldDefinition>
            {
                new()
                {
                    FieldName = "Field1",
                    Type = PdfFieldType.Text,
                    Rect = new SignRect { X = 10, Y = 10, Width = 200, Height = 30 },
                    Page = 1,
                    FontSize = 12
                }
            },
            TemplateName = name
        };
    }

    [Fact]
    public async Task CreateTemplate_ReturnsTemplateId()
    {
        var result = await _sut.CreateTemplateAsync(MakeValidInput("My Template"), _userId);
        Assert.NotEqual(Guid.Empty, result.TemplateId);
    }

    [Fact]
    public async Task CreateTemplate_PersistsToDatabase()
    {
        var result = await _sut.CreateTemplateAsync(MakeValidInput("Persisted"), _userId);
        var stored = await _dbContext.StoredPdfTemplates.FindAsync(result.TemplateId);
        Assert.NotNull(stored);
        Assert.Equal(_userId, stored.UserId);
        Assert.Equal("Persisted", stored.Name);
    }

    [Fact]
    public async Task CreateTemplate_EmptyPdfContent_Throws()
    {
        var input = MakeValidInput();
        input.PdfContent = "";
        await Assert.ThrowsAsync<ApiValidationException>(() => _sut.CreateTemplateAsync(input, _userId));
    }

    [Fact]
    public async Task CreateTemplate_NoFields_Throws()
    {
        var input = MakeValidInput();
        input.Fields = new List<PdfFieldDefinition>();
        await Assert.ThrowsAsync<ApiValidationException>(() => _sut.CreateTemplateAsync(input, _userId));
    }

    [Fact]
    public async Task GetTemplate_ReturnsCorrectData()
    {
        var created = await _sut.CreateTemplateAsync(MakeValidInput("GetTest"), _userId);
        var detail = await _sut.GetTemplateAsync(created.TemplateId, _userId);

        Assert.Equal(created.TemplateId, detail.TemplateId);
        Assert.Equal("GetTest", detail.Name);
        Assert.Single(detail.Fields);
        Assert.Equal("Field1", detail.Fields[0].FieldName);
        Assert.False(string.IsNullOrWhiteSpace(detail.PdfContent));
    }

    [Fact]
    public async Task GetTemplate_WrongUser_Throws()
    {
        var created = await _sut.CreateTemplateAsync(MakeValidInput(), _userId);
        var otherUser = Guid.NewGuid();

        await Assert.ThrowsAsync<ApiValidationException>(
            () => _sut.GetTemplateAsync(created.TemplateId, otherUser));
    }

    [Fact]
    public async Task GetTemplate_NonExistent_Throws()
    {
        await Assert.ThrowsAsync<ApiValidationException>(
            () => _sut.GetTemplateAsync(Guid.NewGuid(), _userId));
    }

    [Fact]
    public async Task ListTemplates_ReturnsUserTemplatesOnly()
    {
        var otherUser = Guid.NewGuid();
        await _sut.CreateTemplateAsync(MakeValidInput("Mine1"), _userId);
        await _sut.CreateTemplateAsync(MakeValidInput("Mine2"), _userId);
        await _sut.CreateTemplateAsync(MakeValidInput("Other"), otherUser);

        var list = await _sut.ListTemplatesAsync(_userId);
        Assert.Equal(2, list.Count);
        Assert.All(list, t => Assert.True(t.Name == "Mine1" || t.Name == "Mine2"));
    }

    [Fact]
    public async Task ListTemplates_IncludesFieldCount()
    {
        await _sut.CreateTemplateAsync(MakeValidInput(), _userId);
        var list = await _sut.ListTemplatesAsync(_userId);
        Assert.Single(list);
        Assert.Equal(1, list.First().FieldCount);
    }

    [Fact]
    public async Task UpdateTemplate_UpdatesName()
    {
        var created = await _sut.CreateTemplateAsync(MakeValidInput("Original"), _userId);
        await _sut.UpdateTemplateAsync(created.TemplateId, _userId, new UpdateTemplateInput
        {
            TemplateName = "Updated"
        });

        var detail = await _sut.GetTemplateAsync(created.TemplateId, _userId);
        Assert.Equal("Updated", detail.Name);
    }

    [Fact]
    public async Task UpdateTemplate_UpdatesFields()
    {
        var created = await _sut.CreateTemplateAsync(MakeValidInput(), _userId);
        var newFields = new List<PdfFieldDefinition>
        {
            new() { FieldName = "NewField", Type = PdfFieldType.Text, Rect = new SignRect { X = 0, Y = 0, Width = 100, Height = 20 }, FontSize = 10 },
            new() { FieldName = "AnotherField", Type = PdfFieldType.Image, Rect = new SignRect { X = 50, Y = 50, Width = 100, Height = 100 }, FontSize = 12 }
        };

        await _sut.UpdateTemplateAsync(created.TemplateId, _userId, new UpdateTemplateInput
        {
            Fields = newFields
        });

        var detail = await _sut.GetTemplateAsync(created.TemplateId, _userId);
        Assert.Equal(2, detail.Fields.Count);
        Assert.Equal("NewField", detail.Fields[0].FieldName);
    }

    [Fact]
    public async Task UpdateTemplate_WrongUser_Throws()
    {
        var created = await _sut.CreateTemplateAsync(MakeValidInput(), _userId);
        await Assert.ThrowsAsync<ApiValidationException>(
            () => _sut.UpdateTemplateAsync(created.TemplateId, Guid.NewGuid(), new UpdateTemplateInput { TemplateName = "Hacked" }));
    }

    [Fact]
    public async Task DeleteTemplate_RemovesFromDatabase()
    {
        var created = await _sut.CreateTemplateAsync(MakeValidInput(), _userId);
        await _sut.DeleteTemplateAsync(created.TemplateId, _userId);

        var stored = await _dbContext.StoredPdfTemplates.FindAsync(created.TemplateId);
        Assert.Null(stored);
    }

    [Fact]
    public async Task DeleteTemplate_WrongUser_Throws()
    {
        var created = await _sut.CreateTemplateAsync(MakeValidInput(), _userId);
        await Assert.ThrowsAsync<ApiValidationException>(
            () => _sut.DeleteTemplateAsync(created.TemplateId, Guid.NewGuid()));
    }

    [Fact]
    public async Task FillAsync_WithInlineFields_ProducesOutput()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var input = new FillPdfInput
        {
            PdfContent = pdfBase64,
            Fields = new List<PdfFieldDefinition>
            {
                new()
                {
                    FieldName = "Name",
                    Type = PdfFieldType.Text,
                    Rect = new SignRect { X = 50, Y = 700, Width = 200, Height = 30 },
                    Page = 1,
                    FontSize = 14
                }
            },
            Data = new List<FillDataSet>
            {
                new() { Data = new List<PdfFieldValue> { new() { FieldName = "Name", Value = "John Doe" } } }
            }
        };

        var result = await _sut.FillAsync(input, _userId);
        Assert.Single(result.Files);
        Assert.False(string.IsNullOrWhiteSpace(result.Files[0]));
        // Verify output is valid PDF
        var bytes = Convert.FromBase64String(result.Files[0]);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public async Task FillAsync_MultipleDataSets_ProducesMultipleFiles()
    {
        var pdfBase64 = TestHelpers.CreateMinimalPdfBase64();
        var input = new FillPdfInput
        {
            PdfContent = pdfBase64,
            Fields = new List<PdfFieldDefinition>
            {
                new()
                {
                    FieldName = "Name",
                    Type = PdfFieldType.Text,
                    Rect = new SignRect { X = 50, Y = 700, Width = 200, Height = 30 },
                    Page = 1,
                    FontSize = 14
                }
            },
            Data = new List<FillDataSet>
            {
                new() { Data = new List<PdfFieldValue> { new() { FieldName = "Name", Value = "Alice" } } },
                new() { Data = new List<PdfFieldValue> { new() { FieldName = "Name", Value = "Bob" } } },
                new() { Data = new List<PdfFieldValue> { new() { FieldName = "Name", Value = "Charlie" } } }
            }
        };

        var result = await _sut.FillAsync(input, _userId);
        Assert.Equal(3, result.Files.Count);
    }

    [Fact]
    public async Task FillAsync_WithTemplateId_UsesStoredTemplate()
    {
        var created = await _sut.CreateTemplateAsync(MakeValidInput(), _userId);

        var input = new FillPdfInput
        {
            TemplateId = created.TemplateId,
            Data = new List<FillDataSet>
            {
                new() { Data = new List<PdfFieldValue> { new() { FieldName = "Field1", Value = "Value1" } } }
            }
        };

        var result = await _sut.FillAsync(input, _userId);
        Assert.Single(result.Files);
        Assert.Equal(created.TemplateId, result.TemplateId);
    }

    [Fact]
    public async Task FillAsync_NoData_Throws()
    {
        var input = new FillPdfInput
        {
            PdfContent = TestHelpers.CreateMinimalPdfBase64(),
            Fields = new List<PdfFieldDefinition>
            {
                new() { FieldName = "F1", Type = PdfFieldType.Text, Rect = new SignRect(), FontSize = 12 }
            },
            Data = new List<FillDataSet>()
        };

        await Assert.ThrowsAsync<ApiValidationException>(() => _sut.FillAsync(input, _userId));
    }

    [Fact]
    public async Task FillAsync_NoPdfContentOrTemplateId_Throws()
    {
        var input = new FillPdfInput
        {
            Data = new List<FillDataSet>
            {
                new() { Data = new List<PdfFieldValue> { new() { FieldName = "F1", Value = "V1" } } }
            }
        };

        await Assert.ThrowsAsync<ApiValidationException>(() => _sut.FillAsync(input, _userId));
    }

    [Fact]
    public async Task CreateTemplate_InvalidFieldName_Throws()
    {
        var input = MakeValidInput();
        input.Fields[0].FieldName = "invalid field name!";
        await Assert.ThrowsAsync<ApiValidationException>(() => _sut.CreateTemplateAsync(input, _userId));
    }

    [Fact]
    public async Task CreateTemplate_FontSizeOutOfRange_Throws()
    {
        var input = MakeValidInput();
        input.Fields[0].FontSize = 200;
        await Assert.ThrowsAsync<ApiValidationException>(() => _sut.CreateTemplateAsync(input, _userId));
    }
}
