using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoHook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueDerivationIndexAndStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentRequests_Id",
                table: "PaymentRequests");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_CurrencySymbol_Network_DerivationIndex",
                table: "PaymentRequests",
                columns: new[] { "CurrencySymbol", "Network", "DerivationIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_Status",
                table: "PaymentRequests",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentRequests_CurrencySymbol_Network_DerivationIndex",
                table: "PaymentRequests");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRequests_Status",
                table: "PaymentRequests");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_Id",
                table: "PaymentRequests",
                column: "Id",
                unique: true);
        }
    }
}
