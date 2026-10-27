using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using iText.Commons.Bouncycastle.Cert;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto;

namespace DotNetSigningServer.Services
{
    public class PdfSealingService
    {
        private readonly SealOptions _sealOptions;
        private readonly PdfSigningService _pdfSigningService;
        private readonly PdfVisualSigningService _visualSigningService;
        private readonly ILogger<PdfSealingService>? _logger;

        public PdfSealingService(
            IOptions<SealOptions>? sealOptions,
            PdfSigningService pdfSigningService,
            PdfVisualSigningService visualSigningService,
            ILogger<PdfSealingService>? logger = null)
        {
            _logger = logger;
            _sealOptions = sealOptions?.Value ?? new SealOptions();
            _pdfSigningService = pdfSigningService;
            _visualSigningService = visualSigningService;
        }

        public virtual string ApplySeal(SealInput input)
        {
            if (!_sealOptions.Enabled)
            {
                throw new InvalidOperationException("Server-side sealing is not enabled.");
            }

            return ApplySeal(input, Convert.FromBase64String(input.PdfContent));
        }

        /// <summary>
        /// Pečeť nad bajty, které už prošly vstupní kontrolou (#74, binární kanál);
        /// <c>input.PdfContent</c> se nečte. Oba kanály končí tady.
        /// </summary>
        public virtual string ApplySeal(SealInput input, byte[] pdf)
        {
            if (!_sealOptions.Enabled)
            {
                throw new InvalidOperationException("Server-side sealing is not enabled.");
            }

            byte[] originalPdf = pdf;

            // Add verification metadata/QR page before sealing
            if (!string.IsNullOrWhiteSpace(input.VerificationUrl) && input.VerificationMode != "disabled")
            {
                originalPdf = PdfVerificationService.AddVerification(
                    originalPdf, input.VerificationUrl, input.VerificationMode ?? "disabled", input.SignerName);
            }

            // Stamp custom fillable field values before sealing so they are covered by the seal signature.
            if (input.Fields is { Count: > 0 })
            {
                originalPdf = PdfTemplateService.StampTextFields(originalPdf, input.Fields);
            }

            if (ShouldApplyVisibleOverlay(input))
            {
                originalPdf = Convert.FromBase64String(_visualSigningService.ApplyVisualSign(new VisualSignInput
                {
                    Location = input.Location,
                    Reason = input.Reason,
                    SignRect = input.SignRect,
                    SignImageContent = input.SignImageContent,
                    StampImageContent = input.StampImageContent,
                    CompanyLogoContent = input.CompanyLogoContent,
                    BackgroundImageContent = input.BackgroundImageContent,
                    SignPageNumber = input.SignPageNumber,
                    Appearance = input.Appearance,
                    TemplateId = input.TemplateId,
                    SignerName = input.SignerName,
                    DesignWidth = input.DesignWidth,
                    DesignHeight = input.DesignHeight,
                    AutoHeight = input.AutoHeight,
                }, originalPdf));
            }

            var (chain, privateKey) = LoadSealCredentials();
            byte[] fullySignedPdf = _pdfSigningService.SignPdfWithKeyPair(
                originalPdf,
                chain,
                privateKey,
                PdfCryptoHelper.EnsureFieldName(null, $"Seal_{Guid.NewGuid():N}"),
                input.SignRect,
                string.IsNullOrWhiteSpace(input.Reason) ? _sealOptions.Reason : input.Reason,
                string.IsNullOrWhiteSpace(input.Location) ? _sealOptions.Location : input.Location,
                input.SignPageNumber,
                _sealOptions.Visible ? input.SignImageContent : null,
                _sealOptions.Visible ? input.Appearance : null,
                _sealOptions.Visible ? input.StampImageContent : null,
                _sealOptions.Visible ? input.BackgroundImageContent : null,
                _sealOptions.Visible ? input.CompanyLogoContent : null,
                visible: _sealOptions.Visible,
                tsaUrl: input.TsaUrl,
                tsaUsername: input.TsaUsername,
                tsaPassword: input.TsaPassword,
                designWidth: input.DesignWidth,
                designHeight: input.DesignHeight,
                autoHeight: input.AutoHeight,
                disableTsa: input.DisableTsa,
                tsaBackup: TsaEndpoint.From(input.TsaBackupUrl, input.TsaBackupUsername, input.TsaBackupPassword));

            return Convert.ToBase64String(fullySignedPdf);
        }

        /// <summary>
        /// What the caller can find out about sealing before committing a user to
        /// a wizard that ends in it.
        ///
        /// Without this the only way to learn that no certificate is configured is
        /// to submit a document and have <see cref="ApplySeal"/> refuse it at the
        /// last step. On the hosted service that is a one-time setup mistake; on a
        /// self-hosted install it is the state every deployment starts in.
        /// </summary>
        public SealCapability DescribeCapability()
        {
            if (!_sealOptions.Enabled) return new SealCapability { Enabled = false };

            try
            {
                var (chain, _) = LoadSealCredentials();
                var signing = chain.FirstOrDefault();
                if (signing == null) return new SealCapability { Enabled = false };

                return new SealCapability
                {
                    Enabled = true,
                    // The certificate is the honest source for whose seal this is —
                    // a configured display name can drift from it over years.
                    Subject = signing.GetSubjectDN()?.ToString(),
                    Issuer = signing.GetIssuerDN()?.ToString(),
                    NotBefore = ToOffset(signing.GetNotBefore()),
                    NotAfter = ToOffset(signing.GetNotAfter()),
                };
            }
            catch (Exception ex)
            {
                // Configured but unusable — a wrong password or an unreadable file.
                // Saying so beats reporting the feature as available.
                _logger?.LogWarning(ex, "[seal] certificate is configured but could not be loaded");
                return new SealCapability { Enabled = false, Error = "SEAL_CERTIFICATE_UNREADABLE" };
            }
        }

        private static DateTimeOffset? ToOffset(DateTime value) =>
            value == default ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        public static bool ShouldApplyVisibleOverlay(SealInput input)
        {
            return !string.IsNullOrWhiteSpace(input.SignImageContent)
                || !string.IsNullOrWhiteSpace(input.StampImageContent)
                || !string.IsNullOrWhiteSpace(input.CompanyLogoContent)
                || !string.IsNullOrWhiteSpace(input.BackgroundImageContent)
                || !string.IsNullOrWhiteSpace(input.SignerName);
        }

        private (IX509Certificate[] Chain, ICipherParameters PrivateKey) LoadSealCredentials() =>
            SealCredentialStore.Get(_sealOptions);
    }
}
