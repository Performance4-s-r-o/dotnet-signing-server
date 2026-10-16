using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class PortalTenantAndRequestId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                schema: "dotnet_signing",
                table: "SigningData",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "SigningData",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RequestId",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "RequestId",
                schema: "dotnet_signing",
                table: "SigningData");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "SigningData");
        }
    }
}
