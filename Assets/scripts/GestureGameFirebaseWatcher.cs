using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

[Serializable] class DocRoot { public Fields fields; }
[Serializable] class Fields { public StrVal activeScene; }
[Serializable] class StrVal { public string stringValue; }

public class GestureGameFirebaseWatcher : MonoBehaviour
{
    [Header("REST Config")]
    [SerializeField] private string projectId = "homedojo-dashboard";
    [SerializeField] private string apiKey = "AIzaSyA_f94XSrBcmuge4VSD8avpgJ6iWRpOC3g";
    [SerializeField] private string room = "default";
    [SerializeField, Range(0.5f, 5f)] private float pollInterval = 1.0f;

    [Header("Scene Routing")]
    [SerializeField] private string minigameSceneId = "multiplechoice";
    [SerializeField] private string mainSceneName = "simpleLetterTrace";

    Coroutine _pollRoutine;
    string _minigameSceneIdNormalized;
    string _etag;

    void OnEnable()
    {
        _minigameSceneIdNormalized = NormalizeSceneId(minigameSceneId);
        if (_pollRoutine == null)
        {
            _pollRoutine = StartCoroutine(PollLoop());
        }
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
        string url =
            $"https://firestore.googleapis.com/v1/projects/{projectId}/databases/(default)/documents/levelmanager_control/{room}" +
            $"?mask.fieldPaths=activeScene&key={apiKey}";
        var wait = new WaitForSecondsRealtime(Mathf.Max(0.5f, pollInterval));

        while (enabled && gameObject.activeInHierarchy)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 8;
                if (!string.IsNullOrEmpty(_etag))
                {
                    request.SetRequestHeader("If-None-Match", _etag);
                }

                yield return request.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
                bool ok = request.result == UnityWebRequest.Result.Success;
#else
                bool ok = !request.isNetworkError && !request.isHttpError;
#endif

                if (ok)
                {
                    _etag = request.GetResponseHeader("ETag") ?? _etag;
                    var root = JsonUtility.FromJson<DocRoot>(request.downloadHandler.text);
                    var targetScene = root?.fields?.activeScene?.stringValue;
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

    bool IsMiniGameScene(string sceneId) => NormalizeSceneId(sceneId) == _minigameSceneIdNormalized;

    static string NormalizeSceneId(string sceneId)
    {
        if (string.IsNullOrWhiteSpace(sceneId)) return string.Empty;
        string lowered = sceneId.Trim().ToLowerInvariant();
        if (lowered == "simplelettertrace") return "main";
        return lowered;
    }
}
