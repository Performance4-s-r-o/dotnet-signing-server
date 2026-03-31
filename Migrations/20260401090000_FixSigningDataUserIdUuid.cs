using DotNetSigningServer.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260401090000_FixSigningDataUserIdUuid")]
    public partial class FixSigningDataUserIdUuid : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "SigningData"
                ALTER COLUMN "UserId" TYPE uuid
                USING NULLIF(BTRIM("UserId"), '')::uuid;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "SigningData"
                ALTER COLUMN "UserId" TYPE text
                USING "UserId"::text;
                """);
        }
    }
}
