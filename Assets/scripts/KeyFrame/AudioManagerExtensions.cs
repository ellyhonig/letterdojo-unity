using UnityEngine;
using System.Reflection;

// Provides a safe PlayPop() extension on AudioManager.
// If AudioManager already has PlayPop(), the instance method is used.
// Otherwise, this defers to private PlayPopSound() or plays the pop clip via reflection.
public static class AudioManagerExtensions
{
    public static void PlayPop(this AudioManager audio)
    {
        if (audio == null) return;

        var type = audio.GetType();

        // 1) Prefer an existing instance method PlayPop() if defined
        var playPop = type.GetMethod(
            "PlayPop",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: System.Type.EmptyTypes,
            modifiers: null
        );
        if (playPop != null)
        {
            try { playPop.Invoke(audio, null); return; } catch { /* ignore and try fallbacks */ }
        }

        // 2) Try private helper PlayPopSound()
        var playPopSound = type.GetMethod(
            "PlayPopSound",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: System.Type.EmptyTypes,
            modifiers: null
        );
        if (playPopSound != null)
        {
            try { playPopSound.Invoke(audio, null); return; } catch { /* ignore and try last resort */ }
        }

        // 3) Last resort: reflect fields and play the pop clip directly
        var popClip = type.GetField("popSound", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(audio) as AudioClip;
        var srcObj = type.GetField("audioSource", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(audio);
        var src = srcObj as AudioSource ?? audio.GetComponent<AudioSource>();

        if (src != null && popClip != null)
        {
            src.PlayOneShot(popClip);
        }
        // If we can't access the clip/source, silently no-op to avoid breaking execution.
    }
}

