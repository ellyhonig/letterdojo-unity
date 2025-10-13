using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection;

public class VisualEffectManager : MonoBehaviour, IMemoryBudgetConsumer
{
    [SerializeField] private LetterTracingSystem tracingSystem;
    [SerializeField] private SimpleRecorder recorder;
    [SerializeField] private CanvasManager canvasManager;
    [SerializeField] private LevelManager levelManager;
    [SerializeField] private bool legacyMode = false;

    [Header("Parenting")]
    [Tooltip("Parent for visuals (use the board/canvas root that holds the spheres). If null, auto = activeSpheres[0].parent or canvasManager.transform.")]
    [SerializeField] private Transform visualsRoot;

    [Header("Sphere visuals")]
    [SerializeField] private Color hitColor = Color.green;
    [SerializeField] private Color currentColor = Color.red;
    [SerializeField] private Color unhitColor = new Color(1f, 1f, 1f, 0.5f);
    [SerializeField] private float normalScale = 1f;
    [SerializeField] private float currentScale = 1.5f;
    [SerializeField] private float proximityScale = 0.8f;
    [SerializeField] private float proximityDistance = 0.08f;

    [Header("Indicators")]
    [SerializeField] private Color lineColor = Color.green;
    [SerializeField] private float lineWidth = 0.01f; // cylinder radius on X/Z
    [SerializeField] private Material dottedLineMaterial;
    [SerializeField] private float indicatorLineProximity = 0.15f;

    [Header("Modes")]
    [Tooltip("If true, draw all inter-dot cylinders up-front while in PhonemeChecking mode.")]
    [SerializeField] private bool showSegmentsInPhonemeChecking = true;

    private readonly List<GameObject> segmentCylinders = new List<GameObject>();
    private readonly Dictionary<GameObject, Vector3> originalScales = new Dictionary<GameObject, Vector3>();
    private readonly HashSet<int> oneRunCreatedForIndex = new HashSet<int>(); // (i-1)->i for this single trace run
    private readonly HashSet<string> segmentKeys = new HashSet<string>();     // persist until mode/letter change

    private GameObject lastHitSphere;
    private LineRenderer trackingLine;
    private LineRenderer voiceTrackingLine;

    private float maxAllowedDistance = float.MaxValue;
    private Transform cachedRoot;

    // change-detect caches
    private string _lastModeName = "";
    private string _lastDrillName = "";
    private int _lastSpheresSig = 0;
    private bool _dotsActive = true;
    private bool tracingCallbacksHooked;
    private bool recorderCallbacksHooked;
    private int lastLoggedHitIndex = -1;

    private void Awake()
    {
        if (!legacyMode)
        {
            enabled = false;
            return;
        }

        EnsureCoreReferences();
    }

    private Transform GetVisualsRoot()
    {
        if (visualsRoot) return visualsRoot;
        if (cachedRoot) return cachedRoot;

        if (canvasManager && canvasManager.activeSpheres != null &&
            canvasManager.activeSpheres.Count > 0 && canvasManager.activeSpheres[0])
        {
            cachedRoot = canvasManager.activeSpheres[0].transform.parent
                ? canvasManager.activeSpheres[0].transform.parent
                : canvasManager.transform;
        }
        else cachedRoot = canvasManager ? canvasManager.transform : transform;
        return cachedRoot;
    }

    private void EnsureCoreReferences()
    {
        if (!tracingSystem) tracingSystem = GetComponent<LetterTracingSystem>();
        if (!recorder)      recorder      = GetComponent<SimpleRecorder>();
        if (!canvasManager) canvasManager = GetComponent<CanvasManager>();
        if (!levelManager)  levelManager  = GetComponent<LevelManager>();
    }

    private void RegisterTracingCallbacks()
    {
        EnsureCoreReferences();
        if (!tracingSystem)
        {
            Debug.LogWarning("[VisualEffectManager] Missing LetterTracingSystem; tracing visuals disabled.", this);
            return;
        }

        if (tracingCallbacksHooked)
        {
            return;
        }

        tracingSystem.OnTraceStarted    += InitializeVisuals;
        tracingSystem.OnKeyframeReached += UpdateVisuals;
        tracingSystem.OnTraceCompleted  += FinalizeVisuals;
        tracingCallbacksHooked = true;
        Debug.Log("[VisualEffectManager] Tracing callbacks registered.");
    }

