package com.neoastra.passkeys;

import java.util.function.Supplier;

/** Selects passkeys support without Android types, so a plain JVM checks the API/provider matrix. */
final class NeoAstraPasskeysSupport {
    static String provider(int apiLevel, boolean systemProvider, Supplier<String> declaredProvider) {
        // A provider may serve passwords before Android 9, but passkeys need API 28.
        if (apiLevel < 28) return null;
        if (apiLevel >= 34 && systemProvider) return "system";
        return declaredProvider.get();
    }
}
