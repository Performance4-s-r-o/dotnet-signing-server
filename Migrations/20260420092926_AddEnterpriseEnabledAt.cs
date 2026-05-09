using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddEnterpriseEnabledAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EnterpriseEnabledAt",
                schema: "dotnet_signing",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnterpriseEnabledAt",
                schema: "dotnet_signing",
                table: "Users");
        }
    }
}
