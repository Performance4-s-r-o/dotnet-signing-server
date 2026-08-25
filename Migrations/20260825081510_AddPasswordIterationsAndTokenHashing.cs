using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace dotnetsigningserver.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordIterationsAndTokenHashing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailVerificationExpiresAt",
                schema: "dotnet_signing",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PasswordIterations",
                schema: "dotnet_signing",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Verification and reset tokens are now looked up by SHA-256 hash. Convert
            // the rows still holding plaintext so links already sitting in inboxes keep
            // working, instead of stranding everyone mid-signup or mid-reset.
            // Convert.ToHexString produces uppercase, so upper() the pgcrypto output.
            migrationBuilder.Sql(@"
                UPDATE dotnet_signing.""Users""
                SET ""EmailVerificationToken"" =
                        upper(encode(sha256(convert_to(""EmailVerificationToken"", 'UTF8')), 'hex')),
                    ""EmailVerificationExpiresAt"" = now() + interval '24 hours'
                WHERE ""EmailVerificationToken"" IS NOT NULL;");

            migrationBuilder.Sql(@"
                UPDATE dotnet_signing.""Users""
                SET ""PasswordResetToken"" =
                        upper(encode(sha256(convert_to(""PasswordResetToken"", 'UTF8')), 'hex'))
                WHERE ""PasswordResetToken"" IS NOT NULL;");

            // PasswordIterations = 0 marks rows hashed with the legacy 100k count;
            // AuthService keeps verifying them and rehashes on next sign-in.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailVerificationExpiresAt",
                schema: "dotnet_signing",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordIterations",
                schema: "dotnet_signing",
                table: "Users");
        }
    }
}
