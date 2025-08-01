using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Services;

public class WebhookService(IHttpClientFactory httpClientFactory, IOptions<WebhookConfigList> webhookConfigs, ILogger<WebhookService> logger) : IWebhookService
{
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly WebhookConfigList _webhookConfigs = webhookConfigs.Value;
    private readonly ILogger<WebhookService> _logger = logger;

    public async Task NotifyPaymentChange(PaymentRequest payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_webhookConfigs is null || _webhookConfigs.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Notifying webhooks for payment {PaymentId} with status {Status}", payload.Id, payload.Status);

        var jsonPayload = JsonSerializer.Serialize(payload);

        _logger.LogDebug("Webhook payload: {Payload}", jsonPayload);

        using var httpClient = _httpClientFactory.CreateClient();

        // Disableing CA2000 warning for this example, as the content is disposed by the HttpClient
#pragma warning disable CA2000
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
#pragma warning restore CA2000

        foreach (var webhook in _webhookConfigs)
        {
            _logger.LogInformation("Sending webhook notification to {Url}", webhook.Url);

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var requestId = Guid.NewGuid().ToString();

            var signature = GenerateHmacSignature(jsonPayload, timestamp, requestId, webhook.Secret);

            httpClient.DefaultRequestHeaders.Add("X-Signature", $"sha256={signature}");
            httpClient.DefaultRequestHeaders.Add("X-Timestamp", timestamp);
            httpClient.DefaultRequestHeaders.Add("X-Request-ID", requestId);

            try
            {
                var response = await httpClient.PostAsync(webhook.Url, content);
                response.EnsureSuccessStatusCode();
                _logger.LogInformation("Successfully sent webhook notification to {Url}", webhook.Url);
            }
            catch (HttpRequestException e)
            {
                _logger.LogError(e, "Error sending webhook notification to {Url}", webhook.Url);
            }
        }
    }

    /// <summary>
    /// Generates HMAC-SHA256 signature for webhook payload verification with replay attack protection
    /// </summary>
    /// <param name="payload">The JSON payload to sign</param>
    /// <param name="timestamp">Unix timestamp for replay protection</param>
    /// <param name="requestId">Unique request identifier</param>
    /// <param name="secret">The webhook secret key</param>
    /// <returns>Hex-encoded HMAC signature</returns>
    private static string GenerateHmacSignature(string payload, string timestamp, string requestId, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);

        var signaturePayload = $"{timestamp}.{requestId}.{payload}";
        var payloadBytes = Encoding.UTF8.GetBytes(signaturePayload);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hashBytes).ToUpperInvariant();
    }
}
