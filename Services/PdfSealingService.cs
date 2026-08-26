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

        public string ApplySeal(SealInput input)
        {
            if (!_sealOptions.Enabled)
            {
                throw new InvalidOperationException("Server-side sealing is not enabled.");
            }

            string pdfContent = input.PdfContent;

            // Add verification metadata/QR page before sealing
            if (!string.IsNullOrWhiteSpace(input.VerificationUrl) && input.VerificationMode != "disabled")
            {
                byte[] pdfBytes = Convert.FromBase64String(pdfContent);
                pdfBytes = PdfVerificationService.AddVerification(
                    pdfBytes, input.VerificationUrl, input.VerificationMode ?? "disabled", input.SignerName);
                pdfContent = Convert.ToBase64String(pdfBytes);
            }

            // Stamp custom fillable field values before sealing so they are covered by the seal signature.
            if (input.Fields is { Count: > 0 })
            {
                byte[] withFields = PdfTemplateService.StampTextFields(Convert.FromBase64String(pdfContent), input.Fields);
                pdfContent = Convert.ToBase64String(withFields);
            }

            if (ShouldApplyVisibleOverlay(input))
            {
                pdfContent = _visualSigningService.ApplyVisualSign(new VisualSignInput
                {
                    PdfContent = pdfContent,
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
                });
            }

            var (chain, privateKey) = LoadSealCredentials();
            byte[] originalPdf = Convert.FromBase64String(pdfContent);
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
                disableTsa: input.DisableTsa);

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

        private (IX509Certificate[] Chain, ICipherParameters PrivateKey) LoadSealCredentials()
        {
            byte[] pfxBytes;
            if (!string.IsNullOrWhiteSpace(_sealOptions.PfxBase64))
            {
                pfxBytes = Convert.FromBase64String(_sealOptions.PfxBase64);
            }
            else if (!string.IsNullOrWhiteSpace(_sealOptions.PfxPath) && File.Exists(_sealOptions.PfxPath))
            {
                pfxBytes = File.ReadAllBytes(_sealOptions.PfxPath);
            }
            else
            {
                throw new InvalidOperationException("Seal certificate is not configured.");
            }

            return PdfCryptoHelper.LoadFromPfxBytes(pfxBytes, _sealOptions.PfxPassword);
        }
    }
}
