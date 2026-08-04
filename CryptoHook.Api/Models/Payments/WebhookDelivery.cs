using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CryptoHook.Api.Models.Enums;

namespace CryptoHook.Api.Models.Payments;

/// <summary>
/// One notification owed to one subscriber, persisted before it is first attempted so that a
/// crash, a restart or an unreachable subscriber cannot lose it.
/// </summary>
public class WebhookDelivery
{
    [Key]
    public Guid Id { get; set; }

    public Guid PaymentRequestId { get; set; }

    /// <summary>
    /// Gets or sets the subscriber this record is owed to. Stored rather than resolved from
    /// configuration at send time, so that a queued delivery keeps going to the subscriber it
    /// was created for even if the configuration changes underneath it.
    /// </summary>
    [Required]
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
        Justification = "Persisted verbatim so that a queued delivery keeps targeting the exact URL it was created for, without a round trip through Uri normalisation.")]
    public required string Url { get; set; }

    [Required]
    public required string EventType { get; set; }

    /// <summary>
    /// Gets or sets the exact JSON body to POST. Frozen at enqueue time so a retry describes the
    /// state that triggered it rather than whatever the payment looks like hours later.
    /// </summary>
    /// <remarks>
    /// Kept out of API responses: a listing is for seeing what was and was not delivered, and
    /// repeating every body would dominate the response. Deliberately not <c>required</c>,
    /// because System.Text.Json refuses to build a contract for a required property it is also
    /// told to ignore.
    /// </remarks>
    [Required]
    [JsonIgnore]
    public string Payload { get; set; } = string.Empty;

    public WebhookDeliveryStatus Status { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Null once the record is finished.</summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public string? LastError { get; set; }
}
