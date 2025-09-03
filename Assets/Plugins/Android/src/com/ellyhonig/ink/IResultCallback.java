package com.ellyhonig.ink;

public interface IResultCallback {
    void onResult(String text);
    void onError(String message);
}

