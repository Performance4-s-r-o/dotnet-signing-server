using System.Text.Json;
using DotNetSigningServer.Models;
using Xunit;

namespace DotNetSigningServer.Tests.Services;

/// <summary>
/// Locks the v0/SPFx → .NET wire contract for custom fillable fields. The TS side
/// (lib/sharepoint/signing/types.ts PresignFieldPayload + toDotnetFieldProps, and
/// the SPFx mirror) serialises camelCase JSON; ASP.NET Core controllers bind with
/// System.Text.Json defaults (PropertyNameCaseInsensitive = true) and the enum
/// converters declared on each enum. This test feeds the EXACT JSON the server
/// receives and asserts every formatting prop reaches PdfFieldDefinition, so a
/// rename on either side fails loudly here instead of silently dropping styling.
/// </summary>
public class PreSignFieldWireContractTests
{
    // Mirror ASP.NET Core's controller defaults.
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Deserialize_FullFormattingPayload_BindsToPdfFieldDefinition()
    {
        // Exactly what v0 sends inside presign body `fields: [...]` for a fully
        // formatted field (toDotnetFieldProps output spread into definition).
        const string json = """
        {
          "fields": [
            {
              "definition": {
                "fieldName": "job_title",
                "rect": { "X": 120, "Y": 600, "Width": 250, "Height": 40 },
                "page": 2,
                "fontSize": 18,
                "fontName": "Times-Roman",
                "fontWeight": "bold",
                "italic": true,
                "underline": true,
                "horizontalAlign": "center",
                "verticalAlign": "bottom",
                "rotation": 90,
                "textColor": "#FF0000",
                "backgroundColor": "#FFFFE0",
                "padding": 4,
                "multiline": true,
                "lineHeight": 1.5,
                "borderStyle": "dashed",
                "borderWidth": 2,
                "borderColor": "#0000FF"
              },
              "value": "Senior Engineer"
            }
          ]
        }
        """;

        var input = JsonSerializer.Deserialize<PreSignInput>(json, Opts);

        Assert.NotNull(input);
        Assert.NotNull(input!.Fields);
        var f = Assert.Single(input.Fields!);
        Assert.Equal("Senior Engineer", f.Value);

        var d = f.Definition;
        Assert.Equal("job_title", d.FieldName);
        Assert.Equal(120, d.Rect.X);
        Assert.Equal(600, d.Rect.Y);
        Assert.Equal(250, d.Rect.Width);
        Assert.Equal(40, d.Rect.Height);
        Assert.Equal(2, d.Page);
        Assert.Equal(18, d.FontSize);
        Assert.Equal(PdfFontName.TimesRoman, d.FontName);
        Assert.Equal(PdfFontWeight.Bold, d.FontWeight);
        Assert.True(d.Italic);
        Assert.True(d.Underline);
        Assert.Equal(PdfHorizontalAlign.Center, d.HorizontalAlign);
        Assert.Equal(PdfVerticalAlign.Bottom, d.VerticalAlign);
        Assert.Equal(90, d.Rotation);
        Assert.Equal("#FF0000", d.TextColor);
        Assert.Equal("#FFFFE0", d.BackgroundColor);
        Assert.Equal(4, d.Padding);
        Assert.True(d.Multiline);
        Assert.Equal(1.5f, d.LineHeight);
        Assert.Equal("dashed", d.BorderStyle);
        Assert.Equal(2, d.BorderWidth);
        Assert.Equal("#0000FF", d.BorderColor);
    }

    [Theory]
    [InlineData("top", PdfVerticalAlign.Top)]
    [InlineData("center", PdfVerticalAlign.Center)]
    [InlineData("bottom", PdfVerticalAlign.Bottom)]
    public void Deserialize_VerticalAlign_BindsEachValue(string wire, PdfVerticalAlign expected)
    {
        var json = $$"""
        { "fields": [ { "definition": { "fieldName": "v", "rect": { "X":1,"Y":2,"Width":3,"Height":4 }, "page": 1, "verticalAlign": "{{wire}}" }, "value": "x" } ] }
        """;
        var input = JsonSerializer.Deserialize<PreSignInput>(json, Opts);
        Assert.Equal(expected, input!.Fields![0].Definition.VerticalAlign);
    }

    [Theory]
    [InlineData("left", PdfHorizontalAlign.Left)]
    [InlineData("center", PdfHorizontalAlign.Center)]
    [InlineData("right", PdfHorizontalAlign.Right)]
    public void Deserialize_HorizontalAlign_BindsEachValue(string wire, PdfHorizontalAlign expected)
    {
        var json = $$"""
        { "fields": [ { "definition": { "fieldName": "h", "rect": { "X":1,"Y":2,"Width":3,"Height":4 }, "page": 1, "horizontalAlign": "{{wire}}" }, "value": "x" } ] }
        """;
        var input = JsonSerializer.Deserialize<PreSignInput>(json, Opts);
        Assert.Equal(expected, input!.Fields![0].Definition.HorizontalAlign);
    }

    [Fact]
    public void Deserialize_MinimalPayload_UsesEngineDefaults()
    {
        // A bare field (no formatting) — what signing sends when the creator
        // didn't open the formatting panel. Engine defaults must apply.
        const string json = """
        {
          "fields": [
            {
              "definition": {
                "fieldName": "note",
                "rect": { "X": 10, "Y": 20, "Width": 100, "Height": 30 },
                "page": 1
              },
              "value": "hi"
            }
          ]
        }
        """;

        var input = JsonSerializer.Deserialize<PreSignInput>(json, Opts);
        var f = Assert.Single(input!.Fields!);
        Assert.Equal("note", f.Definition.FieldName);
        Assert.Equal(12, f.Definition.FontSize);            // default
        Assert.Equal(PdfFontName.Helvetica, f.Definition.FontName); // default
        Assert.Equal("hi", f.Value);
    }
}
