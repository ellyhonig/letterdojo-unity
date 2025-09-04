using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using System.Security.Cryptography;
using System.Globalization;
using System.Linq;

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
    [SerializeField] private TMP_Text   feedbackText;
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

    [Header("Guide Lines (optional visuals)")]
    public Transform skyLine, planeLine, groundLine;

    [Header("Pass/Fail")]
    [Range(0f, 1f)] public float passRate = 1f;

    [Header("Drawer Host (strokes in dictation)")]
    [SerializeField] private GameObject drawerHost;

    [SerializeField] private GameObject tmpLetter;

    private CanvasManager  canvas;
    private SimpleRecorder rec;
    private LevelManager   lvl;
    private SaveManager    saver;
    private ProximityButton gradingBtn;
    private ProximityButton eraseBtn;
    private AudioManager audioManager;
    private int attemptCount = 0; // two tries policy
    private readonly List<GameObject> replayDots = new List<GameObject>();

    private enum State { Idle, Drawing, WaitingForResponse, GradedAccept, GradedReject }
    private State state = State.Idle;

    private bool _lastDrawerActive = true;
    private LevelManager.GameMode _lastNotifiedMode = LevelManager.GameMode.PhonemeChecking;
    private bool _hasLastMode = false;

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
        audioManager = FindObjectOfType<AudioManager>();

        if (drawerHost == null)
        {
            var drawer = GetComponent<PlaneSurfaceDrawer>();
            if (drawer != null) drawerHost = drawer.gameObject;
            else
            {
                var childDrawer = GetComponentInChildren<PlaneSurfaceDrawer>(true);
                if (childDrawer != null) drawerHost = childDrawer.gameObject;
            }
        }

        if (gradingButtonGO)
        {
            gradingBtn = gradingButtonGO.GetComponent<ProximityButton>();
            gradingButtonGO.SetActive(false);
        }
        if (eraseButtonGO)
        {
            eraseBtn = eraseButtonGO.GetComponent<ProximityButton>();
            eraseButtonGO.SetActive(false);
        }

        HardConfigureCaptureCamera();
    }

    void Start()
    {
        if (beamMode == BeamMode.BeamOn)
            StartCoroutine(WarmupBeam()); // primes token + HTTP path
    }

    void OnEnable()
    {
        if (gradingBtn) gradingBtn.OnButtonPressed += HandleGradeBtn;
        if (eraseBtn)   eraseBtn.OnButtonPressed   += HandleEraseBtn;
          ClearBoardVisuals();
    }
    void OnDisable()
    {
        if (gradingBtn) gradingBtn.OnButtonPressed -= HandleGradeBtn;
        if (eraseBtn)   eraseBtn.OnButtonPressed   -= HandleEraseBtn;
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

        UpdateUI();
        if (!isDict) SetFeedback("");
    }

    public void StartDictation()
    {
        if (!lvl) return;

        attemptCount = 0; // reset tries
        ClearBoardVisuals();

        if (rec != null)
        {
            rec.IsRecording = false; // not using stroke capture here
            if (rec.currentRecord != null && rec.currentRecord.frames != null)
                rec.currentRecord.frames.Clear();
        }

        if (tmpLetter != null) tmpLetter.SetActive(false);

        state = State.Drawing;
        UpdateUI();
        SetFeedback(!string.IsNullOrEmpty(lvl.currentLetter) ? $"Write '{lvl.currentLetter}'" : "Write the letter");
        OnDictationStart?.Invoke();
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

    // ===== Flow =====
    private IEnumerator GradeFlow()
    {
        if (gradingButtonGO) gradingButtonGO.SetActive(false);
        if (eraseButtonGO)   eraseButtonGO.SetActive(false);

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
        SetFeedback("Grading…");

        string ocrText = null;
        yield return StartCoroutine(BeamRecognize(snap, t => ocrText = t)); // now Vision-backed
        Destroy(snap);

        string gotRaw = (ocrText ?? "");
        DebugCodepoint("[Vision] RAW", gotRaw);

        string got = NormalizeAsciiStrict(gotRaw);
        string expected = NormalizeAsciiStrict(lvl != null ? lvl.currentLetter : null);

        // if you still want to enforce single-char, this already returns 0–1 char
        bool correct = (!string.IsNullOrEmpty(got) && got == expected);

        Debug.Log($"[Vision] NORMALIZED got='{got}' expected='{expected}'");

        state = correct ? State.GradedAccept : State.GradedReject;
        UpdateUI();

        if (correct)
        {
            OnDictationGraded?.Invoke(100f);
            OnLetterCorrect?.Invoke();
            SetFeedback($"Correct ✅ (saw: '{gotRaw}')");
            ClearBoardVisuals();
            yield return new WaitForSeconds(waitAfterCorrect);
            Advance();
        }
        else
        {
            OnDictationGraded?.Invoke(0f);
            OnLetterIncorrect?.Invoke();
            if (attemptCount == 0)
            {
                // First mistake: show image hint (same as phoneme manager)
                var hint = FindObjectOfType<Letter3DDisplay>();
                if (hint != null) hint.ShowHintNow();
                SetFeedback($"Hint shown. Try '{lvl?.currentLetter}' again.");
                attemptCount = 1;
                state = State.Drawing;
                UpdateUI();
                yield break; // give user another try
            }
            else
            {
                // Second mistake: replay with stroke filling + pops, then move on
                SetFeedback($"Watch the demo of '{lvl?.currentLetter}'…");
                ClearBoardVisuals();
                yield return new WaitForSeconds(waitBeforeRef);
                yield return ReplayReference(); // revert to classic sphere replay
                SetFeedback("Your turn →");
                yield return new WaitForSeconds(waitAfterReplay);
                attemptCount = 2;
                Advance();
            }
        }
    }

    private void Advance()
    {
        state = State.Idle;
        UpdateUI();
        SetFeedback("");

        if (tmpLetter != null) tmpLetter.SetActive(true);

        OnDictationComplete?.Invoke();
    }

    // ===== UI helpers =====
    private void UpdateUI()
    {
        bool inDict = (lvl != null && lvl.currentMode == LevelManager.GameMode.Dictation);
        bool canDraw = (state == State.Drawing);

        if (gradingButtonGO) gradingButtonGO.SetActive(inDict && canDraw);
        if (eraseButtonGO)   eraseButtonGO.SetActive(inDict && canDraw);
        if (feedbackText)    feedbackText.gameObject.SetActive(inDict);
    }

    private void SetFeedback(string msg)
    {
        if (feedbackText) feedbackText.text = msg ?? "";
    }

    // ===== Board ops =====
    private void ClearBoardVisuals()
    {
        if (canvas != null) canvas.ClearVisualization();

        // Also clear VisualEffectManager visuals (spheres, cylinders, etc.)
        var visualEffectManager = GetComponent<VisualEffectManager>();
        if (visualEffectManager != null)
        {
            // Use reflection to call ClearAllVisuals since it's private
            var clearMethod = typeof(VisualEffectManager).GetMethod("ClearAllVisuals", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            clearMethod?.Invoke(visualEffectManager, null);
        }

        // AGGRESSIVE CLEARING: Find and destroy ONLY visualization sphere objects
        var allSpheres = FindObjectsOfType<GameObject>().Where(go => 
            (go.name.StartsWith("keypoint_") || 
             go.name.StartsWith("TraceSegment") ||
             go.name.StartsWith("ReplayDot_")) &&
            !go.GetComponent<TMPro.TextMeshPro>() && // Don't destroy TMP objects
            !go.GetComponent<TMPro.TextMeshProUGUI>() && // Don't destroy TMP objects
            !go.GetComponent<TextMesh>() && // Don't destroy regular TextMesh
            go.GetComponent<Renderer>() != null); // Only objects with renderers
        
        foreach (var sphere in allSpheres)
        {
            if (sphere != null && sphere.activeInHierarchy)
            {
                Debug.Log($"[Dictation] Force destroying visualization: {sphere.name}");
                Destroy(sphere);
            }
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
                if (!audioManager) audioManager = FindObjectOfType<AudioManager>();
                if (audioManager) audioManager.PlayPop();
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

        Transform root = null;
        if (canvas.activeSpheres.Count > 0 && canvas.activeSpheres[0])
            root = canvas.activeSpheres[0].transform.parent;

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

            if (!audioManager) audioManager = FindObjectOfType<AudioManager>();
            if (audioManager) audioManager.PlayPop();

            // draw segment to previous
            if (prev && root)
            {
                CreateReplaySegment(prev.transform.position, cur.transform.position, root);
            }
            prev = cur;
        }
    }

    private void CreateReplaySegment(Vector3 a, Vector3 b, Transform parent)
    {
        float dist = Vector3.Distance(a, b);
        if (dist <= 1e-6f) return;
        GameObject cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cyl.name = "TraceSegment";
        cyl.transform.SetParent(parent, true);
        var col = cyl.GetComponent<Collider>(); if (col) Destroy(col);

        // material
        var mr = cyl.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.black);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.black);
            mr.material = mat;
        }

        // align between a and b
        cyl.transform.position = (a + b) * 0.5f;
        cyl.transform.up = (b - a).normalized;
        float radius = (canvas != null ? Mathf.Max(0.0015f, canvas.sphereRadius * 1.8f) : 0.003f); // thin stroke scaled
        cyl.transform.localScale = new Vector3(radius, dist * 0.5f, radius); // height = 2*y
    }

    private void CreateReplayDot(Vector3 worldPos)
    {
        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dot.name = "ReplayDot_" + replayDots.Count;
        dot.transform.position = worldPos;
        dot.transform.localScale = Vector3.one * Mathf.Max(0.0015f, canvas != null ? canvas.sphereRadius * 2.5f : 0.01f);
        Destroy(dot.GetComponent<Collider>());
        var r = dot.GetComponent<Renderer>();
        if (r != null)
        {
            var mat = r.material;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.black);
            else if (mat.HasProperty("_Color")) mat.color = Color.black;
        }
        // Parent under drawerHost if available
        if (drawerHost) dot.transform.SetParent(drawerHost.transform, true);
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

          
// parsed text
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
        return pem.Replace("\\n", "\n").Replace("\r", "").Trim()
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
