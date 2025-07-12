using CryptoHook.Api.Models.Payments;
using Microsoft.EntityFrameworkCore;
using System.Numerics;

namespace CryptoHook.Api.Manager;

public class DatabaseContext(DbContextOptions<DatabaseContext> options) : DbContext(options)
{
    public DbSet<PaymentRequest> PaymentRequests { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PaymentRequest>(entity =>
        {
            entity.Property(e => e.ExpectedAmount)
                .HasConversion(
                    v => v.ToString(),
                    v => BigInteger.Parse(v)
                );

            entity.Property(e => e.AmountPaid)
                .HasConversion(
                    v => v.ToString(),
                    v => BigInteger.Parse(v)
                );
        });
    }
}