using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;

[RequireComponent(typeof(LevelManager))]
public class PhonemeManager : MonoBehaviour
{
    public enum PhonemeCheckState
    { Idle, Start, WaitingToRecord, Recording, WaitingForResponse,
      Correct, Incorrect, EndIncorrect, EndCorrect }

    public enum BeamMode { BeamOn, MarkAnswersWrong, MarkAnswersCorrect }

    /* ---------- CONFIG ---------- */
    [Header("Beam Mode")]
    [SerializeField] private BeamMode beamMode = BeamMode.BeamOn;

    [Header("Level Manager Reference")]
    [SerializeField] public LevelManager levelManager;

    [Header("Proximity Button (VR)")]
    [SerializeField] public GameObject proximityButtonObject;
    private ProximityButton proximityButton;

    [Header("TextMeshPro for Feedback")]
    [SerializeField] private TextMeshPro feedbackText;

    [Header("Letter Prompt")]
    [SerializeField] private TMP_Text letterPrompt;
    [SerializeField] private GameObject letterPromptRoot;

    [Header("Beam API")]
    [SerializeField] private string API_URL = "https://recognize-3a64e01-v3.app.beam.cloud";
    [SerializeField] private string TOKEN   = "YOUR_BEAM_TOKEN";
    [NonSerialized] public string lastBeamText = "";
    [NonSerialized] public string lastBeamRawText = "";

    [Header("Remote Override")]
    [SerializeField] private bool useRemoteServer = false;
    [SerializeField] private string remoteAPIUrl = "http://108.46.76.56:8080/recognize";

    [Header("Mic Settings")]
    [SerializeField] private int maxRecordingSeconds = 15;
    [SerializeField] private int sampleRate = 16000; // 16 kHz = plenty for speech

    [Header("Auto-Start Alignment")]
    [Range(0f,1f)] public float alignmentThreshold = 0.7f;

    [Header("Auto-Stop Loudness")]
    public float amplitudeThreshold = 0.03f;
    public float loudEnoughTime = 0.18f;

    [Header("Ambient Noise Handling")]
    [SerializeField, Min(0.1f)] private float noiseSampleDuration = 0.5f;
    [SerializeField, Min(1.1f)] private float noiseDeltaMultiplier = 1.1f;

    [Header("Auto-Stop Look-Away")]
    public float lookAwayGrace = 0.2f;

    [Header("Silence Trimming (edges only)")]
    [SerializeField] private bool trimSilence = true;
    [SerializeField] private float silenceThreshold = 0.001f;

    [Header("Mic Status Indicators")]
    [SerializeField] private GameObject speakIndicator;
    [SerializeField] private GameObject waitIndicator;

    [Header("Utterance Progress")]
    [SerializeField] private GameObject[] utteranceCheckmarks = new GameObject[3];
    [SerializeField] private AudioManager audioManager;

    /* -> LENIENCY */
    [Header("Lenient-mode Settings")]
    [Tooltip("How many loud utterances before we send to Beam")]
    [SerializeField] private int utterancesRequired = 3;
    [Tooltip("Secs of relative silence before next utterance allowed")]
    [SerializeField] private float silenceGap = 0.25f;
    [Tooltip("Peak below this factor*ampThresh counts as silence")]
    [SerializeField] private float silenceAmpFactor = 0.5f;

    /* ---------- privates ---------- */
    private bool prevSpeak, prevWait;
    private Coroutine blinkCoroutine;

    public event Action OnPhonemeCorrect;
    public event Action OnPhonemeIncorrect;
    public event Action OnPhonemeTriesExhausted;

    private simplePlayer sPlayer;
    private Renderer btnRenderer;
    private Color btnColorOriginal;
    private Material btnMat; // instanced; we Destroy() it on cleanup

    // scratch buffers (REUSED to avoid GC/alloc churn)
    private float[] micBuf;             // live window buffer for loudness
    private float[] headScratch;        // SafeGetWindow head copy
    private float[] tailScratch;        // SafeGetWindow tail copy
    private float[] clipScratch;        // CopySegmentToMono temp (interleaved)
    private float[] recMonoScratch;     // up to max clip length (mono)

    private AudioClip recordedClip;     // per-take clip (not micClip)
    private AudioClip micClip;          // looping mic buffer
    private int startSample;
    private string micDevice;
    private Coroutine warmMicRoutine;
    private bool isMicWarming;

    private PhonemeCheckState state = PhonemeCheckState.Idle;
    public PhonemeCheckState currentState => state;

    private int attemptCount;
    private bool aligned, alignPressed;
    private float lookAwayTimer;
    private float loudTimer, peakThisClip;
    private bool autoStopped;

    /* utterance + silence gate */
    private int  utteranceCount;
    private bool waitingForSilence;
    private float silenceTimer;

    /* beam startup gate */
    private bool beamReady = false;

    private float ambientNoisePeak;
    private float calibratedAmplitudeDelta;
    private float currentAmplitudeThresholdValue;
    private bool suppressIndicators;
    private Coroutine noiseSampleRoutine;

    private string GetActiveApiUrl() => useRemoteServer ? remoteAPIUrl : API_URL;
    private bool ShouldSendAuthHeader() => !useRemoteServer && !string.IsNullOrEmpty(TOKEN);

    private static readonly Regex ipaFieldRegex    = new Regex("\"ipa\"\\s*:\\s*\"(?<value>.*?)\"", RegexOptions.Compiled);
    private static readonly Regex textFieldRegex   = new Regex("\"text\"\\s*:\\s*\"(?<value>.*?)\"", RegexOptions.Compiled);
    private static readonly Regex rawTextFieldRegex = new Regex("\"raw_text\"\\s*:\\s*\"(?<value>.*?)\"", RegexOptions.Compiled);
    public float AmbientNoiseBaseline => ambientNoisePeak;
    public float EffectiveAmplitudeThreshold => GetEffectiveAmplitudeThreshold();
    public float EffectiveSilenceThreshold => GetSilenceThreshold();

    /* ===================== INDICATOR GATE (mutex-style) ===================== */
    private sealed class IndicatorGate
    {
        private readonly Dictionary<string,int> speakWants = new();
        private readonly Dictionary<string,int> waitWants  = new();
        private uint seq; // recency
        private readonly Dictionary<string,uint> speakSeq = new();
        private readonly Dictionary<string,uint> waitSeq  = new();

        public void WantSpeak(string tag, bool on, int priority)
        {
            if (on) { speakWants[tag] = priority; speakSeq[tag] = ++seq; }
            else    { speakWants.Remove(tag); speakSeq.Remove(tag); }
        }
        public void WantWait(string tag, bool on, int priority)
        {
            if (on) { waitWants[tag] = priority; waitSeq[tag] = ++seq; }
            else    { waitWants.Remove(tag); waitSeq.Remove(tag); }
        }

