namespace CryptoHook.Api.Services;

using System.Threading;
using System.Threading.Tasks;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class PaymentCheckWorker(
    ILogger<PaymentCheckWorker> logger,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    ICryptoServiceFactory cryptoServiceFactory,
    IWebhookService webhookService) : BackgroundService
{
    private readonly ILogger<PaymentCheckWorker> _logger = logger;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly ICryptoServiceFactory _cryptoServiceFactory = cryptoServiceFactory;
    private readonly IWebhookService _webhookService = webhookService;
    private readonly int _checkInterval = 15; // Default check interval in seconds

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaymentCheckWorker running.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckPayments(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while checking payments.");
            }

            _logger.LogDebug("Next check in {Delay} seconds.", _checkInterval);
            await Task.Delay(TimeSpan.FromSeconds(_checkInterval), stoppingToken);
        }
    }

    private async Task CheckPayments(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Checking for pending or paid payments.");

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(stoppingToken);

        var paymentsToCheck = await dbContext.PaymentRequests
            .Where(p => p.Status == PaymentStatusEnum.Pending || p.Status == PaymentStatusEnum.Paid)
            .ToListAsync(stoppingToken);

        if (paymentsToCheck.Count == 0)
        {
            _logger.LogInformation("No payments to check.");
            return;
        }

        var paymentsByCurrencyAndNetwork = paymentsToCheck.GroupBy(p => new { p.CurrencySymbol, p.Network });

        foreach (var group in paymentsByCurrencyAndNetwork)
        {
            var cryptoService = _cryptoServiceFactory.GetService(group.Key.CurrencySymbol, group.Key.Network);

            if (cryptoService is null)
            {
                _logger.LogError("No crypto service found for currency {CurrencySymbol} on network {Network}", group.Key.CurrencySymbol, group.Key.Network);
                continue;
            }

            _logger.LogInformation("Checking {Count} payments for {CurrencySymbol} on {Network}", group.Count(), group.Key.CurrencySymbol, group.Key.Network);

            foreach (var request in group)
            {
                if (stoppingToken.IsCancellationRequested)
                    return;

                var result = await cryptoService.CheckTransactionStatus(request);

                bool shouldNotify = result.Status != request.Status ||
                   result.ConfirmationCount != request.ConfirmationCount;

                request.Status = result.Status;
                request.AmountPaid = result.AmountPaid;
                request.ConfirmationCount = result.ConfirmationCount;
                request.UpdatedAt = DateTime.UtcNow;
                request.TransactionId = result.TransactionId;

                if (shouldNotify)
                {
                    await _webhookService.NotifyPaymentChange(result);
                    _logger.LogInformation("Payment {PaymentId} updated - Status: {Status}, Confirmations: {Confirmations}", request.Id, result.Status, result.ConfirmationCount);
                }
            }
        }

        await dbContext.SaveChangesAsync(stoppingToken);
        return;
    }
}