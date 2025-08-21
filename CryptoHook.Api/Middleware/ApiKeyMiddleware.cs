using CryptoHook.Api.Models.Attributes;
using CryptoHook.Api.Models.Configs;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace CryptoHook.Api.Middleware;

public class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger, IOptions<ApiKeyConfig> apiKeyConfig)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<ApiKeyMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ApiKeyConfig _apiKeyConfig = apiKeyConfig?.Value ?? throw new ArgumentNullException(nameof(apiKeyConfig));

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
            _logger.LogWarning("API key missing from request to {Path}", context.Request.Path);
            await WriteUnauthorizedResponse(context, "API key is required");
            return;
        }

        var apiKey = extractedApiKey.ToString();

        if (!_apiKeyConfig.Contains(apiKey))
        {
            _logger.LogWarning("Invalid API key used for request to {Path}", context.Request.Path);
            await WriteUnauthorizedResponse(context, "Invalid API key");
            return;
        }

        _logger.LogDebug("Valid API key used for request to {Path}", context.Request.Path);
        await _next(context);
    }

    private static bool RequiresApiKey(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint == null)
        {
            return false;
        }

        var actionDescriptor = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>();

        return actionDescriptor?.ControllerTypeInfo.GetCustomAttribute<RequireApiKeyAttribute>() is not null ||
        actionDescriptor?.MethodInfo.GetCustomAttribute<RequireApiKeyAttribute>() is not null;
    }

    private static async Task WriteUnauthorizedResponse(HttpContext context, string message)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync(message);
    }
}
