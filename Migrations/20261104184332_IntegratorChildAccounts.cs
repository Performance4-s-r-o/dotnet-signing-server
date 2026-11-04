using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class IntegratorChildAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TrustedCaller",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RequestId",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "UsageRecords");

            migrationBuilder.DropColumn(
                name: "RequestId",
                schema: "dotnet_signing",
                table: "SigningData");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "SigningData");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "ApiOperations");

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                schema: "dotnet_signing",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Label",
                schema: "dotnet_signing",
                table: "Users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentUserId",
                schema: "dotnet_signing",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsProvisioningToken",
                schema: "dotnet_signing",
                table: "ApiTokens",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AccountProvisioningEvents",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    CallerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChildUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClientIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountProvisioningEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_ParentUserId_ExternalId",
                schema: "dotnet_signing",
                table: "Users",
                columns: new[] { "ParentUserId", "ExternalId" },
                unique: true,
                filter: "\"ErasedAt\" IS NULL AND \"ParentUserId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_Child_HasExternalId",
                schema: "dotnet_signing",
                table: "Users",
                sql: "(\"ParentUserId\" IS NULL) = (\"ExternalId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_Child_IsServiceAccount",
                schema: "dotnet_signing",
                table: "Users",
                sql: "\"ParentUserId\" IS NULL OR \"IsServiceAccount\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ApiTokens_Kind",
                schema: "dotnet_signing",
                table: "ApiTokens",
                sql: "NOT (\"IsBrowserToken\" AND \"IsProvisioningToken\")");

            migrationBuilder.CreateIndex(
                name: "IX_AccountProvisioningEvents_CallerUserId_CreatedAt",
                schema: "dotnet_signing",
                table: "AccountProvisioningEvents",
                columns: new[] { "CallerUserId", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Users_ParentUserId",
                schema: "dotnet_signing",
                table: "Users",
                column: "ParentUserId",
                principalSchema: "dotnet_signing",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Users_ParentUserId",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropTable(
                name: "AccountProvisioningEvents",
                schema: "dotnet_signing");

            migrationBuilder.DropIndex(
                name: "IX_Users_ParentUserId_ExternalId",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Child_HasExternalId",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Child_IsServiceAccount",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ApiTokens_Kind",
                schema: "dotnet_signing",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Label",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ParentUserId",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsProvisioningToken",
                schema: "dotnet_signing",
                table: "ApiTokens");

            migrationBuilder.AddColumn<bool>(
                name: "TrustedCaller",
                schema: "dotnet_signing",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "UsageRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                schema: "dotnet_signing",
                table: "SigningData",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "SigningData",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                schema: "dotnet_signing",
                table: "ApiOperations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
