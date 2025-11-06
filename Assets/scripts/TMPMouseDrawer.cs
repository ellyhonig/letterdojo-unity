using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class TMPMouseDrawer : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private TMP_Text tmp;          // Assign your TextMeshPro (3D) or TMP component
    [SerializeField] private Camera drawingCamera;  // Assign your camera (falls back to Camera.main)

    [Header("Draw Settings")]
    [Min(0.001f)] public float spacingMeters = 0.1f;     // distance between spheres
    [Min(0.0001f)] public float sphereRadius = 0.02f;    // visual radius
    public GameObject spherePrefab;                      // optional; if null, creates a primitive sphere
    public bool forceScaleToDiameter = true;             // scale spheres to 2 * radius
    public bool restrictToRendererBounds = false;        // requires a Renderer on TMP for clamp

    [Header("Numbering + Undo")]
    public bool showNumberLabel = false;                 // adds a small 3D TMP label on each sphere
    public Vector3 numberLabelLocalOffset = new Vector3(0, 0.02f, 0);
    public KeyCode undoKey = KeyCode.X;                  // erase previous entry

    // --- internals ---
    private Collider tmpCollider;                        // optional: for precise ray hits
    private Renderer tmpRenderer;                        // optional: for bounds clamp
    private readonly List<GameObject> spawned = new();   // stack for undo
    private Vector3 lastPlacedPos;
    private bool hasLast;
    private int nextIndex = 1;

    void Awake()
    {
        if (!drawingCamera) drawingCamera = Camera.main;
        CacheTMPHelpers();
    }

    void OnValidate() => CacheTMPHelpers();

    void Update()
    {
        if (!tmp || !drawingCamera) return;

        // Undo last sphere
        if (Input.GetKeyDown(undoKey))
        {
            UndoLast();
        }

        // Drawing
        if (Input.GetMouseButtonDown(0))
        {
            if (TryGetHit(out var p, out var n))
            {
                SeedStroke(p, n);
            }
        }
        else if (Input.GetMouseButton(0) && hasLast)
        {
            if (TryGetHit(out var p, out var n))
            {
                PlaceAlong(lastPlacedPos, p, n);
            }
        }
        else if (Input.GetMouseButtonUp(0))
        {
            hasLast = false;
        }
    }

    void CacheTMPHelpers()
    {
        if (!tmp) return;
        // Try to find a collider on the TMP object or its children
        tmpCollider = tmp.GetComponent<Collider>() ?? tmp.GetComponentInChildren<Collider>();
        // Grab a renderer for optional bounds clamp
        tmpRenderer = tmp.GetComponent<Renderer>() ?? tmp.GetComponentInChildren<Renderer>();
    }

    void SeedStroke(Vector3 p, Vector3 n)
    {
        PlaceSphere(p, n);
        lastPlacedPos = p;
        hasLast = true;
    }

    void PlaceAlong(Vector3 from, Vector3 to, Vector3 surfaceNormal)
    {
        Vector3 delta = to - from;
        float dist = delta.magnitude;
        if (dist < spacingMeters) return;

        Vector3 dir = delta / dist;
        // Place at fixed increments even if mouse moved far this frame
        while (dist >= spacingMeters)
        {
            Vector3 next = from + dir * spacingMeters;
            PlaceSphere(next, surfaceNormal);
            from = next;
            dist -= spacingMeters;
        }

        lastPlacedPos = from; // keep leftover so spacing stays consistent
    }

    void PlaceSphere(Vector3 worldPointOnPlane, Vector3 surfaceNormal)
    {
        // Ensure normal faces the camera (offset goes toward camera/front)
        Ray ray = drawingCamera.ScreenPointToRay(Input.mousePosition);
        if (Vector3.Dot(surfaceNormal, ray.direction) > 0f) surfaceNormal = -surfaceNormal;

        Vector3 center = worldPointOnPlane + surfaceNormal.normalized * sphereRadius;

        if (restrictToRendererBounds && tmpRenderer != null && !tmpRenderer.bounds.Contains(worldPointOnPlane))
            return;

        GameObject go;
        if (spherePrefab)
        {
            go = Instantiate(spherePrefab, center, Quaternion.identity);
            if (forceScaleToDiameter) go.transform.localScale = Vector3.one * (sphereRadius * 2f);
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.transform.position = center;
            go.transform.localScale = Vector3.one * (sphereRadius * 2f);

            // remove collider so future raycasts don't hit spheres
            var col = go.GetComponent<Collider>();
            if (col) Destroy(col);
        }

        // name + index
        int idx = nextIndex++;
        go.name = $"TMPDrawSphere_{idx:000}";

        // parent under TMP, keep world coords
        go.transform.SetParent(tmp.transform, true);

        // avoid ray-hits on spheres
        int ignore = LayerMask.NameToLayer("Ignore Raycast");
        if (ignore >= 0) go.layer = ignore;

        // optional number label
        if (showNumberLabel)
        {
            var labelGO = new GameObject("num");
            labelGO.transform.SetParent(go.transform, false);
            labelGO.transform.localPosition = numberLabelLocalOffset;
            labelGO.transform.rotation = Quaternion.LookRotation(drawingCamera.transform.forward, Vector3.up);

            var t = labelGO.AddComponent<TextMeshPro>();
            t.text = idx.ToString();
            t.alignment = TextAlignmentOptions.Center;
            t.enableAutoSizing = true;
            t.fontSizeMin = 0.05f;
            t.fontSizeMax = 0.5f;
        }

        spawned.Add(go);
    }

    void UndoLast()
    {
        if (spawned.Count == 0) return;

        var last = spawned[spawned.Count - 1];
        spawned.RemoveAt(spawned.Count - 1);
        if (last) Destroy(last);

        // keep numbering contiguous for “prev entry” undo
        nextIndex = Mathf.Max(1, nextIndex - 1);

        // reset stroke anchor so spacing doesn’t jump
        hasLast = false;
    }

    bool TryGetHit(out Vector3 point, out Vector3 normal)
    {
        point = default;
        normal = default;
        Ray ray = drawingCamera.ScreenPointToRay(Input.mousePosition);

        // Prefer precise collider hits if available
        if (tmpCollider)
        {
            var hits = Physics.RaycastAll(ray, 1000f, ~0, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var h in hits)
                {
                    if (!h.collider) continue;
                    Transform t = h.collider.transform;
                    if (t == tmp.transform || t.IsChildOf(tmp.transform))
                    {
                        point = h.point;
                        normal = h.normal;
                        return true;
                    }
                }
            }
        }

        // Fallback: infinite plane aligned with TMP
        Vector3 planePoint = tmpRenderer ? tmpRenderer.bounds.center : tmp.transform.position;
        Vector3 planeNormal = tmp.transform.forward.normalized;
        Plane plane = new Plane(planeNormal, planePoint);

        if (plane.Raycast(ray, out float enter))
        {
            point = ray.GetPoint(enter);
            normal = plane.normal;
            return true;
        }

        return false;
    }
}
