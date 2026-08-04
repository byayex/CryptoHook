using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;
using CryptoHook.Api.Models.Payments;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace CryptoHook.Api.Services.CryptoServices.DataProvider;

/// <remarks>Reads Ethereum payments from Etherscan's unified multichain API.</remarks>
public sealed class EthereumDataProvider : ICryptoDataProvider, IDisposable
{
    /// <summary>
    /// Etherscan's unified (multichain) API endpoint. The chain is selected with the
    /// <c>chainid</c> parameter instead of a per-network host.
    /// </summary>
    internal const string EtherscanApiUrl = "https://api.etherscan.io/v2/api";

    /// <summary>How many of the address' transactions are fetched per page, oldest first.</summary>
    internal const int InspectedTransactionCount = 100;

    /// <remarks>
    /// The caller sums these listings into a balance, so stopping at one page reports a fully paid
    /// request as underpaid. This cap only bounds the work for a poisoned address, and reaching it
    /// is logged.
    /// </remarks>
    internal const int MaxPagesInspected = 20;

    private readonly ILogger<EthereumDataProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ExplorerRateLimiter _rateLimiter;

    public CurrencyConfig CurrencyConfig { get; }

    public EthereumDataProvider(ILogger<EthereumDataProvider> logger, IHttpClientFactory httpClientFactory, CurrencyConfig currencyConfig)
    {
        ArgumentNullException.ThrowIfNull(currencyConfig);

        _logger = logger;
        _httpClientFactory = httpClientFactory;
        CurrencyConfig = currencyConfig;
        _rateLimiter = new ExplorerRateLimiter(currencyConfig.ExplorerRequestsPerSecond);
    }

