using CryptoHook.Api.Data;
using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Services;
using CryptoHook.Api.Services.CryptoServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
});

builder.Services.AddOptions<CurrencyConfigList>()
    .Configure(options =>
    {
        builder.Configuration.GetSection("CurrencyConfigs").Bind(options, binderOptions =>
        {
            binderOptions.BindNonPublicProperties = true;
        });
    })
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<WebhookConfigList>()
    .Bind(builder.Configuration.GetSection("Webhooks"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<ConfigManager>();

builder.Services.AddKeyedSingleton<ICryptoService, BitcoinService>("BTC");

builder.Services.AddScoped<IWebhookService, WebhookService>();

builder.Services.AddHostedService<PaymentCheckWorker>();

builder.Services.AddDbContextFactory<DatabaseContext>(options =>
    options.UseSqlite("Data Source=CryptoHook.db"));

builder.Services.AddHttpClient();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
    context.Database.Migrate();
}

app.UseHttpsRedirection();
app.MapControllers(); ;

app.Run();
