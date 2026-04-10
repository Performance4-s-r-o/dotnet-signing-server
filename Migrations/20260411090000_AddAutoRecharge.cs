using DotNetSigningServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260411090000_AddAutoRecharge")]
    public partial class AddAutoRecharge : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRechargeEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "AutoRechargeQuantity",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "AutoRechargePricePer100",
                table: "Users",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "AutoRechargeCancelToken",
                table: "Users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PriceChangeNotifiedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AutoRechargeEnabled", table: "Users");
            migrationBuilder.DropColumn(name: "AutoRechargeQuantity", table: "Users");
            migrationBuilder.DropColumn(name: "AutoRechargePricePer100", table: "Users");
            migrationBuilder.DropColumn(name: "AutoRechargeCancelToken", table: "Users");
            migrationBuilder.DropColumn(name: "PriceChangeNotifiedAt", table: "Users");
        }
    }
}
