using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddConsentRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConsentRecords",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Document = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ContentHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    OutboxItemId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsentRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_OccurredAt",
                schema: "dotnet_signing",
                table: "ConsentRecords",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConsentRecords_UserId_Document_OccurredAt",
                schema: "dotnet_signing",
                table: "ConsentRecords",
                columns: new[] { "UserId", "Document", "OccurredAt" });

            // Append-only: consent records are evidence. A new decision is a new row; no code
            // updates or deletes one, and the database refuses it as well (PostgreSQL only;
            // SQLite and InMemory are used by tests and local tools).
            if (migrationBuilder.IsNpgsql())
            {
                migrationBuilder.Sql("""
                    CREATE FUNCTION dotnet_signing.consent_records_append_only() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                    BEGIN
                        RAISE EXCEPTION 'ConsentRecords is append-only: % is not allowed', TG_OP
                            USING ERRCODE = 'restrict_violation';
                    END;
                    $$;

                    CREATE TRIGGER consent_records_append_only
                        BEFORE UPDATE OR DELETE ON dotnet_signing."ConsentRecords"
                        FOR EACH ROW EXECUTE FUNCTION dotnet_signing.consent_records_append_only();

                    CREATE TRIGGER consent_records_no_truncate
                        BEFORE TRUNCATE ON dotnet_signing."ConsentRecords"
                        FOR EACH STATEMENT EXECUTE FUNCTION dotnet_signing.consent_records_append_only();
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table drops its triggers; the function goes separately.
            migrationBuilder.DropTable(
                name: "ConsentRecords",
                schema: "dotnet_signing");

            if (migrationBuilder.IsNpgsql())
            {
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS dotnet_signing.consent_records_append_only();");
            }
        }
    }
}
