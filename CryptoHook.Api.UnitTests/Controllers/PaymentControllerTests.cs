using System.Numerics;
using CryptoHook.Api.Controllers;
using CryptoHook.Api.Data;
using CryptoHook.Api.Models.Configs;
using CryptoHook.Api.Models.Enums;
using CryptoHook.Api.Models.Payments;
using CryptoHook.Api.Services.CryptoServices;
using CryptoHook.Api.Services.CryptoServices.DataProvider;
using CryptoHook.Api.Services.CryptoServices.Factory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CryptoHook.Api.UnitTests.Controllers;

/// <summary>
/// These tests run against a real SQLite database on purpose. The in-memory provider
/// evaluates queries in memory and enforces no unique indexes, so it would hide both the
/// untranslatable-query bug and the derivation index race this suite covers.
/// </summary>
public sealed class PaymentControllerTests : IDisposable
{
    private const string TestExtPubKey =
        "xpub6Cr3shfQvx8bZBrj8g8dsaZyYxLbFB75epS8s2wgmw9561qxsj2bRQ8BhiwLQdZB2gVzhWzCyPjNLn7zbg6nt828ooSmDsgs9aU9BSwQYbk";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"cryptohook-tests-{Guid.NewGuid():N}.db");
    private readonly TestDbContextFactory _dbContextFactory;
    private readonly Mock<ICryptoServiceFactory> _mockCryptoServiceFactory = new();
    private readonly CurrencyConfig _currencyConfig;
    private readonly BitcoinService _bitcoinService;
    private readonly PaymentController _controller;

    public PaymentControllerTests()
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);

        using (var context = _dbContextFactory.CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _currencyConfig = new CurrencyConfig
        {
            Name = "Bitcoin",
            Symbol = "BTC",
            IsEnabled = true,
            InitialPaymentTimeout = 30.0,
            ExtPubKey = TestExtPubKey,
            Network = "Main",
            MaxPendingRequests = 3,
            Confirmations = [new() { Amount = 0, ConfirmationsNeeded = 2 }]
        };

        _bitcoinService = new BitcoinService(
            _currencyConfig,
            NullLogger<BitcoinService>.Instance,
            Mock.Of<ICryptoDataProvider>(),
            TestDefaults.PaymentCheckConfig());

        _mockCryptoServiceFactory.Setup(f => f.GetService("BTC", "Main")).Returns(_bitcoinService);

        _controller = new PaymentController(
            NullLogger<PaymentController>.Instance,
            _dbContextFactory,
            _mockCryptoServiceFactory.Object);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        GC.SuppressFinalize(this);
    }

    private sealed class TestDbContextFactory(DbContextOptions<DatabaseContext> options) : IDbContextFactory<DatabaseContext>
    {
        public DatabaseContext CreateDbContext() => new(options);
    }

    private static T AssertResult<T>(ActionResult<PaymentRequest> result) where T : class
    {
        var objectResult = Assert.IsAssignableFrom<T>(result.Result);
        return objectResult;
    }

    private static int StatusCodeOf(ActionResult<PaymentRequest> result)
    {
        return result.Result switch
        {
            ObjectResult objectResult => objectResult.StatusCode ?? 0,
            StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
            _ => 0
        };
    }

    private static PaymentRequest PaymentOf(ActionResult<PaymentRequest> result)
    {
        var okResult = AssertResult<OkObjectResult>(result);
        return Assert.IsType<PaymentRequest>(okResult.Value);
    }

    [Fact]
    public async Task CreatePaymentRequest_OnRealDatabase_Succeeds()
    {
        // The highest-derivation-index lookup used string.Equals with a StringComparison,
        // which SQLite cannot translate; every creation failed with a 500.
        var result = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        var payment = PaymentOf(result);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(0ul, payment.DerivationIndex);
        Assert.Equal(_bitcoinService.GetAddressAtIndex(0), payment.ReceivingAddress);
        Assert.Equal(new BigInteger(100000), payment.AmountExpected);
        Assert.Equal((uint)2, payment.ConfirmationNeeded);
        Assert.NotEqual(Guid.Empty, payment.Id);

        await using var context = _dbContextFactory.CreateDbContext();
        var stored = await context.PaymentRequests.SingleAsync();
        Assert.Equal(payment.ReceivingAddress, stored.ReceivingAddress);
        Assert.NotEmpty(stored.ReceivingAddress);
    }

    [Fact]
    public async Task CreatePaymentRequest_SecondRequest_UsesNextDerivationIndex()
    {
        await _controller.CreatePaymentRequest("BTC", "Main", 100000);
        var second = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        var payment = PaymentOf(second);
        Assert.Equal(1ul, payment.DerivationIndex);
        Assert.Equal(_bitcoinService.GetAddressAtIndex(1), payment.ReceivingAddress);
    }

    [Fact]
    public async Task CreatePaymentRequest_WhenIndexWasTakenConcurrently_RetriesWithTheNextFreeIndex()
    {
        // Simulates the losing side of the race: the index the controller is about to use is
        // already taken, so the insert fails on the unique index and has to be retried.
        await using (var context = _dbContextFactory.CreateDbContext())
        {
            context.PaymentRequests.Add(CreateStoredPayment(derivationIndex: 0, status: PaymentStatus.Confirmed));
            await context.SaveChangesAsync();
        }

        var result = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        var payment = PaymentOf(result);
        Assert.Equal(1ul, payment.DerivationIndex);
    }

    [Fact]
    public async Task CreatePaymentRequest_Concurrently_NeverHandsOutOneAddressTwice()
    {
        _currencyConfig.MaxPendingRequests = 1000;

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => _controller.CreatePaymentRequest("BTC", "Main", 100000))));

        var created = results.Where(r => r.Result is OkObjectResult).Select(PaymentOf).ToList();
        Assert.NotEmpty(created);

        await using var context = _dbContextFactory.CreateDbContext();
        var stored = await context.PaymentRequests.ToListAsync();

        Assert.Equal(created.Count, stored.Count);
        Assert.Equal(stored.Count, stored.Select(p => p.DerivationIndex).Distinct().Count());
        Assert.Equal(stored.Count, stored.Select(p => p.ReceivingAddress).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task CreatePaymentRequest_UnsupportedCurrency_ReturnsBadRequest()
    {
        // An unknown currency is a caller error, so it must not surface as a 500.
        _mockCryptoServiceFactory.Setup(f => f.GetService("DOGE", "Main")).Returns((ICryptoService?)null);

        var result = await _controller.CreatePaymentRequest("DOGE", "Main", 100000);

        AssertResult<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatePaymentRequest_MissingSymbol_ReturnsBadRequest(string symbol)
    {
        var result = await _controller.CreatePaymentRequest(symbol, "Main", 100000);

        AssertResult<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatePaymentRequest_MissingNetwork_ReturnsBadRequest(string network)
    {
        var result = await _controller.CreatePaymentRequest("BTC", network, 100000);

        AssertResult<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreatePaymentRequest_NonPositiveAmount_ReturnsBadRequest(int amount)
    {
        var result = await _controller.CreatePaymentRequest("BTC", "Main", amount);

        AssertResult<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CreatePaymentRequest_TooManyPendingRequests_ReturnsTooManyRequests()
    {
        // Each request permanently consumes a derivation index, so unbounded creation would
        // push the watching wallet past its address gap limit.
        for (var i = 0; i < _currencyConfig.MaxPendingRequests; i++)
        {
            var accepted = await _controller.CreatePaymentRequest("BTC", "Main", 100000);
            Assert.IsType<OkObjectResult>(accepted.Result);
        }

        var result = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        Assert.Equal(StatusCodes.Status429TooManyRequests, StatusCodeOf(result));
    }

    [Fact]
    public async Task CreatePaymentRequest_PendingCapIgnoresSettledRequests()
    {
        await using (var context = _dbContextFactory.CreateDbContext())
        {
            for (var i = 0; i < _currencyConfig.MaxPendingRequests + 5; i++)
            {
                context.PaymentRequests.Add(CreateStoredPayment((ulong)i, PaymentStatus.Confirmed));
            }
            await context.SaveChangesAsync();
        }

        var result = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreatePaymentRequest_DerivationIndexIsScopedToSymbolAndNetwork()
    {
        await using (var context = _dbContextFactory.CreateDbContext())
        {
            var otherNetwork = CreateStoredPayment(derivationIndex: 41, status: PaymentStatus.Confirmed);
            otherNetwork.Network = "Sepolia";
            otherNetwork.CurrencySymbol = "ETH";
            context.PaymentRequests.Add(otherNetwork);
            await context.SaveChangesAsync();
        }

        var result = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        Assert.Equal(0ul, PaymentOf(result).DerivationIndex);
    }

    [Fact]
    public async Task GetPaymentRequest_ExistingId_ReturnsPayment()
    {
        var created = PaymentOf(await _controller.CreatePaymentRequest("BTC", "Main", 100000));

        var result = await _controller.GetPaymentRequest(created.Id);

        Assert.Equal(created.Id, PaymentOf(result).Id);
    }

    [Fact]
    public async Task GetPaymentRequest_UnknownId_ReturnsNotFound()
    {
        var result = await _controller.GetPaymentRequest(Guid.NewGuid());

        AssertResult<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// Seeds <paramref name="count"/> spent derivation indices that never received funds, which
    /// is what a run of expired or abandoned requests leaves behind.
    /// </summary>
    private async Task SeedUnfundedIndices(int count)
    {
        using var context = _dbContextFactory.CreateDbContext();

        for (var index = 0; index < count; index++)
        {
            context.PaymentRequests.Add(CreateStoredPayment((ulong)index, PaymentStatus.Expired));
        }

        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task CreatePaymentRequest_TooManyConsecutiveUnfundedIndices_IsRefused()
    {
        // A watch-only wallet stops scanning after a run of addresses with no history, so once
        // that run reaches its gap limit the merchant's wallet no longer discovers payments made
        // to addresses this service is still handing out. The old guard counted concurrently
        // pending requests, which says nothing about the run: indices are consumed monotonically
        // and never reused, so every expired request lengthens it permanently.
        _currencyConfig.AddressGapLimit = 5;
        _currencyConfig.MaxPendingRequests = 1000;
        await SeedUnfundedIndices(5);

        var result = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, StatusCodeOf(result));
    }

    [Fact]
    public async Task CreatePaymentRequest_UnfundedRunBelowTheGapLimit_IsAllowed()
    {
        _currencyConfig.AddressGapLimit = 5;
        _currencyConfig.MaxPendingRequests = 1000;
        await SeedUnfundedIndices(4);

        var result = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        Assert.Equal(4ul, PaymentOf(result).DerivationIndex);
    }

    [Fact]
    public async Task CreatePaymentRequest_APaidIndexResetsTheRun()
    {
        // The run is measured from the most recent index that actually received funds, so a
        // payment landing on a recent address clears the condition the way a wallet rescan would.
        _currencyConfig.AddressGapLimit = 5;
        _currencyConfig.MaxPendingRequests = 1000;
        await SeedUnfundedIndices(10);

        using (var context = _dbContextFactory.CreateDbContext())
        {
            var recent = await context.PaymentRequests.SingleAsync(p => p.DerivationIndex == 9);
            recent.AmountPaid = 100000;
            recent.Status = PaymentStatus.Confirmed;
            await context.SaveChangesAsync();
        }

        var result = await _controller.CreatePaymentRequest("BTC", "Main", 100000);

        Assert.Equal(10ul, PaymentOf(result).DerivationIndex);
    }

    [Fact]
    public async Task CreatePaymentRequest_GapLimitIsPerCurrencyAndNetwork()
    {
        // Each currency derives from its own key, so one currency's exhausted run must not block
        // another's.
        _currencyConfig.AddressGapLimit = 2;
        _currencyConfig.MaxPendingRequests = 1000;

        var ethereumConfig = new CurrencyConfig
        {
            Name = "Ethereum",
            Symbol = "ETH",
            IsEnabled = true,
            InitialPaymentTimeout = 30.0,
            ExtPubKey = TestExtPubKey,
            Network = "Main",
            AddressGapLimit = 2,
            MaxPendingRequests = 1000,
            Confirmations = [new() { Amount = 0, ConfirmationsNeeded = 2 }]
        };

        var ethereumService = new EthereumService(
            ethereumConfig,
            NullLogger<EthereumService>.Instance,
            Mock.Of<ICryptoDataProvider>(),
            TestDefaults.PaymentCheckConfig());

        _mockCryptoServiceFactory.Setup(f => f.GetService("ETH", "Main")).Returns(ethereumService);

        await SeedUnfundedIndices(2);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable,
            StatusCodeOf(await _controller.CreatePaymentRequest("BTC", "Main", 100000)));

        var ethereumResult = await _controller.CreatePaymentRequest("ETH", "Main", 100000);
        Assert.Equal(0ul, PaymentOf(ethereumResult).DerivationIndex);
    }

    private PaymentRequest CreateStoredPayment(ulong derivationIndex, PaymentStatus status)
    {
        return new PaymentRequest
        {
            Id = Guid.NewGuid(),
            DerivationIndex = derivationIndex,
            Status = status,
            AmountExpected = 100000,
            AmountPaid = BigInteger.Zero,
            ConfirmationCount = 0,
            ConfirmationNeeded = 2,
            ReceivingAddress = _bitcoinService.GetAddressAtIndex((uint)derivationIndex),
            CurrencySymbol = "BTC",
            Network = "Main",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        };
    }
}
