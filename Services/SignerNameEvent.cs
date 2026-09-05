using iText.Signatures;

namespace DotNetSigningServer.Services;

/// <summary>
/// Writes the signer into the signature dictionary's /Name entry.
///
/// Without it that entry stays empty and a reader showing "Signed by" falls
/// back to the certificate subject — which, on every online signature, is our
/// platform seal or the customer's vault key. The visual then said "Jane Doe"
/// while clicking it said "Performance4 s.r.o."
///
/// The certificate itself is not hidden and must not be: it is the only proof
/// the document carries, and on this path the signer has no certificate of
/// their own. /Name is what makes the two layers agree — it is defined by the
/// PDF specification as "the name of the person or authority signing the
/// document", which is the signer for a personal signature and the organisation
/// for a seal.
///
/// Which of the two arrives here is decided by the caller, deliberately: the
/// engine has no idea whether it is stamping for a person or for a company, and
/// guessing from the appearance flags would put that policy in two places.
/// </summary>
internal sealed class SignerNameEvent : PdfSigner.ISignatureEvent
{
    private readonly string _name;

    internal SignerNameEvent(string name) => _name = name;

    public void GetSignatureDictionary(PdfSignature sig) => sig.SetName(_name);
}
