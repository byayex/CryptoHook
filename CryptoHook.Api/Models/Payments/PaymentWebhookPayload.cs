using System;
using System.Numerics;
using System.Text.Json.Serialization;
using CryptoHook.Api.Models.Converters;

namespace CryptoHook.Api.Models.Payments
{
    public class PaymentWebhookPayload
    {
        public required Guid PaymentId { get; set; }
        public required string Status { get; set; }
        [JsonConverter(typeof(BigIntegerStringConverter))]
        public required BigInteger AmountDetected { get; set; }
        [JsonConverter(typeof(BigIntegerStringConverter))]
        public required BigInteger AmountExpected { get; set; }
        public required uint Confirmations { get; set; }
        public string? TransactionId { get; set; }
        public required DateTime Timestamp { get; set; }
    }
}
