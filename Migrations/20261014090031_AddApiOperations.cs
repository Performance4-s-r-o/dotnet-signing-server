using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddApiOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiOperations",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClientKey = table.Column<bool>(type: "boolean", nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PresignId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BaseCost = table.Column<int>(type: "integer", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false),
                    CreditsReserved = table.Column<int>(type: "integer", nullable: false),
                    CreditsCharged = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultStatusCode = table.Column<int>(type: "integer", nullable: true),
                    ResultIsPdf = table.Column<bool>(type: "boolean", nullable: false),
                    ResultProtected = table.Column<byte[]>(type: "bytea", nullable: true),
                    ResultPdfHeaders = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ResultExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiOperations_PresignId",
                schema: "dotnet_signing",
                table: "ApiOperations",
                column: "PresignId",
                unique: true,
                filter: "\"PresignId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApiOperations_State_LeaseUntil",
                schema: "dotnet_signing",
                table: "ApiOperations",
                columns: new[] { "State", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiOperations_UpdatedAt",
                schema: "dotnet_signing",
                table: "ApiOperations",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApiOperations_UserId_OperationId",
                schema: "dotnet_signing",
                table: "ApiOperations",
                columns: new[] { "UserId", "OperationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiOperations",
                schema: "dotnet_signing");
        }
    }
}
