using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using System.Security.Cryptography;
using System.Globalization;
using System.Linq;
using Unity.Sentis;
using LetterDojo.Dictation.OnDevice;
using HandState = PlaneSurfaceDrawer.HandState;

/*
 * DictationManager (Vision OCR backend, Unity-safe PEM import)
 * - Same public API / events / flow as your original
 * - WarmupBeam / BeamRecognize kept (now call Google Vision)
 * - Auth: service-account JSON loaded from Resources/
 * - No RSA.ImportFromPem / ImportPkcs8PrivateKey calls; uses manual PEM/DER parser
 */

[RequireComponent(typeof(CanvasManager))]
[RequireComponent(typeof(SimpleRecorder))]
[RequireComponent(typeof(LevelManager))]
[RequireComponent(typeof(SaveManager))]
public class DictationManager : MonoBehaviour
{
    public enum BeamMode { BeamOn, MarkAnswersWrong, MarkAnswersCorrect }

    public event Action        OnDictationStart;
    public event Action        OnDictationComplete;
    public event Action<float> OnDictationGraded;
    public event Action        OnLetterCorrect;
    public event Action        OnLetterIncorrect;

    [Header("UI / Flow")]
    [SerializeField] private GameObject gradingButtonGO;
    [SerializeField] private GameObject eraseButtonGO;
    [SerializeField] private GameObject repeatButtonGO;
    [SerializeField] private TMP_Text   feedbackText;

    [Header("Replay Prompt")]
    [SerializeField] private TMP_Text replayPromptText;
    [SerializeField] private GameObject replayPromptRoot;
    [SerializeField, Range(0f, 0.02f)] private float replayPromptDuration = 1.2f;
    [SerializeField, Min(0f)] private float tmpReplayFlashDuration = 1f;
    [SerializeField] private float waitBeforeRef    = 0.75f;
    [SerializeField] private float waitAfterReplay  = 0.5f;
    [SerializeField] private float waitAfterCorrect = 0.5f;

    [Header("Board Capture (no crop/rotate)")]
    [SerializeField] private Camera   boardCamera;
    [SerializeField] private Renderer boardRenderer;
    [SerializeField] private LayerMask boardLayer = 0;
    [SerializeField] private int captureWidth  = 512;
    [SerializeField] private int captureHeight = 512;
    [SerializeField] private bool debugWriteCapture = false;

    [Header("OCR (Vision API)")]
    [SerializeField] private BeamMode beamMode = BeamMode.BeamOn;
    [SerializeField] private string visionEndpoint = "https://vision.googleapis.com/v1/images:annotate";
    [SerializeField] private string serviceAccountJsonResource = "plenary-treat-471015-i5-84297c030ee9"; // Resources/<name>.json
    [SerializeField, Range(1, 8)] private int beamLengthHint = 1;
    [SerializeField] private string[] languageHints;

    [Header("Local Classifier (Sentis)")]
    [SerializeField] private bool useLocalClassifier = false;
    [SerializeField] private OnDeviceLetterClassifier localClassifier;
    [SerializeField, Range(0f, 1f)] private float localConfidenceThreshold = 0.5f;
    [SerializeField] private bool fallbackToVisionOnLocalFailure = true;
    [SerializeField] private bool autoCreateLocalClassifier = true;
    [SerializeField] private ModelAsset localClassifierModelAsset;
    [SerializeField] private BackendType localPreferredBackend = BackendType.GPUCompute;
    [SerializeField] private bool localPreloadModelOnAwake = true;
    [SerializeField, Range(0f, 1f)] private float localForegroundThreshold = 32f / 255f;
    [SerializeField, Range(0f, 0.25f)] private float localPaddingFraction = 0.05f;
    [SerializeField, Min(0)] private int localMinimumPaddingPixels = 2;
    [SerializeField] private GameObject debugLocalButtonGO;

    [Header("Remote Classifier")]
    [SerializeField] private bool useRemoteClassifier = true;
    [SerializeField] private string remoteClassifierBaseUrl = "http://108.46.76.56:8080";
    [SerializeField] private string remoteApiKey = "super-secret-key";
    [SerializeField, Range(0f, 1f)] private float remoteConfidenceThreshold = 0.6f;
    [SerializeField, Range(0f, 1f)] private float remoteSecondRankConfidenceThreshold = 0.4f;
    [SerializeField, Range(0f, 1f)] private float remoteWrongConfidenceThreshold = 0.9f;
    [SerializeField, Range(1f, 60f)] private float remoteRequestTimeoutSeconds = 10f;

    [Header("Auto Grading")]
    [SerializeField] private bool autoGradeOnIdle = true;
    [SerializeField, Range(0.1f, 2f)] private float autoGradeIdleSeconds = 0.7f;
    [SerializeField, Range(0f, 1f)] private float autoGradeMinimumConfidence = 0.5f;
    [SerializeField, Range(0f, 1f)] private float autoGradeWrongMinimumConfidence = 0.2f;
    [SerializeField, Min(0f)] private float autoGradeMinimumStrokeLength = 0.01f;

    [Header("Guide Lines (optional visuals)")]
    public Transform skyLine, planeLine, groundLine;

    [Header("Pass/Fail")]
    [Range(0f, 1f)] public float passRate = 1f;

    [Header("Drawer Host (strokes in dictation)")]
    [SerializeField] private GameObject drawerHost;

    [SerializeField] private GameObject tmpLetter;

    [Header("Hint Display")]
    [SerializeField] private Letter3DDisplay hintDisplay;

    private CanvasManager  canvas;
    private SimpleRecorder rec;
    private LevelManager   lvl;
    private SaveManager    saver;

    private ProximityButton gradingBtn;
    private ProximityButton eraseBtn;
    private ProximityButton debugLocalBtn;

    // Laser UI buttons (preferred)
    private LaserUIButton gradingLaserBtn;
    private LaserUIButton eraseLaserBtn;
    private LaserUIButton repeatLaserBtn;

    private AudioManager audioManager;
    private bool loggedMissingAudioManager = false;
    private int attemptCount = 0; // two tries policy
    private bool suppressTmpOnAdvance;
    private readonly List<GameObject> replayDots = new List<GameObject>();

    private enum State { Idle, Drawing, WaitingForResponse, GradedAccept, GradedReject }
    private State state = State.Idle;

    private bool _lastDrawerActive = true;
    private LevelManager.GameMode _lastNotifiedMode = LevelManager.GameMode.PhonemeChecking;
    private bool _hasLastMode = false;

    private bool ShouldUseLocalGrading => useLocalClassifier && localClassifier != null && localClassifier.HasModelAsset;
    private Coroutine _debugLocalRoutine;
    private int _lastStrokeCount = 0;
    private float _lastStrokeChangeTime = -1f;
    private bool _autoGradeTriggeredForStroke = false;
    private bool _autoGradeRunning = false;
    private PlaneSurfaceDrawer _planeDrawer;
    private HandPlaneConstraint _planeConstraint;
    [SerializeField, Range(0f, 0.02f)] private float replayDotLift = 0.0015f;
    private HandState _rightHandState = HandState.Idle;
    private HandState _leftHandState = HandState.Idle;
    private int _lastObservedStrokeTotal = 0;
    private bool _playedInitialAudio = false;
    private static readonly HashSet<char> LettersRequiringTwoStrokes = new HashSet<char> { 'f', 't', 'k', 'x' };
    private bool ShouldUseRemoteGrading => useRemoteClassifier && !string.IsNullOrWhiteSpace(remoteClassifierBaseUrl);

    public void ReleaseMemory()
    {
        if (_debugLocalRoutine != null)
        {
            StopCoroutine(_debugLocalRoutine);
            _debugLocalRoutine = null;
        }

        ClearBoardVisuals();

        if (debugLocalButtonGO)
            debugLocalButtonGO.SetActive(false);
    }

    // ===== Auth cache =====
    private string _cachedAccessToken = null;
    private double _tokenExpiryEpoch  = 0; // unix seconds

    // ===== Unity lifecycle =====
    void Awake()
    {
        canvas = GetComponent<CanvasManager>();
        rec    = GetComponent<SimpleRecorder>();
        lvl    = GetComponent<LevelManager>();
        saver  = GetComponent<SaveManager>();
        ResolveAudioManager();
        ResolveHintDisplay();
        if (lvl != null)
            lvl.OnLetterChanged += HandleLevelManagerLetterChanged;

        EnsureLocalClassifierConfigured();

        if (debugLocalButtonGO)
        {
            debugLocalBtn = debugLocalButtonGO.GetComponent<ProximityButton>();
            debugLocalButtonGO.SetActive(false);
        }

        if (drawerHost == null)
        {
            var drawer = GetComponent<PlaneSurfaceDrawer>();
            if (drawer != null)
            {
                drawerHost = drawer.gameObject;
                _planeDrawer = drawer;
            }
            else
            {
                var childDrawer = GetComponentInChildren<PlaneSurfaceDrawer>(true);
                if (childDrawer != null)
                {
                    drawerHost = childDrawer.gameObject;
                    _planeDrawer = childDrawer;
                }
            }
        }
        else
        {
            _planeDrawer = drawerHost.GetComponent<PlaneSurfaceDrawer>() ??
                           drawerHost.GetComponentInChildren<PlaneSurfaceDrawer>(true);
        }

        if (_planeDrawer != null)
        {
            _planeConstraint = _planeDrawer.GetComponent<HandPlaneConstraint>() ??
                                _planeDrawer.GetComponentInParent<HandPlaneConstraint>();
        }

        EnsurePlaneDrawerCallbacks();
        HideReplayPrompt();

        if (gradingButtonGO)
        {
            gradingLaserBtn = gradingButtonGO.GetComponent<LaserUIButton>();
            if (gradingLaserBtn)
            {
                gradingLaserBtn.SetCustomAction(HandleGradeBtn);
            }
            else
            {
                gradingBtn = gradingButtonGO.GetComponent<ProximityButton>();
            }
            gradingButtonGO.SetActive(false);
        }

        if (eraseButtonGO)
        {
            eraseLaserBtn = eraseButtonGO.GetComponent<LaserUIButton>();
            if (eraseLaserBtn)
            {
                eraseLaserBtn.SetCustomAction(HandleEraseBtn);
            }
            else
            {
                eraseBtn = eraseButtonGO.GetComponent<ProximityButton>();
            }
            eraseButtonGO.SetActive(false);
        }

        if (repeatButtonGO)
        {
            repeatLaserBtn = repeatButtonGO.GetComponent<LaserUIButton>();
            if (repeatLaserBtn)
            {
                repeatLaserBtn.SetCustomAction(HandleRepeatBtn);
            }
            repeatButtonGO.SetActive(false);
        }

        HardConfigureCaptureCamera();
    }

    void Start()
    {
        EnsureLocalClassifierConfigured();

        if (ShouldUseLocalGrading)
        {
            try
            {
                localClassifier?.WarmupModel();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Dictation] Failed to warm up local classifier: {ex.Message}");
            }
        }

