using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TMPro;

[RequireComponent(typeof(PlaneSurfaceDrawer))]
public class VisualDrillManager : MonoBehaviour
{
    [Header("Flow UI")]
    [SerializeField] private TMP_Text promptText;
    [SerializeField] private TMP_Text feedbackText;

    [Header("Timing")]
    [SerializeField] private float gradeUnlockDelay = 1.25f;
    [SerializeField] private float waitAfterCorrect = 0.6f;
    [SerializeField] private float waitAfterFailureAdvance = 0.4f;

    [Header("Hints")]
    [SerializeField] private Renderer hintRenderer;
    [SerializeField] private float hintDisplaySeconds = 2.2f;

    [Header("Board Capture")]
    [SerializeField] private Camera boardCamera;
    [SerializeField] private Renderer boardRenderer;
    [SerializeField] private LayerMask boardLayer = 0;
    [SerializeField] private int captureResolution = 1024;
    [SerializeField, Range(1f, 6f)] private float captureWidthMultiplier = 2f;
    [SerializeField] private bool debugWriteCapture = false;

    [Header("Vision OCR")]
    [SerializeField] private string visionEndpoint = "https://vision.googleapis.com/v1/images:annotate";
    [SerializeField] private string serviceAccountJsonResource = "plenary-treat-471015-i5-84297c030ee9";
    [SerializeField] private string[] languageHints;

    [Header("Plan Source")]
    [SerializeField] private TextAsset levelPlanOverride;
    [SerializeField] private bool autostart = true;
    [SerializeField] private bool loopPlan = true;

    [Header("Optional Refs")]
    [SerializeField] private GameObject drawerHost;
    [SerializeField] private LevelManager levelManager;

    public event Action<string> OnLetterStarted;
    public event Action<string, bool> OnLetterCompleted;

    public string CurrentLetter => _currentExpected;
    public string LastRecognizedRaw => _lastRecognized;
    public bool CanGrade => _state == DrillState.WaitingForDraw && Time.time >= _gradeUnlockAt && _gradeRoutine == null;

    private PlaneSurfaceDrawer planeDrawer;

    private WaitForSeconds _waitAfterCorrectYield;
    private WaitForSeconds _waitAfterFailureYield;
    private WaitForSeconds _hintDurationWait;
    private readonly WaitForEndOfFrame _endOfFrame = new WaitForEndOfFrame();

    private RenderTexture _captureRT;
    private Texture2D _captureTex;

    private Coroutine _gradeRoutine;
    private Coroutine _hintRoutine;

    private float _gradeUnlockAt;

    private MaterialPropertyBlock _hintPropertyBlock;
    private readonly Dictionary<string, Texture2D> _hintTextureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private readonly List<StrokeState> _strokeOverrides = new List<StrokeState>(32);

    private string[] _letters = Array.Empty<string>();
    private int _currentLetterIndex = -1;
    private int _attempt;
    private DrillState _state = DrillState.Inactive;
    private string _currentExpected = string.Empty;
    private string _lastRecognized = string.Empty;

    private string _cachedAccessToken;
    private double _tokenExpiry;

    private VisionRequestWrapper _visionPrimary;
    private VisionRequestWrapper _visionFallback;

    private void ResolveLevelManager()
    {
        if (levelManager) return;
        levelManager = GetComponent<LevelManager>() ?? GetComponentInParent<LevelManager>();
        if (!levelManager)
            levelManager = FindObjectOfType<LevelManager>();
    }

    private void LateUpdate()
    {
        // Prompt visibility is managed by PhonemeManager.
    }

    private void HookLevelManager()
    {
        ResolveLevelManager();
        if (!levelManager) return;

        levelManager.OnGameModeChanged -= HandleGameModeChanged;
        levelManager.OnGameModeChanged += HandleGameModeChanged;
        HandleGameModeChanged(levelManager.currentMode);
    }

    private void UnhookLevelManager()
    {
        if (!levelManager) return;
        levelManager.OnGameModeChanged -= HandleGameModeChanged;
    }

    private static readonly Gradient sBlackGradient = BuildSolidGradient(Color.black);
    private static readonly string[] sDefaultLanguageHints = new[] { "en" };
    private static readonly int ID_BaseMap = Shader.PropertyToID("_BaseMap");
    private static readonly int ID_MainTex = Shader.PropertyToID("_MainTex");

    private enum DrillState
    {
        Inactive,
        WaitingForDraw,
        Processing
    }

    private struct StrokeState
    {
        public LineRenderer line;
        public float width;
        public Gradient gradient;
    }

    [Serializable]
    private class LevelPlanWrapper
    {
        public Phase[] levelplan;
        public string currentLetter;
    }

    [Serializable]
    private class Phase
    {
        public string[] Letters;
        public string[] Phonemes;
        public string[] Modes;
    }

