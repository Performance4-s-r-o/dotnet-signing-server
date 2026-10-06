using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddSigningDataTsaBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TsaBackupPassword",
                schema: "dotnet_signing",
                table: "SigningData",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TsaBackupUrl",
                schema: "dotnet_signing",
                table: "SigningData",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TsaBackupUsername",
                schema: "dotnet_signing",
                table: "SigningData",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TsaBackupPassword",
                schema: "dotnet_signing",
                table: "SigningData");

            migrationBuilder.DropColumn(
                name: "TsaBackupUrl",
                schema: "dotnet_signing",
                table: "SigningData");

            migrationBuilder.DropColumn(
                name: "TsaBackupUsername",
                schema: "dotnet_signing",
                table: "SigningData");
        }
    }
}
