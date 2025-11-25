using iText.Kernel.Pdf;
using iText.Signatures;
using Org.BouncyCastle.Security;
using System.IO;
using System;
using iText.Kernel.Crypto;
using iText.Commons.Bouncycastle.Cert;

namespace DotNetSigningServer.ExternalSignatures
{

    internal class DigestCalcBlankSigner : IExternalSignatureContainer
    {
        private readonly PdfName _filter;
        private readonly PdfName _subFilter;
        private byte[] _docBytesHash = Array.Empty<byte>();
        private IX509Certificate[] _chain = Array.Empty<IX509Certificate>();

        internal DigestCalcBlankSigner(PdfName filter, PdfName subFilter)
        {
            _filter = filter;
            _subFilter = subFilter;
        }

        public void SetChain(IX509Certificate[] chain) => _chain = chain;
        internal byte[] GetDocBytesHash() => _docBytesHash;

        public byte[] Sign(Stream docBytes)
        {
            byte[] hash = DigestAlgorithms.Digest(docBytes, DigestAlgorithms.SHA256);

            var signature = new PdfPKCS7(null, _chain, "SHA256", false);
            _docBytesHash = signature.GetAuthenticatedAttributeBytes(hash, PdfSigner.CryptoStandard.CMS, null, null);
            return _docBytesHash;
        }

        public void ModifySigningDictionary(PdfDictionary signDic)
        {
            signDic.Put(PdfName.Filter, _filter);
            signDic.Put(PdfName.SubFilter, _subFilter);
        }
    }

    internal class ExternalSignatureContainer : IExternalSignatureContainer
    {
        private readonly IX509Certificate[] _chain;
        private readonly byte[] _signature;
        private readonly ITSAClient? _tsaClient;

        public ExternalSignatureContainer(IX509Certificate[] chain, byte[] signature, ITSAClient? tsaClient)
        {
            _chain = chain;
            _signature = signature;
            _tsaClient = tsaClient;
        }

        public byte[] Sign(Stream inputStream)
        {
            var sgn = new PdfPKCS7(null, _chain, "SHA256", false);
            byte[] hash = DigestAlgorithms.Digest(inputStream, DigestAlgorithms.SHA256);
            sgn.SetExternalSignatureValue(_signature, null, "RSA");
            return sgn.GetEncodedPKCS7(hash, PdfSigner.CryptoStandard.CMS, _tsaClient, null, null);
        }

        public void ModifySigningDictionary(PdfDictionary signDic) { }
    }
}
