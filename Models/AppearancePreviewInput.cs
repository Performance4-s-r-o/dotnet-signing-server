namespace DotNetSigningServer.Models
{
    /// <summary>
    /// Preview of a signature appearance on its page (#117): the same drawing as visual-sign, nothing signed
    /// or charged. <see cref="VisualSignInput.FlattenForm"/>, <see cref="VisualSignInput.ConvertToPdfa"/> and
    /// <see cref="VisualSignInput.TemplateId"/> are not read.
    /// </summary>
    public class AppearancePreviewInput : VisualSignInput
    {
        /// <summary>Text drawn diagonally across the page (e.g. "PREVIEW" in the caller's language); required.</summary>
        public string WatermarkText { get; set; } = "";

        /// <summary>Upper limit of <see cref="WatermarkText"/>.</summary>
        public const int WatermarkMaxLength = 40;
    }
}
