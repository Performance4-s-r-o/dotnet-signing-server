using DotNetSigningServer.Models;
using DotNetSigningServer.Options;
using iText.Commons.Bouncycastle.Cert;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto;

namespace DotNetSigningServer.Services
{
    public class PdfSealingService
    {
        private readonly SealOptions _sealOptions;
        private readonly PdfSigningService _pdfSigningService;
        private readonly PdfVisualSigningService _visualSigningService;

        public PdfSealingService(
            IOptions<SealOptions>? sealOptions,
            PdfSigningService pdfSigningService,
            PdfVisualSigningService visualSigningService)
        {
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
                tsaPassword: input.TsaPassword);

            return Convert.ToBase64String(fullySignedPdf);
        }

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
