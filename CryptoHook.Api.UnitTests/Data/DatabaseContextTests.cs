using System.Numerics;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CryptoHook.Api.UnitTests.Data;

/// <summary>
/// Schema level guarantees, asserted against real SQLite because the in-memory provider
/// does not enforce indexes.
/// </summary>
public sealed class DatabaseContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DatabaseContext> _options;

    public DatabaseContextTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new DatabaseContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static PaymentRequest CreatePayment(string symbol, string network, ulong derivationIndex)
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            DerivationIndex = derivationIndex,
            Status = PaymentStatus.Pending,
            AmountExpected = 100000,
            AmountPaid = BigInteger.Zero,
            ConfirmationCount = 0,
            ConfirmationNeeded = 2,
            ReceivingAddress = $"address-{symbol}-{network}-{derivationIndex}",
            CurrencySymbol = symbol,
            Network = network,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
    }

    [Fact]
    public async Task SaveChanges_DuplicateDerivationIndexForTheSameCurrencyAndNetwork_IsRejected()
    {
        // Without this constraint two concurrent creations that read the same highest index
        // both succeed, and two payments end up sharing one receiving address.
        await using var context = new DatabaseContext(_options);
        context.PaymentRequests.Add(CreatePayment("BTC", "Main", 0));
        await context.SaveChangesAsync();

        context.PaymentRequests.Add(CreatePayment("BTC", "Main", 0));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChanges_SameDerivationIndexOnAnotherNetwork_IsAllowed()
    {
        await using var context = new DatabaseContext(_options);
        context.PaymentRequests.Add(CreatePayment("ETH", "Main", 0));
        context.PaymentRequests.Add(CreatePayment("ETH", "Sepolia", 0));
        context.PaymentRequests.Add(CreatePayment("BTC", "Main", 0));

        await context.SaveChangesAsync();

        Assert.Equal(3, await context.PaymentRequests.CountAsync());
    }

    [Fact]
    public async Task SaveChanges_BigIntegerAmountsSurviveARoundTrip()
    {
        var payment = CreatePayment("BTC", "Main", 0);
        payment.AmountExpected = BigInteger.Pow(10, 30);
        payment.AmountPaid = BigInteger.Pow(10, 29);

        await using (var context = new DatabaseContext(_options))
        {
            context.PaymentRequests.Add(payment);
            await context.SaveChangesAsync();
        }

        await using var readContext = new DatabaseContext(_options);
        var stored = await readContext.PaymentRequests.SingleAsync();
        Assert.Equal(BigInteger.Pow(10, 30), stored.AmountExpected);
        Assert.Equal(BigInteger.Pow(10, 29), stored.AmountPaid);
    }
}
