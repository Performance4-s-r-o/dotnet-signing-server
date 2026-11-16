using System.Text.Json.Serialization;
using DotNetSigningServer.Services.SignatureDesign;

namespace DotNetSigningServer.Models
{
    /// <summary>
    /// How a visible signature looks (#116): the layout model of the design and the labels of the document's
    /// language. The size is the <c>SignRect</c> of the request; there is no design size any more.
    /// </summary>
    // Unknown members are refused: a misspelt `seal` would silently switch the seal check off.
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public class SignatureAppearanceOptions
    {
        /// <summary>Layout model (portal <c>lib/signature-layout/model.ts</c>); <c>null</c> = the default for <see cref="Seal"/>.</summary>
        public SignatureLayoutModel? Layout { get; set; }
        /// <summary>Default labels in front of reason and location; <c>null</c> = English.</summary>
        public SignatureLabels? Labels { get; set; }
        /// <summary>The appearance is an organisation seal: no handwritten signature and no signer name, else refused.</summary>
        public bool Seal { get; set; }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public class SignatureLabels
    {
        /// <summary>E.g. "Reason:".</summary>
        public string? Reason { get; set; }
        /// <summary>E.g. "Location:".</summary>
        public string? Location { get; set; }
    }
}
