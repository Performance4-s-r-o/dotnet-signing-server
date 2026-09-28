using System.Text.Json;
using DotNetSigningServer.Options;
using DotNetSigningServer.Services;
using DotNetSigningServer.Services.Backoffice;
using DotNetSigningServer.Services.Backoffice.Handlers;
using DotNetSigningServer.Services.Backoffice.Outbox;
using DotNetSigningServer.Services.Email;
using DotNetSigningServer.Tests.Services.Backoffice.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DotNetSigningServer.Tests.Services.Backoffice.Email;

/// <summary>Options the test changes at run time, like a reloaded configuration.</summary>
internal sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Records what would have been sent directly (Resend with Email Off).</summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    public List<(string To, string Subject, string Html, EmailSendOptions? Options)> Sent { get; } = new();

    public Task SendAsync(string toEmail, string subject, string htmlBody) => SendAsync(toEmail, subject, htmlBody, null);

    public Task SendAsync(string toEmail, string subject, string htmlBody, EmailSendOptions? options)
    {
        Sent.Add((toEmail, subject, htmlBody, options));
        return Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="EmailTestHost"/> plus <see cref="TemplatedEmailSender"/> with the real local
/// templates, the <c>email.template</c> handler and options the test can change.
/// </summary>
internal sealed class TemplatedEmailTestHost : IDisposable
{
    public EmailTestHost Email { get; }

    public MutableOptionsMonitor<P4BackofficeProductOptions> Options { get; }

    /// <summary>The sender used while the Email module is not On.</summary>
    public RecordingEmailSender Direct { get; } = new();

    public TemplatedEmailTestHost(string emailMode = "On", params string[] templateKeys)
    {
        Options = new MutableOptionsMonitor<P4BackofficeProductOptions>(OptionsFor(emailMode, templateKeys));
        Email = new EmailTestHost(configure: services =>
        {
            services.AddSingleton<IOptionsMonitor<P4BackofficeProductOptions>>(Options);
            services.AddSingleton<IEmailTemplateRenderer>(CreateRenderer());
            services.AddSingleton<IOutboxHandler, EmailTemplateOutboxHandler>();
            services.AddScoped<IEmailSender>(sp => emailMode == "On"
                ? sp.GetRequiredService<BackofficeOutboxEmailSender>()
                : Direct);
            services.AddScoped<ITemplatedEmailSender, TemplatedEmailSender>();
        });
    }

    public OutboxTestHost Host => Email.Host;

    public static P4BackofficeProductOptions OptionsFor(string emailMode, params string[] templateKeys) => new()
    {
        DisabledReason = BackofficeDisabledReason.None,
        Modules = { Email = emailMode },
        Email = { TemplateKeys = templateKeys.ToList() },
    };

    public static EmailTemplateRenderer CreateRenderer()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(AppContext.BaseDirectory);
        return new EmailTemplateRenderer(NullLogger<EmailTemplateRenderer>.Instance, env.Object);
    }

    /// <summary>Sends like a caller that saves its own changes (TryEnqueue, save, SendAsync if not queued).</summary>
    public async Task<bool> SendAsync(
        string templateKey, IReadOnlyDictionary<string, string?> variables, string locale = "cs",
        string to = "jan@example.com", Guid? userId = null)
    {
        using var scope = Host.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ITemplatedEmailSender>();
        var options = new EmailSendOptions(templateKey, locale, userId);
        var queued = sender.TryEnqueue(templateKey, to, locale, variables, options);
        await scope.ServiceProvider.GetRequiredService<DotNetSigningServer.Data.ApplicationDbContext>().SaveChangesAsync();
        if (!queued) await sender.SendAsync(templateKey, to, locale, variables, options);
        return queued;
    }

    public Task<List<DotNetSigningServer.Models.BackofficeOutboxItem>> ItemsAsync() =>
        Host.WithDbAsync(db => Task.FromResult(db.BackofficeOutboxItems.OrderBy(i => i.CreatedAt).ToList()));

    /// <summary>Decrypted stored payload of an item.</summary>
    public JsonDocument Payload(DotNetSigningServer.Models.BackofficeOutboxItem item) =>
        JsonDocument.Parse(Host.Services.GetRequiredService<OutboxPayloadProtector>().Unprotect(item.PayloadProtected!));

    public void Dispose() => Email.Dispose();
}
