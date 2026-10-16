using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <summary>
    /// #79: permanent credit grants (unique grant key and payment intent), dead letters in the
    /// backoffice inbox, and the first attempt of an outbox item's retry round.
    /// </summary>
    public partial class BackofficeReliability79 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreditGrants",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PaymentIntentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Credits = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditGrants_GrantKey",
                schema: "dotnet_signing",
                table: "CreditGrants",
                column: "GrantKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditGrants_PaymentIntentId",
                schema: "dotnet_signing",
                table: "CreditGrants",
                column: "PaymentIntentId",
                unique: true,
                filter: "\"PaymentIntentId\" IS NOT NULL");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadAt",
                schema: "dotnet_signing",
                table: "BackofficeWebhookInboxItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstAttemptAt",
                schema: "dotnet_signing",
                table: "BackofficeOutboxItems",
                type: "timestamp with time zone",
                nullable: true);

            // Grants made before this table keep their key: the WebhookEvents markers of a
            // granted checkout session or auto-recharge. User and credits are read from the
            // marker's payload when it has them; the key alone is what prevents a second grant.
            migrationBuilder.Sql("""
                INSERT INTO dotnet_signing."CreditGrants" ("Id", "GrantKey", "PaymentIntentId", "UserId", "Credits", "Source", "GrantedAt")
                SELECT gen_random_uuid(),
                       w."EventId",
                       NULL,
                       substring(w."PayloadJson" from '"userId":"([0-9a-fA-F-]{36})"')::uuid,
                       COALESCE(substring(w."PayloadJson" from '"documents":([0-9]{1,9})')::integer, 0),
                       'legacy',
                       COALESCE(w."ProcessedAt", w."ReceivedAt")
                FROM dotnet_signing."WebhookEvents" w
                WHERE w."EventType" IN ('checkout.confirm', 'auto_recharge')
                ON CONFLICT DO NOTHING;
                """);

            // Events given up before #79 (out of attempts, never processed) become dead letters
            // that can be replayed, instead of rows nothing looks at.
            migrationBuilder.Sql("""
                UPDATE dotnet_signing."BackofficeWebhookInboxItems"
                SET "DeadAt" = "ReceivedAt"
                WHERE "ProcessedAt" IS NULL AND "NextAttemptAt" IS NULL;
                """);

            // Items already retrying started their round no later than they were created.
            migrationBuilder.Sql("""
                UPDATE dotnet_signing."BackofficeOutboxItems"
                SET "FirstAttemptAt" = "CreatedAt"
                WHERE "Attempts" > 0 AND "Status" = 'Pending';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditGrants",
                schema: "dotnet_signing");

            migrationBuilder.DropColumn(
                name: "DeadAt",
                schema: "dotnet_signing",
                table: "BackofficeWebhookInboxItems");

            migrationBuilder.DropColumn(
                name: "FirstAttemptAt",
                schema: "dotnet_signing",
                table: "BackofficeOutboxItems");
        }
    }
}
