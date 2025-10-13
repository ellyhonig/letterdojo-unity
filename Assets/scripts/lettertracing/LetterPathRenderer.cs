
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;
using Newtonsoft.Json.Linq;
using System.Text;

[DisallowMultipleComponent]
public class LetterPathRenderer : MonoBehaviour
{
    [SerializeField] private string letterId = "a";
    [SerializeField] private bool autoRenderOnEnable = true;
    [SerializeField] private bool autoFitToSurface = true;
    [SerializeField, Min(2)] private int samplesPerSegment = 16;
    [SerializeField] private float coordinateScale = 0.005f;
    [SerializeField] private float depthOffset = 0.002f;
    [SerializeField, Range(0.001f, 0.2f)] private float markerSizeRatio = 0.06f;
    [SerializeField, Range(0.0005f, 0.1f)] private float lineWidthRatio = 0.01f;
    [SerializeField] private Transform contentRoot;
    [SerializeField] private GameObject anchorPointPrefab;
    [SerializeField] private GameObject controlPointPrefab;
    [SerializeField] private LineRenderer lineRendererPrefab;
    [SerializeField] private bool spawnControlMarkers;
    [SerializeField, Min(0.01f)] private float letterScaleMultiplier = 1f;
    [SerializeField, Min(0.01f)] private float markerScaleMultiplier = 1f;

    private ObjectPool<GameObject> anchorPool;
    private ObjectPool<GameObject> controlPool;
    private ObjectPool<LineRenderer> linePool;

    private readonly List<GameObject> activeAnchors = new();
    private readonly List<GameObject> activeControls = new();
    private readonly List<LineRenderer> activeLines = new();
    private readonly List<Vector3> sampleBuffer = new(64);
    private readonly Dictionary<Vector2Int, int> anchorLookup = new();
    private readonly List<Vector3> anchorPositions = new();
    private readonly List<SegmentLink> segmentLinks = new();
    private readonly HashSet<Vector2Int> controlCache = new();

    private const float QuantizeFactor = 1000f;
    private const string ResourcePrefix = "2dletterdata/";
    private const float DefaultMarkerSize = 0.01f;
    private const float DefaultLineWidth = 0.005f;
    private const float ControlScaleFactor = 0.75f;

    private static readonly Dictionary<string, Stroke[]> Cache = new(StringComparer.OrdinalIgnoreCase);

    private float resolvedCoordinateScale;
    private float resolvedMarkerScale = DefaultMarkerSize;
    private float resolvedLineWidth = DefaultLineWidth;

    private Material runtimePointMaterial;
    private Material runtimeControlMaterial;
    private Material runtimeLineMaterial;

    private Vector2 dataMin;
    private Vector2 dataMax;
    private Vector2 dataCenter;

    private float planeWidth = 1f;
    private float planeHeight = 1f;

    public IReadOnlyList<GameObject> AnchorMarkers => activeAnchors;
    public IReadOnlyList<Vector3> AnchorPositions => anchorPositions;
    public IReadOnlyList<SegmentLink> SegmentLinks => segmentLinks;
    public IReadOnlyList<LineRenderer> StrokeLines => activeLines;
    public Transform ContentRoot => contentRoot;
    public float MarkerBaseScale => resolvedMarkerScale * markerScaleMultiplier;

    public event Action<LetterPathRenderer> LetterRendered;

    [Serializable]
    public class Stroke
    {
        public Segment[] segments = Array.Empty<Segment>();
    }

    [Serializable]
    public struct Segment
    {
        public Vector2 a;
        public Vector2 b;
        public Vector2 ctrl;
    }

    public readonly struct SegmentLink
    {
        public SegmentLink(int startIndex, int endIndex, Segment segment)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
            Segment = segment;
        }