        public void Apply(GameObject speakGO, GameObject waitGO)
        {
            bool anySpeak = speakWants.Count > 0;
            bool anyWait  = waitWants.Count  > 0;

            bool finalSpeak = false, finalWait = false;

            if (anySpeak && !anyWait) finalSpeak = true;
            else if (!anySpeak && anyWait) finalWait = true;
            else if (!anySpeak && !anyWait) { /* both off */ }
            else
            {
                int topS = speakWants.Values.Max();
                int topW = waitWants.Values.Max();
                if (topS != topW) finalSpeak = topS > topW;
                else
                {
                    uint sSeq = speakSeq.Values.Max();
                    uint wSeq = waitSeq.Values.Max();
                    if (sSeq != wSeq) finalSpeak = sSeq > wSeq;
                    else finalSpeak = true; // tie G�� Speak
                }
                finalWait = !finalSpeak;
            }

            if (speakGO && speakGO.activeSelf != finalSpeak) speakGO.SetActive(finalSpeak);
            if (waitGO  && waitGO.activeSelf  != finalWait ) waitGO.SetActive(finalWait);
        }
    }
    private readonly IndicatorGate indicatorGate = new IndicatorGate();

    /* ---------- PUBLIC ACCESSORS (safe, mutually exclusive) ---------- */
    public void SetSpeakIndicator(bool on, string tag = "external", int priority = 0)
    {
        if (suppressIndicators)
        {
            indicatorGate.WantSpeak(tag, false, priority);
            if (speakIndicator && speakIndicator.activeSelf) speakIndicator.SetActive(false);
            prevSpeak = speakIndicator && speakIndicator.activeSelf;
            return;
        }

        bool inPhonemeMode = levelManager != null && levelManager.currentMode == LevelManager.GameMode.PhonemeChecking;
        // Only allow enabling during phoneme checking
        indicatorGate.WantSpeak(tag, inPhonemeMode && on, priority);
        if (!inPhonemeMode)
        {
            if (speakIndicator && speakIndicator.activeSelf) speakIndicator.SetActive(false);
            if (waitIndicator  && waitIndicator.activeSelf)  waitIndicator.SetActive(false);
        }
        else
        {
            indicatorGate.Apply(speakIndicator, waitIndicator);
        }
        prevSpeak = speakIndicator && speakIndicator.activeSelf;
        prevWait  = waitIndicator  && waitIndicator.activeSelf;
    }

    public void SetWaitIndicator(bool on, string tag = "external", int priority = 0)
    {
        if (suppressIndicators)
        {
            indicatorGate.WantWait(tag, false, priority);
            if (waitIndicator && waitIndicator.activeSelf) waitIndicator.SetActive(false);
            prevWait = waitIndicator && waitIndicator.activeSelf;
            return;
        }

        bool inPhonemeMode = levelManager != null && levelManager.currentMode == LevelManager.GameMode.PhonemeChecking;
        // Only allow enabling during phoneme checking
        indicatorGate.WantWait(tag, inPhonemeMode && on, priority);
        if (!inPhonemeMode)
        {
            if (speakIndicator && speakIndicator.activeSelf) speakIndicator.SetActive(false);
            if (waitIndicator  && waitIndicator.activeSelf)  waitIndicator.SetActive(false);
        }
        else
        {
            indicatorGate.Apply(speakIndicator, waitIndicator);
        }
        prevSpeak = speakIndicator && speakIndicator.activeSelf;
        prevWait  = waitIndicator  && waitIndicator.activeSelf;
    }

    private void UpdateUtteranceProgressVisuals()
    {
        if (utteranceCheckmarks == null || utteranceCheckmarks.Length == 0) return;

        int visibleCount = Mathf.Clamp(utteranceCount, 0, utteranceCheckmarks.Length);
        for (int i = 0; i < utteranceCheckmarks.Length; i++)
        {
            GameObject mark = utteranceCheckmarks[i];
            if (!mark) continue;

            bool shouldShow = i < visibleCount;
            bool wasActive = mark.activeSelf;
            if (wasActive != shouldShow)
            {
                mark.SetActive(shouldShow);
                if (shouldShow && !wasActive)
                    PlayUtterancePop();
            }
        }
    }

    private void ClearUtteranceProgressVisuals()
    {
        utteranceCount = 0;
        if (utteranceCheckmarks == null || utteranceCheckmarks.Length == 0) return;

        for (int i = 0; i < utteranceCheckmarks.Length; i++)
        {
            GameObject mark = utteranceCheckmarks[i];
            if (mark && mark.activeSelf)
                mark.SetActive(false);
        }
    }

    private void PlayUtterancePop()
    {
        if (!audioManager)
            audioManager = FindObjectOfType<AudioManager>();

        audioManager?.PlayPop();
    }

    private static readonly Dictionary<char, string> phonemeCharFold = new();

    /* friendly spellings for remote recognizer */
    private readonly Dictionary<string, List<string>> letterToFriendlyPhonemes = new()
    {
        { "A", new()
            {
                "a", "ay", "aye", "ai", "ey", "hey",
                "ah", "uh", "eh",
                "long a", "short a"
            }
        },
        { "B", new()
            {
                "b", "bee", "be", "buh",
                "p", "pee", "peh"
            }
        },
        { "C", new()
            {
                "c", "see", "cee", "sea", "si",
                "k", "ck", "kee",
                "s", "ess"
            }
        },
        { "D", new()
            {
                "d", "dee", "de", "di",
                "t", "tee", "teh"
            }
        },
        { "E", new()
            {
                "e", "ee", "ie",
                "eat", "ih", "eh"
            }
        },
        { "F", new()
            {
                "f", "eff", "ef", "ph",
                "v", "vee"
            }
        },
        { "G", new()
            {
                "g", "gee", "jee", "ji",
                "j", "jay",
                "k", "kay"
            }
        },
        { "H", new()
            {
                "h", "aitch",
                "ha", "hah", "huh", "heh",
                "hhh", "breath"
            }
        },
        { "I", new()
            {
                "i", "eye", "aye", "ai",
                "ee", "ih", "ah"
            }
        },
        { "J", new()
            {
                "j", "jay", "gee",
                "g", "jee", "dge"
            }
        },
        { "K", new()
            {
                "k", "kay", "ke", "kuh",
                "c", "que", "key",
                "g"
            }
        },
        { "L", new()
            {
                "l", "ell", "el",
                "al", "ul", "ull"
            }
        },
        { "M", new()
            {
                "m", "em", "um", "mm",
                "n", "en"
            }
        },
        { "N", new()
            {
                "n", "en", "in", "an",
                "m", "em"
            }
        },
        { "O", new()
            {
                "o", "oh", "owe",
                "aw", "ah", "uh",
                "oo"
            }
        },
        { "P", new()
            {
                "p", "pee", "pe", "puh",
                "b", "bee"
            }
        },
        { "Q", new()
            {
                "q", "cue", "queue", "kyoo",
                "koo", "coo",
                "k"
            }
        },
        { "R", new()
            {
                "r", "ar", "are",
                "ahr", "er"
            }
        },
        { "S", new()
            {
                "s", "ess", "es",
                "z", "zee",
                "see"
            }
        },
        { "T", new()
            {
                "t", "tee", "ti", "tea",
                "d", "dee"
            }
        },
        { "U", new()
            {
                "u", "you", "yoo", "yew",
                "oo", "ooh",
                "uh"
            }
        },
        { "V", new()
            {
                "v", "vee", "ve",
                "f", "eff"
            }
        },
        { "W", new()
            {
                "w", "double u", "dubya", "dub you",
                "woo", "who"
            }
        },
        { "X", new()
            {
                "x", "ex", "eks",
                "ax", "acks", "ks"
            }
        },
        { "Y", new()
            {
                "y", "why", "wai", "wi",
                "ee", "yee", "yah"
            }
        },
        { "Z", new()
            {
                "z", "zee", "zed",
                "es", "ess", "s"
            }
        },
    };

