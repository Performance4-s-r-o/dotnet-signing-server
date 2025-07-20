using DotNetSigningServer.Models;
using DotNetSigningServer.Services.Old;
using iText.Bouncycastle.X509;
using iText.Commons.Bouncycastle.Cert;
using iText.Forms.Fields.Properties;
using iText.Forms.Form.Element;
using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.X509;

using IOPath = System.IO.Path;


namespace DotNetSigningServer.Services
{
    public class PdfSigningService
    {
        private const string FIELD_NAME = "Signature1";
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

            var chain = LoadCertificatesFromPemString(input.CertificatePem);
            container.SetChain(chain);

            var reader = new PdfReader(msIn);
            var signer = new PdfSigner(reader, msOut, new StampingProperties().UseAppendMode());

            SignerProperties signerProperties = new SignerProperties().SetFieldName(FIELD_NAME);

            var CN = chain[0].GetSubjectDN().ToString()?.Split(",")[0].Split("=")[1];

            SignatureFieldAppearance appearance = new SignatureFieldAppearance(SignerProperties.IGNORED_ID);
            var appearanceText = new SignedAppearanceText().SetReasonLine($"Signed by {CN}\nReason: {input.Reason}");

            if (!string.IsNullOrEmpty(input.SignImageContent))
            {
                byte[] image = Convert.FromBase64String(input.SignImageContent);
                appearance.SetContent(appearanceText, ImageDataFactory.Create(image));
            }
            else
            {
                appearance.SetContent(appearanceText);
            }

            signerProperties
                .SetPageRect(new Rectangle(input.SignRect.X, input.SignRect.Y, input.SignRect.Width, input.SignRect.Height))
                .SetReason(input.Reason)
                .SetLocation(input.Location)
                .SetPageNumber(input.SignPageNumber)
                .SetCertificationLevel(AccessPermissions.UNSPECIFIED)
                .SetFieldName(FIELD_NAME)
                .SetSignatureAppearance(appearance);


            signer.SetSignerProperties(signerProperties);
            signer.SignExternalContainer(container, 8192);

            return msOut.ToArray();
        }

        private static byte[] InjectFinalSignature(byte[] pdfWithPlaceholder, SignInput signInput, string certificatePem)
        {
            using var msIn = new MemoryStream(pdfWithPlaceholder);
            using var msOut = new MemoryStream();

            var reader = new PdfReader(msIn);
            var chain = LoadCertificatesFromPemString(certificatePem);
            byte[] signatureBytes = HexStringToByteArray(signInput.SignedHash);
            IExternalSignatureContainer external = new ExternalSignatureContainer(chain, signatureBytes);

            PdfSigner.SignDeferred(reader, FIELD_NAME, msOut, external);

            return msOut.ToArray();
        }


        private static IX509Certificate[] LoadCertificatesFromPemString(string pem)
        {
            using (var reader = new StringReader(pem))
            {
                var pemReader = new Org.BouncyCastle.OpenSsl.PemReader(reader);
                var certs = new List<IX509Certificate>();
                Console.Out.WriteLine(certs.ToArray());
                object? readObject;
                while ((readObject = pemReader.ReadObject()) != null)
                {

                    IX509Certificate cert = new X509CertificateBC((X509Certificate)readObject);
                    certs.Add(cert);

                }
                return certs.ToArray();
            }
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