        if ((!ShouldUseLocalGrading || fallbackToVisionOnLocalFailure) && beamMode == BeamMode.BeamOn)
            StartCoroutine(WarmupBeam()); // primes token + HTTP path
    }

    void OnEnable()
    {
        ResolveAudioManager();
        EnsurePlaneDrawerCallbacks();
        if (gradingBtn) gradingBtn.OnButtonPressed += HandleGradeBtn;
        if (eraseBtn)   eraseBtn.OnButtonPressed   += HandleEraseBtn;
        if (debugLocalBtn) debugLocalBtn.OnButtonPressed += HandleDebugLocalBtn;
        ClearBoardVisuals();
        UpdateUI();
        TryPlayInitialLetterAudio();
    }
    void OnDisable()
    {
        if (gradingBtn) gradingBtn.OnButtonPressed -= HandleGradeBtn;
        if (eraseBtn)   eraseBtn.OnButtonPressed   -= HandleEraseBtn;
        if (debugLocalBtn) debugLocalBtn.OnButtonPressed -= HandleDebugLocalBtn;
        TeardownPlaneDrawerCallbacks();
        if (_debugLocalRoutine != null)
        {
            StopCoroutine(_debugLocalRoutine);
            _debugLocalRoutine = null;
        }
        _playedInitialAudio = false;
    }

    void OnDestroy()
    {
        if (lvl != null)
            lvl.OnLetterChanged -= HandleLevelManagerLetterChanged;
    }

    private void EnsureLocalClassifierConfigured()
    {
        if (localClassifier == null)
            localClassifier = GetComponent<OnDeviceLetterClassifier>();

        if (localClassifier == null)
        {
            var existing = FindObjectsOfType<OnDeviceLetterClassifier>(true);
            if (existing != null && existing.Length > 0)
                localClassifier = existing[0];
        }

        bool needsLocalComponent = useLocalClassifier || debugLocalButtonGO != null;

        if (localClassifier == null && autoCreateLocalClassifier && needsLocalComponent)
            localClassifier = gameObject.AddComponent<OnDeviceLetterClassifier>();

        if (debugLocalButtonGO && debugLocalBtn == null)
            debugLocalBtn = debugLocalButtonGO.GetComponent<ProximityButton>();

        if (localClassifier == null)
        {
            if (useLocalClassifier)
                Debug.LogWarning("[Dictation] Local classifier is enabled but no OnDeviceLetterClassifier component is available.");
            return;
        }

        localClassifier.Configure(localClassifierModelAsset, localPreferredBackend, localPreloadModelOnAwake,
            localForegroundThreshold, localPaddingFraction, localMinimumPaddingPixels);

        if (useLocalClassifier && !localClassifier.HasModelAsset)
            Debug.LogWarning("[Dictation] Local classifier mode is enabled but no model asset is assigned.");
    }

    private void EnsurePlaneDrawerCallbacks()
    {
        if (_planeDrawer == null && drawerHost != null)
        {
            _planeDrawer = drawerHost.GetComponent<PlaneSurfaceDrawer>() ??
                           drawerHost.GetComponentInChildren<PlaneSurfaceDrawer>(true);
        }

        if (_planeDrawer != null)
        {
            _planeConstraint = _planeDrawer.GetComponent<HandPlaneConstraint>() ??
                                _planeDrawer.GetComponentInParent<HandPlaneConstraint>();

            _planeDrawer.OnHandStateChanged -= HandleDrawerHandStateChanged;
            _planeDrawer.OnHandStateChanged += HandleDrawerHandStateChanged;
        }
    }

    private void TeardownPlaneDrawerCallbacks()
    {
        if (_planeDrawer != null)
            _planeDrawer.OnHandStateChanged -= HandleDrawerHandStateChanged;
    }

    private void HandleDrawerHandStateChanged(bool isRight, HandState state, Vector3 _)
    {
        if (isRight) _rightHandState = state;
        else _leftHandState = state;
    }

    private bool IsUserCurrentlyDrawing => _rightHandState == HandState.Drawing || _leftHandState == HandState.Drawing;

    private AudioManager ResolveAudioManager()
    {
        if (audioManager && audioManager.isActiveAndEnabled)
        {
            loggedMissingAudioManager = false;
            return audioManager;
        }

        var managers = Resources.FindObjectsOfTypeAll<AudioManager>()
            .Where(a => a != null && a.gameObject.scene.IsValid())
            .ToArray();
        audioManager = managers.FirstOrDefault(a => a != null && a.isActiveAndEnabled);
        if (!audioManager && managers.Length > 0)
            audioManager = managers[0];

        if (audioManager && audioManager.isActiveAndEnabled)
        {
            loggedMissingAudioManager = false;
            return audioManager;
        }

        if (!loggedMissingAudioManager)
        {
            Debug.LogWarning("[Dictation] AudioManager not available; letter audio will be skipped until it returns.");
            loggedMissingAudioManager = true;
        }

        return null;
    }

    private static char ExtractFirstAsciiLetter(string value)
    {
        if (string.IsNullOrEmpty(value)) return '\0';
        foreach (char c in value)
            if (char.IsLetter(c))
                return c;
        return '\0';
    }

    private static string ToDisplayLetter(string letter)
    {
        if (string.IsNullOrEmpty(letter)) return "?";
        string trimmed = letter.Trim();
        return trimmed.Length > 0 ? trimmed.Substring(0, 1).ToUpperInvariant() : "?";
    }

    private void TryPlayInitialLetterAudio()
    {
        if (_playedInitialAudio)
            return;
        if (!isActiveAndEnabled)
            return;
        if (lvl == null || string.IsNullOrEmpty(lvl.currentLetter))
            return;

        if (TryPlayCurrentLetterAudio())
            _playedInitialAudio = true;
    }

    private void HandleLevelManagerLetterChanged(string newLetter)
    {
        _playedInitialAudio = false;
        if (lvl != null && lvl.currentMode == LevelManager.GameMode.Dictation)
            TryPlayInitialLetterAudio();
    }

    private bool TryPlayCurrentLetterAudio()
    {
        var manager = ResolveAudioManager();
        if (manager == null)
        {
            Debug.LogWarning("[Dictation] AudioManager unavailable; cannot play letter audio.");
            return false;
        }

        char letter = ExtractFirstAsciiLetter(lvl != null ? lvl.currentLetter : null);
        if (letter != '\0')
        {
            if (manager.TryPlayLetterPronunciation(letter))
            {
                Debug.Log($"[Dictation] Played pronunciation for letter '{char.ToUpperInvariant(letter)}'.");
                return true;
            }

            Debug.LogWarning($"[Dictation] Primary audio failed for letter '{char.ToUpperInvariant(letter)}'; attempting fallback.");
            bool fallback = manager.TryPlayCurrentLetterPronunciation();
            Debug.Log(fallback
                ? "[Dictation] Fallback pronunciation succeeded."
                : "[Dictation] Fallback pronunciation failed.");
            return fallback;
        }

        bool noLetterFallback = manager.TryPlayCurrentLetterPronunciation();
        Debug.Log(noLetterFallback
            ? "[Dictation] Played pronunciation using fallback (no current letter)."
            : "[Dictation] Could not determine any letter audio to play.");
        return noLetterFallback;
    }

#if UNITY_EDITOR
    [ContextMenu("Play Current Letter Audio")]
    public void EditorPlayCurrentLetterAudio()
    {
        Debug.Log("[Dictation] Editor request: play current letter audio.");
        TryPlayCurrentLetterAudio();
    }
