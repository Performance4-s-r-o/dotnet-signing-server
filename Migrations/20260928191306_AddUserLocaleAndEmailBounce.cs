using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLocaleAndEmailBounce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailBouncedAt",
                schema: "dotnet_signing",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Locale",
                schema: "dotnet_signing",
                table: "Users",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailBouncedAt",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Locale",
                schema: "dotnet_signing",
                table: "Users");
        }
    }
}
