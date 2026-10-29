using DotNetSigningServer.Middleware;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace DotNetSigningServer.TagHelpers;

/// <summary>Adds the request's CSP nonce to every &lt;script&gt; of a view.</summary>
[HtmlTargetElement("script")]
public class CspNonceTagHelper : TagHelper
{
    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (output.Attributes.ContainsName("nonce")) return;
        output.Attributes.SetAttribute("nonce", ContentSecurityPolicyMiddleware.GetNonce(ViewContext.HttpContext));
    }
}
