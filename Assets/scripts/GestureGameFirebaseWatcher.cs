using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

/// <summary>
/// Lightweight watcher used in the gesture mini-game scene. Polls the same
/// Firestore document as FirebaseLevelSyncSinglePlan and returns to the
/// main tracing scene when the dashboard stops targeting the mini-game.
/// </summary>
public class GestureGameFirebaseWatcher : MonoBehaviour
{
    [Header("REST Config")]
    [SerializeField] private string projectId = "homedojo-dashboard";
    [SerializeField] private string apiKey = "AIzaSyA_f94XSrBcmuge4VSD8avpgJ6iWRpOC3g";
    [SerializeField] private string room = "default";
    [SerializeField, Range(0.1f, 2f)]
    private float pollInterval = 0.5f;

    [Header("Scene Routing")]
    [SerializeField] private string minigameSceneId = "multiplechoice";
    [SerializeField] private string mainSceneName = "simpleLetterTrace";

    Coroutine _pollRoutine;
    string _minigameSceneIdNormalized;

    void OnEnable()
    {
        _minigameSceneIdNormalized = NormalizeSceneId(minigameSceneId);
        if (_pollRoutine == null)
            _pollRoutine = StartCoroutine(PollLoop());
    }

    void OnDisable()
    {
        if (_pollRoutine != null)
        {
            StopCoroutine(_pollRoutine);
            _pollRoutine = null;
        }
    }

    IEnumerator PollLoop()
    {
        string url = $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/levelmanager_control/{room}?key={apiKey}";
        var wait = new WaitForSecondsRealtime(Mathf.Max(0.05f, pollInterval));

        while (enabled && gameObject.activeInHierarchy)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
                bool ok = request.result == UnityWebRequest.Result.Success;
#else
                bool ok = !request.isNetworkError && !request.isHttpError;
#endif

                if (ok)
                {
                    string targetScene = ExtractSceneId(request.downloadHandler.text);
                    if (!string.IsNullOrEmpty(targetScene) && !IsMiniGameScene(targetScene))
                    {
                        SceneManager.LoadScene(mainSceneName, LoadSceneMode.Single);
                        yield break;
                    }
                }
            }

            yield return wait;
        }
    }

    string ExtractSceneId(string json)
    {
        try
        {
            var root = JObject.Parse(json);
            var fields = root["fields"] as JObject;
            if (fields == null)
                return null;

            return ReadString(fields, "activeScene");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[GestureGameFirebaseWatcher] Failed to parse Firestore payload: {ex.Message}");
            return null;
        }
    }

    static string ReadString(JObject fields, string key, string fallback = "")
    {
        if (fields.TryGetValue(key, out var token))
        {
            if (token is JObject map && map.TryGetValue("stringValue", out var valueToken))
                return valueToken.ToString();
        }
        return fallback;
    }

    bool IsMiniGameScene(string sceneId)
    {
        string normalized = NormalizeSceneId(sceneId);
        return normalized == _minigameSceneIdNormalized;
    }

    static string NormalizeSceneId(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId))
            return string.Empty;
        string lowered = sceneId.Trim().ToLowerInvariant();
        if (lowered == "simplelettertrace")
            return "main";
        return lowered;
    }
}
