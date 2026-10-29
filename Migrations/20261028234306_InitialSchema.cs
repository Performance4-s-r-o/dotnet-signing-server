using System;
using Microsoft.EntityFrameworkCore;
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
            // Squashes the 24 migrations before the first release. Column order and the six
            // DEFAULTs the model does not declare are kept as that history left them.
            migrationBuilder.EnsureSchema(
                name: "dotnet_signing");

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
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiOperations", x => x.Id);
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

            migrationBuilder.CreateTable(
                name: "BackofficeOutboxItems",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PayloadProtected = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Critical = table.Column<bool>(type: "boolean", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemoteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SubjectRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FirstAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackofficeOutboxItems", x => x.Id);
                });

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
                    Error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DeadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackofficeWebhookInboxItems", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "LegalDocuments",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Content = table.Column<string>(type: "text", nullable: false),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDraft = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ChangeKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ContentHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ContentHtml = table.Column<string>(type: "text", nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "manual"),
                    TypeKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperatorSwitches",
                schema: "dotnet_signing",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorSwitches", x => x.Name);
                });

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
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TsaBackupPassword = table.Column<string>(type: "text", nullable: true),
                    TsaBackupUrl = table.Column<string>(type: "text", nullable: true),
                    TsaBackupUsername = table.Column<string>(type: "text", nullable: true),
                    RequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
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
                    EmailOtpCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
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
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EnterpriseEnabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyQueueTimeoutSeconds = table.Column<int>(type: "integer", nullable: true),
                    EmailVerificationExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PasswordIterations = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SealAllowed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    EmailBouncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Locale = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    PriceChangeNotifiedVersion = table.Column<int>(type: "integer", nullable: true),
                    EmailOtpFailedAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LoginChallengeHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreditLimit = table.Column<int>(type: "integer", nullable: true),
                    CreditsUsed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
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
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    RequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
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
                name: "IX_AdminAuditEntries_TargetUserId_CreatedAt",
                schema: "dotnet_signing",
                table: "AdminAuditEntries",
                columns: new[] { "TargetUserId", "CreatedAt" });

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
                name: "IX_AuthAttemptCounters_WindowStart",
                schema: "dotnet_signing",
                table: "AuthAttemptCounters",
                column: "WindowStart");

            migrationBuilder.CreateIndex(
                name: "IX_BackofficeOutboxItems_CreatedAt",
                schema: "dotnet_signing",
                table: "BackofficeOutboxItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BackofficeOutboxItems_Status_NextAttemptAt",
                schema: "dotnet_signing",
                table: "BackofficeOutboxItems",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackofficeOutboxItems_SubjectRef",
                schema: "dotnet_signing",
                table: "BackofficeOutboxItems",
                column: "SubjectRef",
                filter: "\"SubjectRef\" IS NOT NULL");

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
                name: "IX_LegalDocuments_Slug_Locale_IsDraft_EffectiveFrom",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                columns: new[] { "Slug", "Locale", "IsDraft", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_LegalDocuments_Slug_Locale_Version",
                schema: "dotnet_signing",
                table: "LegalDocuments",
                columns: new[] { "Slug", "Locale", "Version" },
                unique: true);

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
                name: "IX_UsageRecords_UserId_Status_CreatedAt",
                schema: "dotnet_signing",
                table: "UsageRecords",
                columns: new[] { "UserId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "dotnet_signing",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_LoginChallengeHash",
                schema: "dotnet_signing",
                table: "Users",
                column: "LoginChallengeHash",
                filter: "\"LoginChallengeHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_StripeCustomerId",
                schema: "dotnet_signing",
                table: "Users",
                column: "StripeCustomerId",
                unique: true,
                filter: "\"StripeCustomerId\" IS NOT NULL AND btrim(\"StripeCustomerId\") <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_EventId",
                schema: "dotnet_signing",
                table: "WebhookEvents",
                column: "EventId",
                unique: true);

            // Hand-written below: the model generator does not see any of it.
            if (!migrationBuilder.IsNpgsql()) return;

            // One account per address regardless of letter case; not expressible in the model.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_Users_Email_Lower" ON dotnet_signing."Users" (lower("Email"));
                """);

            // ConsentRecords is evidence: append-only, except clearing UserAgent during an erasure.
            migrationBuilder.Sql("""
                CREATE FUNCTION dotnet_signing.consent_records_append_only() RETURNS trigger
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

                CREATE TRIGGER consent_records_append_only
                    BEFORE UPDATE OR DELETE ON dotnet_signing."ConsentRecords"
                    FOR EACH ROW EXECUTE FUNCTION dotnet_signing.consent_records_append_only();

                CREATE TRIGGER consent_records_no_truncate
                    BEFORE TRUNCATE ON dotnet_signing."ConsentRecords"
                    FOR EACH STATEMENT EXECUTE FUNCTION dotnet_signing.consent_records_append_only();
                """);

            // AdminAuditEntries is append-only for any client of the runtime role, which has no DDL rights.
            migrationBuilder.Sql(@"
CREATE FUNCTION dotnet_signing.admin_audit_append_only() RETURNS trigger
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

            migrationBuilder.DropTable(
                name: "AdminAuditEntries",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "ApiOperations",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "ApiTokens",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "AuthAttemptCounters",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "BackofficeOutboxItems",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "BackofficeStates",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "BackofficeWebhookInboxItems",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "ConsentRecords",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "CreditGrants",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "LegalDocuments",
                schema: "dotnet_signing");

            migrationBuilder.DropTable(
                name: "OperatorSwitches",
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

            // The tables took their triggers and the lower(Email) index with them.
            if (migrationBuilder.IsNpgsql())
            {
                migrationBuilder.Sql("""
                    DROP FUNCTION IF EXISTS dotnet_signing.consent_records_append_only();
                    DROP FUNCTION IF EXISTS dotnet_signing.admin_audit_append_only();
                    """);
            }
        }
    }
}
