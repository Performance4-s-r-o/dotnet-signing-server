package eu.performance4.validator;

import eu.performance4.validator.net.GuardedDataLoader;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;

@Configuration
public class ValidatorConfig {

    @Bean
    GuardedDataLoader guardedDataLoader(
            @Value("${validator.net.connect-timeout-ms:3000}") int connectTimeoutMs,
            @Value("${validator.net.read-timeout-ms:5000}") int readTimeoutMs,
            @Value("${validator.net.total-deadline-ms:15000}") long totalDeadlineMs,
            @Value("${validator.net.max-bytes:20971520}") int maxBytes) {
        return new GuardedDataLoader(connectTimeoutMs, readTimeoutMs, totalDeadlineMs, maxBytes);
    }
}