#endif

    private int GetCurrentStrokeCount()
    {
        return _planeDrawer != null && _planeDrawer.StrokesRoot != null
            ? _planeDrawer.StrokesRoot.childCount
            : 0;
    }

    private int GetRequiredStrokeCount()
    {
        return RequiresTwoStrokeLetter() ? 2 : 1;
    }

    private bool RequiresTwoStrokeLetter()
    {
        string normalized = NormalizeAsciiStrict(lvl != null ? lvl.currentLetter : null);
        if (string.IsNullOrEmpty(normalized))
            return false;
        return LettersRequiringTwoStrokes.Contains(normalized[0]);
    }

    private float EstimateCurrentStrokeLength()
    {
        if (_planeDrawer == null || _planeDrawer.StrokesRoot == null)
            return 0f;

        float total = 0f;
        for (int i = 0; i < _planeDrawer.StrokesRoot.childCount; i++)
        {
            var child = _planeDrawer.StrokesRoot.GetChild(i);
            if (!child) continue;
            var lr = child.GetComponent<LineRenderer>();
            if (lr == null) continue;
            int points = lr.positionCount;
            if (points < 2) continue;
            Vector3 prev = lr.GetPosition(0);
            for (int p = 1; p < points; p++)
            {
                Vector3 current = lr.GetPosition(p);
                total += Vector3.Distance(prev, current);
                prev = current;
            }
        }
        return total;
    }

    private bool HasSufficientInk()
    {
        float minLength = Mathf.Max(0f, autoGradeMinimumStrokeLength);
        if (minLength <= 0f)
            return true;
        return EstimateCurrentStrokeLength() >= minLength;
    }

    private bool MeetsStrokeRequirement()
    {
        int strokes = GetCurrentStrokeCount();
        _lastObservedStrokeTotal = strokes;
        int required = Mathf.Max(1, GetRequiredStrokeCount());
        return strokes >= required;
    }

    void Update()
    {
        if (lvl != null && drawerHost != null)
        {
            bool shouldBeOn = (lvl.currentMode == LevelManager.GameMode.Dictation);
            if (_lastDrawerActive != shouldBeOn)
            {
                drawerHost.SetActive(shouldBeOn);
                _lastDrawerActive = shouldBeOn;
            }
        }

        AutoGradeUpdate();
    }

    private void AutoGradeUpdate()
    {
        if (!autoGradeOnIdle || _planeDrawer == null || _planeDrawer.StrokesRoot == null || state != State.Drawing)
            return;

        bool canRemote = ShouldUseRemoteGrading;
        bool canLocal = ShouldUseLocalGrading && localClassifier != null && localClassifier.HasModelAsset;

        if (!canRemote && !canLocal)
            return;

        int pointCount = GetCurrentStrokeCount();
        if (pointCount != _lastStrokeCount)
        {
            _lastStrokeCount = pointCount;
            _lastStrokeChangeTime = pointCount > 0 ? Time.time : -1f;
            _autoGradeTriggeredForStroke = false;
            return;
        }

        if (pointCount == 0 || _autoGradeTriggeredForStroke || _autoGradeRunning)
            return;

        if (IsUserCurrentlyDrawing)
            return;

        bool hasEnoughStrokes = MeetsStrokeRequirement();
        if (!hasEnoughStrokes && !RequiresTwoStrokeLetter())
            return;

        if (_lastStrokeChangeTime < 0f)
            _lastStrokeChangeTime = Time.time;

        if (Time.time - _lastStrokeChangeTime >= autoGradeIdleSeconds)
        {
            _autoGradeTriggeredForStroke = true;
            _autoGradeRunning = true;
            StartCoroutine(AutoGradeRoutine());
        }
    }

    // ===== Public hooks =====
    public void PrepareForMode(LevelManager.GameMode mode)
    {
        bool isDict = (mode == LevelManager.GameMode.Dictation);
        if (drawerHost) drawerHost.SetActive(isDict);

        // Clear visuals when leaving Dictation mode OR when entering Dictation mode
        if (_hasLastMode && _lastNotifiedMode == LevelManager.GameMode.Dictation && mode != LevelManager.GameMode.Dictation)
            ClearBoardVisuals();
        else if (isDict)
            ClearBoardVisuals(); // Clear any existing visuals when entering Dictation mode

        _lastNotifiedMode = mode;
        _hasLastMode = true;

        if (tmpLetter != null)
        {
            if (isDict)
                tmpLetter.SetActive(false);
            else
                tmpLetter.SetActive(true);
        }

        UpdateUI();
        if (!isDict) SetFeedback("");
    }

    public void StartDictation()
    {
        if (!lvl) return;

        attemptCount = 0; // reset tries
        ClearBoardVisuals();
        _autoGradeTriggeredForStroke = false;
        _autoGradeRunning = false;
        _lastStrokeCount = 0;
        _lastStrokeChangeTime = -1f;
        _lastObservedStrokeTotal = 0;
        _rightHandState = HandState.Idle;
        _leftHandState = HandState.Idle;

        if (rec != null)
        {
            rec.IsRecording = false; // not using stroke capture here
            if (rec.currentRecord != null && rec.currentRecord.frames != null)
                rec.currentRecord.frames.Clear();
        }

        if (tmpLetter != null) tmpLetter.SetActive(false);

        state = State.Drawing;
        UpdateUI();
        string displayLetter = ToDisplayLetter(lvl.currentLetter);
        SetFeedback(displayLetter != "?" ? $"Write '{displayLetter}'" : "Write the letter");
        OnDictationStart?.Invoke();
        if (TryPlayCurrentLetterAudio())
            _playedInitialAudio = true;
        ClearBoardVisuals();
    }

    // ===== Buttons =====
    private void HandleGradeBtn()
    {
        if (state == State.Drawing)
            StartCoroutine(GradeFlow());
    }

    private void HandleEraseBtn()
    {
        ClearBoardVisuals();
        SetFeedback("Board cleared");
    }

    
    private void HandleRepeatBtn()
    {
        if (!isActiveAndEnabled)
            return;
        PlayRepeatAudio();
    }

    private void PlayRepeatAudio()
    {
        if (!isActiveAndEnabled) return;

        char letter = ExtractFirstAsciiLetter(lvl != null ? lvl.currentLetter : null);
        bool played = TryPlayCurrentLetterAudio();

        if (letter != '\0')
        {
            SetFeedback(played
                ? $"Listen: '{char.ToUpperInvariant(letter)}'"
                : $"Letter: '{char.ToUpperInvariant(letter)}'");
        }
        else if (!played)
        {
            SetFeedback("Listen again when you're ready.");
        }
    }

    private Letter3DDisplay ResolveHintDisplay()
    {
        if (hintDisplay != null)
            return hintDisplay;

        var candidates = Resources.FindObjectsOfTypeAll<Letter3DDisplay>();
        foreach (var candidate in candidates)
        {
            if (candidate == null) continue;
            var go = candidate.gameObject;
            if (!go) continue;
            var scene = go.scene;
            if (!scene.IsValid() || !scene.isLoaded)
                continue;

            hintDisplay = candidate;
            break;
        }

        return hintDisplay;
    }

    private void ShowHintDisplay()
    {
        var display = ResolveHintDisplay();
        if (display != null)
        {
            if (!display.isActiveAndEnabled)
                display.enabled = true;

            string letter = lvl != null ? lvl.currentLetter : null;
            display.ShowHintForLetter(letter, resetAttempts: true);
        }
    }
    public void TriggerLocalDebug()
    {
        if (!isActiveAndEnabled)
        {
            Debug.LogWarning("[Dictation] Cannot run local debug while DictationManager is disabled.");
            return;
        }

        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Dictation] Local debug can only run in Play Mode.");
            return;
        }

        TriggerLocalDebugInternal();
    }

    private void HandleDebugLocalBtn()
    {
        TriggerLocalDebugInternal();
    }

    private void TriggerLocalDebugInternal()
    {
        EnsureLocalClassifierConfigured();

        if (localClassifier == null)
        {
            Debug.LogWarning("[Dictation] Debug triggered but local classifier is not assigned.");
            return;
        }

        if (!localClassifier.HasModelAsset)
        {
            Debug.LogWarning("[Dictation] Debug triggered but local classifier has no model asset assigned.");
            return;
        }

        if (_debugLocalRoutine != null)
            StopCoroutine(_debugLocalRoutine);

        Debug.Log("[Dictation] Running local classifier debug sample.");
        _debugLocalRoutine = StartCoroutine(RunLocalDebugSample());
    }

    private struct LocalEvaluation
    {
        public bool HasWriting;
        public string Raw;
        public string Normalized;
        public float Confidence;
        public bool InTopThree;
        public bool NormalizedMatch;
        public bool MeetsThreshold;
        public bool IsSuccess;
        public bool Top1Match;
        public string Top1Normalized;
        public float Top1Confidence;
        public int MatchRank;
    }

    private bool TryResolveRemoteOutcome(LetterPrediction prediction, string expectedNormalized, out string chosenRaw,
        out string chosenNormalized, out float chosenConfidence, out bool hasWriting, out bool highConfidenceRemoteWrong, out bool topMatch)
    {
        chosenRaw = string.Empty;
        chosenNormalized = string.Empty;
        chosenConfidence = 0f;
        highConfidenceRemoteWrong = false;
        topMatch = false;

        var eval = EvaluateLocalPrediction(prediction, expectedNormalized);

        hasWriting = eval.HasWriting;

        int matchRank = eval.MatchRank;
        bool hasMatch = matchRank >= 0;
        bool matchCorrect = hasMatch && LettersEquivalent(eval.Normalized, expectedNormalized);
        float matchConfidence = eval.Confidence;
        string matchRaw = hasMatch ? eval.Raw : string.Empty;
        string matchNormalized = hasMatch ? eval.Normalized : string.Empty;

        string topRaw = prediction.TopLetter ?? string.Empty;
        string topNormalized = NormalizeAsciiStrict(topRaw);
        bool topCorrect = eval.Top1Match && LettersEquivalent(topNormalized, expectedNormalized);
        float topConfidence = eval.Top1Confidence;

        float requiredTopConfidence = Mathf.Max(autoGradeMinimumConfidence, remoteConfidenceThreshold);
        float requiredSecondConfidence = Mathf.Max(autoGradeMinimumConfidence, remoteSecondRankConfidenceThreshold);
        float wrongConfidenceThreshold = Mathf.Max(autoGradeWrongMinimumConfidence, remoteWrongConfidenceThreshold);

        bool acceptTop1 = topCorrect && topConfidence >= requiredTopConfidence;
        bool acceptTop2 = hasMatch && matchCorrect && matchRank <= 1 && matchConfidence >= requiredSecondConfidence;
        bool shouldForceWrong = !topCorrect && topConfidence >= wrongConfidenceThreshold && (!hasMatch || !matchCorrect || matchConfidence < requiredSecondConfidence);

        if (acceptTop2 && hasMatch && matchCorrect && matchRank <= 1)
        {
            chosenRaw = matchRaw;
            chosenNormalized = matchNormalized;
            chosenConfidence = matchConfidence;
            topMatch = true;
            return true;
        }

        if (acceptTop1)
        {
            chosenRaw = topRaw;
            chosenNormalized = topNormalized;
            chosenConfidence = topConfidence;
            topMatch = true;
            return true;
        }

        chosenRaw = topRaw;
        chosenNormalized = topNormalized;
        chosenConfidence = topConfidence;
        highConfidenceRemoteWrong = shouldForceWrong;
        topMatch = topCorrect;
        return false;
    }

    private LocalEvaluation EvaluateLocalPrediction(LetterPrediction prediction, string expectedNormalized)
    {
        var result = new LocalEvaluation
        {
            HasWriting = prediction != null && prediction.HasWriting,
            Raw = string.Empty,
            Normalized = string.Empty,
            Confidence = 0f,
            InTopThree = false,
            NormalizedMatch = false,
            MeetsThreshold = false,
            IsSuccess = false,
            Top1Match = false,
            Top1Normalized = string.Empty,
            Top1Confidence = 0f,
            MatchRank = -1
        };

        if (prediction == null)
            return result;

        string topRaw = prediction.HasWriting ? (prediction.TopLetter ?? string.Empty) : string.Empty;
        float topConfidence = prediction.TopConfidence;
        string topNormalized = NormalizeAsciiStrict(topRaw);
        bool top1Match = LettersEquivalent(topNormalized, expectedNormalized);

        string matchedRaw = topRaw;
        float matchedConfidence = topConfidence;
        string matchedNormalized = topNormalized;
        bool inTopThreeMatch = top1Match;
        int matchRank = top1Match ? 0 : -1;

        if (!top1Match && prediction.Ranked != null)
        {
            int limit = Mathf.Min(3, prediction.Ranked.Count);
            for (int i = 0; i < limit; i++)
            {
                var candidate = prediction.Ranked[i];
                string candidateNorm = NormalizeAsciiStrict(candidate.letter);
                if (LettersEquivalent(candidateNorm, expectedNormalized))
                {
                    matchedRaw = candidate.letter;
                    matchedConfidence = candidate.probability;
                    matchedNormalized = candidateNorm;
                    inTopThreeMatch = true;
                    matchRank = i;
                    break;
                }
            }
        }

        bool normalizedMatch = LettersEquivalent(matchedNormalized, expectedNormalized);
        bool meetsThreshold = prediction.HasWriting && normalizedMatch && matchedConfidence >= localConfidenceThreshold;
        bool success = (inTopThreeMatch && normalizedMatch) || meetsThreshold;

        result.HasWriting = prediction.HasWriting || inTopThreeMatch;
        result.Raw = matchedRaw ?? string.Empty;
        result.Normalized = matchedNormalized ?? string.Empty;
        result.Confidence = matchedConfidence;
        result.InTopThree = inTopThreeMatch;
        result.NormalizedMatch = normalizedMatch;
        result.MeetsThreshold = meetsThreshold;
        result.IsSuccess = success;
        result.Top1Match = top1Match;
        result.Top1Normalized = topNormalized ?? string.Empty;
        result.Top1Confidence = topConfidence;
        result.MatchRank = matchRank;
        return result;
    }

    [Serializable]
    private class RemotePredictionEntry
    {
        public string letter;
        public float confidence;
    }

    [Serializable]
    private class RemotePredictionResponse
    {
        public RemotePredictionEntry[] top_predictions;
    }

    private string BuildRemoteUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(remoteClassifierBaseUrl))
            return path ?? string.Empty;
        if (string.IsNullOrEmpty(path))
            return remoteClassifierBaseUrl;
        return $"{remoteClassifierBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private IEnumerator RemotePredictTop3(Texture2D snap, Action<LetterPrediction> onDone)
    {
        if (!ShouldUseRemoteGrading)
        {
            onDone?.Invoke(null);
            yield break;
        }

        if (snap == null)
        {
            onDone?.Invoke(null);
            yield break;
        }

        byte[] pngBytes = null;
        try
        {
            pngBytes = snap.EncodeToPNG();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Dictation][Remote] Failed to encode PNG: {ex.Message}");
        }

        if (pngBytes == null || pngBytes.Length == 0)
        {
            onDone?.Invoke(null);
            yield break;
        }

        var form = new WWWForm();
        form.AddBinaryData("file", pngBytes, "capture.png", "image/png");

        string url = BuildRemoteUrl("/predict/top3");

        using (var req = UnityWebRequest.Post(url, form))
        {
            req.timeout = Mathf.Clamp(Mathf.RoundToInt(remoteRequestTimeoutSeconds), 1, 120);
            if (!string.IsNullOrWhiteSpace(remoteApiKey))
                req.SetRequestHeader("x-api-key", remoteApiKey);
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Dictation][Remote] Request failed ({req.responseCode}): {req.error}");
                onDone?.Invoke(null);
                yield break;
            }

            string json = req.downloadHandler.text;
            RemotePredictionResponse response = null;
            try
            {
                response = JsonUtility.FromJson<RemotePredictionResponse>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Dictation][Remote] Failed to parse response: {ex.Message}");
            }

            if (response?.top_predictions == null || response.top_predictions.Length == 0)
            {
                Debug.LogWarning("[Dictation][Remote] Empty prediction response.");
                onDone?.Invoke(null);
                yield break;
            }

            var ranked = new List<(string letter, float probability)>(response.top_predictions.Length);
            foreach (var entry in response.top_predictions)
            {
                if (entry == null) continue;
                string letter = entry.letter ?? string.Empty;
                float confidence = Mathf.Clamp01(entry.confidence);
                ranked.Add((letter, confidence));
            }

            if (ranked.Count == 0)
            {
                onDone?.Invoke(null);
                yield break;
            }

            bool hasWriting = ranked[0].Item2 >= remoteConfidenceThreshold;
            string topLetter = ranked[0].Item1;
            float topConfidence = ranked[0].Item2;
            var prediction = new LetterPrediction(hasWriting, topLetter, topConfidence, ranked);
            onDone?.Invoke(prediction);
        }
    }

    // ===== Flow =====
    private IEnumerator GradeFlow()
    {
        if (gradingButtonGO) gradingButtonGO.SetActive(false);
        if (eraseButtonGO)   eraseButtonGO.SetActive(false);

        EnsureLocalClassifierConfigured();

        // Capture the board EXACTLY as rendered by boardCamera
        Texture2D snap = null;
        yield return StartCoroutine(CaptureBoardExactCo(t => snap = t));
        if (snap == null)
        {
            Debug.LogWarning("[Dictation] Capture failed.");
            YieldFailImmediate();
            SetFeedback("Capture failed");
            yield return new WaitForSeconds(waitAfterReplay);
            Advance();
            yield break;
        }

        state = State.WaitingForResponse;
        UpdateUI();
        SetFeedback("Grading...");

        bool attemptedRemote = ShouldUseRemoteGrading;
        bool remoteFailedHard = false;
        LetterPrediction remotePrediction = null;

        if (attemptedRemote)
        {
            bool receivedResponse = false;
            yield return StartCoroutine(RemotePredictTop3(snap, p =>
            {
                remotePrediction = p;
                receivedResponse = true;
            }));

            if (!receivedResponse || remotePrediction == null)
            {
                Debug.LogWarning("[Dictation] Remote classifier inference failed or returned empty result.");
                remoteFailedHard = true;
                if (fallbackToVisionOnLocalFailure)
                    attemptedRemote = false;
            }
        }

        bool attemptedLocal = !attemptedRemote && ShouldUseLocalGrading;
        bool localFailedHard = false;
        LetterPrediction localPrediction = null;

        if (attemptedLocal)
        {
            if (localClassifier == null)
            {
                Debug.LogWarning("[Dictation] Local grading enabled but classifier reference is missing.");
                localFailedHard = true;
            }
            else
            {
                try
                {
                    localPrediction = localClassifier.Predict(snap);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Dictation] Local classifier inference failed: {ex.Message}");
                    localFailedHard = true;
                }
            }

            if (localFailedHard && fallbackToVisionOnLocalFailure)
            {
                attemptedLocal = false;
            }
        }

        string ocrText = null;
        if (!attemptedRemote && !attemptedLocal)
        {
            yield return StartCoroutine(BeamRecognize(snap, t => ocrText = t));
        }

        Destroy(snap);

        string expected = NormalizeAsciiStrict(lvl != null ? lvl.currentLetter : null);
        string targetLetter = lvl?.currentLetter ?? "?";
        string displayTargetLetter = ToDisplayLetter(targetLetter);
        string gotRaw;
        string gotNormalized;
        bool correct;
        bool usedRemote = attemptedRemote && !remoteFailedHard;
        bool usedLocal = false;
        float primaryConfidence = 0f;
        bool hasWriting = true;
        bool normalizedMatch = false;
        bool highConfidenceRemoteWrong = false;

        bool insufficientInk = !HasSufficientInk();

        if (usedRemote)
        {
            LetterPrediction prediction = remotePrediction ?? LetterPrediction.NoWriting;
            bool topMatch;
            bool accepted = TryResolveRemoteOutcome(prediction, expected, out gotRaw, out gotNormalized,
                out primaryConfidence, out hasWriting, out highConfidenceRemoteWrong, out topMatch);

            correct = accepted;
            normalizedMatch = topMatch;

            DebugCodepoint("[Remote] RAW", gotRaw);
            Debug.Log($"[Remote] NORMALIZED got='{gotNormalized}' expected='{expected}' conf={primaryConfidence:F3} accepted={accepted} highWrong={highConfidenceRemoteWrong} hasWriting={hasWriting}");
        }
        else
        {
            gotRaw = ocrText ?? string.Empty;
            DebugCodepoint("[Vision] RAW", gotRaw);
            gotNormalized = NormalizeAsciiStrict(gotRaw);
            correct = LettersEquivalent(gotNormalized, expected);
            Debug.Log($"[Vision] NORMALIZED got='{gotNormalized}' expected='{expected}'");
        }

        if (!correct && (!hasWriting || insufficientInk))
        {
            string reason = !hasWriting ? "no writing detected" : "insufficient ink";
            Debug.Log($"[Dictation][Grade] {reason}; treating as stray mark.");
            state = State.Drawing;
            UpdateUI();
            SetFeedback(!hasWriting
                ? "No writing yet. Keep writing or erase stray marks."
                : "Keep writing a bit more before grading.");
            yield break;
        }

        bool shouldTreatAsStray =
            !correct &&
            usedRemote &&
            !normalizedMatch &&
            !highConfidenceRemoteWrong;

        if (shouldTreatAsStray)
        {
            Debug.Log("[Dictation][Grade] Prediction does not strongly indicate the wrong letter; treating as stray mark.");
            state = State.Drawing;
            UpdateUI();
            SetFeedback("Keep writing or erase stray marks.");
            yield break;
        }

        bool lowConfidenceWrong = !correct
                                   && hasWriting
                                   && primaryConfidence >= 0f
                                   && primaryConfidence < autoGradeWrongMinimumConfidence
                                   && (usedRemote || usedLocal);
        if (lowConfidenceWrong)
        {
            Debug.Log($"[Dictation][Grade] Low-confidence wrong ({primaryConfidence:F3}) treated as stray mark.");
            state = State.Drawing;
            UpdateUI();
            SetFeedback("Looks like a stray mark. Erase it or keep writing.");
            yield break;
        }

        state = correct ? State.GradedAccept : State.GradedReject;
        UpdateUI();

        if (correct)
        {
            OnDictationGraded?.Invoke(100f);
            OnLetterCorrect?.Invoke();
            if (usedRemote)
                SetFeedback($"Correct (remote '{ToDisplayLetter(gotRaw)}' @ {Mathf.RoundToInt(primaryConfidence * 100f)}%)");
            else if (usedLocal)
                SetFeedback($"Correct (local '{ToDisplayLetter(gotRaw)}' @ {Mathf.RoundToInt(primaryConfidence * 100f)}%)");
            else
                SetFeedback($"Correct (saw: '{ToDisplayLetter(gotRaw)}')");
            ClearBoardVisuals();
            yield return new WaitForSeconds(waitAfterCorrect);
            Advance();
        }
        else
        {
            OnDictationGraded?.Invoke(0f);
            OnLetterIncorrect?.Invoke();
            if (attemptCount <= 0)
            {
                // First mistake: show image hint (same as phoneme manager)
                ShowHintDisplay();
                string retryMsg;
                if (usedRemote)
                {
                    if (!hasWriting)
                        retryMsg = "No writing detected. Try again.";
                    else if (normalizedMatch)
                        retryMsg = $"Not quite yet ({Mathf.RoundToInt(primaryConfidence * 100f)}% confidence). Try again.";
                    else if (highConfidenceRemoteWrong)
                        retryMsg = $"Remote is very confident it's '{ToDisplayLetter(gotRaw)}' ({Mathf.RoundToInt(primaryConfidence * 100f)}%). Watch the demo and retry.";
                    else
                        retryMsg = $"Remote saw '{ToDisplayLetter(gotRaw)}'. Try '{displayTargetLetter}' again.";
                }
                else if (usedLocal)
                {
                    if (!hasWriting)
                        retryMsg = "No writing detected. Try again.";
                    else if (normalizedMatch)
                        retryMsg = $"Not quite yet ({Mathf.RoundToInt(primaryConfidence * 100f)}% confidence). Try again.";
                    else
                        retryMsg = $"Local saw '{ToDisplayLetter(gotRaw)}'. Try '{displayTargetLetter}' again.";
                }
                else
                {
                    retryMsg = $"Hint shown. Try '{displayTargetLetter}' again.";
                }

                ClearBoardVisuals();
                SetFeedback(retryMsg);
                attemptCount = 1;
                state = State.Drawing;
                UpdateUI();
                yield break; // give user another try
            }
            else
            {
                // Second mistake: replay with stroke filling + pops, then move on
                if ((usedRemote || usedLocal) && !hasWriting)
                    SetFeedback("No writing detected. Watch the demo and try again.");
                else
                    SetFeedback($"Watch the demo of '{displayTargetLetter}'...");
                yield return new WaitForSeconds(waitBeforeRef);
                string letterForPrompt = lvl != null ? lvl.currentLetter : string.Empty;
                ClearBoardVisuals();
                yield return ShowReplayPrompt(letterForPrompt);
                SetFeedback("Your turn!");
                yield return new WaitForSeconds(waitAfterReplay);
                attemptCount = 2;
                suppressTmpOnAdvance = true;
                Advance();
            }
        }
    }

    private IEnumerator AutoGradeRoutine()
    {
        if (!autoGradeOnIdle)
        {
            _autoGradeRunning = false;
            _autoGradeTriggeredForStroke = false;
            yield break;
        }

        bool canRemote = ShouldUseRemoteGrading;
        bool canLocal = false;

        int strokeCount = GetCurrentStrokeCount();
        _lastObservedStrokeTotal = strokeCount;
        int requiredStrokes = Mathf.Max(1, GetRequiredStrokeCount());
        bool hasEnoughStrokes = strokeCount >= requiredStrokes;
        bool requiresTwoStrokeLetter = RequiresTwoStrokeLetter();
        bool allowEarlyWrongCheck = requiresTwoStrokeLetter && strokeCount > 0;
        bool hasSufficientInk = HasSufficientInk();

        if (!hasSufficientInk)
        {
            _autoGradeRunning = false;
            _autoGradeTriggeredForStroke = false;
            _lastStrokeChangeTime = Time.time;
            yield break;
        }

        if (!isActiveAndEnabled || state != State.Drawing || IsUserCurrentlyDrawing || (!hasEnoughStrokes && !allowEarlyWrongCheck))
        {
            _autoGradeRunning = false;
            _autoGradeTriggeredForStroke = false;
            _lastStrokeChangeTime = Time.time;
            yield break;
        }

        Texture2D snap = null;
        yield return StartCoroutine(CaptureBoardExactCo(t => snap = t));
        if (snap == null)
        {
            _autoGradeRunning = false;
            _autoGradeTriggeredForStroke = false;
            _lastStrokeChangeTime = Time.time;
            yield break;
        }

        if (canRemote)
        {
            LetterPrediction remotePrediction = null;
            bool received = false;
            yield return StartCoroutine(RemotePredictTop3(snap, p =>
            {
                remotePrediction = p;
                received = true;
            }));

            Destroy(snap);

            if (!received || remotePrediction == null)
            {
                _autoGradeRunning = false;
                _autoGradeTriggeredForStroke = false;
                _lastStrokeChangeTime = Time.time;
                yield break;
            }

            string expectedRemote = NormalizeAsciiStrict(lvl != null ? lvl.currentLetter : null);
            var remoteEval = EvaluateLocalPrediction(remotePrediction, expectedRemote);
            bool hasAnyWriting = remoteEval.HasWriting;

            if (!hasAnyWriting)
            {
                Debug.Log("[Dictation][AutoGrade] Remote detected no writing; skipping auto-grade.");
                _autoGradeRunning = false;
                _autoGradeTriggeredForStroke = false;
                _lastStrokeChangeTime = Time.time;
                yield break;
            }

            bool topCorrect = remoteEval.Top1Match && LettersEquivalent(remoteEval.Top1Normalized, expectedRemote);
            float topConfidence = remoteEval.Top1Confidence;
            int matchRank = remoteEval.MatchRank;
            bool hasMatch = matchRank >= 0;
            bool matchCorrect = hasMatch && LettersEquivalent(remoteEval.Normalized, expectedRemote);
            float matchConfidence = remoteEval.Confidence;

            float requiredTopConfidence = Mathf.Max(autoGradeMinimumConfidence, remoteConfidenceThreshold);
            float requiredSecondConfidence = Mathf.Max(autoGradeMinimumConfidence, remoteSecondRankConfidenceThreshold);
            float wrongConfidenceThreshold = Mathf.Max(autoGradeWrongMinimumConfidence, remoteWrongConfidenceThreshold);

            bool acceptTop1 = topCorrect && topConfidence >= requiredTopConfidence;
            bool acceptTop2 = hasMatch && matchCorrect && matchRank <= 1 && matchConfidence >= requiredSecondConfidence;
            bool shouldForceWrong = !topCorrect && topConfidence >= wrongConfidenceThreshold && (!hasMatch || !matchCorrect || matchConfidence < requiredSecondConfidence);

            DebugCodepoint("[Auto][Remote] RAW", remotePrediction.TopLetter ?? string.Empty);
            Debug.Log($"[Dictation][AutoGrade] REMOTE top1='{remoteEval.Top1Normalized}' expected='{expectedRemote}' confTop1={topConfidence:F3} matchRank={matchRank} matchConf={matchConfidence:F3} strokes={_lastObservedStrokeTotal}");

            if ((acceptTop1 || acceptTop2) && hasEnoughStrokes)
            {
                state = State.GradedAccept;
                UpdateUI();
                OnDictationGraded?.Invoke(100f);
                OnLetterCorrect?.Invoke();

                string display;
                float displayConfidence;
                if (acceptTop2 && hasMatch && matchCorrect && matchRank <= 1)
                {
                    display = ToDisplayLetter(string.IsNullOrEmpty(remoteEval.Raw) ? expectedRemote : remoteEval.Raw);
                    displayConfidence = matchConfidence;
                }
                else
                {
                    display = ToDisplayLetter(string.IsNullOrEmpty(remotePrediction.TopLetter) ? expectedRemote : remotePrediction.TopLetter);
                    displayConfidence = topConfidence;
                }

                SetFeedback($"Great! ('{display}' @ {Mathf.RoundToInt(displayConfidence * 100f)}%)");
                ClearBoardVisuals();
                yield return new WaitForSeconds(waitAfterCorrect);
                Advance();
            }
            else if (shouldForceWrong && (hasEnoughStrokes || allowEarlyWrongCheck))
            {
                string display = ToDisplayLetter(string.IsNullOrEmpty(remotePrediction.TopLetter) ? expectedRemote : remotePrediction.TopLetter);

                if (attemptCount <= 0)
                {
                    OnDictationGraded?.Invoke(0f);
                    OnLetterIncorrect?.Invoke();

                    attemptCount = 1;
                    state = State.Drawing;
                    UpdateUI();

                    ShowHintDisplay();

                    ClearBoardVisuals();
                    SetFeedback($"Remote thinks it's '{display}' ({Mathf.RoundToInt(topConfidence * 100f)}%). Try '{expectedRemote}' again.");
                    _lastStrokeChangeTime = Time.time;
                    _autoGradeTriggeredForStroke = false;
                    yield break;
                }

                state = State.GradedReject;
                UpdateUI();
                OnDictationGraded?.Invoke(0f);
                OnLetterIncorrect?.Invoke();
                SetFeedback($"Remote is very confident it's '{display}' ({Mathf.RoundToInt(topConfidence * 100f)}%). Watch the demo and retry.");
                yield return new WaitForSeconds(waitBeforeRef);
                string letterForPrompt = lvl != null ? lvl.currentLetter : string.Empty;
                ClearBoardVisuals();
                yield return ShowReplayPrompt(letterForPrompt);
                SetFeedback("Your turn!");
                yield return new WaitForSeconds(waitAfterReplay);
                attemptCount = 2;
                suppressTmpOnAdvance = true;
                Advance();
            }
            else
            {
                _lastStrokeChangeTime = Time.time;
                _autoGradeTriggeredForStroke = false;
            }

            _autoGradeRunning = false;
            yield break;
        }

        _autoGradeRunning = false;
    }

    private IEnumerator RunLocalDebugSample()
    {
        string previousFeedback = feedbackText ? feedbackText.text : string.Empty;
        SetFeedback("Local debug...");

        Texture2D snap = null;
        yield return StartCoroutine(CaptureBoardExactCo(t => snap = t));
        if (snap == null)
        {
            SetFeedback("Debug capture failed");
            yield return new WaitForSeconds(1f);
            SetFeedback(previousFeedback);
            _debugLocalRoutine = null;
            yield break;
        }

        if (localClassifier == null || !localClassifier.HasModelAsset)
        {
            Debug.LogWarning("[Dictation] Local debug aborted because classifier became unavailable.");
            Destroy(snap);
            SetFeedback("Local debug unavailable");
            yield return new WaitForSeconds(1.5f);
            SetFeedback(previousFeedback);
            _debugLocalRoutine = null;
            yield break;
        }

        LetterPrediction prediction = null;
        bool inferenceError = false;
        string inferenceMessage = null;
        try
        {
            prediction = localClassifier.Predict(snap);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Dictation] Local debug failed: {ex.Message}");
            inferenceError = true;
            inferenceMessage = "Local debug error";
        }

        Destroy(snap);

        if (inferenceError)
        {
            SetFeedback(inferenceMessage ?? "Local debug error");
            yield return new WaitForSeconds(1.5f);
            SetFeedback(previousFeedback);
            _debugLocalRoutine = null;
            yield break;
        }

        if (prediction == null)
        {
            SetFeedback("Local debug: no result");
            yield return new WaitForSeconds(1.5f);
            SetFeedback(previousFeedback);
            _debugLocalRoutine = null;
            yield break;
        }

        string msg;
        if (!prediction.HasWriting)
        {
            msg = "Local debug: no writing detected";
        }
        else
        {
            float pct = Mathf.Round(prediction.TopConfidence * 100f);
            msg = $"Local debug: '{prediction.TopLetter}' @ {pct}%";
        }

        SetFeedback(msg);

        if (prediction.Ranked != null && prediction.Ranked.Count > 0)
        {
            int topCount = Mathf.Min(3, prediction.Ranked.Count);
            var sb = new StringBuilder();
            sb.Append("[Dictation][LocalDebug] Top predictions: ");
            for (int i = 0; i < topCount; i++)
            {
                var entry = prediction.Ranked[i];
                sb.Append(entry.Item1);
                sb.Append("(");
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F3}", entry.Item2);
                sb.Append(")");
                if (i < topCount - 1) sb.Append(", ");
            }
            Debug.Log(sb.ToString());
        }

        yield return new WaitForSeconds(2f);
        SetFeedback(previousFeedback);
        _debugLocalRoutine = null;
    }

    private void Advance()
    {
        state = State.Idle;
        UpdateUI();
        SetFeedback("");

        if (!suppressTmpOnAdvance && tmpLetter != null) tmpLetter.SetActive(true);

        OnDictationComplete?.Invoke();
        suppressTmpOnAdvance = false;
    }

    // ===== UI helpers =====
    private void UpdateUI()
    {
        bool inDict = (lvl != null && lvl.currentMode == LevelManager.GameMode.Dictation);
        bool canDraw = (state == State.Drawing);

        if (gradingButtonGO) gradingButtonGO.SetActive(inDict && canDraw);
        if (eraseButtonGO)   eraseButtonGO.SetActive(inDict && canDraw);
        if (repeatButtonGO)  repeatButtonGO.SetActive(inDict);
        if (debugLocalButtonGO) debugLocalButtonGO.SetActive(inDict && localClassifier != null && localClassifier.HasModelAsset);
        if (feedbackText)    feedbackText.gameObject.SetActive(inDict);
    }

    private void SetFeedback(string msg)
    {
        if (feedbackText) feedbackText.text = msg ?? "";
    }

    private IEnumerator ShowReplayPrompt(string letter)
    {
        GameObject root = GetReplayPromptRoot();

        if (replayPromptText != null)
            replayPromptText.text = string.IsNullOrEmpty(letter) ? string.Empty : letter.ToLowerInvariant();

        if (root)
            root.SetActive(true);

        if (tmpLetter != null)
            tmpLetter.SetActive(true);

        var manager = ResolveAudioManager();
        if (manager)
            manager.PlayPop();

        float promptDuration = Mathf.Max(0f, replayPromptDuration);
        float tmpDuration = Mathf.Max(0f, tmpReplayFlashDuration);
        float waitDuration = Mathf.Max(promptDuration, tmpDuration);

        if (waitDuration > 0f)
        {
            float elapsed = 0f;
            while (elapsed < waitDuration)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }
        }
        else
        {
            yield return null;
        }

        if (tmpLetter != null)
            tmpLetter.SetActive(false);

        HideReplayPrompt();
    }

    private GameObject GetReplayPromptRoot()
    {
        if (replayPromptRoot) return replayPromptRoot;
        return replayPromptText != null ? replayPromptText.gameObject : null;
    }

    private void HideReplayPrompt()
    {
        if (replayPromptText != null)
            replayPromptText.text = string.Empty;
        GameObject root = GetReplayPromptRoot();
        if (root) root.SetActive(false);
    }

    // ===== Board ops =====
    private void ClearBoardVisuals()
    {
        if (canvas != null) canvas.ClearVisualization();
        if (_planeDrawer != null) _planeDrawer.ClearStrokes();
        HideReplayPrompt();

        _autoGradeTriggeredForStroke = false;
        _autoGradeRunning = false;
        _lastStrokeCount = 0;
        _lastStrokeChangeTime = -1f;
        _lastObservedStrokeTotal = 0;
        _rightHandState = HandState.Idle;
        _leftHandState = HandState.Idle;

        // Also clear VisualEffectManager visuals (spheres, cylinders, etc.)
        var visualEffectManager = GetComponent<VisualEffectManager>();
        if (visualEffectManager != null)
        {
            // Use reflection to call ClearAllVisuals since it's private
            var clearMethod = typeof(VisualEffectManager).GetMethod("ClearAllVisuals", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            clearMethod?.Invoke(visualEffectManager, null);
        }

        // Clean up only transient visuals we own; leave keypoint spheres (CanvasManager manages them via pooling)
        var visuals = FindObjectsOfType<GameObject>().Where(go =>
             (go.name.StartsWith("TraceSegment") ||
              go.name.StartsWith("ReplayDot_")) &&
             !go.GetComponent<TMPro.TextMeshPro>() &&
             !go.GetComponent<TMPro.TextMeshProUGUI>() &&
             !go.GetComponent<TextMesh>() &&
             go.GetComponent<Renderer>() != null);

        foreach (var v in visuals)
        {
            if (v != null && v.activeInHierarchy)
                Destroy(v);
        }

        if (drawerHost != null)
        {
            var trails = drawerHost.GetComponentsInChildren<TrailRenderer>(true);
            foreach (var tr in trails) tr.Clear();

            var lines = drawerHost.GetComponentsInChildren<LineRenderer>(true);
            foreach (var lr in lines) lr.positionCount = 0;

            for (int i = drawerHost.transform.childCount - 1; i >= 0; i--)
            {
                var child = drawerHost.transform.GetChild(i);
                if (child.name.StartsWith("Stroke", StringComparison.OrdinalIgnoreCase) ||
                    child.name.StartsWith("Line",   StringComparison.OrdinalIgnoreCase) ||
                    child.name.StartsWith("ReplayDot_", StringComparison.OrdinalIgnoreCase))
                {
                    Destroy(child.gameObject);
                }
            }
        }

        // Clear tracked replay dots
        replayDots.Clear();
    }

    // ===== Replay (unchanged) =====
    private IEnumerator ReplayReference()
    {
        if (rec != null && lvl != null)
            rec.LoadRecording(lvl.currentLetter);

        yield return null;

        // Build spheres even in Dictation (bypass gating) using local->world conversion for robustness
        if (canvas != null)
            canvas.CreateVisualizationForAllPointsEvenInDictation();

        if (canvas != null && canvas.activeSpheres != null)
        {
            foreach (var s in canvas.activeSpheres) s.SetActive(false);

            float replayTotal = 2.0f; // slowed down per request
            float step = replayTotal / Mathf.Max(canvas.activeSpheres.Count, 1);

            foreach (var s in canvas.activeSpheres)
            {
                s.SetActive(true);
                s.transform.localScale = Vector3.zero;

                var rend = s.GetComponent<Renderer>();
                if (rend != null && rend.material != null) rend.material.color = Color.yellow;

                float t = 0f;
                while (t < step)
                {
                    s.transform.localScale = Vector3.one * canvas.sphereRadius * 5f * (t / step);
                    t += Time.deltaTime;
                    yield return null;
                }
                s.transform.localScale = Vector3.one * canvas.sphereRadius * 5f;
                var manager = ResolveAudioManager();
                if (manager) manager.PlayPop();
            }
        }
    }

    // Enhanced replay: fill connecting segments with dot marks at ~pointDistance, pop on each
    // Replay showing spheres sequentially and drawing a connecting stroke (cylinder) from n-1 -> n
    private IEnumerator ReplayReferenceWithSegments()
    {
        if (rec != null && lvl != null)
            rec.LoadRecording(lvl.currentLetter);

        yield return null;

        // Build spheres even in Dictation (provides point anchors). Use local->world conversion
        if (canvas != null)
            canvas.CreateVisualizationForAllPointsEvenInDictation();

        if (canvas == null || canvas.activeSpheres == null || canvas.activeSpheres.Count == 0) yield break;

        // Hide all spheres initially
        foreach (var s in canvas.activeSpheres) if (s) s.SetActive(false);

        float replayTotal = 1.25f;
        float step = replayTotal / Mathf.Max(canvas.activeSpheres.Count, 1);

        Transform root = ResolveReplayParent(canvas != null && canvas.canvasPlane != null
            ? canvas.canvasPlane.transform
            : (canvas.activeSpheres.Count > 0 && canvas.activeSpheres[0] ? canvas.activeSpheres[0].transform.parent : null));

        GameObject prev = null;
        for (int i = 0; i < canvas.activeSpheres.Count; i++)
        {
            var cur = canvas.activeSpheres[i];
            if (!cur) continue;

            // reveal current sphere with a quick scale-in
            cur.SetActive(true);
            Vector3 targetScale = Vector3.one * canvas.sphereRadius * 5f;
            cur.transform.localScale = Vector3.zero;
            float t = 0f;
            while (t < step)
            {
                cur.transform.localScale = targetScale * (t / step);
                t += Time.deltaTime;
                yield return null;
            }
            cur.transform.localScale = targetScale;

            var manager = ResolveAudioManager();
            if (manager) manager.PlayPop();

            CreateReplayDot(cur.transform.position, root);

            if (prev)
            {
                CreateReplaySegment(prev.transform.position, cur.transform.position, root);
            }
            prev = cur;
        }
    }

    private Transform ResolveReplayParent(Transform overrideParent = null)
    {
        if (overrideParent != null)
            return overrideParent;
        if (canvas != null && canvas.canvasPlane != null)
            return canvas.canvasPlane.transform;
        if (_planeDrawer != null && _planeDrawer.StrokesRoot != null)
            return _planeDrawer.StrokesRoot;
        if (_planeDrawer != null)
            return _planeDrawer.transform;
        if (drawerHost != null)
            return drawerHost.transform;
        return null;
    }

    private Vector3 GetReplayPlaneNormal(Transform parent)
    {
        if (_planeConstraint != null)
            return _planeConstraint.PlaneNormal.normalized;

        if (parent != null)
        {
            Vector3 normal = parent.forward;
            if (normal.sqrMagnitude <= 1e-6f)
                normal = parent.up;
            if (normal.sqrMagnitude > 1e-6f)
                return normal.normalized;
        }

        if (_planeDrawer != null)
        {
            Vector3 normal = _planeDrawer.transform.forward;
            if (normal.sqrMagnitude > 1e-6f)
                return normal.normalized;
        }

        return Vector3.forward;
    }

    private Vector3 ProjectReplayPoint(Vector3 worldPos, Transform parent, bool applyLift = false)
    {
        Vector3 projected = worldPos;

        if (_planeConstraint != null)
        {
            projected = _planeConstraint.ProjectToPlane(worldPos);
        }
        else if (parent != null)
        {
            Vector3 normal = GetReplayPlaneNormal(parent);
            Plane plane = new Plane(normal, parent.position);
            projected = plane.ClosestPointOnPlane(worldPos);
        }

        if (applyLift && replayDotLift > 0f)
            projected += GetReplayPlaneNormal(parent) * replayDotLift;

        return projected;
    }

    private void CreateReplaySegment(Vector3 a, Vector3 b, Transform parentHint)
    {
        Transform parent = ResolveReplayParent(parentHint);

        Vector3 worldA = ProjectReplayPoint(a, parent);
        Vector3 worldB = ProjectReplayPoint(b, parent);

        float dist = Vector3.Distance(worldA, worldB);
        if (dist <= 1e-6f) return;

        GameObject cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cyl.name = "TraceSegment";
        var col = cyl.GetComponent<Collider>();
        if (col) Destroy(col);

        var mr = cyl.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.black);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.black);
            mr.material = mat;
        }

        float radius = canvas != null ? Mathf.Max(0.0015f, canvas.sphereRadius * 1.8f) : 0.003f;

        if (parent != null)
        {
            cyl.transform.SetParent(parent, false);

            Vector3 localA = parent.InverseTransformPoint(worldA);
            Vector3 localB = parent.InverseTransformPoint(worldB);
            Vector3 localDelta = localB - localA;
            float localDist = localDelta.magnitude;
            if (localDist <= 1e-6f)
            {
                Destroy(cyl);
                return;
            }

            Vector3 localMid = (localA + localB) * 0.5f;
            Vector3 localDir = localDelta / localDist;

            cyl.transform.localPosition = localMid;
            cyl.transform.localRotation = Quaternion.FromToRotation(Vector3.up, localDir);
            cyl.transform.localScale = new Vector3(radius, localDist * 0.5f, radius);
            cyl.layer = parent.gameObject.layer;
        }
        else
        {
            Vector3 mid = (worldA + worldB) * 0.5f;
            Vector3 worldDir = (worldB - worldA).normalized;

            cyl.transform.position = mid;
            cyl.transform.rotation = Quaternion.FromToRotation(Vector3.up, worldDir);
            cyl.transform.localScale = new Vector3(radius, dist * 0.5f, radius);

            if (drawerHost != null)
                cyl.layer = drawerHost.layer;
        }
    }

    private void CreateReplayDot(Vector3 worldPos, Transform parentOverride = null)
    {
        Transform parent = ResolveReplayParent(parentOverride);
        Vector3 projected = ProjectReplayPoint(worldPos, parent, applyLift: true);

        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dot.name = "ReplayDot_" + replayDots.Count;

        float scale = Mathf.Max(0.0015f, canvas != null ? canvas.sphereRadius * 2.5f : 0.01f);

        if (parent != null)
        {
            dot.transform.SetParent(parent, false);
            dot.transform.localPosition = parent.InverseTransformPoint(projected);
            dot.transform.localRotation = Quaternion.identity;
            dot.transform.localScale = Vector3.one * scale;
            dot.layer = parent.gameObject.layer;
        }
        else
        {
            dot.transform.position = projected;
            dot.transform.localScale = Vector3.one * scale;
            dot.transform.up = GetReplayPlaneNormal(parent);
            if (drawerHost != null)
                dot.layer = drawerHost.layer;
        }

        Destroy(dot.GetComponent<Collider>());
        var r = dot.GetComponent<Renderer>();
        if (r != null)
        {
            var mat = r.material;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.black);
            else if (mat.HasProperty("_Color")) mat.color = Color.black;
        }

        replayDots.Add(dot);
    }

    private void YieldFailImmediate()
    {
        state = State.GradedReject;
        UpdateUI();
        OnDictationGraded?.Invoke(0f);
        OnLetterIncorrect?.Invoke();
    }

    // =========================================================
    // ==================   BOARD CAPTURE   ====================
    // =========================================================

    private void HardConfigureCaptureCamera()
    {
        if (!boardCamera) { Debug.LogError("[Dictation] Assign boardCamera."); return; }

        boardCamera.stereoTargetEye = StereoTargetEyeMask.None;
        boardCamera.allowHDR  = false;
        boardCamera.allowMSAA = false;
        boardCamera.clearFlags = CameraClearFlags.SolidColor;
        boardCamera.backgroundColor = Color.white;

        if (boardLayer.value != 0)
            boardCamera.cullingMask = boardLayer;

        boardCamera.orthographic = true;
        boardCamera.nearClipPlane = -10f;
        boardCamera.farClipPlane  =  10f;

        if (!boardCamera.gameObject.activeSelf)
            boardCamera.gameObject.SetActive(true);
    }

    private void FitOrthoToRenderer(Camera cam, Renderer target, float padding = 1.02f)
    {
        if (!cam || !target) return;

        Bounds b = target.bounds;

        Vector3[] corners = new Vector3[8];
        Vector3 c = b.center; Vector3 e = b.extents;
        corners[0] = c + new Vector3(-e.x, -e.y, -e.z);
        corners[1] = c + new Vector3( e.x, -e.y, -e.z);
        corners[2] = c + new Vector3(-e.x,  e.y, -e.z);
        corners[3] = c + new Vector3( e.x,  e.y, -e.z);
        corners[4] = c + new Vector3(-e.x, -e.y,  e.z);
        corners[5] = c + new Vector3( e.x, -e.y,  e.z);
        corners[6] = c + new Vector3(-e.x,  e.y,  e.z);
        corners[7] = c + new Vector3( e.x,  e.y,  e.z);

        Matrix4x4 w2c = cam.worldToCameraMatrix;
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        float zmin = float.PositiveInfinity, zmax = float.NegativeInfinity;

        for (int i = 0; i < 8; i++)
        {
            Vector3 v = w2c.MultiplyPoint(corners[i]);
            if (v.x < min.x) min.x = v.x;
            if (v.y < min.y) min.y = v.y;
            if (v.x > max.x) max.x = v.x;
            if (v.y > max.y) max.y = v.y;
            if (v.z < zmin) zmin = v.z;
            if (v.z > zmax) zmax = v.z;
        }

        float width  = (max.x - min.x) * padding;
        float height = (max.y - min.y) * padding;

        cam.orthographic = true;
        cam.orthographicSize = height * 0.5f;

        Vector3 camPosWS = cam.cameraToWorldMatrix.MultiplyPoint(new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, (zmin + zmax) * 0.5f));
        cam.transform.position = camPosWS;

        // aspect mismatch will letterbox in the RT
    }

    private IEnumerator CaptureBoardExactCo(Action<Texture2D> done)
    {
        if (!boardCamera) { done?.Invoke(null); yield break; }

        if (boardRenderer != null)
            FitOrthoToRenderer(boardCamera, boardRenderer, 1.02f);

        int w = Mathf.Max(64, captureWidth);
        int h = Mathf.Max(64, captureHeight);

        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
        {
            antiAliasing = 1,
            autoGenerateMips = false,
            useMipMap = false,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        var prevTarget = boardCamera.targetTexture;
        var prevActive = RenderTexture.active;

        boardCamera.targetTexture = rt;

        int prevMask = boardCamera.cullingMask;
        if (boardLayer.value != 0)
            boardCamera.cullingMask = boardLayer;

        yield return new WaitForEndOfFrame();
        yield return new WaitForEndOfFrame();

        boardCamera.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
        tex.Apply(false, false);

        RenderTexture.active = prevActive;
        boardCamera.targetTexture = prevTarget;
        if (boardLayer.value != 0) boardCamera.cullingMask = prevMask;
        rt.Release();
        Destroy(rt);

        if (debugWriteCapture)
        {
            try
            {
                var jpg = tex.EncodeToJPG(90);
                string path = System.IO.Path.Combine(Application.persistentDataPath, $"board_cap_{DateTime.Now:HHmmssfff}.jpg");
                System.IO.File.WriteAllBytes(path, jpg);
                Debug.Log("[Dictation] Wrote debug capture: " + path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Dictation] Debug write failed: " + e.Message);
            }
        }

        done?.Invoke(tex);
    }

    // =========================================================
    // ====================   VISION I/O   =====================
    // =========================================================

    [Serializable] private struct BeamPayload { public string image_b64; public int length; } // kept for compatibility
    [Serializable] private class BeamRespLoose { public string text; public string[] texts; [Serializable] public class Prediction { public string text; } public Prediction[] predictions; } // compatibility

    [Serializable] private class ServiceAccountJson
    {
        public string type;
        public string project_id;
        public string private_key_id;
        public string private_key;   // PEM
        public string client_email;
        public string client_id;
        public string auth_uri;
        public string token_uri;
    }

    [Serializable] private class TokenResp { public string access_token; public string token_type; public int expires_in; }

    [Serializable] private class VisionRequestWrapper { public VisionRequest[] requests; }
    [Serializable] private class VisionRequest
    {
        public VisionImage image;
        public VisionFeature[] features;
        public VisionImageContext imageContext;
    }
    [Serializable] private class VisionImage { public string content; }
    [Serializable] private class VisionFeature { public string type; public int maxResults; }
    [Serializable] private class VisionImageContext { public string[] languageHints; }

    [Serializable] private class VisionRespWrapper { public VisionResp[] responses; }
    [Serializable] private class VisionResp
    {
        public VisionTextAnn[] textAnnotations;
        public VisionFullText fullTextAnnotation;
    }
    [Serializable] private class VisionTextAnn { public string description; }
    [Serializable] private class VisionFullText { public string text; }

    private IEnumerator WarmupBeam()
    {
        var tex = new Texture2D(32, 32, TextureFormat.RGB24, false);
        var px = new Color32[32 * 32];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(px);
        tex.Apply();

        yield return StartCoroutine(BeamRecognize(tex, _ => { }));
        Destroy(tex);
    }

    // Kept name for compatibility
    private IEnumerator BeamRecognize(Texture2D snap, Action<string> onDone)
    {
        string accessToken = null;
        bool tokenOk = false;
        yield return StartCoroutine(GetAccessTokenCo(t => { accessToken = t; tokenOk = !string.IsNullOrEmpty(t); }));
        if (!tokenOk)
        {
            Debug.LogWarning("[Dictation] Failed to get access token.");
            onDone?.Invoke(null);
            yield break;
        }

        byte[] jpg = snap.EncodeToJPG(85);
        string b64 = Convert.ToBase64String(jpg);

        var reqObj = new VisionRequestWrapper
        {
            requests = new[]
            {
                new VisionRequest
                {
                    image = new VisionImage { content = b64 },
                    features = new[] { new VisionFeature { type = "DOCUMENT_TEXT_DETECTION", maxResults = 1 } },
                    imageContext = new VisionImageContext { languageHints = (languageHints != null && languageHints.Length > 0) ? languageHints : new [] { "en" } }
                }
            }
        };
        string json = JsonUtility.ToJson(reqObj);

        using (var req = new UnityWebRequest(visionEndpoint, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 20;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Dictation] Vision error {req.responseCode}: {req.error}\n{req.downloadHandler.text}");
                onDone?.Invoke(null);
                yield break;
            }

            string parsed = ParseVisionText(req.downloadHandler.text);
            Debug.Log($"[Vision] PARSED: '{parsed}'");
            onDone?.Invoke(parsed);
        }
    }

    private string ParseVisionText(string resp)
    {
        try
        {
            var wrap = JsonUtility.FromJson<VisionRespWrapper>(resp);
            if (wrap != null && wrap.responses != null && wrap.responses.Length > 0)
            {
                var r = wrap.responses[0];
                if (r == null) return null;

                if (r.fullTextAnnotation != null && !string.IsNullOrEmpty(r.fullTextAnnotation.text))
                    return r.fullTextAnnotation.text.Trim();

                if (r.textAnnotations != null && r.textAnnotations.Length > 0 && !string.IsNullOrEmpty(r.textAnnotations[0].description))
                    return r.textAnnotations[0].description.Trim();
            }
        }
        catch { }
        return null;
    }

    // =========================================================
    // ===================   AUTH (JWT)   ======================
    // =========================================================

    private IEnumerator GetAccessTokenCo(Action<string> onDone)
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrEmpty(_cachedAccessToken) && now < _tokenExpiryEpoch - 60)
        {
            onDone?.Invoke(_cachedAccessToken);
            yield break;
        }

        if (string.IsNullOrEmpty(serviceAccountJsonResource))
        {
            Debug.LogError("[Dictation] serviceAccountJsonResource is empty. Put your key in Resources/ and set the name (without .json).");
            onDone?.Invoke(null);
            yield break;
        }
        TextAsset ta = Resources.Load<TextAsset>(serviceAccountJsonResource);
        if (ta == null || string.IsNullOrEmpty(ta.text))
        {
            Debug.LogError("[Dictation] Could not load service account JSON from Resources/" + serviceAccountJsonResource + ".json");
            onDone?.Invoke(null);
            yield break;
        }

        ServiceAccountJson sa = null;
        try { sa = JsonUtility.FromJson<ServiceAccountJson>(ta.text); }
        catch (Exception e)
        {
            Debug.LogError("[Dictation] Invalid service account JSON: " + e.Message);
            onDone?.Invoke(null);
            yield break;
        }
        if (sa == null || string.IsNullOrEmpty(sa.client_email) || string.IsNullOrEmpty(sa.private_key) || string.IsNullOrEmpty(sa.token_uri))
        {
            Debug.LogError("[Dictation] Missing fields in service account JSON.");
            onDone?.Invoke(null);
            yield break;
        }

        string scope = "https://www.googleapis.com/auth/cloud-vision";
        long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long exp = iat + 3600;

        string headerJson = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
        string claimJson  = "{\"iss\":\"" + sa.client_email + "\",\"scope\":\"" + scope + "\",\"aud\":\"" + sa.token_uri + "\",\"exp\":" + exp + ",\"iat\":" + iat + "}";

        string headerB64 = ToBase64Url(Encoding.UTF8.GetBytes(headerJson));
        string claimB64  = ToBase64Url(Encoding.UTF8.GetBytes(claimJson));
        string signingInput = headerB64 + "." + claimB64;

        byte[] signature;
        try
        {
            using (RSA rsa = PemKeyUtil.CreateRSAFromPem(sa.private_key))
            {
                if (rsa == null) throw new Exception("PEM parse failed");
                signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[Dictation] RSA sign failed: " + e.Message);
            onDone?.Invoke(null);
            yield break;
        }

        string jwt = signingInput + "." + ToBase64Url(signature);

        WWWForm form = new WWWForm();
        form.AddField("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer");
        form.AddField("assertion", jwt);

        using (var req = UnityWebRequest.Post(sa.token_uri, form))
        {
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 20;
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[Dictation] Token request failed {req.responseCode}: {req.error}\n{req.downloadHandler.text}");
                onDone?.Invoke(null);
                yield break;
            }

            TokenResp tok = null;
            try { tok = JsonUtility.FromJson<TokenResp>(req.downloadHandler.text); } catch { }
            if (tok == null || string.IsNullOrEmpty(tok.access_token))
            {
                Debug.LogError("[Dictation] Token parse failed: " + req.downloadHandler.text);
                onDone?.Invoke(null);
                yield break;
            }

            _cachedAccessToken = tok.access_token;
            _tokenExpiryEpoch  = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(60, tok.expires_in);
            onDone?.Invoke(_cachedAccessToken);
        }
    }

    private static string ToBase64Url(byte[] input)
    {
        string s = Convert.ToBase64String(input);
        s = s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return s;
    }

    // === Character normalization methods ===
    private static string NormalizeToAsciiLetter(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        // 1) lowercase + trim
        s = s.Trim().ToLowerInvariant();

        // 2) NFKD to strip accents
        var norm = s.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark &&
                uc != UnicodeCategory.SpacingCombiningMark &&
                uc != UnicodeCategory.EnclosingMark)
                sb.Append(ch);
        }
        s = sb.ToString();

        // 3) common homoglyphs → latin
        //   (cyrillic)        (greek)
        s = s
            .Replace('а', 'a') // Cyrillic a
            .Replace('е', 'e') // Cyrillic e
            .Replace('о', 'o') // Cyrillic o
            .Replace('р', 'p') // Cyrillic r
            .Replace('с', 'c') // Cyrillic s
            .Replace('у', 'y') // Cyrillic u
            .Replace('х', 'x') // Cyrillic x
            .Replace('к', 'k') // Cyrillic k
            .Replace('м', 'm') // Cyrillic m
            .Replace('т', 't') // Cyrillic t
            .Replace('н', 'h') // Cyrillic n (visually h in some fonts; keep if you ever use 'h')
            .Replace('ι', 'i') // Greek iota
            .Replace('ο', 'o') // Greek omicron
            .Replace('ρ', 'p') // Greek rho
            .Replace('χ', 'x') // Greek chi
            .Replace('κ', 'k') // Greek kappa
            .Replace('μ', 'm') // Greek mu
            .Replace('τ', 't') // Greek tau
            .Replace('ν', 'v'); // Greek nu

        // 4) grab the first ascii a-z only
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch >= 'a' && ch <= 'z') return ch.ToString();
        }
        return "";
    }

    private static void DebugCodepoint(string label, string s)
    {
        if (string.IsNullOrEmpty(s)) { Debug.Log($"{label}: <null/empty>"); return; }
        var sb = new StringBuilder();
        foreach (var ch in s)
            sb.Append($"U+{((int)ch):X4} ");
        Debug.Log($"{label}: '{s}' ({sb})");
    }

    // Robust ASCII-only normalization with homoglyph mapping
    private static readonly Dictionary<char, char> ConfusableToAscii = new Dictionary<char, char>
    {
        // Cyrillic lowercase
        ['\u0430'] = 'a', ['\u0435'] = 'e', ['\u043E'] = 'o', ['\u0440'] = 'p', ['\u0441'] = 'c',
        ['\u0443'] = 'y', ['\u0445'] = 'x', ['\u043A'] = 'k', ['\u043C'] = 'm', ['\u0442'] = 't',
        ['\u0432'] = 'b', ['\u043D'] = 'h', ['\u0438'] = 'u', ['\u0456'] = 'i',
        // Cyrillic uppercase
        ['\u0410'] = 'a', ['\u0412'] = 'b', ['\u0415'] = 'e', ['\u041A'] = 'k', ['\u041C'] = 'm',
        ['\u041D'] = 'h', ['\u041E'] = 'o', ['\u0420'] = 'p', ['\u0421'] = 'c', ['\u0422'] = 't',
        ['\u0423'] = 'y', ['\u0425'] = 'x', ['\u0406'] = 'i',
        // Greek lowercase
        ['\u03B1'] = 'a', ['\u03B5'] = 'e', ['\u03BF'] = 'o', ['\u03C1'] = 'p', ['\u03BD'] = 'v',
        ['\u03BC'] = 'm', ['\u03B9'] = 'i', ['\u03BA'] = 'k', ['\u03C7'] = 'x', ['\u03C5'] = 'y', ['\u03C4'] = 't',
        // Greek uppercase
        ['\u0391'] = 'a', ['\u0392'] = 'b', ['\u0395'] = 'e', ['\u0397'] = 'h', ['\u0399'] = 'i', ['\u039A'] = 'k',
        ['\u039C'] = 'm', ['\u039D'] = 'n', ['\u039F'] = 'o', ['\u03A1'] = 'p', ['\u03A4'] = 't', ['\u03A5'] = 'y', ['\u03A7'] = 'x', ['\u039B'] = 'l',
    };

    private static string NormalizeAsciiStrict(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        // Normalize and strip combining marks
        var norm = s.Normalize(NormalizationForm.FormKD);
        foreach (var ch in norm)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.EnclosingMark)
                continue;

            char c = char.ToLowerInvariant(ch);
            if (c >= 'a' && c <= 'z') return c.ToString();
            if (ConfusableToAscii.TryGetValue(c, out var mapped)) return mapped.ToString();
        }
        return "";
    }

    private static bool LettersEquivalent(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        if (string.Equals(a, b, StringComparison.Ordinal))
            return true;
        if (a.Length == 1 && b.Length == 1)
        {
            char ca = char.ToLowerInvariant(a[0]);
            char cb = char.ToLowerInvariant(b[0]);
            if ((ca == 'l' && cb == 'i') || (ca == 'i' && cb == 'l'))
                return true;
        }
        return false;
    }
