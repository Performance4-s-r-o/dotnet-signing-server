using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageRecordStatusAndError : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HttpStatusCode",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_UserId_Status_CreatedAt",
                schema: "dotnet_signing",
                table: "UsageRecords",
                columns: new[] { "UserId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageRecords_UserId_Status_CreatedAt",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "HttpStatusCode",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                schema: "dotnet_signing",
                table: "UsageRecords");
        }
    }
}
