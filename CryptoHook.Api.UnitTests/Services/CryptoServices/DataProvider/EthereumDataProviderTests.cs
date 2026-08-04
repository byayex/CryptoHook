using System.Globalization;
using System.Net;
using System.Numerics;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace CryptoHook.Api.UnitTests.Services.CryptoServices.DataProvider;

public sealed class EthereumDataProviderTests : IDisposable
{
    private const string Address = "0x1234567890abcdef1234567890abcdef12345678";

    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<HttpMessageHandler> _mockHandler = new();
    private readonly List<Uri> _requestedUris = [];
    private readonly List<EthereumDataProvider> _providers = [];

    public EthereumDataProviderTests()
    {
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_mockHandler.Object));
    }

    public void Dispose()
    {
        _providers.ForEach(provider => provider.Dispose());
        _mockHandler.Object.Dispose();
    }

    private static CurrencyConfig CreateConfig(string network = "Main", string? explorerApiKey = null)
    {
        return new CurrencyConfig
        {
            Name = "Ethereum",
            Symbol = "ETH",
            IsEnabled = true,
            InitialPaymentTimeout = 30.0,
            ExtPubKey = "xpub-not-used-here",
            Network = network,
            ExplorerApiKey = explorerApiKey,
            ExplorerRequestsPerSecond = 100,
            Confirmations = [new() { Amount = 0, ConfirmationsNeeded = 1 }]
        };
    }

    /// <summary>
    /// Answers all three kinds of request the provider makes: the external listing, the internal
    /// one, and the current block number.
    /// </summary>
    private void SetupResponses(string? externalJson = null, string? internalJson = null, string currentBlockHex = "0x64")
    {
        externalJson ??= TxListJson();
        internalJson ??= TxListJson();

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                _requestedUris.Add(request.RequestUri!);
                var query = request.RequestUri!.Query;

                var body = query.Contains("eth_blockNumber", StringComparison.Ordinal)
                    ? $"{{\"jsonrpc\":\"2.0\",\"result\":\"{currentBlockHex}\"}}"
                    : query.Contains("action=txlistinternal", StringComparison.Ordinal)
                        ? internalJson
                        : externalJson;

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                });
            });
    }

    private static string TxListJson(params string[] transactions)
    {
        return $"{{\"status\":\"1\",\"message\":\"OK\",\"result\":[{string.Join(',', transactions)}]}}";
    }

    private static string Tx(string hash, string to, string value, ulong blockNumber, string? isError = null, string? receiptStatus = null)
    {
        var fields = new List<string>
        {
            $"\"hash\":\"{hash}\"",
            $"\"to\":\"{to}\"",
            $"\"value\":\"{value}\"",
            $"\"blockNumber\":\"{blockNumber.ToString(CultureInfo.InvariantCulture)}\"",
        };

        if (isError is not null)
        {
            fields.Add($"\"isError\":\"{isError}\"");
        }

        if (receiptStatus is not null)
        {
            fields.Add($"\"txreceipt_status\":\"{receiptStatus}\"");
        }

        return "{" + string.Join(',', fields) + "}";
    }

    private EthereumDataProvider CreateProvider(CurrencyConfig config)
    {
        var provider = new EthereumDataProvider(NullLogger<EthereumDataProvider>.Instance, _mockHttpClientFactory.Object, config);
        _providers.Add(provider);
        return provider;
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_InboundTransaction_IsReturned()
    {
        SetupResponses(TxListJson(Tx("0xhash1", Address, "1000000000000000000", 90)));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        var transaction = Assert.Single(result);
        Assert.Equal("0xhash1", transaction.TransactionId);
        Assert.Equal(BigInteger.Parse("1000000000000000000", CultureInfo.InvariantCulture), transaction.AmountPaid);
        Assert.Equal(11u, transaction.Confirmations); // 100 - 90 + 1
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_PaymentFromAContract_IsFound()
    {
        // Value moved by a contract appears only in the internal listing. Safe and other
        // smart-contract wallets, and several exchanges' withdrawal systems, pay this way, so
        // watching only the external listing missed those payments entirely.
        SetupResponses(internalJson: TxListJson(Tx("0xinternal", Address, "500", 95)));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        var transaction = Assert.Single(result);
        Assert.Equal("0xinternal", transaction.TransactionId);
        Assert.Equal(new BigInteger(500), transaction.AmountPaid);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_FailedTransaction_IsIgnored()
    {
        // A reverted transaction still carries its nominal value in the listing even though
        // nothing was transferred. Counting it would settle a payment that never happened.
        SetupResponses(TxListJson(Tx("0xreverted", Address, "1000", 90, receiptStatus: "0")));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_FailedInternalTransfer_IsIgnored()
    {
        SetupResponses(internalJson: TxListJson(Tx("0xfailed", Address, "1000", 90, isError: "1")));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_SuccessfulTransaction_IsNotMistakenForAFailure()
    {
        SetupResponses(TxListJson(Tx("0xok", Address, "1000", 90, isError: "0", receiptStatus: "1")));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Equal("0xok", Assert.Single(result).TransactionId);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_SameHashInBothListings_IsSummedOnce()
    {
        // A transaction can move value both directly and through an internal call, so the
        // transfers are summed per hash rather than deduplicated or double counted.
        SetupResponses(
            externalJson: TxListJson(Tx("0xhash", Address, "400", 90)),
            internalJson: TxListJson(Tx("0xhash", Address, "600", 90)));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        var transaction = Assert.Single(result);
        Assert.Equal(new BigInteger(1000), transaction.AmountPaid);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_RateLimitedResponse_Throws()
    {
        // Etherscan reports rate limiting and invalid keys with HTTP 200 and status "0".
        // Silently treating that as "no transactions" would let paid requests expire.
        SetupResponses("{\"status\":\"0\",\"message\":\"NOTOK\",\"result\":\"Max rate limit reached\"}");

        var provider = CreateProvider(CreateConfig());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetInboundTransactionsAsync(Address));
        Assert.Contains("Max rate limit reached", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_NoTransactionsFoundResponse_ReturnsEmpty()
    {
        SetupResponses(
            "{\"status\":\"0\",\"message\":\"No transactions found\",\"result\":[]}",
            "{\"status\":\"0\",\"message\":\"No transactions found\",\"result\":[]}");

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_OutgoingTransactions_AreIgnored()
    {
        SetupResponses(TxListJson(
            Tx("0xout1", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "5", 98),
            Tx("0xout2", "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "5", 97),
            Tx("0xpayment", Address, "42", 96)));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Equal("0xpayment", Assert.Single(result).TransactionId);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_SeveralInboundTransactions_AreAllReturnedOldestFirst()
    {
        SetupResponses(TxListJson(
            Tx("0xpayment1", Address, "42", 94),
            Tx("0xpayment2", Address, "43", 95),
            Tx("0xpayment3", Address, "44", 96)));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Equal(3, result.Count);
        Assert.Equal(["0xpayment1", "0xpayment2", "0xpayment3"], result.Select(t => t.TransactionId));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_ZeroValueTransfer_IsIgnored()
    {
        SetupResponses(TxListJson(Tx("0xhash1", Address, "0", 90)));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_TransactionAboveChainTip_ReportsZeroConfirmations()
    {
        // Guards against unsigned wrap-around, which would look like billions of confirmations.
        SetupResponses(TxListJson(Tx("0xhash1", Address, "42", 200)));

        var result = await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Equal(0u, Assert.Single(result).Confirmations);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_FetchesTheBlockNumberOnlyOnce()
    {
        SetupResponses(TxListJson(
            Tx("0xpayment1", Address, "42", 94),
            Tx("0xpayment2", Address, "43", 95)));

        await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.Single(_requestedUris, uri => uri.Query.Contains("eth_blockNumber", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_NoInboundTransactions_SkipsTheBlockNumberRequest()
    {
        SetupResponses();

        await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.DoesNotContain(_requestedUris, uri => uri.Query.Contains("eth_blockNumber", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_WithExplorerApiKey_SendsItToTheExplorer()
    {
        SetupResponses(TxListJson(Tx("0xhash1", Address, "42", 90)));

        await CreateProvider(CreateConfig(explorerApiKey: "my-explorer-key")).GetInboundTransactionsAsync(Address);

        Assert.All(_requestedUris, uri => Assert.Contains("apikey=my-explorer-key", uri.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_WithoutExplorerApiKey_SendsNoApiKeyParameter()
    {
        SetupResponses(TxListJson(Tx("0xhash1", Address, "42", 90)));

        await CreateProvider(CreateConfig()).GetInboundTransactionsAsync(Address);

        Assert.All(_requestedUris, uri => Assert.DoesNotContain("apikey=", uri.Query, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Main", 1)]
    [InlineData("Sepolia", 11155111)]
    public async Task GetInboundTransactionsAsync_UsesTheChainIdOfTheConfiguredNetwork(string network, int expectedChainId)
    {
        SetupResponses(TxListJson(Tx("0xhash1", Address, "42", 90)));

        await CreateProvider(CreateConfig(network)).GetInboundTransactionsAsync(Address);

        Assert.All(_requestedUris, uri => Assert.Contains($"chainid={expectedChainId}", uri.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_RetiredNetwork_Throws()
    {
        // Goerli was shut down; failing loudly beats querying a dead endpoint.
        SetupResponses();

        var provider = CreateProvider(CreateConfig("Goerli"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetInboundTransactionsAsync(Address));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("0x123")]
    [InlineData("0xZZZ4567890abcdef1234567890abcdef12345678")]
    public async Task GetInboundTransactionsAsync_InvalidAddress_Throws(string address)
    {
        var provider = CreateProvider(CreateConfig());

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetInboundTransactionsAsync(address));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_HttpFailure_Throws()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var provider = CreateProvider(CreateConfig());

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetInboundTransactionsAsync(Address));
    }
}
