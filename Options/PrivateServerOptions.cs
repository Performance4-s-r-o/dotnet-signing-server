namespace DotNetSigningServer.Options;

/// <summary>
/// Runs this server as an internal API rather than as a service anyone signs up
/// to.
///
/// On a self-hosted installation the signing engine sits behind the portal and
/// has no public face: nobody buys credits from it, reads its marketing pages or
/// opens a support ticket on it. Everything that exists for the hosted service is
/// then surface with no purpose, and surface with no purpose is the kind that
/// nobody notices is exposed.
///
/// What this does not do is relax authentication. All the authorisation of who
/// may sign what lives in the portal; this server only ever knew that a caller
/// was allowed to use it. Reaching it directly still skips every one of those
/// checks — and <c>/api/seal</c> is the endpoint that would then seal any
/// document with the customer's own certificate.
/// </summary>
public class PrivateServerOptions
{
    public bool Enabled { get; set; } = false;
}
