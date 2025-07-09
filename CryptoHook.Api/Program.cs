using CryptoHook.Api.Manager;
using CryptoHook.Api.Models.Config;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
});

builder.Services.AddOptions<CurrencyConfigList>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddScoped<ConfigManager>();

builder.Services.AddKeyedSingleton<ICryptoManager, BitcoinManager>("BTC");

var app = builder.Build();

app.UseHttpsRedirection();
app.MapControllers(); ;

app.Run();