    [Serializable]
    private class ServiceAccountJson
    {
        public string type;
        public string project_id;
        public string private_key_id;
        public string private_key;
        public string client_email;
        public string client_id;
        public string auth_uri;
        public string token_uri;
    }

    [Serializable]
    private class TokenResp
    {
        public string access_token;
        public int expires_in;
    }

    [Serializable]
    private class VisionRequestWrapper
    {
        public VisionRequest[] requests;
    }

    [Serializable]
    private class VisionRequest
    {
        public VisionImage image;
        public VisionFeature[] features;
        public VisionImageContext imageContext;
    }

    [Serializable]
    private class VisionImage
    {
        public string content;
    }

    [Serializable]
    private class VisionFeature
    {
        public string type;
        public int maxResults;
    }

    [Serializable]
    private class VisionImageContext
    {
        public string[] languageHints;
    }

    [Serializable]
    private class VisionRespWrapper
    {
        public VisionResp[] responses;
    }

    [Serializable]
    private class VisionResp
    {
        public VisionTextAnn[] textAnnotations;
        public VisionFullText fullTextAnnotation;
    }
    [Serializable]
    private class VisionTextAnn
    {
        public string description;
    }

    [Serializable]
    private class VisionFullText
    {
        public string text;
    }

    private void Awake()
    {
        planeDrawer = GetComponent<PlaneSurfaceDrawer>();
        if (!drawerHost && planeDrawer)
        {
            drawerHost = planeDrawer.gameObject;
        }
        ResolveLevelManager();
        CacheWaits();
        SetupVisionPayloads();

        if (hintRenderer)
        {
            hintRenderer.enabled = false;
        }

        _gradeUnlockAt = float.NegativeInfinity;
        SetDrawerEnabled(false);
    }

    private void OnEnable()
    {
        HookLevelManager();
    }

    private void Start()
    {
        LoadPlan();
        if (autostart)
        {
            AdvanceLetter();
        }
    }

    private void OnDisable()
    {
        UnhookLevelManager();
    }

    private void OnDestroy()
    {
        UnhookLevelManager();
        ReleaseCaptureTargets();
        HideHintImmediate();
    }

    private void HandleGameModeChanged(LevelManager.GameMode mode)
    {
        // Prompt visibility is managed by PhonemeManager.
    }

    private void SetPromptVisible(bool visible)
    {
        // Legacy method retained for compatibility; actual visibility is managed by PhonemeManager.
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        captureResolution = Mathf.Clamp(captureResolution, 128, 2048);
        if (!Application.isPlaying) return;
        CacheWaits();
    }
#endif

    public void RestartSequence()
    {
        _currentLetterIndex = -1;
        AdvanceLetter();
    }

    public void RequestGrade()
    {
        Debug.Log($"[VisualDrill] RequestGrade invoked. CanGrade={CanGrade}, state={_state}, unlockIn={Mathf.Max(0f, _gradeUnlockAt - Time.time):F2}s");
        if (!CanGrade)
        {
            Debug.Log("[VisualDrill] Grade ignored because CanGrade is false (still locked or not drawing).");
            return;
        }

        if (_gradeRoutine != null)
        {
            Debug.LogWarning("[VisualDrill] Grade requested while a grade coroutine is already running.");
            return;
        }

        _gradeRoutine = StartCoroutine(GradeCurrentLetter());
    }

    public void RequestErase()
    {
        if (_state != DrillState.WaitingForDraw)
        {
            Debug.Log("[VisualDrill] RequestErase ignored because state is " + _state);
            return;
        }

        Debug.Log("[VisualDrill] Board erased by request.");
        planeDrawer?.ClearStrokes();
        HideHintImmediate();
        SetFeedback(string.Empty);
        ScheduleGradeUnlock();
    }

