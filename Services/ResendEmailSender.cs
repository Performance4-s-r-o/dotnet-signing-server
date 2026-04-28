using System.Net.Http.Headers;
using System.Text.Json;
using DotNetSigningServer.Options;
using Microsoft.Extensions.Options;

namespace DotNetSigningServer.Services;

public class ResendEmailSender : IEmailSender
{
    private const string EndpointUrl = "https://api.resend.com/emails";

    private readonly ResendOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(
        IOptions<ResendOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<ResendEmailSender> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("Resend API key not configured. Skipping email send (subject: {Subject}).", subject);
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["from"] = _options.From,
            ["to"] = new[] { toEmail },
            ["subject"] = subject,
            ["html"] = htmlBody,
        };

        if (!string.IsNullOrWhiteSpace(_options.ReplyTo))
        {
            payload["reply_to"] = _options.ReplyTo;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var client = _httpClientFactory.CreateClient("resend");

        try
        {
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Resend returned {Status} for email (subject: {Subject}): {Body}",
                    (int)response.StatusCode,
                    subject,
                    errorBody);
                throw new InvalidOperationException($"Resend failed: {(int)response.StatusCode} {errorBody}");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex, "Failed to send email via Resend (subject: {Subject})", subject);
            throw;
        }
    }
}
