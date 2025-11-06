using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class FirebaseLevelSyncSinglePlan : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private LevelManager levelManager;
    [Tooltip("Single plan JSON (letters/phonemes). Modes are injected at runtime.")]
    [SerializeField] private TextAsset basePlanJson;

    [Header("REST Config")]
    [SerializeField] private string projectId = "homedojo-dashboard";
    [SerializeField] private string apiKey = "AIzaSyA_f94XSrBcmuge4VSD8avpgJ6iWRpOC3g";
    [SerializeField] private string room = "default";
    [SerializeField, Range(0.1f, 2f)] private float pollInterval = 0.5f; // seconds

    // change tracking
    private long _lastSeq = -1;
    private string _lastDrill = null;
    private string _lastLetter = null;
    private string _lastCommand = null;
    private readonly List<string> _lastLevelLetters = new List<string>();

    private static readonly IReadOnlyList<string> DefaultDictationWords = SharedWordLibrary.Words;

    private readonly Dictionary<string, int> _phaseByDrill = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _defaultLetterByDrill = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private const string SceneIdMain = "main";
    private const string SceneIdMinigame = "multiplechoice";
    private const string UnitySceneMainName = "simpleLetterTrace";
    private const string UnitySceneMinigameName = "multiplechoice";

    private static FirebaseLevelSyncSinglePlan _instance;
    private string _lastSceneName = SceneIdMain;
    private string _desiredScene = SceneIdMain;
    private bool _sceneTransitionInProgress;

    // reflection into LevelManager to swap plan cleanly
    private FieldInfo _fiLevelPlan;
    private FieldInfo _fiPhaseIndex;
    private FieldInfo _fiLetterIndex;
    private FieldInfo _fiModeIndex;

    [Serializable] private class PhaseDTO { public List<string> Letters; public List<string> Phonemes; public List<string> Modes; }
    [Serializable] private class WrapperDTO { public List<PhaseDTO> levelplan; public string currentLetter; }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += HandleSceneLoaded;

        _lastSceneName = NormalizeSceneId(SceneManager.GetActiveScene().name);
        _desiredScene = _lastSceneName;

        if (!basePlanJson)
        {
            Debug.LogError("[HttpSync] Assign basePlanJson.");
            enabled = false;
            return;
        }

        if (!levelManager)
        {
            LocateLevelManager();
            if (!levelManager)
            {
                Debug.LogWarning("[HttpSync] LevelManager not found in current scene; awaiting scene load.");
            }
        }

        var t = typeof(LevelManager);
        _fiLevelPlan  = t.GetField("levelPlan",  BindingFlags.Instance | BindingFlags.NonPublic);
        _fiPhaseIndex = t.GetField("phaseIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        _fiLetterIndex= t.GetField("letterIndex",BindingFlags.Instance | BindingFlags.NonPublic);
        _fiModeIndex  = t.GetField("modeIndex",  BindingFlags.Instance | BindingFlags.NonPublic);
        if (_fiLevelPlan == null || _fiPhaseIndex == null || _fiLetterIndex == null || _fiModeIndex == null)
        {
            Debug.LogError("[HttpSync] LevelManager private fields not found (signature changed?).");
            enabled = false;
            return;
        }
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            _instance = null;
        }
    }

    private IEnumerator Start()
    {
        int frames = 0;
        while (frames < 10 && (levelManager == null || string.IsNullOrEmpty(levelManager.currentLetter)))
        {
            if (!levelManager)
            {
                LocateLevelManager();
            }
            frames++;
            yield return null;
        }

        yield return StartCoroutine(BootstrapOnce());
        StartCoroutine(PollLoop());
    }

    private IEnumerator PollLoop()
    {
        var url = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/levelmanager_control/{room}?key={apiKey}";
        while (enabled)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success;
#else
                bool ok = !req.isNetworkError && !req.isHttpError;
#endif
                if (ok)
                {
                    try
                    {
                        var root = JObject.Parse(req.downloadHandler.text);
                        var fields = (JObject)root["fields"];
                        if (fields != null)
                        {
                            long seq = FInt(fields, "seq", -1);
                            string drill = FStr(fields, "currentDrill", "Audio");
                            string letter = FStr(fields, "currentLetter", "A");
                            string command = FStr(fields, "command", "none");
                            string sceneName = NormalizeSceneId(FStr(fields, "activeScene", _lastSceneName));
                            List<string> levelLetters = FArray(fields, "levelLetters");

                            bool first = (_lastSeq < 0);
                            bool drillChanged  = !string.Equals(drill,  _lastDrill,  StringComparison.OrdinalIgnoreCase);
                            bool letterChanged = !string.Equals(letter, _lastLetter, StringComparison.OrdinalIgnoreCase);
                            bool commandChanged= !string.Equals(command,_lastCommand,StringComparison.OrdinalIgnoreCase);
                            bool seqChanged    = (seq != _lastSeq);
                            bool lettersChanged= !SequenceEquals(levelLetters, _lastLevelLetters);
                            bool sceneChanged  = !string.Equals(sceneName, _lastSceneName, StringComparison.OrdinalIgnoreCase);

                            EnsureScene(sceneName);

                            if (first || drillChanged || letterChanged || commandChanged || seqChanged || lettersChanged || sceneChanged)
                            {
                                Apply(drill, letter, command, levelLetters, sceneName);
                                _lastSeq = seq;
                                _lastDrill = drill;
                                _lastLetter = letter;
                                _lastCommand = command;
                                _lastLevelLetters.Clear();
                                if (levelLetters != null)
                                    _lastLevelLetters.AddRange(levelLetters);
                            }
                            _lastSceneName = sceneName;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[HttpSync] Poll parse error: " + ex.Message);
                    }
                }
            }
            yield return new WaitForSecondsRealtime(pollInterval);
        }
    }

    private void Apply(string drill, string letter, string command, List<string> levelLetters, string sceneName)
    {
        string normalizedScene = NormalizeSceneId(sceneName);
        bool inMainScene = string.Equals(normalizedScene, SceneIdMain, StringComparison.OrdinalIgnoreCase);

        if (!inMainScene)
        {
            return;
        }

        if (!levelManager)
        {
            LocateLevelManager();
            if (!levelManager)
            {
                Debug.LogWarning("[HttpSync] LevelManager unavailable in main scene; deferring apply.");
                return;
            }
        }

        bool drillChanged = !string.Equals(drill, _lastDrill, StringComparison.OrdinalIgnoreCase);
        bool lettersChanged = !SequenceEquals(levelLetters, _lastLevelLetters);

        if (!string.IsNullOrEmpty(drill))
        {
            try { levelManager.SetCurrentDrill(drill); }
            catch (Exception ex) { Debug.LogWarning("[HttpSync] SetCurrentDrill failed: " + ex.Message); }
        }

        Dictionary<string, int> newPhaseMap = null;
        Dictionary<string, string> newDefaultsMap = null;
        string suggestedLetter = null;
        bool planSwapped = false;
        bool letterAppliedDuringSwap = false;

        if (drillChanged || lettersChanged)
        {
            if (TrySwapPlan(drill, levelLetters, out suggestedLetter, out newPhaseMap, out newDefaultsMap))
            {
                planSwapped = true;

                _phaseByDrill.Clear();
                if (newPhaseMap != null)
                {
                    foreach (var kvp in newPhaseMap)
                        _phaseByDrill[kvp.Key] = kvp.Value;
                }

                _defaultLetterByDrill.Clear();
                if (newDefaultsMap != null)
                {
                    foreach (var kvp in newDefaultsMap)
                        _defaultLetterByDrill[kvp.Key] = kvp.Value;
                }

                string fallbackLetter = ResolveFallbackLetter(drill);
                if (string.IsNullOrEmpty(fallbackLetter))
                    fallbackLetter = suggestedLetter;

                string targetLetter = !string.IsNullOrEmpty(letter) ? letter : fallbackLetter;
                letterAppliedDuringSwap = TryApplyLetterForDrill(drill, targetLetter, fallbackLetter);
            }
        }

        bool restartRequested = string.Equals(command, "restart", StringComparison.OrdinalIgnoreCase);
        bool clearRequested = string.Equals(command, "clear", StringComparison.OrdinalIgnoreCase);

        if (restartRequested || clearRequested)
        {
            SafeRestart();
        }

        if (restartRequested)
        {
            bool applied = TryApplyLetterForDrill(
                drill,
                !string.IsNullOrEmpty(letter) ? letter : ResolveFallbackLetter(drill),
                ResolveFallbackLetter("Audio"));
            if (applied)
                letterAppliedDuringSwap = true;
        }

        bool letterChanged = !string.Equals(letter, _lastLetter, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(letter) &&
            letterChanged &&
            !string.Equals(command, "clear", StringComparison.OrdinalIgnoreCase))
        {
            if (!letterAppliedDuringSwap)
            {
                TryApplyLetterForDrill(drill, letter, ResolveFallbackLetter(drill));
            }
        }
        else if (planSwapped && !letterAppliedDuringSwap)
        {
            string fallback = ResolveFallbackLetter(drill);
            string secondaryFallback = ResolveFallbackLetter("Audio");
            TryApplyLetterForDrill(drill, fallback, secondaryFallback);
        }
    }

    private IEnumerator BootstrapOnce()
    {
        var url = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/levelmanager_control/{room}?key={apiKey}";
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 10;
            yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            bool ok = req.result == UnityWebRequest.Result.Success;
#else
            bool ok = !req.isNetworkError && !req.isHttpError;
#endif
            if (ok)
            {
                try
                {
                    var root = JObject.Parse(req.downloadHandler.text);
                    var fields = (JObject)root["fields"];
                    if (fields != null)
                    {
                        long seq = FInt(fields, "seq", -1);
                        string drill = FStr(fields, "currentDrill", "Audio");
                        string letter = FStr(fields, "currentLetter", "A");
                        string command = FStr(fields, "command", "none");
                        string sceneName = NormalizeSceneId(FStr(fields, "activeScene", _lastSceneName));
                        List<string> levelLetters = FArray(fields, "levelLetters");

                        EnsureScene(sceneName);
                        Apply(drill, letter, command, levelLetters, sceneName);
                        _lastSeq = seq;
                        _lastDrill = drill;
                        _lastLetter = letter;
                        _lastCommand = command;
                        _lastSceneName = sceneName;
                        _lastLevelLetters.Clear();
                        if (levelLetters != null)
                            _lastLevelLetters.AddRange(levelLetters);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[HttpSync] Bootstrap parse error: " + ex.Message);
                }
            }
        }
    }

    private bool TrySwapPlan(string drillRaw, List<string> remoteLetters, out string suggestedLetter, out Dictionary<string, int> phaseByDrill, out Dictionary<string, string> defaultLettersByDrill)
    {
        suggestedLetter = null;
        phaseByDrill = null;
        defaultLettersByDrill = null;

        if (!levelManager)
            return false;

        string drill = (drillRaw ?? "Audio").Trim();

        WrapperDTO dto;
        try { dto = JsonConvert.DeserializeObject<WrapperDTO>(basePlanJson.text); }
        catch (Exception ex) { Debug.LogError("[HttpSync] Bad base plan JSON: " + ex.Message); return false; }

        var letterSequence = LooksLikeLetterList(remoteLetters)
            ? NormalizeLetterSequence(remoteLetters)
            : ExtractBaseLetters(dto);
        if (letterSequence.Count == 0)
            letterSequence = ExtractBaseLetters(dto);
        if (letterSequence.Count == 0)
            letterSequence.Add("a");

        var dictationWords = NormalizeWordList(remoteLetters);
        if (dictationWords.Count == 0)
            dictationWords.AddRange(DefaultDictationWords);

        var phases = new List<LevelManager.Phase>();

        var audioLetters = new List<string>(letterSequence);
        var audioPhase = new LevelManager.Phase
        {
            Letters = audioLetters,
            Phonemes = new List<string>(audioLetters),
            Modes = new List<LevelManager.GameMode>
            {
                LevelManager.GameMode.PhonemeChecking,
                LevelManager.GameMode.TraceChecking
            }
        };
        NormalizePhaseLettersAndPhonemes(audioPhase, true);
        phases.Add(audioPhase);

        var visualLetters = new List<string>(letterSequence);
        var visualPhase = new LevelManager.Phase
        {
            Letters = visualLetters,
            Phonemes = new List<string>(visualLetters),
            Modes = new List<LevelManager.GameMode> { LevelManager.GameMode.Dictation }
        };
        NormalizePhaseLettersAndPhonemes(visualPhase, true);
        phases.Add(visualPhase);

        var dictLetters = new List<string>(dictationWords);
        var dictPhase = new LevelManager.Phase
        {
            Letters = dictLetters,
            Phonemes = new List<string>(dictationWords),
            Modes = new List<LevelManager.GameMode> { LevelManager.GameMode.Dictation }
        };
        NormalizePhaseLettersAndPhonemes(dictPhase, false);
        phases.Add(dictPhase);

        var freeLetters = letterSequence.Count > 0 ? new List<string>(letterSequence) : new List<string> { "a" };
        var freePhase = new LevelManager.Phase
        {
            Letters = freeLetters,
            Phonemes = new List<string>(freeLetters),
            Modes = new List<LevelManager.GameMode> { LevelManager.GameMode.FreeDrawing }
        };
        NormalizePhaseLettersAndPhonemes(freePhase, true);
        phases.Add(freePhase);

        try
        {
            _fiLevelPlan.SetValue(levelManager, phases);
            _fiPhaseIndex.SetValue(levelManager, 0);
            _fiLetterIndex.SetValue(levelManager, 0);
            _fiModeIndex.SetValue(levelManager, 0);
            Debug.Log($"[HttpSync] Drill -> {drill} (plan swapped).");
        }
        catch (Exception ex)
        {
            Debug.LogError("[HttpSync] Reflection write failed: " + ex.Message);
            return false;
        }

        phaseByDrill = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Audio"] = 0,
            ["Visual"] = 1,
            ["Dictation"] = 2,
            ["FreeDraw"] = 3,
            ["Free Drawing"] = 3
        };

        defaultLettersByDrill = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Audio"] = FirstOrDefault(audioPhase.Letters),
            ["Visual"] = FirstOrDefault(visualPhase.Letters),
            ["Dictation"] = FirstOrDefault(dictPhase.Letters),
            ["FreeDraw"] = FirstOrDefault(freePhase.Letters),
            ["Free Drawing"] = FirstOrDefault(freePhase.Letters)
        };

        suggestedLetter = ResolveDefaultLetter(drill, defaultLettersByDrill);
        if (string.IsNullOrEmpty(suggestedLetter))
        {
            suggestedLetter = FirstOrDefault(audioPhase.Letters);
            if (string.IsNullOrEmpty(suggestedLetter))
                suggestedLetter = FirstOrDefault(dictPhase.Letters);
        }

        return true;
    }

    private void SafeRestart()
    {
        if (!levelManager)
            return;
        try { levelManager.Restart(); Debug.Log("[HttpSync] Restart()"); }
        catch (Exception ex) { Debug.LogError("[HttpSync] Restart failed: " + ex.Message); }
    }

    private static string FStr(JObject f, string key, string defVal)
    {
        var v = f[key];
        if (v == null) return defVal;
        return v["stringValue"]?.Value<string>() ?? defVal;
    }

    private static long FInt(JObject f, string key, long defVal)
    {
        var v = f[key];
        if (v == null) return defVal;
        var sv = v["integerValue"]?.Value<string>();
        if (long.TryParse(sv, out var l)) return l;
        var dv = v["doubleValue"]?.Value<double>();
        if (dv.HasValue) return (long)dv.Value;
        return defVal;
    }

    private static List<string> FArray(JObject fields, string key)
    {
        var arrayField = fields?[key]?["arrayValue"]?["values"] as JArray;
        if (arrayField == null) return new List<string>();
        var list = new List<string>(arrayField.Count);
        foreach (var item in arrayField)
        {
            string val = item?["stringValue"]?.Value<string>();
            if (!string.IsNullOrEmpty(val))
                list.Add(val);
        }
        return list;
    }

    private static bool SequenceEquals(List<string> a, List<string> b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool LooksLikeLetterList(List<string> values)
    {
        if (values == null) return false;
        bool seenEntry = false;
        foreach (var entry in values)
        {
            string trimmed = (entry ?? string.Empty).Trim();
            if (trimmed.Length == 0) continue;
            seenEntry = true;
            if (trimmed.Length != 1)
                return false;
            if (!char.IsLetter(trimmed[0]))
                return false;
        }
        return seenEntry;
    }

    private static List<string> ExtractBaseLetters(WrapperDTO dto)
    {
        var letters = new List<string>();
        if (dto?.levelplan == null) return letters;
        foreach (var phase in dto.levelplan)
        {
            if (phase?.Letters == null) continue;
            foreach (var entry in phase.Letters)
            {
                string trimmed = (entry ?? string.Empty).Trim();
                if (trimmed.Length == 0) continue;
                string normalized = trimmed.Substring(0, 1).ToLowerInvariant();
                if (!letters.Contains(normalized))
                    letters.Add(normalized);
            }
        }
        return letters;
    }

    private static string FirstOrDefault(IList<string> list)
    {
        return (list != null && list.Count > 0) ? list[0] : null;
    }

    private static bool ContainsIgnoreCase(List<string> list, string value)
    {
        if (list == null || value == null) return false;
        foreach (var item in list)
        {
            if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string ResolveDefaultLetter(string drill, Dictionary<string, string> defaults)
    {
        if (defaults == null || defaults.Count == 0) return null;
        if (!string.IsNullOrEmpty(drill) && defaults.TryGetValue(drill, out var letter) && !string.IsNullOrEmpty(letter))
            return letter;
        if (defaults.TryGetValue("Audio", out var audioLetter) && !string.IsNullOrEmpty(audioLetter))
            return audioLetter;
        foreach (var value in defaults.Values)
        {
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        return null;
    }

    private int ResolvePhaseIndex(string drill)
    {
        if (!string.IsNullOrEmpty(drill) && _phaseByDrill.TryGetValue(drill, out var idx))
            return idx;
        if (_phaseByDrill.TryGetValue("Audio", out var audioIdx))
            return audioIdx;
        foreach (var value in _phaseByDrill.Values)
            return value;
        return -1;
    }

    private string ResolveFallbackLetter(string drill)
    {
        if (!string.IsNullOrEmpty(drill) && _defaultLetterByDrill.TryGetValue(drill, out var letter) && !string.IsNullOrEmpty(letter))
            return letter;
        if (_defaultLetterByDrill.TryGetValue("Audio", out var audioLetter) && !string.IsNullOrEmpty(audioLetter))
            return audioLetter;
        foreach (var value in _defaultLetterByDrill.Values)
        {
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        return null;
    }

    private bool TryApplyLetterForDrill(string drill, string primaryLetter, string fallbackLetter = null)
    {
        if (!levelManager)
            return false;

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(primaryLetter))
            candidates.Add(primaryLetter);
        if (!string.IsNullOrEmpty(fallbackLetter) && !ContainsIgnoreCase(candidates, fallbackLetter))
            candidates.Add(fallbackLetter);
        if (candidates.Count == 0)
            return false;

        int targetPhase = ResolvePhaseIndex(string.IsNullOrEmpty(drill) ? "Audio" : drill);

        foreach (var candidate in candidates)
        {
            bool applied = false;
            if (targetPhase >= 0)
            {
                try
                {
                    applied = levelManager.TrySetLevelByLetter(candidate, targetPhase);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[HttpSync] TrySetLevelByLetter({candidate},{drill}) failed: {ex.Message}");
                }
            }

            if (applied)
                return true;
            else
                Debug.LogWarning($"[HttpSync] Letter '{candidate}' not found in drill '{drill}'.");
        }

        return false;
    }

    private void EnsureScene(string targetSceneId)
    {
        string normalizedId = NormalizeSceneId(targetSceneId);
        _desiredScene = normalizedId;

        var active = SceneManager.GetActiveScene();
        string activeUnity = active.name;
        string targetUnity = ResolveUnitySceneName(normalizedId);

        if (string.Equals(activeUnity, targetUnity, StringComparison.Ordinal))
        {
            _sceneTransitionInProgress = false;
            return;
        }

        if (_sceneTransitionInProgress)
            return;

        StartCoroutine(LoadSceneRoutine(targetUnity, normalizedId));
    }

    private IEnumerator LoadSceneRoutine(string unitySceneName, string sceneId)
    {
        _sceneTransitionInProgress = true;
        AsyncOperation op = null;
        try
        {
            op = SceneManager.LoadSceneAsync(unitySceneName, LoadSceneMode.Single);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HttpSync] Scene load '{unitySceneName}' failed: {ex.Message}");
            _sceneTransitionInProgress = false;
            yield break;
        }

        if (op != null)
        {
            while (!op.isDone)
                yield return null;
        }

        _sceneTransitionInProgress = false;
        _desiredScene = sceneId;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _sceneTransitionInProgress = false;
        _lastSceneName = NormalizeSceneId(scene.name);
        _desiredScene = _lastSceneName;
        LocateLevelManager();
        if (string.Equals(_lastSceneName, SceneIdMain, StringComparison.OrdinalIgnoreCase))
        {
            StartCoroutine(ResyncAfterSceneLoad());
        }
    }

    private void LocateLevelManager()
    {
        if (levelManager != null)
            return;

        levelManager = FindObjectOfType<LevelManager>();
        if (levelManager != null)
        {
            Debug.Log("[HttpSync] LevelManager reference refreshed.");
        }
    }

    private IEnumerator ResyncAfterSceneLoad()
    {
        // Give the scene a moment to finish wiring dependencies.
        yield return null;
        yield return null;

        LocateLevelManager();
        if (!levelManager || !string.Equals(_lastSceneName, SceneIdMain, StringComparison.OrdinalIgnoreCase))
            yield break;
        if (_lastSeq < 0)
            yield break;

        var rememberedLetters = new List<string>(_lastLevelLetters);
        var lettersCopy = new List<string>(_lastLevelLetters);
        _lastLevelLetters.Clear();
        try
        {
            Apply(_lastDrill ?? "Audio", _lastLetter ?? "a", _lastCommand ?? "none", lettersCopy, _lastSceneName);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[HttpSync] Resync after scene load failed: " + ex.Message);
        }
        finally
        {
            _lastLevelLetters.Clear();
            _lastLevelLetters.AddRange(rememberedLetters);
        }
    }

    private static string NormalizeSceneId(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName))
            return SceneIdMain;

        string trimmed = sceneName.Trim();
        string lowered = trimmed.ToLowerInvariant();

        if (lowered == "simplelettertrace")
            return SceneIdMain;
        if (lowered == SceneIdMain)
            return SceneIdMain;
        if (lowered == SceneIdMinigame)
            return SceneIdMinigame;

        return lowered;
    }

    private static string ResolveUnitySceneName(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            return UnitySceneMainName;

        string lowered = sceneId.Trim().ToLowerInvariant();
        if (lowered == SceneIdMain || lowered == "simplelettertrace")
            return UnitySceneMainName;
        if (lowered == SceneIdMinigame)
            return UnitySceneMinigameName;
        return sceneId.Trim();
    }

    private static List<string> NormalizeWordList(List<string> words)
    {
        var list = new List<string>();
        if (words != null)
        {
            foreach (var w in words)
            {
                string trimmed = (w ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    list.Add(trimmed.ToLowerInvariant());
            }
        }
        return list;
    }

    private static List<string> NormalizeLetterSequence(List<string> letters)
    {
        var list = new List<string>();
        if (letters != null)
        {
            foreach (var l in letters)
            {
                string trimmed = (l ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                string lower = trimmed.Substring(0, 1).ToLowerInvariant();
                list.Add(lower);
            }
        }
        return list;
    }

    private static void NormalizePhaseLettersAndPhonemes(LevelManager.Phase phase, bool singleCharacter)
    {
        if (phase == null) return;
        if (phase.Letters == null)
            phase.Letters = new List<string>();
        if (phase.Phonemes == null)
            phase.Phonemes = new List<string>();

        for (int i = 0; i < phase.Letters.Count; i++)
        {
            string raw = phase.Letters[i] ?? string.Empty;
            raw = raw.Trim();
            string lower = string.Empty;
            if (raw.Length > 0)
                lower = singleCharacter ? raw.Substring(0, 1).ToLowerInvariant() : raw.ToLowerInvariant();
            phase.Letters[i] = lower;

            if (phase.Phonemes.Count <= i)
                phase.Phonemes.Add(lower);
            else
                phase.Phonemes[i] = lower;
        }

        if (phase.Phonemes.Count > phase.Letters.Count)
            phase.Phonemes.RemoveRange(phase.Letters.Count, phase.Phonemes.Count - phase.Letters.Count);
    }
}