    private void LoadPlan()
    {
        string json = null;

        if (levelPlanOverride && !string.IsNullOrWhiteSpace(levelPlanOverride.text))
        {
            json = levelPlanOverride.text;
        }
        else
        {
            var asset = Resources.Load<TextAsset>("LevelPlan");
            if (asset && !string.IsNullOrWhiteSpace(asset.text))
            {
                json = asset.text;
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            Debug.LogWarning("[VisualDrill] No LevelPlan JSON found in Resources.");
            _letters = Array.Empty<string>();
            return;
        }

        try
        {
            var wrapper = JsonUtility.FromJson<LevelPlanWrapper>(json);
            if (wrapper?.levelplan == null || wrapper.levelplan.Length == 0)
            {
                _letters = Array.Empty<string>();
                return;
            }

            var list = new List<string>(wrapper.levelplan.Length * 4);
            foreach (var phase in wrapper.levelplan)
            {
                if (phase?.Letters == null) continue;

                bool includePhase = true;
                if (phase.Modes != null && phase.Modes.Length > 0)
                {
                    includePhase = false;
                    for (int i = 0; i < phase.Modes.Length; i++)
                    {
                        if (string.Equals(phase.Modes[i], "Dictation", StringComparison.OrdinalIgnoreCase))
                        {
                            includePhase = true;
                            break;
                        }
                    }
                }

                if (!includePhase) continue;

                foreach (var letter in phase.Letters)
                {
                    if (string.IsNullOrWhiteSpace(letter)) continue;
                    list.Add(letter.Trim());
                }
            }

            _letters = list.Count > 0 ? list.ToArray() : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            Debug.LogError("[VisualDrill] Failed to parse LevelPlan: " + ex.Message);
            _letters = Array.Empty<string>();
        }
    }
    private void AdvanceLetter()
    {
        ReturnToBaseline();

        if (_letters == null || _letters.Length == 0)
        {
            _state = DrillState.Inactive;
            SetFeedback(string.Empty);
            if (promptText) promptText.text = string.Empty;
            return;
        }

        _currentLetterIndex++;
        if (_currentLetterIndex >= _letters.Length)
        {
            if (loopPlan)
            {
                _currentLetterIndex = 0;
            }
            else
            {
                _state = DrillState.Inactive;
                SetDrawerEnabled(false);
                SetFeedback(string.Empty);
                if (promptText) promptText.text = string.Empty;
                return;
            }
        }

        _currentExpected = _letters[_currentLetterIndex];
        _attempt = 0;
        _lastRecognized = string.Empty;

        if (promptText) promptText.text = _currentExpected.ToUpperInvariant();
        SetFeedback(string.Empty);
        HideHintImmediate();
        planeDrawer?.ClearStrokes();
        SetDrawerEnabled(true);

        _state = DrillState.WaitingForDraw;
        ScheduleGradeUnlock();

        OnLetterStarted?.Invoke(_currentExpected);
    }

    private void ReturnToBaseline()
    {
        if (_gradeRoutine != null)
        {
            StopCoroutine(_gradeRoutine);
            _gradeRoutine = null;
        }

        if (_hintRoutine != null)
        {
            StopCoroutine(_hintRoutine);
            _hintRoutine = null;
        }

        _state = DrillState.Inactive;
        _gradeUnlockAt = Time.time;
        HideHintImmediate();
        planeDrawer?.ClearStrokes();
        SetDrawerEnabled(false);
    }

    private void ScheduleGradeUnlock()
    {
        gradeUnlockDelay = Mathf.Max(0f, gradeUnlockDelay);
        _gradeUnlockAt = Time.time + gradeUnlockDelay;
    }

    private IEnumerator GradeCurrentLetter()
    {
        _state = DrillState.Processing;
        SetDrawerEnabled(false);

        ApplyStrokeOverrides();

        Texture2D captured = null;
        yield return CaptureBoardExactCo(tex => captured = tex);

        RestoreStrokeOverrides();

        if (captured == null)
        {
            Debug.LogWarning("[VisualDrill] Board capture failed.");
            _gradeRoutine = null;
            CompleteLetter(false);
            yield break;
        }

        string recognized = null;
        yield return BeamRecognize(captured, s => recognized = s);

        _lastRecognized = recognized ?? string.Empty;
        string displayRaw = string.IsNullOrEmpty(_lastRecognized) ? "<none>" : _lastRecognized;
        string expectedDisplay = string.IsNullOrEmpty(_currentExpected) ? "<none>" : _currentExpected;
        Debug.Log($"[VisualDrill] Vision OCR returned '{displayRaw}' for expected '{expectedDisplay}'.");

        bool correct = EvaluateRecognition(_lastRecognized, _currentExpected);

        if (correct)
        {
            SetFeedback($"RIGHT: {displayRaw} (expected {expectedDisplay})");
            Debug.Log($"[VisualDrill] Letter '{expectedDisplay}' accepted.");
            planeDrawer?.ClearStrokes();
            yield return _waitAfterCorrectYield;
            _gradeRoutine = null;
            CompleteLetter(true);
            yield break;
        }

        SetFeedback($"WRONG: {displayRaw} (expected {expectedDisplay})");
        Debug.LogWarning($"[VisualDrill] Letter '{expectedDisplay}' incorrect on attempt {_attempt + 1} with OCR '{displayRaw}'.");
        _attempt++;
        planeDrawer?.ClearStrokes();

        if (_attempt <= 1)
        {
            ShowHintVariant(_attempt - 1);
            _state = DrillState.WaitingForDraw;
            SetDrawerEnabled(true);
            ScheduleGradeUnlock();
            _gradeRoutine = null;
            yield break;
        }

        yield return _waitAfterFailureYield;
        _gradeRoutine = null;
        CompleteLetter(false);
    }

    private void CompleteLetter(bool correct)
    {
        var finished = _currentExpected;
        Debug.Log($"[VisualDrill] Advancing from letter '{finished}' (correct={correct}).");
        OnLetterCompleted?.Invoke(finished, correct);
        AdvanceLetter();
    }

    private void CacheWaits()
    {
        gradeUnlockDelay = Mathf.Max(0f, gradeUnlockDelay);
        waitAfterCorrect = Mathf.Max(0f, waitAfterCorrect);
        waitAfterFailureAdvance = Mathf.Max(0f, waitAfterFailureAdvance);
        hintDisplaySeconds = Mathf.Max(0.1f, hintDisplaySeconds);

        _waitAfterCorrectYield = new WaitForSeconds(waitAfterCorrect);
        _waitAfterFailureYield = new WaitForSeconds(waitAfterFailureAdvance);
        _hintDurationWait = new WaitForSeconds(hintDisplaySeconds);
    }

    private void SetupVisionPayloads()
    {
        _visionPrimary = new VisionRequestWrapper
        {
            requests = new[]
            {
                new VisionRequest
                {
                    image = new VisionImage(),
                    features = new[] { new VisionFeature { type = "TEXT_DETECTION", maxResults = 5 } },
                    imageContext = new VisionImageContext()
                }
            }
        };

        _visionFallback = new VisionRequestWrapper
        {
            requests = new[]
            {
                new VisionRequest
                {
                    image = new VisionImage(),
                    features = new[] { new VisionFeature { type = "DOCUMENT_TEXT_DETECTION", maxResults = 1 } },
                    imageContext = new VisionImageContext()
                }
            }
        };
    }

    private void SetDrawerEnabled(bool enabled)
    {
        if (drawerHost && drawerHost != gameObject)
        {
            drawerHost.SetActive(enabled);
        }

        if (!planeDrawer) return;

        planeDrawer.SetLaserBeamsEnabled(enabled);
        planeDrawer.SetControllerSphereVisible(!enabled);
        planeDrawer.enabled = enabled;
    }

    private void SetFeedback(string message)
    {
        if (!feedbackText) return;
        feedbackText.text = message ?? string.Empty;
    }

    private void ShowHintVariant(int variantIndex)
    {
        if (!hintRenderer) return;
        var tex = GetHintTexture(_currentExpected, variantIndex);
        if (!tex)
        {
            HideHintImmediate();
            return;
        }

        if (_hintRoutine != null) StopCoroutine(_hintRoutine);
        _hintRoutine = StartCoroutine(HintRoutine(tex));
    }

    private IEnumerator HintRoutine(Texture2D tex)
    {
        ApplyHintTexture(tex);
        yield return _hintDurationWait;
        HideHintImmediate();
    }

    private void ApplyHintTexture(Texture tex)
    {
        if (!hintRenderer) return;
        if (_hintPropertyBlock == null) _hintPropertyBlock = new MaterialPropertyBlock();
        hintRenderer.GetPropertyBlock(_hintPropertyBlock);
        _hintPropertyBlock.SetTexture(ID_BaseMap, tex);
        _hintPropertyBlock.SetTexture(ID_MainTex, tex);
        hintRenderer.SetPropertyBlock(_hintPropertyBlock);
        hintRenderer.enabled = tex != null;
    }

    private void HideHintImmediate()
    {
        if (_hintRoutine != null)
        {
            StopCoroutine(_hintRoutine);
            _hintRoutine = null;
        }
        ApplyHintTexture(null);
    }

    private Texture2D GetHintTexture(string letter, int variantIndex)
    {
        if (string.IsNullOrWhiteSpace(letter)) return null;
        int safeVariant = Mathf.Clamp(variantIndex, 0, 1);
        string key = $"{letter}_{safeVariant}";
        if (_hintTextureCache.TryGetValue(key, out var cached)) return cached;

        string suffix = safeVariant == 0 ? "1" : "2";
        string resource = $"letterphotos/{letter.Trim().ToUpperInvariant()}{suffix}";
        var tex = Resources.Load<Texture2D>(resource);
        if (!tex) Debug.LogWarning("[VisualDrill] Missing hint texture: " + resource);
        _hintTextureCache[key] = tex;
        return tex;
    }
    private void ApplyStrokeOverrides()
    {
        _strokeOverrides.Clear();
        if (!planeDrawer) return;
        var root = planeDrawer.StrokesRoot;
        if (!root) return;

        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (!child) continue;
            var lr = child.GetComponent<LineRenderer>();
            if (!lr) continue;

            var state = new StrokeState
            {
                line = lr,
                width = lr.widthMultiplier,
                gradient = lr.colorGradient
            };
            _strokeOverrides.Add(state);
            lr.widthMultiplier = state.width * captureWidthMultiplier;
            lr.colorGradient = sBlackGradient;
        }
    }

