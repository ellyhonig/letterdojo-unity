using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
[RequireComponent(typeof(LetterPathRenderer))]
public class LetterTraceAimSequence : MonoBehaviour
{
    public enum ControllerHand
    {
        Left,
        Right
    }

    [Header("References")]
    [SerializeField] private LetterPathRenderer pathRenderer;
    [SerializeField] private simplePlayer player;
    [SerializeField] private SimpleRecorder recorder;
    [SerializeField] private CanvasManager canvasManager;
    [SerializeField] private ControllerHand controllerHand = ControllerHand.Right;

    [Header("Point Visuals")]
    [SerializeField] private Color pendingColor = new(0.85f, 0.1f, 0.1f, 1f);
    [SerializeField] private Color activeColor = new(0.2f, 0.95f, 0.2f, 1f);
    [SerializeField] private Color completedColor = new(0.2f, 0.8f, 0.2f, 1f);
    [Tooltip("Scale multiplier applied when a dot is highlighted.")]
    [SerializeField, Min(1f)] private float hitDotScaleMultiplier = 1.35f;
    [SerializeField, Range(0.05f, 1f)] private float segmentThicknessRatio = 0.12f;

    [Header("Laser Visuals")]
    [SerializeField] private float laserLag = 0.08f;
    [SerializeField] private float laserLength = 3f;
    [SerializeField] private float laserWidth = 0.0035f;
    [SerializeField] private Color laserColor = new(0.2f, 0.8f, 1f, 0.95f);

    [Header("Aim Settings")]
    [SerializeField] private float hmdDownOffset = 0.20f;
    [SerializeField, Min(0f)] private float extendDistanceThreshold = 0.18f;
    [SerializeField] private bool useControllerLaserOrigin = true;
    [SerializeField, Range(1f, 3f)] private float hitRadiusMultiplier = 1.4f;

    private const float AutoAimBendWeight = 0.95f;
    private const float AutoAimCompletionRadiusMultiplier = 1.2f;
    private const float AutoAimAlignmentThreshold = 0.70f;
    private const float AutoAimReleaseThreshold = -0.60f;
    private const float AutoAimReleaseGraceSeconds = 0.35f;
    private const float AutoAdvanceCompletionFraction = 0.6f;
    private const float AutoAdvanceMinimumDistance = 0.015f;

    [Header("Generated Markers")]
    [SerializeField] private GameObject markerPrefab;
    [SerializeField] private Material markerMaterialOverride;
    [SerializeField] private Transform markerParentOverride;
    [SerializeField, Range(0.0001f, 0.05f)] private float minPointSpacing = 0.002f;
    [SerializeField, Range(8, 256)] private int maxPointCount = 128;
    [Tooltip("Fallback dot size used when temporary markers are generated.")]
    [SerializeField, Min(0.001f)] private float fallbackGeneratedScale = 0.02f;

    [Header("Events")]
    [SerializeField] private UnityEvent onTraceCompleted;
    [SerializeField] private bool logSequenceEvents = false;

    public event Action TraceCompleted;
    public event Action SequenceStarted;
    public event Action<int> PointCompleted;

    public int PointCount => points.Count;
    public int CompletedCount => Mathf.Clamp(currentIndex, 0, points.Count);

    private readonly List<PointState> points = new();
    private readonly Dictionary<SegmentKey, LetterPathRenderer.SegmentLink> segmentLookup = new();
    private readonly Dictionary<SegmentKey, GameObject> cylinderLookup = new();
    private readonly List<GameObject> spawnedCylinders = new();
    private readonly Stack<GameObject> cylinderPool = new();
    private MaterialPropertyBlock cylinderPropertyBlock;
    private static Mesh sharedMarkerMesh;
    private static Material sharedMarkerMaterial;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    private bool isReady;
    private bool isCompleted;
    private int currentIndex;
    private bool visualsVisible = true;
    private bool laserEnabled = false;

    private LineRenderer laserRenderer;
    private Material laserMaterial;
    private Material cylinderMaterial;
    private Vector3 lagTip;
    private Vector3 lagVelocity;
    private bool hasLag;
    private bool autoAimActive;
    private Vector3 autoAimCurrentTarget;
    private float autoAimCurrentRadius;
    private bool autoAimLatched;
    private bool hasLatestControllerPos;
    private Vector3 latestControllerPos;
    private bool autoAdvanceHasTarget;
    private Vector3 autoAdvanceDirection;
    private float autoAdvanceSegmentLength;
    private float autoAdvanceProgress;
    private float autoAdvanceTargetDistance;
    private Vector3 autoAdvanceStartWorld;
    private bool hasHitCurrentDot;
    private float autoAimReleaseTimer;
    private Transform cachedMarkerParent;
    private Transform activePlaneTransform;
    private Vector3 activePlaneNormal = Vector3.up;
    private float activePlaneDepth = 0.002f;
    private float cachedMarkerBaseScale = -1f;
    private int cachedAnchorHash;
    private const float AnchorHashQuantize = 1000f;

private class PointState
{
    public GameObject marker;
    public Renderer renderer;
    public MaterialPropertyBlock propertyBlock;
    public Vector3 baseScale;
    public Vector3 localPosition;
    public bool hit;
    public bool generated;
}


    private readonly struct SegmentKey : IEquatable<SegmentKey>
    {
        public SegmentKey(int a, int b)
        {
            A = a;
            B = b;
        }

        public int A { get; }
        public int B { get; }

        public SegmentKey Reversed() => new SegmentKey(B, A);
        public bool Equals(SegmentKey other) => A == other.A && B == other.B;
        public override bool Equals(object obj) => obj is SegmentKey other && Equals(other);
        public override int GetHashCode() => (A * 397) ^ B;
    }

