using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(HandPlaneConstraint))]
public class PlaneSurfaceDrawer : MonoBehaviour
{
    [Header("Stroke Settings")]
    [SerializeField] private float lineWidth = 0.01f;          // meters
    [SerializeField] private float minPointSpacing = 0.005f;   // meters (world)
    [SerializeField] private float surfaceLift = 0.001f;       // meters off plane (world)
    [Tooltip("If the first point of a new segment is far from the last point, start a new stroke anyway.")]
    [SerializeField] private float startNewStrokeGap = 0.03f;  // meters (world)

    [Header("Drawing Filters")]
    [Tooltip("Do not draw when hand is retreating toward the surface; small tolerance to ignore jitter (m)")]
    [SerializeField] private float retractNoDrawEpsilon = 0.0005f; // meters

    [Header("Layers (optional)")]
    [Tooltip("Optional: put strokes on a layer, e.g., 'Board'. Leave empty to keep default.")]
    [SerializeField] private string strokeLayerName = "Board";
    private int strokeLayer = -1;

    // (Removed legacy pop sound logic by request)

    [Header("Visual Lines")]
    [SerializeField] private Color armIdleColor = new Color(1,1,1,0.2f);
    [SerializeField] private Color armExtendedColor = new Color(0.2f,1f,0.2f,0.9f);
    [SerializeField] private Color laserColor = new Color(1f, 0.2f, 0.2f, 0.9f);
    [SerializeField] private float armLineWidth = 0.005f;
    [SerializeField] private float laserLineWidth = 0.0035f;

    [Header("Laser Halo (glow)")]
    [Tooltip("Duplicate a wider, faint line for a glow effect")] [SerializeField]
    private bool useHaloLaser = true;
    [Tooltip("Halo width relative to main laser")] [SerializeField]
    private float haloWidthMultiplier = 2.5f;
    [Tooltip("Halo color (low alpha additive)")] [SerializeField]
    private Color haloColor = new Color(1f, 0.15f, 0.15f, 0.28f);

    [Header("Handedness")]
    [Tooltip("If true, only left hand can charge/draw and show beam")] [SerializeField]
    private bool leftHanded = false;
    [Tooltip("Draw helper line from HMD-down origin to controller (debug)")]
    [SerializeField] private bool showArmHelperLine = false;

    [Header("Charging + Laser FX")]
    [Tooltip("Require charging gesture (fist) before drawing")] [SerializeField]
    private bool requireChargeToDraw = true;
    [Tooltip("Seconds to charge once extended with fist")] [SerializeField]
    private float chargeTime = 1.0f;
    [Tooltip("Hand tint while charging")] [SerializeField]
    private Color chargingHandColor = new Color(1f, 0f, 0f, 1f);
    [Tooltip("Laser color while charging")] [SerializeField]
    private Color laserChargingColor = new Color(0.2f, 1.0f, 0.2f, 0.9f);
    [Tooltip("Laser color while drawing")] [SerializeField]
    private Color laserDrawingColor = new Color(1.0f, 0.4f, 0.2f, 1f);
    [Tooltip("Laser pulse speed while charging")] [SerializeField]
    private float laserPulseSpeed = 4.0f;
    [Tooltip("Laser pulse intensity (0..1)")] [SerializeField, Range(0f,1f)]
    private float laserPulseIntensity = 0.45f;
    [Tooltip("Optional additive laser material (Quest-friendly). If null, fallback is used.")]
    [SerializeField] private Material laserMaterial;
    [Tooltip("Laser width when drawing")] [SerializeField]
    private float laserWidthDrawing = 0.0055f;
    [Tooltip("Laser width when charging")] [SerializeField]
    private float laserWidthCharging = 0.0045f;

    [Header("Hand Pose Gate")]
    [Tooltip("Require at least one non-thumb finger to point toward the board to draw")] [SerializeField]
    private bool requireExtendedFinger = true;
    [Tooltip("Cosine threshold for finger alignment with aim dir (0..1)")] [SerializeField, Range(0f,1f)]
    private float fingerAlignDot = 0.65f; // ~49 degrees
    [Serializable]
    private class FingerJoints { public Transform tip; public Transform prev; }
    [Serializable]
    private class HandFingers
    {
        public Transform handRoot; // optional override; will default to player's hand
        public FingerJoints index = new FingerJoints();
        public FingerJoints middle = new FingerJoints();
        public FingerJoints ring = new FingerJoints();
        public FingerJoints pinky = new FingerJoints();
    }
    [SerializeField] private HandFingers rightFingers = new HandFingers();
    [SerializeField] private HandFingers leftFingers  = new HandFingers();

