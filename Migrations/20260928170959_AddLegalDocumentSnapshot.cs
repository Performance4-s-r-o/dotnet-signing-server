using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalDocumentSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangeKind",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentHtml",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FetchedAt",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.AddColumn<string>(
                name: "TypeKey",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChangeKind",
                schema: "dotnet_signing",
                table: "LegalDocuments");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                schema: "dotnet_signing",
                table: "LegalDocuments");

            migrationBuilder.DropColumn(
                name: "ContentHtml",
                schema: "dotnet_signing",
                table: "LegalDocuments");

            migrationBuilder.DropColumn(
                name: "FetchedAt",
                schema: "dotnet_signing",
                table: "LegalDocuments");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "dotnet_signing",
                table: "LegalDocuments");

            migrationBuilder.DropColumn(
                name: "TypeKey",
                schema: "dotnet_signing",
                table: "LegalDocuments");
        }
    }
}
