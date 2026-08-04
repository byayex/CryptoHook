using CryptoHook.Api.Models.Payments;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Numerics;

namespace CryptoHook.Api.Data;

public class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options)
{
    public DbSet<PaymentRequest> PaymentRequests { get; set; }

    public DbSet<WebhookDelivery> WebhookDeliveries { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PaymentRequest>(entity =>
        {
            entity.Property(e => e.Id)
                .HasConversion(
                    v => v.ToString(),
                    v => Guid.Parse(v));

            entity.HasIndex(e => e.CurrencySymbol);

            // Two concurrent creations can read the same highest derivation index. This
            // constraint makes the loser's insert fail instead of letting two payment
            // requests share one receiving address.
            entity.HasIndex(e => new { e.CurrencySymbol, e.Network, e.DerivationIndex })
                .IsUnique();

            // Supports the worker's "non-terminal payments" scan.
            entity.HasIndex(e => e.Status);

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

            // Finds the requests that are close enough to expiry to still be worth abandoning.
            entity.HasIndex(e => new { e.Status, e.ExpiresAt });

            // Finds settled payments whose reorg watch window is still open. That scan filters on
            // SettledAt, which the index above does not cover.
            entity.HasIndex(e => new { e.Status, e.SettledAt });
        });

        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.Property(e => e.Id)
                .HasConversion(
                    v => v.ToString(),
                    v => Guid.Parse(v));

            entity.Property(e => e.PaymentRequestId)
                .HasConversion(
                    v => v.ToString(),
                    v => Guid.Parse(v));

            entity.HasIndex(e => e.PaymentRequestId);

            // Drives the dispatcher's "what is due" scan, which runs every few seconds.
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
        });
    }
}