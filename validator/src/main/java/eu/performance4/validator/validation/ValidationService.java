package eu.performance4.validator.validation;

import eu.europa.esig.dss.alert.LogOnStatusAlert;
import eu.europa.esig.dss.diagnostic.CertificateRevocationWrapper;
import eu.europa.esig.dss.diagnostic.CertificateWrapper;
import eu.europa.esig.dss.diagnostic.DiagnosticData;
import eu.europa.esig.dss.diagnostic.SignatureWrapper;
import eu.europa.esig.dss.diagnostic.TimestampWrapper;
import eu.europa.esig.dss.diagnostic.jaxb.XmlDigestMatcher;
import eu.europa.esig.dss.enumerations.CertificateSourceType;
import eu.europa.esig.dss.enumerations.Indication;
import eu.europa.esig.dss.enumerations.QCType;
import eu.europa.esig.dss.enumerations.SignatureLevel;
import eu.europa.esig.dss.enumerations.SignatureQualification;
import eu.europa.esig.dss.enumerations.SubIndication;
import eu.europa.esig.dss.enumerations.TimestampQualification;
import eu.europa.esig.dss.enumerations.TokenExtractionStrategy;
import eu.europa.esig.dss.jaxb.object.Message;
import eu.europa.esig.dss.model.InMemoryDocument;
import eu.europa.esig.dss.model.policy.ValidationPolicy;
import eu.europa.esig.dss.model.x509.CertificateToken;
import eu.europa.esig.dss.service.crl.OnlineCRLSource;
import eu.europa.esig.dss.service.ocsp.OnlineOCSPSource;
import eu.europa.esig.dss.simplereport.SimpleReport;
import eu.europa.esig.dss.spi.DSSUtils;
import eu.europa.esig.dss.spi.validation.CommonCertificateVerifier;
import eu.europa.esig.dss.spi.x509.CommonTrustedCertificateSource;
import eu.europa.esig.dss.spi.x509.aia.DefaultAIASource;
import eu.europa.esig.dss.validation.SignedDocumentValidator;
import eu.europa.esig.dss.validation.policy.ValidationPolicyLoader;
import eu.europa.esig.dss.validation.reports.Reports;
import eu.performance4.validator.net.GuardedDataLoader;
import eu.performance4.validator.trust.TrustedListService;
import jakarta.annotation.PostConstruct;
import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.math.BigInteger;
import java.nio.file.Files;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HexFormat;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;

/**
 * Validace podle pevné politiky. Výstup je ve tvaru kontraktu z návrhu fáze 4B
 * (§3); {@code trustStatus} je předběžné mapování pro spike, ne konečná pravidla.
 */
@Service
public class ValidationService {

    private static final Logger LOG = LoggerFactory.getLogger(ValidationService.class);
    private static final String QCT_ESEAL_OID = "0.4.0.1862.1.6.2";

    private static final Set<SubIndication> INVALID_SUBS = Set.of(
            SubIndication.HASH_FAILURE, SubIndication.SIG_CRYPTO_FAILURE);
    private static final Set<SubIndication> UNTRUSTED_SUBS = Set.of(
            SubIndication.REVOKED, SubIndication.EXPIRED, SubIndication.NOT_YET_VALID,
            SubIndication.CHAIN_CONSTRAINTS_FAILURE, SubIndication.SIG_CONSTRAINTS_FAILURE,
            SubIndication.CRYPTO_CONSTRAINTS_FAILURE, SubIndication.REVOKED_NO_POE,
            SubIndication.REVOKED_CA_NO_POE);

    @Value("${validator.revocation.online:true}")
    private boolean onlineRevocation;
    @Value("${validator.test-trust-anchors:}")
    private String testTrustAnchors;
    @Value("${validator.dss-version}")
    private String dssVersion;

    private final TrustedListService trustedLists;
    private final GuardedDataLoader dataLoader;
    private CommonCertificateVerifier verifier;
    private CommonTrustedCertificateSource testAnchors;
    private byte[] policyBytes;
    private String policySha256;
    private String policyId;

    public ValidationService(TrustedListService trustedLists, GuardedDataLoader dataLoader) {
        this.trustedLists = trustedLists;
        this.dataLoader = dataLoader;
    }

