using System.Numerics;
using CryptoHook.Api.Manager;
using CryptoHook.Api.Manager.CryptoManager;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Payments;
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

builder.Services.AddSingleton<ConfigManager>();

builder.Services.AddKeyedSingleton<ICryptoManager, BitcoinManager>("BTC");

builder.Services.AddDbContext<DatabaseContext>(options =>
    options.UseSqlite("Data Source=CryptoHook.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
    dbContext.Database.Migrate();
    dbContext.PaymentRequests.Add(new PaymentRequest
    {
        ExpectedAmount = BigInteger.Parse("100000000"), // 1 BTC in satoshis
        AmountPaid = BigInteger.Zero,
        ReceivingAddress = "1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(30),
        Status = CryptoHook.Api.Models.Enums.PaymentStatusEnum.Pending,
        TransactionId = null
    });
    dbContext.PaymentRequests.Add(new PaymentRequest
    {
        ExpectedAmount = BigInteger.Parse("500000000000000000000000000000000000000000000000000000000000000000"), // 1 BTC in satoshis
        AmountPaid = BigInteger.Zero,
        ReceivingAddress = "1A1zPdsfdsfsdfsdfsdfLmv7DivfNa",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddDays(30),
        Status = CryptoHook.Api.Models.Enums.PaymentStatusEnum.Pending,
        TransactionId = null
    });
    dbContext.SaveChanges();
}

app.UseHttpsRedirection();
app.MapControllers(); ;

app.Run();