    [Header("Impact FX")]
    [Tooltip("Optional spark prefab to place at impact while drawing")] [SerializeField]
    private GameObject sparkImpactPrefab;
    [Tooltip("Optional smoke prefab to place at impact while drawing")] [SerializeField]
    private GameObject smokeImpactPrefab;
    [Tooltip("Enable impact FX while drawing")] [SerializeField]
    private bool useImpactFX = true;
    [SerializeField, Tooltip("Seconds between spark burst emits while drawing")]
    private float impactSparkInterval = 0.06f;
    [SerializeField, Tooltip("Particles per spark burst emit")] private int impactSparkCount = 8;

    [Header("Stroke Cooldown")] 
    [Tooltip("Recent stroke color that cools to black")] [SerializeField]
    private Color strokeHotColor = new Color(1f, 0.1f, 0.1f, 1f);
    [SerializeField] private Color strokeColdColor = Color.black;
    [SerializeField, Tooltip("Seconds to fade from hot to cold")] private float strokeCooldownSeconds = 0.25f;

    [Header("Hand Models")]
    [Tooltip("Root of the right hand model (e.g., OpenXRCustomHandPrefab_R)")]
    [SerializeField] private Transform rightHandModelRoot;
    [Tooltip("Root of the left hand model (e.g., OpenXRCustomHandPrefab_L)")]
    [SerializeField] private Transform leftHandModelRoot;
    [SerializeField] private Color triggeredHandColor = Color.red;
    private MaterialPropertyBlock _mpb; // init in Awake to satisfy Unity ctor rules
    private readonly List<Renderer> _rightHandRenderers = new List<Renderer>();
    private readonly List<Renderer> _leftHandRenderers  = new List<Renderer>();
    private bool _rightRenderersCached = false;
    private bool _leftRenderersCached = false;

    [Header("External Laser Beams (optional)")]
    [Tooltip("External bright beam GO for right hand (enabled when charging/drawing if beams enabled)")]
    [SerializeField] private GameObject rightLaserBeamGO;
    [Tooltip("External bright beam GO for left hand (enabled when charging/drawing if beams enabled)")]
    [SerializeField] private GameObject leftLaserBeamGO;
    [Tooltip("Control external beam GO visibility via SetLaserBeamsEnabled or toggle here")] [SerializeField]
    private bool laserBeamsEnabled = false;

    [Header("Beam Visuals")] 
    [Tooltip("Beam tint while charging (cleared when charged/drawing)")]
    [SerializeField] private Color beamChargingColor = Color.red;
    private MaterialPropertyBlock _beamMpb;
    private readonly List<Renderer> _beamRenderersR = new List<Renderer>();
    private readonly List<Renderer> _beamRenderersL = new List<Renderer>();
    private bool _beamCachedR = false, _beamCachedL = false;

    // Stable parent for all strokes under this component's GameObject
    private Transform _strokesRoot;

    // Current active stroke per hand
    private LineRenderer _activeLineR, _activeLineL;

    // NOTE: these are **LOCAL-SPACE** points (relative to _strokesRoot) for LineRenderer
    private readonly List<Vector3> _ptsR = new();
    private readonly List<Vector3> _ptsL = new();

    // Track last penetration depth per hand to detect retreating motion
    private float _lastDepthR = float.NaN;
    private float _lastDepthL = float.NaN;

    void OnValidate()
    {
        strokeLayer = string.IsNullOrEmpty(strokeLayerName) ? -1 : LayerMask.NameToLayer(strokeLayerName);
    }