    private void RestoreStrokeOverrides()
    {
        for (int i = 0; i < _strokeOverrides.Count; i++)
        {
            var state = _strokeOverrides[i];
            if (!state.line) continue;
            state.line.widthMultiplier = state.width;
            if (state.gradient != null) state.line.colorGradient = state.gradient;
        }
        _strokeOverrides.Clear();
    }

    private IEnumerator CaptureBoardExactCo(Action<Texture2D> done)
    {
        if (!boardCamera)
        {
            done?.Invoke(null);
            yield break;
        }

        if (boardRenderer)
        {
            FitOrthoToRenderer(boardCamera, boardRenderer, 1.02f);
        }

        int size = Mathf.Max(128, captureResolution);
        EnsureCaptureTargets(size);

        var prevTarget = boardCamera.targetTexture;
        var prevActive = RenderTexture.active;
        int prevMask = boardCamera.cullingMask;

        boardCamera.targetTexture = _captureRT;
        if (boardLayer.value != 0) boardCamera.cullingMask = boardLayer;

        yield return _endOfFrame;
        yield return _endOfFrame;

        boardCamera.Render();

        RenderTexture.active = _captureRT;
        _captureTex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
        _captureTex.Apply(false, false);

        if (debugWriteCapture)
        {
            WriteDebugCapture(_captureTex);
        }

        RenderTexture.active = prevActive;
        boardCamera.targetTexture = prevTarget;
        if (boardLayer.value != 0) boardCamera.cullingMask = prevMask;

        done?.Invoke(_captureTex);
    }

