using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;
using NBitcoin;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace CryptoHook.Api.Services.CryptoServices.DataProvider;

/// <remarks>
/// Reads Bitcoin payments from a Blockstream Esplora instance. The address transaction listing
/// already carries every output and each transaction's block height, so a whole address costs one
/// listing call per page plus one chain-tip call; fetching each transaction individually instead
/// puts the request count per payment into the double digits and stops the worker from finishing a
/// pass inside its own interval.
/// </remarks>
public sealed class BitcoinDataProvider : ICryptoDataProvider, IDisposable
{
    /// <summary>Esplora returns at most 25 confirmed transactions per page for an address.</summary>
    internal const int ExplorerPageSize = 25;

    /// <remarks>
    /// A receiving address is single use, so needing even a second page is already unusual. This
    /// cap only stops a poisoned address with tens of thousands of dust transactions from making
    /// one payment check run indefinitely; reaching it is logged, because past that point the
    /// reported balance really is incomplete.
    /// </remarks>
    internal const int MaxPagesInspected = 40;

    private readonly ILogger<BitcoinDataProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ExplorerRateLimiter _rateLimiter;

    public CurrencyConfig CurrencyConfig { get; }

    public BitcoinDataProvider(ILogger<BitcoinDataProvider> logger, IHttpClientFactory httpClientFactory, CurrencyConfig currencyConfig)
    {
        ArgumentNullException.ThrowIfNull(currencyConfig);

        _logger = logger;
        _httpClientFactory = httpClientFactory;
        CurrencyConfig = currencyConfig;
        _rateLimiter = new ExplorerRateLimiter(currencyConfig.ExplorerRequestsPerSecond);
    }

    public async Task<List<PaymentTransaction>> GetInboundTransactionsAsync(string address, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Fetching inbound transactions for address {Address}", address);

        var network = Network.GetNetwork(CurrencyConfig.Network)
            ?? throw new InvalidOperationException($"Invalid network '{CurrencyConfig.Network}' configured for {CurrencyConfig.Symbol}.");

        var apiBaseUrl = ExplorerBaseUrlFor(network);

        using var httpClient = _httpClientFactory.CreateClient();

        var candidates = await GetInboundCandidatesAsync(httpClient, apiBaseUrl, address, cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            _logger.LogDebug("No inbound transactions for address {Address}", address);
            return [];
        }

        // The chain tip is the same for every transaction in this pass, so it is fetched once.
        var tipHeight = await GetChainTipHeightAsync(httpClient, apiBaseUrl, cancellationToken).ConfigureAwait(false);

        var transactions = candidates
            .Select(candidate => new PaymentTransaction
            {
                TransactionId = candidate.TransactionId,
                AmountPaid = candidate.AmountPaid,
                Confirmations = ConfirmationsFor(candidate, tipHeight),
            })
            .ToList();

        foreach (var transaction in transactions)
        {
            _logger.LogDebug("Transaction {TxId} for address {Address} has {Confirmations} confirmations and paid {AmountPaid} satoshis",
                transaction.TransactionId, address, transaction.Confirmations, transaction.AmountPaid);
        }

        return transactions;
    }

    /// <remarks>
    /// Exhaustive rather than "mainnet or else testnet": that fallback points TestNet4, Signet and
    /// RegTest at the testnet3 explorer, so confirmations and balances are read off a different
    /// chain than the addresses were derived for, which is a wrong answer rather than a failure.
    /// </remarks>
    private static string ExplorerBaseUrlFor(Network network)
    {
        if (network == Network.Main)
        {
            return "https://blockstream.info/api";
        }

        if (network == Network.TestNet)
        {
            return "https://blockstream.info/testnet/api";
        }

        throw new NotSupportedException(
            $"No block explorer is configured for the Bitcoin network '{network}'. Supported networks are {Network.Main} and {Network.TestNet}.");
    }

    /// <remarks>
    /// The unconfirmed and confirmed listings are requested separately because
    /// <c>last_seen_txid</c> only paginates confirmed history, and a mempool transaction id is not
    /// a valid cursor for it.
    /// </remarks>
    private async Task<List<InboundCandidate>> GetInboundCandidatesAsync(
        HttpClient httpClient,
        string apiBaseUrl,
        string address,
        CancellationToken cancellationToken)
    {
        var encodedAddress = Uri.EscapeDataString(address);
        var candidates = new List<InboundCandidate>();

        var mempool = await GetPageAsync(
            httpClient,
            new Uri($"{apiBaseUrl}/address/{encodedAddress}/txs/mempool"),
            address,
            cancellationToken).ConfigureAwait(false);

        candidates.AddRange(mempool.Candidates);

        string? lastSeenTxId = null;
        var pages = 0;

        while (pages < MaxPagesInspected)
        {
            var uri = lastSeenTxId is null
                ? new Uri($"{apiBaseUrl}/address/{encodedAddress}/txs/chain")
                : new Uri($"{apiBaseUrl}/address/{encodedAddress}/txs/chain/{Uri.EscapeDataString(lastSeenTxId)}");

            var page = await GetPageAsync(httpClient, uri, address, cancellationToken).ConfigureAwait(false);
            pages++;

            candidates.AddRange(page.Candidates);

            // Esplora signals the end of the history with a short page. A full one only means
            // there may be more, so the cursor moves on even if none of it paid this address.
            if (page.Inspected < ExplorerPageSize || page.LastTransactionId is null)
            {
                // Esplora lists the most recent transaction first; callers expect chronological order.
                candidates.Reverse();
                return candidates;
            }

            lastSeenTxId = page.LastTransactionId;
        }

        _logger.LogWarning(
            "Address {Address} has more than {Pages} pages of confirmed transactions. The remaining history was not inspected and the reported balance may be incomplete.",
            address, MaxPagesInspected);

        candidates.Reverse();
        return candidates;
    }