    void Awake()
    {
        _constraint = GetComponent<HandPlaneConstraint>();

        // Ensure a stable anchor child
        _strokesRoot = transform.Find("__Strokes");
        if (_strokesRoot == null)
        {
            var rootGO = new GameObject("__Strokes");
            rootGO.transform.SetParent(transform, false);
            _strokesRoot = rootGO.transform;
        }

        strokeLayer = string.IsNullOrEmpty(strokeLayerName) ? -1 : LayerMask.NameToLayer(strokeLayerName);

        // Subscribe to penetration edge events
        _constraint.OnPenetrationEnter += HandleEnter;
        _constraint.OnPenetrationExit  += HandleExit;
    }

    void OnDestroy()
    {
        if (_constraint != null)
        {
            _constraint.OnPenetrationEnter -= HandleEnter;
            _constraint.OnPenetrationExit  -= HandleExit;
        }
    }

    // Keep parenting locked even if something re-parents the lines
    void LateUpdate()
    {
        if (_strokesRoot == null || _strokesRoot.parent != transform)
        {
            _strokesRoot = transform.Find("__Strokes");
            if (_strokesRoot == null)
            {
                var rootGO = new GameObject("__Strokes");
                rootGO.transform.SetParent(transform, false);
                _strokesRoot = rootGO.transform;
            }
        }
        if (_activeLineR && _activeLineR.transform.parent != _strokesRoot)
            _activeLineR.transform.SetParent(_strokesRoot, false);
        if (_activeLineL && _activeLineL.transform.parent != _strokesRoot)
            _activeLineL.transform.SetParent(_strokesRoot, false);
    }

    /* =========================
       Stroke lifecycle per hand
       ========================= */

    private void HandleEnter(HandPlaneConstraint.Hand which, Vector3 projectedPointWorld)
    {
        // Always start a fresh stroke on penetration enter (no connecting lines)
        if (which == HandPlaneConstraint.Hand.Right)
        {
            StartNewStroke(ref _activeLineR, _ptsR, "RightStroke");
            AddPointProjected(_ptsR, _activeLineR, projectedPointWorld, forceFirst:true);
            // Initialize depth tracker
            _lastDepthR = ComputePenetrationDepth(GetRightCtrlPos());
        }
        else
        {
            StartNewStroke(ref _activeLineL, _ptsL, "LeftStroke");
            AddPointProjected(_ptsL, _activeLineL, projectedPointWorld, forceFirst:true);
            // Initialize depth tracker
            _lastDepthL = ComputePenetrationDepth(GetLeftCtrlPos());
        }
    }

    private void HandleExit(HandPlaneConstraint.Hand which, Vector3 projectedPointWorld)
    {
        // Do not add a point on exit to avoid drawing during retreat
        if (which == HandPlaneConstraint.Hand.Right)
            _lastDepthR = float.NaN;
        else
            _lastDepthL = float.NaN;
        // Do NOT clear lists here; next penetration starts a fresh stroke anyway.
    }

    void Update()
    {
        if (_constraint.IsConstrained(HandPlaneConstraint.Hand.Right))
        {
            Vector3 ctrlPos = GetRightCtrlPos();
            float depth = ComputePenetrationDepth(ctrlPos);
            bool isRetracting = false;
            if (float.IsNaN(_lastDepthR)) _lastDepthR = depth;
            else if (depth < _lastDepthR - retractNoDrawEpsilon) isRetracting = true;
            _lastDepthR = depth;

            if (!isRetracting)
            {
                Vector3 ctrlWorld = _constraint.ProjectToPlane(ctrlPos);
                AddPointSmart(ref _activeLineR, _ptsR, "RightStroke", ctrlWorld);
            }
        }
        else
        {
            // Not constrained; reset tracker
            _lastDepthR = float.NaN;
        }

        if (_constraint.IsConstrained(HandPlaneConstraint.Hand.Left))
        {
            Vector3 ctrlPos = GetLeftCtrlPos();
            float depth = ComputePenetrationDepth(ctrlPos);
            bool isRetracting = false;
            if (float.IsNaN(_lastDepthL)) _lastDepthL = depth;
            else if (depth < _lastDepthL - retractNoDrawEpsilon) isRetracting = true;
            _lastDepthL = depth;

            if (!isRetracting)
            {
                Vector3 ctrlWorld = _constraint.ProjectToPlane(ctrlPos);
                AddPointSmart(ref _activeLineL, _ptsL, "LeftStroke", ctrlWorld);
            }
        }
        else
        {
            // Not constrained; reset tracker
            _lastDepthL = float.NaN;
        }
    }

