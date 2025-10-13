using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Globalization;

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

    [Header("Beam API")]
    [SerializeField] private string API_URL = "https://recognize-3a64e01-v3.app.beam.cloud";
    [SerializeField] private string TOKEN   = "YOUR_BEAM_TOKEN";
    [NonSerialized] public string lastBeamText = "";

    [Header("Mic Settings")]
    [SerializeField] private int maxRecordingSeconds = 15;
    [SerializeField] private int sampleRate = 16000; // 16 kHz = plenty for speech

    [Header("Auto-Start Alignment")]
    [Range(0f,1f)] public float alignmentThreshold = 0.7f;

    [Header("Auto-Stop Loudness")]
    public float amplitudeThreshold = 0.03f;
    public float loudEnoughTime = 0.18f;

    [Header("Auto-Stop Look-Away")]
    public float lookAwayGrace = 0.2f;

    [Header("Silence Trimming (edges only)")]
    [SerializeField] private bool trimSilence = true;
    [SerializeField] private float silenceThreshold = 0.001f;

    [Header("Mic Status Indicators")]
    [SerializeField] private GameObject speakIndicator;
    [SerializeField] private GameObject waitIndicator;

    /* -> LENIENCY */
    [Header("Lenient-mode Settings")]
    [Tooltip("How many loud utterances before we send to Beam")]
    [SerializeField] private int utterancesRequired = 3;
    [Tooltip("Seconds wait indicator flashes after 1st utterance")]
    [SerializeField] private float betweenUtteranceFlash = 1f;
    [Tooltip("Secs of relative silence before next utterance allowed")]
    [SerializeField] private float silenceGap = 0.25f;
    [Tooltip("Peak below this factor*ampThresh counts as silence")]
    [SerializeField] private float silenceAmpFactor = 0.5f;

    /* ---------- privates ---------- */
    private bool prevSpeak, prevWait;
    private Coroutine blinkCoroutine;
    private Coroutine flashCoroutine;

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
                    else finalSpeak = true; // tie GåÆ Speak
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

    private static readonly Dictionary<char, string> phonemeCharFold = new()
    {
        { '+ª', "ae" },
        { '+æ', "a" },
        { '+Æ', "o" },
        { '+É', "a" },
        { '-î', "u" },
        { '+Ö', "e" },
        { '+¢', "e" },
        { '+£', "e" },
        { '+P', "e" },
        { '+Ü', "er" },
        { '+¥', "er" },
        { '+¬', "i" },
        { '+»', "i" },
        { '-¦', "i" },
        { '-Å', "y" },
        { '-è', "u" },
        { '+ô', "oe" },
        { '++', "oe" },
        { '+ö', "o" },
        { '+í', "g" },
        { '++', "th" },
        { '+¦', "th" },
        { '-â', "sh" },
        { '-Æ', "zh" },
        { '+ï', "ng" },
        { '+º', "sh" },
        { '-ñ', "j" },
        { '-º', "ch" },
        { '-É', "" },
        { '-ê', "" },
        { '-î', "" },
        { '?', "" },
        { 'n++', "" }
    };

    /* ultra-lenient IPA map */
    private readonly Dictionary<string,List<string>> letterToIPA = new()
    {
        { "A", new(){ "a","+æ","+ª","+Æ","-î","+Ö","e+¬","a+¬","+¢","e","+É","a-É","+æ-É","+ª-É","+£","+ÿ","+Ö¦ƒ","e +¬" }},
        { "B", new(){ "b","b +Ö","b +æ","b +ö","b +¢","p","p +Ö","p +æ","p +ö","p +¢","bi","bi-É","b i","b i-É","b +¬","b+¬" }},
        { "C", new(){ "k","k +Ö","k +æ","k -î","k +ö","s","s +Ö","s +æ","s -î","s +ö","si","si-É","s i","s i-É","t-â","t -â","-â","ts","t s" }},
        { "D", new(){ "d","d +Ö","d +æ","d +¢","t","t +Ö","t +æ","t -î","t +ö","di","di-É","d i","d i-É","+¦","++" }},
        { "E", new(){ "+¢","e","i","e+¬","+£","+¬","+¢+Ö","e+Ö","i-É","+¬+Ö","e-É","e +¬","+ÿ","+Ö" }},
        { "F", new(){ "f","f +Ö","f +æ","v","v +Ö","v +æ","+¢f","e f","+¢ f","ef" }},
        { "G", new(){ "+í","g","+í +Ö","+í +æ","k","k +Ö","k +æ","k -î","k +ö","d-Æ","d-Æ +Ö","d-Æ +æ","d-Æ -î","-Æ","d-Æi","d-Æi-É","d -Æ i","d -Æ i-É" }},
        { "H", new(){ "h","h +Ö","h +æ","h -î","h +ö","e+¬t-â","e +¬ t-â","e+¬ t-â","he+¬t-â","h e+¬ t-â" }},
        { "I", new(){ "+¬","i","a+¬","e","-î","+Ö","+¬+Ö","i-É","a i","aj","+¬-É" }},
        { "J", new(){ "d-Æ","d-Æ +Ö","d-Æ +æ","d-Æ -î","t-â","t-â +Ö","t-â +æ","d-Æe+¬","d-Æ e+¬","-Æ" }},
        { "K", new(){ "k","k +Ö","k +æ","k -î","+í","g","+í +Ö","+í +æ","ke+¬","k e+¬" }},
        { "L", new(){ "l","l +Ö","l +æ","+½","l¦¬","+Öl","+¢l","e l","+¢ l","el" }},
        { "M", new(){ "m","m +Ö","m +æ","m¦¬","+Öm","+¢m","e m","+¢ m","em","n" }},
        { "N", new(){ "n","n +Ö","n +æ","+ï","n¦¬","+Ön","+¢n","e n","+¢ n","en" }},
        { "O", new(){ "o-è","+Æ","+ö","+æ","+Ö-è","o","+£","-î","a","+ö-è","o-É","+ö-É","ow" }},
        { "P", new(){ "p","p +Ö","p +æ","p +ö","b","b +Ö","b +æ","b +ö","b +¢","pi","pi-É","p i","p i-É","p+¬","p +¬" }},
        { "Q", new(){ "k w","k w +Ö","k w +æ","+í w","+í w +Ö","+í w +æ","kw","+íw","kju","kju-É","k ju","k j u","k" }},
        { "R", new(){ "+¦","r","+¦ +Ö","+¦ +æ","+¦ -î","+Ü","+¥","+æ-É","+æ+¦","++","++","+æ +¦","ar","r¦¬","+¦¦¬" }},
        { "S", new(){ "s","s +Ö","s +æ","z","z +Ö","z +æ","+¢s","e s","+¢ s","es","-â","++" }},
        { "T", new(){ "t","t +Ö","t +æ","t -î","d","d +Ö","d +æ","d +¢","ti","ti-É","t i","t i-É","t-â","t -â","ts","t s","++","++" }},
        { "U", new(){ "-î","u","ju","+Ö","-è","u-É","a","ju-É","j u","+»","-ë" }},
        { "V", new(){ "v","v +Ö","v +æ","f","f +Ö","f +æ","w","vi","vi-É","v i","v i-É","v+¬","v +¬" }},
        { "W", new(){ "w","w +Ö","w +æ","-è","u","-êd-îb+Ölju","d-îb+Ölju","d -î b +Ö l j u","w-è","v" }},
        { "X", new(){ "k s","k s +Ö","k s +æ","+¢ ks","+¢ k","+í z","+í z +Ö","+í z +æ","k","+¢","+í s","g z","+¢ gz","e gz","egz","+¬ ks","i ks","+¬ k s","eks" }},
        { "Y", new(){ "j","j +Ö","j +æ","wa+¬","i","ja+¬","w a+¬","j i","ji","+¬","i-É" }},
        { "Z", new(){ "z","z +Ö","z +æ","s","s +Ö","s +æ","zi","zi-É","z i","z i-É","z+¬","z +¬","z+¢d","z +¢ d","-Æ" }},
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
        levelManager.OnPhonemeCheckStart += () => SetState(PhonemeCheckState.Start);
        // Ensure indicators follow mode changes instantly (never on in other modes)
        if (levelManager != null) levelManager.OnGameModeChanged += HandleModeChanged;
    }

    private void Start()
    {
        StartMicIfNeeded();

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

        // silence gate
        if (waitingForSilence)
        {
            if (framePeak < amplitudeThreshold * silenceAmpFactor)
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
        loudTimer = framePeak >= amplitudeThreshold ? (loudTimer + Time.deltaTime) : 0f;
        if (loudTimer >= loudEnoughTime)
        {
            utteranceCount++;
            Debug.Log($"PhonemeManager | utterance {utteranceCount}/{utterancesRequired} | peak {peakThisClip:F3}");

            if (utteranceCount < utterancesRequired)
            {
                waitingForSilence = true;
                silenceTimer = 0f;

                if (flashCoroutine != null) StopCoroutine(flashCoroutine);
                flashCoroutine = StartCoroutine(FlashWaitBetween());

                loudTimer = 0f;
                peakThisClip = 0f;
                return;
            }

            // got required utterances GÇô finish
            autoStopped = true;
            HandleVirtualRelease();
        }
    }

    private IEnumerator FlashWaitBetween()
    {
        SetWaitIndicator(true, tag: "flash", priority: 100);
        yield return new WaitForSeconds(betweenUtteranceFlash);
        SetWaitIndicator(false, tag: "flash");
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
                    ? "Starting upGÇª please wait"
                    : "Look at the button to begin."); break;

            case PhonemeCheckState.WaitingToRecord:
                alignPressed = false;
                SetBtnTint(btnColorOriginal);
                feedbackText?.SetText("Say the letter three times."); break;

            case PhonemeCheckState.Recording:
                SetBtnTint(Color.green);
                feedbackText?.SetText("Recording GÇª");
                StartCoroutine(BeginMic());
                break;

            case PhonemeCheckState.WaitingForResponse:
                feedbackText?.SetText("Processing GÇª");
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
        Debug.Log($"PhonemeManager: recording cancelled GÇô {reason}");
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
            feedbackText?.SetText("Starting upGÇª");
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
                Debug.Log($"Mic warmed on -½{dev}-+ G£à");
                yield break;
            }
            Microphone.End(dev);
            Debug.LogWarning($"Warm-up failed on -½{dev}-+, nextGÇª");
        }
        Debug.LogError("All mics failed to warm up =ƒÜ¿");
    }

    private IEnumerator WarmUpBeam()
    {
        if (string.IsNullOrEmpty(API_URL) || string.IsNullOrEmpty(TOKEN)) { beamReady = true; yield break; }

        int samples = sampleRate * 1;
        AudioClip silentClip = AudioClip.Create("BeamWarmup", samples, 1, sampleRate, false);
        byte[] wav = WavUtility.FromAudioClip(silentClip, out _);
        Destroy(silentClip);

        string b64 = Convert.ToBase64String(wav);
        string json = JsonUtility.ToJson(new BeamReq { audio_file = b64 });

        using (var req = new UnityWebRequest(API_URL, "POST"))
        {
            req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", $"Bearer {TOKEN}");
            Debug.Log("PhonemeManager: Warming up Beam serverGÇª");
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

        using (var r = new UnityWebRequest(API_URL, "POST"))
        {
            r.uploadHandler   = new UploadHandlerRaw(jsonBytes);
            r.downloadHandler = new DownloadHandlerBuffer();
            r.SetRequestHeader("Content-Type", "application/json");
            r.SetRequestHeader("Authorization", $"Bearer {TOKEN}");

            yield return r.SendWebRequest();

            if (blinkCoroutine != null)
            {
                StopCoroutine(blinkCoroutine);
                blinkCoroutine = null;
            }

            if (r.result == UnityWebRequest.Result.Success) ParseBeam(r.downloadHandler.text);
            else SetState(PhonemeCheckState.Incorrect);
        }

        // cleanup temp clips
        if (clipToSend && clipToSend != recordedClip) Destroy(clipToSend);
        if (recordedClip) { Destroy(recordedClip); recordedClip = null; }
    }

    private void ParseBeam(string json)
    {
        int idx = json.IndexOf("\"text\":\"", StringComparison.Ordinal);
        lastBeamText = idx >= 0
            ? json.Substring(idx + 8, json.IndexOf("\"", idx + 8, StringComparison.Ordinal) - (idx + 8))
            : "";

        if (string.IsNullOrEmpty(lastBeamText))
        {
            SetState(PhonemeCheckState.Incorrect);
            return;
        }

        string L = levelManager.currentLetter.ToUpper();
        if (!letterToIPA.TryGetValue(L, out var wants) || wants.Count == 0)
        {
            SetState(PhonemeCheckState.Incorrect);
            return;
        }

        bool rawMatch = wants.Any(w => !string.IsNullOrEmpty(w)
                                    && lastBeamText.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        string normalizedBeam = NormalizePhonemeToken(lastBeamText);
        bool normalizedMatch = !string.IsNullOrEmpty(normalizedBeam) && wants.Any(w =>
        {
            string normalizedWant = NormalizePhonemeToken(w);
            return !string.IsNullOrEmpty(normalizedWant) && normalizedBeam.Contains(normalizedWant);
        });

        if (normalizedMatch && !rawMatch)
        {
            Debug.Log($"[Phoneme] Normalized match for '{L}': raw='{lastBeamText}' normalized='{normalizedBeam}'");
        }

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
    }

    /* ---------- Cleanup ---------- */
    private void OnDestroy()
    {
        if (proximityButton)
        {
            proximityButton.OnButtonPressed  -= HandlePhysicalPress;
            proximityButton.OnButtonReleased -= HandlePhysicalRelease;
        }
        if (levelManager != null) levelManager.OnGameModeChanged -= HandleModeChanged;
        StopMic();
        if (recordedClip) Destroy(recordedClip);
        recordedClip = null;

        if (btnMat) Destroy(btnMat);
        btnMat = null;
    }

    [Serializable] private struct BeamReq { public string audio_file; }

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
        }
        else
        {
            StartMicIfNeeded();
            // Re-evaluate based on current recording state
            RefreshIndicators(true);
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
