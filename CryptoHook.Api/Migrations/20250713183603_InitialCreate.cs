using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoHook.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DerivationIndex = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedAmount = table.Column<string>(type: "TEXT", nullable: false),
                    AmountPaid = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivingAddress = table.Column<string>(type: "TEXT", nullable: false),
                    CurrencySymbol = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TransactionId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_CurrencySymbol",
                table: "PaymentRequests",
                column: "CurrencySymbol");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_Id",
                table: "PaymentRequests",
                column: "Id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentRequests");
        }
    }
}
