using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dotnet_signing");

            migrationBuilder.CreateTable(
                name: "PricingPlans",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PricePer100 = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SigningData",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    PresignedPdfPath = table.Column<string>(type: "text", nullable: false),
                    HashToSign = table.Column<string>(type: "text", nullable: false),
                    CertificatePem = table.Column<string>(type: "text", nullable: false),
                    FieldName = table.Column<string>(type: "text", nullable: false),
                    TsaUrl = table.Column<string>(type: "text", nullable: true),
                    TsaUsername = table.Column<string>(type: "text", nullable: true),
                    TsaPassword = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningData", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoredPdfTemplates",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Base64Content = table.Column<string>(type: "text", nullable: false),
                    FieldsJson = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredPdfTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    PasswordSalt = table.Column<byte[]>(type: "bytea", nullable: false),
                    StripeCustomerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    EmailVerificationToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EmailOtpCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    EmailOtpExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PasswordResetToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PasswordResetExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreditsRemaining = table.Column<int>(type: "integer", nullable: false),
                    AutoRechargeEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AutoRechargeQuantity = table.Column<int>(type: "integer", nullable: false),
                    AutoRechargePricePer100 = table.Column<decimal>(type: "numeric", nullable: false),
                    AutoRechargeCancelToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PriceChangeNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EmailNotificationsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MaxConcurrentOperations = table.Column<int>(type: "integer", nullable: true),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnterprise = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEvents",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiTokens",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    IsBrowserToken = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedOrigins = table.Column<string>(type: "text", nullable: true),
                    AllowedIps = table.Column<string>(type: "text", nullable: true),
                    TokenPrefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiTokens_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "dotnet_signing",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Documents_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "dotnet_signing",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeInvoiceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AmountCents = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoices_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "dotnet_signing",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UsageRecords",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    BaseCost = table.Column<int>(type: "integer", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageRecords_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "dotnet_signing",
                        principalTable: "Documents",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UsageRecords_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "dotnet_signing",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    StripePaymentIntentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AmountCents = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "dotnet_signing",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Payments_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "dotnet_signing",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenHash",
                schema: "dotnet_signing",
                table: "ApiTokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_TokenPrefix",
                schema: "dotnet_signing",
                table: "ApiTokens",
                column: "TokenPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_ApiTokens_UserId",
                schema: "dotnet_signing",
                table: "ApiTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_UserId",
                schema: "dotnet_signing",
                table: "Documents",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_StripeInvoiceId",
                schema: "dotnet_signing",
                table: "Invoices",
                column: "StripeInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_UserId",
                schema: "dotnet_signing",
                table: "Invoices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_InvoiceId",
                schema: "dotnet_signing",
                table: "Payments",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_StripePaymentIntentId",
                schema: "dotnet_signing",
                table: "Payments",
                column: "StripePaymentIntentId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_UserId",
                schema: "dotnet_signing",
                table: "Payments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_DocumentId",
                schema: "dotnet_signing",
                table: "UsageRecords",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageRecords_UserId_CreatedAt",
                schema: "dotnet_signing",
                table: "UsageRecords",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "dotnet_signing",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_EventId",
                schema: "dotnet_signing",
                table: "WebhookEvents",
                column: "EventId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiTokens",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "PricingPlans",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "SigningData",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "StoredPdfTemplates",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "UsageRecords",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "WebhookEvents",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "Invoices",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "Documents",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "dotnet_signing");
        }
    }
}