    /* ultra-lenient IPA map */
    // UTF-8, super‑lenient guesses for letter → possible pronunciations (IPA + some practical shorthands).
// Intention: catch noisy inputs & L2 accents; includes letter names ("keɪ", "eks", "waɪ", "zɛd/ziː").
// Note: duplicates kept minimal; both tied and untied affricates included (t͡ʃ/tʃ, d͡ʒ/dʒ), plus spaced forms.
private readonly Dictionary<string, List<string>> letterToIPA = new()
{
    { "A", new()
        {
            "a", "æ", "ɑ", "ɑː", "ɒ", "ɔ", "ɔː",
            "e", "ɛ", "eɪ", "ə",
            "aɪ", "aj",
            "æɹ", "ɑɹ", "eə", "eɚ"
        }
    },
    { "B", new()
        {
            "b", "p",
            "bi", "biː", "bɪ", "b i", "b iː"
        }
    },
    { "C", new()
        {
            "k", "s",
            "t͡ʃ", "tʃ", "ʃ",
            "ts", "t͡s", "t s",
            "θ",  // lenient (Spanish C before e/i)
            "si", "siː", "s i", "s iː"
        }
    },
    { "D", new()
        {
            "d", "t", "ð", "ɾ",
            "di", "diː", "d i", "d iː"
        }
    },
    { "E", new()
        {
            "e", "eɪ",
            "i", "iː", "ɪ",
            "ɛ", "ə"
        }
    },
    { "F", new()
        {
            "f", "v",
            "ef", "ɛf"
        }
    },
    { "G", new()
        {
            "g", "ɡ", "k",
            "d͡ʒ", "dʒ", "ʒ",
            "dʒi", "dʒiː", "ɡi", "ɡiː",
            "g i", "g iː"
        }
    },
    { "H", new()
        {
            "h", "∅", // silent h
            "heɪtʃ", "eɪtʃ"
        }
    },
    { "I", new()
        {
            "i", "iː", "ɪ",
            "aɪ", "aj",
            "e", "ə"
        }
    },
    { "J", new()
        {
            "d͡ʒ", "dʒ", "ʒ", "j", // lenient: some L2 say /j/
            "dʒeɪ"
        }
    },
    { "K", new()
        {
            "k", "g", // lenient confusion
            "keɪ", "k eɪ"
        }
    },
    { "L", new()
        {
            "l", "ɫ", "l̩",
            "əl", "el", "ɛl"
        }
    },
    { "M", new()
        {
            "m", "ɱ", "m̩",
            "n", // lenient confusion
            "em", "ɛm", "əm"
        }
    },
    { "N", new()
        {
            "n", "ŋ", "n̩",
            "ɲ", // lenient
            "en", "ɛn", "ən"
        }
    },
    { "O", new()
        {
            "o", "oʊ", "əʊ",
            "ɒ", "ɔ", "ɔː", "ɑ",
            "aʊ", "ow", "ou" // shorthands commonly seen
        }
    },
    { "P", new()
        {
            "p", "b",
            "pi", "piː", "p i", "p iː"
        }
    },
    { "Q", new()
        {
            "k", "kw", "kʷ", "k w",
            "kju", "kjuː", "kjʊ",
            "q" // ultra-lenient fallback token
        }
    },
    { "R", new()
        {
            "ɹ", "r", "ɾ", "ɻ", "ɽ",
            "ʀ", "ʁ",
            "ɚ", "ɝ", "əɹ", "ɑɹ", "ɜː", "ar", "r̩"
        }
    },
    { "S", new()
        {
            "s", "z", "ʃ", "ʒ",
            "ts", "t͡s", // lenient for /s/→/ts/
            "es", "ɛs"
        }
    },
    { "T", new()
        {
            "t", "d", "ɾ", "ʔ",
            "t͡ʃ", "tʃ", "ʃ", // ti/tu/tion → /ʃ/
            "ts", "t s",
            "ti", "tiː"
        }
    },
    { "U", new()
        {
            "u", "uː", "ʊ",
            "ju", "juː",
            "ʌ", "ə", "a" // lenient for L2 confusions
        }
    },
    { "V", new()
        {
            "v", "f", "w", // common L2 swaps
            "vi", "viː", "v i", "v iː"
        }
    },
    { "W", new()
        {
            "w", "v", "ʍ",
            "u", // sometimes perceived as vowel
            "wu", "wʊ", // shorthands
            "dʌbəlju", "dʌbəljuː", "ˈdʌbəlju", "ˈdʌbəljuː"
        }
    },
    { "X", new()
        {
            "ks", "k s", "k͡s",
            "ɡz", "g z", "ɡ͡z",
            "z",
            "ɛks", "eks", "egz",
            "ik s", "i ks"
        }
    },
    { "Y", new()
        {
            "j",
            "i", "ɪ", "iː",
            "aɪ",
            "waɪ",
            "ji", "jiː", "j i"
        }
    },
    { "Z", new()
        {
            "z", "s", "dz", "d͡z",
            "zi", "ziː", "z i", "z iː",
            "zɛd", "zed"
        }
    },
};


    // Downmix a segment from micClip (handles wrap at call site) with REUSED buffer
    void CopySegmentToMono(int startFrame, int frames, float[] dst, int dstOffset)
    {
        if (micClip == null || frames <= 0) return;
        int ch = micClip.channels;
        int needSamples = frames * ch;

        if (clipScratch == null || clipScratch.Length < needSamples)
            clipScratch = new float[needSamples];

        micClip.GetData(clipScratch, startFrame);

        if (ch == 1)
        {
            Buffer.BlockCopy(clipScratch, 0, dst, dstOffset * sizeof(float), frames * sizeof(float));
            return;
        }

        int si = 0;
        for (int f = 0; f < frames; f++)
        {
            float acc = 0f;
            for (int c = 0; c < ch; c++) acc += clipScratch[si++];
            dst[dstOffset + f] = acc / ch;
        }
    }

