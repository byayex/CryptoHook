using System.Globalization;
using System.Net;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace CryptoHook.Api.UnitTests.Services.CryptoServices.DataProvider;

public sealed class BitcoinDataProviderTests : IDisposable
{
    private const string WatchedAddress = "bc1qwatched000000000000000000000000000000";
    private const string OtherAddress = "bc1qother0000000000000000000000000000000";

    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<HttpMessageHandler> _mockHandler = new();
    private readonly List<Uri> _requestedUris = [];

    /// <summary>Esplora transaction listing entries, newest first, as the explorer orders them.</summary>
    private readonly List<ListingEntry> _transactions = [];

    private readonly List<BitcoinDataProvider> _providers = [];

    private uint _tipHeight = 100;

    public BitcoinDataProviderTests()
    {
        _mockHttpClientFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(_mockHandler.Object));

        SetupHandler();
    }

    public void Dispose()
    {
        _providers.ForEach(provider => provider.Dispose());
        _mockHandler.Object.Dispose();
    }

    private static CurrencyConfig CreateConfig(string network = "Main")
    {
        return new CurrencyConfig
        {
            Name = "Bitcoin",
            Symbol = "BTC",
            IsEnabled = true,
            InitialPaymentTimeout = 30.0,
            ExtPubKey = "xpub-not-used-here",
            Network = network,
            ExplorerRequestsPerSecond = 100,
            Confirmations = [new() { Amount = 0, ConfirmationsNeeded = 1 }]
        };
    }

    private BitcoinDataProvider CreateProvider(CurrencyConfig? config = null)
    {
        var provider = new BitcoinDataProvider(
            NullLogger<BitcoinDataProvider>.Instance,
            _mockHttpClientFactory.Object,
            config ?? CreateConfig());

        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// Adds a listing entry in the shape Esplora returns: the outputs and the confirmation status
    /// come with the listing rather than needing a call per transaction.
    /// </summary>
    private string AddTransaction(string txId, params (string Address, long Satoshis)[] outputs)
    {
        return AddTransaction(txId, blockHeight: null, outputs);
    }

    private string AddTransaction(string txId, uint? blockHeight, params (string Address, long Satoshis)[] outputs)
    {
        var vout = string.Join(',', outputs.Select(o =>
            $"{{\"scriptpubkey_address\":\"{o.Address}\",\"value\":{o.Satoshis.ToString(CultureInfo.InvariantCulture)}}}"));

        var status = blockHeight is { } height
            ? $"{{\"confirmed\":true,\"block_height\":{height.ToString(CultureInfo.InvariantCulture)}}}"
            : "{\"confirmed\":false}";

        _transactions.Insert(0, new ListingEntry(
            txId,
            $"{{\"txid\":\"{txId}\",\"vout\":[{vout}],\"status\":{status}}}",
            blockHeight is not null));

        return txId;
    }

    private sealed record ListingEntry(string TxId, string Json, bool Confirmed);

    private static string Listing(IEnumerable<ListingEntry> entries)
    {
        return "[" + string.Join(',', entries.Select(entry => entry.Json)) + "]";
    }

    private void SetupHandler()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>((request, _) =>
            {
                _requestedUris.Add(request.RequestUri!);
                var path = request.RequestUri!.AbsolutePath;

                string body;
                if (path.EndsWith("/txs/mempool", StringComparison.Ordinal))
                {
                    body = Listing(_transactions.Where(entry => !entry.Confirmed));
                }
                else if (path.Contains("/txs/chain", StringComparison.Ordinal))
                {
                    var confirmed = _transactions.Where(entry => entry.Confirmed).ToList();
                    var cursor = path.Split("/txs/chain/", 2, StringSplitOptions.None) is [_, { Length: > 0 } lastSeen]
                        ? lastSeen
                        : null;

                    var skip = cursor is null
                        ? 0
                        : confirmed.FindIndex(entry => entry.TxId == cursor) + 1;

                    body = Listing(confirmed.Skip(skip).Take(BitcoinDataProvider.ExplorerPageSize));
                }
                else if (path.EndsWith("/blocks/tip/height", StringComparison.Ordinal))
                {
                    body = _tipHeight.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body),
                });
            });
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_PaymentToTheAddress_IsReturnedWithConfirmations()
    {
        AddTransaction("tx1", blockHeight: 91, (WatchedAddress, 100000));

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        var transaction = Assert.Single(result);
        Assert.Equal("tx1", transaction.TransactionId);
        Assert.Equal(100000, transaction.AmountPaid);
        Assert.Equal(10u, transaction.Confirmations); // 100 - 91 + 1
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_UnconfirmedPayment_HasZeroConfirmations()
    {
        AddTransaction("tx1", (WatchedAddress, 100000));

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(0u, Assert.Single(result).Confirmations);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_TransactionAboveTheReportedTip_ReportsZeroConfirmations()
    {
        // A reorg or a lagging node can report a tip below the transaction's block, where an
        // unguarded unsigned subtraction wraps around and settles the payment immediately.
        AddTransaction("tx1", blockHeight: 150, (WatchedAddress, 100000));
        _tipHeight = 100;

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(0u, Assert.Single(result).Confirmations);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_TransactionInTheTipBlock_HasOneConfirmation()
    {
        AddTransaction("tx1", blockHeight: 100, (WatchedAddress, 100000));
        _tipHeight = 100;

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(1u, Assert.Single(result).Confirmations);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_OnlyOutputsPayingTheAddressCount()
    {
        // The payer's change output belongs to them, not to this payment.
        AddTransaction("tx1", blockHeight: 90, (WatchedAddress, 100000), (OtherAddress, 250000));

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(100000, Assert.Single(result).AmountPaid);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_SeveralOutputsToTheAddress_AreSummed()
    {
        AddTransaction("tx1", blockHeight: 90, (WatchedAddress, 40000), (WatchedAddress, 60000));

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(100000, Assert.Single(result).AmountPaid);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_TransactionsForOtherAddresses_AreIgnored()
    {
        AddTransaction("payment", blockHeight: 80, (WatchedAddress, 100000));
        AddTransaction("other1", blockHeight: 90, (OtherAddress, 5000));
        AddTransaction("other2", blockHeight: 91, (OtherAddress, 6000));

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(100000, Assert.Single(result).AmountPaid);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_MultiplePayments_AreAllReturnedOldestFirst()
    {
        // Every inbound transaction has to come back: the caller sums them to decide whether the
        // request is covered, so a truncated list produces a wrong balance rather than a stale one.
        AddTransaction("tx1", blockHeight: 90, (WatchedAddress, 1000));
        AddTransaction("tx2", blockHeight: 91, (WatchedAddress, 2000));
        AddTransaction("tx3", blockHeight: 92, (WatchedAddress, 3000));

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(3, result.Count);
        Assert.Equal(["tx1", "tx2", "tx3"], result.Select(t => t.TransactionId));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_FetchesTheChainTipOnlyOnce()
    {
        // The tip is the same for every transaction in a pass. Refetching it per transaction,
        // on top of a status and a hex call each, is what made a pass cost more requests than
        // the worker's interval could absorb.
        AddTransaction("tx1", blockHeight: 90, (WatchedAddress, 1000));
        AddTransaction("tx2", blockHeight: 91, (WatchedAddress, 2000));
        AddTransaction("tx3", blockHeight: 92, (WatchedAddress, 3000));

        await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Single(_requestedUris, uri => uri.AbsolutePath.EndsWith("/blocks/tip/height", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_NoInboundTransactions_SkipsTheChainTipRequest()
    {
        AddTransaction("other", blockHeight: 90, (OtherAddress, 5000));

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Empty(result);
        Assert.DoesNotContain(_requestedUris, uri => uri.AbsolutePath.EndsWith("/blocks/tip/height", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_MoreTransactionsThanFitOnOnePage_AreAllInspected()
    {
        // A truncated listing is not a stale answer but a wrong one: the caller sums these into a
        // balance, so missing history reports a fully paid request as underpaid.
        for (var i = 0; i < BitcoinDataProvider.ExplorerPageSize + 5; i++)
        {
            AddTransaction($"tx{i}", blockHeight: (uint)(50 + i), (WatchedAddress, 1000));
        }

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(BitcoinDataProvider.ExplorerPageSize + 5, result.Count);
        Assert.Equal(30000, result.Sum(transaction => (long)transaction.AmountPaid));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_UnconfirmedAndConfirmedListings_AreBothRead()
    {
        AddTransaction("confirmed", blockHeight: 90, (WatchedAddress, 1000));
        AddTransaction("unconfirmed", (WatchedAddress, 2000));

        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Equal(2, result.Count);
        Assert.Equal(3000, result.Sum(transaction => (long)transaction.AmountPaid));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_NoTransactions_ReturnsEmpty()
    {
        var result = await CreateProvider().GetInboundTransactionsAsync(WatchedAddress);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_InvalidNetwork_Throws()
    {
        var provider = CreateProvider(CreateConfig("NotANetwork"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetInboundTransactionsAsync(WatchedAddress));
    }

    [Fact]
    public async Task GetInboundTransactionsAsync_HttpFailure_Throws()
    {
        _mockHandler.Reset();
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var provider = CreateProvider();

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetInboundTransactionsAsync(WatchedAddress));
    }
}
