using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalDocumentDraftFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegalDocuments_Slug_Locale_EffectiveFrom",
                schema: "dotnet_signing",
                table: "LegalDocuments");

            migrationBuilder.AddColumn<bool>(
                name: "IsDraft",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocuments_Slug_Locale_IsDraft_EffectiveFrom",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                columns: new[] { "Slug", "Locale", "IsDraft", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LegalDocuments_Slug_Locale_IsDraft_EffectiveFrom",
                schema: "dotnet_signing",
                table: "LegalDocuments");

            migrationBuilder.DropColumn(
                name: "IsDraft",
                schema: "dotnet_signing",
                table: "LegalDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocuments_Slug_Locale_EffectiveFrom",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                columns: new[] { "Slug", "Locale", "EffectiveFrom" });
        }
    }
}
