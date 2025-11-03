using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Serialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using System.Linq;
public class LevelManager : MonoBehaviour
{
    public enum GameMode { PhonemeChecking, TraceChecking, ObjectPlacing, Dictation, FreeDrawing }
    public event Action<GameMode> OnGameModeChanged;
    public event Action OnPhonemeCheckStart;
    public event Action OnAllPhasesCompleted;
    public event Action<string> OnLetterChanged;
    public event Action<string> OnDrillChanged;
    public GameMode currentMode { get; private set; }
    public string currentLetter { get; private set; }
    public string currentPhoneme { get; private set; }
    public string currentSound => currentPhoneme;
    public int currentLevel { get; private set; }
    public string currentDrill { get; private set; } = "Audio";
    public bool IsPaused { get; private set; }
    [Header("References (set in Inspector)")]
    [SerializeField] private SimpleRecorder recorder;
    [SerializeField] private LetterTracingSystem tracingSystem;
    [SerializeField] private ObjectOfInterestManager objectManager;
    [SerializeField] private DictationManager dictationManager;
    [SerializeField] public PhonemeManager phonemeManager;
    [SerializeField] private PlaneSurfaceDrawer planeSurfaceDrawer;
    [SerializeField] private SaveManager saveManager;
    [SerializeField] private AudioManager audioManager;
    [Header("Pause (optional)")]
    [SerializeField] private GameObject pauseButtonGO;
    private ProximityButton pauseBtn;
    [Header("Memory Management")]
    [FormerlySerializedAs("reloadSceneAfterLetters")]
    [SerializeField, Tooltip("Perform a lightweight cleanup after this many completed letters. 0 disables cadence-based sweeps.")]
    private int lettersBeforeMemorySweep = 0;
    [FormerlySerializedAs("reloadSceneOnModeSwitch")]
    [SerializeField, Tooltip("Run a cleanup whenever the game mode changes.")]
    private bool sweepOnModeSwitch = false;
    [FormerlySerializedAs("minSecondsBetweenAutoReloads")]
    [SerializeField, Tooltip("Minimum seconds between automatic cleanups.")]
    private float minSecondsBetweenSweeps = 12f;
    [SerializeField, Tooltip("Check memory usage every N seconds. 0 disables the watchdog.")]
    private float memoryCheckIntervalSeconds = 5f;
    [SerializeField, Tooltip("Trigger a memory cleanup when reserved memory (MB) exceeds this threshold. 0 disables the emergency sweep.")]
    private float memoryDangerThresholdMB = 1750f;
    [SerializeField, Tooltip("Optional warning threshold (MB) before the danger point; negative disables warnings.")]
    private float memoryWarningThresholdMB = 1600f;
    [FormerlySerializedAs("logAutomaticReloads")]
    [SerializeField, Tooltip("Log when maintenance sweeps run.")]
    private bool logMemorySweeps = true;
    [SerializeField, Tooltip("Seconds after scene load before sweeps may run (gives systems time to warm up).")]
    private float sweepWarmupSeconds = 3f;
    private readonly List<Phase> levelPlan = new List<Phase>();
    private readonly List<IMemoryBudgetConsumer> memoryConsumers = new List<IMemoryBudgetConsumer>();
    private int phaseIndex;
    private int letterIndex;
    private int modeIndex;
    private int lettersSinceLastSweep;
    private bool hasBootstrapped;
    private bool sweepWarmupElapsed;
    private string lastReportedLetter = string.Empty;
    private string bootstrapLetterFromPlan;
    private Coroutine memoryGuardRoutine;
    private Coroutine memorySweepRoutine;
    private float lastMemorySweepTime;
    private float lastMemoryWarningTime;
    private string lastSpokenLetter = string.Empty;
    private bool loggedMissingAudioManager = false;
    private void Start()
    {
        InitializeComponents();
        WirePauseButton();
        RefreshMemoryConsumers();
        LoadLevelPlan();
        bool started = false;
        if (!string.IsNullOrEmpty(bootstrapLetterFromPlan))
            started = TrySetLevelByLetter(bootstrapLetterFromPlan, false);
        if (!started)
            started = TryRestoreLetterFromPrefs();
        if (!started)
            StartCurrentLetter();
        if ((memoryCheckIntervalSeconds > 0f || memoryDangerThresholdMB > 0f) && memoryGuardRoutine == null)
            memoryGuardRoutine = StartCoroutine(MemorySafeguardLoop());
        if (sweepWarmupSeconds > 0f)
            StartCoroutine(SweepWarmupTimer());
        else
            sweepWarmupElapsed = true;
        OnDrillChanged?.Invoke(currentDrill);
    }
    private void InitializeComponents()
    {
        if (!recorder)        recorder        = GetComponent<SimpleRecorder>();
        if (!tracingSystem)   tracingSystem   = GetComponent<LetterTracingSystem>();
        if (!objectManager)   objectManager   = GetComponent<ObjectOfInterestManager>();
        if (!dictationManager)dictationManager= GetComponent<DictationManager>();
        if (!saveManager)     saveManager     = GetComponent<SaveManager>();
        if (!phonemeManager)  phonemeManager  = GetComponent<PhonemeManager>();
        if (!planeSurfaceDrawer)
        {
            planeSurfaceDrawer = GetComponent<PlaneSurfaceDrawer>();
            if (!planeSurfaceDrawer && tracingSystem)
                planeSurfaceDrawer = tracingSystem.GetComponent<PlaneSurfaceDrawer>();
            if (!planeSurfaceDrawer)
                planeSurfaceDrawer = FindObjectOfType<PlaneSurfaceDrawer>();
        }
        if (!audioManager)
            audioManager = GetComponent<AudioManager>() ?? GetComponentInChildren<AudioManager>(true);
        if (!audioManager)
            audioManager = FindObjectOfType<AudioManager>();
        if (phonemeManager != null)
        {
            phonemeManager.OnPhonemeCorrect += HandlePhonemeCorrect;
            phonemeManager.OnPhonemeTriesExhausted += HandlePhonemeTriesExhausted;
        }
        if (tracingSystem != null)
            tracingSystem.OnTraceCompleted += OnTraceCompleted;
        if (objectManager != null)
            objectManager.OnObjectCollected += OnObjectCollected;
        if (dictationManager != null)
            dictationManager.OnDictationComplete += OnDictationComplete;
    }
    private void WirePauseButton()
    {
        if (!pauseButtonGO) return;
        pauseBtn = pauseButtonGO.GetComponent<ProximityButton>();
        if (pauseBtn != null)
            pauseBtn.OnButtonPressed += HandlePausePressed;
    }
    private void HandlePausePressed()
    {
        SetPaused(!IsPaused);
    }
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
            Debug.LogWarning("[LevelManager] AudioManager not available; skipping letter audio.");
            loggedMissingAudioManager = true;
        }
        return null;
    }
    private static char ExtractFirstAsciiLetter(string value)
    {
        if (string.IsNullOrEmpty(value)) return '\0';
        foreach (char c in value)
        {
            if (char.IsLetter(c))
                return c;
        }
        return '\0';
    }
    private void TryPlayLetterAudio(string letter)
    {
        if (string.IsNullOrEmpty(letter))
            return;
        if (string.Equals(lastSpokenLetter, letter, StringComparison.OrdinalIgnoreCase))
            return;
        var manager = ResolveAudioManager();
        if (manager == null)
            return;
        char c = ExtractFirstAsciiLetter(letter);
        if (c == '\0')
            return;
        if (manager.TryPlayLetterPronunciation(c))
            lastSpokenLetter = letter;
    }
    private IEnumerator SweepWarmupTimer()
    {
        yield return new WaitForSeconds(sweepWarmupSeconds);
        sweepWarmupElapsed = true;
    }
    public void LoadLevelPlan()
    {
        levelPlan.Clear();
        bootstrapLetterFromPlan = null;
        var ta = Resources.Load<TextAsset>("LevelPlan");
        if (ta == null)
        {
            Debug.LogError("LevelPlan resource not found!");
            return;
        }
        try
        {
            var wrapper = JsonConvert.DeserializeObject<LevelPlanWrapper>(ta.text);
            if (wrapper?.levelplan != null)
            {
                foreach (var phaseData in wrapper.levelplan)
                {
                    if (phaseData == null) continue;
                    var phase = new Phase();
                    if (phaseData.Letters != null) phase.Letters.AddRange(phaseData.Letters);
                    if (phaseData.Phonemes != null) phase.Phonemes.AddRange(phaseData.Phonemes);
                    var parsedModes = ConvertModes(phaseData.Modes);
                    phase.Modes = NormalizeModes(parsedModes);
                    EnsurePhonemeCoverage(phase);
                    levelPlan.Add(phase);
                }
            }
            bootstrapLetterFromPlan = wrapper?.currentLetter;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to parse LevelPlan: {ex.Message}");
        }
        Debug.Log($"Level plan loaded. Phases: {levelPlan.Count}. Bootstrap letter: {bootstrapLetterFromPlan ?? "(none)"}");
    }
    private static void EnsurePhonemeCoverage(Phase phase)
    {
        if (phase.Letters == null)
            phase.Letters = new List<string>();
        if (phase.Phonemes == null)
            phase.Phonemes = new List<string>();
        for (int i = 0; i < phase.Letters.Count; i++)
        {
            string raw = phase.Letters[i] ?? string.Empty;
            raw = raw.Trim();
            string lower = raw.ToLowerInvariant();
            phase.Letters[i] = lower;
            if (phase.Phonemes.Count <= i)
                phase.Phonemes.Add(lower);
            else
                phase.Phonemes[i] = lower;
        }
        if (phase.Phonemes.Count > phase.Letters.Count)
            phase.Phonemes.RemoveRange(phase.Letters.Count, phase.Phonemes.Count - phase.Letters.Count);
    }
    private static List<GameMode> ConvertModes(List<string> modes)
    {
        if (modes == null || modes.Count == 0) return null;
        var result = new List<GameMode>(modes.Count);
        foreach (var entry in modes)
        {
            if (Enum.TryParse(entry, true, out GameMode gm))
                result.Add(gm);
        }
        return result;
    }
    private static List<GameMode> NormalizeModes(List<GameMode> modes)
    {
        if (modes == null || modes.Count == 0)
            return new List<GameMode> { GameMode.PhonemeChecking, GameMode.TraceChecking };

        var normalized = new List<GameMode>(modes.Count);
        foreach (var mode in modes)
        {
            if (!normalized.Contains(mode))
                normalized.Add(mode);
        }

        bool containsFreeDrawing = normalized.Contains(GameMode.FreeDrawing);
        bool dictationOnly = normalized.Count == 1 && normalized[0] == GameMode.Dictation;

        if (!containsFreeDrawing && !dictationOnly)
        {
            if (!normalized.Contains(GameMode.PhonemeChecking))
                normalized.Insert(0, GameMode.PhonemeChecking);

            if (!normalized.Contains(GameMode.TraceChecking))
            {
                int insertIndex = normalized.Contains(GameMode.PhonemeChecking) ? 1 : 0;
                normalized.Insert(insertIndex, GameMode.TraceChecking);
            }
        }

        return normalized;
    }
    private bool TryRestoreLetterFromPrefs()
    {
        try
        {
            string storedLetter = PlayerPrefs.GetString("LevelManager.LastLetter", string.Empty);
            if (!string.IsNullOrEmpty(storedLetter))
            {
                if (TrySetLevelByLetter(storedLetter, false))
                {
                    Debug.Log($"[LevelManager] Restored last letter '{storedLetter}' from PlayerPrefs.");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LevelManager] Failed to restore letter from PlayerPrefs: {ex.Message}");
        }
        return false;
    }
    private void StartCurrentLetter()
    {
        if (phaseIndex >= levelPlan.Count)
        {
            FinishAll();
            return;
        }
        var currentPhase = levelPlan[phaseIndex];
        if (currentPhase.Letters == null || currentPhase.Letters.Count == 0)
        {
            Debug.LogWarning($"Phase {phaseIndex} has no letters; skipping.");
            StartNextPhase();
            return;
        }
        if (letterIndex >= currentPhase.Letters.Count)
        {
            StartNextPhase();
            return;
        }
        currentLetter = currentPhase.Letters[letterIndex];
        currentPhoneme = SafeGet(currentPhase.Phonemes, letterIndex) ?? currentLetter;
        currentLevel = ComputeAbsoluteLevel(phaseIndex, letterIndex);
        if (modeIndex >= currentPhase.Modes.Count) modeIndex = 0;
        NotifyLetterChanged();
        LoadLevelData();
        hasBootstrapped = false;
        StartCurrentMode();
    }
    private void StartNextPhase()
    {
        phaseIndex++;
        if (phaseIndex >= levelPlan.Count)
        {
            FinishAll();
            return;
        }
        letterIndex = 0;
        modeIndex = 0;
        StartCurrentLetter();
    }
    private void FinishAll()
    {
        OnAllPhasesCompleted?.Invoke();
        Debug.Log("All phases completed. Game finished!");
    }
    private void StartCurrentMode()
    {
        if (phaseIndex >= levelPlan.Count) { FinishAll(); return; }
        var phase = levelPlan[phaseIndex];
        if (phase.Modes == null || phase.Modes.Count == 0)
        {
            phase.Modes = NormalizeModes(null);
        }
        if (modeIndex >= phase.Modes.Count)
        {
            CompleteLetter();
            return;
        }
        SetGameMode(phase.Modes[modeIndex]);
    }
    private void CompleteLetter()
    {
        modeIndex = 0;
        letterIndex++;
        currentLevel++;
        lettersSinceLastSweep++;
        ReleaseMemoryConsumers();
        bool cadenceReached = lettersBeforeMemorySweep > 0 && lettersSinceLastSweep >= lettersBeforeMemorySweep;
        if (cadenceReached)
        {
            lettersSinceLastSweep = 0;
            MaybeScheduleMemorySweep("Cadence", true);
        }
        else
        {
            MaybeScheduleMemorySweep("Letter complete", false);
        }
        StartCurrentLetter();
    }
    public void SkipToNextLetterManual()
    {
        RunModeExitCleanup(currentMode);
        CompleteLetter();
    }
    public void ReturnToPreviousLetterManual()
    {
        if (levelPlan.Count == 0)
            return;
        RunModeExitCleanup(currentMode);
        ReleaseMemoryConsumers();
        if (letterIndex > 0)
        {
            letterIndex--;
        }
        else if (phaseIndex > 0)
        {
            phaseIndex--;
            var prevPhase = levelPlan[phaseIndex];
            letterIndex = (prevPhase.Letters != null && prevPhase.Letters.Count > 0)
                ? prevPhase.Letters.Count - 1
                : 0;
        }
        else
        {
            return;
        }
        lettersSinceLastSweep = Mathf.Max(0, lettersSinceLastSweep - 1);
        currentLevel = ComputeAbsoluteLevel(Mathf.Clamp(phaseIndex, 0, levelPlan.Count - 1), letterIndex);
        modeIndex = 0;
        hasBootstrapped = false;
        StartCurrentLetter();
    }
    private void SetGameMode(GameMode mode)
    {
        if (currentMode == mode && hasBootstrapped) return;
        var previousMode = currentMode;
        bool firstEntry = !hasBootstrapped;
        currentMode = mode;
        hasBootstrapped = true;
        if (!firstEntry)
        {
            RunModeExitCleanup(previousMode);
            if (sweepOnModeSwitch)
                MaybeScheduleMemorySweep($"Mode switch {previousMode} -> {mode}", false);
        }
        OnGameModeChanged?.Invoke(currentMode);
        UpdateComponentStates();
        bool traceActive = currentMode == GameMode.TraceChecking;
        tracingSystem?.SetVisualizationActive(traceActive);
        if (phonemeManager != null)
        {
            try
            {
                if (currentMode == GameMode.PhonemeChecking)
                {
                    var startMic = phonemeManager.GetType().GetMethod("StartMicIfNeeded", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    startMic?.Invoke(phonemeManager, null);
                }
                else
                {
                    var stopMic = phonemeManager.GetType().GetMethod("StopMic", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    stopMic?.Invoke(phonemeManager, null);
                }
            }
            catch { }
        }
        switch (currentMode)
        {
            case GameMode.PhonemeChecking:
                OnPhonemeCheckStart?.Invoke();
                break;
            case GameMode.TraceChecking:
                tracingSystem?.StartTracing();
                if (!string.IsNullOrEmpty(currentLetter))
                {
                    lastSpokenLetter = string.Empty;
                    TryPlayLetterAudio(currentLetter);
                }
                break;
            case GameMode.ObjectPlacing:
                objectManager?.StartObjectPlacing();
                break;
            case GameMode.Dictation:
                if (dictationManager != null)
                {
                    dictationManager.PrepareForMode(GameMode.Dictation);
                    dictationManager.StartDictation();
                }
                break;
            case GameMode.FreeDrawing:
                if (dictationManager != null)
                {
                    dictationManager.PrepareForMode(GameMode.FreeDrawing);
                    dictationManager.StartDictation();
                }
                break;
        }
        if (logMemorySweeps)
            Debug.Log($"[LevelManager] Mode -> {currentMode} | Letter {currentLetter}");
    }
    private void RunModeExitCleanup(GameMode mode)
    {
        if (mode == GameMode.PhonemeChecking && phonemeManager != null)
        {
            phonemeManager.ReleaseMemory();
        }
        else if ((mode == GameMode.Dictation || mode == GameMode.FreeDrawing) && dictationManager != null)
        {
            dictationManager.ReleaseMemory();
        }
        else if (mode == GameMode.TraceChecking && planeSurfaceDrawer != null)
        {
            planeSurfaceDrawer.ForceStopAllAudio();
        }
    }
    private void UpdateComponentStates()
    {
        bool isPhoneme = currentMode == GameMode.PhonemeChecking;
        if (phonemeManager)  phonemeManager.enabled  = isPhoneme;
        if (tracingSystem)   tracingSystem.enabled   = true;
        if (objectManager)   objectManager.enabled   = true;
        if (dictationManager)dictationManager.PrepareForMode(currentMode);
    }
    private void LoadLevelData()
    {
        StartCoroutine(LoadAfterStart());
    }
    private IEnumerator LoadAfterStart()
    {
        yield return null;
        // Only load fallback strokes for trace modes; skip in Dictation/FreeDrawing to avoid unnecessary memory churn.
        if (recorder && currentMode != GameMode.Dictation && currentMode != GameMode.FreeDrawing)
            recorder.LoadRecording(currentLetter);
    }
    private void HandlePhonemeCorrect()
    {
        if (currentMode == GameMode.PhonemeChecking)
            ProceedToNextMode();
    }
    private void HandlePhonemeTriesExhausted()
    {
        // Ensure the pronunciation can replay when we advance to trace checking.
        lastSpokenLetter = string.Empty;
        ProceedToNextMode();
    }
    private void OnTraceCompleted()
    {
        if (currentMode == GameMode.TraceChecking)
            ProceedToNextMode();
    }
    private void OnObjectCollected()
    {
        if (currentMode == GameMode.ObjectPlacing)
            ProceedToNextMode();
    }
    private void OnDictationComplete()
    {
        if (currentMode == GameMode.Dictation)
            ProceedToNextMode();
    }
    public void ProceedToNextMode()
    {
        if (phaseIndex >= levelPlan.Count)
            return;
        var phase = levelPlan[phaseIndex];
        modeIndex++;
        if (dictationManager) dictationManager.PrepareForMode(GameMode.PhonemeChecking);
        if (modeIndex < phase.Modes.Count)
        {
            StartCurrentMode();
        }
        else
        {
            CompleteLetter();
        }
    }
    public void Restart()
    {
        phaseIndex = 0;
        letterIndex = 0;
        modeIndex = 0;
        currentLevel = 0;
        lettersSinceLastSweep = 0;
        hasBootstrapped = false;
        StartCurrentLetter();
    }
    public void SetLevel(int level)
    {
        if (level < 0)
        {
            Debug.LogWarning("Level must be >= 0.");
            return;
        }
        int total = 0;
        for (int p = 0; p < levelPlan.Count; p++)
        {
            var phase = levelPlan[p];
            int letterCount = phase?.Letters?.Count ?? 0;
            for (int l = 0; l < letterCount; l++)
            {
                if (total == level)
                {
                    phaseIndex = p;
                    letterIndex = l;
                    modeIndex = 0;
                    currentLevel = level;
                    currentLetter = phase.Letters[l];
                    currentPhoneme = SafeGet(phase.Phonemes, l) ?? currentLetter;
                    NotifyLetterChanged();
                    LoadLevelData();
                    hasBootstrapped = false;
                    StartCurrentMode();
                    return;
                }
                total++;
            }
        }
        Debug.LogWarning($"Attempted to set level to {level}, but it does not exist.");
    }
    public void SetLevelByLetter(string letter)
    {
        TrySetLevelByLetter(letter, true);
    }
    public bool TrySetLevelByLetter(string letter, int preferredPhaseIndex)
    {
        return TrySetLevelByLetter(letter, true, preferredPhaseIndex);
    }
    private bool TrySetLevelByLetter(string letter, bool logIfMissing, int preferredPhaseIndex = -1)
    {
        if (string.IsNullOrEmpty(letter))
        {
            if (logIfMissing) Debug.LogWarning("Letter is null/empty.");
            return false;
        }

        string trimmed = letter.Trim();
        string normalized = trimmed.ToLowerInvariant();

        if (preferredPhaseIndex >= 0 && preferredPhaseIndex < levelPlan.Count)
        {
            var preferredPhase = levelPlan[preferredPhaseIndex];
            if (preferredPhase?.Letters != null)
            {
                int preferredLetterIndex = preferredPhase.Letters.FindIndex(l => string.Equals(l, normalized, StringComparison.OrdinalIgnoreCase));
                if (preferredLetterIndex >= 0)
                {
                    phaseIndex = preferredPhaseIndex;
                    letterIndex = preferredLetterIndex;
                    modeIndex = 0;
                    currentLetter = preferredPhase.Letters[preferredLetterIndex];
                    if (preferredPhase.Phonemes.Count <= preferredLetterIndex)
                        preferredPhase.Phonemes.Add(currentLetter);
                    currentPhoneme = preferredPhase.Phonemes[preferredLetterIndex];
                    currentLevel = ComputeAbsoluteLevel(preferredPhaseIndex, preferredLetterIndex);
                    lettersSinceLastSweep = 0;

                    NotifyLetterChanged();
                    LoadLevelData();
                    hasBootstrapped = false;
                    StartCurrentMode();
                    Debug.Log($"Set level to letter '{letter}': Phase {preferredPhaseIndex}, LetterIndex {preferredLetterIndex}");
                    return true;
                }
            }
        }

        for (int p = 0; p < levelPlan.Count; p++)
        {
            if (p == preferredPhaseIndex) continue;
            var phase = levelPlan[p];
            if (phase?.Letters == null) continue;

            int li = phase.Letters.FindIndex(l => string.Equals(l, normalized, StringComparison.OrdinalIgnoreCase));
            if (li >= 0)
            {
                phaseIndex = p;
                letterIndex = li;
                modeIndex = 0;
                currentLetter = phase.Letters[li];
                if (phase.Phonemes.Count <= li)
                    phase.Phonemes.Add(currentLetter);
                currentPhoneme = phase.Phonemes[li];
                currentLevel = ComputeAbsoluteLevel(p, li);
                lettersSinceLastSweep = 0;

                NotifyLetterChanged();
                LoadLevelData();
                hasBootstrapped = false;
                StartCurrentMode();
                Debug.Log($"Set level to letter '{letter}': Phase {p}, LetterIndex {li}");
                return true;
            }
        }

        if (levelPlan.Count == 0)
            levelPlan.Add(new Phase());

        int injectionPhaseIndex = (preferredPhaseIndex >= 0 && preferredPhaseIndex < levelPlan.Count)
            ? preferredPhaseIndex
            : Mathf.Clamp(phaseIndex, 0, levelPlan.Count - 1);

        var targetPhase = levelPlan[injectionPhaseIndex];
        if (targetPhase.Letters == null) targetPhase.Letters = new List<string>();
        if (targetPhase.Phonemes == null) targetPhase.Phonemes = new List<string>();

        int insertIndex = Mathf.Clamp(letterIndex, 0, targetPhase.Letters.Count);
        targetPhase.Letters.Insert(insertIndex, normalized);
        int phonemeInsert = Mathf.Clamp(insertIndex, 0, targetPhase.Phonemes.Count);
        targetPhase.Phonemes.Insert(phonemeInsert, normalized);

        phaseIndex = Mathf.Clamp(injectionPhaseIndex, 0, levelPlan.Count - 1);
        letterIndex = Mathf.Clamp(insertIndex, 0, targetPhase.Letters.Count - 1);
        modeIndex = 0;
        currentLetter = normalized;
        currentPhoneme = normalized;
        currentLevel = ComputeAbsoluteLevel(phaseIndex, letterIndex);
        lettersSinceLastSweep = 0;

        NotifyLetterChanged();
        LoadLevelData();
        hasBootstrapped = false;
        StartCurrentMode();
        Debug.Log($"[LevelManager] Injected new letter '{normalized}' into current phase (was missing).");
        return true;
    }

    private int ComputeAbsoluteLevel(int targetPhase, int targetLetterIndex)
    {
        int total = 0;
        for (int p = 0; p < levelPlan.Count; p++)
        {
            var phase = levelPlan[p];
            int count = phase?.Letters?.Count ?? 0;
            if (p == targetPhase)
            {
                total += Mathf.Clamp(targetLetterIndex, 0, count);
                break;
            }
            total += count;
        }
        return total;
    }
    public void SetCurrentDrill(string drill)
    {
        if (string.IsNullOrEmpty(drill)) return;
        string normalized = drill.Trim();
        if (normalized.Length == 0) return;
        if (string.Equals(currentDrill, normalized, StringComparison.OrdinalIgnoreCase))
        {
            currentDrill = normalized; // ensure casing matches source
            return;
        }
        currentDrill = normalized;
        lettersSinceLastSweep = 0;
        ReleaseMemoryConsumers();
        RestartSubsystemsForDrill();
        OnDrillChanged?.Invoke(currentDrill);
    }
    private void RestartSubsystemsForDrill()
    {
        bool isAudio = string.Equals(currentDrill, "Audio", StringComparison.OrdinalIgnoreCase);
        if (phonemeManager)
        {
            if (phonemeManager is IMemoryBudgetConsumer consumer && !isAudio)
            {
                consumer.ReleaseMemory();
            }
            if (isAudio)
            {
                var warmMethod = phonemeManager.GetType().GetMethod("WarmUpMic", BindingFlags.Instance | BindingFlags.NonPublic);
                if (warmMethod != null)
                {
                    var enumerator = warmMethod.Invoke(phonemeManager, null) as IEnumerator;
                    if (enumerator != null) phonemeManager.StartCoroutine(enumerator);
                }
            }
        }
        if (!isAudio)
        {
            dictationManager?.ReleaseMemory();
        }
    }
    private void NotifyLetterChanged()
    {
        string letter = currentLetter ?? string.Empty;
        tracingSystem?.SetLetter(letter);
        tracingSystem?.SetVisualizationActive(currentMode == GameMode.TraceChecking);
        if (!string.Equals(lastReportedLetter, letter, StringComparison.OrdinalIgnoreCase))
        {
            lastReportedLetter = letter;
            if (!string.IsNullOrEmpty(letter))
            {
                try
                {
                    PlayerPrefs.SetString("LevelManager.LastLetter", letter);
                    PlayerPrefs.Save();
                }
                catch { }
            }
        }
        // Reset so the upcoming mode switch can replay the pronunciation.
        lastSpokenLetter = string.Empty;
        OnLetterChanged?.Invoke(letter);
    }
    private void RefreshMemoryConsumers()
    {
        memoryConsumers.Clear();
        var found = GetComponentsInChildren<IMemoryBudgetConsumer>(true);
        if (found != null && found.Length > 0)
            memoryConsumers.AddRange(found);
    }
    private void ReleaseMemoryConsumers()
    {
        RefreshMemoryConsumers();
        foreach (var consumer in memoryConsumers)
        {
            if (consumer == null) continue;
            try { consumer.ReleaseMemory(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LevelManager] Memory consumer {consumer.GetType().Name} threw: {ex.Message}");
            }
        }
    }
    private void MaybeScheduleMemorySweep(string reason, bool force)
    {
        if (!sweepWarmupElapsed && !force) return;
        if (memorySweepRoutine != null) return;
        float now = Time.realtimeSinceStartup;
        if (!force && minSecondsBetweenSweeps > 0f && now - lastMemorySweepTime < minSecondsBetweenSweeps)
            return;
        memorySweepRoutine = StartCoroutine(MemorySweepRoutine(reason, force));
    }
    private IEnumerator MemorySweepRoutine(string reason, bool forced)
    {
        if (logMemorySweeps)
            Debug.Log($"[LevelManager] Memory sweep starting ({reason}){(forced ? " [forced]" : string.Empty)}.");
        ReleaseMemoryConsumers();
        lettersSinceLastSweep = 0;
        yield return null;
        var unload = Resources.UnloadUnusedAssets();
        if (unload != null)
        {
            while (!unload.isDone)
                yield return null;
        }
        System.GC.Collect();
        lastMemorySweepTime = Time.realtimeSinceStartup;
        memorySweepRoutine = null;
        if (logMemorySweeps)
            Debug.Log("[LevelManager] Memory sweep finished.");
    }
    private IEnumerator MemorySafeguardLoop()
    {
        var wait = new WaitForSeconds(Mathf.Max(1f, memoryCheckIntervalSeconds));
        while (enabled)
        {
            yield return wait;
            if (!sweepWarmupElapsed && Time.realtimeSinceStartup < sweepWarmupSeconds) continue;
            long reservedBytes = Profiler.GetTotalReservedMemoryLong();
            float reservedMB = reservedBytes / (1024f * 1024f);
            if (memoryWarningThresholdMB > 0f && reservedMB >= memoryWarningThresholdMB)
            {
                if (Time.realtimeSinceStartup - lastMemoryWarningTime >= Mathf.Max(2f, memoryCheckIntervalSeconds))
                {
                    Debug.LogWarning($"[LevelManager] Memory high: {reservedMB:0} MB (danger at {memoryDangerThresholdMB:0} MB)");
                    lastMemoryWarningTime = Time.realtimeSinceStartup;
                }
            }
            if (memoryDangerThresholdMB > 0f && reservedMB >= memoryDangerThresholdMB)
            {
                MaybeScheduleMemorySweep($"Reserved memory {reservedMB:0} MB >= {memoryDangerThresholdMB:0} MB", true);
            }
        }
    }
    public void SetPaused(bool paused)
    {
        if (IsPaused == paused) return;
        IsPaused = paused;
        Time.timeScale = IsPaused ? 0f : 1f;
        if (dictationManager) dictationManager.enabled = !IsPaused;
        if (phonemeManager)  phonemeManager.enabled  = !IsPaused;
        if (tracingSystem)   tracingSystem.enabled   = !IsPaused;
        if (objectManager)   objectManager.enabled   = !IsPaused;
        Debug.Log($"[LevelManager] {(IsPaused ? "Paused" : "Resumed")}");
    }
    public void RequestSceneReload(string reason, bool preserveState = true)
    {
        Debug.LogWarning($"[LevelManager] Scene reload requested ({reason}) but reloads are disabled; scheduling memory sweep instead.");
        MaybeScheduleMemorySweep(reason, true);
    }
    private static string SafeGet(List<string> list, int i) =>
        (list != null && i >= 0 && i < list.Count) ? list[i] : null;
    private void OnDestroy()
    {
        if (pauseBtn) pauseBtn.OnButtonPressed -= HandlePausePressed;
        if (phonemeManager != null)
        {
            phonemeManager.OnPhonemeCorrect -= HandlePhonemeCorrect;
            phonemeManager.OnPhonemeTriesExhausted -= HandlePhonemeTriesExhausted;
        }
        if (tracingSystem != null) tracingSystem.OnTraceCompleted -= OnTraceCompleted;
        if (objectManager != null) objectManager.OnObjectCollected -= OnObjectCollected;
        if (dictationManager != null) dictationManager.OnDictationComplete -= OnDictationComplete;
        if (memoryGuardRoutine != null)
        {
            StopCoroutine(memoryGuardRoutine);
            memoryGuardRoutine = null;
        }
        if (memorySweepRoutine != null)
        {
            StopCoroutine(memorySweepRoutine);
            memorySweepRoutine = null;
        }
    }
    [Serializable]
    public class Phase
    {
        public List<string> Letters = new List<string>();
        public List<string> Phonemes = new List<string>();
        public List<GameMode> Modes = new List<GameMode>();
    }
    [Serializable]
    private class PhaseData
    {
        public List<string> Letters;
        public List<string> Phonemes;
        public List<string> Modes;
    }
    [Serializable]
    private class LevelPlanWrapper
    {
        public List<PhaseData> levelplan;
        public string currentLetter;
    }
}




