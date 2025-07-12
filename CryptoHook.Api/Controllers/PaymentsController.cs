using System.Numerics;
using CryptoHook.Api.Managers;
using CryptoHook.Api.Managers.CryptoManager;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

namespace CryptoHook.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/payments")]
[ApiVersion("1.0")]
public class PaymentController(ILogger<PaymentController> logger, DatabaseContext databaseContext, IServiceProvider serviceProvider) : ControllerBase
{
    private readonly DatabaseContext _databaseContext = databaseContext;
    private readonly ILogger<PaymentController> _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    [HttpGet]
    [Route("{id}")]
    public async Task<ActionResult<PaymentRequest>> GetPaymentRequest(Guid id)
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
    [Route("{symbol}/{amount}")]
    public ActionResult<PaymentRequest> CreatePaymentRequest(string symbol, BigInteger amount)
    {
        _logger.LogInformation("Symbol: {Symbol}, Amount: {Amount}", symbol, amount);

        if (string.IsNullOrWhiteSpace(symbol))
        {
            _logger.LogWarning("Crypto symbol is required but was not provided");
            return BadRequest("Crypto symbol is required");
        }

        if (amount <= 0)
        {
            _logger.LogWarning("Invalid amount provided. Symbol: {Symbol}, Amount: {Amount}", symbol, amount);
            return BadRequest("Amount is required to be greater than zero");
        }

        var cryptoManager = _serviceProvider.GetKeyedService<ICryptoManager>(symbol.ToUpperInvariant());

        if (cryptoManager == null)
        {
            _logger.LogWarning("Unsupported cryptocurrency symbol: {Symbol}", symbol);
            return BadRequest($"Cryptocurrency '{symbol}' is not supported");
        }

        using var transaction = _databaseContext.Database.BeginTransaction();

        try
        {
            var maxDerivationIndex = _databaseContext.PaymentRequests
                .Where(pr => string.Equals(symbol, pr.CurrencySymbol))
                .Max(pr => (long?)pr.DerivationIndex) ?? 0;

            var nextDerivationIndex = (ulong)(maxDerivationIndex + 1);

            var paymentRequest = new PaymentRequest
            {
                Id = Guid.NewGuid(),
                DerivationIndex = nextDerivationIndex,
                Status = PaymentStatusEnum.Pending,
                ExpectedAmount = amount,
                AmountPaid = BigInteger.Zero,
                CurrencySymbol = symbol.ToUpperInvariant(),
                ReceivingAddress = "",
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(cryptoManager.CurrencyConfig.InitialPaymentTimeout),
                TransactionId = null,
            };

            _databaseContext.PaymentRequests.Add(paymentRequest);
            _databaseContext.SaveChanges();

            paymentRequest.ReceivingAddress = cryptoManager.GetAddressAtIndex((uint)paymentRequest.DerivationIndex);
            _databaseContext.SaveChanges();

            transaction.Commit();

            _logger.LogInformation("Created payment request {Id} with address {Address} at derivation index {Index}",
                paymentRequest.Id, paymentRequest.ReceivingAddress, paymentRequest.DerivationIndex);

            return Ok(paymentRequest);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Failed to create payment request for symbol {Symbol} and amount {Amount}", symbol, amount);
            return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while creating the payment request");
        }
    }
}