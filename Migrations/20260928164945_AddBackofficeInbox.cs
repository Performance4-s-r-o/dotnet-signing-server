using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddBackofficeInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackofficeStates",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackofficeStates", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "BackofficeWebhookInboxItems",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WebhookId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackofficeWebhookInboxItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackofficeWebhookInboxItems_NextAttemptAt",
                schema: "dotnet_signing",
                table: "BackofficeWebhookInboxItems",
                column: "NextAttemptAt",
                filter: "\"ProcessedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BackofficeWebhookInboxItems_ProcessedAt",
                schema: "dotnet_signing",
                table: "BackofficeWebhookInboxItems",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BackofficeWebhookInboxItems_WebhookId",
                schema: "dotnet_signing",
                table: "BackofficeWebhookInboxItems",
                column: "WebhookId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackofficeStates",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "BackofficeWebhookInboxItems",
                schema: "dotnet_signing");
        }
    }
}
