using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace CryptoHook.Api.Middleware;

public class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger, ApiKeyRegistry apiKeyRegistry)
{
    public const string ApiKeyHeaderName = "X-API-Key";

    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));
    private readonly ILogger<ApiKeyMiddleware> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ConcurrentDictionary<Endpoint, bool> _apiKeyRequirementCache = new();

    /// <remarks>
    /// The keys are held (and re-hashed on change) by the registry, which the rate limiter shares:
    /// it partitions on the presented key and runs before this middleware, so it needs the same
    /// answer about whether that key is real.
    /// </remarks>
    private readonly ApiKeyRegistry _apiKeyRegistry = apiKeyRegistry ?? throw new ArgumentNullException(nameof(apiKeyRegistry));

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!RequiresApiKey(context))
        {
            await _next(context);
            return;
        }

        // Fail closed: a protected endpoint must never become reachable just because the
        // configuration is missing, empty or failed to bind.
        if (!_apiKeyRegistry.HasKeys)
        {
            _logger.LogCritical(
                "No API keys are configured, denying request to {RequestPath}. Populate the 'ApiKeys' configuration section.",
                context.Request.Path.Value);
            await WriteUnauthorizedResponse(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedApiKey) || extractedApiKey.Count != 1)
        {
            _logger.LogWarning("API key missing or supplied more than once for request to {RequestPath}", context.Request.Path.Value);
            await WriteUnauthorizedResponse(context);
            return;
        }

        var presentedKey = extractedApiKey[0];

        if (string.IsNullOrEmpty(presentedKey))
        {
            _logger.LogWarning("Empty API key used for request to {RequestPath}", context.Request.Path.Value);
            await WriteUnauthorizedResponse(context);
            return;
        }

        if (!_apiKeyRegistry.IsValid(presentedKey))
        {
            _logger.LogWarning("Invalid API key used for request to {RequestPath}", context.Request.Path.Value);
            await WriteUnauthorizedResponse(context);
            return;
        }

        _logger.LogDebug("Valid API key used for request to {RequestPath}", context.Request.Path.Value);
        await _next(context);
    }

    /// <remarks>
    /// Deny by default: every routed endpoint needs a key unless it opts out with
    /// <see cref="AllowAnonymousAttribute"/>. Requiring <c>[RequireApiKey]</c> to be present instead
    /// meant anything that forgot it was silently public, including any endpoint without a
    /// controller action descriptor.
    /// </remarks>
    private bool RequiresApiKey(HttpContext context)
    {
        var endpoint = context.GetEndpoint();

        // Nothing was routed, so there is no endpoint to protect and this ends as a 404.
        if (endpoint == null)
        {
            return false;
        }

        if (_apiKeyRequirementCache.TryGetValue(endpoint, out var requiresApiKey))
        {
            return requiresApiKey;
        }

        requiresApiKey = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;

        _apiKeyRequirementCache[endpoint] = requiresApiKey;

        return requiresApiKey;
    }

    private static async Task WriteUnauthorizedResponse(HttpContext context)
    {
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Unauthorized",
            Detail = "A valid API key is required to access this endpoint.",
            Type = "https://datatracker.ietf.org/doc/html/rfc7235#section-3.1"
        };

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(
            problemDetails,
            options: null,
            contentType: "application/problem+json");
    }

}
