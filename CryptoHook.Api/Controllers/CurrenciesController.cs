using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/currencies")]
[ApiVersion("1.0")]
public class CurrenciesController(ConfigManager configManager, ILogger<CurrenciesController> logger) : ControllerBase
{
    private readonly ConfigManager _configManager = configManager;
    private readonly ILogger<CurrenciesController> _logger = logger;

    [HttpGet]
    public ActionResult<List<AvailableCurrency>> GetAvailableCurrencies()
    {
        try
        {
            _logger.LogInformation("Successfully returned {Count} usable currencies", _configManager.GetUsableCurrencies().Count);
            return Ok(_configManager.GetUsableCurrencies());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while retrieving usable currencies");
            return StatusCode(StatusCodes.Status500InternalServerError, "An error occurred while retrieving usable currencies.");
        }
    }
}