    private void RegisterRecorderCallbacks()
    {
        EnsureCoreReferences();
        if (!recorder)
        {
            Debug.LogWarning("[VisualEffectManager] Missing SimpleRecorder; recorder callbacks not registered.", this);
            return;
        }

        if (recorderCallbacksHooked)
        {
            return;
        }

        recorder.OnRecordingLoaded += OnLetterReloaded;
        recorderCallbacksHooked = true;
    }

    private void UnregisterTracingCallbacks()
    {
        if (!tracingCallbacksHooked || !tracingSystem)
        {
            tracingCallbacksHooked = false;
            return;
        }

        tracingSystem.OnTraceStarted    -= InitializeVisuals;
        tracingSystem.OnKeyframeReached -= UpdateVisuals;
        tracingSystem.OnTraceCompleted  -= FinalizeVisuals;
        tracingCallbacksHooked = false;
        Debug.Log("[VisualEffectManager] Tracing callbacks unregistered.");
    }

    private void UnregisterRecorderCallbacks()
    {
        if (!recorderCallbacksHooked || !recorder)
        {
            recorderCallbacksHooked = false;
            return;
        }

        recorder.OnRecordingLoaded -= OnLetterReloaded;
        recorderCallbacksHooked = false;
    }

    private void OnEnable()
    {
        if (!legacyMode)
        {
            return;
        }

        RegisterTracingCallbacks();
        RegisterRecorderCallbacks();

        _lastModeName = GetModeName();
        _lastDrillName = GetCurrentDrill();
        _lastSpheresSig = ComputeSpheresSignature();
        UpdateReferenceDotsState(force: true);
        if (_dotsActive)
            MaybeBuildPhonemeSegments();
        lastLoggedHitIndex = -1;
    }

    private void Start()
    {
        if (!legacyMode)
        {
            return;
        }

        EnsureCoreReferences();
        RegisterTracingCallbacks();
        RegisterRecorderCallbacks();
    }

    private void OnDisable()
    {
        if (!legacyMode)
        {
            return;
        }

        UnregisterTracingCallbacks();
        UnregisterRecorderCallbacks();

        ClearAllVisuals();
        lastLoggedHitIndex = -1;
    }

    // ---- Trace lifecycle
    private void InitializeVisuals()
    {
        if (!_dotsActive)
        {
            ClearAllVisuals();
            return;
        }

        if (!canvasManager)
        {
            Debug.LogWarning("[VisualEffectManager] InitializeVisuals aborted - CanvasManager missing.", this);
            return;
        }

        if (canvasManager.activeSpheres == null)
        {
            Debug.LogWarning("[VisualEffectManager] InitializeVisuals aborted - no active spheres list.", this);
            return;
        }

        if (canvasManager.activeSpheres.Count == 0)
        {
            canvasManager.CreateVisualizationForAllPoints();
        }

        if (canvasManager.activeSpheres.Count == 0)
        {
            Debug.LogWarning("[VisualEffectManager] InitializeVisuals aborted - no spheres available after rebuild.", this);
            return;
        }

        lastLoggedHitIndex = -1;
        Debug.Log("[VisualEffectManager] InitializeVisuals with " + canvasManager.activeSpheres.Count + " spheres.");

        // do NOT clear cylinders here (we want them to persist)
        oneRunCreatedForIndex.Clear();
        lastHitSphere = null;
        originalScales.Clear();

        if (canvasManager.activeSpheres.Count >= 2)
            maxAllowedDistance = Vector3.Distance(canvasManager.activeSpheres[0].transform.position,
                                                  canvasManager.activeSpheres[1].transform.position);
        else
            maxAllowedDistance = float.MaxValue;

        for (int i = 0; i < canvasManager.activeSpheres.Count; i++)
        {
            var s = canvasManager.activeSpheres[i];
            originalScales[s] = s.transform.localScale;

            if (i == 0)      SetSphereProps(s, hitColor,    normalScale);
            else if (i == 1) SetSphereProps(s, currentColor, currentScale);
            else             SetSphereProps(s, unhitColor,   normalScale);
        }

        EnsureTrackingLine();
    }