    private void Awake()
    {
        if (!pathRenderer && !TryGetComponent(out pathRenderer))
        {
            Debug.LogError("LetterTraceAimSequence requires a LetterPathRenderer", this);
            enabled = false;
            return;
        }

        if (!player)
        {
            player = FindObjectOfType<simplePlayer>();
        }
        if (!recorder)
        {
            recorder = FindObjectOfType<SimpleRecorder>();
        }
        if (!canvasManager)
        {
            canvasManager = FindObjectOfType<CanvasManager>();
        }
        // Create Unity objects here (avoid constructor/field-init usage that calls into engine)
        cylinderPropertyBlock = new MaterialPropertyBlock();
    }

    private void OnEnable()
    {
        pathRenderer.LetterRendered += HandleLetterRendered;
        if (recorder) recorder.OnRecordingLoaded += HandleRecorderLoaded;
        TryBuildSequence();
    }
    private void ApplyPointColor(PointState state, Color color)
    {
        if (state == null || state.renderer == null)
        {
            return;
        }

        if (state.propertyBlock == null)
        {
            state.propertyBlock = new MaterialPropertyBlock();
        }

        state.renderer.GetPropertyBlock(state.propertyBlock);
        state.propertyBlock.SetColor(BaseColorId, color);
        state.propertyBlock.SetColor(ColorId, color);
        state.propertyBlock.SetColor(EmissionColorId, color * 0.5f);
        state.renderer.SetPropertyBlock(state.propertyBlock);

        if (state.marker != null)
        {
            state.marker.transform.localScale = state.baseScale;
        }
    }

    private void ApplyVisual(PointState state, Color color, float scaleMultiplier)
    {
        if (state == null)
        {
            return;
        }

        ApplyPointColor(state, color);

        if (state.marker != null)
        {
            state.marker.transform.localScale = state.baseScale * scaleMultiplier;
        }
    }

    private void ClampPointScale(PointState state, float neighborSpacing)
    {
        if (state == null)
            return;

        float desired = Mathf.Max(0.0001f, state.baseScale.x);
        state.baseScale = Vector3.one * desired;

        if (state.marker != null)
            state.marker.transform.localScale = state.baseScale;
    }

    private void MaybeRefreshMarkerScales()
    {
        if (!visualsVisible || points.Count == 0)
            return;

        float baseScale = GetBaseMarkerScale();
        if (Mathf.Approximately(baseScale, cachedMarkerBaseScale))
            return;

        RefreshPointScales(baseScale);
    }

    private void RefreshPointScales(float baseScale)
    {
        cachedMarkerBaseScale = baseScale;
        if (points.Count == 0)
            return;

        UpdateActivePlane();

        Vector3? lastWorld = null;
        for (int i = 0; i < points.Count; i++)
        {
            PointState state = points[i];
            if (state == null)
                continue;

            state.baseScale = Vector3.one * baseScale;
            Vector3 world = PlaneLocalToWorld(state.localPosition);
            float spacing = lastWorld.HasValue ? Vector3.Distance(world, lastWorld.Value) : float.PositiveInfinity;

            if (i > 0)
            {
                PointState previous = points[i - 1];
                if (previous != null)
                {
                    ClampPointScale(previous, spacing);
                }
            }

            ClampPointScale(state, spacing);
            lastWorld = world;
        }

        ReapplyPointVisuals();
    }

    private void ReapplyPointVisuals()
    {
        for (int i = 0; i < points.Count; i++)
        {
            PointState state = points[i];
            if (state == null)
                continue;

            if (state.hit)
            {
                ApplyPointColor(state, completedColor);
            }
            else if (i == currentIndex)
            {
                ApplyVisual(state, activeColor, hitDotScaleMultiplier);
            }
            else
            {
                ApplyPointColor(state, pendingColor);
            }
        }
    }

    private int ComputeAnchorHash()
    {
        if (pathRenderer == null)
            return 0;

        IReadOnlyList<Vector3> anchors = pathRenderer.AnchorPositions;
        if (anchors == null || anchors.Count == 0)
            return 0;

        int hash = 17;
        for (int i = 0; i < anchors.Count; i++)
        {
            Vector3 p = anchors[i];
            int x = Mathf.RoundToInt(p.x * AnchorHashQuantize);
            int y = Mathf.RoundToInt(p.y * AnchorHashQuantize);
            int z = Mathf.RoundToInt(p.z * AnchorHashQuantize);
            unchecked
            {
                hash = hash * 31 + x;
                hash = hash * 31 + y;
                hash = hash * 31 + z;
            }
        }

        return hash;
    }

    private void OnDisable()
    {
        pathRenderer.LetterRendered -= HandleLetterRendered;
        if (recorder) recorder.OnRecordingLoaded -= HandleRecorderLoaded;
        ResetSequence();
        ResetLaser();
    }

    private void OnDestroy()
    {
        if (recorder) recorder.OnRecordingLoaded -= HandleRecorderLoaded;
        if (pathRenderer) pathRenderer.LetterRendered -= HandleLetterRendered;
    }

    private void Update()
    {
        MaybeRefreshMarkerScales();

        if (!laserEnabled)
        {
            UpdateLaser(false, Vector3.zero, Vector3.forward);
            return;
        }

        if (!TryGetAimRay(out Vector3 origin, out Vector3 direction))
        {
            UpdateLaser(false, Vector3.zero, Vector3.forward);
            return;
        }

        UpdateLaser(true, origin, direction);

        if (!isReady || isCompleted)
        {
            return;
        }

        if (currentIndex < 0 || currentIndex >= points.Count)
        {
            return;
        }

        if (points[currentIndex] == null)
        {
            return;
        }

        if (hasLatestControllerPos && ProcessAutoAdvance(latestControllerPos))
        {
            return;
        }
    }

