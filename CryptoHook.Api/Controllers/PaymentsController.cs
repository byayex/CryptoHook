using System.Numerics;
using CryptoHook.Api.Models.Attributes;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using CryptoHook.Api.Data;
using CryptoHook.Api.Services.CryptoServices.Factory;

namespace CryptoHook.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/payments")]
[ApiVersion("1.0")]
[RequireApiKey]
public class PaymentController(ILogger<PaymentController> logger, DatabaseContext databaseContext, ICryptoServiceFactory cryptoServiceFactory) : ControllerBase
{
    private readonly DatabaseContext _databaseContext = databaseContext;
    private readonly ICryptoServiceFactory _cryptoServiceFactory = cryptoServiceFactory;
    private readonly ILogger<PaymentController> _logger = logger;

    [HttpGet]
    [Route("{id}")]
    public async Task<ActionResult<PaymentRequest>> GetPaymentRequest([FromRoute] Guid id)
    {
        _logger.LogInformation("Fetching payment request with ID: {Id}", id);

        try
        {
            var paymentRequest = await _databaseContext.PaymentRequests.FindAsync(id);

            if (paymentRequest == null)
            {
                _logger.LogWarning("Payment request with ID {Id} not found", id);
                return NotFound($"Payment request with ID '{id}' not found");
            }

            _logger.LogInformation("Retrieved payment request: {@PaymentRequest}", paymentRequest);
            return Ok(paymentRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch payment request with ID {Id}", id);
            return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while fetching the payment request");
        }
    }

    [HttpPost]
    [Route("{symbol}/{network}/{amount}")]
    public async Task<ActionResult<PaymentRequest>> CreatePaymentRequest(string symbol, string network, BigInteger amount)
    {
        _logger.LogInformation("Creating payment request - Symbol: {Symbol}, Network: {Network}, Amount: {Amount}", symbol, network, amount);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            _logger.LogWarning("Crypto symbol is required but was not provided");
            return BadRequest("Crypto symbol is required");
        }

        if (amount <= 0)
        {
            _logger.LogWarning("Invalid amount provided. Symbol: {Symbol}, Network: {Network}, Amount: {Amount}", symbol, network, amount);
            return BadRequest("Amount is required to be greater than zero");
        }

        var cryptoManager = _cryptoServiceFactory.GetService(symbol, network);

        if (cryptoManager == null)
        {
            _logger.LogWarning("Unsupported cryptocurrency. Symbol: {Symbol}, Network: {Network}", symbol, network);
            return BadRequest($"Cryptocurrency '{symbol}' on network '{network}' is not supported");
        }

        using var transaction = await _databaseContext.Database.BeginTransactionAsync();

        try
        {
            var maxDerivationIndex = _databaseContext.PaymentRequests
                .Where(pr => string.Equals(symbol, pr.CurrencySymbol, StringComparison.Ordinal))
                .Max(pr => (long?)pr.DerivationIndex) ?? 0;

            var nextDerivationIndex = (ulong)(maxDerivationIndex + 1);

            var paymentRequest = new PaymentRequest
            {
                Id = Guid.NewGuid(),
                DerivationIndex = nextDerivationIndex,
                Status = PaymentStatus.Pending,
                AmountExpected = amount,
                AmountPaid = BigInteger.Zero,
                ConfirmationCount = 0,
                ConfirmationNeeded = cryptoManager.CurrencyConfig.GetConfirmationsNeeded(amount),
                Network = cryptoManager.CurrencyConfig.Network,
                CurrencySymbol = cryptoManager.CurrencyConfig.Symbol,
                ReceivingAddress = "",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(cryptoManager.CurrencyConfig.InitialPaymentTimeout),
                UpdatedAt = DateTime.UtcNow,
                TransactionId = null,
            };

            await _databaseContext.PaymentRequests.AddAsync(paymentRequest);
            await _databaseContext.SaveChangesAsync();

            paymentRequest.ReceivingAddress = cryptoManager.GetAddressAtIndex((uint)paymentRequest.DerivationIndex);
            await _databaseContext.SaveChangesAsync();

            await transaction.CommitAsync();

            _logger.LogInformation("Created payment request {Id} with address {Address} at derivation index {Index}",
                paymentRequest.Id, paymentRequest.ReceivingAddress, paymentRequest.DerivationIndex);

            return Ok(paymentRequest);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to create payment request for symbol {Symbol}, network {Network}, and amount {Amount}", symbol, network, amount);
            return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while creating the payment request");
        }
    }
}