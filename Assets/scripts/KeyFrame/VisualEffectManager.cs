using UnityEngine;
using System.Collections.Generic;
using System.Reflection;

public class VisualEffectManager : MonoBehaviour
{
    [SerializeField] private LetterTracingSystem tracingSystem;
    [SerializeField] private SimpleRecorder recorder;
    [SerializeField] private CanvasManager canvasManager;
    [SerializeField] private LevelManager levelManager;

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
    private int _lastSpheresSig = 0;

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

    private void Start()
    {
        if (!tracingSystem) tracingSystem = GetComponent<LetterTracingSystem>();
        if (!recorder)      recorder      = GetComponent<SimpleRecorder>();
        if (!canvasManager) canvasManager = GetComponent<CanvasManager>();
        if (!levelManager)  levelManager  = GetComponent<LevelManager>();

        tracingSystem.OnTraceStarted    += InitializeVisuals;
        tracingSystem.OnKeyframeReached += UpdateVisuals;
        tracingSystem.OnTraceCompleted  += FinalizeVisuals;

        // New letter load → clear & maybe pre-draw
        recorder.OnRecordingLoaded      += OnLetterReloaded;

        _lastModeName = GetModeName();
        _lastSpheresSig = ComputeSpheresSignature();
        MaybeBuildPhonemeSegments();
    }

    private void OnDisable()
    {
        tracingSystem.OnTraceStarted    -= InitializeVisuals;
        tracingSystem.OnKeyframeReached -= UpdateVisuals;
        tracingSystem.OnTraceCompleted  -= FinalizeVisuals;
        recorder.OnRecordingLoaded      -= OnLetterReloaded;

        ClearAllVisuals();
    }

    // ---- Trace lifecycle
    private void InitializeVisuals()
    {
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
        int hitIndex  = tracingSystem.CurrentKeyframeIndex;
        int nextIndex = hitIndex + 1;

        // TRACE mode behavior (unchanged): create segment only once when (hitIndex-1)->(hitIndex) is reached
        if (hitIndex > 0 && !oneRunCreatedForIndex.Contains(hitIndex))
        {
            var prev = canvasManager.activeSpheres[hitIndex - 1];
            var cur  = canvasManager.activeSpheres[hitIndex];
            CreateCylinderSegment(prev, cur); // dedup across runs via segmentKeys
            oneRunCreatedForIndex.Add(hitIndex);
        }

        for (int i = 0; i < canvasManager.activeSpheres.Count; i++)
        {
            var s = canvasManager.activeSpheres[i];
            if (i <= hitIndex)       SetSphereProps(s, hitColor,    normalScale);
            else if (i == nextIndex) SetSphereProps(s, currentColor, currentScale);
            else                     SetSphereProps(s, unhitColor,  normalScale);
        }

        lastHitSphere = canvasManager.activeSpheres[hitIndex];
    }

    private void FinalizeVisuals()
    {
        // keep cylinders; only tidy ephemeral lines
        foreach (var s in canvasManager.activeSpheres)
            SetSphereProps(s, hitColor, normalScale);

        DestroyTrackingLine();
        DestroyVoiceTrackingLine();
    }

    private void Update()
    {
        // Detect mode or letter change without relying on specific LevelManager APIs
        string modeNow = GetModeName();
        int sigNow = ComputeSpheresSignature();
        if (modeNow != _lastModeName || sigNow != _lastSpheresSig)
        {
            _lastModeName = modeNow;
            _lastSpheresSig = sigNow;
            ClearAllVisuals();
            MaybeBuildPhonemeSegments();
        }

        HandleProximityEffects();
        UpdateTrackingLine();
        //UpdateVoiceTrackingLineIfNeeded();
    }

    // ---- Helpers
    private void HandleProximityEffects()
    {
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
                s.transform.localScale = originalScales[s] * scale;
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
        _lastSpheresSig = ComputeSpheresSignature();
        MaybeBuildPhonemeSegments();
    }

    private void MaybeBuildPhonemeSegments()
    {
        if (!showSegmentsInPhonemeChecking) return;
        if (!canvasManager || canvasManager.activeSpheres == null) return;
        if (canvasManager.activeSpheres.Count < 2) return;
        if (!IsPhonemeMode()) return;

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
}
