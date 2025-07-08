using CryptoHook.Api.Models.Config;
using CryptoHook.Api.Models.Consts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/currencies")]
[ApiVersion("1.0")]
public class CurrenciesController(IOptions<CurrencyConfigList> currencyConfig, ILogger<CurrenciesController> logger) : ControllerBase
{
    private readonly CurrencyConfigList _currencyConfig = currencyConfig.Value;
    private readonly ILogger<CurrenciesController> _logger = logger;

    [HttpGet]
    public ActionResult<Dictionary<string, string>> GetAvailableCurrencies()
    {
        try
        {
            _logger.LogInformation("Retrieving available currencies");

            var availableCurrencies = AvailableCurrencies.Currencies
                .Where(kv => _currencyConfig.CurrencyConfigs.Any(c => c.Symbol == kv.Key && c.IsEnabled))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            _logger.LogInformation("Successfully retrieved {Count} available currencies", availableCurrencies.Count);

            return Ok(availableCurrencies);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while retrieving available currencies");
            return Problem("An error occurred while retrieving available currencies.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}