        public int StartIndex { get; }
        public int EndIndex { get; }
        public Segment Segment { get; }
    }

    [Serializable]
    private class StrokeWrapper
    {
        public Stroke[] strokes = Array.Empty<Stroke>();
    }

    private void Awake()
    {
        if (contentRoot == null)
        {
            contentRoot = transform;
        }

        samplesPerSegment = Mathf.Max(2, samplesPerSegment);
        resolvedCoordinateScale = Mathf.Max(coordinateScale, 0.0001f);
        EnsurePools();
    }

    private void OnEnable()
    {
        if (autoRenderOnEnable)
        {
            RenderLetter(letterId);
        }
    }

    private void OnDisable()
    {
        ClearActive();
    }

    private void OnDestroy()
    {
        ClearActive();
        linePool?.Dispose();
        anchorPool?.Dispose();
        controlPool?.Dispose();
        DisposeMaterial(ref runtimePointMaterial);
        DisposeMaterial(ref runtimeControlMaterial);
        DisposeMaterial(ref runtimeLineMaterial);
    }

    public void RenderLetter(string letter)
    {
        if (string.IsNullOrWhiteSpace(letter))
        {
            return;
        }

        string trimmed = letter.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        letterId = trimmed;

        EnsurePools();
        CachePlaneMetrics();
        ClearActive();

        Stroke[] strokes = LoadStrokes(trimmed);
        if (strokes == null || strokes.Length == 0)
        {
            return;
        }

        if (!TryComputeDataBounds(strokes))
        {
            Debug.LogWarning("LetterPathRenderer: No usable points in letter data.");
            return;
        }

        AutoConfigureForSurface(strokes);

        for (int strokeIndex = 0; strokeIndex < strokes.Length; strokeIndex++)
        {
            Stroke stroke = strokes[strokeIndex];
            if (stroke == null || stroke.segments == null || stroke.segments.Length == 0)
            {
                continue;
            }

            sampleBuffer.Clear();
            bool firstSegment = true;

            for (int segmentIndex = 0; segmentIndex < stroke.segments.Length; segmentIndex++)
            {
                Segment segment = stroke.segments[segmentIndex];

                int startIndex = SpawnAnchorMarker(segment.a);
                int endIndex = SpawnAnchorMarker(segment.b);

                if (spawnControlMarkers)
                {
                    SpawnControlMarker(segment.ctrl);
                }

                AppendSegmentSamples(segment, firstSegment);
                firstSegment = false;

                if (startIndex >= 0 && endIndex >= 0)
                {
                    segmentLinks.Add(new SegmentLink(startIndex, endIndex, segment));
                }
            }

            if (linePool == null || sampleBuffer.Count < 2)
            {
                continue;
            }

            LineRenderer renderer = linePool.Get();
            renderer.useWorldSpace = false;
            renderer.positionCount = sampleBuffer.Count;
            for (int i = 0; i < sampleBuffer.Count; i++)
            {
                renderer.SetPosition(i, sampleBuffer[i]);
            }
            activeLines.Add(renderer);
        }

        LetterRendered?.Invoke(this);
    }

    private void OnValidate()
    {
        markerScaleMultiplier = Mathf.Max(0.01f, markerScaleMultiplier);
        letterScaleMultiplier = Mathf.Max(0.01f, letterScaleMultiplier);

        if (Application.isPlaying)
        {
            if (isActiveAndEnabled && !string.IsNullOrEmpty(letterId))
                RenderLetter(letterId);
            return;
        }

        if (!autoRenderOnEnable || string.IsNullOrEmpty(letterId))
            return;

        ClearActive();
        RenderLetter(letterId);
        SetVisualizationVisible(true);
        SetVisualizationVisible(false);
    }

    [ContextMenu("Regenerate Anchors")]
    public void RegenerateAnchors()
    {
        if (string.IsNullOrWhiteSpace(letterId))
        {
            Debug.LogWarning("[LetterPathRenderer] Cannot regenerate anchors because letterId is empty.", this);
            return;
        }

        Debug.Log("[LetterPathRenderer] Regenerating anchors for '" + letterId + "'.", this);

        ClearActive();
        RenderLetter(letterId);
        SetVisualizationVisible(true);
        SetStrokeLinesVisible(true);
    }

    public void SetStrokeLinesVisible(bool visible)
    {
        for (int i = 0; i < activeLines.Count; i++)
        {
            LineRenderer renderer = activeLines[i];
            if (renderer != null)
            {
                renderer.enabled = visible;
            }
        }
    }

    public void SetVisualizationVisible(bool visible)
    {
        for (int i = 0; i < activeAnchors.Count; i++)
        {
            if (activeAnchors[i])
                activeAnchors[i].SetActive(visible);
        }

        for (int i = 0; i < activeControls.Count; i++)
        {
            if (activeControls[i])
                activeControls[i].SetActive(visible);
        }

        SetStrokeLinesVisible(visible);
    }

    public Vector3 DataPointToLocal(Vector2 source)
    {
        return ToLocalPosition(source);
    }

    public Vector3 DataPointToWorld(Vector2 source)
    {
        Vector3 local = ToLocalPosition(source);
        return contentRoot != null ? contentRoot.TransformPoint(local) : local;
    }

    private void EnsurePools()
    {
        if (lineRendererPrefab != null && linePool == null)
        {
            linePool = new ObjectPool<LineRenderer>(CreateLineRenderer, OnLineRetrieved, OnLineReleased,
DestroyLineRenderer, false, 4, 32);
        }

        if (anchorPool == null)
        {
            anchorPool = new ObjectPool<GameObject>(CreateAnchorMarker, OnAnchorRetrieved, OnAnchorReleased,
DestroyAnchorMarker, false, 32, 256);
        }

        if (spawnControlMarkers)
        {
            if (controlPool == null)
            {
                controlPool = new ObjectPool<GameObject>(CreateControlMarker, OnControlRetrieved, OnControlReleased,
DestroyControlMarker, false, 8, 128);
            }
        }
        else if (controlPool != null)
        {
            controlPool.Dispose();
            controlPool = null;
        }
    }

    private void ClearActive()
    {
        if (linePool != null)
        {
            for (int i = 0; i < activeLines.Count; i++)
            {
                linePool.Release(activeLines[i]);
            }
            activeLines.Clear();
        }

        if (anchorPool != null)
        {
            for (int i = 0; i < activeAnchors.Count; i++)
            {
                anchorPool.Release(activeAnchors[i]);
            }
            activeAnchors.Clear();
        }

        if (controlPool != null)
        {
            for (int i = 0; i < activeControls.Count; i++)
            {
                controlPool.Release(activeControls[i]);
            }
            activeControls.Clear();
        }

        anchorLookup.Clear();
        anchorPositions.Clear();
        segmentLinks.Clear();
        controlCache.Clear();
    }

    private LineRenderer CreateLineRenderer()
    {
        Transform parent = contentRoot != null ? contentRoot : transform;
        LineRenderer renderer;
        if (lineRendererPrefab != null)
        {
            renderer = Instantiate(lineRendererPrefab, parent);
        }
        else
        {
            GameObject go = new GameObject("LetterStroke");
            go.transform.SetParent(parent, false);
            renderer = go.AddComponent<LineRenderer>();
            renderer.useWorldSpace = false;
            renderer.textureMode = LineTextureMode.Stretch;
            renderer.numCornerVertices = 2;
            renderer.numCapVertices = 2;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = GetLineMaterial();
        }

        renderer.positionCount = 0;
        renderer.startWidth = renderer.endWidth = resolvedLineWidth;
        renderer.enabled = true;
        return renderer;
    }

    private void OnLineRetrieved(LineRenderer renderer)
    {
        renderer.transform.SetParent(contentRoot != null ? contentRoot : transform, false);
        renderer.useWorldSpace = false;
        renderer.positionCount = 0;
        float width = resolvedLineWidth * markerScaleMultiplier;
        renderer.startWidth = renderer.endWidth = width;
        renderer.enabled = true;
    }

    private void OnLineReleased(LineRenderer renderer)
    {
        if (renderer != null)
        {
            renderer.positionCount = 0;
            renderer.enabled = false;
        }
    }

    private void DestroyLineRenderer(LineRenderer renderer)
    {
        if (renderer != null)
        {
            Destroy(renderer.gameObject);
        }
    }

    private GameObject CreateAnchorMarker()
    {
        Transform parent = contentRoot != null ? contentRoot : transform;
        GameObject marker = anchorPointPrefab != null ? Instantiate(anchorPointPrefab, parent) :
            GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.transform.SetParent(parent, false);
        marker.transform.localScale = Vector3.one * (resolvedMarkerScale * markerScaleMultiplier);
        marker.SetActive(false);
        RemoveCollider(marker);
        if (marker.TryGetComponent(out Renderer renderer))
        {
            renderer.sharedMaterial = GetPointMaterial();
        }
        return marker;
    }

    private void OnAnchorRetrieved(GameObject marker)
    {
        marker.transform.SetParent(contentRoot != null ? contentRoot : transform, false);
        marker.transform.localScale = Vector3.one * (resolvedMarkerScale * markerScaleMultiplier);
        marker.transform.localRotation = Quaternion.identity;
        marker.SetActive(true);
    }

    private void OnAnchorReleased(GameObject marker)
    {
        marker.SetActive(false);
    }

    private void DestroyAnchorMarker(GameObject marker)
    {
        if (marker != null)
        {
            Destroy(marker);
        }
    }

    private GameObject CreateControlMarker()
    {
        Transform parent = contentRoot != null ? contentRoot : transform;
        GameObject marker = controlPointPrefab != null ? Instantiate(controlPointPrefab, parent) :
            GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.transform.SetParent(parent, false);
        marker.transform.localScale = Vector3.one * (resolvedMarkerScale * ControlScaleFactor * markerScaleMultiplier);
        marker.SetActive(false);
        RemoveCollider(marker);
        if (marker.TryGetComponent(out Renderer renderer))
        {
            renderer.sharedMaterial = GetControlMaterial();
        }
        return marker;
    }

    private void OnControlRetrieved(GameObject marker)
    {
        marker.transform.SetParent(contentRoot != null ? contentRoot : transform, false);
        marker.transform.localScale = Vector3.one * (resolvedMarkerScale * ControlScaleFactor * markerScaleMultiplier);
        marker.transform.localRotation = Quaternion.identity;
        marker.SetActive(true);
    }

    private void OnControlReleased(GameObject marker)
    {
        marker.SetActive(false);
    }

    private void DestroyControlMarker(GameObject marker)
    {
        if (marker != null)
        {
            Destroy(marker);
        }
    }

    private int SpawnAnchorMarker(Vector2 position)
    {
        Vector2Int key = Quantize(position);
        if (anchorLookup.TryGetValue(key, out int existing))
        {
            return existing;
        }

        if (anchorPool == null)
        {
            return -1;
        }

        GameObject marker = anchorPool.Get();
        Vector3 localPosition = ToLocalPosition(position);
        marker.transform.localPosition = localPosition;
        marker.transform.localRotation = Quaternion.identity;
        marker.transform.localScale = Vector3.one * resolvedMarkerScale;

        activeAnchors.Add(marker);
        anchorPositions.Add(localPosition);
        int index = anchorPositions.Count - 1;
        anchorLookup[key] = index;
        return index;
    }

    private void SpawnControlMarker(Vector2 position)
    {
        if (controlPool == null)
        {
            return;
        }

        Vector2Int key = Quantize(position);
        if (!controlCache.Add(key))
        {
            return;
        }

        GameObject marker = controlPool.Get();
        Vector3 localPosition = ToLocalPosition(position);
        marker.transform.localPosition = localPosition;
        marker.transform.localRotation = Quaternion.identity;
        marker.transform.localScale = Vector3.one * (resolvedMarkerScale * ControlScaleFactor);
        activeControls.Add(marker);
    }

    private void AppendSegmentSamples(Segment segment, bool includeStartPoint)
    {
        if (includeStartPoint)
        {
            sampleBuffer.Add(ToLocalPosition(segment.a));
        }

        int steps = Mathf.Max(2, samplesPerSegment);
        float invSteps = 1f / steps;
        for (int i = 1; i <= steps; i++)
        {
            float t = i * invSteps;
            Vector2 sample = EvaluateQuadratic(segment.a, segment.ctrl, segment.b, t);
            sampleBuffer.Add(ToLocalPosition(sample));
        }
    }

    private Vector3 ToLocalPosition(Vector2 source)
    {
        float scale = resolvedCoordinateScale > 0f ? resolvedCoordinateScale : coordinateScale;
        float appliedScale = scale * letterScaleMultiplier;
        float x = (source.x - dataCenter.x) * appliedScale;
        float z = (dataCenter.y - source.y) * appliedScale;
        return new Vector3(x, depthOffset, z);
    }

    private bool TryComputeDataBounds(Stroke[] strokes)
    {
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        bool hasPoint = false;

        for (int i = 0; i < strokes.Length; i++)
        {
            Segment[] segments = strokes[i]?.segments;
            if (segments == null)
            {
                continue;
            }

            for (int j = 0; j < segments.Length; j++)
            {
                Segment seg = segments[j];
                minX = Mathf.Min(minX, seg.a.x, seg.b.x, seg.ctrl.x);
                minY = Mathf.Min(minY, seg.a.y, seg.b.y, seg.ctrl.y);
                maxX = Mathf.Max(maxX, seg.a.x, seg.b.x, seg.ctrl.x);
                maxY = Mathf.Max(maxY, seg.a.y, seg.b.y, seg.ctrl.y);
                hasPoint = true;
            }
        }

        if (!hasPoint)
        {
            return false;
        }

        dataMin = new Vector2(minX, minY);
        dataMax = new Vector2(maxX, maxY);
        dataCenter = (dataMin + dataMax) * 0.5f;
        return true;
    }

    private void CachePlaneMetrics()
    {
        Transform root = contentRoot != null ? contentRoot : transform;

        if (root.TryGetComponent(out MeshFilter meshFilter) && meshFilter.sharedMesh != null)
        {
            Vector3 meshSize = meshFilter.sharedMesh.bounds.size;
            Vector3 scale = root.lossyScale;
            planeWidth = Mathf.Max(0.0001f, meshSize.x * Mathf.Abs(scale.x));
            planeHeight = Mathf.Max(0.0001f, meshSize.z * Mathf.Abs(scale.z));
        }
        else if (root.TryGetComponent(out Renderer renderer))
        {
            Vector3 size = renderer.bounds.size;
            planeWidth = Mathf.Max(0.0001f, size.x);
            planeHeight = Mathf.Max(0.0001f, size.z);
        }
        else if (root.TryGetComponent(out Collider collider))
        {
            Vector3 size = collider.bounds.size;
            planeWidth = Mathf.Max(0.0001f, size.x);
            planeHeight = Mathf.Max(0.0001f, size.z);
        }
        else
        {
            Vector3 scale = root.lossyScale;
            planeWidth = Mathf.Max(0.0001f, scale.x);
            planeHeight = Mathf.Max(0.0001f, scale.z);
        }
    }

    private void AutoConfigureForSurface(Stroke[] strokes)
    {
        float dataHeight = Mathf.Max(0.0001f, dataMax.y - dataMin.y);
        float scaleByHeight = planeHeight / dataHeight;

        resolvedCoordinateScale = Mathf.Max(scaleByHeight, 0.0001f);
        resolvedMarkerScale = Mathf.Max(DefaultMarkerSize, planeHeight * markerSizeRatio);
        resolvedLineWidth = Mathf.Max(DefaultLineWidth * 0.25f, planeHeight * lineWidthRatio);
        coordinateScale = resolvedCoordinateScale;
    }

    private static Vector2 EvaluateQuadratic(Vector2 a, Vector2 c, Vector2 b, float t)
    {
        float oneMinusT = 1f - t;
        return (oneMinusT * oneMinusT * a) + (2f * oneMinusT * t * c) + (t * t * b);
    }

    private static Vector2Int Quantize(Vector2 position)
    {
        return new Vector2Int(Mathf.RoundToInt(position.x * QuantizeFactor), Mathf.RoundToInt(position.y *
QuantizeFactor));
    }

    private static string NormalizeKey(string letter)
    {
        return string.IsNullOrWhiteSpace(letter) ? string.Empty : letter.Trim().ToLowerInvariant();
    }

    private Stroke[] LoadStrokes(string letter)
    {
        string normalized = NormalizeKey(letter);
        if (string.IsNullOrEmpty(normalized))
        {
            return Array.Empty<Stroke>();
        }

        if (Cache.TryGetValue(normalized, out Stroke[] cached))
        {
            return cached;
        }

        Stroke[] resolved = Array.Empty<Stroke>();
        string resourcePath = ResourcePrefix + normalized;
        TextAsset asset = Resources.Load<TextAsset>(resourcePath);

        if (asset != null)
        {
            try
            {
                resolved = ParseLetterJson(asset.text) ?? Array.Empty<Stroke>();
            }
            catch (Exception ex)
            {
                Debug.LogError($"LetterPathRenderer: Failed to parse letter {normalized}: {ex.Message}");
                resolved = Array.Empty<Stroke>();
            }
        }

        if (resolved.Length == 0)
        {
            Stroke[] fallback = LoadFromSimpleRecording(normalized);
            if (fallback.Length > 0)
            {
                Debug.Log("LetterPathRenderer: Using simple recording fallback for '" + normalized + "' with " + fallback.Length + " stroke(s).");
                resolved = fallback;
            }
            else if (asset == null)
            {
                Debug.LogWarning($"LetterPathRenderer: No 2D or simple recording data found for letter '{normalized}'.");
            }
        }

        Cache[normalized] = resolved;
        return resolved;
    }

    private Stroke[] LoadFromSimpleRecording(string normalized)
    {
        string resourcePath = $"letter3Dpathdata/simple_recording_{normalized.ToUpperInvariant()}";
        TextAsset asset = Resources.Load<TextAsset>(resourcePath);
        if (asset == null || string.IsNullOrWhiteSpace(asset.text))
        {
            return Array.Empty<Stroke>();
        }

        try
        {
            var record = JsonUtility.FromJson<SimpleRecordingDto>(asset.text);
            if (record == null || record.frames == null || record.frames.Length < 2)
            {
                return Array.Empty<Stroke>();
            }

            Debug.Log("LetterPathRenderer: Loaded simple recording '" + normalized + "' frames=" + record.frames.Length);

            Vector3 origin = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            if (record.canvasTransform != null)
            {
                origin = record.canvasTransform.position;
                rotation = record.canvasTransform.rotation;
            }

            Vector3 right = rotation * Vector3.right;
            Vector3 up = rotation * Vector3.up;

            var segments = new Segment[record.frames.Length - 1];
            for (int i = 1; i < record.frames.Length; i++)
            {
                Vector3 relPrev = record.frames[i - 1].position - origin;
                Vector3 relCurr = record.frames[i].position - origin;

                Vector2 a = new Vector2(Vector3.Dot(relPrev, right), Vector3.Dot(relPrev, up));
                Vector2 b = new Vector2(Vector3.Dot(relCurr, right), Vector3.Dot(relCurr, up));
                Vector2 ctrl = (a + b) * 0.5f;

                segments[i - 1] = new Segment
                {
                    a = a,
                    b = b,
                    ctrl = ctrl
                };
            }

            Debug.Log("LetterPathRenderer: simple recording generated " + segments.Length + " segments for letter '" + normalized + "'.");
            var stroke = new Stroke { segments = segments };
            var strokes = new[] { stroke };
            Normalize(strokes);
            return strokes;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"LetterPathRenderer: Failed to parse simple recording fallback for '{normalized}': {ex.Message}");
            return Array.Empty<Stroke>();
        }
    }

    private static Stroke[] ParseLetterJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<Stroke>();

        string trimmed = json.Trim();
        if (trimmed.Length == 0)
            return Array.Empty<Stroke>();

        if (trimmed[0] == '{')
        {
            try
            {
                JObject obj = JObject.Parse(trimmed);
                JToken strokesToken = obj["strokes"];
                if (strokesToken is JArray strokeArray && strokeArray.Count > 0)
                {
                    var strokeList = new List<Stroke>(strokeArray.Count);
                    foreach (JToken strokeTok in strokeArray)
                    {
                        if (strokeTok is JArray segmentArray && segmentArray.Count > 0)
                        {
                            var segments = new Segment[segmentArray.Count];
                            for (int i = 0; i < segmentArray.Count; i++)
                            {
                                segments[i] = segmentArray[i].ToObject<Segment>();
                            }
                            strokeList.Add(new Stroke { segments = segments });
                        }
                        else if (strokeTok.Type == JTokenType.Object)
                        {
                            var strokeObj = strokeTok.ToObject<Stroke>();
                            if (strokeObj != null && strokeObj.segments != null && strokeObj.segments.Length > 0)
                                strokeList.Add(strokeObj);
                        }
                    }

                    if (strokeList.Count > 0)
                    {
                        Stroke[] parsed = strokeList.ToArray();
                        Normalize(parsed);
                        return parsed;
                    }
                }
            }
            catch (Exception)
            {
                // fall through to legacy handling
            }
        }

        try
        {
            StrokeWrapper wrapped = JsonUtility.FromJson<StrokeWrapper>(trimmed);
            if (wrapped != null && wrapped.strokes != null && wrapped.strokes.Length > 0)
            {
                Normalize(wrapped.strokes);
                return wrapped.strokes;
            }
        }
        catch (ArgumentException)
        {
        }

        string converted = ConvertLegacyArray(trimmed);
        if (!string.IsNullOrEmpty(converted))
        {
            StrokeWrapper wrapped = JsonUtility.FromJson<StrokeWrapper>(converted);
            if (wrapped != null && wrapped.strokes != null)
            {
                Normalize(wrapped.strokes);
                return wrapped.strokes;
            }
        }

        throw new FormatException("LetterPathRenderer: JSON did not match expected schema.");
    }

    private static void Normalize(Stroke[] strokes)
    {
        if (strokes == null)
        {
            return;
        }

        for (int i = 0; i < strokes.Length; i++)
        {
            if (strokes[i] == null)
            {
                strokes[i] = new Stroke();
            }
            else if (strokes[i].segments == null)
            {
                strokes[i].segments = Array.Empty<Segment>();
            }
        }
    }

    private static string ConvertLegacyArray(string json)
    {
        string trimmed = json?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '[')
        {
            return null;
        }

        StringBuilder builder = new StringBuilder(trimmed.Length + 32);
        builder.Append("{\"strokes\":[");

        int depth = 0;
        int strokeStart = -1;
        bool firstStroke = true;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '[')
            {
                depth++;
                if (depth == 2)
                {
                    strokeStart = i;
                }
            }
            else if (c == ']')
            {
                if (depth == 2 && strokeStart >= 0)
                {
                    int length = i - strokeStart + 1;
                    if (!firstStroke)
                    {
                        builder.Append(',');
                    }

                    builder.Append("{\"segments\":");
                    builder.Append(trimmed, strokeStart, length);
                    builder.Append('}');

                    firstStroke = false;
                    strokeStart = -1;
                }
                depth--;
            }
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private Material GetPointMaterial()
    {
        if (runtimePointMaterial != null)
        {
            return runtimePointMaterial;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ??
Shader.Find("Standard");
        runtimePointMaterial = new Material(shader)
        {
            color = new Color(1f, 1f, 1f, 0.9f),
            hideFlags = HideFlags.HideAndDontSave
        };
        return runtimePointMaterial;
    }

    private Material GetControlMaterial()
    {
        if (runtimeControlMaterial != null)
        {
            return runtimeControlMaterial;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ??
Shader.Find("Standard");
        runtimeControlMaterial = new Material(shader)
        {
            color = new Color(0.95f, 0.6f, 0.2f, 0.9f),
            hideFlags = HideFlags.HideAndDontSave
        };
        return runtimeControlMaterial;
    }

    private Material GetLineMaterial()
    {
        if (runtimeLineMaterial != null)
        {
            return runtimeLineMaterial;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ??
Shader.Find("Sprites/Default");
        runtimeLineMaterial = new Material(shader)
        {
            color = new Color(1f, 1f, 1f, 0.8f),
            hideFlags = HideFlags.HideAndDontSave
        };
        return runtimeLineMaterial;
    }

    private static void RemoveCollider(GameObject primitive)
    {
        if (primitive.TryGetComponent(out Collider collider))
        {
            collider.enabled = false;
        }
    }

    private static void DisposeMaterial(ref Material material)
    {
        if (material == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(material);
        }
        else
        {
            DestroyImmediate(material);
        }

        material = null;
    }

    [Serializable]
    private class SimpleRecordingDto
    {
        public SimpleFrameDto[] frames;
        public SimpleCanvasTransformDto canvasTransform;
    }

    [Serializable]
    private class SimpleFrameDto
    {
        public Vector3 position;
    }

    [Serializable]
    private class SimpleCanvasTransformDto
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }
}
