package eu.performance4.validator.trust;

import eu.europa.esig.dss.model.tsl.LOTLInfo;
import eu.europa.esig.dss.model.tsl.TLInfo;
import eu.europa.esig.dss.model.tsl.TLParsingInfoRecord;
import eu.europa.esig.dss.model.tsl.TLValidationJobSummary;
import eu.europa.esig.dss.service.http.commons.FileCacheDataLoader;
import eu.europa.esig.dss.spi.client.http.IgnoreDataLoader;
import eu.europa.esig.dss.spi.tsl.TrustedListsCertificateSource;
import eu.europa.esig.dss.spi.x509.KeyStoreCertificateSource;
import eu.europa.esig.dss.tsl.function.GrantedOrRecognizedAtNationalLevelTrustAnchorPeriodPredicate;
import eu.europa.esig.dss.tsl.function.OfficialJournalSchemeInformationURI;
import eu.europa.esig.dss.tsl.job.TLValidationJob;
import eu.europa.esig.dss.tsl.sha2.Sha2FileCacheDataLoader;
import eu.europa.esig.dss.tsl.source.LOTLSource;
import eu.europa.esig.dss.tsl.sync.ExpirationAndSignatureCheckStrategy;
import eu.performance4.validator.net.GuardedDataLoader;
import jakarta.annotation.PostConstruct;
import jakarta.annotation.PreDestroy;
import java.io.File;
import java.io.IOException;
import java.nio.file.Files;
import java.time.Duration;
import java.time.Instant;
import java.util.Date;
import java.util.List;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.stereotype.Service;

/**
 * EU LOTL a národní TL. Start vždy z cache ve volume (offline), online obnova
 * běží na pozadí jen při {@code VALIDATOR_TL_MODE=online}. Bez sítě se jede z
 * cache a {@link #status()} to hlásí přes {@code tlStale}.
 */
@Service
public class TrustedListService {

    private static final Logger LOG = LoggerFactory.getLogger(TrustedListService.class);
    private static final String MARKER = ".last-online-refresh";

    private final TrustedListsCertificateSource certificateSource = new TrustedListsCertificateSource();
    private final ScheduledExecutorService scheduler = Executors.newSingleThreadScheduledExecutor(r -> {
        Thread t = new Thread(r, "tl-refresh");
        t.setDaemon(true);
        return t;
    });

    @Value("${validator.tl.mode:offline}")
    private String mode;
    @Value("${validator.tl.cache-dir:/var/cache/validator/tl}")
    private String cacheDir;
    @Value("${validator.tl.lotl-url:https://ec.europa.eu/tools/lotl/eu-lotl.xml}")
    private String lotlUrl;
    @Value("${validator.tl.oj-url:https://eur-lex.europa.eu/eli/C/2026/1944/oj}")
    private String ojUrl;
    @Value("${validator.tl.oj-keystore:/app/oj-keystore.p12}")
    private String ojKeystore;
    @Value("${validator.tl.oj-keystore-password:dss-password}")
    private String ojKeystorePassword;
    @Value("${validator.tl.refresh-hours:6}")
    private long refreshHours;
    @Value("${validator.tl.max-age-hours:24}")
    private long maxAgeHours;

    private final GuardedDataLoader dataLoader;
    private TLValidationJob job;
    private volatile Instant lastOnlineRefresh;
    private volatile Duration lastOnlineRefreshDuration;
    private volatile Duration offlineLoadDuration;
    private volatile String lastError;

    public TrustedListService(GuardedDataLoader dataLoader) {
        this.dataLoader = dataLoader;
    }

    @PostConstruct
    void start() throws IOException {
        File dir = new File(cacheDir);
        if (!dir.mkdirs() && !dir.isDirectory()) {
            throw new IOException("TL cache directory not usable: " + dir);
        }
        job = buildJob(dir);
        lastOnlineRefresh = readMarker(dir);

        long t0 = System.nanoTime();
        job.offlineRefresh();
        offlineLoadDuration = Duration.ofNanos(System.nanoTime() - t0);
        LOG.info("TL offline load from cache: {} ms, {} trusted certificates",
                offlineLoadDuration.toMillis(), certificateSource.getNumberOfCertificates());

        if ("online".equalsIgnoreCase(mode)) {
            scheduler.scheduleWithFixedDelay(this::onlineRefresh, 0, refreshHours, TimeUnit.HOURS);
        }
    }

    @PreDestroy
    void stop() {
        scheduler.shutdownNow();
    }

    private TLValidationJob buildJob(File dir) throws IOException {
        LOTLSource lotl = new LOTLSource();
        lotl.setUrl(lotlUrl);
        lotl.setCertificateSource(new KeyStoreCertificateSource(new File(ojKeystore), "PKCS12",
                ojKeystorePassword.toCharArray()));
        lotl.setPivotSupport(true);
        lotl.setSigningCertificatesAnnouncementPredicate(new OfficialJournalSchemeInformationURI(ojUrl));
        lotl.setTrustAnchorValidityPredicate(new GrantedOrRecognizedAtNationalLevelTrustAnchorPeriodPredicate());
        lotl.setTLVersions(List.of(6));

        FileCacheDataLoader offline = new FileCacheDataLoader();
        offline.setCacheExpirationTime(-1);
        offline.setDataLoader(new IgnoreDataLoader());
        offline.setFileCacheDirectory(dir);

        FileCacheDataLoader online = new FileCacheDataLoader();
        online.setCacheExpirationTime(-1);
        online.setDataLoader(dataLoader);
        online.setFileCacheDirectory(dir);

        // Neplatný nebo prošlý seznam se nesynchronizuje: radši žádná důvěra než podvržená.
        ExpirationAndSignatureCheckStrategy strategy = new ExpirationAndSignatureCheckStrategy();
        strategy.setAcceptExpiredListOfTrustedLists(false);
        strategy.setAcceptInvalidListOfTrustedLists(false);
        strategy.setAcceptExpiredTrustedList(false);
        strategy.setAcceptInvalidTrustedList(false);

        TLValidationJob j = new TLValidationJob();
        j.setTrustedListCertificateSource(certificateSource);
        j.setListOfTrustedListSources(lotl);
        j.setOfflineDataLoader(offline);
        j.setOnlineDataLoader(Sha2FileCacheDataLoader.initSha2DailyUpdateDataLoader(online));
        j.setSynchronizationStrategy(strategy);
        return j;
    }

