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
    [SerializeField, Range(4, 64)] private int samplesPerSegment = 12;
    [SerializeField, Range(1f, 3f)] private float activeScaleMultiplier = 1.4f;
    [SerializeField] private Color pendingColor = new(0.85f, 0.1f, 0.1f, 1f);
    [SerializeField] private Color activeColor = new(0.2f, 0.95f, 0.2f, 1f);
    [SerializeField] private Color completedColor = new(0.2f, 0.8f, 0.2f, 1f);
    [SerializeField, Range(0.5f, 2f)] private float completedScaleMultiplier = 1f;
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

    [Header("Generated Markers")]
    [SerializeField] private GameObject markerPrefab;
    [SerializeField] private Material markerMaterialOverride;
    [SerializeField] private Transform markerParentOverride;
    [SerializeField, Range(0.001f, 0.05f)] private float markerScale = 0.02f;
    [SerializeField, Range(0.001f, 0.05f)] private float minPointSpacing = 0.01f;
    [SerializeField, Range(8, 256)] private int maxPointCount = 128;

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
    private readonly List<GameObject> generatedMarkers = new();
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

private class PointState
{
    public GameObject marker;
    public Renderer renderer;
    public MaterialPropertyBlock propertyBlock;
    public Vector3 baseScale;
    public Vector3 localPosition;
    public bool hit;
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

        PointState state = points[currentIndex];
        if (state == null)
        {
            return;
        }

