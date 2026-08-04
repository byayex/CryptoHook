using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using CryptoHook.Api.Data;
using CryptoHook.Api.Managers;
using CryptoHook.Api.Middleware;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Consts;
using CryptoHook.Api.Models.Converters;
using CryptoHook.Api.Services;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Turns an unhandled exception into a ProblemDetails response rather than a bare 500 (and, in
// development, a stack trace served to the caller).
builder.Services.AddProblemDetails();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
});

// Configure rather than Bind, because the confirmation amounts are stored in a non-public property
// so that they can round-trip through configuration as strings.
builder.Services.AddOptions<CurrencyConfigList>()
    .Configure(options =>
    {
        builder.Configuration.GetSection("CurrencyConfigs").Bind(options, binderOptions =>
        {
            binderOptions.BindNonPublicProperties = true;
        });
    })
    .ValidateOnStart()
    .ValidateDataAnnotations();

builder.Services.AddOptions<WebhookConfigList>()
    .Bind(builder.Configuration.GetSection("Webhooks"))
    .ValidateOnStart()
    .ValidateDataAnnotations();

// Validated on start so that a missing or empty 'ApiKeys' section fails the deployment
// loudly instead of leaving protected endpoints unreachable at runtime.
builder.Services.AddOptions<ApiKeyConfig>()
    .Bind(builder.Configuration.GetSection("ApiKeys"))
    .ValidateOnStart()
    .ValidateDataAnnotations();

builder.Services.AddOptions<WebhookDeliveryConfig>()
    .Bind(builder.Configuration.GetSection("WebhookDelivery"))
    .ValidateOnStart()
    .ValidateDataAnnotations();

builder.Services.AddOptions<PaymentCheckConfig>()
    .Bind(builder.Configuration.GetSection("PaymentCheck"))
    .ValidateOnStart()
    .ValidateDataAnnotations();

// Shared by the API key middleware and the rate limiter, which needs the same verdict on a
// presented key one stage earlier in the pipeline.
builder.Services.AddSingleton<ApiKeyRegistry>();

builder.Services.AddSingleton<ConfigManager>();

builder.Services.AddSingleton<WebhookDispatchSignal>();

builder.Services.AddSingleton<IWebhookService, WebhookService>();

builder.Services.AddSingleton<ICryptoServiceFactory, CryptoServiceFactory>();

builder.Services.AddHostedService<PaymentCheckWorker>();

// Notifications are queued by the payment worker and sent from here, so that a subscriber that
// is slow or unreachable delays nobody and no event is lost when one is.
builder.Services.AddHostedService<WebhookDispatchWorker>();

builder.Services.AddDbContextFactory<DatabaseContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Database") ?? "Data Source=CryptoHook.db"));