    private void HandleLetterRendered(LetterPathRenderer renderer)
    {
        if (renderer != pathRenderer)
            return;

        int newAnchorHash = ComputeAnchorHash();
        bool needsRebuild = !isReady || points.Count == 0 || newAnchorHash != cachedAnchorHash;

        if (needsRebuild)
        {
            TryBuildSequence();
            return;
        }

        RefreshPointScales(GetBaseMarkerScale());
    }

    public bool PrepareSequence()
    {
        return TryBuildSequence();
    }

    private void HandleRecorderLoaded()
    {
        TryBuildSequence();
    }

    public bool StartRun()
    {
        bool ready = TryBuildSequence();
        if (!ready)
        {
            laserEnabled = false;
            return false;
        }

        ResetLaser();
        laserEnabled = true;
        if (visualsVisible && pathRenderer != null)
            pathRenderer.SetStrokeLinesVisible(true);
        SequenceStarted?.Invoke();
        return true;
    }

    private bool TryBuildSequence()
    {
        if (!visualsVisible)
            return false;

        UpdateActivePlane();
        ResetSequence();

        BuildPoints();
        BuildSegmentLookup();

        if (points.Count == 0)
        {
            isReady = false;
            return false;
        }

        if (logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Prepared {points.Count} point(s) with {segmentLookup.Count} cached segment link(s).");
        }
        pathRenderer?.SetStrokeLinesVisible(false);

        isReady = true;
        isCompleted = false;
        currentIndex = 0;
        ActivateCurrentPoint();
        cachedMarkerBaseScale = GetBaseMarkerScale();
        cachedAnchorHash = ComputeAnchorHash();
        return true;
    }

    private void BuildPoints()
    {
        UpdateActivePlane();
        if (TryBuildFromAnchors())
            return;

        if (TryBuildPointsFromRecorder())
            return;

        Debug.LogWarning("[LetterTraceAimSequence] No anchor data available to build points.", this);
    }

    private bool TryBuildFromAnchors()
    {
        if (pathRenderer == null)
            return false;

        Transform root = pathRenderer.ContentRoot != null ? pathRenderer.ContentRoot : pathRenderer.transform;
        IReadOnlyList<Vector3> anchorPositions = pathRenderer.AnchorPositions;
        if (anchorPositions == null || anchorPositions.Count == 0)
            return false;

        float minSpacingSqr = Mathf.Max(0.000001f, minPointSpacing * minPointSpacing);
        float baseScaleValue = GetBaseMarkerScale();
        Vector3? lastWorld = null;

        for (int i = 0; i < anchorPositions.Count; i++)
        {
            if (points.Count >= maxPointCount)
                break;

            Vector3 local = anchorPositions[i];
            Vector3 world = root.TransformPoint(local);
            Vector3 projectedWorld = ProjectOntoActivePlane(world);
            Vector3 planeLocal = WorldToPlaneLocal(projectedWorld);

            if (lastWorld.HasValue && (projectedWorld - lastWorld.Value).sqrMagnitude < minSpacingSqr)
                continue;

            GameObject marker = CreateGeneratedMarker(planeLocal);
            marker.transform.localScale = Vector3.one * baseScaleValue;
            Renderer renderer = marker.GetComponent<Renderer>() ?? marker.GetComponentInChildren<Renderer>();

            PointState state = new PointState
            {
                marker = marker,
                renderer = renderer,
                baseScale = Vector3.one * baseScaleValue,
                localPosition = planeLocal,
                hit = false,
                generated = true
            };

            float spacing = lastWorld.HasValue ? Vector3.Distance(projectedWorld, lastWorld.Value) : float.PositiveInfinity;
            if (points.Count > 0)
                ClampPointScale(points[points.Count - 1], spacing);
            ClampPointScale(state, spacing);

            ApplyPointColor(state, pendingColor);
            marker.SetActive(visualsVisible);
            points.Add(state);
            lastWorld = projectedWorld;
        }

        pathRenderer.SetVisualizationVisible(false);
        HideSourceMarkers();

        if (logSequenceEvents)
            Debug.Log($"[LetterTraceAimSequence] Adopted {points.Count} anchor point(s) from LetterPathRenderer.", this);

        return points.Count > 0;
    }

    private GameObject CreateGeneratedMarker(Vector3 planeLocalPosition)
    {
        Transform parent = ResolveMarkerParent();
        float baseScale = GetBaseMarkerScale();

        GameObject marker;
        if (markerPrefab)
        {
            marker = Instantiate(markerPrefab, parent, false);
            marker.transform.localScale = Vector3.one * baseScale;
            RemoveCollider(marker);
        }
        else
        {
            marker = CreateDefaultMarker();
            marker.transform.SetParent(parent, false);
            marker.transform.localScale = Vector3.one * baseScale;
        }

        marker.name = "TraceAnchor";
        marker.layer = parent.gameObject.layer;
        marker.transform.localPosition = planeLocalPosition;
        marker.transform.localRotation = Quaternion.identity;
        return marker;
    }

    private GameObject CreateDefaultMarker()
    {
        EnsureSharedMarkerResources();
        GameObject marker = new GameObject("TraceAnchor");
        var filter = marker.AddComponent<MeshFilter>();
        filter.sharedMesh = sharedMarkerMesh;
        var renderer = marker.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = markerMaterialOverride ? markerMaterialOverride : sharedMarkerMaterial;
        return marker;
    }