// ==================   PEM/DER PARSER (FIXED)   ===================
private static class PemKeyUtil
{
    public static RSA CreateRSAFromPem(string pem)
    {
        if (string.IsNullOrEmpty(pem)) return null;
        pem = NormalizePem(pem);

        if (pem.Contains("-----BEGIN RSA PRIVATE KEY-----"))
        {
            byte[] der = DecodePem(pem, "RSA PRIVATE KEY");
            RSAParameters p = ParsePkcs1PrivateKey(der);
            var rsa = RSA.Create(); rsa.ImportParameters(p); return rsa;
        }
        if (pem.Contains("-----BEGIN PRIVATE KEY-----"))
        {
            byte[] der = DecodePem(pem, "PRIVATE KEY");            // PKCS#8
            byte[] inner = ExtractPkcs8PrivateKeyOctet(der);       // RSAPrivateKey DER
            RSAParameters p = ParsePkcs1PrivateKey(inner);
            var rsa = RSA.Create(); rsa.ImportParameters(p); return rsa;
        }
        throw new Exception("Unsupported key: missing BEGIN PRIVATE KEY header");
    }

    private static string NormalizePem(string pem)
    {
        return pem.Replace("\r\n", "\n").Replace("\r", "").Trim()
                  .Replace("-----BEGIN  PRIVATE KEY-----", "-----BEGIN PRIVATE KEY-----")
                  .Replace("-----END  PRIVATE KEY-----", "-----END PRIVATE KEY-----");
    }

