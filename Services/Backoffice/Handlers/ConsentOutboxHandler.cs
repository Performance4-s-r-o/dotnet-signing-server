using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Consents;

namespace DotNetSigningServer.Services.Backoffice.Handlers;

/// <summary>
/// Sends outbox items <c>consent</c> to <c>POST /v1/consents</c>: the stored
/// <see cref="ConsentBatchPayload"/> (<c>{ "events": [ConsentInput, …] }</c>, at most 100)
/// as is, with the item id as <c>Idempotency-Key</c>, so a retry never records a batch twice.
///
/// Own HTTP call following the service's OpenAPI document — no SDK dependency, so every build
/// (forks included) can use it.
/// </summary>
public sealed class ConsentOutboxHandler : JsonPostOutboxHandler
{
    public override string Kind => ConsentRequirements.OutboxKind;

    protected override string Path => "v1/consents";
}
