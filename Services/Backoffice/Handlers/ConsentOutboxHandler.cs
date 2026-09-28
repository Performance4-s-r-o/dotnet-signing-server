using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// Sends outbox items <c>consent</c> to <c>POST /v1/consents</c>: the stored
/// <see cref="ConsentBatchPayload"/> (<c>{ "events": [ConsentInput, …] }</c>, at most 100)
/// as is, with the item id as <c>Idempotency-Key</c>, so a retry never records a batch twice.
///
/// TODO(P4.Backoffice.Sdk): once the package is restored in CI, send through
/// <c>BackofficeApiClient.V1.Consents.PostAsync</c> with the same header; the payload shape
/// stays the one of <c>openapi.json</c> (<c>ConsentBatch</c>).
/// </summary>
public sealed class ConsentOutboxHandler : JsonPostOutboxHandler
{
    public override string Kind => ConsentRequirements.OutboxKind;

    protected override string Path => "v1/consents";
}