    private void UpdateVisuals()
    {
        if (!_dotsActive) return;

        if (!canvasManager || canvasManager.activeSpheres == null || canvasManager.activeSpheres.Count == 0)
        {
            Debug.LogWarning("[VisualEffectManager] UpdateVisuals skipped - no active spheres.", this);
            return;
        }

        int sphereCount = canvasManager.activeSpheres.Count;
        int rawHitIndex = tracingSystem != null ? tracingSystem.CurrentKeyframeIndex : 0;
        int hitIndex = Mathf.Clamp(rawHitIndex, 0, sphereCount - 1);
        int nextIndex = hitIndex + 1;

        if (rawHitIndex != hitIndex)
        {
            Debug.LogWarning($"[VisualEffectManager] UpdateVisuals clamped keyframe index from {rawHitIndex} to {hitIndex} (spheres={sphereCount}).", this);
        }

        if (lastLoggedHitIndex != rawHitIndex)
        {
            Debug.Log($"[VisualEffectManager] UpdateVisuals processing keyframe {rawHitIndex} (clamped {hitIndex}) over {sphereCount} spheres.");
            lastLoggedHitIndex = rawHitIndex;
        }

        // TRACE mode behavior (unchanged): create segment only once when (hitIndex-1)->(hitIndex) is reached
        if (hitIndex > 0 && hitIndex < sphereCount && !oneRunCreatedForIndex.Contains(hitIndex))
        {
            var prev = canvasManager.activeSpheres[hitIndex - 1];
            var cur  = canvasManager.activeSpheres[hitIndex];
            CreateCylinderSegment(prev, cur); // dedup across runs via segmentKeys
            oneRunCreatedForIndex.Add(hitIndex);
        }

        for (int i = 0; i < sphereCount; i++)
        {
            var s = canvasManager.activeSpheres[i];
            if (i <= hitIndex)       SetSphereProps(s, hitColor,    normalScale);
            else if (i == nextIndex) SetSphereProps(s, currentColor, currentScale);
            else                     SetSphereProps(s, unhitColor,  normalScale);
        }

        if (hitIndex >= 0 && hitIndex < sphereCount)
            lastHitSphere = canvasManager.activeSpheres[hitIndex];
    }

    private void FinalizeVisuals()
    {
        if (!_dotsActive) return;

        Debug.Log("[VisualEffectManager] FinalizeVisuals invoked.");
        lastLoggedHitIndex = -1;

        // keep cylinders; only tidy ephemeral lines
        foreach (var s in canvasManager.activeSpheres)
            SetSphereProps(s, hitColor, normalScale);

        DestroyTrackingLine();
        DestroyVoiceTrackingLine();
    }

    private void Update()
    {
        string modeNow = GetModeName();
        string drillNow = GetCurrentDrill();
        int sigNow = ComputeSpheresSignature();

        if (modeNow != _lastModeName || drillNow != _lastDrillName || sigNow != _lastSpheresSig)
        {
            _lastModeName = modeNow;
            _lastDrillName = drillNow;
            _lastSpheresSig = sigNow;
            UpdateReferenceDotsState(force: true);
            if (_dotsActive)
                MaybeBuildPhonemeSegments();
        }

        if (!_dotsActive)
        {
            DestroyTrackingLine();
            DestroyVoiceTrackingLine();
            return;
        }

        HandleProximityEffects();
        UpdateTrackingLine();
        //UpdateVoiceTrackingLineIfNeeded();
    }

    // ---- Helpers
    private void HandleProximityEffects()
    {
        if (!_dotsActive) return;

        int hitIndex  = tracingSystem.CurrentKeyframeIndex;
        int nextIndex = hitIndex + 1;
        Vector3 handPos = recorder.playerToRecord.righthand.transform.position;

        foreach (var s in canvasManager.activeSpheres)
        {
            int idx = canvasManager.activeSpheres.IndexOf(s);
            if (idx > hitIndex && idx != nextIndex)
            {
                float dist = Vector3.Distance(handPos, s.transform.position);
                float scale = dist < proximityDistance ? proximityScale : 1f;
                if (!originalScales.TryGetValue(s, out var baseScale))
                {
                    baseScale = s.transform.localScale;
                    originalScales[s] = baseScale;
                }
                s.transform.localScale = baseScale * scale;
            }
        }
    }

