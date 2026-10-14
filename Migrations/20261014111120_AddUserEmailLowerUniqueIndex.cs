using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <summary>
    /// One account per e-mail address whatever the letter case (#82). The existing unique index
    /// on "Email" compares case-sensitively, so <c>Foo@x</c> and <c>foo@x</c> could be two
    /// accounts. Lookups now compare <c>lower("Email")</c> (<c>UserEmails</c>) and this index
    /// backs them. Existing rows are not rewritten.
    ///
    /// Addresses already differing only by case would make the index fail; the check before it
    /// stops the migration with a clear message instead, and nothing is merged or renamed
    /// automatically — which of two accounts keeps the address is a person's decision.
    /// PostgreSQL only (expression index); the model is unchanged.
    /// </summary>
    public partial class AddUserEmailLowerUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.IsNpgsql()) return;

            migrationBuilder.Sql("""
                DO $$
                DECLARE collisions integer;
                BEGIN
                    SELECT count(*) INTO collisions
                    FROM (SELECT lower("Email") FROM dotnet_signing."Users"
                          GROUP BY lower("Email") HAVING count(*) > 1) AS d;
                    IF collisions > 0 THEN
                        RAISE EXCEPTION 'Users: % e-mail address(es) exist in several accounts differing only by letter case; resolve them before this migration (SELECT lower("Email"), array_agg("Id") FROM dotnet_signing."Users" GROUP BY 1 HAVING count(*) > 1)', collisions
                            USING ERRCODE = 'unique_violation';
                    END IF;
                END
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_Users_Email_Lower" ON dotnet_signing."Users" (lower("Email"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.IsNpgsql()) return;

            migrationBuilder.Sql("""DROP INDEX IF EXISTS dotnet_signing."IX_Users_Email_Lower";""");
        }
    }
}
