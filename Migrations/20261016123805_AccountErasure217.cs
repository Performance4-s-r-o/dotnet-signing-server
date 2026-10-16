using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <summary>
    /// #217 account erasure, additive only: <c>Users.ErasedAt</c> (tombstone marker), an index
    /// for the outbox items of one subject, and a narrower append-only rule for
    /// <c>ConsentRecords</c>: the erasure may clear <c>UserAgent</c> — that column and only to
    /// NULL — inside its own transaction (<c>SET LOCAL dotnet_signing.erasure = 'on'</c>).
    /// Every other UPDATE, every DELETE and TRUNCATE are refused as before.
    /// </summary>
    public partial class AccountErasure217 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ErasedAt",
                schema: "dotnet_signing",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackofficeOutboxItems_SubjectRef",
                schema: "dotnet_signing",
                table: "BackofficeOutboxItems",
                column: "SubjectRef",
                filter: "\"SubjectRef\" IS NOT NULL");

            if (migrationBuilder.IsNpgsql())
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION dotnet_signing.consent_records_append_only() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                    BEGIN
                        IF TG_OP = 'UPDATE'
                           AND current_setting('dotnet_signing.erasure', true) = 'on'
                           AND NEW."UserAgent" IS NULL
                           AND (to_jsonb(NEW) - 'UserAgent') = (to_jsonb(OLD) - 'UserAgent') THEN
                            RETURN NEW;
                        END IF;
                        RAISE EXCEPTION 'ConsentRecords is append-only: % is not allowed', TG_OP
                            USING ERRCODE = 'restrict_violation';
                    END;
                    $$;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.IsNpgsql())
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION dotnet_signing.consent_records_append_only() RETURNS trigger
                    LANGUAGE plpgsql AS $$
                    BEGIN
                        RAISE EXCEPTION 'ConsentRecords is append-only: % is not allowed', TG_OP
                            USING ERRCODE = 'restrict_violation';
                    END;
                    $$;
                    """);
            }

            migrationBuilder.DropIndex(
                name: "IX_BackofficeOutboxItems_SubjectRef",
                schema: "dotnet_signing",
                table: "BackofficeOutboxItems");

            migrationBuilder.DropColumn(
                name: "ErasedAt",
                schema: "dotnet_signing",
                table: "Users");
        }
    }
}