    private void SetSphereProps(GameObject sphere, Color color, float scaleMul)
    {
        var r = sphere.GetComponent<Renderer>();
        if (r != null) r.material.color = color;
        if (originalScales.TryGetValue(sphere, out var baseScale))
            sphere.transform.localScale = baseScale * scaleMul;
    }

    private void EnsureTrackingLine()
    {
        if (!_dotsActive) return;
        if (trackingLine != null) return;
        trackingLine = new GameObject("TrackingLine").AddComponent<LineRenderer>();
        trackingLine.material = dottedLineMaterial;
        trackingLine.startColor = trackingLine.endColor = lineColor;
        trackingLine.startWidth = trackingLine.endWidth = lineWidth;
        trackingLine.positionCount = 2;
        trackingLine.textureMode = LineTextureMode.Tile;
        trackingLine.alignment = LineAlignment.View;
        trackingLine.transform.SetParent(GetVisualsRoot(), true);
    }

    private void UpdateTrackingLine()
    {
        if (!_dotsActive)
        {
            if (trackingLine != null) trackingLine.enabled = false;
            return;
        }
        if (trackingLine == null) return;

        Vector3 handPos = recorder.playerToRecord.righthand.transform.position;
        int nextIndex = tracingSystem.CurrentKeyframeIndex + 1;
        if (nextIndex >= canvasManager.activeSpheres.Count) { trackingLine.enabled = false; return; }

        Vector3 spherePos = canvasManager.activeSpheres[nextIndex].transform.position;
        trackingLine.enabled = Vector3.Distance(handPos, spherePos) > indicatorLineProximity;
        trackingLine.SetPosition(0, handPos);
        trackingLine.SetPosition(1, spherePos);
    }

    private void DestroyTrackingLine()
    {
        if (trackingLine != null) { Destroy(trackingLine.gameObject); trackingLine = null; }
    }

    private void DestroyVoiceTrackingLine()
    {
        if (voiceTrackingLine != null) { Destroy(voiceTrackingLine.gameObject); voiceTrackingLine = null; }
    }

    // --- cylinders between spheres (persist)
    private string MakeKey(Transform a, Transform b)
    {
        int ia = a.GetInstanceID(), ib = b.GetInstanceID();
        return (ia < ib) ? ia + "_" + ib : ib + "_" + ia;
    }

    private void CreateCylinderSegment(GameObject start, GameObject end)
    {
        if (!_dotsActive) return;
        if (!start || !end) return;

        float distance = Vector3.Distance(start.transform.position, end.transform.position);
        if (distance <= 0.0001f) return;

        // bound nonsense long links using spacing of first pair if available
        if (maxAllowedDistance == float.MaxValue && canvasManager.activeSpheres.Count >= 2)
        {
            maxAllowedDistance = Vector3.Distance(canvasManager.activeSpheres[0].transform.position,
                                                  canvasManager.activeSpheres[1].transform.position);
        }
        if (maxAllowedDistance != float.MaxValue && distance > maxAllowedDistance * 2.5f) return;

        string key = MakeKey(start.transform, end.transform);
        if (segmentKeys.Contains(key)) return; // already drew this segment in this letter/mode

        GameObject cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cyl.name = "TraceSegment";
        cyl.transform.SetParent(GetVisualsRoot(), true);

        var mr = cyl.GetComponent<MeshRenderer>();
        if (mr != null)
        {
            Material mat = dottedLineMaterial
                ? new Material(dottedLineMaterial)
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", lineColor);
            else if (mat.HasProperty("_Color")) mat.SetColor("_Color", lineColor);
            mr.material = mat;
        }

        var col = cyl.GetComponent<Collider>();
        if (col) Destroy(col);

        AlignCylinderBetween(start.transform.position, end.transform.position, cyl.transform, distance);
        segmentCylinders.Add(cyl);
        segmentKeys.Add(key);
    }

    private void AlignCylinderBetween(Vector3 a, Vector3 b, Transform t, float dist)
    {
        Vector3 mid = (a + b) * 0.5f;
        Vector3 dir = (b - a).normalized;
        t.position = mid;
        t.up = dir; // align along world Y
        t.localScale = new Vector3(lineWidth, dist * 0.5f, lineWidth); // height = 2*scale.y
    }