    private void EnsureSharedMarkerResources()
    {
        if (sharedMarkerMesh != null && sharedMarkerMaterial != null)
            return;

        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        RemoveCollider(temp);
        var filter = temp.GetComponent<MeshFilter>();
        var renderer = temp.GetComponent<MeshRenderer>();
        sharedMarkerMesh = filter.sharedMesh;
        sharedMarkerMaterial = new Material(renderer.sharedMaterial)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        if (Application.isPlaying)
            Destroy(temp);
        else
            DestroyImmediate(temp);
    }

    private float GetBaseMarkerScale()
    {
        if (pathRenderer != null)
            return Mathf.Max(0.0001f, pathRenderer.MarkerBaseScale);
        return Mathf.Max(0.0001f, fallbackGeneratedScale);
    }

    private void ReleaseCylinders()
    {
        bool canPoolCylinders = Application.isPlaying;
        Transform poolParent = ResolveMarkerParent();

        foreach (GameObject cylinder in spawnedCylinders)
        {
            if (!cylinder) continue;

            if (canPoolCylinders)
            {
                cylinder.SetActive(false);
                cylinder.transform.SetParent(poolParent, false);
                cylinderPool.Push(cylinder);
            }
            else
            {
                DestroyImmediate(cylinder);
            }
        }

        if (!canPoolCylinders)
            cylinderPool.Clear();

        spawnedCylinders.Clear();
        cylinderLookup.Clear();
    }

    private void RecyclePointStates(bool keepVisible)
    {
        Transform parent = ResolveMarkerParent();

        foreach (PointState state in points)
        {
            if (state == null || state.marker == null)
                continue;

            if (state.generated)
            {
                if (Application.isPlaying)
                    Destroy(state.marker);
                else
                    DestroyImmediate(state.marker);
                state.marker = null;
                continue;
            }

            state.hit = false;
            ApplyPointColor(state, pendingColor);
            state.marker.transform.SetParent(parent, false);
            state.marker.transform.localPosition = state.localPosition;
            state.marker.transform.localRotation = Quaternion.identity;
            state.marker.transform.localScale = state.baseScale;
            state.marker.SetActive(keepVisible && visualsVisible);
        }

        points.Clear();
    }

    private void HideSourceMarkers()
    {
        if (pathRenderer == null)
            return;

        var anchors = pathRenderer.AnchorMarkers;
        if (anchors != null)
        {
            for (int i = 0; i < anchors.Count; i++)
            {
                if (anchors[i])
                    anchors[i].SetActive(false);
            }
        }

        pathRenderer.SetStrokeLinesVisible(false);
        pathRenderer.SetVisualizationVisible(false);
    }

    private Transform ResolveMarkerParent(bool forceRefresh = false)
    {
        if (forceRefresh || cachedMarkerParent == null)
        {
            cachedMarkerParent = markerParentOverride
                ? markerParentOverride
                : (canvasManager != null && canvasManager.canvasPlane != null
                    ? canvasManager.canvasPlane.transform
                    : (pathRenderer != null && pathRenderer.ContentRoot != null
                        ? pathRenderer.ContentRoot
                        : transform));
        }
        return cachedMarkerParent;
    }

    private void UpdateActivePlane()
    {
        Transform resolved = ResolveMarkerParent(true);
        activePlaneTransform = resolved != null ? resolved : transform;

        Vector3 referenceNormal = pathRenderer != null
            ? pathRenderer.transform.TransformDirection(Vector3.up)
            : Vector3.up;

        if (activePlaneTransform != null && activePlaneTransform != transform)
        {
            Vector3[] axes =
            {
                activePlaneTransform.TransformDirection(Vector3.up),
                activePlaneTransform.TransformDirection(Vector3.forward),
                activePlaneTransform.TransformDirection(Vector3.right)
            };

            float bestDot = -1f;
            Vector3 bestAxis = referenceNormal;
            Vector3 refNorm = referenceNormal.sqrMagnitude > 0f ? referenceNormal.normalized : Vector3.up;
            for (int i = 0; i < axes.Length; i++)
            {
                Vector3 axis = axes[i].sqrMagnitude > 0f ? axes[i].normalized : Vector3.zero;
                float dot = Mathf.Abs(Vector3.Dot(refNorm, axis));
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestAxis = axis;
                }
            }
            if (Vector3.Dot(bestAxis, referenceNormal) < 0f)
                bestAxis = -bestAxis;
            activePlaneNormal = bestAxis.sqrMagnitude > 0f ? bestAxis.normalized : refNorm;
        }
        else
        {
            activePlaneNormal = referenceNormal.sqrMagnitude > 0f ? referenceNormal.normalized : Vector3.up;
        }