    // Read a wrap-safe window from the circular mic buffer into dst (REUSES scratch arrays)
    bool SafeGetWindow(int offsetFrames, int sizeSamples, float[] dst)
    {
        if (micClip == null) return false;
        int ch = micClip.channels;
        int totalFrames = micClip.samples;

        if (sizeSamples <= 0 || sizeSamples > dst.Length) return false;

        int startFrame = offsetFrames % totalFrames;
        if (startFrame < 0) startFrame += totalFrames;

        int headFrames  = Math.Min(totalFrames - startFrame, sizeSamples / ch);
        int headSamples = headFrames * ch;
        int tailSamples = sizeSamples - headSamples;

        if (headSamples > 0)
        {
            if (headScratch == null || headScratch.Length < headSamples) headScratch = new float[headSamples];
            micClip.GetData(headScratch, startFrame);
            Buffer.BlockCopy(headScratch, 0, dst, 0, headSamples * sizeof(float));
        }

        if (tailSamples > 0)
        {
            if (tailScratch == null || tailScratch.Length < tailSamples) tailScratch = new float[tailSamples];
            micClip.GetData(tailScratch, 0);
            Buffer.BlockCopy(tailScratch, 0, dst, headSamples * sizeof(float), tailSamples * sizeof(float));
        }

        return true;
    }

    /* ---------- Unity lifecycle ---------- */
    private void Awake()
    {
        if (!levelManager) levelManager = GetComponent<LevelManager>();
        micDevice = Microphone.devices.Length > 0 ? Microphone.devices[0] : null;
        if (micDevice == null) Debug.LogError("PhonemeManager: No microphone detected");

        SetLetterPromptVisible(false);
        ClearUtteranceProgressVisuals();

        if (!audioManager)
            audioManager = FindObjectOfType<AudioManager>();

        if (proximityButtonObject)
        {
            proximityButton = proximityButtonObject.GetComponent<ProximityButton>();
            btnRenderer = proximityButtonObject.GetComponentInChildren<Renderer>(true);
            if (btnRenderer)
            {
                // instanced material (we'll Destroy it later)
                btnMat = btnRenderer.material;
                btnColorOriginal = btnMat.color;
            }
        }

        sPlayer = GetComponent<simplePlayer>();
        RefreshIndicators(true);

        if (levelManager != null)
        {
            levelManager.OnPhonemeCheckStart += () =>
            {
                RequestAmbientNoiseSample();
                SetState(PhonemeCheckState.Start);
            };
            levelManager.OnGameModeChanged += HandleModeChanged;
            levelManager.OnLetterChanged   += HandleLetterChanged;
            HandleModeChanged(levelManager.currentMode);
            HandleLetterChanged(levelManager.currentLetter);
        }
    }

    private void Start()
    {
        StartMicIfNeeded();

        ambientNoisePeak = 0f;
        calibratedAmplitudeDelta = amplitudeThreshold;
        currentAmplitudeThresholdValue = Mathf.Max(amplitudeThreshold, 0.001f);
        RequestAmbientNoiseSample();

        beamReady = beamMode != BeamMode.BeamOn;
        if (beamMode == BeamMode.BeamOn) StartCoroutine(WarmUpBeam());

        if (proximityButton)
        {
            proximityButton.OnButtonPressed += HandlePhysicalPress;
            proximityButton.OnButtonReleased += HandlePhysicalRelease;
        }
        SetSpeakIndicator(false);
        SetWaitIndicator(false);
    }

    private void Update()
    {
        HandleAlignment();
        HandleRecordingMonitors();
        RefreshIndicators();
    }

    /* ---------- Alignment ---------- */
    private void HandleAlignment()
    {
        if (sPlayer?.hmd == null || btnRenderer == null) return;
        Vector3 fwd = sPlayer.hmd.transform.forward;
        Vector3 dir = (proximityButtonObject.transform.position - sPlayer.hmd.transform.position).normalized;
        aligned = Vector3.Dot(fwd, dir) >= alignmentThreshold;
        if (state == PhonemeCheckState.Start || state == PhonemeCheckState.WaitingToRecord)
            SetBtnTint(aligned ? Color.green : btnColorOriginal);

        if ((state == PhonemeCheckState.Start || state == PhonemeCheckState.WaitingToRecord)
            && aligned && !alignPressed)
        {
            alignPressed = true;
            HandleVirtualPress();
        }

        if (state == PhonemeCheckState.WaitingToRecord && !aligned)
            alignPressed = false;

        if (state == PhonemeCheckState.Recording)
        {
            lookAwayTimer = aligned ? 0f : lookAwayTimer + Time.deltaTime;
            if (lookAwayTimer > lookAwayGrace)
            {
                CancelRecording("Alignment lost");
                return;
            }
        }
    }

    private float GetEffectiveAmplitudeThreshold()
    {
        if (currentAmplitudeThresholdValue <= 0f)
            currentAmplitudeThresholdValue = Mathf.Max(amplitudeThreshold, 0.001f);
        return currentAmplitudeThresholdValue;
    }

    private float GetSilenceThreshold()
    {
        float baseLine = ambientNoisePeak;
        float deltaForSilence = calibratedAmplitudeDelta > 0f ? calibratedAmplitudeDelta * silenceAmpFactor : amplitudeThreshold * silenceAmpFactor;
        float silenceLevel = baseLine + deltaForSilence;
        float effectiveScaled = GetEffectiveAmplitudeThreshold() * silenceAmpFactor;
        return Mathf.Max(baseLine, Mathf.Max(silenceLevel, effectiveScaled));
    }

    private void RequestAmbientNoiseSample()
    {
        if (!isActiveAndEnabled) return;
        if (noiseSampleRoutine != null)
            StopCoroutine(noiseSampleRoutine);
        noiseSampleRoutine = StartCoroutine(SampleAmbientNoise());
    }