    private static byte[] DecodePem(string pem, string label)
    {
        string header = $"-----BEGIN {label}-----";
        string footer = $"-----END {label}-----";
        int start = pem.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) throw new Exception($"Missing {header}");
        start += header.Length;
        int end = pem.IndexOf(footer, start, StringComparison.Ordinal);
        if (end < 0) throw new Exception($"Missing {footer}");
        string b64 = pem.Substring(start, end - start).Replace("\n", "").Replace("\t", "").Replace(" ", "");
        return Convert.FromBase64String(b64);
    }

    // ---------- minimal DER helpers ----------
    private static int ReadLen(byte[] der, ref int ofs)
    {
        if (ofs >= der.Length) throw new IndexOutOfRangeException("DER len read");
        int len = der[ofs++];
        if ((len & 0x80) == 0) return len;
        int bytes = len & 0x7F;
        if (bytes < 1 || bytes > 4 || ofs + bytes > der.Length) throw new Exception("Invalid DER length");
        int v = 0; for (int i = 0; i < bytes; i++) v = (v << 8) | der[ofs++]; return v;
    }

    // Read only the SEQUENCE header; leave ofs at start of its content
    private static int ReadSeqHeader(byte[] der, ref int ofs)
    {
        if (ofs >= der.Length) throw new IndexOutOfRangeException("DER tag read");
        byte tag = der[ofs++]; if (tag != 0x30) throw new Exception("ASN.1: SEQUENCE expected");
        int len = ReadLen(der, ref ofs);
        if (ofs + len > der.Length) throw new IndexOutOfRangeException("DER seq overflow");
        return len; // content length
    }

    // Read a full block (tag + length + content), return content bytes
    private static byte[] ReadBlock(byte[] der, ref int ofs, byte expectTag)
    {
        if (ofs >= der.Length) throw new IndexOutOfRangeException("DER tag read");
        byte tag = der[ofs++]; if (tag != expectTag) throw new Exception($"ASN.1 tag {expectTag:X2} expected, got {tag:X2}");
        int len = ReadLen(der, ref ofs);
        if (ofs + len > der.Length) throw new IndexOutOfRangeException("DER block overflow");
        var val = new byte[len]; Buffer.BlockCopy(der, ofs, val, 0, len); ofs += len; return val;
    }

    private static byte[] ReadIntegerBytes(byte[] der, ref int ofs)
    {
        byte[] v = ReadBlock(der, ref ofs, 0x02); // INTEGER
        if (v.Length > 1 && v[0] == 0x00) { var t = new byte[v.Length - 1]; Buffer.BlockCopy(v, 1, t, 0, t.Length); v = t; }
        return v;
    }

    // PKCS#8 PrivateKeyInfo => OCTET STRING "privateKey"
    private static byte[] ExtractPkcs8PrivateKeyOctet(byte[] der)
    {
        int ofs = 0;
        int seqLen = ReadSeqHeader(der, ref ofs); // enter PrivateKeyInfo
        int end = ofs + seqLen;

        ReadIntegerBytes(der, ref ofs);           // version
        ReadBlock(der, ref ofs, 0x30);            // AlgorithmIdentifier (skip content)
        byte[] oct = ReadBlock(der, ref ofs, 0x04); // privateKey
        // attributes [0] optional may follow; we don't need it
        if (ofs > end) throw new Exception("PKCS#8 parse overflow");
        return oct;
    }

    // PKCS#1 RSAPrivateKey => RSAParameters
    private static RSAParameters ParsePkcs1PrivateKey(byte[] der)
    {
        int ofs = 0;
        int seqLen = ReadSeqHeader(der, ref ofs); // enter RSAPrivateKey
        int end = ofs + seqLen;

        ReadIntegerBytes(der, ref ofs);           // version
        byte[] n  = ReadIntegerBytes(der, ref ofs); // modulus
        byte[] e  = ReadIntegerBytes(der, ref ofs); // publicExponent
        byte[] d  = ReadIntegerBytes(der, ref ofs); // privateExponent
        byte[] p  = ReadIntegerBytes(der, ref ofs); // prime1
        byte[] q  = ReadIntegerBytes(der, ref ofs); // prime2
        byte[] dp = ReadIntegerBytes(der, ref ofs); // exponent1
        byte[] dq = ReadIntegerBytes(der, ref ofs); // exponent2
        byte[] iq = ReadIntegerBytes(der, ref ofs); // coefficient

        if (ofs > end) throw new Exception("PKCS#1 parse overflow");
        return new RSAParameters { Modulus = n, Exponent = e, D = d, P = p, Q = q, DP = dp, DQ = dq, InverseQ = iq };
    }
}
}