        float depth = pathRenderer != null ? pathRenderer.DepthOffset : 0.002f;
        activePlaneDepth = Mathf.Clamp(depth, -0.2f, 0.2f);
    }

    private Vector3 ProjectOntoActivePlane(Vector3 worldPoint)
    {
        Vector3 normal = activePlaneNormal.sqrMagnitude > 0f ? activePlaneNormal.normalized : Vector3.up;
        Vector3 origin = PlaneLocalToWorld(Vector3.zero);
        float distance = Vector3.Dot(worldPoint - origin, normal);
        Vector3 projected = worldPoint - distance * normal;
        return projected + normal * activePlaneDepth;
    }

    private Vector3 PlaneLocalToWorld(Vector3 planeLocal)
    {
        Transform plane = activePlaneTransform != null ? activePlaneTransform : transform;
        return plane != null ? plane.TransformPoint(planeLocal) : planeLocal;
    }

    private Vector3 WorldToPlaneLocal(Vector3 worldPoint)
    {
        Transform plane = activePlaneTransform != null ? activePlaneTransform : transform;
        return plane != null ? plane.InverseTransformPoint(worldPoint) : worldPoint;
    }

    private Vector3 GetStateWorldPosition(PointState state)
    {
        if (state == null)
            return Vector3.zero;

        if (state.marker != null)
            return state.marker.transform.position;

        return PlaneLocalToWorld(state.localPosition);
    }

    private bool TryBuildPointsFromRecorder()
    {
        if (!recorder || recorder.currentRecord == null ||
            recorder.currentRecord.frames == null || recorder.currentRecord.frames.Count == 0)
        {
            return false;
        }

        UpdateActivePlane();
        bool created = false;
        bool treatLocal = recorder.currentRecord.isLocalSpace;
        float minSpacingSqr = Mathf.Max(0.000001f, minPointSpacing * minPointSpacing);
        Vector3? lastWorld = null;

        for (int i = 0; i < recorder.currentRecord.frames.Count; i++)
        {
            if (points.Count >= maxPointCount)
                break;

            Vector3 framePos = recorder.currentRecord.frames[i].position;
            Vector3 worldPos = treatLocal
                ? PlaneLocalToWorld(framePos)
                : framePos;
            Vector3 projectedWorld = ProjectOntoActivePlane(worldPos);
            Vector3 planeLocal = WorldToPlaneLocal(projectedWorld);

            if (points.Count > 0)
            {
                Vector3 prevWorld = PlaneLocalToWorld(points[points.Count - 1].localPosition);
                if ((projectedWorld - prevWorld).sqrMagnitude < minSpacingSqr)
                    continue;
            }

            GameObject marker = CreateGeneratedMarker(planeLocal);
            Renderer renderer = marker.GetComponent<Renderer>() ?? marker.GetComponentInChildren<Renderer>();
            float baseScaleValue = marker.transform.localScale.x;

            PointState state = new PointState
            {
                marker = marker,
                renderer = renderer,
                baseScale = Vector3.one * baseScaleValue,
                localPosition = planeLocal,
                hit = false,
                generated = true
            };

            float spacing = lastWorld.HasValue ? Vector3.Distance(projectedWorld, lastWorld.Value) : float.PositiveInfinity;
            if (points.Count > 0)
                ClampPointScale(points[points.Count - 1], spacing);
            ClampPointScale(state, spacing);

            ApplyPointColor(state, pendingColor);
            marker.SetActive(visualsVisible);
            points.Add(state);
            created = true;
            lastWorld = projectedWorld;
        }

        if (created && logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Built {points.Count} point(s) from SimpleRecorder fallback.", this);
        }

        pathRenderer?.SetVisualizationVisible(false);
        return created;
    }

    private void BuildSegmentLookup()
    {
        segmentLookup.Clear();
        cylinderLookup.Clear();

        IReadOnlyList<LetterPathRenderer.SegmentLink> links = pathRenderer.SegmentLinks;
        for (int i = 0; i < links.Count; i++)
        {
            LetterPathRenderer.SegmentLink link = links[i];
            SegmentKey key = new(link.StartIndex, link.EndIndex);
            if (!segmentLookup.ContainsKey(key))
            {
                segmentLookup[key] = link;
            }

            SegmentKey reverse = key.Reversed();
            if (!segmentLookup.ContainsKey(reverse))
            {
                segmentLookup[reverse] = link;
            }
        }
    }

    private void ResetSequence()
    {
        pathRenderer?.SetStrokeLinesVisible(visualsVisible);

        isReady = false;
        isCompleted = false;
        currentIndex = 0;
        laserEnabled = false;
        ResetLaser();
        autoAdvanceHasTarget = false;
        autoAdvanceDirection = Vector3.zero;
        autoAdvanceSegmentLength = 0f;
        autoAdvanceProgress = 0f;
        autoAdvanceTargetDistance = 0f;
        hasLatestControllerPos = false;
        autoAdvanceStartWorld = Vector3.zero;
        autoAimLatched = false;
        hasHitCurrentDot = false;
        autoAimReleaseTimer = 0f;

        ReleaseCylinders();
        RecyclePointStates(keepVisible: visualsVisible);
        segmentLookup.Clear();
        cachedMarkerBaseScale = -1f;
        cachedAnchorHash = 0;
    }

    public void SetVisualizationVisible(bool visible)
    {
        if (visualsVisible == visible)
            return;

        visualsVisible = visible;

        if (!visible)
        {
            laserEnabled = false;
            ResetLaser();
            RecyclePointStates(false);
            ReleaseCylinders();
            HideSourceMarkers();
            isReady = false;
            currentIndex = 0;
            cachedMarkerBaseScale = -1f;
            cachedAnchorHash = 0;
            autoAdvanceHasTarget = false;
            autoAdvanceDirection = Vector3.zero;
            autoAdvanceSegmentLength = 0f;
            autoAdvanceProgress = 0f;
            autoAdvanceTargetDistance = 0f;
            hasLatestControllerPos = false;
            autoAdvanceStartWorld = Vector3.zero;
            autoAimLatched = false;
            hasHitCurrentDot = false;
            autoAimReleaseTimer = 0f;
            return;
        }

        HideSourceMarkers();
        TryBuildSequence();
        if (!laserEnabled)
            ResetLaser();
    }
    private void ActivateCurrentPoint()
    {
        if (currentIndex < 0 || currentIndex >= points.Count)
        {
            return;
        }

        if (logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Activating point {currentIndex + 1}/{points.Count}.");
        }

        ApplyVisual(points[currentIndex], activeColor, hitDotScaleMultiplier);
        PrepareAutoAdvanceForCurrentPoint();
        MaybeAutoCompleteTerminalPoint();
    }

    private void PrepareAutoAdvanceForCurrentPoint()
    {
        autoAdvanceProgress = 0f;
        autoAdvanceDirection = Vector3.zero;
        autoAdvanceSegmentLength = 0f;
        autoAdvanceTargetDistance = 0f;
        autoAdvanceHasTarget = false;
        autoAdvanceStartWorld = Vector3.zero;

        if (!IsValidPointIndex(currentIndex))
        {
            return;
        }

        int nextIndex = currentIndex + 1;
        if (!IsValidPointIndex(nextIndex))
        {
            return;
        }

        PointState currentState = points[currentIndex];
        PointState nextState = points[nextIndex];
        if (currentState == null || nextState == null)
        {
            return;
        }

        Vector3 currentWorld = GetStateWorldPosition(currentState);
        Vector3 nextWorld = GetStateWorldPosition(nextState);
        Vector3 segment = nextWorld - currentWorld;
        float length = segment.magnitude;
        if (length <= 1e-5f)
        {
            autoAdvanceHasTarget = false;
            autoAdvanceDirection = Vector3.zero;
            autoAdvanceSegmentLength = 0f;
            autoAdvanceTargetDistance = 0f;
            CompleteCurrentPoint();
            return;
        }

        autoAdvanceDirection = segment / length;
        autoAdvanceSegmentLength = length;
        float fraction = AutoAdvanceCompletionFraction;
        float fractionalTarget = Mathf.Max(0f, length * fraction);
        float minTarget = AutoAdvanceMinimumDistance;
        float baseTarget = Mathf.Max(minTarget, fractionalTarget);
        autoAdvanceTargetDistance = Mathf.Clamp(baseTarget, minTarget, length);
        autoAdvanceStartWorld = currentWorld;
        autoAdvanceHasTarget = true;
        hasHitCurrentDot = autoAimLatched;
        autoAimReleaseTimer = 0f;
    }

    private void MaybeAutoCompleteTerminalPoint()
    {
        if (autoAdvanceHasTarget)
        {
            return;
        }

        if (!IsValidPointIndex(currentIndex))
        {
            return;
        }

        if (currentIndex == points.Count - 1)
        {
            CompleteCurrentPoint();
        }
    }

    private bool ProcessAutoAdvance(Vector3 controllerPosition)
    {
        if (!autoAdvanceHasTarget)
        {
            return false;
        }

        if (autoAdvanceSegmentLength <= 1e-5f)
        {
            CompleteCurrentPoint();
            return true;
        }

        if (!hasHitCurrentDot)
        {
            return false;
        }

        Vector3 controllerOnPlane = ProjectOntoActivePlane(controllerPosition);
        Vector3 offset = controllerOnPlane - autoAdvanceStartWorld;
        if (offset.sqrMagnitude <= 1e-8f)
        {
            return false;
        }

        float along = Vector3.Dot(offset, autoAdvanceDirection);
        along = Mathf.Clamp(along, 0f, autoAdvanceSegmentLength);

        autoAdvanceProgress = Mathf.Max(autoAdvanceProgress, along);

        float target = Mathf.Max(1e-5f, autoAdvanceTargetDistance);
        if (autoAdvanceProgress >= target)
        {
            CompleteCurrentPoint();
            return true;
        }

        return false;
    }

    private void CompleteCurrentPoint()
    {
        if (currentIndex < 0 || currentIndex >= points.Count)
        {
            return;
        }

        PointState state = points[currentIndex];
        if (state == null || state.hit)
        {
            return;
        }

        autoAdvanceHasTarget = false;
        autoAdvanceProgress = 0f;
        hasHitCurrentDot = autoAimLatched;
        autoAimReleaseTimer = 0f;

        if (logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Completing point {currentIndex + 1}/{points.Count}.");
        }

        state.hit = true;
        ApplyVisual(state, completedColor, hitDotScaleMultiplier);

        int previousIndex = currentIndex - 1;
        if (previousIndex >= 0 && previousIndex < points.Count)
        {
            RevealSegment(previousIndex, currentIndex);
        }

        PointCompleted?.Invoke(currentIndex);

        currentIndex++;
        if (currentIndex >= points.Count)
        {
            FinishSequence();
        }
        else
        {
            ActivateCurrentPoint();
        }
    }

    private void FinishSequence()
    {
        if (isCompleted)
        {
            return;
        }

        isCompleted = true;
        laserEnabled = false;
        UpdateLaser(false, Vector3.zero, Vector3.forward);
        if (logSequenceEvents)
        {
            Debug.Log("[LetterTraceAimSequence] Trace completed.");
        }
        onTraceCompleted?.Invoke();
        TraceCompleted?.Invoke();
    }

    private void RevealSegment(int startIndex, int endIndex)
    {
        if (logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Connectors disabled; visited segment {startIndex}->{endIndex}.");
        }
    }

    private GameObject CreateSegmentCylinder()
    {
        GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        RemoveCollider(cylinder);
        cylinder.name = "TraceSegment";
        Transform parent = ResolveMarkerParent();
        cylinder.transform.SetParent(parent, false);
        cylinder.layer = parent.gameObject.layer;
        cylinder.GetComponent<Renderer>().sharedMaterial = ResolveCylinderMaterial();
        spawnedCylinders.Add(cylinder);
        return cylinder;
    }

    private void PositionCylinder(GameObject cylinder, LetterPathRenderer.SegmentLink link)
    {
        if (cylinder == null)
        {
            return;
        }

        IReadOnlyList<Vector3> anchors = pathRenderer.AnchorPositions;
        if (link.StartIndex < 0 || link.StartIndex >= anchors.Count || link.EndIndex < 0 || link.EndIndex >=
anchors.Count)
        {
            cylinder.SetActive(false);
            return;
        }

        Transform root = pathRenderer.ContentRoot != null ? pathRenderer.ContentRoot : transform;
        Vector3 startWorld = root.TransformPoint(anchors[link.StartIndex]);
        Vector3 endWorld = root.TransformPoint(anchors[link.EndIndex]);

        PositionCylinderWorld(cylinder, startWorld, endWorld);
    }

    private void PositionCylinderWorld(GameObject cylinder, Vector3 startWorld, Vector3 endWorld)
    {
        if (cylinder == null)
            return;

        Vector3 direction = endWorld - startWorld;
        float length = direction.magnitude;
        if (length < 1e-5f)
        {
            cylinder.SetActive(false);
            return;
        }

        cylinder.SetActive(true);
        cylinder.transform.position = (startWorld + endWorld) * 0.5f;
        cylinder.transform.rotation = Quaternion.FromToRotation(Vector3.up, direction.normalized);

        float baseScale = GetBaseMarkerScale();
        float thickness = Mathf.Max(0.002f, baseScale * segmentThicknessRatio);
        cylinder.transform.localScale = new Vector3(thickness, length * 0.5f, thickness);

        if (cylinder.TryGetComponent(out Renderer renderer))
        {
            renderer.material.color = completedColor;
        }
    }

    private GameObject GetOrCreateCylinder(SegmentKey key)
    {
        if (!cylinderLookup.TryGetValue(key, out GameObject cylinder) || cylinder == null)
        {
            cylinder = CreateSegmentCylinder();
            cylinderLookup[key] = cylinder;
        }
        return cylinder;
    }

    private bool IsValidPointIndex(int index) => index >= 0 && index < points.Count;

    private bool TryGetAimRay(out Vector3 origin, out Vector3 direction)
    {
        origin = Vector3.zero;
        direction = Vector3.forward;
        hasLatestControllerPos = false;

        if (player == null)
        {
            player = FindObjectOfType<simplePlayer>();
            if (player == null)
            {
                return false;
            }
        }

        Transform hmd = player.hmd != null ? player.hmd.transform : null;
        GameObject controllerObject = controllerHand == ControllerHand.Right ? player.conR : player.conL;
        if (controllerObject == null)
        {
            controllerObject = controllerHand == ControllerHand.Right ? player.conL : player.conR;
        }

        Transform controller = controllerObject != null ? controllerObject.transform : null;
        if (hmd == null || controller == null)
        {
            return false;
        }

        Vector3 loweredOrigin = hmd.position + Vector3.down * hmdDownOffset;
        Vector3 toController = controller.position - loweredOrigin;
        float distance = toController.magnitude;

        float threshold = Mathf.Max(0f, extendDistanceThreshold);
        if (distance < Mathf.Max(threshold, 1e-5f))
        {
            hasLatestControllerPos = false;
            return false;
        }

        latestControllerPos = controller.position;
        hasLatestControllerPos = true;

        origin = useControllerLaserOrigin ? controller.position : loweredOrigin;
        direction = toController / distance;
        return true;
    }

    private bool TryGetSphereHit(PointState state, Vector3 origin, Vector3 direction, out float hitDistance, float radiusMultiplier = 1f)
    {
        hitDistance = 0f;
        if (state == null)
        {
            return false;
        }

        Vector3 center = GetStateWorldPosition(state);
        float radius = GetPointRadius(state) * Mathf.Max(0.0001f, radiusMultiplier);

        Vector3 oc = origin - center;
        float b = Vector3.Dot(oc, direction);
        float c = Vector3.Dot(oc, oc) - radius * radius;
        float discriminant = b * b - c;
        if (discriminant < 0f)
        {
            return false;
        }

        float sqrtDisc = Mathf.Sqrt(discriminant);
        float distance = -b - sqrtDisc;
        if (distance < 0f)
        {
            distance = -b + sqrtDisc;
            if (distance < 0f)
            {
                return false;
            }
        }

        hitDistance = distance;
        return true;
    }

    private bool TryGetCurrentTarget(out PointState state, out Vector3 worldCenter, out float radius)
    {
        state = null;
        worldCenter = Vector3.zero;
        radius = 0f;

        if (!isReady || currentIndex < 0 || currentIndex >= points.Count)
        {
            return false;
        }

        state = points[currentIndex];
        if (state == null)
        {
            return false;
        }

        worldCenter = GetStateWorldPosition(state);
        radius = GetPointRadius(state);
        return radius > 0f;
    }

    private float GetPointRadius(PointState state)
    {
        if (state == null)
        {
            return 0.001f;
        }

        Vector3 scale = state.marker != null ? state.marker.transform.lossyScale : (Vector3.one *
pathRenderer.MarkerBaseScale);
        float radius = Mathf.Max(0.001f, Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) * 0.5f);
        return radius * Mathf.Max(1f, hitRadiusMultiplier);
    }

    private void UpdateLaser(bool hasRay, Vector3 origin, Vector3 direction)
    {
        if (!hasRay)
        {
            ResetLaser();
            return;
        }

        EnsureLaser();

        autoAimActive = false;
        autoAimCurrentTarget = Vector3.zero;
        autoAimCurrentRadius = 0f;

        Vector3 desiredTip = origin + direction * laserLength;
        PointState state;
        if (TryGetCurrentTarget(out state, out Vector3 targetCenter, out float targetRadius))
        {
            bool hit = TryGetSphereHit(state, origin, direction, out float distance);

            Vector3 dirNorm = direction;
            float dirMag = dirNorm.magnitude;
            if (dirMag > 1e-5f) dirNorm /= dirMag;
            else dirNorm = Vector3.forward;

            Vector3 toTarget = targetCenter - origin;
            float toTargetMag = toTarget.magnitude;
            bool alignmentValid = toTargetMag > 1e-5f;
            float alignment = alignmentValid ? Vector3.Dot(dirNorm, toTarget.normalized) : -1f;

            if (!autoAimLatched && hit && alignmentValid && alignment >= AutoAimAlignmentThreshold)
            {
                autoAimLatched = true;
                hasHitCurrentDot = true;
                autoAimReleaseTimer = 0f;
            }

            if (autoAimLatched)
            {
                if (!alignmentValid || alignment <= AutoAimReleaseThreshold)
                    autoAimReleaseTimer += Time.deltaTime;
                else
                    autoAimReleaseTimer = 0f;

                if (autoAimReleaseTimer >= AutoAimReleaseGraceSeconds)
                {
                    autoAimLatched = false;
                    hasHitCurrentDot = false;
                    autoAimReleaseTimer = 0f;
                }
            }
            else
            {
                autoAimReleaseTimer = 0f;
            }

            if (autoAimLatched && alignmentValid)
            {
                autoAimActive = true;
                autoAimCurrentTarget = targetCenter;
                autoAimCurrentRadius = targetRadius * Mathf.Max(1f, AutoAimCompletionRadiusMultiplier);

                Vector3 baseTip = hit ? (origin + direction * distance) : (origin + direction * laserLength);
                float weight = Mathf.Clamp01(AutoAimBendWeight + 0.1f);
                desiredTip = (weight >= 0.999f) ? targetCenter :
                             (weight <= 0.001f) ? baseTip :
                             Vector3.Lerp(baseTip, targetCenter, weight);

                if (hit) hasHitCurrentDot = true;
            }
            else if (hit)
            {
                desiredTip = origin + direction * distance;
            }
            else
            {
                desiredTip = origin + direction * laserLength;
            }
        }
        else
        {
            autoAimLatched = false;
            hasHitCurrentDot = false;
            desiredTip = origin + direction * laserLength;
        }

        if (!hasLag)
        {
            hasLag = true;
            lagTip = desiredTip;
            lagVelocity = Vector3.zero;
        }
        else
        {
            float lag = autoAimActive ? Mathf.Max(0.00005f, laserLag * 0.35f) : Mathf.Max(0.0001f, laserLag);
            lagTip = Vector3.SmoothDamp(lagTip, desiredTip, ref lagVelocity, lag);
        }

        laserRenderer.enabled = true;
        laserRenderer.startWidth = laserRenderer.endWidth = Mathf.Max(0.0005f, laserWidth);
        laserRenderer.startColor = laserColor;
        laserRenderer.endColor = laserColor;
        laserRenderer.positionCount = 2;
        laserRenderer.SetPosition(0, origin);
        laserRenderer.SetPosition(1, lagTip);
    }

    private void EnsureLaser()
    {
        if (laserRenderer != null)
        {
            return;
        }

        GameObject go = new GameObject("LetterTraceLaser");
        go.transform.SetParent(transform, false);
        laserRenderer = go.AddComponent<LineRenderer>();
        laserRenderer.useWorldSpace = true;
        laserRenderer.textureMode = LineTextureMode.Stretch;
        laserRenderer.numCornerVertices = 2;
        laserRenderer.numCapVertices = 2;
        laserRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        laserRenderer.receiveShadows = false;
        laserRenderer.sharedMaterial = ResolveLaserMaterial();
        laserRenderer.enabled = false;
    }

    private void ResetLaser()
    {
        hasLag = false;
        lagVelocity = Vector3.zero;
        autoAimActive = false;
        autoAimCurrentTarget = Vector3.zero;
        autoAimCurrentRadius = 0f;
        autoAimLatched = false;
        hasLatestControllerPos = false;
        hasHitCurrentDot = false;
        autoAimReleaseTimer = 0f;
        if (laserRenderer != null)
        {
            laserRenderer.enabled = false;
        }
    }


    private Material ResolveLaserMaterial()
    {
        if (laserMaterial != null)
        {
            return laserMaterial;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ??
Shader.Find("Sprites/Default");
        laserMaterial = new Material(shader)
        {
            color = laserColor,
            hideFlags = HideFlags.HideAndDontSave
        };
        return laserMaterial;
    }

    private Material ResolveCylinderMaterial()
    {
        if (cylinderMaterial != null)
        {
            return cylinderMaterial;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ??
Shader.Find("Sprites/Default");
        cylinderMaterial = new Material(shader)
        {
            color = completedColor,
            hideFlags = HideFlags.HideAndDontSave
        };
        return cylinderMaterial;
    }

    private static void RemoveCollider(GameObject primitive)
    {
        if (primitive.TryGetComponent(out Collider collider))
        {
            if (Application.isPlaying)
            {
                Destroy(collider);
            }
            else
            {
                DestroyImmediate(collider);
            }
        }
    }
}

