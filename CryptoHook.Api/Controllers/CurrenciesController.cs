using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Attributes;
using CryptoHook.Api.Models.Configs;
using Microsoft.AspNetCore.Mvc;

namespace CryptoHook.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/currencies")]
[ApiVersion("1.0")]
[RequireApiKey]
public class CurrenciesController(ConfigManager configManager, ILogger<CurrenciesController> logger) : ControllerBase
{
    private readonly ConfigManager _configManager = configManager;
    private readonly ILogger<CurrenciesController> _logger = logger;

    [HttpGet]
    public ActionResult<List<AvailableCurrency>> GetAvailableCurrencies()
    {
        try
        {
            var usableCurrencies = _configManager.GetUsableCurrencies();
            _logger.LogInformation("Successfully returned {Count} usable currencies", usableCurrencies.Count);
            return Ok(usableCurrencies);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while retrieving usable currencies");
            return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while retrieving usable currencies.");
        }
    }
}