using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class SessionSecurityAndAdminAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailOtpFailedAttempts",
                schema: "dotnet_signing",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LoginChallengeHash",
                schema: "dotnet_signing",
                table: "Users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreditLimit",
                schema: "dotnet_signing",
                table: "ApiTokens",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreditsUsed",
                schema: "dotnet_signing",
                table: "ApiTokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AdminAuditEntries",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Change = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuthAttemptCounters",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthAttemptCounters", x => x.Key);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_LoginChallengeHash",
                schema: "dotnet_signing",
                table: "Users",
                column: "LoginChallengeHash",
                filter: "\"LoginChallengeHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEntries_TargetUserId_CreatedAt",
                schema: "dotnet_signing",
                table: "AdminAuditEntries",
                columns: new[] { "TargetUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthAttemptCounters_WindowStart",
                schema: "dotnet_signing",
                table: "AuthAttemptCounters",
                column: "WindowStart");

            // Append-only in the database too (#80): the context refuses it, this refuses it for
            // any other client of the same role. Dropping the trigger needs DDL rights, which the
            // runtime role does not have when migrations run as a separate role (#85).
            //
            // Not a guarantee where one role does both — on-prem with RUN_MIGRATIONS_ON_START=true,
            // or an empty MIGRATION_DB_USER: that role owns the table and can drop or disable the
            // trigger. Also: ActorEmail is personal data that this table, by design, never deletes;
            // an erasure request has to be answered with that in mind (legitimate interest of an
            // audit trail, or a manual pseudonymisation with the trigger switched off by the owner).
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION dotnet_signing.admin_audit_append_only() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'AdminAuditEntries is append-only';
END;
$$;

CREATE TRIGGER ""AdminAuditEntries_append_only""
    BEFORE UPDATE OR DELETE ON dotnet_signing.""AdminAuditEntries""
    FOR EACH ROW EXECUTE FUNCTION dotnet_signing.admin_audit_append_only();

CREATE TRIGGER ""AdminAuditEntries_no_truncate""
    BEFORE TRUNCATE ON dotnet_signing.""AdminAuditEntries""
    FOR EACH STATEMENT EXECUTE FUNCTION dotnet_signing.admin_audit_append_only();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS ""AdminAuditEntries_append_only"" ON dotnet_signing.""AdminAuditEntries"";
DROP TRIGGER IF EXISTS ""AdminAuditEntries_no_truncate"" ON dotnet_signing.""AdminAuditEntries"";
DROP FUNCTION IF EXISTS dotnet_signing.admin_audit_append_only();");

            migrationBuilder.DropTable(
                name: "AdminAuditEntries",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "AuthAttemptCounters",
                schema: "dotnet_signing");

            migrationBuilder.DropIndex(
                name: "IX_Users_LoginChallengeHash",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EmailOtpFailedAttempts",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LoginChallengeHash",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CreditLimit",
                schema: "dotnet_signing",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "CreditsUsed",
                schema: "dotnet_signing",
                table: "ApiTokens");
        }
    }
}