    private IEnumerator SampleAmbientNoise()
    {
        suppressIndicators = true;
        indicatorGate.WantSpeak("noise", false, 0);
        indicatorGate.WantWait("noise", false, 0);
        RefreshIndicators(true);

        const float MIN_DURATION = 0.1f;
        float duration = Mathf.Max(noiseSampleDuration, MIN_DURATION);

        if (micClip == null || !Microphone.IsRecording(micDevice))
        {
            StartMicIfNeeded();
            float timeout = 2f;
            while ((micClip == null || !Microphone.IsRecording(micDevice)) && timeout > 0f)
            {
                timeout -= Time.unscaledDeltaTime;
                yield return null;
            }
            if (micClip == null || !Microphone.IsRecording(micDevice))
            {
                suppressIndicators = false;
                RefreshIndicators(true);
                noiseSampleRoutine = null;
                yield break;
            }
        }

        const int WIN = 1024;
        float elapsed = 0f;
        float peak = 0f;

        while (elapsed < duration)
        {
            if (micClip != null && Microphone.IsRecording(micDevice))
            {
                int ch = Mathf.Max(1, micClip.channels);
                int pos = Microphone.GetPosition(micDevice);
                if (pos >= WIN)
                {
                    int offset = pos - WIN;
                    if (offset < 0) offset += micClip.samples;
                    int needed = WIN * ch;
                    if (micBuf == null || micBuf.Length < needed) micBuf = new float[needed];
                    if (SafeGetWindow(offset, needed, micBuf))
                    {
                        float framePeak = 0f;
                        for (int i = 0; i < needed; i++)
                        {
                            float a = Mathf.Abs(micBuf[i]);
                            if (a > framePeak) framePeak = a;
                        }
                        if (framePeak > peak) peak = framePeak;
                    }
                }
            }
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        ambientNoisePeak = peak;
        float deltaMultiplier = Mathf.Max(noiseDeltaMultiplier, 1.1f);
        float minDelta = ambientNoisePeak * deltaMultiplier;
        calibratedAmplitudeDelta = Mathf.Max(amplitudeThreshold, minDelta);
        currentAmplitudeThresholdValue = Mathf.Max(ambientNoisePeak + calibratedAmplitudeDelta, Mathf.Max(amplitudeThreshold, 0.001f));

        suppressIndicators = false;
        RefreshIndicators(true);
        noiseSampleRoutine = null;

        Debug.Log($"PhonemeManager | Ambient noise baseline {ambientNoisePeak:F3}, trigger {currentAmplitudeThresholdValue:F3}");
    }
    /* ---------- Recording monitors ---------- */
    private void HandleRecordingMonitors()
    {
        if (state != PhonemeCheckState.Recording || autoStopped) return;
        if (micClip == null || micClip.channels == 0) return;
        if (!Microphone.IsRecording(micDevice)) return;

        const int WIN = 1024;
        int pos = Microphone.GetPosition(micDevice);
        if (pos < WIN) return;

        int offset = pos - WIN;
        if (offset < 0) offset += micClip.samples;
        int needed = WIN * micClip.channels;
        if (micBuf == null || micBuf.Length < needed) micBuf = new float[needed];
        if (!SafeGetWindow(offset, needed, micBuf)) return;

        float framePeak = 0f;
        // interleaved OK; peak is per-sample abs
        for (int i = 0; i < needed; i++)
        {
            float a = Mathf.Abs(micBuf[i]);
            if (a > framePeak) framePeak = a;
        }
        if (framePeak > peakThisClip) peakThisClip = framePeak;

        float effectiveThreshold = GetEffectiveAmplitudeThreshold();
        float silenceThreshold = GetSilenceThreshold();

        // silence gate
        if (waitingForSilence)
        {
            if (framePeak < silenceThreshold)
                silenceTimer += Time.deltaTime;
            else
                silenceTimer = 0f;

            if (silenceTimer >= silenceGap)
            {
                waitingForSilence = false;
                loudTimer = 0f;
                peakThisClip = 0f;
            }
            return;
        }

        // loudness count
        loudTimer = framePeak >= effectiveThreshold ? (loudTimer + Time.deltaTime) : 0f;
        if (loudTimer >= loudEnoughTime)
        {
            utteranceCount++;
            UpdateUtteranceProgressVisuals();
            Debug.Log($"PhonemeManager | utterance {utteranceCount}/{utterancesRequired} | peak {peakThisClip:F3}");

            if (utteranceCount < utterancesRequired)
            {
                waitingForSilence = true;
                silenceTimer = 0f;

                loudTimer = 0f;
                peakThisClip = 0f;
                return;
            }

            // got required utterances G�� finish
            autoStopped = true;
            HandleVirtualRelease();
        }
    }

    /* ---------- State machine ---------- */
    private void SetState(PhonemeCheckState s)
    {
        state = s;
        switch (s)
        {
            case PhonemeCheckState.Idle:
                feedbackText?.SetText(""); break;

            case PhonemeCheckState.Start:
                attemptCount = 0; alignPressed = false;
                feedbackText?.SetText(beamMode == BeamMode.BeamOn && !beamReady
                    ? "Starting upGǪ please wait"
                    : "Look at the button to begin."); break;

            case PhonemeCheckState.WaitingToRecord:
                alignPressed = false;
                SetBtnTint(btnColorOriginal);
                feedbackText?.SetText("Say the letter three times."); break;

            case PhonemeCheckState.Recording:
                SetBtnTint(Color.green);
                feedbackText?.SetText("Recording GǪ");
                StartCoroutine(BeginMic());
                break;

            case PhonemeCheckState.WaitingForResponse:
                feedbackText?.SetText("Processing GǪ");
                if (blinkCoroutine != null) StopCoroutine(blinkCoroutine);
                blinkCoroutine = StartCoroutine(BlinkRed());
                break;

            case PhonemeCheckState.Correct:
                OnPhonemeCorrect?.Invoke();
                feedbackText?.SetText("Good job! Trace.");
                SetState(PhonemeCheckState.EndCorrect);
                break;

            case PhonemeCheckState.Incorrect:
                OnPhonemeIncorrect?.Invoke();
                attemptCount++;
                feedbackText?.SetText(attemptCount >= 2 ? "Second incorrect." : "Incorrect. Try again!");
                StartCoroutine(ReturnToRecordOrEnd());
                break;

            case PhonemeCheckState.EndIncorrect:
                OnPhonemeTriesExhausted?.Invoke();
                feedbackText?.SetText("Move on. Trace.");
                break;

            case PhonemeCheckState.EndCorrect:
                break;
        }
        RefreshIndicators();
    }

    private IEnumerator ReturnToRecordOrEnd()
    {
        yield return new WaitForSeconds(2);
        if (attemptCount >= 2) SetState(PhonemeCheckState.EndIncorrect);
        else SetState(PhonemeCheckState.WaitingToRecord);
    }

    /* ---------- Mic control ---------- */
    private IEnumerator BeginMic()
    {
        if (micClip == null)
        {
            StartMicIfNeeded();
            float timeout = 2f;
            float waited = 0f;
            while (micClip == null && isMicWarming && waited < timeout)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (micClip == null)
            {
                Debug.LogError("Mic not warmed");
                yield break;
            }
        }
        startSample = Microphone.GetPosition(micDevice);
        recordedClip = null; // reset; we'll create a fresh clip at EndMic
        loudTimer = 0f;
        peakThisClip = 0f;
        autoStopped = false;
        lookAwayTimer = 0f;
        utteranceCount = 0;
        waitingForSilence = false;
        silenceTimer = 0f;
        UpdateUtteranceProgressVisuals();
        yield break;
    }

    private void EndMic()
    {
        if (micClip == null) return;

        int endFrame    = Microphone.GetPosition(micDevice);
        int totalFrames = micClip.samples;

        int lenFrames = endFrame >= startSample
            ? (endFrame - startSample)
            : (totalFrames - startSample + endFrame);

        if (lenFrames <= 0)
        {
            Debug.LogWarning("EndMic: len<=0");
            return;
        }

        // reuse a mono scratch as big as possible take
        int maxFrames = Mathf.CeilToInt(maxRecordingSeconds * sampleRate);
        if (recMonoScratch == null || recMonoScratch.Length < maxFrames)
            recMonoScratch = new float[maxFrames];

        // copy into recMonoScratch
        int headFrames = Math.Min(totalFrames - startSample, lenFrames);
        CopySegmentToMono(startSample, headFrames, recMonoScratch, 0);

        int tailFrames = lenFrames - headFrames;
        if (tailFrames > 0)
            CopySegmentToMono(0, tailFrames, recMonoScratch, headFrames);

        // create the exact-sized clip
        recordedClip = AudioClip.Create("take", lenFrames, 1, sampleRate, false);
        recordedClip.SetData(recMonoScratch, 0);

        Debug.Log($"EndMic: captured {(float)lenFrames / sampleRate:F2}s  ({lenFrames} frames)");
    }

    private void CancelRecording(string reason)
    {
        Debug.Log($"PhonemeManager: recording cancelled G�� {reason}");
        SetBtnTint(btnColorOriginal);
        SetState(PhonemeCheckState.WaitingToRecord);
    }

    /* ---------- Button press proxies ---------- */
    private void HandlePhysicalPress()  => HandleVirtualPress();
    private void HandlePhysicalRelease() => HandleVirtualRelease();

    private void HandleVirtualPress()
    {
        if (!beamReady && beamMode == BeamMode.BeamOn)
        {
            feedbackText?.SetText("Starting upGǪ");
            return;
        }
        alignPressed = true;

        if (state == PhonemeCheckState.Start) SetState(PhonemeCheckState.WaitingToRecord);
        else if (state == PhonemeCheckState.WaitingToRecord) SetState(PhonemeCheckState.Recording);
    }

    private void HandleVirtualRelease()
    {
        if (state != PhonemeCheckState.Recording) return;
        EndMic();
        SetState(PhonemeCheckState.WaitingForResponse);
        SendToBeam();
    }

    /* ---------- Warm-ups / Beam etc ---------- */
    private IEnumerator WarmUpMic()
    {
        if (Microphone.devices.Length == 0) yield break;
    #if UNITY_ANDROID && !UNITY_EDITOR
        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
    #endif
        foreach (var dev in Microphone.devices)
        {
            micDevice = dev;
            micClip = Microphone.Start(dev, true, maxRecordingSeconds, sampleRate);
            const float TIMEOUT = 1.5f;
            float t = 0f;
            while (Microphone.GetPosition(dev) <= 0 && t < TIMEOUT)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            if (Microphone.GetPosition(dev) > 0)
            {
                Debug.Log($"Mic warmed on -�{dev}-+ G��");
                yield break;
            }
            Microphone.End(dev);
            Debug.LogWarning($"Warm-up failed on -�{dev}-+, nextGǪ");
        }
        Debug.LogError("All mics failed to warm up =�ܿ");
    }

    private IEnumerator WarmUpBeam()
    {
        string targetUrl = GetActiveApiUrl();
        bool needsAuth = ShouldSendAuthHeader();

        if (string.IsNullOrEmpty(targetUrl) || (!useRemoteServer && !needsAuth))
        {
            beamReady = true;
            yield break;
        }

        int samples = sampleRate * 1;
        AudioClip silentClip = AudioClip.Create("BeamWarmup", samples, 1, sampleRate, false);
        byte[] wav = WavUtility.FromAudioClip(silentClip, out _);
        Destroy(silentClip);

        string b64 = Convert.ToBase64String(wav);
        string json = JsonUtility.ToJson(new BeamReq { audio_file = b64 });

        using (var req = new UnityWebRequest(targetUrl, "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (needsAuth)
                req.SetRequestHeader("Authorization", $"Bearer {TOKEN}");
            Debug.Log("PhonemeManager: Warming up Beam serverGǪ");
            yield return req.SendWebRequest();

            beamReady = true;
            alignPressed = false;
            feedbackText?.SetText("Look at the button to begin.");
            SetState(PhonemeCheckState.Start);

            if (req.result == UnityWebRequest.Result.Success)
                Debug.Log("PhonemeManager: Beam warmup complete.");
            else
                Debug.LogWarning($"PhonemeManager: Beam warmup failed: {req.error}");
        } // disposes handlers
    }

    /* ---------- Beam comms ---------- */
    private void SendToBeam()
    {
        if (beamMode != BeamMode.BeamOn)
        {
            SetState(beamMode == BeamMode.MarkAnswersCorrect
                     ? PhonemeCheckState.Correct
                     : PhonemeCheckState.Incorrect);
            return;
        }
        if (!recordedClip) { SetState(PhonemeCheckState.Incorrect); return; }

        AudioClip clipToSend = trimSilence ? TrimSilence(recordedClip, silenceThreshold) : recordedClip;
        StartCoroutine(PostAndCleanup(clipToSend));
    }

    private IEnumerator PostAndCleanup(AudioClip clipToSend)
    {
        byte[] wav = WavUtility.FromAudioClip(clipToSend, out _);
        var jsonBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new BeamReq { audio_file = Convert.ToBase64String(wav) }));

        using (var r = new UnityWebRequest(GetActiveApiUrl(), "POST"))
        {
            Debug.Log($"[Phoneme] POST {GetActiveApiUrl()} (remote={useRemoteServer})");
            r.uploadHandler   = new UploadHandlerRaw(jsonBytes);
            r.downloadHandler = new DownloadHandlerBuffer();
            r.SetRequestHeader("Content-Type", "application/json");
            if (ShouldSendAuthHeader())
                r.SetRequestHeader("Authorization", $"Bearer {TOKEN}");

            yield return r.SendWebRequest();

            if (blinkCoroutine != null)
            {
                StopCoroutine(blinkCoroutine);
                blinkCoroutine = null;
            }

            if (r.result == UnityWebRequest.Result.Success)
            {
                string body = r.downloadHandler.text ?? "";
                Debug.Log($"[Phoneme] response ({body.Length} chars) -> {body}");
                ParseBeam(r.downloadHandler.text);
            }
            else
            {
                Debug.LogError($"[Phoneme] POST failed: {r.result} ({r.responseCode}) {r.error}");
                SetState(PhonemeCheckState.Incorrect);
            }
        }

        // cleanup temp clips
        if (clipToSend && clipToSend != recordedClip) Destroy(clipToSend);
        if (recordedClip) { Destroy(recordedClip); recordedClip = null; }
    }

