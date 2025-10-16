using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(AudioSource))]
public class AudioFeedbackManager : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private LevelManager levelManager;
    [SerializeField] private PhonemeManager phonemeManager;
    [SerializeField] private LetterTracingSystem tracingSystem;
    [SerializeField] private ObjectOfInterestManager objectManager;

    // Our AudioSource for playing feedback.
    private AudioSource audioSource;

    // Maps feedback “keys” to filenames in StreamingAssets/feedbackAudio
    private Dictionary<string, string> feedbackClips = new Dictionary<string, string>()
    {
        // For Phoneme Checking
        { "startrecording",  "startrecording.wav" },
        { "waiting",         "waiting.wav"        },
        { "incorrect",       "incorrect.wav"       },
        { "incorrect2",      "incorrect2.wav"      },
        { "correct",         "correct.wav"         },

        // For Trace Checking
        { "starttrace",      "starttrace.wav"      },
        { "endtrace",        "endtrace.wav"        },

        // For Object Placing
        { "grab",            "grab.wav"            },
        { "moveOn",          "moveOn.wav"          },
    };

    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();

        // If references are not assigned in Inspector, try to find them.
        if (!levelManager)      levelManager = FindObjectOfType<LevelManager>();
        if (!phonemeManager)    phonemeManager = FindObjectOfType<PhonemeManager>();
        if (!tracingSystem)     tracingSystem = FindObjectOfType<LetterTracingSystem>();
        if (!objectManager)     objectManager = FindObjectOfType<ObjectOfInterestManager>();
    }
     private void Start()
    {
        // -------- PHONEME MANAGER EVENTS --------
        // Called when the phoneme process starts: we might want “waiting” or “startrecording” here
        if (levelManager) 
            levelManager.OnPhonemeCheckStart += HandlePhonemeCheckStart;

        if (phonemeManager)
        {
            phonemeManager.OnPhonemeCorrect += HandlePhonemeCorrect;
            phonemeManager.OnPhonemeIncorrect += HandlePhonemeIncorrect;
            phonemeManager.OnPhonemeTriesExhausted += HandlePhonemeTriesExhausted;
        }

        // -------- TRACING EVENTS --------
        // Our system has OnTraceCompleted => can play “endtrace”
        if (tracingSystem)
            tracingSystem.OnTraceCompleted += HandleTraceCompleted;

        // -------- OBJECT MANAGER EVENTS --------
        // Suppose "OnObjectCollected" means "grab" or "moveOn"? We’ll treat it as "grab"
        if (objectManager)
            objectManager.OnObjectCollected += HandleObjectCollected;

        // If you want to play "starttrace" or "moveOn" upon mode changes,
        // you can either add an event in LevelManager, or hack it here by
        // checking SetGameMode calls. For clarity, we’ll assume
        // you have an event we can subscribe to (not in your original code, but a good idea).
        // Example (only works if you add OnGameModeChanged in LevelManager):
        // levelManager.OnGameModeChanged += OnGameModeChanged;
    }

    // ---------- PHONEME CHECKING HANDLERS ----------

    private void HandlePhonemeCheckStart()
    {
        // If the game is truly in PhonemeChecking, we can do a "waiting" or "startrecording" clip.
        if (levelManager.currentMode == LevelManager.GameMode.PhonemeChecking)
        {
            // Choose whichever you want to signal the beginning. 
            PlayFeedback("waiting");
        }
    }

    private void HandlePhonemeCorrect()
    {
        // Only play the "correct" clip if in phoneme mode
        if (levelManager.currentMode == LevelManager.GameMode.PhonemeChecking)
        {
            PlayFeedback("correct");
        }
    }

    private void HandlePhonemeIncorrect()
    {
        // If in phoneme mode, figure out if it’s first or second attempt
        if (levelManager.currentMode == LevelManager.GameMode.PhonemeChecking)
        {
            // If you’re using a bool in PhonemeManager to track secondAttempt,
            // you could read it here. Let’s do a simple guess:
            if (/*some check for second attempt??*/ false)
                PlayFeedback("incorrect2");
            else
                PlayFeedback("incorrect");
        }
    }

    private void HandlePhonemeTriesExhausted()
    {
        // If tries are exhausted, presumably "incorrect2" is happening.
        if (levelManager.currentMode == LevelManager.GameMode.PhonemeChecking)
        {
            PlayFeedback("incorrect2");
        }
    }

    // ---------- TRACING HANDLERS ----------

    private void HandleTraceCompleted()
    {
        // Only play "endtrace" if we’re in trace checking
        if (levelManager.currentMode == LevelManager.GameMode.TraceChecking)
        {
            PlayFeedback("endtrace");
        }
    }

    // If you want "starttrace" automatically when mode changes:
    // Just create a LevelManager event OnGameModeChanged(GameMode newMode)
    // and do something like:
    //
    // private void OnGameModeChanged(GameMode newMode)
    // {
    //     if (newMode == GameMode.TraceChecking) PlayFeedback("starttrace");
    // }

    // ---------- OBJECT PLACING HANDLERS ----------

    private void HandleObjectCollected()
    {
        // If we’re in object placing mode, play "grab" or "moveOn"
        if (levelManager.currentMode == LevelManager.GameMode.ObjectPlacing)
        {
            PlayFeedback("grab");
        }
    }

    // If you want “moveOn” after an object is placed, you’ll need an event
    // for that in ObjectOfInterestManager or LevelManager as well. Then call:
    // private void HandleObjectPlaced() { if (mode == ObjectPlacing) PlayFeedback("moveOn"); }

    // ---------- PLAYING AUDIO FROM STREAMING ASSETS ----------

    private void PlayFeedback(string key)
    {
        if (!feedbackClips.TryGetValue(key, out string filename))
        {
            Debug.LogWarning($"AudioFeedbackManager: No clip mapped for key '{key}'!");
            return;
        }

        // Build a full path to the WAV (or MP3/OGG) in StreamingAssets/feedbackAudio
        string filePath = System.IO.Path.Combine(Application.streamingAssetsPath, "feedbackAudio", filename);
        Debug.Log($"AudioFeedbackManager: Trying to play '{filePath}' for key '{key}'");

        // Start a coroutine to load and play the file
        StartCoroutine(LoadAndPlayAudio(filePath));
    }

    private IEnumerator LoadAndPlayAudio(string filePath)
    {
        // On most platforms, loading from StreamingAssets requires "file://"
        string uri = "file://" + filePath;

        using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.WAV))
        {
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip)
                {
                    // Clean up previous clip to prevent memory accumulation
                    if (audioSource.clip != null && audioSource.clip != clip)
                    {
                        DestroyAudioClipSafe(audioSource.clip);
                    }
                    
                    audioSource.clip = clip;
                    audioSource.Play();
                }
            }
            else
            {
                Debug.LogError($"AudioFeedbackManager: Failed to load audio: {req.error}");
            }
        }
    }

    // ---------- CLEANUP ----------

    private void OnDestroy()
    {
        // Unsubscribe from events to prevent memory leaks or null references
        if (levelManager)
        {
            levelManager.OnPhonemeCheckStart -= HandlePhonemeCheckStart;
        }

        if (phonemeManager)
        {
            phonemeManager.OnPhonemeCorrect -= HandlePhonemeCorrect;
            phonemeManager.OnPhonemeIncorrect -= HandlePhonemeIncorrect;
            phonemeManager.OnPhonemeTriesExhausted -= HandlePhonemeTriesExhausted;
        }

        if (tracingSystem)
        {
            tracingSystem.OnTraceCompleted -= HandleTraceCompleted;
        }

        if (objectManager)
        {
            objectManager.OnObjectCollected -= HandleObjectCollected;
        }
        
        // Clean up audio source clip to prevent memory leaks
        if (audioSource && audioSource.clip)
        {
            DestroyAudioClipSafe(audioSource.clip);
            audioSource.clip = null;
        }

        // Stop all coroutines to prevent memory leaks
        StopAllCoroutines();
    }

    private void DestroyAudioClipSafe(AudioClip clip)
    {
        if (!clip) return;
#if UNITY_EDITOR
        DestroyImmediate(clip, true);
#else
        Destroy(clip);
#endif
    }
}
