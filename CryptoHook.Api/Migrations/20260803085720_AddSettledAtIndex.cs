using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CryptoHook.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSettledAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_Status_SettledAt",
                table: "PaymentRequests",
                columns: new[] { "Status", "SettledAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentRequests_Status_SettledAt",
                table: "PaymentRequests");
        }
    }
}