builder.Services.AddHttpClient();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Every request, including the ones that fail authentication. Without this the API key check
    // itself is unthrottled and can be brute forced as fast as the network allows.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientPartitionKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));

    // Creating a payment request burns a derivation index permanently, so it is limited separately
    // to keep a caller from running the watching wallet past its gap limit.
    //
    // Partitioned per caller rather than shared: one global window lets any caller, authenticated or
    // not, exhaust the budget for every merchant at once.
    options.AddPolicy(RateLimitPolicies.PaymentCreation, context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientPartitionKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

// The presented API key when it is a key that actually exists, so that limits follow the caller
// rather than whatever address they happen to connect from; the remote address otherwise. Hashed
// because this ends up in limiter state and in diagnostics.
//
// Only a *validated* key may partition. The limiter runs before the API key middleware, on purpose,
// so that failed authentications are throttled too; keying on the raw header would hand every guess
// in a brute force its own fresh window, which is the attack this limiter exists to stop, and would
// let an unauthenticated caller allocate a limiter partition per guess.
static string ClientPartitionKey(HttpContext context)
{
    var apiKey = context.Request.Headers[ApiKeyMiddleware.ApiKeyHeaderName].ToString();

    if (!string.IsNullOrEmpty(apiKey)
        && context.RequestServices.GetRequiredService<ApiKeyRegistry>().IsValid(apiKey))
    {
        return $"key:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))}";
    }

    return $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(gen =>
{
    gen.AddSchemaFilterInstance(new BigIntegerSchemaFilter());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

{
    // Through the factory, which is what is actually registered; resolving DatabaseContext from a
    // scope only works by way of an implicit registration.
    await using var context = await app.Services
        .GetRequiredService<IDbContextFactory<DatabaseContext>>()
        .CreateDbContextAsync();

    await context.Database.MigrateAsync();
}

// Surface an unusable API key configuration before anything else, so that the most
// security relevant misconfiguration is also the first error an operator sees.
_ = app.Services.GetRequiredService<IOptions<ApiKeyConfig>>().Value;

// Build every enabled currency's service up front: a key that cannot be parsed is a
// misconfiguration, and finding out at startup beats answering every payment request with a 500.
{
    var configManager = app.Services.GetRequiredService<ConfigManager>();
    var cryptoServiceFactory = app.Services.GetRequiredService<ICryptoServiceFactory>();

    foreach (var currency in configManager.GetUsableCurrencies())
    {
        try
        {
            if (cryptoServiceFactory.GetService(currency) is null)
            {
                throw new InvalidOperationException(
                    $"No crypto service could be created for enabled currency '{currency.Symbol}' on network '{currency.Network}'.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The configuration of '{currency.Symbol}' on network '{currency.Network}' is unusable (check ExtPubKey).", ex);
        }
    }
}

app.UseExceptionHandler();

// Behind a TLS-terminating proxy the request arrives over plain HTTP, so without this the HTTPS
// checks below either loop or pass everything through, and the rate limiter partitions every caller
// behind the proxy into one bucket.
//
// The proxy has to be named. The defaults trust loopback only, so on any other address the headers
// are silently dropped and the single-bucket problem stays; trusting every address instead would
// make X-Forwarded-For caller-controlled, and with it the rate limiter's IP partition.
{
    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    };

    var knownProxies = app.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
    var knownNetworks = app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];

    foreach (var proxy in knownProxies)
    {
        if (!IPAddress.TryParse(proxy, out var address))
        {
            throw new InvalidOperationException(
                $"ForwardedHeaders:KnownProxies contains '{proxy}', which is not an IP address.");
        }

        forwardedHeadersOptions.KnownProxies.Add(address);
    }

    foreach (var network in knownNetworks)
    {
        var parts = network.Split('/', 2);

        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var prefix)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var prefixLength))
        {
            throw new InvalidOperationException(
                $"ForwardedHeaders:KnownNetworks contains '{network}', which is not a CIDR range such as '10.0.0.0/8'.");
        }

        forwardedHeadersOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
    }

    // A proxy that forwards more hops than this has its outermost headers ignored, which reads as
    // the wrong client address rather than as an error.
    if (knownProxies.Length + knownNetworks.Length > forwardedHeadersOptions.ForwardLimit)
    {
        forwardedHeadersOptions.ForwardLimit = knownProxies.Length + knownNetworks.Length;
    }

    if (knownProxies.Length == 0 && knownNetworks.Length == 0 && !app.Environment.IsDevelopment())
    {
        app.Logger.LogWarning(
            "No ForwardedHeaders:KnownProxies or :KnownNetworks are configured, so only a loopback proxy is trusted. " +
            "If this instance sits behind a reverse proxy or container ingress on any other address, its X-Forwarded-For and " +
            "X-Forwarded-Proto headers are being ignored: every caller behind the proxy shares one rate-limit partition and " +
            "HTTPS enforcement sees a plaintext request.");
    }

    app.UseForwardedHeaders(forwardedHeadersOptions);
}

// Redirect (and pin) to HTTPS before anything reads the API key off the wire, so a credential is
// never processed from a plaintext request.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// UseHttpsRedirection silently does nothing when it cannot work out a port to redirect to, which
// means the API key would be read off a plaintext connection with only a warning in the log. If no
// port is configured, say so where an operator will see it.
if (app.Configuration["HttpsRedirectionPort"] is not null
    || app.Configuration["ASPNETCORE_HTTPS_PORTS"] is not null
    || app.Configuration["ASPNETCORE_HTTPS_PORT"] is not null)
{
    app.UseHttpsRedirection();
}
else if (!app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "No HTTPS port is configured, so HTTPS redirection is inactive and API keys may be accepted over plaintext HTTP. " +
        "Set HttpsRedirectionPort (or ASPNETCORE_HTTPS_PORTS), or terminate TLS at a proxy and enforce it there.");
}

// Routing must run before the API key middleware, which decides whether a key is required by
// inspecting the resolved endpoint's metadata.
app.UseRouting();

// Before the API key check on purpose: the global limiter has to cover the requests that fail
// authentication, which are exactly the ones a brute force consists of.
app.UseRateLimiter();

app.UseMiddleware<ApiKeyMiddleware>();

app.MapControllers();

await app.RunAsync();
