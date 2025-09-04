package org.donquixote.letterdojo;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context c, Intent i) {
        if (Intent.ACTION_BOOT_COMPLETED.equals(i.getAction())) {
            Intent L = c.getPackageManager().getLaunchIntentForPackage(c.getPackageName());
            if (L != null) {
                // FLAG_ACTIVITY_NEW_TASK | FLAG_ACTIVITY_CLEAR_TOP
                L.addFlags(0x10000000 | 0x00080000);
                c.startActivity(L);
            }
        }
    }
}