    private bool TryGetExpectedVariants(string letter, out List<string> variants)
    {
        variants = null;
        if (string.IsNullOrEmpty(letter)) return false;

        string key = letter.ToUpperInvariant();
        bool needFriendlyFallback =
            useRemoteServer &&
            !string.IsNullOrEmpty(lastBeamRawText) &&
            string.Equals(lastBeamRawText, lastBeamText, StringComparison.Ordinal);

        var merged = new List<string>();
        if (letterToIPA.TryGetValue(key, out var wants) && wants != null)
            merged.AddRange(wants);

        if (needFriendlyFallback &&
            letterToFriendlyPhonemes.TryGetValue(key, out var friendly) &&
            friendly != null)
        {
            merged.AddRange(friendly);
        }

        variants = merged
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return variants.Count > 0;
    }

    private void ParseBeam(string json)
    {
        string payload = json ?? string.Empty;
        var match = ipaFieldRegex.Match(payload);
        if (!match.Success)
            match = textFieldRegex.Match(payload);
        lastBeamText = match.Success ? match.Groups["value"].Value : string.Empty;
        var rawMatch = rawTextFieldRegex.Match(payload);
        lastBeamRawText = rawMatch.Success ? rawMatch.Groups["value"].Value : string.Empty;
        if (!string.IsNullOrEmpty(lastBeamRawText))
            lastBeamRawText = lastBeamRawText.Trim();

        if (string.IsNullOrEmpty(lastBeamText))
        {
            Debug.LogWarning("[Phoneme] Empty transcription from recognizer.");
            SetState(PhonemeCheckState.Incorrect);
            return;
        }

        string currentLetter = levelManager != null ? levelManager.currentLetter : string.Empty;
        string L = string.IsNullOrEmpty(currentLetter) ? string.Empty : currentLetter.ToUpperInvariant();
        if (!TryGetExpectedVariants(L, out var wants) || wants.Count == 0)
        {
            Debug.LogWarning($"[Phoneme] No expectations for letter '{currentLetter}'.");
            SetState(PhonemeCheckState.Incorrect);
            return;
        }

        lastBeamText = lastBeamText.Trim();
        if (!string.IsNullOrEmpty(lastBeamRawText) && !string.Equals(lastBeamRawText, lastBeamText, StringComparison.Ordinal))
            Debug.Log($"[Phoneme] raw transcript='{lastBeamRawText}'");

        Debug.Log($"[Phoneme] last='{lastBeamText}' expects={string.Join("|", wants)}");

        string beamRawLower = lastBeamText.ToLowerInvariant();
        bool rawMatch = wants.Any(w =>
        {
            if (string.IsNullOrEmpty(w)) return false;
            string wantLower = w.ToLowerInvariant().Trim();
            return wantLower.Length > 0 && beamRawLower.Contains(wantLower);
        });

        string normalizedBeam = NormalizePhonemeToken(lastBeamText);
        string normalizedBeamLower = normalizedBeam.ToLowerInvariant();
        bool normalizedMatch = !string.IsNullOrEmpty(normalizedBeamLower) && wants.Any(w =>
        {
            string normalizedWant = NormalizePhonemeToken(w).ToLowerInvariant().Trim();
            return !string.IsNullOrEmpty(normalizedWant) && normalizedBeamLower.Contains(normalizedWant);
        });

        if (normalizedMatch && !rawMatch)
        {
            Debug.Log($"[Phoneme] Normalized match for '{L}': raw='{lastBeamText}' normalized='{normalizedBeam}'");
        }

        Debug.Log($"[Phoneme] rawMatch={rawMatch} normalizedMatch={normalizedMatch}");
        bool isMatch = rawMatch || normalizedMatch;
        SetState(isMatch ? PhonemeCheckState.Correct : PhonemeCheckState.Incorrect);
    }

