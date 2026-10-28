using System.Net.Http.Headers;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Inbox;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNetSigningServer.Services.Billing;

/// <summary>
/// Payments by <c>P4Backoffice:Modules:Billing</c> (Off by default, never inherited from the
/// global mode, forced Off on a PrivateServer):
/// <list type="bullet">
/// <item>Off — Stripe directly with the product's key (<see cref="StripePaymentGateway"/>), the
/// product's Stripe webhook grants credits; nothing here talks to the service.</item>
/// <item>On — <see cref="BackofficePaymentGateway"/> and <see cref="BackofficeAutoRecharge"/>
/// call <c>/v1/billing/*</c>; <c>billing.*</c> events grant credits
/// (<see cref="BillingEventsHandler"/>). The product's Stripe webhook endpoint answers 404
/// (#79): the product's Stripe account no longer takes the payments.</item>
/// </list>
/// </summary>
public static class BillingRegistration
{
    public static void AddBilling(IServiceCollection services, P4BackofficeProductOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<StripePaymentGateway>();

        var mode = options.ModeFor(BackofficeModule.Billing);
        if (mode == BackofficeMode.Off)
        {
            services.TryAddScoped<IPaymentGateway>(sp => sp.GetRequiredService<StripePaymentGateway>());
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
