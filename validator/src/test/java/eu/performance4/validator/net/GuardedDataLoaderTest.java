package eu.performance4.validator.net;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import eu.europa.esig.dss.spi.exception.DSSExternalResourceException;
import java.net.InetAddress;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

/** URL v certifikátu píše útočník: nic z toho nesmí vést do vnitřní sítě. */
class GuardedDataLoaderTest {

    private final GuardedDataLoader loader = new GuardedDataLoader(1000, 1000, 3000, 1024 * 1024);

    @ParameterizedTest
    @ValueSource(strings = {
        "127.0.0.1", "10.1.2.3", "172.16.0.1", "192.168.1.1", "169.254.169.254", "100.64.0.1",
        "0.0.0.0", "224.0.0.1", "255.255.255.255", "::1", "::", "fe80::1", "fd00::1",
        "::ffff:127.0.0.1", "::ffff:169.254.169.254", "64:ff9b::a9fe:a9fe", "2002:7f00:1::1"
    })
    void nonPublicAddressesAreRefused(String ip) throws Exception {
        assertFalse(AddressPolicy.isPublic(InetAddress.getByName(ip)), ip);
    }

    @ParameterizedTest
    @ValueSource(strings = {"8.8.8.8", "1.1.1.1", "2a00:1450:4001:80b::200e"})
    void publicAddressesAreAllowed(String ip) throws Exception {
        assertTrue(AddressPolicy.isPublic(InetAddress.getByName(ip)), ip);
    }

    @ParameterizedTest
    @ValueSource(strings = {
        "http://127.0.0.1:1/crl",
        "http://169.254.169.254/latest/meta-data/",
        "http://[::1]/ocsp",
        "http://localhost/ca.crt",
        "http://2130706433/",
        "ldap://127.0.0.1/cn=x",
        "file:///etc/passwd",
        "http://user:pw@example.com/"
    })
    void fetchToInternalTargetsFails(String url) {
        long before = GuardedDataLoader.BLOCKED.get();
        assertThrows(DSSExternalResourceException.class, () -> loader.get(url));
        assertTrue(GuardedDataLoader.BLOCKED.get() > before, url);
    }

    @Test
    void postToMetadataEndpointFails() {
        assertThrows(DSSExternalResourceException.class,
                () -> loader.post("http://169.254.169.254/ocsp", new byte[] {1, 2, 3}));
    }

    @Test
    void listVariantFailsWhenAllTargetsAreInternal() {
        assertThrows(DSSExternalResourceException.class,
                () -> loader.get(List.of("http://127.0.0.1/a", "http://10.0.0.1/b")));
    }
}