    private void EnsureCaptureTargets(int size)
    {
        if (_captureRT == null || _captureRT.width != size || _captureRT.height != size)
        {
            if (_captureRT != null)
            {
                _captureRT.Release();
                Destroy(_captureRT);
            }

            _captureRT = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                autoGenerateMips = false,
                useMipMap = false,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
        }

        if (_captureTex == null || _captureTex.width != size || _captureTex.height != size)
        {
            if (_captureTex != null) Destroy(_captureTex);
            _captureTex = new Texture2D(size, size, TextureFormat.RGB24, false, false);
        }
    }

    private void ReleaseCaptureTargets()
    {
        if (_captureRT)
        {
            _captureRT.Release();
            Destroy(_captureRT);
            _captureRT = null;
        }

        if (_captureTex)
        {
            Destroy(_captureTex);
            _captureTex = null;
        }
    }

    private void WriteDebugCapture(Texture2D tex)
    {
        if (!tex) return;
        try
        {
            var png = tex.EncodeToPNG();
            string path = Path.Combine(Application.persistentDataPath, $"visual_drill_{DateTime.UtcNow:HHmmssfff}.png");
            File.WriteAllBytes(path, png);
            Debug.Log("[VisualDrill] Wrote debug capture: " + path);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[VisualDrill] Debug capture failed: " + ex.Message);
        }
    }

    private void FitOrthoToRenderer(Camera cam, Renderer target, float padding)
    {
        if (!cam || !target) return;

        var bounds = target.bounds;
        Vector3 center = bounds.center;
        Vector3 ext = bounds.extents;
        Vector3[] corners = new Vector3[8];
        corners[0] = center + new Vector3(ext.x, ext.y, ext.z);
        corners[1] = center + new Vector3(ext.x, ext.y, -ext.z);
        corners[2] = center + new Vector3(ext.x, -ext.y, ext.z);
        corners[3] = center + new Vector3(ext.x, -ext.y, -ext.z);
        corners[4] = center + new Vector3(-ext.x, ext.y, ext.z);
        corners[5] = center + new Vector3(-ext.x, ext.y, -ext.z);
        corners[6] = center + new Vector3(-ext.x, -ext.y, ext.z);
        corners[7] = center + new Vector3(-ext.x, -ext.y, -ext.z);

        Matrix4x4 w2c = cam.worldToCameraMatrix;
        Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        float zmin = float.PositiveInfinity, zmax = float.NegativeInfinity;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 v = w2c.MultiplyPoint(corners[i]);
            if (v.x < min.x) min.x = v.x;
            if (v.y < min.y) min.y = v.y;
            if (v.x > max.x) max.x = v.x;
            if (v.y > max.y) max.y = v.y;
            if (v.z < zmin) zmin = v.z;
            if (v.z > zmax) zmax = v.z;
        }

        float height = (max.y - min.y) * padding;
        cam.orthographic = true;
        cam.orthographicSize = height * 0.5f;

