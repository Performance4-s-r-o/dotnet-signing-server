using DotNetSigningServer.Models;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Layout.Element;
using iText.Signatures;
using Org.BouncyCastle.X509;
using System;
using System.IO;

using IOPath = System.IO.Path;


namespace DotNetSigningServer.Services
{
    public class PdfSigningService
    {
        public (string PresignedPdfPath, string HashToSign) HandlePreSign(PreSignInput input)
        {
            byte[] originalPdf = Convert.FromBase64String(input.PdfContent);
            var preSignContainer = new DigestCalcBlankSigner(PdfName.Adobe_PPKLite, PdfName.Adbe_pkcs7_detached);

            byte[] pdfWithPlaceholder = CreatePreSignedPdf(originalPdf, preSignContainer, input);

            string preSignedPdfPath = IOPath.Combine(IOPath.GetTempPath(), $"presigned_{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(preSignedPdfPath, pdfWithPlaceholder);

            string hashToSign = BitConverter.ToString(preSignContainer.GetDocBytesHash()).Replace("-", "").ToLowerInvariant();
            return (preSignedPdfPath, hashToSign);
        }

        public string HandleSign(SignInput input, string presignedPdfPath, string certificatePem)
        {
            if (!File.Exists(presignedPdfPath))
            {
                throw new FileNotFoundException("Pre-signed PDF not found.", presignedPdfPath);
            }

            byte[] preSignedPdf = File.ReadAllBytes(presignedPdfPath);
            byte[] fullySignedPdf = InjectFinalSignature(preSignedPdf, input, certificatePem);

            return Convert.ToBase64String(fullySignedPdf);
        }

        private static byte[] CreatePreSignedPdf(byte[] originalPdf, DigestCalcBlankSigner container, PreSignInput input)
        {
            using var msIn = new MemoryStream(originalPdf);
            using var msOut = new MemoryStream();

            var chain = new[] { LoadCertificateFromPem(input.CertificatePem) };
            container.SetChain(chain);

            var reader = new PdfReader(msIn);
            var signer = new CustomPdfSigner(reader, msOut, new StampingProperties().UseAppendMode());

            var appearance = signer.GetSignatureAppearance()
                .SetReason(input.Reason)
                .SetLocation(input.Location)
                .SetPageRect(new Rectangle(input.SignRect.X, input.SignRect.Y, input.SignRect.Width, input.SignRect.Height))
                .SetPageNumber(input.SignPageNumber)
                .SetCertificate(chain[0]);

            if (!string.IsNullOrEmpty(input.SignImageContent))
            {
                byte[] image = Convert.FromBase64String(input.SignImageContent);
                appearance.SetSignatureGraphic(ImageDataFactory.Create(image))
                          .SetRenderingMode(PdfSignatureAppearance.RenderingMode.GRAPHIC_AND_DESCRIPTION);
            }

            signer.SetCertificationLevel(PdfSigner.NOT_CERTIFIED);
            signer.SignExternalContainer(container, 8192 * 2);

            return msOut.ToArray();
        }

        private static byte[] InjectFinalSignature(byte[] pdfWithPlaceholder, SignInput signInput, string certificatePem)
        {
            using var msIn = new MemoryStream(pdfWithPlaceholder);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var chain = new[] { LoadCertificateFromPem(certificatePem) };
            byte[] signatureBytes = HexStringToByteArray(signInput.SignedHash);
            IExternalSignatureContainer external = new ExternalSignatureContainer(chain, signatureBytes);

            PdfSigner.SignDeferred(new PdfDocument(reader, new PdfWriter(msOut), new StampingProperties()), "Signature1", msOut, external);

            return msOut.ToArray();
        }

        private static X509Certificate LoadCertificateFromPem(string pem)
        {
            var pemReader = new Org.BouncyCastle.OpenSsl.PemReader(new StringReader(pem));
            return (X509Certificate)pemReader.ReadObject();
        }

        private static byte[] HexStringToByteArray(string hex)
        {
            if (hex.Length % 2 != 0) throw new ArgumentException("Hex string must have an even length.");
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < hex.Length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return bytes;
        }
    }
}
