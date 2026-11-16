using DotNetSigningServer.Services.SignatureDesign;

namespace DotNetSigningServer.Tests.Services.SignatureDesign;

public class SignatureLayoutModelTests
{
    [Fact]
    public void Default_model_is_valid() => Assert.Null(new SignatureLayoutModel().Validate());

    [Theory]
    [InlineData("same-kind")]
    [InlineData("order")]
    [InlineData("split")]
    [InlineData("font-size")]
    [InlineData("colour")]
    [InlineData("pattern")]
    [InlineData("opacity")]
    public void Refuses_invalid_models(string what)
    {
        var m = new SignatureLayoutModel();
        switch (what)
        {
            case "same-kind": m.Right = "sig"; break;
            case "order": m.Order = new() { "left", "left", "text" }; break;
            case "split": m.SplitLeft = 70; break;
            case "font-size": m.FontSize = 11; break;
            case "colour": m.TextColor = "red"; break;
            case "pattern": m.Background.Pattern = "waves"; break;
            case "opacity": m.Background.Opacity = 5; break;
        }
        Assert.NotNull(m.Validate());
    }

    [Fact]
    public void Measures_czech_and_falls_back_to_n()
    {
        Assert.True(FontMetrics.TextWidth("ů", "Helvetica", false, 1000) > 0);
        Assert.Equal(FontMetrics.TextWidth("n", "Helvetica", false, 1000), FontMetrics.TextWidth("中", "Helvetica", false, 1000));
    }

    [Fact]
    public void Js_rounding()
    {
        // Half up like Math.round, not banker's rounding (0.125 * 100 = 12.5 exactly).
        Assert.Equal(0.13, SignatureLayoutEngine.R2(0.125), 10);
        Assert.Equal(0.38, SignatureLayoutEngine.R2(0.375), 10);
    }
}

public class SignatureLayoutHygieneTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void Normalizes_like_the_portal()
    {
        Assert.Equal("Jana Nováková", SignatureLayoutEngine.NormalizeText("﻿ Jana\t\nNováková\u0085 "));
        Assert.Equal("a�b", SignatureLayoutEngine.NormalizeText("a\uD800b"));
        Assert.Equal(200, SignatureLayoutEngine.NormalizeText(new string('x', 500)).Length);
    }

    [Theory]
    [InlineData(10, 70)]
    [InlineData(200, double.NaN)]
    [InlineData(5000, 70)]
    public void Refuses_frames_outside_the_range(double w, double h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SignatureLayoutEngine.Compute(new SignatureLayoutInput { Width = w, Height = h }));

    [Fact]
    public void Seal_shows_no_person()
    {
        var seal = new SignatureLayoutModel { Left = "logo", Right = "image" };
        seal.Elements.Name.Enabled = false;
        Assert.Null(seal.SealViolation());
        Assert.NotNull(new SignatureLayoutModel().SealViolation());
    }

    [Fact]
    public void Json_model_must_be_complete_and_known()
    {
        var full = System.Text.Json.JsonSerializer.Serialize(new SignatureLayoutModel(), Json);
        Assert.NotNull(System.Text.Json.JsonSerializer.Deserialize<SignatureLayoutModel>(full, Json));
        var missing = full.Replace("\"version\":1,", "");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<SignatureLayoutModel>(missing, Json));
        var extra = full.Replace("\"version\":1,", "\"version\":1,\"extra\":true,");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<SignatureLayoutModel>(extra, Json));
    }

    [Fact]
    public void One_line_text_only()
    {
        var m = new SignatureLayoutModel { CustomText = "a\nb" };
        Assert.NotNull(m.Validate());
    }
}