        if (TryGetSphereHit(state, origin, direction, out _))
        {
            CompleteCurrentPoint();
        }
    }

    private void HandleLetterRendered(LetterPathRenderer renderer)
    {
        if (renderer == pathRenderer)
        {
            TryBuildSequence();
        }
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
            pathRenderer.SetVisualizationVisible(true);
        SequenceStarted?.Invoke();
        return true;
    }

    private bool TryBuildSequence()
    {
        if (!visualsVisible)
            return false;

        ResetSequence();

        BuildPoints();
        BuildSegmentLookup();
        if (pathRenderer != null)
            pathRenderer.SetVisualizationVisible(false);

        if (points.Count == 0)
        {
            isReady = false;
            return false;
        }

        if (visualsVisible && pathRenderer != null)
            pathRenderer.SetVisualizationVisible(true);
        if (logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Prepared {points.Count} point(s) with {segmentLookup.Count} cached segment link(s).");
        }
        if (visualsVisible)
            pathRenderer.SetStrokeLinesVisible(false);

        isReady = true;
        isCompleted = false;
        currentIndex = 0;
        ActivateCurrentPoint();
        return true;
    }

    private void BuildPoints()
    {
        if (TryBuildPointsFromRecorder())
            return;

        IReadOnlyList<GameObject> markers = pathRenderer.AnchorMarkers;
        IReadOnlyList<Vector3> positions = pathRenderer.AnchorPositions;
        Transform root = pathRenderer.ContentRoot != null ? pathRenderer.ContentRoot : pathRenderer.transform;

        int count = Mathf.Max(markers.Count, positions.Count);
        if (count == 0)
        {
            if (!TryBuildPointsFromRecorder())
            {
                Debug.LogWarning("[LetterTraceAimSequence] No anchor data available to build points.", this);
            }
            return;
        }

        float minSpacingSqr = Mathf.Max(0.000001f, minPointSpacing * minPointSpacing);

        for (int i = 0; i < count; i++)
        {
            if (points.Count >= maxPointCount)
                break;

            Vector3 local = Vector3.zero;
            if (positions.Count > i)
                local = positions[i];
            else if (i < markers.Count && markers[i])
                local = markers[i].transform.localPosition;

            Vector3 world = root.TransformPoint(local);

            if (points.Count > 0)
            {
                Vector3 prev = points[points.Count - 1].marker.transform.position;
                if ((world - prev).sqrMagnitude < minSpacingSqr)
                    continue;
            }

            GameObject marker = CreateGeneratedMarker(world);
            Renderer renderer = marker.GetComponent<Renderer>() ?? marker.GetComponentInChildren<Renderer>();
            Vector3 baseScale = marker.transform.localScale;

            PointState state = new PointState
            {
                marker = marker,
                renderer = renderer,
                baseScale = baseScale,
                localPosition = transform.InverseTransformPoint(world),
                hit = false
            };

            ApplyPointColor(state, pendingColor);
            marker.transform.localScale = baseScale;
            marker.SetActive(visualsVisible);
            points.Add(state);
        }

        if (logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Built {points.Count} point(s) from LetterPathRenderer anchors.", this);
        }
    }

    private GameObject CreateGeneratedMarker(Vector3 worldPosition)
    {
        Transform parent = ResolveMarkerParent();

        GameObject marker;
        if (markerPrefab)
        {
            marker = Instantiate(markerPrefab, parent, false);
            marker.transform.localScale = markerPrefab.transform.localScale * markerScale;
            RemoveCollider(marker);
        }
        else
        {
            marker = CreateDefaultMarker();
            marker.transform.SetParent(parent, false);
            marker.transform.localScale = Vector3.one * markerScale;
        }

        marker.name = "TraceAnchor";
        marker.layer = parent.gameObject.layer;
        marker.transform.position = worldPosition;
        generatedMarkers.Add(marker);
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

    private Transform ResolveMarkerParent()
    {
        if (markerParentOverride)
            return markerParentOverride;
        if (canvasManager != null && canvasManager.canvasPlane != null)
            return canvasManager.canvasPlane.transform;
        if (pathRenderer != null && pathRenderer.ContentRoot != null)
            return pathRenderer.ContentRoot;
        return transform;
    }

    private bool TryBuildPointsFromRecorder()
    {
        if (!recorder || recorder.currentRecord == null ||
            recorder.currentRecord.frames == null || recorder.currentRecord.frames.Count == 0)
        {
            return false;
        }

        Transform canvasPlane = null;
        if (canvasManager != null && canvasManager.canvasPlane != null)
        {
            canvasPlane = canvasManager.canvasPlane.transform;
        }

        bool created = false;
        bool treatLocal = recorder.currentRecord.isLocalSpace;
        float minSpacingSqr = Mathf.Max(0.000001f, minPointSpacing * minPointSpacing);

        for (int i = 0; i < recorder.currentRecord.frames.Count; i++)
        {
            if (points.Count >= maxPointCount)
                break;

            Vector3 framePos = recorder.currentRecord.frames[i].position;
            Vector3 worldPos = (treatLocal && canvasPlane != null)
                ? canvasPlane.TransformPoint(framePos)
                : framePos;

            if (points.Count > 0)
            {
                Vector3 prev = points[points.Count - 1].marker.transform.position;
                if ((worldPos - prev).sqrMagnitude < minSpacingSqr)
                    continue;
            }

            GameObject marker = CreateGeneratedMarker(worldPos);
            Renderer renderer = marker.GetComponent<Renderer>() ?? marker.GetComponentInChildren<Renderer>();
            Vector3 baseScale = marker.transform.localScale;

            PointState state = new PointState
            {
                marker = marker,
                renderer = renderer,
                baseScale = baseScale,
                localPosition = transform.InverseTransformPoint(worldPos),
                hit = false
            };

            ApplyPointColor(state, pendingColor);
            marker.transform.localScale = baseScale;
            marker.SetActive(visualsVisible);
            points.Add(state);
            created = true;
        }

        if (created && logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Built {points.Count} point(s) from SimpleRecorder fallback.", this);
        }

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

        bool canPoolCylinders = Application.isPlaying;
        Transform poolParent = pathRenderer != null && pathRenderer.ContentRoot != null ? pathRenderer.ContentRoot : transform;

        foreach (GameObject cylinder in spawnedCylinders)
        {
            if (cylinder == null)
            {
                continue;
            }

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
        {
            cylinderPool.Clear();
        }

        spawnedCylinders.Clear();
        cylinderLookup.Clear();
        segmentLookup.Clear();

        foreach (PointState state in points)
        {
            if (state == null)
            {
                continue;
            }

            state.hit = false;
            ApplyPointColor(state, pendingColor);

            if (state.marker != null)
            {
                state.marker.transform.localScale = state.baseScale;
                state.marker.SetActive(visualsVisible);
            }
        }

        points.Clear();
        if (generatedMarkers.Count > 0)
        {
            for (int i = 0; i < generatedMarkers.Count; i++)
            {
                var marker = generatedMarkers[i];
                if (!marker) continue;

                if (Application.isPlaying)
                    Destroy(marker);
                else
                    DestroyImmediate(marker);
            }
            generatedMarkers.Clear();
        }
    }

    public void SetVisualizationVisible(bool visible)
    {
        if (visualsVisible == visible)
            return;

        visualsVisible = visible;
        pathRenderer?.SetVisualizationVisible(visible);

        if (!visible)
        {
            laserEnabled = false;
            ResetSequence();
            if (laserRenderer) laserRenderer.enabled = false;
            isReady = false;
            return;
        }

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

        ApplyVisual(points[currentIndex], activeColor, activeScaleMultiplier);
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

        if (logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Completing point {currentIndex + 1}/{points.Count}.");
        }

        state.hit = true;
        ApplyVisual(state, completedColor, completedScaleMultiplier);

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
        if (!IsValidPointIndex(startIndex) || !IsValidPointIndex(endIndex))
            return;

        var startPoint = points[startIndex];
        var endPoint = points[endIndex];
        if (startPoint?.marker == null || endPoint?.marker == null)
            return;

        SegmentKey key = new SegmentKey(startIndex, endIndex);
        GameObject cylinder = GetOrCreateCylinder(key);

        if (segmentLookup.TryGetValue(key, out LetterPathRenderer.SegmentLink link))
        {
            PositionCylinder(cylinder, link);
        }
        else
        {
            PositionCylinderWorld(cylinder, startPoint.marker.transform.position, endPoint.marker.transform.position);
        }

        if (logSequenceEvents)
        {
            Debug.Log($"[LetterTraceAimSequence] Revealed segment {startIndex}->{endIndex}.");
        }
    }

    private GameObject CreateSegmentCylinder()
    {
        GameObject cylinder = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        RemoveCollider(cylinder);
        cylinder.name = "TraceSegment";
        Transform parent = ResolveMarkerParent();
        cylinder.transform.SetParent(parent, false);
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

        float baseScale = pathRenderer != null ? pathRenderer.MarkerBaseScale : markerScale;
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
            return false;
        }

        origin = useControllerLaserOrigin ? controller.position : loweredOrigin;
        direction = toController / distance;
        return true;
    }

    private bool TryGetSphereHit(PointState state, Vector3 origin, Vector3 direction, out float hitDistance)
    {
        hitDistance = 0f;
        if (state == null)
        {
            return false;
        }

        Vector3 center = state.marker != null
            ? state.marker.transform.position
            : transform.TransformPoint(state.localPosition);
        float radius = GetPointRadius(state);

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

        worldCenter = state.marker != null
            ? state.marker.transform.position
            : transform.TransformPoint(state.localPosition);
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

        Vector3 desiredTip = origin + direction * laserLength;
        if (TryGetCurrentTarget(out PointState state, out _, out _) && TryGetSphereHit(state, origin, direction, out
float distance))
        {
            desiredTip = origin + direction * distance;
        }

        if (!hasLag)
        {
            hasLag = true;
            lagTip = desiredTip;
            lagVelocity = Vector3.zero;
        }
        else
        {
            lagTip = Vector3.SmoothDamp(lagTip, desiredTip, ref lagVelocity, Mathf.Max(0.0001f, laserLag));
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
