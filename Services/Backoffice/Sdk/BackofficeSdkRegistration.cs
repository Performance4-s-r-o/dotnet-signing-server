using P4.Backoffice.Sdk;
using P4.Backoffice.Sdk.DependencyInjection;

namespace DotNetSigningServer.Services.Backoffice.Sdk;

/// <summary>
/// The only place that registers SDK types. Compiled only with <c>UseP4BackofficeSdk=true</c>
/// (see <c>Directory.Build.props</c>); everything in this folder may reference the SDK.
/// </summary>
internal static class BackofficeSdkRegistration
{
    public static IServiceCollection AddP4BackofficeSdk(this IServiceCollection services, IConfiguration section)
    {
        // Same section as P4BackofficeProductOptions: BaseUrl, SecretKey, Timeout, DocumentsTtl, BundlePath.
        services.AddP4Backoffice(options => section.Bind(options));
        return services;
    }
}
