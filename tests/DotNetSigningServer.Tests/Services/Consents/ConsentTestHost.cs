using DotNetSigningServer.Data;
using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Consents;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetSigningServer.Tests.Services.Consents;

/// <summary>
/// <see cref="OutboxTestHost"/> (InMemory database, stubbed service) plus the consent services
/// in the given Consents mode and the real <see cref="ConsentOutboxHandler"/>.
/// </summary>
internal static class ConsentTestHost
{
    public static OutboxTestHost Create(string consentsMode = "On", Action<DbContextOptionsBuilder>? database = null) =>
        new(database, services =>
        {
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options(consentsMode)));
            services.AddSingleton<IOutboxHandler, ConsentOutboxHandler>();
            services.AddScoped<ConsentService>();
            services.AddScoped<ConsentBackfill>();
        });

    public static P4BackofficeProductOptions Options(string consentsMode) => new()
    {
        Modules = { Consents = consentsMode },
        BaseUrl = OutboxTestHost.BaseUrl,
        SecretKey = OutboxTestHost.SecretKey,
        DisabledReason = BackofficeDisabledReason.None,
    };

    public static readonly string TermsHash = new('a', 64);

    /// <summary>What the sign-up form shows by default: terms and DPA granted, privacy acknowledged, all v1.</summary>
    public static IReadOnlyList<ConsentChoice> SignupChoices(string locale = "en") =>
    [
        new(new ConsentDocumentVersion("terms", ConsentActions.Granted, "terms-of-service", 1, locale, TermsHash, null, null, null), 1, TermsHash),
        new(new ConsentDocumentVersion("dpa", ConsentActions.Granted, "data-processing-agreement", 1, locale, null, null, null, null), 1, null),
        new(new ConsentDocumentVersion("privacy", ConsentActions.Acknowledged, "privacy-policy", 1, locale, null, null, null, null), 1, null),
    ];

    public static User NewUser(string email = "new@example.com") => new()
    {
        Email = email,
        PasswordHash = [1],
        PasswordSalt = [2],
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>A sign-up as the controller does it: user + consents, one SaveChanges.</summary>
    public static async Task<User> SignUpAsync(OutboxTestHost host, string email, string? ip = "203.0.113.7")
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = NewUser(email);
        db.Users.Add(user);
        scope.ServiceProvider.GetRequiredService<ConsentService>()
            .RecordSignupConsents(user, SignupChoices(), "Mozilla/5.0 (test)", ip);
        await db.SaveChangesAsync();
        return user;
    }
}