    @PostConstruct
    void init() throws IOException {
        // Pevná politika: výchozí constraint.xml z DSS, otisk jde do každé odpovědi.
        try (InputStream in = ValidationPolicyLoader.class.getResourceAsStream("/policy/constraint.xml")) {
            if (in == null) {
                throw new IOException("Default DSS policy not on classpath");
            }
            policyBytes = in.readAllBytes();
        }
        policySha256 = sha256(policyBytes);

        verifier = new CommonCertificateVerifier();
        verifier.setTrustedCertSources(trustedLists.certificateSource());
        if (testTrustAnchors != null && !testTrustAnchors.isBlank()) {
            testAnchors = new CommonTrustedCertificateSource();
            File dir = new File(testTrustAnchors);
            File[] files = dir.isDirectory() ? dir.listFiles((d, n) -> n.endsWith(".pem") || n.endsWith(".cer")) : new File[] {dir};
            for (File f : files == null ? new File[0] : files) {
                try (InputStream in = Files.newInputStream(f.toPath())) {
                    CertificateToken token = DSSUtils.loadCertificate(in);
                    testAnchors.addCertificate(token);
                }
            }
            verifier.addTrustedCertSources(testAnchors);
            LOG.warn("TEST TRUST ANCHORS ENABLED ({} certificates) - never in production",
                    testAnchors.getNumberOfCertificates());
        }
        if (onlineRevocation) {
            verifier.setCrlSource(new OnlineCRLSource(dataLoader));
            verifier.setOcspSource(new OnlineOCSPSource(dataLoader));
            verifier.setAIASource(new DefaultAIASource(dataLoader));
        } else {
            verifier.setCrlSource(null);
            verifier.setOcspSource(null);
            verifier.setAIASource(null);
        }
        verifier.setCheckRevocationForUntrustedChains(false);
        verifier.setAlertOnMissingRevocationData(new LogOnStatusAlert());
        verifier.setAlertOnInvalidSignature(new LogOnStatusAlert());
        verifier.setAlertOnInvalidTimestamp(new LogOnStatusAlert());
        verifier.setAlertOnRevokedCertificate(new LogOnStatusAlert());
        verifier.setAlertOnUncoveredPOE(new LogOnStatusAlert());

        policyId = "dss-default-constraint@" + dssVersion + (testAnchors != null ? "+test-anchors" : "");
    }

    private ValidationPolicy policy() {
        return ValidationPolicyLoader.fromValidationPolicy(new ByteArrayInputStream(policyBytes)).create();
    }

    public ValidatorInfo info() {
        TrustedListService.Status s = trustedLists.status();
        return new ValidatorInfo("eu-dss", dssVersion, policyId, policySha256, s.tlLoadedAt(), s.tlStale());
    }

    public ValidationResult validate(byte[] pdf) {
        long t0 = System.nanoTime();
        ValidatorInfo info = info();
        InMemoryDocument document = new InMemoryDocument(pdf, "document.pdf");
        SignedDocumentValidator validator = SignedDocumentValidator.fromDocument(document);
        validator.setCertificateVerifier(verifier);
        validator.setTokenExtractionStrategy(TokenExtractionStrategy.NONE);
        validator.setLocale(Locale.ENGLISH);
        validator.setEnableEtsiValidationReport(false);
        Reports reports = validator.validateDocument(policy());

        DiagnosticData diagnostic = reports.getDiagnosticData();
        SimpleReport simple = reports.getSimpleReport();
        List<SignatureResult> signatures = new ArrayList<>();
        List<SignatureWrapper> sorted = new ArrayList<>(diagnostic.getSignatures());
        sorted.sort(Comparator.comparing(s -> byteRangeEnd(s.getSignatureByteRange())));
        int revision = 0;
        for (SignatureWrapper sig : sorted) {
            revision++;
            signatures.add(mapSignature(sig, simple, pdf.length, revision, info.tlStale()));
        }

        List<TimestampResult> documentTimestamps = new ArrayList<>();
        for (TimestampWrapper ts : diagnostic.getTimestampList()) {
            if (ts.getType() != null && ts.getType().isDocumentTimestamp()) {
                documentTimestamps.add(mapTimestamp(ts, simple));
            }
        }

        long ms = (System.nanoTime() - t0) / 1_000_000;
        return new ValidationResult(info, sha256(pdf), pdf.length, signatures, documentTimestamps, ms);
    }

