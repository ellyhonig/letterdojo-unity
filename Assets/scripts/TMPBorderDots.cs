using System.Collections.Generic;
using UnityEngine;
using TMPro;

[ExecuteAlways]
[AddComponentMenu("TextMeshPro/Utilities/TMP Border Dots")]
public class TMPBorderDots : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("World-space TextMeshPro (not UGUI).")]
    public TMP_Text tmp;

    [Header("Generation")]
    [Tooltip("Distance in world units between samples on the outline.")]
    public float spacing = 0.05f;

    [Tooltip("How sharp the contour detection is (0..1). 0.5 is the text edge.")]
    [Range(0.01f, 0.99f)] public float isoThreshold = 0.5f;

    [Tooltip("Oversampling factor. Higher = crisper outline, slower.")]
    [Range(1, 8)] public int oversample = 4;

    [Tooltip("Extra pixels around bounds to avoid clipping.")]
    [Range(0, 64)] public int paddingPixels = 8;

    [Header("Dots (Spheres)")]
    [Tooltip("Optional prefab to use for dots; otherwise a Unity sphere primitive is created.")]
    public GameObject spherePrefab;
    [Tooltip("World size of each sphere. If 0, uses spacing * 0.5.")]
    public float sphereSize = 0f;

    [Header("Squares (Cubes)")]
    [Tooltip("Optional prefab to use for squares; otherwise a Unity cube primitive is created.")]
    public GameObject cubePrefab;
    [Tooltip("World size of each cube. If 0, uses spacing.")]
    public float cubeSize = 0f;

    [Header("Cleanup")]
    public string dotsGroupName = "__TMP_Dots";
    public string squaresGroupName = "__TMP_Squares";

    // Cached last generated points (ordered along contours)
    [SerializeField, HideInInspector] private List<Vector3> lastOrderedPoints = new List<Vector3>();
    [SerializeField, HideInInspector] private List<int> loopStartIndices = new List<int>(); // indices into lastOrderedPoints where a loop begins

    // --- PUBLIC ACTIONS (called by inspector buttons) ---

    public void GenerateDots()
    {
        if (!ValidateTMP()) return;

        // Ensure up-to-date geometry
        tmp.ForceMeshUpdate();

        // Compute bounds in world
        var rend = tmp.GetComponent<Renderer>();
        if (rend == null)
        {
            Debug.LogError("[TMPBorderDots] Missing Renderer on TMP object.");
            return;
        }

        Bounds b = rend.bounds;
        if (b.size.x <= 1e-5f || b.size.y <= 1e-5f)
        {
            Debug.LogWarning("[TMPBorderDots] TMP bounds too small; nothing to trace.");
            return;
        }

        // Decide render resolution based on desired spacing
        // Make pixel size ~ spacing/oversample in world
        float pixelWorld = Mathf.Max(spacing / Mathf.Max(oversample,1), 0.0005f);
        int texW = Mathf.Clamp(Mathf.CeilToInt(b.size.x / pixelWorld), 128, 4096);
        int texH = Mathf.Clamp(Mathf.CeilToInt(b.size.y / pixelWorld), 128, 4096);

        // Render TMP alone to RT
        Texture2D mask = RenderTMPToMask(tmp, b, texW, texH, paddingPixels, out float frustumW, out float frustumH);

        if (mask == null)
        {
            Debug.LogError("[TMPBorderDots] Failed to render mask.");
            return;
        }

        // Build scalar field from luminance (0..1). We take alpha if available; else grayscale.
        float[,] field = ExtractField(mask);

        // Marching Squares to extract contours at isoThreshold
        var contours = MarchingSquares(field, isoThreshold);

        // Map contour points (pixel space) -> world space (in TMP plane)
        var worldContours = new List<List<Vector3>>(contours.Count);
        foreach (var loop in contours)
        {
            var wloop = new List<Vector3>(loop.Count);
            foreach (var p in loop)
                wloop.Add(PixelToWorld(p, texW, texH, b.center, tmp.transform.right, tmp.transform.up, frustumW, frustumH));
            worldContours.Add(wloop);
        }

        // Resample each loop at uniform spacing
        lastOrderedPoints.Clear();
        loopStartIndices.Clear();
        foreach (var loop in worldContours)
        {
            if (loop.Count < 2) continue;
            var res = ResampleLoop(loop, spacing);
            if (res.Count == 0) continue;
            loopStartIndices.Add(lastOrderedPoints.Count);
            lastOrderedPoints.AddRange(res);
        }

        // Spawn dots under TMP
        Transform dotsRoot = EnsureChildGroup(dotsGroupName, clearExisting: true);
        if (dotsRoot == null) return;

        float sSize = (sphereSize > 0f) ? sphereSize : spacing * 0.5f;

        for (int i = 0; i < lastOrderedPoints.Count; i++)
        {
            var p = lastOrderedPoints[i];
            GameObject go = (spherePrefab != null)
                ? Instantiate(spherePrefab, p, Quaternion.identity, dotsRoot)
                : GameObject.CreatePrimitive(PrimitiveType.Sphere);

            if (go.transform.parent == null) go.transform.SetParent(dotsRoot, true);
            go.transform.position = p;
            SetUniformWorldScale(go.transform, sSize);

            // If we created a primitive sphere at runtime in Editor, strip collider to keep scene clean
            var coll = go.GetComponent<Collider>();
            if (coll != null) DestroyImmediate(coll);
        }

        // Clear old squares if any (so user can press "Generate Squares" fresh)
        Transform sq = FindChildGroup(squaresGroupName);
        if (sq != null) DestroyImmediate(sq.gameObject);
    }

    public void GenerateSquaresOverDots()
    {
        if (lastOrderedPoints == null || lastOrderedPoints.Count == 0)
        {
            Debug.LogWarning("[TMPBorderDots] No dots cached. Click 'Generate Dots' first.");
            return;
        }

        Transform squaresRoot = EnsureChildGroup(squaresGroupName, clearExisting: true);
        if (squaresRoot == null) return;

        float qSize = (cubeSize > 0f) ? cubeSize : Mathf.Max(spacing, 1e-4f);
        var fwd = tmp.transform.forward; // plane normal
        var R = tmp.transform.right;
        var U = tmp.transform.up;

        // Walk each loop separately to keep orientation smooth + continuous
        int loops = loopStartIndices.Count;
        for (int li = 0; li < loops; li++)
        {
            int start = loopStartIndices[li];
            int end = (li + 1 < loops) ? loopStartIndices[li + 1] : lastOrderedPoints.Count;

            int count = end - start;
            if (count <= 1) continue;

            // unwrap angle for smoothness
            float prevAngle = float.NaN;

            for (int k = 0; k < count; k++)
            {
                int i = start + k;
                int im1 = start + ((k - 1 + count) % count);
                int ip1 = start + ((k + 1) % count);

                Vector3 p = lastOrderedPoints[i];
                Vector3 pm1 = lastOrderedPoints[im1];
                Vector3 pp1 = lastOrderedPoints[ip1];

                // Tangent in plane (use centered difference, normalized)
                Vector3 t = (pp1 - pm1).normalized;

                // Project tangent onto TMP plane to be safe
                t = Vector3.ProjectOnPlane(t, fwd).normalized;
                if (t.sqrMagnitude < 1e-10f) t = R; // fallback

                // Build rotation so that the square's LOCAL +X axis follows the tangent direction,
                // keeping it in the text plane (so adjacent squares "face" each other by edges).
                // We'll do this by computing the signed angle in TMP local XY and rotate around local +Z.
                Vector3 tLocal = tmp.transform.InverseTransformDirection(t);
                float angle = Mathf.Atan2(tLocal.y, tLocal.x) * Mathf.Rad2Deg;

                // unwrap to be smooth w.r.t. previous
                if (!float.IsNaN(prevAngle))
                {
                    float delta = Mathf.DeltaAngle(prevAngle, angle);
                    angle = prevAngle + delta; // smallest change
                }
                prevAngle = angle;

                Quaternion localRot = Quaternion.AngleAxis(angle, Vector3.forward);
                Quaternion worldRot = tmp.transform.rotation * localRot;

                GameObject go = (cubePrefab != null)
                    ? Instantiate(cubePrefab, p, worldRot, squaresRoot)
                    : GameObject.CreatePrimitive(PrimitiveType.Cube);

                if (go.transform.parent == null) go.transform.SetParent(squaresRoot, true);
                go.transform.position = p;
                go.transform.rotation = worldRot;
                SetUniformWorldScale(go.transform, qSize);

                var coll = go.GetComponent<Collider>();
                if (coll != null) DestroyImmediate(coll);
            }
        }
    }

    // --- Helpers ---

    private bool ValidateTMP()
    {
        if (tmp == null)
        {
            Debug.LogError("[TMPBorderDots] Assign a world-space TextMeshPro (TMP_Text) first.");
            return false;
        }
        // We rely on world-space mesh (TextMeshPro), not UGUI
        if (!(tmp is TextMeshPro))
        {
            Debug.LogWarning("[TMPBorderDots] This script targets world-space TextMeshPro. For UGUI, put the text on a World Space Canvas or convert to TextMeshPro (3D).");
        }
        return true;
    }

    private Transform EnsureChildGroup(string groupName, bool clearExisting)
    {
        if (tmp == null) return null;
        Transform group = FindChildGroup(groupName);
        if (group != null && clearExisting)
        {
            for (int i = group.childCount - 1; i >= 0; i--)
                DestroyImmediate(group.GetChild(i).gameObject);
        }
        if (group == null)
        {
            GameObject go = new GameObject(groupName);
            group = go.transform;
            group.SetParent(tmp.transform, false);
            group.localPosition = Vector3.zero;
            group.localRotation = Quaternion.identity;
            group.localScale = Vector3.one;
        }
        return group;
    }

    private Transform FindChildGroup(string groupName)
    {
        if (tmp == null) return null;
        for (int i = 0; i < tmp.transform.childCount; i++)
        {
            var t = tmp.transform.GetChild(i);
            if (t.name == groupName) return t;
        }
        return null;
    }

    private static void SetUniformWorldScale(Transform t, float worldSize)
    {
        // Make it a cube/sphere with worldSize edge/diameter even if parent has scale
        var parent = t.parent;
        Vector3 parentScale = parent ? parent.lossyScale : Vector3.one;
        float inv = 1f / Mathf.Max(1e-6f, (parentScale.x + parentScale.y + parentScale.z) / 3f);
        t.localScale = Vector3.one * (worldSize * inv);
    }

    private Texture2D RenderTMPToMask(TMP_Text tmp, Bounds b, int texW, int texH, int pad, out float frustumW, out float frustumH)
    {
        frustumH = b.size.y;
        frustumW = b.size.x;

        // Adjust for padding: expand camera size slightly so padding fits.
        float padXWorld = (b.size.x / texW) * pad * 2f;
        float padYWorld = (b.size.y / texH) * pad * 2f;
        frustumW += padXWorld;
        frustumH += padYWorld;

        // Ortho camera aligned with TMP
        GameObject camGO = new GameObject("__TMPBorderDots_Cam_TEMP");
        Camera cam = camGO.AddComponent<Camera>();
        cam.enabled = false;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.orthographic = true;
        cam.orthographicSize = frustumH * 0.5f;
        cam.aspect = Mathf.Max(0.0001f, frustumW / Mathf.Max(0.0001f, frustumH));

        // Look straight at TMP plane
        cam.transform.position = b.center - tmp.transform.forward * 2.0f; // small offset in front
        cam.transform.rotation = Quaternion.LookRotation(tmp.transform.forward, tmp.transform.up);
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 10f;

        // Only render the TMP's layer (best effort)
        int layer = tmp.gameObject.layer;
        cam.cullingMask = (1 << layer);

        RenderTexture rt = new RenderTexture(texW + pad * 2, texH + pad * 2, 0, RenderTextureFormat.ARGB32);
        rt.antiAliasing = 1;
        cam.targetTexture = rt;

        // Force render
        cam.Render();

        // Readback
        RenderTexture active = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = active;

        // cleanup
        if (Application.isPlaying)
        {
            Destroy(rt);
            Destroy(camGO);
        }
        else
        {
            DestroyImmediate(rt);
            DestroyImmediate(camGO);
        }

        return tex;
    }

    private static float[,] ExtractField(Texture2D tex)
    {
        int w = tex.width;
        int h = tex.height;
        var pixels = tex.GetPixels32();
        var field = new float[w, h];

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                var c = pixels[row + x];
                // Prefer alpha if present, else luminance
                float a = c.a / 255f;
                float lum = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
                field[x, y] = (a > 0.001f) ? a : lum;
            }
        }
        return field;
    }

    // Marching Squares with asymptotic decider for 5/10 cases
    private static List<List<Vector2>> MarchingSquares(float[,] field, float iso)
    {
        int w = field.GetLength(0);
        int h = field.GetLength(1);

        // Build raw line segments
        var segs = new List<Vector2[]>(256);

        for (int y = 0; y < h - 1; y++)
        {
            for (int x = 0; x < w - 1; x++)
            {
                float a = field[x, y];         // lower-left
                float b = field[x + 1, y];     // lower-right
                float c = field[x + 1, y + 1]; // upper-right
                float d = field[x, y + 1];     // upper-left

                int idx = 0;
                if (a >= iso) idx |= 1;
                if (b >= iso) idx |= 2;
                if (c >= iso) idx |= 4;
                if (d >= iso) idx |= 8;

                if (idx == 0 || idx == 15) continue;

                // Edge interpolation helpers
                Vector2 E(int edge)
                {
                    const float eps = 1e-6f;
                    switch (edge)
                    {
                        case 0: // left: a->d
                            {
                                float t = Mathf.Abs(d - a) < eps ? 0.5f : (iso - a) / (d - a);
                                return new Vector2(x, y + Mathf.Clamp01(t));
                            }
                        case 1: // bottom: a->b
                            {
                                float t = Mathf.Abs(b - a) < eps ? 0.5f : (iso - a) / (b - a);
                                return new Vector2(x + Mathf.Clamp01(t), y);
                            }
                        case 2: // right: b->c
                            {
                                float t = Mathf.Abs(c - b) < eps ? 0.5f : (iso - b) / (c - b);
                                return new Vector2(x + 1, y + Mathf.Clamp01(t));
                            }
                        case 3: // top: d->c
                            {
                                float t = Mathf.Abs(c - d) < eps ? 0.5f : (iso - d) / (c - d);
                                return new Vector2(x + Mathf.Clamp01(t), y + 1);
                            }
                    }
                    return Vector2.zero;
                }

                void Add(int e0, int e1)
                {
                    segs.Add(new[] { E(e0), E(e1) });
                }

                switch (idx)
                {
                    case 1: Add(0, 1); break;
                    case 2: Add(1, 2); break;
                    case 3: Add(0, 2); break;
                    case 4: Add(2, 3); break;

                    case 5:
                        {
                            // Asymptotic decider
                            float s = (a - iso) * (c - iso) - (b - iso) * (d - iso);
                            if (s > 0)
                            {
                                Add(0, 3); // left-top
                                Add(1, 2); // bottom-right
                            }
                            else
                            {
                                Add(0, 1); // left-bottom
                                Add(3, 2); // top-right
                            }
                            break;
                        }

                    case 6: Add(1, 3); break;
                    case 7: Add(0, 3); break;
                    case 8: Add(0, 3); break;
                    case 9: Add(1, 3); break;

                    case 10:
                        {
                            float s = (a - iso) * (c - iso) - (b - iso) * (d - iso);
                            if (s > 0)
                            {
                                Add(0, 1);
                                Add(3, 2);
                            }
                            else
                            {
                                Add(0, 3);
                                Add(1, 2);
                            }
                            break;
                        }

                    case 11: Add(2, 1); break;
                    case 12: Add(2, 0); break;
                    case 13: Add(2, 3); break;
                    case 14: Add(1, 0); break;
                }
            }
        }

        // Chain segments into polylines
        return ChainSegments(segs);
    }

    private static List<List<Vector2>> ChainSegments(List<Vector2[]> segs)
    {
        var loops = new List<List<Vector2>>();
        if (segs.Count == 0) return loops;

        // Hash helper for endpoint merging
        var indexByPoint = new Dictionary<(int, int), List<int>>();
        const float mergeEps = 1e-3f;

        (int, int) Key(Vector2 p) => ((int)Mathf.Round(p.x * 1000f), (int)Mathf.Round(p.y * 1000f));

        for (int i = 0; i < segs.Count; i++)
        {
            var a = segs[i][0];
            var b = segs[i][1];

            var ka = Key(a);
            var kb = Key(b);

            if (!indexByPoint.TryGetValue(ka, out var la)) indexByPoint[ka] = la = new List<int>();
            if (!indexByPoint.TryGetValue(kb, out var lb)) indexByPoint[kb] = lb = new List<int>();
            la.Add(i);
            lb.Add(i);
        }

        var used = new bool[segs.Count];

        for (int i = 0; i < segs.Count; i++)
        {
            if (used[i]) continue;

            var loop = new List<Vector2>();
            // grow from segment i
            var a = segs[i][0];
            var b = segs[i][1];
            used[i] = true;
            loop.Add(a);
            loop.Add(b);

            // forward
            Vector2 end = b;
            while (true)
            {
                var key = Key(end);
                if (!indexByPoint.TryGetValue(key, out var list)) break;
                int next = -1;
                foreach (var idx in list)
                {
                    if (used[idx]) continue;
                    var s = segs[idx];
                    if ((s[0] - end).sqrMagnitude < mergeEps * mergeEps)
                    {
                        next = idx; end = s[1]; break;
                    }
                    if ((s[1] - end).sqrMagnitude < mergeEps * mergeEps)
                    {
                        next = idx; end = s[0]; break;
                    }
                }
                if (next == -1) break;
                used[next] = true;
                loop.Add(end);
                // Stop if closed
                if ((end - loop[0]).sqrMagnitude < mergeEps * mergeEps) break;
            }

            // If not closed, try backward growth
            Vector2 start = loop[0];
            while (true)
            {
                var key = Key(start);
                if (!indexByPoint.TryGetValue(key, out var list)) break;
                int next = -1;
                foreach (var idx in list)
                {
                    if (used[idx]) continue;
                    var s = segs[idx];
                    if ((s[0] - start).sqrMagnitude < mergeEps * mergeEps)
                    {
                        next = idx; start = s[1]; break;
                    }
                    if ((s[1] - start).sqrMagnitude < mergeEps * mergeEps)
                    {
                        next = idx; start = s[0]; break;
                    }
                }
                if (next == -1) break;
                used[next] = true;
                loop.Insert(0, start);
            }

            // Only keep non-trivial
            if (loop.Count >= 2) loops.Add(loop);
        }

        return loops;
    }

    private static Vector3 PixelToWorld(Vector2 px, int texW, int texH, Vector3 center, Vector3 axisRight, Vector3 axisUp, float frustumW, float frustumH)
    {
        // Normalize pixel to [-0.5, 0.5] in each axis
        float nx = (px.x / texW) - 0.5f;
        float ny = (px.y / texH) - 0.5f;
        return center + axisRight * (nx * frustumW) + axisUp * (ny * frustumH);
    }

    private static List<Vector3> ResampleLoop(List<Vector3> loopPoints, float step)
    {
        var res = new List<Vector3>();
        if (loopPoints.Count < 2) return res;

        // Ensure closed (last ~= first)
        bool closed = (loopPoints[0] - loopPoints[loopPoints.Count - 1]).sqrMagnitude < 1e-6f;
        var pts = new List<Vector3>(loopPoints);
        if (!closed) pts.Add(loopPoints[0]); // force closed for sampling along outline

        // Arc-length parameterization
        int n = pts.Count;
        var len = new float[n];
        len[0] = 0f;
        for (int i = 1; i < n; i++)
            len[i] = len[i - 1] + Vector3.Distance(pts[i - 1], pts[i]);

        float total = len[n - 1];
        if (total < 1e-6f) return res;

        int samples = Mathf.Max(1, Mathf.CeilToInt(total / Mathf.Max(step, 1e-6f)));
        float d = 0f;
        for (int s = 0; s < samples; s++)
        {
            float target = (s / (float)samples) * total;
            // find segment
            int i = System.Array.BinarySearch(len, target);
            if (i < 0)
            {
                i = ~i;
                i = Mathf.Clamp(i, 1, n - 1);
            }
            float segLen = Mathf.Max(len[i] - len[i - 1], 1e-6f);
            float t = (target - len[i - 1]) / segLen;
            Vector3 p = Vector3.LerpUnclamped(pts[i - 1], pts[i], t);
            res.Add(p);
        }

        return res;
    }

