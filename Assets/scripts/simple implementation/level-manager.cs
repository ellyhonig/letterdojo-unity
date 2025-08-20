// LevelManager.cs
using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Collections;

public class LevelManager : MonoBehaviour
{
    public enum GameMode { PhonemeChecking, TraceChecking, ObjectPlacing, Dictation }

    // listenable: e.g., VisualEffectManager clears on mode swap
    public event Action<GameMode> OnGameModeChanged;

    public GameMode currentMode { get; private set; }
    public string   currentLetter { get; private set; }
    public string   currentPhoneme { get; private set; }
    public string   currentSound => currentPhoneme;
    public int      currentLevel { get; private set; }

    private int phaseIndex = 0;
    private int letterIndex = 0;
    private int modeIndex = 0;

    private List<Phase> levelPlan = new List<Phase>();

    [Header("Refs (set in Inspector)")]
    [SerializeField] private SimpleRecorder        recorder;
    [SerializeField] private LetterTracingSystem   tracingSystem;
    [SerializeField] private ObjectOfInterestManager objectManager;
    [SerializeField] private DictationManager      dictationManager;
    [SerializeField] public  PhonemeManager        phonemeManager;
    [SerializeField] private SaveManager           saveManager;

    public event Action OnPhonemeCheckStart;
    public event Action OnAllPhasesCompleted;

    private void Start()
    {
        InitializeComponents();
        LoadLevelPlan();
        currentLevel = 0;
        StartCurrentLetter();
    }

    private void InitializeComponents()
    {
        // fallback gets, but prefer Inspector
        if (!recorder)        recorder      = GetComponent<SimpleRecorder>();
        if (!tracingSystem)   tracingSystem = GetComponent<LetterTracingSystem>();
        if (!objectManager)   objectManager = GetComponent<ObjectOfInterestManager>();
        if (!dictationManager)dictationManager = GetComponent<DictationManager>();
        if (!saveManager)     saveManager   = GetComponent<SaveManager>();
        if (!phonemeManager)  phonemeManager= GetComponent<PhonemeManager>();

        // wire events (idempotent)
        if (phonemeManager)
        {
            phonemeManager.OnPhonemeCorrect += () =>
            {
                if (currentMode == GameMode.PhonemeChecking) ProceedToNextMode();
            };
            phonemeManager.OnPhonemeTriesExhausted += () => ProceedToNextMode();
        }

        if (tracingSystem)   tracingSystem.OnTraceCompleted    += OnTraceCompleted;
        if (objectManager)   objectManager.OnObjectCollected   += OnObjectCollected;
        if (dictationManager)dictationManager.OnDictationComplete += OnDictationComplete;
    }

    public void LoadLevelPlan()
{
    var ta = Resources.Load<TextAsset>("LevelPlan");
    if (ta == null)
    {
        Debug.LogError("LevelPlan resource not found!");
        return;
    }

    var wrapper = JsonConvert.DeserializeObject<LevelPlanWrapper>(ta.text);
    if (wrapper == null)
    {
        Debug.LogError("Failed to parse LevelPlanWrapper from JSON!");
        return;
    }

    levelPlan = wrapper.levelplan ?? new List<Phase>();
    if (!string.IsNullOrEmpty(wrapper.currentLetter))
        SetLevelByLetter(wrapper.currentLetter);

    Debug.Log($"Level plan loaded. Total phases: {levelPlan.Count} | currentLetter: {currentLetter ?? "(null)"}");
}

    private void StartCurrentLetter()
    {
        if (phaseIndex >= levelPlan.Count) { FinishAll(); return; }
        var currentPhase = levelPlan[phaseIndex];

        if (letterIndex < currentPhase.Letters.Count)
        {
            currentLetter  = currentPhase.Letters[letterIndex];
            currentPhoneme = SafeGet(currentPhase.Phonemes, letterIndex);
            if (string.IsNullOrEmpty(currentPhoneme)) currentPhoneme = currentLetter;

            if (modeIndex >= currentPhase.Modes.Count) modeIndex = 0;

            Debug.Log($"Phase {phaseIndex}, Letter: {currentLetter}, Phoneme: {currentPhoneme}");

            LoadLevelData();
            StartCurrentMode();
        }
        else
        {
            StartNextPhase();
        }
    }

    private static string SafeGet(List<string> list, int i) =>
        (list != null && i >= 0 && i < list.Count) ? list[i] : null;

    private void StartNextPhase()
    {
        phaseIndex++;
        if (phaseIndex < levelPlan.Count)
        {
            letterIndex = 0;
            modeIndex   = 0;
            StartCurrentLetter();
        }
        else FinishAll();
    }

    private void FinishAll()
    {
        OnAllPhasesCompleted?.Invoke();
        Debug.Log("All phases completed. Game finished!");
    }

    private void StartCurrentMode()
    {
        var currentPhase = levelPlan[phaseIndex];
        if (modeIndex < currentPhase.Modes.Count)
        {
            SetGameMode(currentPhase.Modes[modeIndex]);
        }
        else
        {
            modeIndex = 0;
            letterIndex++;
            currentLevel++;
            StartCurrentLetter();
        }
    }