    /* =========================
       Helpers
       ========================= */

    private void StartNewStroke(ref LineRenderer line, List<Vector3> pts, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_strokesRoot != null ? _strokesRoot : transform, false);
        if (strokeLayer >= 0) SetLayerRecursive(go, strokeLayer);

        line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = false; // LOCAL SPACE so strokes move with parent (original logic)
        line.textureMode = LineTextureMode.Stretch;
        line.numCapVertices = 4;
        line.numCornerVertices = 4;
        line.widthMultiplier = lineWidth;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        line.material = new Material(Shader.Find("Sprites/Default")) { color = Color.black };
        line.positionCount = 0;

        pts.Clear(); // new segment list (history kept via old GOs)
    }

    private void AddPointSmart(ref LineRenderer line, List<Vector3> pts, string name, Vector3 projectedOnPlaneWorld)
    {
        if (line == null)
            StartNewStroke(ref line, pts, name);

        // world -> lifted world
        Vector3 liftedWorld = projectedOnPlaneWorld + _constraint.PlaneNormal * surfaceLift;

        // If far jump from last point (world), start a new stroke
        if (pts.Count > 0)
        {
            Vector3 lastWorld = _strokesRoot.TransformPoint(pts[^1]); // convert last local -> world
            if ((liftedWorld - lastWorld).sqrMagnitude > startNewStrokeGap * startNewStrokeGap)
            {
                StartNewStroke(ref line, pts, name);
            }
        }

        AddPointProjected(pts, line, liftedWorld, forceFirst:false, inputIsWorld:true);
    }

    // projectedOnPlane param is in **world** coords; we store **local** for the LineRenderer
    private void AddPointProjected(List<Vector3> pts, LineRenderer lr, Vector3 projectedOnPlaneWorld, bool forceFirst, bool inputIsWorld = true)
    {
        Vector3 liftedWorld = projectedOnPlaneWorld + _constraint.PlaneNormal * (inputIsWorld ? 0f : surfaceLift);
        // spacing check in WORLD space so meters mean meters
        if (!forceFirst && pts.Count > 0)
        {
            Vector3 lastWorld = _strokesRoot.TransformPoint(pts[^1]);
            if ((liftedWorld - lastWorld).sqrMagnitude < minPointSpacing * minPointSpacing)
                return;
        }

        // store LOCAL for renderer
        Vector3 local = _strokesRoot.InverseTransformPoint(liftedWorld);
        pts.Add(local);
        lr.positionCount = pts.Count;
        lr.SetPositions(pts.ToArray());
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++)
            SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
    }

    private Vector3 GetRightCtrlPos()
    {
        var spField = typeof(HandPlaneConstraint).GetField("player",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var sp = (simplePlayer)spField?.GetValue(_constraint);
        return sp != null ? sp.conR.transform.position : transform.position;
    }

    private Vector3 GetLeftCtrlPos()
    {
        var spField = typeof(HandPlaneConstraint).GetField("player",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var sp = (simplePlayer)spField?.GetValue(_constraint);
        return sp != null ? sp.conL.transform.position : transform.position;
    }

    private float ComputePenetrationDepth(Vector3 ctrlWorldPos)
    {
        // Mirror HandPlaneConstraint's penetration calculation
        Vector3 normal = _constraint.PlaneNormal;
        Vector3 planePoint = _constraint.transform.position;
        float dist = Vector3.Dot(normal, ctrlWorldPos - planePoint); // signed (positive on normal side)

        // Determine side sign based on HMD
        var spField = typeof(HandPlaneConstraint).GetField("player",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var sp = (simplePlayer)spField?.GetValue(_constraint);
        float sideSign = 1f;
        if (sp != null && sp.hmd != null)
        {
            bool hmdSide = Vector3.Dot(normal, sp.hmd.transform.position - planePoint) > 0f;
            sideSign = hmdSide ? 1f : -1f;
        }

        // Depth > 0 means past the plane away from the HMD
        return -sideSign * dist;
    }

    /* Expose anchors for other scripts (e.g., DictationManager) */
    public Transform StrokesRoot => _strokesRoot;
}
