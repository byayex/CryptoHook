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
        if (_webhookConfigs is null || _webhookConfigs.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Notifying webhooks for payment {PaymentId} with status {Status}", payload.Id, payload.Status);

        var jsonPayload = JsonSerializer.Serialize(payload);

        _logger.LogDebug("Webhook payload: {Payload}", jsonPayload);

        foreach (var webhook in _webhookConfigs)
        {
            _logger.LogInformation("Sending webhook notification to {Url}", webhook.Url);
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-Signature", webhook.Secret);
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
}
