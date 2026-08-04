using System.Numerics;
using CryptoHook.Api.Models.Attributes;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using CryptoHook.Api.Data;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CryptoHook.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/payments")]
[ApiVersion("1.0")]
[RequireApiKey]
public class PaymentController(
    ILogger<PaymentController> logger,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    ICryptoServiceFactory cryptoServiceFactory) : ControllerBase
{
    /// <summary>
    /// How often the creation of a payment request is retried when a concurrent request
    /// claimed the same derivation index first.
    /// </summary>
    internal const int MaxCreateAttempts = 5;

    /// <summary>
    /// Recognises the lost-race case that is worth retrying: the unique index on
    /// (CurrencySymbol, Network, DerivationIndex) rejecting a duplicate.
    /// </summary>
    /// <remarks>
    /// Retrying on any persistence failure burns a fresh derivation index per attempt for problems
    /// that retrying cannot fix, and SQLITE_BUSY under write contention looks exactly like a lost
    /// race from the outside. Providers that do not report a SQLite error code (the in-memory
    /// provider used by the tests) keep the permissive behaviour, since there is nothing to narrow
    /// on.
    /// </remarks>
    private static bool IsDerivationIndexTaken(DbUpdateException exception)
    {
        return exception.InnerException is not SqliteException sqlite
               || sqlite.SqliteErrorCode == SqliteConstraintErrorCode;
    }

    /// <summary>SQLITE_CONSTRAINT.</summary>
    private const int SqliteConstraintErrorCode = 19;

    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly ICryptoServiceFactory _cryptoServiceFactory = cryptoServiceFactory;
    private readonly ILogger<PaymentController> _logger = logger;

    [HttpGet]
    [Route("{id}")]
    public async Task<ActionResult<PaymentRequest>> GetPaymentRequest([FromRoute] Guid id)
    {
        _logger.LogInformation("Fetching payment request with ID: {Id}", id);

        try
        {
            await using var databaseContext = await _dbContextFactory.CreateDbContextAsync();
            var paymentRequest = await databaseContext.PaymentRequests.FindAsync(id);

            if (paymentRequest == null)
            {
                _logger.LogWarning("Payment request with ID {Id} not found", id);
                var problemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Payment Request Not Found",
                    Detail = $"Payment request with ID '{id}' not found",
                    Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.4"
                };
                return NotFound(problemDetails);
            }

            _logger.LogInformation("Retrieved payment request {Id} with status {Status}", paymentRequest.Id, paymentRequest.Status);
            return Ok(paymentRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch payment request with ID {Id}", id);
            var problemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal Server Error",
                Detail = "An error occurred while fetching the payment request",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.1"
            };
            return StatusCode(StatusCodes.Status500InternalServerError, problemDetails);
        }
    }

    [HttpPost]
    // The amount is constrained in the route rather than only checked afterwards: BigInteger
    // parsing is superlinear, so an unbounded numeric path segment is a cheap way to burn CPU on an
    // endpoint that has to parse it before anything else can reject it.
    [Route("{symbol}/{network}/{amount:regex(^[[0-9]]{{1,39}}$)}")]
    [EnableRateLimiting(RateLimitPolicies.PaymentCreation)]
    public async Task<ActionResult<PaymentRequest>> CreatePaymentRequest(string symbol, string network, BigInteger amount)
    {
        _logger.LogInformation("Creating payment request - Symbol: {Symbol}, Network: {Network}, Amount: {Amount}", symbol, network, amount);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            _logger.LogWarning("Crypto symbol is required but was not provided");
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = "Crypto symbol is required",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1"
            });
        }

        if (string.IsNullOrWhiteSpace(network))
        {
            _logger.LogWarning("Network is required but was not provided");
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = "Network is required",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1"
            });
        }

        if (amount <= 0)
        {
            _logger.LogWarning("Invalid amount provided. Symbol: {Symbol}, Network: {Network}, Amount: {Amount}", symbol, network, amount);
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = "Amount is required to be greater than zero",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1"
            });
        }

        try
        {
            // Inside the try: resolving a service parses an extended public key and can throw for a
            // misconfigured currency, which outside of it would escape as an unhandled 500.
            var cryptoService = _cryptoServiceFactory.GetService(symbol, network);

            if (cryptoService == null)
            {
                _logger.LogWarning("Unsupported cryptocurrency. Symbol: {Symbol}, Network: {Network}", symbol, network);
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Bad Request",
                    Detail = $"Cryptocurrency '{symbol}' on network '{network}' is not supported",
                    Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.1"
                });
            }

            await using var gateContext = await _dbContextFactory.CreateDbContextAsync();
            var config = cryptoService.CurrencyConfig;

            var pendingCount = await gateContext.PaymentRequests.CountAsync(pr =>
                pr.CurrencySymbol == config.Symbol &&
                pr.Network == config.Network &&
                pr.Status == PaymentStatus.Pending);

            if (pendingCount >= config.MaxPendingRequests)
            {
                _logger.LogWarning(
                    "Refusing to create a payment request for {Symbol} on {Network}: {PendingCount} requests are already pending (limit {Limit})",
                    config.Symbol, config.Network, pendingCount, config.MaxPendingRequests);

                return StatusCode(StatusCodes.Status429TooManyRequests, new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too Many Pending Payment Requests",
                    Detail = $"There are already {pendingCount} pending payment requests for '{config.Symbol}' on '{config.Network}'. Try again once some of them have been paid or expired.",
                    Type = "https://datatracker.ietf.org/doc/html/rfc6585#section-4"
                });
            }

            var gateResult = await CheckAddressGapLimit(gateContext, config);

            if (gateResult is not null)
            {
                return gateResult;
            }

            return await CreateWithNextDerivationIndex(cryptoService);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create payment request for symbol {Symbol}, network {Network}, and amount {Amount}", symbol, network, amount);
            return StatusCode(StatusCodes.Status500InternalServerError, new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal Server Error",
                Detail = "An error occurred while creating the payment request",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.1"
            });
        }

        // A watch-only wallet scans forward from the last address that has any history and stops
        // after a run of empty ones, so once that run reaches the wallet's gap limit the merchant's
        // wallet no longer discovers incoming funds, including funds paid to addresses this service
        // is still handing out. The count of pending requests says nothing about this: indices are
        // consumed monotonically and never reused, so every expired request lengthens the run
        // permanently. Clearing the condition means paying the most recently issued address, or
        // rescanning the wallet with a larger gap limit.
        async Task<ActionResult<PaymentRequest>?> CheckAddressGapLimit(DatabaseContext context, CurrencyConfig config)
        {
            var currencyRequests = context.PaymentRequests
                .Where(pr => pr.CurrencySymbol == config.Symbol && pr.Network == config.Network);

            var highestIndex = await currencyRequests.MaxAsync(pr => (long?)pr.DerivationIndex);

            if (highestIndex is not { } lastIssued)
            {
                return null;
            }

            var highestFundedIndex = await currencyRequests
                .Where(pr => pr.AmountPaid != BigInteger.Zero)
                .MaxAsync(pr => (long?)pr.DerivationIndex) ?? -1;

            var unusedRun = lastIssued - highestFundedIndex;

            if (unusedRun < config.AddressGapLimit)
            {
                return null;
            }

            _logger.LogError(
                "Refusing to create a payment request for {Symbol} on {Network}: {UnusedRun} consecutive derivation indices have never received funds, which meets the configured address gap limit of {GapLimit}. " +
                "A watch-only wallet will stop discovering payments beyond this point.",
                config.Symbol, config.Network, unusedRun, config.AddressGapLimit);

            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Address Gap Limit Reached",
                Detail = $"The last {unusedRun} addresses derived for '{config.Symbol}' on '{config.Network}' have never received funds, which meets the configured gap limit of {config.AddressGapLimit}. Issuing more addresses risks the receiving wallet no longer discovering payments. Pay or sweep an outstanding address, or raise AddressGapLimit after rescanning the wallet with a matching gap limit.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.4"
            });
        }

        async Task<ActionResult<PaymentRequest>> CreateWithNextDerivationIndex(ICryptoService service)
        {
            var config = service.CurrencyConfig;

            // Reading the highest index and inserting are two steps, so two concurrent callers
            // can pick the same index. The unique index on (CurrencySymbol, Network,
            // DerivationIndex) turns that race into a failed insert, which we simply retry
            // instead of handing out one address for two payments.
            for (var attempt = 1; attempt <= MaxCreateAttempts; attempt++)
            {
                await using var databaseContext = await _dbContextFactory.CreateDbContextAsync();

                var maxDerivationIndex = await databaseContext.PaymentRequests
                    .Where(pr => pr.CurrencySymbol == config.Symbol && pr.Network == config.Network)
                    .MaxAsync(pr => (long?)pr.DerivationIndex) ?? -1;

                var nextDerivationIndex = maxDerivationIndex + 1;

                // Not uint.MaxValue: BIP32 reads an index with the high bit set as hardened, and a
                // hardened child cannot be derived from an extended public key at all.
                if (nextDerivationIndex > CryptoServiceBase.MaxDerivationIndex)
                {
                    _logger.LogCritical("Derivation index space for {Symbol} on {Network} is exhausted", config.Symbol, config.Network);
                    return StatusCode(StatusCodes.Status507InsufficientStorage, new ProblemDetails
                    {
                        Status = StatusCodes.Status507InsufficientStorage,
                        Title = "Derivation Index Exhausted",
                        Detail = $"No further addresses can be derived for '{config.Symbol}' on '{config.Network}'.",
                    });
                }

                var now = DateTime.UtcNow;
                var paymentRequest = new PaymentRequest
                {
                    Id = Guid.NewGuid(),
                    DerivationIndex = (ulong)nextDerivationIndex,
                    Status = PaymentStatus.Pending,
                    AmountExpected = amount,
                    AmountPaid = BigInteger.Zero,
                    ConfirmationCount = 0,
                    ConfirmationNeeded = config.GetConfirmationsNeeded(amount),
                    Network = config.Network,
                    CurrencySymbol = config.Symbol,
                    // Derived before the insert so the row is written exactly once and the
                    // unique index can reject a duplicated index.
                    ReceivingAddress = service.GetAddressAtIndex((uint)nextDerivationIndex),
                    CreatedAt = now,
                    ExpiresAt = now.AddMinutes(config.InitialPaymentTimeout),
                    UpdatedAt = now,
                    TransactionId = null,
                };

                databaseContext.PaymentRequests.Add(paymentRequest);

                try
                {
                    await databaseContext.SaveChangesAsync();
                }
                catch (DbUpdateException ex) when (IsDerivationIndexTaken(ex))
                {
                    if (attempt == MaxCreateAttempts)
                    {
                        // Falls through to the 409 below. Rethrowing here would surface a lost race
                        // as a 500, which tells the caller nothing about retrying.
                        break;
                    }

                    _logger.LogWarning(ex,
                        "Derivation index {Index} for {Symbol} on {Network} was taken by a concurrent request, retrying (attempt {Attempt}/{MaxAttempts})",
                        nextDerivationIndex, config.Symbol, config.Network, attempt, MaxCreateAttempts);
                    continue;
                }

                _logger.LogInformation("Created payment request {Id} with address {Address} at derivation index {Index}",
                    paymentRequest.Id, paymentRequest.ReceivingAddress, paymentRequest.DerivationIndex);

                return Ok(paymentRequest);
            }

            _logger.LogError("Giving up creating a payment request for {Symbol} on {Network} after {MaxAttempts} attempts",
                config.Symbol, config.Network, MaxCreateAttempts);

            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Detail = "The payment request could not be created due to concurrent requests. Please retry.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.5.8"
            });
        }
    }
}