    // --- clearing (only on mode/letter change or disable)
    public void ReleaseMemory()
    {
        ClearAllVisuals();
    }

    private void ClearAllVisuals()
    {
        foreach (var seg in segmentCylinders) if (seg) Destroy(seg);
        segmentCylinders.Clear();
        segmentKeys.Clear();
        oneRunCreatedForIndex.Clear();
        DestroyTrackingLine();
        DestroyVoiceTrackingLine();
        lastHitSphere = null;
    }

    // ---- mode/letter hooks
    private void OnLetterReloaded()
    {
        ClearAllVisuals();
        _lastModeName = GetModeName();
        _lastDrillName = GetCurrentDrill();
        _lastSpheresSig = ComputeSpheresSignature();
        UpdateReferenceDotsState(force: true);
        if (_dotsActive)
            MaybeBuildPhonemeSegments();
    }

    private void MaybeBuildPhonemeSegments()
    {
        if (!_dotsActive) return;
        if (!showSegmentsInPhonemeChecking) return;
        if (!canvasManager || canvasManager.activeSpheres == null) return;
        if (canvasManager.activeSpheres.Count < 2) return;

        // establish a reasonable bound for long links
        maxAllowedDistance = Vector3.Distance(canvasManager.activeSpheres[0].transform.position,
                                              canvasManager.activeSpheres[1].transform.position);

        // Pre-draw segments between every adjacent pair
        for (int i = 1; i < canvasManager.activeSpheres.Count; i++)
        {
            var prev = canvasManager.activeSpheres[i - 1];
            var cur  = canvasManager.activeSpheres[i];
            CreateCylinderSegment(prev, cur);
        }
    }

    // ---- detection utils (robust to different LevelManager APIs)
    private string GetModeName()
    {
        if (!levelManager) return "";
        var t = levelManager.GetType();
        // Try common property/field names
        string[] names = {
            "CurrentGameMode","CurrentMode","Mode","GameMode",
            "currentGameMode","currentMode","mode"
        };

        foreach (var n in names)
        {
            var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null)
            {
                var v = p.GetValue(levelManager, null);
                if (v != null) return v.ToString();
            }
            var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                var v = f.GetValue(levelManager);
                if (v != null) return v.ToString();
            }
        }
        return "";
    }

    private bool IsPhonemeMode()
    {
        var name = GetModeName();
        return !string.IsNullOrEmpty(name) && name.Contains("PhonemeChecking");
    }

    private int ComputeSpheresSignature()
    {
        if (canvasManager == null || canvasManager.activeSpheres == null) return 0;
        int sig = canvasManager.activeSpheres.Count * 73856093;
        for (int i = 0; i < canvasManager.activeSpheres.Count; i++)
        {
            var s = canvasManager.activeSpheres[i];
            if (s) sig ^= s.GetInstanceID() * 19349663;
        }
        return sig;
    }

    private void UpdateReferenceDotsState(bool force = false)
    {
        bool shouldShow = ShouldShowReferenceDots();
        Debug.Log("[VisualEffectManager] UpdateReferenceDotsState force=" + force +
                  " shouldShow=" + shouldShow +
                  " dotsActive=" + _dotsActive +
                  " mode=" + GetModeName() +
                  " drill=" + GetCurrentDrill());
        if (!force && shouldShow == _dotsActive) return;

        _dotsActive = shouldShow;

        if (_dotsActive)
        {
            if (canvasManager && (canvasManager.activeSpheres == null || canvasManager.activeSpheres.Count == 0))
                canvasManager.CreateVisualizationForAllPoints();
        }
        else
        {
            if (canvasManager) canvasManager.ClearVisualization();
            ClearAllVisuals();
        }
    }

    private bool ShouldShowReferenceDots()
    {
        if (!levelManager) return true;
        bool isTrace = levelManager.currentMode == LevelManager.GameMode.TraceChecking;
        if (!isTrace) return false;

        string drill = levelManager.currentDrill ?? string.Empty;
        if (drill.IndexOf("Visual", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;

        return true;
    }

    private string GetCurrentDrill()
    {
        if (!levelManager) return string.Empty;
        return levelManager.currentDrill ?? string.Empty;
    }
}



