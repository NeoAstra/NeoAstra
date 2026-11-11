package com.neoastra.push.adm;

import com.amazon.device.messaging.ADMMessageReceiver;

/**
 * Takes the broadcasts of Amazon Device Messaging and hands them to the handler of the generation of ADM on the device: the job
 * of ADM 2 (Fire OS 7 and later), whose classes a device with ADM 1 does not have, or the intent service otherwise.
 */
public final class NeoAstraAdmMessageReceiver extends ADMMessageReceiver {
    // Its own job ID, which no other job of the application should take.
    static final int JOB_ID = 0x4e454f41;

    public NeoAstraAdmMessageReceiver() {
        super(NeoAstraAdmLegacyMessageHandler.class);
        boolean v2;
        try { Class.forName("com.amazon.device.messaging.ADMMessageHandlerJobBase"); v2 = true; }
        catch (ClassNotFoundException error) { v2 = false; }
        if (v2) registerJobServiceClass(NeoAstraAdmMessageHandler.class, JOB_ID);
    }
}
