using System.ComponentModel.DataAnnotations;

namespace CryptoHook.Api.Models.Configs;

public class WebhookConfig
{
    [Required]
    public required Uri Url { get; set; }

    [Required]
    public required string Secret { get; set; }
}

public class WebhookConfigList : List<WebhookConfig>;