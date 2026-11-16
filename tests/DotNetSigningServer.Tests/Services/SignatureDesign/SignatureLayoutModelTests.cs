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
