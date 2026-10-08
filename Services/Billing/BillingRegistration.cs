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
/// <item>Shadow — the same, and <c>billing.*</c> events are only logged and compared.</item>
/// <item>On — <see cref="BackofficePaymentGateway"/> and <see cref="BackofficeAutoRecharge"/>
/// call <c>/v1/billing/*</c>; <c>billing.*</c> events grant credits
/// (<see cref="BillingEventsHandler"/>). The Stripe webhook endpoint stays as a safety net:
/// both paths claim the same keys, so nothing is granted twice.</item>
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

        services.AddScoped<IBackofficeEventHandler>(sp => ActivatorUtilities.CreateInstance<BillingEventsHandler>(sp, mode));
        if (mode == BackofficeMode.Shadow)
        {
            services.TryAddScoped<IPaymentGateway>(sp => sp.GetRequiredService<StripePaymentGateway>());
            return;
        }

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