    /* ---------- Silence trim helper (edges only) ---------- */
    private AudioClip TrimSilence(AudioClip clip, float thresh = 0.001f)
    {
        if (!clip) return clip;

        float[] samples = new float[clip.samples];
        clip.GetData(samples, 0);

        int start = 0;
        while (start < samples.Length && Mathf.Abs(samples[start]) < thresh) start++;

        int end = samples.Length - 1;
        while (end > start && Mathf.Abs(samples[end]) < thresh) end--;

        int len = end - start + 1;
        if (len <= 0) len = 1;

        float[] trimmed = new float[len];
        Buffer.BlockCopy(samples, start * sizeof(float), trimmed, 0, len * sizeof(float));

        AudioClip newClip = AudioClip.Create("trimmed", len, 1, clip.frequency, false);
        newClip.SetData(trimmed, 0);
        return newClip;
    }

    /* ---------- UI / indicator helpers ---------- */
    private void SetBtnTint(Color c)
    {
        if (btnMat != null)
        {
            if (btnMat.color != c) btnMat.color = c;
        }
        else if (btnRenderer != null)
        {
            btnRenderer.material.color = c;
        }
    }

    private IEnumerator BlinkRed()
    {
        Color red = Color.red;
        while (state == PhonemeCheckState.WaitingForResponse)
        {
            SetBtnTint(red); yield return new WaitForSeconds(0.5f);
            SetBtnTint(btnColorOriginal); yield return new WaitForSeconds(0.5f);
        }
    }

    private void RefreshIndicators(bool force = false)
    {
        if (suppressIndicators)
        {
            indicatorGate.WantSpeak("state", false, 0);
            indicatorGate.WantWait("state", false, 0);
            if (speakIndicator && speakIndicator.activeSelf) speakIndicator.SetActive(false);
            if (waitIndicator && waitIndicator.activeSelf) waitIndicator.SetActive(false);
            prevSpeak = speakIndicator && speakIndicator.activeSelf;
            prevWait = waitIndicator && waitIndicator.activeSelf;
            return;
        }
        bool inPhonemeMode = levelManager != null && levelManager.currentMode == LevelManager.GameMode.PhonemeChecking;
        // Outside phoneme mode: force both indicators off and clear state wants
        if (!inPhonemeMode)
        {
            indicatorGate.WantSpeak("state", false, priority: 0);
            indicatorGate.WantWait ("state", false, priority: 0);
            if (speakIndicator && speakIndicator.activeSelf) speakIndicator.SetActive(false);
            if (waitIndicator  && waitIndicator.activeSelf)  waitIndicator.SetActive(false);
            prevSpeak = speakIndicator && speakIndicator.activeSelf;
            prevWait  = waitIndicator  && waitIndicator.activeSelf;
            return;
        }

        bool wantSpeak = state == PhonemeCheckState.Recording;
        bool wantWait  = !wantSpeak;

        indicatorGate.WantSpeak("state", wantSpeak, priority: 10);
        indicatorGate.WantWait ("state", wantWait,  priority: 5);

        indicatorGate.Apply(speakIndicator, waitIndicator);

        bool nowSpeak = speakIndicator && speakIndicator.activeSelf;
        bool nowWait  = waitIndicator  && waitIndicator.activeSelf;

        if (force || nowSpeak != prevSpeak) prevSpeak = nowSpeak;
        if (force || nowWait  != prevWait ) prevWait  = nowWait;
    }

