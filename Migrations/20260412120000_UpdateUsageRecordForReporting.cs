using DotNetSigningServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260412120000_UpdateUsageRecordForReporting")]
    public partial class UpdateUsageRecordForReporting : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Make DocumentId nullable (many operations don't create a Document)
            migrationBuilder.AlterColumn<System.Guid>(
                name: "DocumentId",
                table: "UsageRecords",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(System.Guid),
                oldType: "uuid");

            // Add reporting columns
            migrationBuilder.AddColumn<int>(
                name: "BaseCost",
                table: "UsageRecords",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Tier",
                table: "UsageRecords",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                table: "UsageRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BaseCost", table: "UsageRecords");
            migrationBuilder.DropColumn(name: "Tier", table: "UsageRecords");
            migrationBuilder.DropColumn(name: "Operation", table: "UsageRecords");

            migrationBuilder.AlterColumn<System.Guid>(
                name: "DocumentId",
                table: "UsageRecords",
                type: "uuid",
                nullable: false,
                defaultValue: System.Guid.Empty,
                oldClrType: typeof(System.Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
