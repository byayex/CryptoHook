namespace CryptoHook.Api.Services;

using System.Threading;
using System.Threading.Tasks;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Services.CryptoServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class PaymentCheckWorker(
    ILogger<PaymentCheckWorker> logger,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    IServiceProvider serviceProvider) : BackgroundService
{
    private readonly ILogger<PaymentCheckWorker> _logger = logger;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
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

        using var scope = _serviceProvider.CreateScope();
        var paymentsByCurrency = paymentsToCheck.GroupBy(p => p.CurrencySymbol);

        foreach (var group in paymentsByCurrency)
        {
            var cryptoService = scope.ServiceProvider.GetKeyedService<ICryptoService>(group.Key);

            if (cryptoService is null)
            {
                _logger.LogError("No crypto service found for currency {CurrencySymbol}", group.Key);
                continue;
            }

            _logger.LogInformation("Checking {Count} payments for {CurrencySymbol}", group.Count(), group.Key);

            foreach (var request in group)
            {
                if (stoppingToken.IsCancellationRequested)
                    return;

                var result = await cryptoService.CheckTransactionStatus(request);

                if (result.Status != request.Status)
                {
                    var webhookService = scope.ServiceProvider.GetService<IWebhookService>();
                    if (webhookService is not null)
                    {
                        await webhookService.NotifyPaymentChange(request.Id, result);
                    }
                    _logger.LogInformation("Payment {PaymentId} status changed from {OldStatus} to {NewStatus}", request.Id, request.Status, result.Status);
                    request.Status = result.Status;
                    request.AmountPaid = result.AmountDetected;
                    request.TransactionId = result.TransactionId;
                }
            }
        }

        await dbContext.SaveChangesAsync(stoppingToken);
        return;
    }
}