    private SignatureResult mapSignature(SignatureWrapper sig, SimpleReport simple, long length, int revision,
            boolean tlStale) {
        String id = sig.getId();
        List<BigInteger> br = sig.getSignatureByteRange();
        boolean byteRangeSane = sig.isSignatureByteRangeValid() && br != null && br.size() == 4
                && br.get(0).signum() == 0
                && br.get(0).add(br.get(1)).compareTo(br.get(2)) < 0
                && br.get(2).add(br.get(3)).longValue() <= length;
        Boolean coversWholeDocument = br == null || br.size() != 4 ? null
                : br.get(2).add(br.get(3)).longValue() == length;

        boolean digestIntact = !sig.getDigestMatchers().isEmpty()
                && sig.getDigestMatchers().stream().allMatch(XmlDigestMatcher::isDataIntact);
        String integrity = sig.isSignatureIntact() && digestIntact ? "valid"
                : (!sig.getDigestMatchers().isEmpty() ? "invalid" : "unknown");

        String modified = modification(sig);
        // Bajty za podpisem, které DSS nepoznal jako revizi (např. smetí za %%EOF): nevíme, co jsou.
        if ("none".equals(modified) && Boolean.FALSE.equals(coversWholeDocument)) {
            modified = "unknown";
        }

        Indication indication = simple.getIndication(id);
        SubIndication sub = simple.getSubIndication(id);
        SignatureQualification qualification = simple.getSignatureQualification(id);
        SignatureLevel format = simple.getSignatureFormat(id);

        CertificateWrapper cert = sig.getSigningCertificate();
        CertificateResult certificate = cert == null ? null : mapCertificate(sig, cert, qualification);

        List<TimestampWrapper> sigTs = sig.getSignatureTimestamps();
        TimestampResult timestamp = sigTs.isEmpty()
                ? new TimestampResult(false, null, null, null, null, null, null, null, null, null)
                : mapTimestamp(sigTs.get(0), simple);

        boolean seal = (qualification != null && qualification.getReadable() != null
                && qualification.getReadable().contains("Seal"))
                || (cert != null && cert.getQcTypes().stream().map(QCType::getOid).anyMatch(QCT_ESEAL_OID::equals));

        String trustStatus = trustStatus(integrity, byteRangeSane, modified, indication, sub, certificate, tlStale);

        return new SignatureResult(
                sig.getFirstFieldName(), revision, sig.getSubFilter(), false, seal ? "seal" : "signature",
                integrity, coversWholeDocument, byteRangeSane, modified, certificate, timestamp,
                indication == null ? null : indication.name(), sub == null ? null : sub.name(),
                qualification == null ? null : qualification.getReadable(),
                format == null ? null : format.name(),
                trustStatus,
                messages(simple.getAdESValidationErrors(id)),
                messages(simple.getAdESValidationWarnings(id)),
                messages(simple.getQualificationErrors(id)),
                messages(simple.getQualificationWarnings(id)));
    }

    private CertificateResult mapCertificate(SignatureWrapper sig, CertificateWrapper cert,
            SignatureQualification qualification) {
        boolean euTl = sig.getCertificateChain().stream().anyMatch(CertificateWrapper::isTrustedListReached);
        boolean test = testAnchors != null && sig.getCertificateChain().stream().anyMatch(c ->
                c.isTrusted() && c.getSources().contains(CertificateSourceType.TRUSTED_STORE));
        String trustSource = euTl ? "eu-tl" : test ? "test-anchor" : "none";
        String revocation = "unknown";
        for (CertificateRevocationWrapper r : cert.getCertificateRevocationData()) {
            if (r.isRevoked()) {
                revocation = "revoked";
                break;
            }
            if (r.getStatus() != null && r.getStatus().isGood()) {
                revocation = "good";
            }
        }
        String qualified = "unknown";
        if (qualification != null) {
            String q = qualification.getReadable();
            if (q.startsWith("QES") || q.contains("QC with QSCD")) qualified = "qc-qscd";
            else if (q.contains("QC")) qualified = "qc";
            else if (!q.startsWith("Indeterminate") && !q.equals("N/A")) qualified = "no";
        }
        return new CertificateResult(cert.getCertificateDN(), cert.getCertificateIssuerDN(), cert.getSerialNumber(),
                trustSource, sig.isTrustedChain(), revocation, qualified,
                cert.getNotBefore() == null ? null : cert.getNotBefore().toInstant(),
                cert.getNotAfter() == null ? null : cert.getNotAfter().toInstant());
    }

