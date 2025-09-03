package com.ellyhonig.ink;

import android.util.Log;

// Stubbed InkBridge with no ML Kit dependency to unblock Quest builds.
public class InkBridge {
    private static final String TAG = "InkBridge";

    private static final InkBridge INSTANCE = new InkBridge();
    public static InkBridge getInstance() { return INSTANCE; }

    private volatile boolean ready = false;
    private volatile IResultCallback callback = null;

    private InkBridge() {}

    public void init(String bcp47, IResultCallback cb) {
        this.callback = cb;
        // ML Ink disabled: keep not-ready and notify once for clarity
        ready = false;
        Log.i(TAG, "InkBridge.init called, but ML Ink is disabled in this build.");
        if (callback != null) callback.onError("ML Ink disabled in this build");
    }

    public boolean isReady() { return false; }

    public void beginStroke() { /* no-op */ }
    public void addPoint(float x, float y, long t) { /* no-op */ }
    public void endStroke() { /* no-op */ }

    public void recognize() {
        Log.i(TAG, "recognize() called, but ML Ink is disabled.");
        if (callback != null) callback.onResult("");
    }
}