    public void StartMicIfNeeded()
    {
        if (!isActiveAndEnabled) return;
        if (micClip != null) return;
        if (warmMicRoutine != null) return;
        warmMicRoutine = StartCoroutine(WarmMicWrapper());
    }

    public void StopMic()
    {
        if (noiseSampleRoutine != null)
        {
            StopCoroutine(noiseSampleRoutine);
            noiseSampleRoutine = null;
        }
        suppressIndicators = false;

        if (warmMicRoutine != null)
        {
            StopCoroutine(warmMicRoutine);
            warmMicRoutine = null;
            isMicWarming = false;
        }

        if (!string.IsNullOrEmpty(micDevice) && Microphone.IsRecording(micDevice))
            Microphone.End(micDevice);

        if (micClip)
        {
            Destroy(micClip);
            micClip = null;
        }
    }

    private IEnumerator WarmMicWrapper()
    {
        isMicWarming = true;
        yield return WarmUpMic();
        isMicWarming = false;
        warmMicRoutine = null;
        bool micReady = micClip && !string.IsNullOrEmpty(micDevice) && Microphone.IsRecording(micDevice);
        if (!micReady)
        {
            if (micClip)
            {
                Destroy(micClip);
                micClip = null;
            }
            Debug.LogWarning("PhonemeManager: Mic warm-up did not produce a clip.");
        }
    }

    public void ReleaseMemory()
    {
        indicatorGate.WantSpeak("state", false, 0);
        indicatorGate.WantWait ("state", false, 0);
        if (noiseSampleRoutine != null)
        {
            StopCoroutine(noiseSampleRoutine);
            noiseSampleRoutine = null;
        }
        suppressIndicators = false;
        if (speakIndicator && speakIndicator.activeSelf) speakIndicator.SetActive(false);
        if (waitIndicator  && waitIndicator.activeSelf)  waitIndicator.SetActive(false);
        prevSpeak = speakIndicator && speakIndicator.activeSelf;
        prevWait  = waitIndicator  && waitIndicator.activeSelf;

        StopMic();

        if (recordedClip)
        {
            Destroy(recordedClip);
            recordedClip = null;
        }

        micBuf = null;
        headScratch = null;
        tailScratch = null;
        clipScratch = null;
        recMonoScratch = null;
        ClearUtteranceProgressVisuals();
    }

    /* ---------- Cleanup ---------- */
    private void OnDestroy()
    {
        if (proximityButton)
        {
            proximityButton.OnButtonPressed  -= HandlePhysicalPress;
            proximityButton.OnButtonReleased -= HandlePhysicalRelease;
        }
        if (levelManager != null)
        {
            levelManager.OnGameModeChanged -= HandleModeChanged;
            levelManager.OnLetterChanged   -= HandleLetterChanged;
        }
        StopMic();
        if (recordedClip) Destroy(recordedClip);
        recordedClip = null;

        if (btnMat) Destroy(btnMat);
        btnMat = null;

        ClearUtteranceProgressVisuals();
        SetLetterPromptVisible(false);
    }

    [Serializable] private struct BeamReq { public string audio_file; }

    private GameObject ResolveLetterPromptRoot()
    {
        if (!letterPrompt) return null;
        if (letterPromptRoot) return letterPromptRoot;
        Transform parent = letterPrompt.transform.parent;
        return parent ? parent.gameObject : letterPrompt.gameObject;
    }

    private void SetLetterPromptVisible(bool visible)
    {
        if (!letterPrompt) return;

        GameObject root = ResolveLetterPromptRoot();
        if (root && !root.activeSelf)
        {
            // Keep the plane active so trace visuals stay available.
            root.SetActive(true);
        }

        letterPrompt.enabled = visible;
        if (!visible)
            letterPrompt.text = string.Empty;
    }

    private void UpdateLetterPromptText()
    {
        if (!letterPrompt || levelManager == null) return;
        string letter = levelManager.currentLetter ?? string.Empty;
        letterPrompt.text = string.IsNullOrEmpty(letter) ? string.Empty : letter.ToUpperInvariant();
    }

    private void HandleLetterChanged(string letter)
    {
        if (!letterPrompt) return;

        string display = string.IsNullOrEmpty(letter) ? string.Empty : letter.ToUpperInvariant();
        letterPrompt.text = display;

        if (levelManager != null && levelManager.currentMode == LevelManager.GameMode.PhonemeChecking)
        {
            SetLetterPromptVisible(true);
        }
        else
        {
            SetLetterPromptVisible(false);
        }
    }

    private void HandleModeChanged(LevelManager.GameMode mode)
    {
        bool inPhonemeMode = (mode == LevelManager.GameMode.PhonemeChecking);
        if (!inPhonemeMode)
        {
            // Hard-off outside phoneme mode
            indicatorGate.WantSpeak("state", false, 0);
            indicatorGate.WantWait ("state", false, 0);
            if (speakIndicator && speakIndicator.activeSelf) speakIndicator.SetActive(false);
            if (waitIndicator  && waitIndicator.activeSelf)  waitIndicator.SetActive(false);
            prevSpeak = speakIndicator && speakIndicator.activeSelf;
            prevWait  = waitIndicator  && waitIndicator.activeSelf;
            SetLetterPromptVisible(false);
            ClearUtteranceProgressVisuals();
        }
        else
        {
            StartMicIfNeeded();
            RequestAmbientNoiseSample();
            // Re-evaluate based on current recording state
            RefreshIndicators(true);
            UpdateLetterPromptText();
            SetLetterPromptVisible(true);
        }
    }

    private static string NormalizePhonemeToken(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        string decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (char c in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark ||
                category == UnicodeCategory.SpacingCombiningMark ||
                category == UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(c) || c == '-' || c == '_' || c == '.' ||
                c == ',' || c == '\'' || c == '"' || c == '/' || c == '\\')
            {
                continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (phonemeCharFold.TryGetValue(lower, out var replacement))
            {
                if (!string.IsNullOrEmpty(replacement))
                {
                    sb.Append(replacement);
                }
                continue;
            }

            if ((lower >= 'a' && lower <= 'z') || char.IsDigit(lower))
            {
                sb.Append(lower);
            }
        }

        return sb.ToString();
    }
}


