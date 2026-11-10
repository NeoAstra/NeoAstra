package com.neoastra.push.adm;

import android.content.Context;
import android.content.Intent;

import com.amazon.device.messaging.ADMMessageHandlerJobBase;

/** Receives the registration IDs and the messages of Amazon Device Messaging as a job, on Fire OS 7 and later. */
public final class NeoAstraAdmMessageHandler extends ADMMessageHandlerJobBase {
    @Override protected void onMessage(Context context, Intent intent) { NeoAstraAdmPush.received(context, intent); }
    @Override protected void onRegistered(Context context, String registrationId) { NeoAstraAdmPush.registered(registrationId); }
    @Override protected void onUnregistered(Context context, String registrationId) { }
    @Override protected void onRegistrationError(Context context, String error) { NeoAstraAdmPush.registrationError(error); }
}
