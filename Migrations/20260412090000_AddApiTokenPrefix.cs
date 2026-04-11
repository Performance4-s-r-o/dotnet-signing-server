using DotNetSigningServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260412090000_AddApiTokenPrefix")]
    public partial class AddApiTokenPrefix : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TokenPrefix",
                table: "ApiTokens",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentOperations",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenPrefix",
                table: "ApiTokens",
                column: "TokenPrefix");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApiTokens_TokenPrefix",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "TokenPrefix",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentOperations",
                table: "Users");
        }
    }
}
