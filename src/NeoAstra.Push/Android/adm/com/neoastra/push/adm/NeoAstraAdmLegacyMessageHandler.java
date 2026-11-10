package com.neoastra.push.adm;

import android.content.Intent;

import com.amazon.device.messaging.ADMMessageHandlerBase;

/** Receives the registration IDs and the messages of Amazon Device Messaging as an intent service, before Fire OS 7. */
public final class NeoAstraAdmLegacyMessageHandler extends ADMMessageHandlerBase {
    public NeoAstraAdmLegacyMessageHandler() { super("NeoAstraAdmLegacyMessageHandler"); }

    @Override protected void onMessage(Intent intent) { NeoAstraAdmPush.received(this, intent); }
    @Override protected void onRegistered(String registrationId) { NeoAstraAdmPush.registered(registrationId); }
    @Override protected void onUnregistered(String registrationId) { }
    @Override protected void onRegistrationError(String error) { NeoAstraAdmPush.registrationError(error); }
}
