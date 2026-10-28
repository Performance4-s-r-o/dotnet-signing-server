using System.Net.Http.Headers;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Payments by <c>P4Backoffice:Modules:Billing</c> (never inherited from the global mode, forced
/// Off on a PrivateServer). Production requires On (checked at startup,
/// <see cref="BackofficeOptionsValidator"/>):
/// <list type="bullet">
/// <item>On — <see cref="BackofficePaymentGateway"/> and <see cref="BackofficeAutoRecharge"/>
/// call <c>/v1/billing/*</c>; <c>billing.*</c> events grant credits
/// (<see cref="BillingEventsHandler"/>). The product holds no Stripe key.</item>
/// <item>Anything else (a private server, development) — <see cref="DisabledPaymentGateway"/>:
/// no payment can be started.</item>
/// </list>
/// </summary>
public static class BillingRegistration
{
    public static void AddBilling(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);

        var mode = options.ModeFor(BackofficeModule.Billing);
        if (mode == BackofficeMode.Off)
        {
            services.TryAddScoped<IPaymentGateway, DisabledPaymentGateway>();
            return;
        }

        services.AddScoped<IBackofficeEventHandler, BillingEventsHandler>();

        var baseUrl = options.BaseUrl?.Trim() ?? "";
        var secretKey = options.SecretKey?.Trim() ?? "";
        services.AddHttpClient(BackofficeBillingClient.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            client.Timeout = BackofficeBillingClient.RequestTimeout;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("dotnet-signing-server/backoffice-billing");
        })
            // The factory's request log prints the full URI, which names the customer
            // (customer_ref=user:<id>); BackofficeBillingClient logs the path without it.
            .RemoveAllLoggers();
        services.TryAddSingleton<BackofficeBillingClient>();
        services.TryAddScoped<BackofficePaymentGateway>();
        services.TryAddScoped<IPaymentGateway>(sp => sp.GetRequiredService<BackofficePaymentGateway>());
        services.TryAddScoped<BackofficeAutoRecharge>();
    }
}
