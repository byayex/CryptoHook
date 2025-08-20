using CryptoHook.Api.Models.Payments;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Numerics;

namespace CryptoHook.Api.Data;

public class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options)
{
    public DbSet<PaymentRequest> PaymentRequests { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PaymentRequest>(entity =>
        {
            entity.HasIndex(e => e.Id)
                .IsUnique();

            entity.Property(e => e.Id)
                .HasConversion(
                    v => v.ToString(),
                    v => Guid.Parse(v));

            entity.HasIndex(e => e.CurrencySymbol);

            entity.Property(e => e.AmountExpected)
                .HasConversion(
                    v => v.ToString(CultureInfo.InvariantCulture),
                    v => BigInteger.Parse(v, CultureInfo.InvariantCulture)
                );

            entity.Property(e => e.AmountPaid)
                .HasConversion(
                    v => v.ToString(CultureInfo.InvariantCulture),
                    v => BigInteger.Parse(v, CultureInfo.InvariantCulture)
                );
        });
    }
}