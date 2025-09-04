package org.donquixote.letterdojo;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;

public class HomeAlias extends Activity {
    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        Intent i = getPackageManager().getLaunchIntentForPackage(getPackageName());
        if (i != null) {
            // FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TOP
            i.addFlags(0x10000000 | 0x00080000);
            startActivity(i);
        }
        finish();
    }
}

