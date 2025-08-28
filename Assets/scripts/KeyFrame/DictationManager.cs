using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Text;
using TMPro;

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
    [SerializeField] private GameObject eraseButtonGO;          // NEW
    [SerializeField] private TMP_Text   feedbackText;           // NEW
    [SerializeField] private float waitBeforeRef = 0.75f;       // before replay (wrong)
    [SerializeField] private float waitAfterReplay = 0.5f;      // NEW: after replay, before advance
    [SerializeField] private float waitAfterCorrect = 0.5f;     // NEW: after correct, before advance

    [Header("Board Screenshot (for OCR)")]
    [SerializeField] private Camera boardCamera;
    [SerializeField] private int captureWidth = 512;
    [SerializeField] private int captureHeight = 512;

    [Header("Beam OCR")]
    [SerializeField] private BeamMode beamMode = BeamMode.BeamOn;
    [SerializeField] private string beamUrl = "https://recognize-handwriting-56bf23f-v4.app.beam.cloud";
    [SerializeField] private string beamBearerToken = "m1DC_VrjUplOzgiTbAexPIvvKR25tT9LeXRb8avJ46M-2FzVeMiApL3yJ02Gp6UWFP9RWZKT2ThBw3zzLcOR5A==";
    [SerializeField, Range(1, 8)] private int beamLengthHint = 1; // 1-char output

    [Header("Writing-line marker objects")]
    public Transform skyLine, planeLine, groundLine;

    [Header("Pass/Fail")]
    [Range(0f, 1f)] public float passRate = 1f;

    [Header("Drawer Host")]
    [Tooltip("GO holding PlaneSurfaceDrawer/HandPlaneConstraint. Enabled only in Dictation mode.")]
    [SerializeField] private GameObject drawerHost;
    [SerializeField] private GameObject tmpLetter;
    private CanvasManager  canvas;
    private SimpleRecorder rec;
    private LevelManager   lvl;
    private SaveManager    saver;
    private ProximityButton gradingBtn;
    private ProximityButton eraseBtn; // NEW

    private enum State { Idle, Drawing, WaitingForResponse, GradedAccept, GradedReject } // NEW WaitingForResponse
    private State state = State.Idle;

    private bool isReplaying = false;
    private bool _lastDrawerActive = true;

    private LevelManager.GameMode _lastNotifiedMode = LevelManager.GameMode.PhonemeChecking;
    private bool _hasLastMode = false;

    void Awake()
    {
        canvas = GetComponent<CanvasManager>();
        rec    = GetComponent<SimpleRecorder>();
        lvl    = GetComponent<LevelManager>();
        saver  = GetComponent<SaveManager>();

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
    }

    void Start()
    {
        if (beamMode == BeamMode.BeamOn)
            StartCoroutine(WarmupBeam());
    }

    void OnEnable()
    {
        if (gradingBtn) gradingBtn.OnButtonPressed += HandleGradeBtn;
        if (eraseBtn)   eraseBtn.OnButtonPressed   += HandleEraseBtn;
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

        // clear when leaving Dictation
        if (_hasLastMode && _lastNotifiedMode == LevelManager.GameMode.Dictation && mode != LevelManager.GameMode.Dictation)
            ClearBoardVisuals();

        _lastNotifiedMode = mode;
        _hasLastMode = true;

        UpdateUI();
        if (!isDict) SetFeedback(""); // hide text outside dictation
    }

    public void StartDictation()
    {
        if (!lvl) return;

        ClearBoardVisuals();
        if (rec != null && rec.currentRecord != null && rec.currentRecord.frames != null)
            rec.currentRecord.frames.Clear();
        if (rec != null) rec.IsRecording = false;

        // Disable tmpLetter when dictation starts
        if (tmpLetter != null) tmpLetter.SetActive(false);

        state = State.Drawing;
        UpdateUI();
        SetFeedback(lvl != null && !string.IsNullOrEmpty(lvl.currentLetter)
            ? $"Write the letter '{lvl.currentLetter}'"
            : "Write the letter");
        OnDictationStart?.Invoke();
    }

    // ===== Buttons =====
    private void HandleGradeBtn()
    {
        if (state == State.Drawing)
            StartCoroutine(GradeFlow()); // single press → full flow
    }

    private void HandleEraseBtn()
    {
        if (lvl != null && lvl.currentMode == LevelManager.GameMode.Dictation)
        {
            ClearBoardVisuals();
            SetFeedback("Board cleared");
        }
    }

    // ===== Flow =====
    private IEnumerator GradeFlow()
    {
        if (rec != null) rec.IsRecording = false;
        if (gradingButtonGO) gradingButtonGO.SetActive(false); // prevent double taps
        if (eraseButtonGO)   eraseButtonGO.SetActive(false);

        bool correct;

        if (beamMode == BeamMode.BeamOn)
        {
            Texture2D snap = CaptureBoardTexture();
            if (snap == null)
            {
                Debug.LogWarning("[Dictation] Board camera missing or capture failed.");
                YieldFailImmediate();
                SetFeedback("Capture failed");
                yield return new WaitForSeconds(waitAfterReplay);
                Advance();
                yield break;
            }

            state = State.WaitingForResponse;
            UpdateUI();
            SetFeedback("Grading… (one sec)");

            string ocrText = null;
            yield return StartCoroutine(BeamRecognize(snap, t => ocrText = t));
            Destroy(snap);

            string got = (ocrText ?? "").Trim().ToLowerInvariant();
            string expected = (lvl != null ? (lvl.currentLetter ?? "").Trim().ToLowerInvariant() : "");

            if (beamLengthHint > 0 && got.Length >= beamLengthHint)
                got = got.Substring(0, beamLengthHint);

            correct = (!string.IsNullOrEmpty(got) && got == expected);
        }
        else
        {
            state = State.WaitingForResponse;
            UpdateUI();
            SetFeedback("Checking…");
            yield return null;
            correct = (beamMode == BeamMode.MarkAnswersCorrect);
        }

        state = correct ? State.GradedAccept : State.GradedReject;
        UpdateUI();

        if (correct)
        {
            OnDictationGraded?.Invoke(100f);
            OnLetterCorrect?.Invoke();
            SetFeedback("Correct ✅");
            ClearBoardVisuals();
            yield return new WaitForSeconds(waitAfterCorrect);
            Advance(); // auto-advance
        }
        else
        {
            OnDictationGraded?.Invoke(0f);
            OnLetterIncorrect?.Invoke();
            SetFeedback("Almost. Watch the demo…");
            ClearBoardVisuals();
            yield return new WaitForSeconds(waitBeforeRef);
            yield return ReplayReference();
            SetFeedback("Your turn next →");
            yield return new WaitForSeconds(waitAfterReplay);
            Advance(); // auto-advance after replay
        }
    }

    private void Advance()
    {
        state = State.Idle;
        UpdateUI();
        SetFeedback("");
        
        // Re-enable tmpLetter when dictation ends
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
                    child.name.StartsWith("Line",   StringComparison.OrdinalIgnoreCase))
                {
                    Destroy(child.gameObject);
                }
            }
        }
    }

    // ===== Replay =====
    private IEnumerator ReplayReference()
    {
        isReplaying = true;

        if (rec != null && lvl != null)
            rec.LoadRecording(lvl.currentLetter);

        yield return null;

        if (canvas != null && canvas.activeSpheres != null)
        {
            foreach (var s in canvas.activeSpheres) s.SetActive(false);

            float replayTotal = 1.25f;
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
            }
        }

        isReplaying = false;
    }

    private void YieldFailImmediate()
    {
        state = State.GradedReject;
        UpdateUI();
        OnDictationGraded?.Invoke(0f);
        OnLetterIncorrect?.Invoke();
    }

    // ===== Capture & Beam =====
    private Texture2D CaptureBoardTexture()
    {
        if (boardCamera == null) return null;

        var rt = new RenderTexture(captureWidth, captureHeight, 16, RenderTextureFormat.ARGB32);
        var prev = boardCamera.targetTexture;
        boardCamera.targetTexture = rt;
        boardCamera.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(captureWidth, captureHeight, TextureFormat.RGBA32, false, false);
        tex.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0, false);
        tex.Apply();

        boardCamera.targetTexture = prev;
        RenderTexture.active = null;
        rt.Release();
        Destroy(rt);

        return tex;
    }

    [Serializable] private struct BeamPayload { public string image_b64; public int length; }
    [Serializable] private class BeamRespLoose
    {
        public string text;
        public string[] texts;
        [Serializable] public class Prediction { public string text; }
        public Prediction[] predictions;
    }

    private IEnumerator WarmupBeam()
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, new Color(0, 0, 0, 0));
        tex.Apply();

        yield return StartCoroutine(BeamRecognize(tex, _ => { }));
        Destroy(tex);
    }

    private IEnumerator BeamRecognize(Texture2D snap, Action<string> onDone)
    {
        byte[] png = snap.EncodeToPNG();
        string b64 = Convert.ToBase64String(png);
        var payload = new BeamPayload { image_b64 = b64, length = Mathf.Max(1, beamLengthHint) };
        string json = JsonUtility.ToJson(payload);

        using (var req = new UnityWebRequest(beamUrl, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Authorization", $"Bearer {beamBearerToken}");
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = 20;

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[DictationManager] Beam error {req.responseCode}: {req.error}\n{req.downloadHandler.text}");
                onDone?.Invoke(null);
                yield break;
            }

            string parsed = ParseBeamText(req.downloadHandler.text);
            if (parsed == null) parsed = req.downloadHandler.text;
            onDone?.Invoke(parsed);
        }
    }

    private string ParseBeamText(string resp)
    {
        try
        {
            var obj = JsonUtility.FromJson<BeamRespLoose>(resp);
            if (obj != null)
            {
                if (!string.IsNullOrEmpty(obj.text)) return obj.text;
                if (obj.texts != null && obj.texts.Length > 0) return obj.texts[0];
                if (obj.predictions != null && obj.predictions.Length > 0 &&
                    !string.IsNullOrEmpty(obj.predictions[0].text))
                    return obj.predictions[0].text;
            }
        }
        catch { }
        return null;
    }
}
