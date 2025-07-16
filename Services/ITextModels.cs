using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.Security;
using System.IO;
using System;

namespace DotNetSigningServer.Services
{
    internal class CustomPdfSigner : PdfSigner
    {
        public CustomPdfSigner(PdfReader reader, Stream outputStream, StampingProperties properties) : base(reader, outputStream, properties) { }
    }

    internal class DigestCalcBlankSigner : IExternalSignatureContainer
    {
        private readonly PdfName _filter;
        private readonly PdfName _subFilter;
        private byte[] _docBytesHash = Array.Empty<byte>();
        private X509Certificate[] _chain = Array.Empty<X509Certificate>();

        internal DigestCalcBlankSigner(PdfName filter, PdfName subFilter)
        {
            _filter = filter;
            _subFilter = subFilter;
        }

        public void SetChain(X509Certificate[] chain) => _chain = chain;
        internal byte[] GetDocBytesHash() => _docBytesHash;

        public byte[] Sign(Stream docBytes)
        {
            var digest = DigestUtilities.GetDigest(DigestAlgorithms.SHA256);
            byte[] hash = DigestAlgorithms.Digest(docBytes, digest);

            var signature = new PdfPKCS7(null, _chain, "SHA256", false);
            _docBytesHash = signature.GetAuthenticatedAttributeBytes(hash, PdfSigner.CryptoStandard.CMS, null, null);
            return Array.Empty<byte>();
        }

        public void ModifySigningDictionary(PdfDictionary signDic)
        {
            signDic.Put(PdfName.Filter, _filter);
            signDic.Put(PdfName.SubFilter, _subFilter);
        }
    }

    internal class ExternalSignatureContainer : IExternalSignatureContainer
    {
        private readonly X509Certificate[] _chain;
        private readonly byte[] _signature;

        public ExternalSignatureContainer(X509Certificate[] chain, byte[] signature)
        {
            _chain = chain;
            _signature = signature;
        }

        public byte[] Sign(Stream inputStream)
        {
            var sgn = new PdfPKCS7(null, _chain, "SHA256", false);
            byte[] hash = DigestAlgorithms.Digest(inputStream, DigestUtilities.GetDigest("SHA256"));
            sgn.SetExternalDigest(_signature, null, "RSA");
            return sgn.GetEncodedPKCS7(hash, PdfSigner.CryptoStandard.CMS, null, null, null);
        }

        public void ModifySigningDictionary(PdfDictionary signDic) { }
    }
}
