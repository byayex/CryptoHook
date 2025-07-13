using CryptoHook.Api.Models.Configs;
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
    public ActionResult<List<AvailableCurrency>> GetAvailableCurrencies()
    {
        try
        {
            _logger.LogInformation("Retrieving available currencies");

            var availableCurrencies = AvailableCurrencies.Currencies
                .Where(c => _currencyConfig.Any(cc => cc.Symbol == c.Symbol && cc.IsEnabled))
                .Select(c => new AvailableCurrency
                {
                    Symbol = c.Symbol,
                    Name = c.Name,
                    Network = c.Network
                }).ToList();

            _logger.LogInformation("Successfully retrieved {Count} available currencies", availableCurrencies.Count);

            return Ok(availableCurrencies);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while retrieving available currencies");
            return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while retrieving available currencies.");
        }
    }
}