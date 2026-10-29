package eu.performance4.validator.net;

import java.net.Inet4Address;
import java.net.Inet6Address;
import java.net.InetAddress;

/**
 * Které adresy smí validátor kontaktovat. URL v certifikátu (AIA, CRL, OCSP) píše
 * útočník, takže vše mimo veřejný unicast se odmítá.
 */
public final class AddressPolicy {

    private AddressPolicy() {
    }

    public static boolean isPublic(InetAddress address) {
        if (address.isAnyLocalAddress()
                || address.isLoopbackAddress()
                || address.isLinkLocalAddress()
                || address.isSiteLocalAddress()
                || address.isMulticastAddress()) {
            return false;
        }
        if (address instanceof Inet4Address v4) {
            return isPublicV4(v4.getAddress());
        }
        if (address instanceof Inet6Address v6) {
            return isPublicV6(v6.getAddress());
        }
        return false;
    }

    private static boolean isPublicV4(byte[] a) {
        int b0 = a[0] & 0xff;
        int b1 = a[1] & 0xff;
        int b2 = a[2] & 0xff;
        if (b0 == 0 || b0 == 10 || b0 == 127) return false;
        if (b0 == 100 && b1 >= 64 && b1 <= 127) return false;    // CGNAT 100.64/10
        if (b0 == 169 && b1 == 254) return false;                // link-local, cloud metadata
        if (b0 == 172 && b1 >= 16 && b1 <= 31) return false;
        if (b0 == 192 && b1 == 168) return false;
        if (b0 == 192 && b1 == 0 && b2 == 0) return false;       // IETF protocol assignments
        if (b0 == 192 && b1 == 0 && b2 == 2) return false;       // TEST-NET-1
        if (b0 == 198 && (b1 == 18 || b1 == 19)) return false;   // benchmarking
        if (b0 == 198 && b1 == 51 && b2 == 100) return false;    // TEST-NET-2
        if (b0 == 203 && b1 == 0 && b2 == 113) return false;     // TEST-NET-3
        return b0 < 224;                                         // multicast + 240/4 + broadcast
    }

    private static boolean isPublicV6(byte[] a) {
        int b0 = a[0] & 0xff;
        if ((b0 & 0xfe) == 0xfc) return false;                   // ULA fc00::/7
        if (b0 == 0xfe && (a[1] & 0xc0) == 0xc0) return false;   // site-local fec0::/10
        // IPv4-mapped ::ffff:a.b.c.d, NAT64 64:ff9b::/96 a 6to4 2002::/16 nesou IPv4 uvnitř.
        boolean mapped = allZero(a, 0, 10) && (a[10] & 0xff) == 0xff && (a[11] & 0xff) == 0xff;
        boolean nat64 = (a[0] & 0xff) == 0x00 && (a[1] & 0xff) == 0x64 && (a[2] & 0xff) == 0xff
                && (a[3] & 0xff) == 0x9b && allZero(a, 4, 12);
        if (mapped || nat64) {
            return isPublicV4(new byte[] {a[12], a[13], a[14], a[15]});
        }
        if (b0 == 0x20 && (a[1] & 0xff) == 0x02) {
            return isPublicV4(new byte[] {a[2], a[3], a[4], a[5]});
        }
        if (allZero(a, 0, 12)) return false;                     // ::/96 včetně :: a ::1
        return (b0 & 0xe0) == 0x20;                              // jen globální unicast 2000::/3
    }

    private static boolean allZero(byte[] a, int from, int to) {
        for (int i = from; i < to; i++) {
            if (a[i] != 0) return false;
        }
        return true;
    }
}
