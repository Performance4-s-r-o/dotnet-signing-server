using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <summary>
    /// One user per Stripe customer (#82). Linking a customer checked "nobody else holds it" and
    /// then wrote it — two steps; this index decides a race (StripeCustomerBinding treats losing
    /// it as a refused link). Blank values are not a customer and are left out.
    ///
    /// A customer already linked to several users would make the index fail; the check before it
    /// stops the migration with a clear message instead. Nothing is unlinked automatically:
    /// which user the customer (its invoices, its saved card) belongs to is a person's decision.
    /// </summary>
    public partial class AddUserStripeCustomerUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.IsNpgsql())
            {
                migrationBuilder.Sql("""
                    DO $$
                    DECLARE collisions integer;
                    BEGIN
                        SELECT count(*) INTO collisions
                        FROM (SELECT "StripeCustomerId" FROM dotnet_signing."Users"
                              WHERE "StripeCustomerId" IS NOT NULL AND btrim("StripeCustomerId") <> ''
                              GROUP BY "StripeCustomerId" HAVING count(*) > 1) AS d;
                        IF collisions > 0 THEN
                            RAISE EXCEPTION 'Users: % Stripe customer(s) are linked to several users; resolve them before this migration (SELECT "StripeCustomerId", array_agg("Id") FROM dotnet_signing."Users" WHERE "StripeCustomerId" IS NOT NULL AND btrim("StripeCustomerId") <> '''' GROUP BY 1 HAVING count(*) > 1)', collisions
                                USING ERRCODE = 'unique_violation';
                        END IF;
                    END
                    $$;
                    """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_Users_StripeCustomerId",
                schema: "dotnet_signing",
                table: "Users",
                column: "StripeCustomerId",
                unique: true,
                filter: "\"StripeCustomerId\" IS NOT NULL AND btrim(\"StripeCustomerId\") <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_StripeCustomerId",
                schema: "dotnet_signing",
                table: "Users");
        }
    }
}