    public async Task<List<PaymentTransaction>> GetInboundTransactionsAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!IsValidEthereumAddress(address))
        {
            _logger.LogError("Invalid Ethereum address format: {Address}", address);
            throw new ArgumentException($"Invalid Ethereum address format: {address}", nameof(address));
        }

        _logger.LogDebug("Fetching inbound transactions for address {Address}", address);

        using var httpClient = _httpClientFactory.CreateClient();

        // Two listings, because they cover different things and a payment can arrive through
        // either. 'txlist' holds transactions sent by an externally owned account. 'txlistinternal'
        // holds value moved by a contract: Safe and other smart-contract wallets, and the
        // withdrawal systems of several exchanges, pay this way and appear nowhere in 'txlist'.
        // Watching only the former silently misses those payments entirely.
        var external = await GetTransfersAsync(httpClient, address, "txlist", cancellationToken).ConfigureAwait(false);
        var internalTransfers = await GetTransfersAsync(httpClient, address, "txlistinternal", cancellationToken).ConfigureAwait(false);

        // A transaction can move value both directly and through an internal call, so transfers
        // are summed per transaction hash rather than deduplicated.
        var merged = external.Concat(internalTransfers)
            .GroupBy(transfer => transfer.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Hash = group.First().Hash,
                Value = group.Aggregate(BigInteger.Zero, (total, transfer) => total + transfer.Value),
                BlockNumber = group.Min(transfer => transfer.BlockNumber),
            })
            .Where(transfer => transfer.Value > 0)
            .OrderBy(transfer => transfer.BlockNumber)
            .ToList();

        if (merged.Count == 0)
        {
            _logger.LogDebug("No inbound transactions for address {Address}", address);
            return [];
        }

        // One block-number call for the whole address rather than one per transaction.
        var currentBlockNumber = await GetCurrentBlockNumberAsync(httpClient, cancellationToken).ConfigureAwait(false);

        var transactions = new List<PaymentTransaction>();
        foreach (var transfer in merged)
        {
            var confirmations = currentBlockNumber >= transfer.BlockNumber
                ? (uint)Math.Min(currentBlockNumber - transfer.BlockNumber + 1, uint.MaxValue)
                : 0;

            _logger.LogDebug("Transaction {TxId} for address {Address} has {Confirmations} confirmations and paid {AmountPaid} wei",
                transfer.Hash, address, confirmations, transfer.Value);

            transactions.Add(new PaymentTransaction
            {
                TransactionId = transfer.Hash,
                AmountPaid = transfer.Value,
                Confirmations = confirmations,
            });
        }

        return transactions;
    }

    private static bool IsValidEthereumAddress(string address)
    {
        return !string.IsNullOrEmpty(address) &&
               address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
               address.Length == 42 &&
               address[2..].All(char.IsAsciiHexDigit);
    }

    private async Task<List<EthereumTransfer>> GetTransfersAsync(
        HttpClient httpClient,
        string address,
        string action,
        CancellationToken cancellationToken)
    {
        var transfers = new List<EthereumTransfer>();

        for (var page = 1; page <= MaxPagesInspected; page++)
        {
            var (pageTransfers, inspected) = await GetTransferPageAsync(httpClient, address, action, page, cancellationToken).ConfigureAwait(false);

            transfers.AddRange(pageTransfers);

            // A short page is the end of the history. A full one only means there may be more.
            if (inspected < InspectedTransactionCount)
            {
                return transfers;
            }
        }

        _logger.LogWarning(
            "Address {Address} has more than {Pages} pages of {Action} entries. The remaining history was not inspected and the reported balance may be incomplete.",
            address, MaxPagesInspected, action);

        return transfers;
    }

    private async Task<(List<EthereumTransfer> Transfers, int Inspected)> GetTransferPageAsync(
        HttpClient httpClient,
        string address,
        string action,
        int page,
        CancellationToken cancellationToken)
    {
        var url = BuildApiUri(
            $"module=account&action={action}&address={Uri.EscapeDataString(address)}&startblock=0&endblock=99999999&sort=asc&page={page}&offset={InspectedTransactionCount}");

        var json = await GetStringAsync(httpClient, url, $"{action} for {address}", cancellationToken).ConfigureAwait(false);

        using var jsonDoc = JsonDocument.Parse(json);

        // Etherscan answers rate limiting and invalid-key errors with HTTP 200 and status "0".
        // Treating that as "no transactions" would silently mark paid requests as expired.
        EnsureSuccessfulEtherscanResponse(jsonDoc.RootElement, address);

        if (!jsonDoc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return ([], 0);
        }

        var inspected = 0;
        var transfers = new List<EthereumTransfer>();
        foreach (var tx in result.EnumerateArray())
        {
            inspected++;

            // A reverted transaction still carries its nominal value in the listing even though
            // nothing was transferred. Counting it would settle a payment that never happened.
            if (IsFailed(tx))
            {
                _logger.LogDebug("Skipping failed transaction {TxId} for address {Address}",
                    GetOptionalString(tx, "hash"), address);
                continue;
            }

            var hash = GetOptionalString(tx, "hash");
            var to = GetOptionalString(tx, "to");
            var valueString = GetOptionalString(tx, "value");
            var blockNumberString = GetOptionalString(tx, "blockNumber");

            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(to) ||
                string.IsNullOrEmpty(valueString) || string.IsNullOrEmpty(blockNumberString))
            {
                continue;
            }

            if (!string.Equals(to, address, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (BigInteger.TryParse(valueString, CultureInfo.InvariantCulture, out var value) && value > 0 &&
                ulong.TryParse(blockNumberString, CultureInfo.InvariantCulture, out var blockNumber))
            {
                transfers.Add(new EthereumTransfer(hash, value, blockNumber));
            }
        }

        return (transfers, inspected);
    }

    /// <summary>
    /// Recognises a reverted transaction. External transactions report the outcome in
    /// <c>txreceipt_status</c> ("0" means reverted) and internal ones in <c>isError</c>
    /// ("1" means the call failed); older external records only carry <c>isError</c>.
    /// </summary>
    private static bool IsFailed(JsonElement tx)
    {
        if (string.Equals(GetOptionalString(tx, "isError"), "1", StringComparison.Ordinal))
        {
            return true;
        }

        var receiptStatus = GetOptionalString(tx, "txreceipt_status");
        return string.Equals(receiptStatus, "0", StringComparison.Ordinal);
    }

    private void EnsureSuccessfulEtherscanResponse(JsonElement root, string address)
    {
        if (!root.TryGetProperty("status", out var statusElement))
        {
            return;
        }

        var status = statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString()
            : statusElement.ToString();

        if (string.Equals(status, "1", StringComparison.Ordinal))
        {
            return;
        }

        var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;

        // "No transactions found" is the documented empty result, not a failure.
        if (message?.Contains("No transactions found", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var detail = root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.String
            ? resultElement.GetString()
            : null;

        _logger.LogError("Block explorer rejected the transaction query for {Address}: {Message} {Detail}", address, message, detail);
        throw new HttpRequestException($"Block explorer rejected the transaction query for {address}: {message} {detail}".Trim());
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private async Task<ulong> GetCurrentBlockNumberAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(
            httpClient,
            BuildApiUri("module=proxy&action=eth_blockNumber"),
            "the current block number",
            cancellationToken).ConfigureAwait(false);

        using var blockDoc = JsonDocument.Parse(json);

        var currentBlockHex = blockDoc.RootElement.TryGetProperty("result", out var resultElement)
            ? resultElement.GetString()
            : null;

        if (string.IsNullOrEmpty(currentBlockHex))
        {
            _logger.LogError("The explorer returned no current block number.");
            throw new InvalidOperationException("The explorer returned no current block number.");
        }

        return Convert.ToUInt64(currentBlockHex, 16);
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

    private Uri BuildApiUri(string query)
    {
        var uri = $"{EtherscanApiUrl}?chainid={EthereumNetworks.GetChainId(CurrencyConfig.Network)}&{query}";

        // Keyless requests are throttled hard and eventually rejected outright.
        if (!string.IsNullOrWhiteSpace(CurrencyConfig.ExplorerApiKey))
        {
            uri += $"&apikey={Uri.EscapeDataString(CurrencyConfig.ExplorerApiKey)}";
        }

        return new Uri(uri);
    }

    public void Dispose() => _rateLimiter.Dispose();

    private sealed record EthereumTransfer(string Hash, BigInteger Value, ulong BlockNumber);
}
