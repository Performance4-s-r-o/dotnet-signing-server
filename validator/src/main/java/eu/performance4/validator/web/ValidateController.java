package eu.performance4.validator.web;

import eu.performance4.validator.net.GuardedDataLoader;
import eu.performance4.validator.trust.TrustedListService;
import eu.performance4.validator.validation.ValidationService;
import jakarta.servlet.http.HttpServletRequest;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.Semaphore;
import java.util.concurrent.TimeUnit;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class ValidateController {

    private static final Logger LOG = LoggerFactory.getLogger(ValidateController.class);

    private final ValidationService validation;
    private final TrustedListService trustedLists;
    private final Semaphore slots;
    private final int maxBytes;

    public ValidateController(ValidationService validation, TrustedListService trustedLists,
            @Value("${validator.max-concurrency:2}") int maxConcurrency,
            @Value("${validator.max-document-bytes:26214400}") int maxBytes) {
        this.validation = validation;
        this.trustedLists = trustedLists;
        this.slots = new Semaphore(maxConcurrency, true);
        this.maxBytes = maxBytes;
    }

    /** Tělo je PDF binárně; nic se neukládá na disk. */
    @PostMapping(path = "/validate", consumes = {"application/pdf", "application/octet-stream"})
    public ResponseEntity<?> validate(HttpServletRequest request) throws IOException, InterruptedException {
        byte[] pdf = readCapped(request.getInputStream());
        if (pdf == null) {
            return error(HttpStatus.PAYLOAD_TOO_LARGE, "DOCUMENT_TOO_LARGE");
        }
        if (pdf.length < 5 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F') {
            return error(HttpStatus.UNPROCESSABLE_ENTITY, "NOT_A_PDF");
        }
        if (!slots.tryAcquire(10, TimeUnit.SECONDS)) {
            return error(HttpStatus.SERVICE_UNAVAILABLE, "VALIDATOR_BUSY");
        }
        try {
            return ResponseEntity.ok(validation.validate(pdf));
        } catch (RuntimeException e) {
            LOG.warn("Validation failed: {}", e.toString());
            return error(HttpStatus.UNPROCESSABLE_ENTITY, "UNPARSEABLE_DOCUMENT");
        } finally {
            slots.release();
        }
    }

    @GetMapping("/health")
    public Map<String, Object> health() {
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("status", "up");
        body.put("validator", validation.info());
        body.put("trustedLists", trustedLists.status());
        body.put("blockedFetches", GuardedDataLoader.BLOCKED.get());
        return body;
    }

    private byte[] readCapped(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = in.read(buffer)) != -1) {
            if (out.size() + read > maxBytes) {
                return null;
            }
            out.write(buffer, 0, read);
        }
        return out.toByteArray();
    }

    private static ResponseEntity<Map<String, String>> error(HttpStatus status, String code) {
        return ResponseEntity.status(status).body(Map.of("error", code));
    }
}