    /** Veřejné kvůli měření ve spiku; v provozu jen z plánovače. */
    public synchronized void onlineRefresh() {
        long t0 = System.nanoTime();
        try {
            job.onlineRefresh();
            lastOnlineRefreshDuration = Duration.ofNanos(System.nanoTime() - t0);
            Status s = status();
            if (s.lotlValid()) {
                lastOnlineRefresh = Instant.now();
                writeMarker(new File(cacheDir), lastOnlineRefresh);
                lastError = null;
            } else {
                lastError = "LOTL not valid after refresh";
            }
            LOG.info("TL online refresh: {} ms, {} TLs, {} trusted certificates, LOTL valid={}",
                    lastOnlineRefreshDuration.toMillis(), s.tlCount(), s.trustedCertificates(), s.lotlValid());
        } catch (RuntimeException e) {
            lastError = e.getClass().getSimpleName() + ": " + e.getMessage();
            LOG.warn("TL online refresh failed", e);
        }
    }

    public TrustedListsCertificateSource certificateSource() {
        return certificateSource;
    }

    public Status status() {
        // Z certificate source, ne z job.getSummary(): to čeká na zámek běžící obnovy.
        TLValidationJobSummary summary = certificateSource.getSummary();
        Date lotlNextUpdate = null;
        boolean lotlValid = false;
        int tlCount = 0;
        int tlUsable = 0;
        int tlExpired = 0;
        Date now = new Date();
        if (summary != null) {
            for (LOTLInfo lotl : summary.getLOTLInfos()) {
                if (lotl.getValidationCacheInfo() != null && lotl.getValidationCacheInfo().isValid()) {
                    lotlValid = true;
                }
                if (lotl.getParsingCacheInfo() instanceof TLParsingInfoRecord p && p.getNextUpdateDate() != null) {
                    lotlNextUpdate = p.getNextUpdateDate();
                }
                for (TLInfo tl : lotl.getTLInfos()) {
                    tlCount++;
                    if (tl.getValidationCacheInfo() != null && tl.getValidationCacheInfo().isValid()) {
                        tlUsable++;
                    }
                    if (tl.getParsingCacheInfo() instanceof TLParsingInfoRecord p
                            && p.getNextUpdateDate() != null && p.getNextUpdateDate().before(now)) {
                        tlExpired++;
                    }
                }
            }
        }
        Instant loadedAt = latest(lastOnlineRefresh, cachedLotlTime());
        boolean tooOld = loadedAt == null
                || Duration.between(loadedAt, Instant.now()).toHours() >= maxAgeHours;
        boolean nextUpdatePassed = lotlNextUpdate == null || lotlNextUpdate.before(now);
        // Zastaralé = nikdy nenačteno, LOTL neplatný, LOTL po NextUpdate, nebo bez úspěšné obnovy déle než max-age.
        boolean stale = !lotlValid || nextUpdatePassed || tooOld;
        return new Status(mode, loadedAt, stale, lotlValid,
                lotlNextUpdate == null ? null : lotlNextUpdate.toInstant(),
                tlCount, tlUsable, tlExpired, certificateSource.getNumberOfCertificates(),
                offlineLoadDuration == null ? null : offlineLoadDuration.toMillis(),
                lastOnlineRefreshDuration == null ? null : lastOnlineRefreshDuration.toMillis(),
                lastError);
    }

    /** Kdy byl LOTL v cache stažen (mtime souboru FileCacheDataLoaderu). */
    private Instant cachedLotlTime() {
        File f = new File(cacheDir, lotlUrl.replaceAll("[^a-zA-Z0-9]", "_"));
        return f.isFile() ? Instant.ofEpochMilli(f.lastModified()) : null;
    }

    private static Instant latest(Instant a, Instant b) {
        if (a == null) return b;
        if (b == null) return a;
        return a.isAfter(b) ? a : b;
    }

    private static Instant readMarker(File dir) {
        try {
            File f = new File(dir, MARKER);
            return f.isFile() ? Instant.parse(Files.readString(f.toPath()).trim()) : null;
        } catch (IOException | RuntimeException e) {
            return null;
        }
    }

    private static void writeMarker(File dir, Instant at) {
        try {
            Files.writeString(new File(dir, MARKER).toPath(), at.toString());
        } catch (IOException e) {
            LOG.warn("Cannot write TL refresh marker", e);
        }
    }

    public record Status(
            String mode,
            Instant tlLoadedAt,
            boolean tlStale,
            boolean lotlValid,
            Instant lotlNextUpdate,
            int tlCount,
            int tlUsable,
            int tlExpired,
            int trustedCertificates,
            Long offlineLoadMs,
            Long lastOnlineRefreshMs,
            String lastError) {
    }
}
