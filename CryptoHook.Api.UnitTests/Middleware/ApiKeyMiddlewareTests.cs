using System.Net;
using System.Reflection;
using System.Text.Json;
using CryptoHook.Api.Middleware;
using CryptoHook.Api.Models.Configs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CryptoHook.Api.UnitTests.Middleware;

public class ApiKeyMiddlewareTests
{
    private const string ValidKey = "a-sufficiently-long-api-key";
    private const string OtherValidKey = "another-sufficiently-long-api-key";

    private static class Controller
    {
        public static void Action() { }
    }

    private static ApiKeyMiddleware CreateMiddleware(IEnumerable<string> keys, RequestDelegate next)
    {
        var config = new ApiKeyConfig();
        config.AddRange(keys);

        // Owned by the middleware for the lifetime of the test; the monitor it subscribes to is a
        // no-op, so there is nothing to release.
#pragma warning disable CA2000
        var registry = new ApiKeyRegistry(NullLogger<ApiKeyRegistry>.Instance, new StaticOptionsMonitor(config));
#pragma warning restore CA2000

        return new ApiKeyMiddleware(next, NullLogger<ApiKeyMiddleware>.Instance, registry);
    }

    private sealed class StaticOptionsMonitor(ApiKeyConfig config) : IOptionsMonitor<ApiKeyConfig>
    {
        public ApiKeyConfig CurrentValue { get; } = config;

        public ApiKeyConfig Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ApiKeyConfig, string?> listener) => new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static DefaultHttpContext CreateContext(bool routed, string? apiKey, int apiKeyHeaderCount = 1, bool allowAnonymous = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/payments";
        context.Response.Body = new MemoryStream();

        if (routed)
        {
            var metadata = new List<object>
            {
                new ControllerActionDescriptor
                {
                    ControllerTypeInfo = typeof(Controller).GetTypeInfo(),
                    MethodInfo = typeof(Controller).GetMethod("Action")!,
                },
            };

            if (allowAnonymous)
            {
                metadata.Add(new AllowAnonymousAttribute());
            }

            context.Features.Set<IEndpointFeature>(new EndpointFeature
            {
                Endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"),
            });
        }

        if (apiKey is not null)
        {
            context.Request.Headers[ApiKeyMiddleware.ApiKeyHeaderName] =
                Enumerable.Repeat(apiKey, apiKeyHeaderCount).ToArray();
        }

        return context;
    }

    private sealed class EndpointFeature : IEndpointFeature
    {
        public Endpoint? Endpoint { get; set; }
    }

    private static async Task<string> ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task InvokeAsync_ProtectedEndpointWithoutConfiguredKeys_DeniesRequest()
    {
        // Fail closed: treating an empty key list as "no authentication required" would make a
        // default deployment fully public.
        var nextCalled = false;
        var middleware = CreateMiddleware([], _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(routed: true, apiKey: null);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ProtectedEndpointWithoutConfiguredKeys_DeniesEvenWhenAKeyIsPresented()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware([], _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(routed: true, ValidKey);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_MissingKey_RespondsWith401StatusCodeAndProblemDetails()
    {
        // A rejection written with a 200 status code reads as a success to any caller that checks
        // the status code.
        var middleware = CreateMiddleware([ValidKey], _ => Task.CompletedTask);
        var context = CreateContext(routed: true, apiKey: null);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        using var document = JsonDocument.Parse(await ReadBody(context));
        Assert.Equal(401, document.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task InvokeAsync_InvalidKey_RespondsWith401()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware([ValidKey], _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(routed: true, "not-the-configured-key");

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(ValidKey)]
    [InlineData(OtherValidKey)]
    public async Task InvokeAsync_ValidKey_CallsNext(string presentedKey)
    {
        var nextCalled = false;
        var middleware = CreateMiddleware([ValidKey, OtherValidKey], _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(routed: true, presentedKey);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal((int)HttpStatusCode.OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_KeySuppliedMoreThanOnce_RespondsWith401()
    {
        // Repeated headers must be rejected, not joined into one string and compared.
        var nextCalled = false;
        var middleware = CreateMiddleware([ValidKey], _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(routed: true, ValidKey, apiKeyHeaderCount: 2);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_EmptyKeyHeader_RespondsWith401()
    {
        var middleware = CreateMiddleware([ValidKey], _ => Task.CompletedTask);
        var context = CreateContext(routed: true, string.Empty);

        await middleware.InvokeAsync(context);

        Assert.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_EndpointMarkedAllowAnonymous_IsPublic()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware([ValidKey], _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(routed: true, apiKey: null, allowAnonymous: true);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_EndpointWithoutAnyAnnotation_StillRequiresAKey()
    {
        // Deny by default. An endpoint that forgets to declare anything must not be public just
        // because it forgot.
        var nextCalled = false;
        var middleware = CreateMiddleware([ValidKey], _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(routed: true, apiKey: null);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal((int)HttpStatusCode.Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_UnroutedRequest_IsPassedThrough()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware([ValidKey], _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var context = CreateContext(routed: false, apiKey: null);

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ProtectedEndpointIsCachedPerEndpoint_StillEnforcesTheKey()
    {
        var nextCalls = 0;
        var middleware = CreateMiddleware([ValidKey], _ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(CreateContext(routed: true, ValidKey));
        var secondContext = CreateContext(routed: true, "wrong-key-but-long-enough");
        await middleware.InvokeAsync(secondContext);

        Assert.Equal(1, nextCalls);
        Assert.Equal((int)HttpStatusCode.Unauthorized, secondContext.Response.StatusCode);
    }
}
