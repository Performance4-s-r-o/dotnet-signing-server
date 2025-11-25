using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SigningData",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PresignedPdfPath = table.Column<string>(type: "text", nullable: false),
                    HashToSign = table.Column<string>(type: "text", nullable: false),
                    CertificatePem = table.Column<string>(type: "text", nullable: false),
                    FieldName = table.Column<string>(type: "text", nullable: false, defaultValue: "Signature1"),
                    TsaUrl = table.Column<string>(type: "text", nullable: true),
                    TsaUsername = table.Column<string>(type: "text", nullable: true),
                    TsaPassword = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningData", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SigningData");
        }
    }
}