#if UNITY_EDITOR
    // --------- Custom Inspector ----------
    [UnityEditor.CustomEditor(typeof(TMPBorderDots))]
    public class TMPBorderDotsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var t = (TMPBorderDots)target;

            UnityEditor.EditorGUILayout.Space();

            using (new UnityEditor.EditorGUI.DisabledScope(t.tmp == null))
            {
                if (GUILayout.Button("Generate Dots (Spheres)"))
                {
                    UnityEditor.Undo.RegisterFullObjectHierarchyUndo(t.gameObject, "Generate Dots");
                    t.GenerateDots();
                    UnityEditor.EditorUtility.SetDirty(t);
                }

                using (new UnityEditor.EditorGUI.DisabledScope(t == null))
                {
                    if (GUILayout.Button("Generate Squares Over Dots"))
                    {
                        UnityEditor.Undo.RegisterFullObjectHierarchyUndo(t.gameObject, "Generate Squares");
                        t.GenerateSquaresOverDots();
                        UnityEditor.EditorUtility.SetDirty(t);
                    }
                }
            }

            UnityEditor.EditorGUILayout.HelpBox(
                "Workflow:\n1) Assign a world-space TextMeshPro.\n2) Click 'Generate Dots'.\n3) Click 'Generate Squares Over Dots' (optional).\nAll instances are parented under the TMP.",
                UnityEditor.MessageType.Info);
        }
    }
#endif
}