        Vector3 camPosWS = cam.cameraToWorldMatrix.MultiplyPoint(new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, (zmin + zmax) * 0.5f));
        cam.transform.position = camPosWS;
    }
    private IEnumerator BeamRecognize(Texture2D snap, Action<string> onDone)
    {
        string token = null;
        bool ok = false;
        yield return GetAccessTokenCo(t => { token = t; ok = !string.IsNullOrEmpty(t); });

        if (!ok)
        {
            onDone?.Invoke(null);
            yield break;
        }

        byte[] png = snap.EncodeToPNG();
        string b64 = Convert.ToBase64String(png);
        var hints = (languageHints != null && languageHints.Length > 0) ? languageHints : sDefaultLanguageHints;

        _visionPrimary.requests[0].image.content = b64;
        _visionPrimary.requests[0].imageContext.languageHints = hints;
        string json = JsonUtility.ToJson(_visionPrimary);

        string parsed = null;
        yield return SendVisionRequest(json, token, s => parsed = s);

        if (string.IsNullOrWhiteSpace(parsed))
        {
            _visionFallback.requests[0].image.content = b64;
            _visionFallback.requests[0].imageContext.languageHints = hints;
            string json2 = JsonUtility.ToJson(_visionFallback);
            yield return SendVisionRequest(json2, token, s => parsed = s);
        }

        onDone?.Invoke(parsed);
    }

    private IEnumerator SendVisionRequest(string json, string token, Action<string> onDone)
    {
        using (var req = new UnityWebRequest(visionEndpoint, UnityWebRequest.kHttpVerbPOST))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {token}");
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 20;

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                onDone?.Invoke(ParseVisionText(req.downloadHandler.text));
            }
            else
            {
                Debug.LogWarning($"[VisualDrill] Vision error {req.responseCode}: {req.error}\n{req.downloadHandler.text}");
                onDone?.Invoke(null);
            }
        }
    }

    private string ParseVisionText(string resp)
    {
        if (string.IsNullOrEmpty(resp)) return null;

        try
        {
            var wrapper = JsonUtility.FromJson<VisionRespWrapper>(resp);
            if (wrapper?.responses == null || wrapper.responses.Length == 0) return null;
            var r = wrapper.responses[0];
            if (r == null) return null;

            if (r.textAnnotations != null && r.textAnnotations.Length > 0 && !string.IsNullOrWhiteSpace(r.textAnnotations[0].description))
                return r.textAnnotations[0].description.Trim();

            if (r.fullTextAnnotation != null && !string.IsNullOrWhiteSpace(r.fullTextAnnotation.text))
                return r.fullTextAnnotation.text.Trim();
        }
        catch
        {
            // fall back to string search
        }

        int idx = resp.IndexOf("\"description\"", StringComparison.Ordinal);
        if (idx >= 0)
        {
            int q1 = resp.IndexOf('"', idx + 13);
            int q2 = (q1 >= 0) ? resp.IndexOf('"', q1 + 1) : -1;
            if (q1 >= 0 && q2 > q1) return resp.Substring(q1 + 1, q2 - q1 - 1).Trim();
        }

        return null;
    }
    private IEnumerator GetAccessTokenCo(Action<string> onDone)
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrEmpty(_cachedAccessToken) && now < _tokenExpiry - 60)
        {
            onDone?.Invoke(_cachedAccessToken);
            yield break;
        }

        if (string.IsNullOrEmpty(serviceAccountJsonResource))
        {
            Debug.LogError("[VisualDrill] serviceAccountJsonResource is empty.");
            onDone?.Invoke(null);
            yield break;
        }

        TextAsset ta = Resources.Load<TextAsset>(serviceAccountJsonResource);
        if (!ta || string.IsNullOrEmpty(ta.text))
        {
            Debug.LogError("[VisualDrill] Could not load service account JSON.");
            onDone?.Invoke(null);
            yield break;
        }

        ServiceAccountJson sa = null;
        try
        {
            sa = JsonUtility.FromJson<ServiceAccountJson>(ta.text);
        }
        catch (Exception ex)
        {
            Debug.LogError("[VisualDrill] Invalid service account JSON: " + ex.Message);
            onDone?.Invoke(null);
            yield break;
        }

        if (sa == null || string.IsNullOrEmpty(sa.client_email) || string.IsNullOrEmpty(sa.private_key) || string.IsNullOrEmpty(sa.token_uri))
        {
            Debug.LogError("[VisualDrill] Missing fields in service account JSON.");
            onDone?.Invoke(null);
            yield break;
        }
        const string scope = "https://www.googleapis.com/auth/cloud-vision";
        long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long exp = iat + 3600;

        string headerJson = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
        string claimJson = "{\"iss\":\"" + sa.client_email + "\",\"scope\":\"" + scope + "\",\"aud\":\"" + sa.token_uri + "\",\"exp\":" + exp + ",\"iat\":" + iat + "}";

        string headerB64 = ToBase64Url(Encoding.UTF8.GetBytes(headerJson));
        string claimB64 = ToBase64Url(Encoding.UTF8.GetBytes(claimJson));
        string signingInput = headerB64 + "." + claimB64;

        byte[] signature;
        try
        {
            using (RSA rsa = PemKeyUtil.CreateRSAFromPem(sa.private_key))
            {
                signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[VisualDrill] RSA sign failed: " + ex.Message);
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
                Debug.LogError($"[VisualDrill] Token request failed {req.responseCode}: {req.error}\n{req.downloadHandler.text}");
                onDone?.Invoke(null);
                yield break;
            }

            TokenResp tok = null;
            try
            {
                tok = JsonUtility.FromJson<TokenResp>(req.downloadHandler.text);
            }
            catch
            {
                // ignore
            }

            if (tok == null || string.IsNullOrEmpty(tok.access_token))
            {
                Debug.LogError("[VisualDrill] Token parse failed: " + req.downloadHandler.text);
                onDone?.Invoke(null);
                yield break;
            }

            _cachedAccessToken = tok.access_token;
            _tokenExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Math.Max(60, tok.expires_in);
            onDone?.Invoke(_cachedAccessToken);
        }
    }
    private static string ToBase64Url(byte[] input)
    {
        string s = Convert.ToBase64String(input);
        s = s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return s;
    }

    private bool EvaluateRecognition(string recognized, string expected)
    {
        if (string.IsNullOrEmpty(expected)) return false;
        string normExpected = NormalizeAsciiStrict(expected);
        string normRecognized = NormalizeAsciiStrict(recognized);
        if (string.IsNullOrEmpty(normRecognized) || string.IsNullOrEmpty(normExpected)) return false;
        return IsEquivalentChar(normExpected[0], normRecognized[0]);
    }

    private static readonly Dictionary<char, char> ConfusableToAscii = new Dictionary<char, char>
    {
        ['\u0430'] = 'a', ['\u0435'] = 'e', ['\u043E'] = 'o', ['\u0440'] = 'p', ['\u0441'] = 'c',
        ['\u0443'] = 'y', ['\u0445'] = 'x', ['\u043A'] = 'k', ['\u043C'] = 'm', ['\u0442'] = 't',
        ['\u0432'] = 'b', ['\u043D'] = 'h', ['\u0438'] = 'u', ['\u0456'] = 'i',
        ['\u0410'] = 'a', ['\u0412'] = 'b', ['\u0415'] = 'e', ['\u041A'] = 'k', ['\u041C'] = 'm',
        ['\u041D'] = 'h', ['\u041E'] = 'o', ['\u0420'] = 'p', ['\u0421'] = 'c', ['\u0422'] = 't',
        ['\u0423'] = 'y', ['\u0425'] = 'x', ['\u0406'] = 'i',
        ['\u03B1'] = 'a', ['\u03B5'] = 'e', ['\u03BF'] = 'o', ['\u03C1'] = 'p', ['\u03BD'] = 'v',
        ['\u03BC'] = 'm', ['\u03B9'] = 'i', ['\u03BA'] = 'k', ['\u03C7'] = 'x', ['\u03C5'] = 'y', ['\u03C4'] = 't',
        ['\u0391'] = 'a', ['\u0392'] = 'b', ['\u0395'] = 'e', ['\u0397'] = 'h', ['\u0399'] = 'i', ['\u039A'] = 'k',
        ['\u039C'] = 'm', ['\u039D'] = 'n', ['\u039F'] = 'o', ['\u03A1'] = 'p', ['\u03A4'] = 't', ['\u03A5'] = 'y',
        ['\u03A7'] = 'x', ['\u039B'] = 'l'
    };
    private static string NormalizeAsciiStrict(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Trim();
        var norm = s.Normalize(NormalizationForm.FormKD);
        foreach (var ch in norm)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark || cat == UnicodeCategory.EnclosingMark)
                continue;

            char c = char.ToLowerInvariant(ch);
            if (c >= 'a' && c <= 'z') return c.ToString();
            if (ConfusableToAscii.TryGetValue(c, out var mapped)) return mapped.ToString();
            if (c == '0') return "o";
            if (c == '1' || c == '|' || c == '!') return "l";
        }
        return string.Empty;
    }

    private static bool IsEquivalentChar(char expected, char got)
    {
        if (expected == got) return true;
        if (expected == 'o' && got == '0') return true;
        if ((expected == 'i' || expected == 'l') && (got == 'i' || got == 'l' || got == '1' || got == '|' || got == '!')) return true;
        return false;
    }

    private static Gradient BuildSolidGradient(Color color)
    {
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
            new[] { new GradientAlphaKey(color.a, 0f), new GradientAlphaKey(color.a, 1f) }
        );
        return grad;
    }

    private static class PemKeyUtil
    {
        public static RSA CreateRSAFromPem(string pem)
        {
            if (string.IsNullOrEmpty(pem)) throw new ArgumentNullException(nameof(pem));
            pem = NormalizePem(pem);

            if (pem.Contains("-----BEGIN RSA PRIVATE KEY-----"))
            {
                byte[] der = DecodePem(pem, "RSA PRIVATE KEY");
                RSAParameters p = ParsePkcs1PrivateKey(der);
                var rsa = RSA.Create();
                rsa.ImportParameters(p);
                return rsa;
            }

            if (pem.Contains("-----BEGIN PRIVATE KEY-----"))
            {
                byte[] der = DecodePem(pem, "PRIVATE KEY");
                byte[] inner = ExtractPkcs8PrivateKeyOctet(der);
                RSAParameters p = ParsePkcs1PrivateKey(inner);
                var rsa = RSA.Create();
                rsa.ImportParameters(p);
                return rsa;
            }

            throw new Exception("Unsupported key format");
        }

        private static string NormalizePem(string pem)
        {
            return pem.Replace("\n", "\n").Replace("\r", string.Empty).Trim()
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
            string b64 = pem.Substring(start, end - start).Replace("\n", string.Empty).Replace("\t", string.Empty).Replace(" ", string.Empty);
            return Convert.FromBase64String(b64);
        }

        private static int ReadLen(byte[] der, ref int ofs)
        {
            if (ofs >= der.Length) throw new IndexOutOfRangeException("DER len read");
            int len = der[ofs++];
            if ((len & 0x80) == 0) return len;
            int bytes = len & 0x7F;
            if (bytes < 1 || bytes > 4 || ofs + bytes > der.Length) throw new Exception("Invalid DER length");
            int v = 0;
            for (int i = 0; i < bytes; i++) v = (v << 8) | der[ofs++];
            return v;
        }

        private static int ReadSeqHeader(byte[] der, ref int ofs)
        {
            if (ofs >= der.Length) throw new IndexOutOfRangeException("DER tag read");
            byte tag = der[ofs++];
            if (tag != 0x30) throw new Exception("ASN.1: SEQUENCE expected");
            int len = ReadLen(der, ref ofs);
            if (ofs + len > der.Length) throw new IndexOutOfRangeException("DER seq overflow");
            return len;
        }

        private static byte[] ReadBlock(byte[] der, ref int ofs, byte expectTag)
        {
            if (ofs >= der.Length) throw new IndexOutOfRangeException("DER tag read");
            byte tag = der[ofs++];
            if (tag != expectTag) throw new Exception($"ASN.1 tag {expectTag:X2} expected, got {tag:X2}");
            int len = ReadLen(der, ref ofs);
            if (ofs + len > der.Length) throw new IndexOutOfRangeException("DER block overflow");
            var val = new byte[len];
            Buffer.BlockCopy(der, ofs, val, 0, len);
            ofs += len;
            return val;
        }

        private static byte[] ReadIntegerBytes(byte[] der, ref int ofs)
        {
            byte[] v = ReadBlock(der, ref ofs, 0x02);
            if (v.Length > 1 && v[0] == 0x00)
            {
                var t = new byte[v.Length - 1];
                Buffer.BlockCopy(v, 1, t, 0, t.Length);
                v = t;
            }
            return v;
        }

        private static byte[] ExtractPkcs8PrivateKeyOctet(byte[] der)
        {
            int ofs = 0;
            int seqLen = ReadSeqHeader(der, ref ofs);
            int end = ofs + seqLen;

            ReadIntegerBytes(der, ref ofs);
            ReadBlock(der, ref ofs, 0x30);
            byte[] oct = ReadBlock(der, ref ofs, 0x04);
            if (ofs > end) throw new Exception("PKCS#8 parse overflow");
            return oct;
        }

        private static RSAParameters ParsePkcs1PrivateKey(byte[] der)
        {
            int ofs = 0;
            int seqLen = ReadSeqHeader(der, ref ofs);
            int end = ofs + seqLen;

            ReadIntegerBytes(der, ref ofs);
            byte[] n = ReadIntegerBytes(der, ref ofs);
            byte[] e = ReadIntegerBytes(der, ref ofs);
            byte[] d = ReadIntegerBytes(der, ref ofs);
            byte[] p = ReadIntegerBytes(der, ref ofs);
            byte[] q = ReadIntegerBytes(der, ref ofs);
            byte[] dp = ReadIntegerBytes(der, ref ofs);
            byte[] dq = ReadIntegerBytes(der, ref ofs);
            byte[] iq = ReadIntegerBytes(der, ref ofs);

            if (ofs > end) throw new Exception("PKCS#1 parse overflow");
            return new RSAParameters { Modulus = n, Exponent = e, D = d, P = p, Q = q, DP = dp, DQ = dq, InverseQ = iq };
        }
    }
}
