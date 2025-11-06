using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;   // for safe, lightweight reflection lookups

public class AudioManager : MonoBehaviour
{
    private static AudioManager _instance; // ensure single instance
    [Header("General SFX")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip popSound;
    [SerializeField] private AudioClip winSound;
    [SerializeField] private AudioClip munchSound;
    [SerializeField] private AudioClip incorrectSound;

    public event Action OnWinSoundPlayed;
    public event Action OnIncorrectSoundPlayed;

    [Header("Scene References")]
    [SerializeField] private LetterTracingSystem tracingSystem;
    [SerializeField] private ObjectOfInterestManager objManager;

    private DictationManager dictationManager;
    private PhonemeManager phonemeManager;
    private LevelManager levelManager;

    [Header("Phoneme Clips (Resources/phonemeAudio)")]
    [Tooltip("Folder under Resources that contains the letter wavs like 01_A, 02_B, ...")]
    [SerializeField] private string resourcesFolder = "phonemeAudio";
    [Tooltip("Preload all phoneme clips on start (recommended: tiny, avoids runtime loads).")]
    [SerializeField] private bool preloadPhonemes = true;

    // A-Z map
    private readonly Dictionary<char, AudioClip> letterClips = new Dictionary<char, AudioClip>(26);

    // cooldown for spammy win sounds
    private float winSoundCooldown = 0.4f;
    private float lastWinSoundTime;

    // keep the exact delegate we subscribe with (works whether it's Action, Action<string>, Action<char>)
    private Delegate dictationStartSubscription;

    private void Awake()
    {
        // Singleton guard: keep only one AudioManager alive
        if (_instance && _instance != this)
        {
            Destroy(this);
            return;
        }
        _instance = this;

        if (!audioSource)
            audioSource = gameObject.GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();

        if (preloadPhonemes)
            PreloadPhonemeClips();
    }

    private void Start()
    {
        objManager = objManager ? objManager : GetComponent<ObjectOfInterestManager>();
        tracingSystem = tracingSystem ? tracingSystem : GetComponent<LetterTracingSystem>();
        dictationManager = GetComponent<DictationManager>();
        phonemeManager = FindObjectOfType<PhonemeManager>();
        levelManager = FindObjectOfType<LevelManager>();

        if (tracingSystem != null)
        {
            tracingSystem.OnKeyframeReached += PlayPopSound;
            tracingSystem.OnTraceCompleted  += PlayWinSound;
        }
        else Debug.LogError("[AudioManager] LetterTracingSystem missing.");

        if (objManager != null)
            objManager.OnHMDProximity += PlayMunchSound;

        if (phonemeManager != null)
        {
            phonemeManager.OnPhonemeCorrect        += PlayWinSound;
            phonemeManager.OnPhonemeIncorrect      += OnPhonemeIncorrectHandler;
            phonemeManager.OnPhonemeTriesExhausted += OnPhonemeTriesExhaustedHandler;
        }
        else Debug.LogError("[AudioManager] PhonemeManager missing.");

        if (dictationManager != null)
        {
            dictationManager.OnLetterCorrect    += PlayWinSound;
            dictationManager.OnLetterIncorrect  += OnPhonemeIncorrectHandler;

            // subscribe to OnDictationStart regardless of the delegate signature (Action/Action<string>/Action<char>)
            SubscribeDictationStart(dictationManager);
        }
        else Debug.LogError("[AudioManager] DictationManager missing.");

        if (levelManager == null) Debug.LogError("[AudioManager] LevelManager missing.");

        lastWinSoundTime = -winSoundCooldown;
    }

    /* -----------------------------------------------------------
     * Phoneme clip loading (memory-safe: load once, reuse)
     * ---------------------------------------------------------*/
    private void PreloadPhonemeClips()
    {
        letterClips.Clear();
        var clips = Resources.LoadAll<AudioClip>(resourcesFolder);
        if (clips == null || clips.Length == 0)
        {
            Debug.LogWarning($"[AudioManager] No clips found under Resources/{resourcesFolder}.");
            return;
        }

        foreach (var clip in clips)
        {
            if (!clip) continue;
            if (TryExtractLetterFromName(clip.name, out char letter))
            {
                letter = char.ToUpperInvariant(letter);
                if (!letterClips.ContainsKey(letter))
                    letterClips[letter] = clip;
            }
        }
        // Optional sanity log:
        // Debug.Log($"[AudioManager] Preloaded {letterClips.Count} phoneme clips.");
    }

    private static bool TryExtractLetterFromName(string name, out char letter)
    {
        // Works with names like "01_A", "A", "A_sound", etc.
        letter = '\0';
        if (string.IsNullOrEmpty(name)) return false;

        for (int i = name.Length - 1; i >= 0; i--)
        {
            char c = name[i];
            if (c >= 'A' && c <= 'Z') { letter = c; return true; }
            if (c >= 'a' && c <= 'z') { letter = char.ToUpperInvariant(c); return true; }
        }
        return false;
    }

    private AudioClip GetClipForLetter(char letter)
    {
        letter = char.ToUpperInvariant(letter);

        if (letterClips.Count == 0)
            PreloadPhonemeClips();

        if (letterClips.TryGetValue(letter, out var clip))
            return clip;

        return null;
    }

    /* -----------------------------------------------------------
     * DictationStart subscription (signature-agnostic)
     * ---------------------------------------------------------*/
    private void SubscribeDictationStart(object dm)
    {
        if (dm == null) return;

        var evt = dm.GetType().GetEvent("OnDictationStart",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (evt == null)
        {
            Debug.LogWarning("[AudioManager] DictationManager has no OnDictationStart event.");
            return;
        }

        var handlerType = evt.EventHandlerType;
        var invoke = handlerType.GetMethod("Invoke");
        var parms = invoke.GetParameters();

        // Support common shapes: Action(), Action<string>, Action<char>
        if (parms.Length == 0)
        {
            dictationStartSubscription = (Action)OnDictationStart_NoArgs;
        }
        else if (parms.Length == 1)
        {
            var pType = parms[0].ParameterType;
            if (pType == typeof(string))
                dictationStartSubscription = (Action<string>)OnDictationStart_String;
            else if (pType == typeof(char))
                dictationStartSubscription = (Action<char>)OnDictationStart_Char;
            else
            {
                Debug.LogWarning($"[AudioManager] Unsupported OnDictationStart signature: {pType.Name}");
                return;
            }
        }
        else
        {
            Debug.LogWarning("[AudioManager] Unsupported OnDictationStart signature (>=2 params).");
            return;
        }

        evt.AddEventHandler(dm, dictationStartSubscription);
    }

    private void UnsubscribeDictationStart(object dm)
    {
        if (dm == null || dictationStartSubscription == null) return;

        var evt = dm.GetType().GetEvent("OnDictationStart",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (evt != null)
            evt.RemoveEventHandler(dm, dictationStartSubscription);

        dictationStartSubscription = null;
    }

    /* -----------------------------------------------------------
     * DictationStart handlers
     * ---------------------------------------------------------*/
    private void OnDictationStart_NoArgs()
    {
        if (dictationManager && dictationManager.IsWordDictationActive)
            return;
        // Prefer authoritative LevelManager letter to avoid stale reflection reads
        if (levelManager != null && !string.IsNullOrEmpty(levelManager.currentLetter))
        {
            PlayLetterClip(levelManager.currentLetter[0]);
            return;
        }
        // Fallback to reflection if LevelManager is not available
        var letterStr = TryGetLetterFrom(dictationManager) ?? TryGetLetterFrom(levelManager);
        if (!string.IsNullOrEmpty(letterStr)) PlayLetterClip(letterStr[0]);
        else Debug.LogWarning("[AudioManager] Could not determine current letter at dictation start.");
    }

    private void OnDictationStart_String(string letter)
    {
        if (dictationManager && dictationManager.IsWordDictationActive)
            return;
        var one = ExtractFirstAZ(letter);
        if (one != '\0') PlayLetterClip(one);
        else OnDictationStart_NoArgs();
    }

    private void OnDictationStart_Char(char letter)
    {
        if (dictationManager && dictationManager.IsWordDictationActive)
            return;
        PlayLetterClip(letter);
    }

    public void PlayCurrentLetterPronunciation()
    {
        OnDictationStart_NoArgs();
    }

    public bool TryPlayCurrentLetterPronunciation()
    {
        bool success = false;

        if (levelManager != null && !string.IsNullOrEmpty(levelManager.currentLetter))
        {
            success = TryPlayLetterPronunciation(levelManager.currentLetter[0]);
            if (success) return true;
        }

        var letterStr = TryGetLetterFrom(dictationManager) ?? TryGetLetterFrom(levelManager);
        if (!string.IsNullOrEmpty(letterStr))
            success = TryPlayLetterPronunciation(letterStr[0]);

        if (!success)
            Debug.LogWarning("[AudioManager] TryPlayCurrentLetterPronunciation could not determine a letter to play.");

        return success;
    }

    public bool TryPlayLetterPronunciation(char letter)
    {
        if (letter == '\0')
            return false;

        var clip = GetClipForLetter(letter);
        if (clip == null)
            return false;

        PlayLetterClip(letter);
        return true;
    }

    private void PlayLetterClip(char letter)
    {
        var clip = GetClipForLetter(letter);
        if (clip != null)
        {
            // Ensure we don't overlap previous letter audio
            if (!audioSource) audioSource = gameObject.GetComponent<AudioSource>() ?? gameObject.AddComponent<AudioSource>();
            audioSource.Stop();
            audioSource.clip = clip;
            audioSource.Play();
        }
        else
        {
            Debug.LogWarning($"[AudioManager] No clip found for letter '{char.ToUpperInvariant(letter)}'. " +
                              $"Put a file like '01_{char.ToUpperInvariant(letter)}.wav' in Resources/{resourcesFolder}.");
        }
    }

    private static char ExtractFirstAZ(string s)
    {
        if (string.IsNullOrEmpty(s)) return '\0';
        foreach (var c in s)
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                return char.ToUpperInvariant(c);
        return '\0';
    }

    private string TryGetLetterFrom(object obj)
    {
        if (obj == null) return null;

        // Try common field/property names youGve used before
        string[] candidates = {
            "currentSound","CurrentSound",
            "currentLetter","CurrentLetter",
            "targetLetter","TargetLetter",
            "expected","Expected"
        };

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var name in candidates)
        {
            var fi = obj.GetType().GetField(name, flags);
            if (fi != null)
            {
                var v = fi.GetValue(obj);
                var s = v?.ToString();
                var c = ExtractFirstAZ(s);
                if (c != '\0') return c.ToString();
            }

            var pi = obj.GetType().GetProperty(name, flags);
            if (pi != null && pi.CanRead)
            {
                var v = pi.GetValue(obj, null);
                var s = v?.ToString();
                var c = ExtractFirstAZ(s);
                if (c != '\0') return c.ToString();
            }
        }
        return null;
    }

    /* -----------------------------------------------------------
     * Basic SFX helpers
     * ---------------------------------------------------------*/
    private void PlaySound(AudioClip clip)
    {
        if (audioSource && clip)
            audioSource.PlayOneShot(clip);   // one-shot = no extra AudioSources, no leaks
        else
            Debug.LogWarning("[AudioManager] AudioSource or clip is null.");
    }

    private void PlayPopSound() => PlaySound(popSound);

    // Public wrapper so other systems (e.g., Dictation replay) can trigger pops per point
    public void PlayPop() => PlaySound(popSound);

    public void PlaySoftCorrectChime() => PlaySound(winSound);

    public void PlaySoftIncorrectChime() => PlaySound(incorrectSound);

    private void PlayWinSound()
    {
        if (Time.time >= lastWinSoundTime + winSoundCooldown)
        {
            PlaySound(winSound);
            lastWinSoundTime = Time.time;
            OnWinSoundPlayed?.Invoke();
        }
    }

    private void PlayMunchSound() => PlaySound(munchSound);

    private void OnPhonemeIncorrectHandler()
    {
        PlaySound(incorrectSound);
        OnIncorrectSoundPlayed?.Invoke();
    }

    private void OnPhonemeTriesExhaustedHandler()
    {
        // still use popSound on exhaustion
        PlayPopSound();
    }

    private void OnDisable()
    {
        if (tracingSystem != null)
        {
            tracingSystem.OnKeyframeReached -= PlayPopSound;
            tracingSystem.OnTraceCompleted  -= PlayWinSound;
        }

        if (objManager != null)
            objManager.OnHMDProximity -= PlayMunchSound;

        if (phonemeManager != null)
        {
            phonemeManager.OnPhonemeCorrect        -= PlayWinSound;
            phonemeManager.OnPhonemeIncorrect      -= OnPhonemeIncorrectHandler;
            phonemeManager.OnPhonemeTriesExhausted -= OnPhonemeTriesExhaustedHandler;
        }

        if (dictationManager != null)
        {
            dictationManager.OnLetterCorrect   -= PlayWinSound;
            dictationManager.OnLetterIncorrect -= OnPhonemeIncorrectHandler;
            UnsubscribeDictationStart(dictationManager);
        }
    }
}