    private TimestampResult mapTimestamp(TimestampWrapper ts, SimpleReport simple) {
        CertificateWrapper tsa = ts.getSigningCertificate();
        TimestampQualification q = simple.getTimestampQualification(ts.getId());
        Indication ind = simple.getIndication(ts.getId());
        SubIndication sub = simple.getSubIndication(ts.getId());
        return new TimestampResult(true,
                ts.isSignatureValid() && ts.isMessageImprintDataIntact(),
                ts.isMessageImprintDataIntact(),
                tsa == null ? null : tsa.getCertificateDN(),
                tsa == null ? null : (ts.getCertificateChain().stream().anyMatch(CertificateWrapper::isTrustedListReached)
                        ? "eu-tl" : ts.isTrustedChain() ? "test-anchor" : "none"),
                q == null ? null : q.getReadable(),
                ts.getProductionTime() == null ? null : ts.getProductionTime().toInstant(),
                ts.getType() == null ? null : ts.getType().name(),
                ind == null ? null : ind.name(),
                sub == null ? null : sub.name());
    }

    /** DSS sleduje změny po podpisu; nedefinované a vizuální změny jsou nepovolené. */
    private static String modification(SignatureWrapper sig) {
        if (!sig.getPdfUndefinedChanges().isEmpty()
                || !sig.getPdfVisualDifferenceConcernedPages().isEmpty()
                || !sig.getPdfPageDifferenceConcernedPages().isEmpty()
                || !sig.getPdfAnnotationsOverlapConcernedPages().isEmpty()) {
            return "disallowed";
        }
        if (sig.arePdfObjectModificationsDetected() || sig.arePdfModificationsDetected()) {
            return "allowed";
        }
        return "none";
    }

    /** Předběžné mapování na hodnoty z §3 návrhu. */
    static String trustStatus(String integrity, boolean byteRangeSane, String modified, Indication indication,
            SubIndication sub, CertificateResult cert, boolean tlStale) {
        if ("invalid".equals(integrity) || !byteRangeSane || (sub != null && INVALID_SUBS.contains(sub))) {
            return "invalid";
        }
        if ("disallowed".equals(modified)) {
            return "modified";
        }
        if (sub != null && UNTRUSTED_SUBS.contains(sub)) {
            return "untrusted";
        }
        boolean euTl = cert != null && "eu-tl".equals(cert.trustSource());
        if (indication == Indication.TOTAL_PASSED) {
            if (euTl) {
                return tlStale || "unknown".equals(modified) ? "indeterminate" : "trusted";
            }
            // Test anchor nebo jiný zdroj: kryptograficky v pořádku, ale ne EU TL.
            return "integrity-only";
        }
        // Jen řetězec, který k EU TL vůbec nevede. *_NO_POE u řetězce z TL je nerozhodnuto, ne „nedůvěryhodné“.
        if (sub == SubIndication.NO_CERTIFICATE_CHAIN_FOUND && !euTl && "valid".equals(integrity)) {
            return "integrity-only";
        }
        return "indeterminate";
    }

    private static long byteRangeEnd(List<BigInteger> br) {
        return br == null || br.size() != 4 ? Long.MAX_VALUE : br.get(2).add(br.get(3)).longValue();
    }

    private static List<String> messages(List<Message> messages) {
        return messages == null ? List.of() : messages.stream().map(m -> m.getKey() + ": " + m.getValue()).toList();
    }

    static String sha256(byte[] data) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(data));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }

    public record ValidatorInfo(String engine, String version, String policyId, String policySha256,
            Instant tlLoadedAt, boolean tlStale) {
    }

    public record ValidationResult(ValidatorInfo validator, String documentSha256, long byteLength,
            List<SignatureResult> signatures, List<TimestampResult> documentTimestamps, long durationMs) {
    }

    public record SignatureResult(String fieldName, int revision, String subFilter, boolean isDocumentTimestamp,
            String kind, String integrity, Boolean coversWholeDocument, boolean byteRangeSane,
            String modifiedAfterSigning, CertificateResult certificate, TimestampResult timestamp,
            String indication, String subIndication, String qualification, String padesLevel, String trustStatus,
            List<String> adesErrors, List<String> adesWarnings, List<String> qualificationErrors,
            List<String> qualificationWarnings) {
    }

    public record CertificateResult(String subject, String issuer, String serial, String trustSource,
            boolean chainTrusted, String revocation, String qualified, Instant notBefore, Instant notAfter) {
    }

    public record TimestampResult(boolean present, Boolean valid, Boolean messageImprintOk, String tsaSubject,
            String tsaTrustSource, String tsaQualified, Instant genTime, String type, String indication,
            String subIndication) {
    }
}