    private async Task<ExplorerPage> GetPageAsync(HttpClient httpClient, Uri uri, string address, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(httpClient, uri, $"transactions for {address}", cancellationToken).ConfigureAwait(false);

        using var jsonDoc = JsonDocument.Parse(json);

        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new HttpRequestException($"Unexpected transaction listing for {address}: expected a JSON array.");
        }

        return ParseInboundTransactions(jsonDoc.RootElement, address);
    }

    private static ExplorerPage ParseInboundTransactions(JsonElement root, string address)
    {
        var inspected = 0;
        string? lastTransactionId = null;
        var candidates = new List<InboundCandidate>();

        foreach (var tx in root.EnumerateArray())
        {
            inspected++;

            var txId = tx.TryGetProperty("txid", out var txIdElement) ? txIdElement.GetString() : null;

            // The cursor for the next page is the last transaction of this one, whether or not it
            // paid this address.
            lastTransactionId = txId ?? lastTransactionId;

            if (string.IsNullOrEmpty(txId) || !tx.TryGetProperty("vout", out var vout) || vout.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // Only the outputs that pay this address count. Everything else in the transaction
            // belongs to other parties, including the payer's own change output.
            var amountPaid = BigInteger.Zero;
            foreach (var output in vout.EnumerateArray())
            {
                var outputAddress = output.TryGetProperty("scriptpubkey_address", out var addressElement)
                    ? addressElement.GetString()
                    : null;

                // Bech32 is case insensitive by specification, so an address that was normalised
                // differently anywhere along the way must still match its own outputs.
                if (!string.Equals(outputAddress, address, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (output.TryGetProperty("value", out var valueElement) && valueElement.TryGetUInt64(out var satoshis))
                {
                    amountPaid += satoshis;
                }
            }

            if (amountPaid <= 0)
            {
                continue;
            }

            var confirmed = tx.TryGetProperty("status", out var status)
                            && status.TryGetProperty("confirmed", out var confirmedElement)
                            && confirmedElement.ValueKind == JsonValueKind.True;

            uint? blockHeight = null;
            if (confirmed && status.TryGetProperty("block_height", out var heightElement) && heightElement.TryGetUInt32(out var height))
            {
                blockHeight = height;
            }

            candidates.Add(new InboundCandidate(txId, amountPaid, blockHeight));
        }

        return new ExplorerPage(candidates, inspected, lastTransactionId);
    }

    private uint ConfirmationsFor(InboundCandidate candidate, uint tipHeight)
    {
        if (candidate.BlockHeight is not { } blockHeight)
        {
            return 0;
        }

        if (tipHeight < blockHeight)
        {
            // Happens during a reorg or when the tip is served by a lagging node. Without this
            // guard the unsigned subtraction wraps around and the payment would look like it had
            // billions of confirmations.
            _logger.LogWarning(
                "Transaction {TxId} is in block {BlockHeight}, which is above the reported chain tip {TipHeight}. Reporting 0 confirmations.",
                candidate.TransactionId, blockHeight, tipHeight);
            return 0;
        }

        return tipHeight - blockHeight + 1;
    }

    private async Task<uint> GetChainTipHeightAsync(HttpClient httpClient, string apiBaseUrl, CancellationToken cancellationToken)
    {
        var body = await GetStringAsync(
            httpClient,
            new Uri($"{apiBaseUrl}/blocks/tip/height"),
            "the chain tip height",
            cancellationToken).ConfigureAwait(false);

        return uint.TryParse(body.Trim(), CultureInfo.InvariantCulture, out var height)
            ? height
            : throw new HttpRequestException($"Could not read the chain tip height from the explorer response '{body}'.");
    }

    private async Task<string> GetStringAsync(HttpClient httpClient, Uri uri, string what, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitForTurnAsync(cancellationToken).ConfigureAwait(false);

        var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Could not fetch {What}. Status: {StatusCode}", what, response.StatusCode);
            throw new HttpRequestException($"Could not fetch {what}. Status: {response.StatusCode}");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _rateLimiter.Dispose();

    private sealed record ExplorerPage(List<InboundCandidate> Candidates, int Inspected, string? LastTransactionId);

    private sealed record InboundCandidate(string TransactionId, BigInteger AmountPaid, uint? BlockHeight);
}
