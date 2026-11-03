package com.neoastra.push;

import com.google.firebase.messaging.FirebaseMessagingService;
import com.google.firebase.messaging.RemoteMessage;

/** Receives the tokens of Firebase Cloud Messaging and the messages that reach the application, which the push service queues. */
public final class NeoAstraPushMessagingService extends FirebaseMessagingService {
    @Override public void onNewToken(String token) { NeoAstraPush.token(token); }

    @Override public void onMessageReceived(RemoteMessage message) { NeoAstraPush.received(this, message); }
}
