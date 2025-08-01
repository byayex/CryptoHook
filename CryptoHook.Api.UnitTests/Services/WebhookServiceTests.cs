using System.Linq.Expressions;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;

namespace CryptoHook.Api.UnitTests.Services;

public class WebhookServiceTests
{
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly Mock<IOptions<WebhookConfigList>> _mockWebhookOptions;
    private readonly Mock<ILogger<WebhookService>> _mockLogger;
    private readonly Mock<HttpMessageHandler> _mockHttpMessageHandler;

    public WebhookServiceTests()
    {
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockWebhookOptions = new Mock<IOptions<WebhookConfigList>>();
        _mockLogger = new Mock<ILogger<WebhookService>>();
        _mockHttpMessageHandler = new Mock<HttpMessageHandler>();

        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
                     .Returns(() =>
                     {
                         return new HttpClient(_mockHttpMessageHandler.Object);
                     });
    }

    private static PaymentRequest CreateTestPaymentRequest()
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            Status = PaymentStatus.Paid,
            AmountExpected = 100000,
            AmountPaid = 100000,
            ConfirmationCount = 1,
            ConfirmationNeeded = 1,
            ReceivingAddress = "bc1qtest123",
            CurrencySymbol = "BTC",
            Network = "Main",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };
    }

    private static WebhookConfigList CreateTestWebhookConfigs()
    {
        return
        [
            new WebhookConfig { Url = new Uri("https://example.com/webhook1"), Secret = "secret1" },
            new WebhookConfig { Url = new Uri("https://example.com/webhook2"), Secret = "secret2" }
        ];
    }

    [Fact]
    public async Task NotifyPaymentChange_WithNoWebhooks_DoesNothing()
    {
        // Arrange
        var emptyConfigs = new WebhookConfigList();
        _mockWebhookOptions.Setup(o => o.Value).Returns(emptyConfigs);

        var service = new WebhookService(_mockHttpClientFactory.Object, _mockWebhookOptions.Object, _mockLogger.Object);
        var paymentRequest = CreateTestPaymentRequest();

        // Act
        await service.NotifyPaymentChange(paymentRequest);

        // Assert
        _mockHttpClientFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NotifyPaymentChange_WithNullWebhooks_DoesNothing()
    {
        // Arrange
        _mockWebhookOptions.Setup(o => o.Value).Returns((WebhookConfigList)null!);

        var service = new WebhookService(_mockHttpClientFactory.Object, _mockWebhookOptions.Object, _mockLogger.Object);
        var paymentRequest = CreateTestPaymentRequest();

        // Act
        await service.NotifyPaymentChange(paymentRequest);

        // Assert
        _mockHttpClientFactory.Verify(f => f.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NotifyPaymentChange_WithWebhooks_SendsRequestsToAllUrls()
    {
        // Arrange
        var webhookConfigs = CreateTestWebhookConfigs();
        _mockWebhookOptions.Setup(o => o.Value).Returns(webhookConfigs);

        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK));

        var service = new WebhookService(_mockHttpClientFactory.Object, _mockWebhookOptions.Object, _mockLogger.Object);
        var paymentRequest = CreateTestPaymentRequest();

        // Act
        await service.NotifyPaymentChange(paymentRequest);

        // Assert
        _mockHttpMessageHandler
            .Protected()
            .Verify(
                "SendAsync",
                Times.Exactly(2),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task NotifyPaymentChange_SendsCorrectJsonPayloadToAllConfigs()
    {
        // Arrange
        var webhookConfigs = CreateTestWebhookConfigs();
        _mockWebhookOptions.Setup(o => o.Value).Returns(webhookConfigs);

        var capturedRequests = new List<HttpRequestMessage>();
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequests.Add(req)) // Add to list
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK));

        var service = new WebhookService(_mockHttpClientFactory.Object, _mockWebhookOptions.Object, _mockLogger.Object);
        var paymentRequest = CreateTestPaymentRequest();

        // Act
        await service.NotifyPaymentChange(paymentRequest);

        // Assert
        Assert.Equal(webhookConfigs.Count, capturedRequests.Count);

        var expectedJsonPayload = JsonSerializer.Serialize(paymentRequest);

        for (int i = 0; i < capturedRequests.Count; i++)
        {
            var request = capturedRequests[i];
            var expectedEndpoint = webhookConfigs[i];

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(expectedEndpoint.Url, request.RequestUri!);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);

            var actualJsonPayload = await request.Content.ReadAsStringAsync();
            Assert.Equal(expectedJsonPayload, actualJsonPayload);
        }
    }

    [Fact]
    public async Task NotifyPaymentChange_IncludesCorrectSignatureHeader()
    {
        // Arrange
        var webhookConfigs = CreateTestWebhookConfigs();
        _mockWebhookOptions.Setup(o => o.Value).Returns(webhookConfigs);

        HttpRequestMessage? capturedRequest = null;
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK));

        var service = new WebhookService(_mockHttpClientFactory.Object, _mockWebhookOptions.Object, _mockLogger.Object);
        var paymentRequest = CreateTestPaymentRequest();

        // Act
        await service.NotifyPaymentChange(paymentRequest);

        // Assert
        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains("X-Signature"));

        var signatureHeader = capturedRequest.Headers.GetValues("X-Signature").First();
        Assert.StartsWith("sha256=", signatureHeader);
        Assert.True(signatureHeader.Length > 7);
    }

    [Fact]
    public async Task NotifyPaymentChange_SendsToCorrectUrls()
    {
        // Arrange
        var webhookConfigs = CreateTestWebhookConfigs();
        _mockWebhookOptions.Setup(o => o.Value).Returns(webhookConfigs);

        var capturedRequests = new List<HttpRequestMessage>();
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequests.Add(req))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK));

        var service = new WebhookService(_mockHttpClientFactory.Object, _mockWebhookOptions.Object, _mockLogger.Object);
        var paymentRequest = CreateTestPaymentRequest();

        // Act
        await service.NotifyPaymentChange(paymentRequest);

        // Assert
        Assert.Equal(2, capturedRequests.Count);
        Assert.Contains(capturedRequests, r => r.RequestUri!.ToString() == "https://example.com/webhook1");
        Assert.Contains(capturedRequests, r => r.RequestUri!.ToString() == "https://example.com/webhook2");
    }

    [Fact]
    public async Task NotifyPaymentChange_ContinuesWithOtherWebhooksOnError()
    {
        // Arrange
        var webhookConfigs = CreateTestWebhookConfigs();
        _mockWebhookOptions.Setup(o => o.Value).Returns(webhookConfigs);

        var callCount = 0;
        _mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("First webhook failed");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

        var service = new WebhookService(_mockHttpClientFactory.Object, _mockWebhookOptions.Object, _mockLogger.Object);
        var paymentRequest = CreateTestPaymentRequest();

        // Act
        await service.NotifyPaymentChange(paymentRequest);

        // Assert
        Assert.Equal(2, callCount);
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => !string.IsNullOrWhiteSpace(v.ToString())),
                It.IsAny<HttpRequestException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
