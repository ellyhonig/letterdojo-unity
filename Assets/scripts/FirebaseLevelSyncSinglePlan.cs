using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

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

    // de-dupe
    private long _lastSeq = -1;
    private string _lastDrill = null;
    private string _lastLetter = null;
    private string _lastCommand = null;

    // reflection into LevelManager to swap plan cleanly
    private FieldInfo _fiLevelPlan;
    private FieldInfo _fiPhaseIndex;
    private FieldInfo _fiLetterIndex;
    private FieldInfo _fiModeIndex;

    [Serializable] class PhaseDTO { public List<string> Letters; public List<string> Phonemes; public List<string> Modes; }
    [Serializable] class WrapperDTO { public List<PhaseDTO> levelplan; public string currentLetter; }

    private void Awake()
    {
        if (!levelManager) { Debug.LogError("[HttpSync] Assign LevelManager."); enabled = false; return; }
        if (!basePlanJson) { Debug.LogError("[HttpSync] Assign basePlanJson."); enabled = false; return; }

        var t = typeof(LevelManager);
        _fiLevelPlan  = t.GetField("levelPlan",  BindingFlags.Instance | BindingFlags.NonPublic);
        _fiPhaseIndex = t.GetField("phaseIndex", BindingFlags.Instance | BindingFlags.NonPublic);
        _fiLetterIndex= t.GetField("letterIndex",BindingFlags.Instance | BindingFlags.NonPublic);
        _fiModeIndex  = t.GetField("modeIndex",  BindingFlags.Instance | BindingFlags.NonPublic);
        if (_fiLevelPlan == null || _fiPhaseIndex == null || _fiLetterIndex == null || _fiModeIndex == null)
        {
            Debug.LogError("[HttpSync] LevelManager private fields not found (signature changed?).");
            enabled = false; return;
        }
    }

    // Defer polling until Start(), after we conform once
    private void OnEnable() { }

    // Ensure we conform immediately on startup without waiting for the first poll tick
    private IEnumerator Start()
    {
        // Wait a few frames for LevelManager to finish Start()/LoadLevelPlan()
        // so our initial Apply won't be immediately overwritten.
        int frames = 0;
        while (frames < 10 && (levelManager == null || string.IsNullOrEmpty(levelManager.currentLetter)))
        {
            frames++;
            yield return null;
        }

        yield return StartCoroutine(BootstrapOnce());

        // Begin steady-state polling after initial conform
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
                            string command = FStr(fields, "command", "none"); // "restart" | "clear" | "none"

                            // Act whenever any meaningful field changed
                            bool first = (_lastSeq < 0);
                            bool drillChanged  = !string.Equals(drill,  _lastDrill,  StringComparison.OrdinalIgnoreCase);
                            bool letterChanged = !string.Equals(letter, _lastLetter, StringComparison.OrdinalIgnoreCase);
                            bool commandChanged= !string.Equals(command,_lastCommand,StringComparison.OrdinalIgnoreCase);
                            bool seqChanged    = (seq != _lastSeq);

                            if (first || drillChanged || letterChanged || commandChanged || seqChanged)
                            {
                                Apply(drill, letter, command);
                                _lastSeq = seq;
                                _lastDrill = drill;
                                _lastLetter = letter;
                                _lastCommand = command;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[HttpSync] Parse error: " + ex.Message);
                    }
                }
                else
                {
                    // 404 until the doc exists; or rules denied
                    // Debug.LogWarning($"[HttpSync] {req.responseCode} {req.error}");
                }
            }
            yield return new WaitForSecondsRealtime(pollInterval);
        }
    }

    private void Apply(string drill, string letter, string command)
    {
        bool drillChanged = !string.Equals(drill, _lastDrill, StringComparison.OrdinalIgnoreCase);

        if (drillChanged)
        {
            if (TrySwapPlan(drill))
            {
                // Do NOT Restart here; it would briefly start the first default letter and play its sound.
                // Instead, directly set the requested letter which will load data and start the correct mode.
                if (!string.IsNullOrEmpty(letter))
                {
                    try { levelManager.SetLevelByLetter(letter); }
                    catch (Exception ex) { Debug.LogError("[HttpSync] SetLevelByLetter after drill swap failed: " + ex.Message); }
                }
            }
        }

        if (string.Equals(command, "restart", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, "clear",   StringComparison.OrdinalIgnoreCase))
        {
            SafeRestart();
        }

        // Apply letter change if it changed since last seen; otherwise if we restarted above,
        // the SetLevelByLetter after drill swap already enforced the desired letter.
        if (!string.IsNullOrEmpty(letter) &&
            !string.Equals(letter, _lastLetter, StringComparison.OrdinalIgnoreCase) &&
            !drillChanged)
        {
            try { levelManager.SetLevelByLetter(letter); }
            catch (Exception ex) { Debug.LogError("[HttpSync] SetLevelByLetter failed: " + ex.Message); }
        }
    }

    // One-shot fetch-and-apply at startup so the scene conforms to Firebase immediately
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

                        Apply(drill, letter, command);
                        _lastSeq = seq;
                        _lastDrill = drill;
                        _lastLetter = letter;
                        _lastCommand = command;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[HttpSync] Bootstrap parse error: " + ex.Message);
                }
            }
            else
            {
                // Ignore failures here; PollLoop will retry
            }
        }
    }

    private bool TrySwapPlan(string drillRaw)
    {
        string drill = (drillRaw ?? "Audio").Trim();
        WrapperDTO dto;
        try { dto = JsonConvert.DeserializeObject<WrapperDTO>(basePlanJson.text); }
        catch (Exception ex) { Debug.LogError("[HttpSync] Bad base plan JSON: " + ex.Message); return false; }

        if (dto?.levelplan == null || dto.levelplan.Count == 0)
        {
            Debug.LogError("[HttpSync] Base plan empty.");
            return false;
        }

        // choose modes by drill
        List<LevelManager.GameMode> modes;
        if (string.Equals(drill, "Visual", StringComparison.OrdinalIgnoreCase))
            modes = new List<LevelManager.GameMode> { LevelManager.GameMode.Dictation };
        else
            modes = new List<LevelManager.GameMode> { LevelManager.GameMode.PhonemeChecking, LevelManager.GameMode.TraceChecking };

        // rebuild phases with injected modes
        var phases = new List<LevelManager.Phase>(dto.levelplan.Count);
        foreach (var p in dto.levelplan)
        {
            var phase = new LevelManager.Phase
            {
                Letters  = p?.Letters  ?? new List<string>(),
                Phonemes = p?.Phonemes ?? new List<string>(),
                Modes    = new List<LevelManager.GameMode>(modes)
            };
            NormalizePhaseLettersAndPhonemes(phase);
            phases.Add(phase);
        }

        try
        {
            _fiLevelPlan.SetValue(levelManager, phases);
            _fiPhaseIndex.SetValue(levelManager, 0);
            _fiLetterIndex.SetValue(levelManager, 0);
            _fiModeIndex.SetValue(levelManager, 0);
            Debug.Log($"[HttpSync] Drill → {drill} (plan swapped).");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("[HttpSync] Reflection write failed: " + ex.Message);
            return false;
        }
    }

    private void SafeRestart()
    {
        try { levelManager.Restart(); Debug.Log("[HttpSync] Restart()"); }
        catch (Exception ex) { Debug.LogError("[HttpSync] Restart failed: " + ex.Message); }
    }

    // -------- Firestore REST typed helpers --------
    private static string FStr(JObject f, string key, string defVal)
    {
        var v = f[key];
        if (v == null) return defVal;
        // stringValue or null
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

    private static void NormalizePhaseLettersAndPhonemes(LevelManager.Phase phase)
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
            string lower = raw.Length > 0 ? raw.Substring(0, 1).ToLowerInvariant() : string.Empty;
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
