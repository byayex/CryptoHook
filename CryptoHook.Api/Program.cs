using CryptoHook.Api.Data;
using CryptoHook.Api.Managers;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Converters;
using CryptoHook.Api.Services;
using CryptoHook.Api.Services.CryptoServices.Factory;
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
    .ValidateOnStart()
    .ValidateDataAnnotations();

builder.Services.AddOptions<WebhookConfigList>()
    .Bind(builder.Configuration.GetSection("Webhooks"))
    .ValidateOnStart()
    .ValidateDataAnnotations();

builder.Services.AddSingleton<ConfigManager>();

builder.Services.AddSingleton<IWebhookService, WebhookService>();

builder.Services.AddSingleton<ICryptoServiceFactory, CryptoServiceFactory>();

builder.Services.AddHostedService<PaymentCheckWorker>();

builder.Services.AddDbContextFactory<DatabaseContext>(options =>
    options.UseSqlite("Data Source=CryptoHook.db"));

builder.Services.AddHttpClient();

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

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
    context.Database.Migrate();
}

app.UseHttpsRedirection();
app.MapControllers(); ;

app.Run();