    private void LoadLevelData() => StartCoroutine(LoadAfterStart());
    private IEnumerator LoadAfterStart()
    {
        yield return null; // wait a frame for SimpleRecorder.Start
        if (recorder) recorder.LoadRecording(currentLetter);
    }

    private void SetGameMode(GameMode mode)
    {
        currentMode = mode;

        // notify non-gameplay visuals to clear immediately (e.g., sphere trails)
        OnGameModeChanged?.Invoke(currentMode);

        // allow sub-systems to prep/hibernate
        UpdateComponentStates();

        // kickoff selected mode
        switch (currentMode)
        {
            case GameMode.PhonemeChecking:
                OnPhonemeCheckStart?.Invoke();
                break;

            case GameMode.TraceChecking:
                if (tracingSystem) tracingSystem.StartTracing();
                break;

            case GameMode.ObjectPlacing:
                if (objectManager) objectManager.StartObjectPlacing();
                break;

            case GameMode.Dictation:
                if (dictationManager)
                {
                    // make sure DM knows which letter/phoneme we’re on (it already reads from LevelManager, but this is explicit)
                    dictationManager.PrepareForMode(GameMode.Dictation);
                    dictationManager.StartDictation();
                }
                break;
        }
        Debug.Log($"[LevelManager] Mode → {currentMode} | Letter {currentLetter}");
    }

    private void UpdateComponentStates()
    {
        // enable/disable heavy systems by mode (optional but nice)
        bool isPhoneme = (currentMode == GameMode.PhonemeChecking);
        bool isTrace   = (currentMode == GameMode.TraceChecking);
        bool isObjs    = (currentMode == GameMode.ObjectPlacing);
        bool isDict    = (currentMode == GameMode.Dictation);

        if (phonemeManager)  phonemeManager.enabled  = isPhoneme;
        if (tracingSystem)   tracingSystem.enabled   = true; // handles its own internal active state via Start/Complete
        if (objectManager)   objectManager.enabled   = true;
        if (dictationManager)dictationManager.PrepareForMode(currentMode); // also hides drawerHost when not Dictation
    }

    private void OnTraceCompleted()
    {
        if (currentMode == GameMode.TraceChecking) ProceedToNextMode();
    }

    private void OnObjectCollected()
    {
        if (currentMode == GameMode.ObjectPlacing) ProceedToNextMode();
    }

    private void OnDictationComplete()
    {
        if (currentMode == GameMode.Dictation) ProceedToNextMode();
    }

    public void ProceedToNextMode()
    {
        var phase = levelPlan[phaseIndex];
        modeIndex++;

        // ensure dictation UI/flows are torn down if we’re leaving it
        if (dictationManager) dictationManager.PrepareForMode(GameMode.PhonemeChecking);

        if (modeIndex < phase.Modes.Count)
        {
            StartCurrentMode();
        }
        else
        {
            modeIndex = 0;
            letterIndex++;
            currentLevel++;
            StartCurrentLetter();
        }
    }

    public void Restart()
    {
        phaseIndex = 0; letterIndex = 0; modeIndex = 0; currentLevel = 0;
        StartCurrentLetter();
    }

    public void SetLevel(int level)
    {
        int total = 0;
        for (int p = 0; p < levelPlan.Count; p++)
        {
            var phase = levelPlan[p];
            for (int l = 0; l < phase.Letters.Count; l++)
            {
                if (total == level)
                {
                    phaseIndex = p; letterIndex = l; modeIndex = 0; currentLevel = level;
                    currentLetter  = phase.Letters[l];
                    currentPhoneme = SafeGet(phase.Phonemes, l) ?? currentLetter;
                    LoadLevelData();
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
        for (int p = 0; p < levelPlan.Count; p++)
        {
            var phase = levelPlan[p];
            int li = phase.Letters.IndexOf(letter);
            if (li >= 0)
            {
                if (li >= phase.Phonemes.Count)
                {
                    Debug.LogError($"Phonemes array too short for letter '{letter}'. Index {li}, phonemes {phase.Phonemes.Count}.");
                    return;
                }
                phaseIndex = p; letterIndex = li; modeIndex = 0;
                currentLetter  = phase.Letters[li];
                currentPhoneme = phase.Phonemes[li];
                LoadLevelData();
                StartCurrentMode();
                Debug.Log($"Set level to letter '{letter}': Phase {p}, LetterIndex {li}");
                return;
            }
        }
        Debug.LogWarning($"Letter '{letter}' not found in the level plan.");
    }

    private void OnDestroy()
    {
        if (tracingSystem)    tracingSystem.OnTraceCompleted    -= OnTraceCompleted;
        if (objectManager)    objectManager.OnObjectCollected   -= OnObjectCollected;
        if (dictationManager) dictationManager.OnDictationComplete -= OnDictationComplete;
    }

    [Serializable] public class Phase
    {
        public List<string> Letters;
        public List<string> Phonemes;
        public List<GameMode> Modes;
    }

    public class LevelPlanWrapper
    {
        public List<Phase> levelplan;
        public string currentLetter;
    }
}
