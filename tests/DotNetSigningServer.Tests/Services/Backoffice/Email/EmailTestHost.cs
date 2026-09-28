using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

/// <summary>
/// Outbox host with the Email module's pieces (outbox sender, <c>email.raw</c> handler,
/// break-glass) and a mocked <see cref="ResendEmailSender"/> — nothing leaves the test.
/// </summary>
internal sealed class EmailTestHost : IDisposable
{
    public const string From = "Performance4PDF <noreply@send.example.com>";

    public OutboxTestHost Host { get; }

    public Mock<ResendEmailSender> Resend { get; }

    public List<(string To, string Subject, string Html)> DirectSends { get; } = new();

    public EmailTestHost(
        bool fallbackDirect = true,
        bool resendConfigured = true,
        Action<DbContextOptionsBuilder>? database = null)
    {
        Resend = new Mock<ResendEmailSender>(
            Microsoft.Extensions.Options.Options.Create(new ResendOptions()),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<ResendEmailSender>.Instance) { CallBase = false };
        Resend.SetupGet(r => r.IsConfigured).Returns(resendConfigured);
        Resend.Setup(r => r.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<string, string, string>((to, subject, html) => DirectSends.Add((to, subject, html)))
            .Returns(Task.CompletedTask);

        Host = new OutboxTestHost(database, services =>
        {
            services.Configure<ResendOptions>(o => o.From = From);
            services.Configure<P4BackofficeProductOptions>(o => o.Email.FallbackDirect = fallbackDirect);
            services.AddScoped(_ => Resend.Object);
            services.AddScoped<BackofficeOutboxEmailSender>();
            services.AddSingleton<IOutboxHandler, EmailRawOutboxHandler>();
            services.AddSingleton<BreakGlassEmailFallback>();
        });
    }

    public BreakGlassEmailFallback Fallback => Host.Services.GetRequiredService<BreakGlassEmailFallback>();

    public OutboxCircuitBreaker Breaker => Host.Services.GetRequiredService<OutboxCircuitBreaker>();

    /// <summary>Queues an e-mail through the outbox sender, as a request would.</summary>
    public async Task<Guid> SendAsync(string templateId, string to = "jan@example.com", Guid? userId = null)
    {
        var before = await Host.WithDbAsync(db => db.BackofficeOutboxItems.Select(i => i.Id).ToListAsync());
        using (var scope = Host.Services.CreateScope())
        {
            var sender = scope.ServiceProvider.GetRequiredService<BackofficeOutboxEmailSender>();
            await sender.SendAsync(to, "Subject " + templateId, "<p>code 123456</p>", new EmailSendOptions(templateId, "cs", userId));
        }
        var after = await Host.WithDbAsync(db => db.BackofficeOutboxItems.Select(i => i.Id).ToListAsync());
        return after.Except(before).Single();
    }

    public async Task UpdateAsync(Guid id, Action<DotNetSigningServer.Models.BackofficeOutboxItem> change)
    {
        await Host.WithDbAsync(async db =>
        {
            var item = await db.BackofficeOutboxItems.SingleAsync(i => i.Id == id);
            change(item);
            return await db.SaveChangesAsync();
        });
    }

    public void Dispose() => Host.Dispose();
}
