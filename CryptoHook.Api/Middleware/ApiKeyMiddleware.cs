using CryptoHook.Api.Models.Attributes;
using CryptoHook.Api.Models.Configs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;

namespace CryptoHook.Api.Middleware;

public class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger, IOptions<ApiKeyConfig> apiKeyConfig)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<ApiKeyMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ApiKeyConfig _apiKeyConfig = apiKeyConfig?.Value ?? throw new ArgumentNullException(nameof(apiKeyConfig));
    private readonly ConcurrentDictionary<Endpoint, bool> _apiKeyRequirementCache = new();

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // If no API keys are configured, skip authentication
        if (_apiKeyConfig.Count == 0)
        {
            await _next(context);
            return;
        }

        // Check if the endpoint requires API key authentication
        if (!RequiresApiKey(context))
        {
            await _next(context);
            return;
        }

        // Check for API key in headers
        if (!context.Request.Headers.TryGetValue("X-API-Key", out var extractedApiKey))
        {
            _logger.LogWarning("API key missing from request to {RequestPath}", context.Request.Path.Value);
            await WriteUnauthorizedResponse(context);
            return;
        }

        var apiKey = extractedApiKey.ToString();

        bool isValid = false;
        foreach (var configuredApiKey in _apiKeyConfig)
        {
            if (CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(apiKey),
                System.Text.Encoding.UTF8.GetBytes(configuredApiKey)))
            {
                isValid = true;
                // Don't break here! Continue the loop to ensure the
                // method takes the same amount of time for any valid key.
            }
        }

        if (!isValid)
        {
            _logger.LogWarning("Invalid API key used for request to {RequestPath}", context.Request.Path.Value);
            await WriteUnauthorizedResponse(context);
            return;
        }

        _logger.LogDebug("Valid API key used for request to {RequestPath}", context.Request.Path.Value);
        await _next(context);
    }

    private bool RequiresApiKey(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint == null)
        {
            return false;
        }

        if (_apiKeyRequirementCache.TryGetValue(endpoint, out var requiresApiKey))
        {
            return requiresApiKey;
        }

        var actionDescriptor = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>();

        requiresApiKey = actionDescriptor?.ControllerTypeInfo.GetCustomAttribute<RequireApiKeyAttribute>() is not null ||
                       actionDescriptor?.MethodInfo.GetCustomAttribute<RequireApiKeyAttribute>() is not null;

        _apiKeyRequirementCache[endpoint] = requiresApiKey;

        return requiresApiKey;
    }

    private static async Task WriteUnauthorizedResponse(HttpContext context)
    {
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "A valid API key is required to access this endpoint."
        };
        await context.Response.WriteAsJsonAsync(problemDetails);
    }

}
