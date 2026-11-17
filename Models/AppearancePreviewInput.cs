namespace DotNetSigningServer.Models
{
    /// <summary>
    /// Preview of a signature appearance on its page (#117): the same drawing as visual-sign, nothing signed
    /// or charged. <see cref="VisualSignInput.FlattenForm"/>, <see cref="VisualSignInput.ConvertToPdfa"/> and
    /// <see cref="VisualSignInput.TemplateId"/> are not read.
    /// </summary>
    public class AppearancePreviewInput : VisualSignInput
    {
        /// <summary>Language of the watermark ("en", "cs", "de", "es"); the text itself is the server's, anything else is English.</summary>
        public string? Locale { get; set; }
    }
}
