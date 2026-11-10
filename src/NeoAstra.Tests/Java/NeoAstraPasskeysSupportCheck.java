package com.neoastra.passkeys;

/** Checks the API/provider matrix on a plain JVM; AndroidJavaTests compiles and runs it. */
public final class NeoAstraPasskeysSupportCheck {
    public static void main(String[] arguments) {
        for (int api = 24; api < 28; api++) {
            // A Credential Manager provider can serve passwords on Android 7–8, but not passkeys.
            expect(null, provider(api, false, true), "API " + api + " with Play services");
            expect(null, provider(api, false, false), "API " + api + " without a provider");
            expect(null, NeoAstraPasskeysSupport.provider(api, true, () -> {
                throw new AssertionError("Android before API 28 must not look up a provider");
            }), "API " + api + " does not query providers");
        }
        for (int api = 28; api < 34; api++) {
            expect("play-services", provider(api, false, true), "API " + api + " with Play services");
            expect(null, provider(api, false, false), "API " + api + " without a provider");
        }
        for (int api = 34; api <= 36; api++) {
            expect("system", NeoAstraPasskeysSupport.provider(api, true, () -> {
                throw new AssertionError("The system provider must not query manifest providers");
            }), "API " + api + " with the system service");
            // Also covers the manifest fallback on TVs/cars and devices without the system service.
            expect("play-services", provider(api, false, true), "API " + api + " with only Play services");
            expect(null, provider(api, false, false), "API " + api + " without either provider");
        }
        System.out.println("NeoAstraPasskeysSupport: ok");
    }

    private static String provider(int api, boolean system, boolean playServices) {
        return NeoAstraPasskeysSupport.provider(api, system, () -> playServices ? "play-services" : null);
    }

    private static void expect(String expected, String actual, String message) {
        if (!java.util.Objects.equals(expected, actual)) {
            System.out.println("FAILED: " + message + ": expected " + expected + ", got " + actual);
            System.exit(1);
        }
    }
}
