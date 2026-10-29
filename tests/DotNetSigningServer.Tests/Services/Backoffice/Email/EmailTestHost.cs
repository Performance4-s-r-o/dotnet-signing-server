using DotNetSigningServer.Options;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

/// <summary>
/// Outbox host with the e-mail pieces (outbox sender, <c>email.raw</c> handler) — nothing leaves the test.
/// </summary>
internal sealed class EmailTestHost : IDisposable
{
    public const string From = "Performance4PDF <noreply@send.example.com>";

    public OutboxTestHost Host { get; }

    public EmailTestHost(
        Action<DbContextOptionsBuilder>? database = null,
        Action<IServiceCollection>? configure = null)
    {
        Host = new OutboxTestHost(database, services =>
        {
            services.Configure<P4BackofficeProductOptions>(o => o.Email.From = From);
            services.AddScoped<BackofficeOutboxEmailSender>();
            services.AddSingleton<IOutboxHandler, EmailRawOutboxHandler>();
            configure?.Invoke(services);
        });
    }

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
