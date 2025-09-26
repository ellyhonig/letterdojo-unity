using UnityEngine;
using System.Collections.Generic;
using System;

[RequireComponent(typeof(HandPlaneConstraint))]
public class PlaneSurfaceDrawer : MonoBehaviour
{
    [Header("Stroke Settings")]
    [SerializeField] private float lineWidth = 0.01f;          // meters
    [SerializeField] private float minPointSpacing = 0.003f;   // meters (world) - reduced for smoother strokes
    [SerializeField] private float surfaceLift = 0.001f;       // meters off plane (world)
    [Tooltip("If the first point of a new segment is far from the last point, start a new stroke anyway.")]
    [SerializeField] private float startNewStrokeGap = 0.03f;  // meters (world)
    
    [Header("Paintbrush Quality")]
    [Tooltip("Enable pressure-sensitive width variation for paintbrush feel")]
    [SerializeField] private bool usePressureWidth = true;
    [Tooltip("Maximum width multiplier based on drawing speed")]
    [SerializeField] private float maxWidthMultiplier = 2.5f;
    [Tooltip("Minimum width multiplier based on drawing speed")]
    [SerializeField] private float minWidthMultiplier = 0.3f;
    [Tooltip("Speed threshold for width variation (m/s)")]
    [SerializeField] private float speedThreshold = 0.1f;
    [Tooltip("Additional smoothing for stroke points")]
    [SerializeField] private float strokeSmoothing = 0.15f;
    [Tooltip("Enable stroke width tapering at ends")]
    [SerializeField] private bool useWidthTapering = true;
    [Tooltip("Length of taper at stroke ends (meters)")]
    [SerializeField] private float taperLength = 0.02f;

    [Header("Ray + Lag Drawing")]
    [Tooltip("Meters below HMD for arm-origin")] [SerializeField]
    private float hmdDownOffset = 0.20f;
    [Tooltip("Min distance from HMD-origin to controller to consider the arm extended")] [SerializeField]
    private float extendDistanceThreshold = 0.25f; // fallback if no estimator
    [Tooltip("Max ray distance when aiming at the board")] [SerializeField]
    private float raycastMaxDistance = 3.0f;
    [Tooltip("Smooth time for lagged laser end")] [SerializeField]
    private float lagSmoothTime = 0.25f; // Increased for more paintbrush-like lag
    [Tooltip("Emit a stroke point every X seconds")] [SerializeField]
    private float emitInterval = 0.01f; // 100 Hz - higher frequency for smoother strokes

    [Header("Arm Length Estimator")]
    [Tooltip("Optional: use ArmLengthEstimator to drive the extend trigger distance (50% of best guess)")]
    [SerializeField] private bool useArmLengthEstimator = true;
    [Tooltip("Estimator reference; auto-found if left empty")]
    [SerializeField] private ArmLengthEstimator armEstimator;

    [Header("Advanced (optional)")]
    [Tooltip("Start the laser at the controller instead of the HMD-down origin.")]
    [SerializeField] private bool useControllerLaserOrigin = true;
    [Tooltip("Draw laser as a curved rope from start to end.")]
    [SerializeField] private bool useCurvedLaser = true;
    [Tooltip("Segments used for curved laser")] [SerializeField, Range(3, 32)]
    private int laserSegments = 12;
    [Tooltip("Laser bend amount relative to length (0..1)")] [SerializeField, Range(0f, 0.6f)]
    private float laserBendAmount = 0.22f;
    [Tooltip("Require larger movement before laser tip updates (scale-aware)")]
    [SerializeField] private bool useDeadzoneSmoothing = true;
    [Tooltip("Deadzone fraction of smaller plane dimension (e.g., 0.012 = 1.2%)")] [SerializeField, Range(0.001f, 0.05f)]
    private float laserDeadzoneFraction = 0.012f;
    [Tooltip("Extra lag on sharp turns; scaled by board size")]
    [SerializeField] private bool useTurnSensitiveLag = true;
    [Tooltip("Feature fraction of smaller plane dimension (letters >= this)")] [SerializeField, Range(0.05f, 0.5f)]
    private float featureFraction = 0.20f;


    [Tooltip("Extra SmoothDamp time on sharp turns (seconds)")] [SerializeField]
    private float turnExtraSmoothTime = 0.35f; // Increased for more smoothing
    [Tooltip("Seconds to traverse the smaller plane dimension at base speed")] [SerializeField]
    private float planeTraverseTime = 0.60f; // Slower for more paintbrush feel
    [Tooltip("Keep a single stroke while aimed (no gap-based splits)")]
    [SerializeField] private bool continuousWhileAiming = true;
    [Header("Momentum (optional)")]
    [Tooltip("Enable heavy 'static friction' so starting to move requires a big nudge, while continuing movement is easier.")]
    [SerializeField] private bool useMomentumLag = false;
    [Tooltip("Kick distance to overcome when (re)starting, as a fraction of the board's smaller dimension")]
    [SerializeField, Range(0.02f, 0.2f)] private float momentumKickFraction = 0.08f;
    [Tooltip("After drawing stops, keep the momentum lock for at least this many seconds before it can unlock.")]
    [SerializeField] private float momentumHoldSeconds = 0.20f;


    [Header("Support Hand Gate")]
    [Tooltip("Require the non-drawing hand to hold a helper sphere before drawing starts")]
    [SerializeField] private bool requireSupportHandHold = true;
    [Tooltip("Interpolation between HMD anchor and drawing controller for support sphere position (0 = HMD, 1 = controller)")]
    [SerializeField, Range(0f, 1f)] private float supportSphereLerp = 0.5f;
    [Tooltip("Additional downward offset applied to the support sphere (meters)")]
    [SerializeField] private float supportSphereDownOffset = 0.0f;
    [Tooltip("Base radius of the support sphere (meters)")]
    [SerializeField] private float supportSphereRadius = 0.07f;
    [Tooltip("Seconds the support hand must remain inside the gate (uses 1.5x radius) to enable drawing")]
    [SerializeField] private float supportSphereHoldSeconds = 0.18f;
    [Tooltip("Show a visual sphere to indicate the support-hand gate")]
    [SerializeField] private bool showSupportSphere = true;
    [SerializeField] private Color supportSphereIdleColor = new Color(1f, 0.25f, 0.25f, 0.15f);
    [SerializeField] private Color supportSphereReadyColor = new Color(0.2f, 1f, 0.35f, 0.28f);


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
    private Color laserChargingColor = new Color(1f, 0f, 0f, 0.9f);
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

    // Plane + scene refs
    private HandPlaneConstraint _constraint;
    [SerializeField] private Collider boardCollider; // if null, will try GetComponent<Collider>()
    [SerializeField] private simplePlayer player;    // if null, will attempt to infer

    // Visual helper lines
    private LineRenderer _armLineR, _armLineL;
    private LineRenderer _laserR, _laserL;
    private LineRenderer _laserHaloR, _laserHaloL;

    // Lag state per hand
    private Vector3 _lagPosR, _lagPosL;
    private Vector3 _lagVelR, _lagVelL; // for SmoothDamp
    private bool _lagInitR = false, _lagInitL = false;
    // Snap lag exactly to aim on first frame of Drawing to avoid stray lines
    private bool _snapLagR = false, _snapLagL = false;
    private bool _isDrawingR = false, _isDrawingL = false;
    private float _emitTimerR = 0f, _emitTimerL = 0f;
    // Optional smoothing state
    private Vector3 _stableTargetR, _stableTargetL; // deadzone-stabilized targets
    private bool _hasStableR = false, _hasStableL = false;
    private Vector3 _prevTargetR, _prevTargetL;    // previous target for turn detection
    private Vector3 _prevDeltaR, _prevDeltaL;      // previous delta
    private bool _hasPrevTargetR = false, _hasPrevTargetL = false;
    
    // Paintbrush quality state
    private Vector3 _lastDrawPosR, _lastDrawPosL;  // for speed calculation
    private float _lastDrawTimeR, _lastDrawTimeL;  // for speed calculation
    private Vector3 _smoothedPosR, _smoothedPosL;  // for additional smoothing
    private bool _hasLastDrawPosR = false, _hasLastDrawPosL = false;
    // Momentum lock state
    private bool _momentumLockedR = false, _momentumLockedL = false;
    private float _momentumLockTimeR = -999f, _momentumLockTimeL = -999f;
    private Vector3 _momentumAnchorR, _momentumAnchorL;

    // Support-hand gating state
    private GameObject _supportSphereGO;
    private MeshRenderer _supportSphereRenderer;
    private MaterialPropertyBlock _supportSphereMpb;
    private float _supportHoldTimer = 0f;
    private bool _supportReady = false;

    // Impact FX instances
    private GameObject _sparkR, _sparkL, _smokeR, _smokeL;
    // Stroke cooldown timers
    private float _lastEmitTimeR = -999f, _lastEmitTimeL = -999f;
    // Dot stamp support
    [Header("Dot Stamp")]
    [SerializeField] private bool enableDotStamp = true;
    [SerializeField] private float dotRadius = 0.006f;
    [SerializeField] private float dotMovementThreshold = 0.008f; // meters
    private Vector3 _drawStartPosR, _drawStartPosL;
    private bool _dotStampedR = false, _dotStampedL = false;
    // Impact FX timers
    private float _impactTmrR = 0f, _impactTmrL = 0f;
    // Runtime-created fallback laser material (additive) if none assigned in Inspector
    private Material _laserRuntimeMat;

    // Shared materials to avoid per-instance clones
    private static Material sUnlitLineMat;
    private static Material sAdditiveLineMat;
    private static Material sSpriteBlackMat;
    private static Material sParticleAdditiveMat;
    private static Material sParticleAlphaMat;

    // Cooldown audio throttle
    private float _lastCooldownSoundTimeR = -999f;
    private float _lastCooldownSoundTimeL = -999f;
    private static bool sSharedInited;
    private static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int ID_Color     = Shader.PropertyToID("_Color");

    private static void EnsureSharedMaterials()
    {
        if (sSharedInited) return;
        sSharedInited = true;

        var shUnlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Legacy Shaders/Unlit/Color") ?? Shader.Find("Sprites/Default");
        sUnlitLineMat = new Material(shUnlit) { name = "__Shared_UnlitLine" };
        sUnlitLineMat.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;

        var shAdd = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive") ?? Shader.Find("Sprites/Default");
        sAdditiveLineMat = new Material(shAdd) { name = "__Shared_AdditiveLine" };
        sAdditiveLineMat.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;

        var shSprite = Shader.Find("Sprites/Default") ?? shUnlit;
        sSpriteBlackMat = new Material(shSprite) { name = "__Shared_SpriteBlack" };
        sSpriteBlackMat.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        if (sSpriteBlackMat.HasProperty(ID_BaseColor)) sSpriteBlackMat.SetColor(ID_BaseColor, Color.black);
        if (sSpriteBlackMat.HasProperty(ID_Color))     sSpriteBlackMat.SetColor(ID_Color, Color.black);

        sParticleAdditiveMat = new Material(shAdd) { name = "__Shared_ParticleAdd" };
        sParticleAdditiveMat.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        var shAlpha = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Sprites/Default");
        sParticleAlphaMat = new Material(shAlpha) { name = "__Shared_ParticleAlpha" };
        sParticleAlphaMat.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        if (sParticleAlphaMat.HasProperty(ID_Color)) sParticleAlphaMat.SetColor(ID_Color, new Color(0.2f,0.2f,0.2f,0.25f));
    }

    private void EnsureSupportSphere()
    {
        if (!requireSupportHandHold)
        {
            if (_supportSphereGO && _supportSphereGO.activeSelf) _supportSphereGO.SetActive(false);
            return;
        }

        EnsureSharedMaterials();

        if (_supportSphereGO == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "__SupportHandGate";
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            var col = go.GetComponent<Collider>();
            if (col)
            {
                if (Application.isPlaying) Destroy(col);
                else DestroyImmediate(col);
            }
            _supportSphereGO = go;
            _supportSphereRenderer = go.GetComponent<MeshRenderer>();
            if (_supportSphereRenderer)
            {
                _supportSphereRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _supportSphereRenderer.receiveShadows = false;
                _supportSphereRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                _supportSphereRenderer.sharedMaterial = sParticleAlphaMat;
            }
            _supportSphereMpb = new MaterialPropertyBlock();
        }

        if (_supportSphereGO)
        {
            _supportSphereGO.SetActive(showSupportSphere);
        }
    }

    private void UpdateSupportGate()
    {
        if (!requireSupportHandHold)
        {
            _supportReady = true;
            _supportHoldTimer = 0f;
            if (_supportSphereGO && _supportSphereGO.activeSelf) _supportSphereGO.SetActive(false);
            return;
        }

        if (!player || !player.hmd || !player.conR || !player.conL)
        {
            _supportReady = false;
            _supportHoldTimer = 0f;
            if (_supportSphereGO && _supportSphereGO.activeSelf) _supportSphereGO.SetActive(false);
            return;
        }

        bool drawingHandIsRight = !leftHanded;
        Transform drawingCon = drawingHandIsRight ? player.conR.transform : player.conL.transform;
        Transform supportCon = drawingHandIsRight ? player.conL.transform : player.conR.transform;

        EnsureSupportSphere();

        Vector3 hmdPos = player.hmd.transform.position;
        Vector3 start = hmdPos + Vector3.down * hmdDownOffset;
        Vector3 end = drawingCon.position;
        Vector3 offset = Vector3.down * supportSphereDownOffset;
        Vector3 gateStart = start + offset;
        Vector3 gateEnd = end + offset;
        Vector3 axis = gateEnd - gateStart;
        float length = axis.magnitude;
        if (length < 0.001f) length = 0.001f;
        Vector3 center = gateStart + axis * 0.5f;

        if (_supportSphereGO)
        {
            _supportSphereGO.transform.position = center;
            if (axis.sqrMagnitude > 0.0001f)
                _supportSphereGO.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis.normalized);
            if (_supportSphereGO.activeSelf != showSupportSphere)
                _supportSphereGO.SetActive(showSupportSphere);
            float radius = Mathf.Max(0.005f, supportSphereRadius);
            _supportSphereGO.transform.localScale = new Vector3(radius * 2f, length * 0.5f, radius * 2f);
        }

        float gateRadius = Mathf.Max(0.005f, supportSphereRadius);
        float distance = DistancePointToSegment(supportCon.position + offset, gateStart, gateEnd);
        bool inside = distance <= gateRadius;
        if (inside)
        {
            float holdTarget = Mathf.Max(0f, supportSphereHoldSeconds);
            _supportHoldTimer = Mathf.Min(holdTarget + 0.25f, _supportHoldTimer + Time.deltaTime);
            _supportReady = holdTarget <= 0f || _supportHoldTimer >= holdTarget;
        }
        else
        {
            _supportHoldTimer = 0f;
            _supportReady = false;
        }

        if (_supportSphereRenderer && _supportSphereGO && _supportSphereGO.activeSelf)
        {
            var drawingState = drawingHandIsRight ? _stateR : _stateL;
            _supportSphereMpb ??= new MaterialPropertyBlock();
            _supportSphereMpb.Clear();
            Color gateColor;
            if (drawingState == HandState.Drawing)
            {
                gateColor = Color.red;
            }
            else if (drawingState == HandState.Charging)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 8f);
                gateColor = Color.Lerp(new Color(0.4f, 0f, 0f, 0.2f), Color.red, pulse);
            }
            else if (_supportReady)
            {
                gateColor = supportSphereReadyColor;
            }
            else
            {
                gateColor = supportSphereIdleColor;
            }
            _supportSphereMpb.SetColor(ID_Color, gateColor);
            _supportSphereMpb.SetColor(ID_BaseColor, gateColor);
            _supportSphereRenderer.SetPropertyBlock(_supportSphereMpb);
        }
    }

// Charging/drawing state machine
public enum HandState { Idle, Charging, Charged, Drawing }
private HandState _stateR = HandState.Idle;
private HandState _stateL = HandState.Idle;

public event Action<bool, HandState, Vector3> OnHandStateChanged;
public event Action<bool, Vector3> OnDropPoint;

public Vector3 RightAimWorld => _lagPosR;

private void SetHandState(bool isRight, HandState newState)
{
    HandState previous = isRight ? _stateR : _stateL;
    if (previous == newState) return;

        if (isRight) _stateR = newState;
        else _stateL = newState;

        Vector3 aim = isRight ? _lagPosR : _lagPosL;
        OnHandStateChanged?.Invoke(isRight, newState, aim);
        // Engage momentum lock when leaving Drawing
        if (useMomentumLag)
        {
            HandState prev = previous;
            if (prev == HandState.Drawing && newState != HandState.Drawing)
            {
                if (isRight)
                {
                    _momentumLockedR = true; _momentumAnchorR = _lagPosR; _momentumLockTimeR = Time.time;
                }
                else
                {
                    _momentumLockedL = true; _momentumAnchorL = _lagPosL; _momentumLockTimeL = Time.time;
                }
            }
        }
        if (newState == HandState.Drawing && previous != HandState.Drawing)
        {
            OnDropPoint?.Invoke(isRight, aim);
        }
    }

    private float _chargeTmrR = 0f, _chargeTmrL = 0f;

    [Header("Audio Clips (assign in Inspector)")]
    [SerializeField] private AudioClip chargeUpClip;     // one-shot at charge start
    [SerializeField] private AudioClip chargeDownClip;   // one-shot on abort/retract
    [SerializeField] private AudioClip drawingLoopClip;  // loop while drawing
    [SerializeField] private bool debugAudio = false;
    [Tooltip("Minimum seconds between cooldown (charge-down) sound events")]
    [SerializeField] private float cooldownSoundMinInterval = 0.45f;
    

    void OnValidate()
    {
        strokeLayer = string.IsNullOrEmpty(strokeLayerName) ? -1 : LayerMask.NameToLayer(strokeLayerName);
    }

    /// <summary>
    /// Initializes the PlaneSurfaceDrawer component by setting up materials, constraints, player references,
    /// board collider, arm estimator, stroke anchors, visual helper lines, laser materials, impact effects,
    /// and caches hand and beam renderers. Ensures all required components and references are assigned or created,
    /// and configures visual elements for drawing and interaction on the plane surface.
    /// </summary>
    void Awake()
    {
        EnsureSharedMaterials();
        _constraint = GetComponent<HandPlaneConstraint>();
        _mpb = new MaterialPropertyBlock();
        _beamMpb = new MaterialPropertyBlock();

        // Try to bind player from HandPlaneConstraint private field or FindObject
        if (!player && _constraint)
        {
            var spField = typeof(HandPlaneConstraint).GetField("player",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            player = (simplePlayer)spField?.GetValue(_constraint);
        }
        if (!player) player = FindObjectOfType<simplePlayer>();

        // Board collider (to restrict drawing to plane extents)
        if (!boardCollider) boardCollider = GetComponent<Collider>();
        if (!boardCollider) boardCollider = GetComponentInChildren<Collider>();

        // Optional: auto-find estimator
        if (!armEstimator) armEstimator = FindObjectOfType<ArmLengthEstimator>();

        // Stable anchor for strokes
        _strokesRoot = transform.Find("__Strokes");
        if (_strokesRoot == null)
        {
            var rootGO = new GameObject("__Strokes");
            rootGO.transform.SetParent(transform, false);
            _strokesRoot = rootGO.transform;
        }

        strokeLayer = string.IsNullOrEmpty(strokeLayerName) ? -1 : LayerMask.NameToLayer(strokeLayerName);

        // Visual helper lines
        _armLineR = CreateHelperLine("ArmLine_R", armLineWidth, armIdleColor);
        _armLineL = CreateHelperLine("ArmLine_L", armLineWidth, armIdleColor);
        _laserR   = CreateHelperLine("Laser_R",   laserLineWidth, laserColor);
        _laserL   = CreateHelperLine("Laser_L",   laserLineWidth, laserColor);
        if (useHaloLaser)
        {
            _laserHaloR = CreateHelperLine("LaserHalo_R", laserLineWidth * Mathf.Max(1.1f, haloWidthMultiplier), haloColor);
            _laserHaloL = CreateHelperLine("LaserHalo_L", laserLineWidth * Mathf.Max(1.1f, haloWidthMultiplier), haloColor);
        }
        // Apply additive material to lasers (Inspector material or runtime fallback)
        Material laserMatToUse = null;
        if (laserMaterial)
        {
            laserMatToUse = laserMaterial;
        }
        else
        {
            var sh = Shader.Find("Legacy Shaders/Particles/Additive");
            if (!sh) sh = Shader.Find("Particles/Additive");
            if (!sh) sh = Shader.Find("Sprites/Default");
            _laserRuntimeMat = new Material(sh) { color = laserColor };
            laserMatToUse = _laserRuntimeMat;
        }
        var sharedLaserMat = laserMatToUse ? laserMatToUse : sAdditiveLineMat;
        if (_laserR) _laserR.sharedMaterial = sharedLaserMat;
        if (_laserL) _laserL.sharedMaterial = sharedLaserMat;
        if (_laserHaloR) _laserHaloR.sharedMaterial = sharedLaserMat;
        if (_laserHaloL) _laserHaloL.sharedMaterial = sharedLaserMat;
        SetLineEnabled(_armLineR, false); SetLineEnabled(_armLineL, false);
        SetLineEnabled(_laserR, false);   SetLineEnabled(_laserL, false);
        if (_laserHaloR) SetLineEnabled(_laserHaloR, false);
        if (_laserHaloL) SetLineEnabled(_laserHaloL, false);

        // Instantiate impact FX (use default if prefabs missing)
        if (useImpactFX)
        {
            if (!sparkImpactPrefab) sparkImpactPrefab = CreateDefaultSparkPrefab();
            if (!smokeImpactPrefab) smokeImpactPrefab = CreateDefaultSmokePrefab();
            if (sparkImpactPrefab)
            {
                _sparkR = Instantiate(sparkImpactPrefab, transform); _sparkR.SetActive(false);
                _sparkL = Instantiate(sparkImpactPrefab, transform); _sparkL.SetActive(false);
            }
            if (smokeImpactPrefab)
            {
                _smokeR = Instantiate(smokeImpactPrefab, transform); _smokeR.SetActive(false);
                _smokeL = Instantiate(smokeImpactPrefab, transform); _smokeL.SetActive(false);
            }
        }

        // Cache hand/beam renderers if models are assigned
        CacheHandRenderers();
        CacheBeamRenderers();
    }

    void OnEnable()
    {
        // Reset drawing/lag state to avoid stray points during startup or after resets
        _lagInitR = _lagInitL = false;
        _hasPrevTargetR = _hasPrevTargetL = false;
        _hasStableR = _hasStableL = false;
        _lagVelR = Vector3.zero; _lagVelL = Vector3.zero;
        _isDrawingR = _isDrawingL = false;
        _emitTimerR = _emitTimerL = 0f;
        
        // Reset paintbrush quality state
        _hasLastDrawPosR = _hasLastDrawPosL = false;
        _smoothedPosR = _smoothedPosL = Vector3.zero;

        SetLineEnabled(_laserR, false);
        // Ensure advanced drawing features are active at runtime
        useControllerLaserOrigin = true;
        useCurvedLaser = true;
        useDeadzoneSmoothing = true;
        useTurnSensitiveLag = true;
        continuousWhileAiming = true;

        // Reset charging state and audio counters
        SetHandState(true, HandState.Idle);
        SetHandState(false, HandState.Idle);
        _chargeTmrR = _chargeTmrL = 0f;
        _beamCachedR = _beamCachedL = false;
        CacheBeamRenderers();

        _supportHoldTimer = 0f;
        _supportReady = !requireSupportHandHold;
        if (requireSupportHandHold)
        {
            EnsureSupportSphere();
        }
        else if (_supportSphereGO)
        {
            _supportSphereGO.SetActive(false);
        }
    }

    

    void LateUpdate()
    {
        // Keep parenting locked even if something re-parents the lines
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

    void Update()
    {
        UpdateSupportGate();

        if (leftHanded)
        {
            TickHand(HandPlaneConstraint.Hand.Right); // support hand when left-handed
            TickHand(HandPlaneConstraint.Hand.Left);  // drawing hand
        }
        else
        {
            TickHand(HandPlaneConstraint.Hand.Left);  // support hand
            TickHand(HandPlaneConstraint.Hand.Right); // drawing hand
        }
    }

    private void TickHand(HandPlaneConstraint.Hand which)
    {
        if (!player || !player.hmd || !player.conR || !player.conL || !boardCollider) return;

        // Derive controller & per-hand state
        Transform con = (which == HandPlaneConstraint.Hand.Right) ? player.conR.transform : player.conL.transform;
        bool isRight = which == HandPlaneConstraint.Hand.Right;

        // Origin is slightly below HMD
        Vector3 hmdPos = player.hmd.transform.position;
        Vector3 origin = hmdPos + Vector3.down * hmdDownOffset;
        Vector3 dir = (con.position - origin);
        float armLen = dir.magnitude;
        float threshold = GetExtendThreshold();
        bool extended = armLen >= threshold;
        Vector3 dirN = armLen > 1e-5f ? (dir / armLen) : Vector3.forward;

        // Update arm visual (disabled by default)
        var armLine = isRight ? _armLineR : _armLineL;
        if (showArmHelperLine)
            UpdateHelperLine(armLine, origin, con.position, extended ? armExtendedColor : armIdleColor, extended);
        else
            SetLineEnabled(armLine, false);

        // Finger gate check (independent of aiming so we can tint hands)
        bool pointing = IsAnyFingerPointingToward(dirN, isRight);
        bool fingerOk = !requireExtendedFinger || pointing; // drawing gate
        bool thisHandIsDrawing = leftHanded ? !isRight : isRight;
        bool supportGateSatisfied = !requireSupportHandHold || !thisHandIsDrawing || _supportReady;
        bool gateTriggered = extended && fingerOk && supportGateSatisfied;
        SetHandTriggeredVisual(isRight, gateTriggered);

        // Aim ray: direction is origin->controller (arm), start can be origin or controller
        bool aimedAtBoard = false;
        Vector3 hitPoint = default;
        if (extended)
        {
            Vector3 rayStart = useControllerLaserOrigin ? con.position : origin;
            Ray ray = new Ray(rayStart, dirN);
            if (boardCollider.Raycast(ray, out RaycastHit hit, raycastMaxDistance))
            {
                aimedAtBoard = true;
                hitPoint = hit.point;
            }
        }

        // Update laser visual (shows lag end). Only when aimed
        var laser = isRight ? _laserR : _laserL;
        if (!thisHandIsDrawing)
        {
            SetLineEnabled(laser, false);
            if (useHaloLaser)
            {
                var halo = isRight ? _laserHaloR : _laserHaloL;
                if (halo) SetLineEnabled(halo, false);
            }
            aimedAtBoard = false;
        }

        if (aimedAtBoard)
        {
            // Seed lag position once
            if (isRight && !_lagInitR) { _lagPosR = hitPoint; _lagInitR = true; _lagVelR = Vector3.zero; }
            if (!isRight && !_lagInitL) { _lagPosL = hitPoint; _lagInitL = true; _lagVelL = Vector3.zero; }

            Vector3 lag = isRight ? _lagPosR : _lagPosL;
            Vector3 vel = isRight ? _lagVelR : _lagVelL;
            // Snap lag to aim on first Drawing frame to avoid stray line from previous lag position
            if (isRight && _snapLagR) { if (useMomentumLag && _momentumLockedR) { lag = _momentumAnchorR; } else { lag = hitPoint; } vel = Vector3.zero; _snapLagR = false; _hasStableR = false; _hasPrevTargetR = false; }
            if (!isRight && _snapLagL) { if (useMomentumLag && _momentumLockedL) { lag = _momentumAnchorL; } else { lag = hitPoint; } vel = Vector3.zero; _snapLagL = false; _hasStableL = false; _hasPrevTargetL = false; }

            Vector3 target = hitPoint;
            float planeMinDim = 1f;
            if (useDeadzoneSmoothing || useTurnSensitiveLag)
                planeMinDim = GetPlaneMinDimension();

            if (useDeadzoneSmoothing)
            {
                float dz = Mathf.Max(0.0005f, laserDeadzoneFraction * planeMinDim);
                if (isRight)
                {
                    if (!_hasStableR) { _stableTargetR = hitPoint; _hasStableR = true; }
                    else if ((hitPoint - _stableTargetR).sqrMagnitude >= dz * dz) { _stableTargetR = hitPoint; }
                    target = _stableTargetR;
                }
                else
                {
                    if (!_hasStableL) { _stableTargetL = hitPoint; _hasStableL = true; }
                    else if ((hitPoint - _stableTargetL).sqrMagnitude >= dz * dz) { _stableTargetL = hitPoint; }
                    target = _stableTargetL;
                }
            }


            // Momentum lock: require a large kick movement before the lag target updates after a stop
            if (useMomentumLag)
            {
                float minDim = planeMinDim > 0f ? planeMinDim : GetPlaneMinDimension();
                float kick = Mathf.Max(0.0005f, momentumKickFraction * minDim);
                if (isRight ? _momentumLockedR : _momentumLockedL)
                {
                    Vector3 anchor = isRight ? _momentumAnchorR : _momentumAnchorL;
                    float sinceLock = Time.time - (isRight ? _momentumLockTimeR : _momentumLockTimeL);
                    bool holdDone = sinceLock >= momentumHoldSeconds;
                    float d = Vector3.Distance(hitPoint, anchor);
                    if (holdDone && d >= kick)
                    {
                        if (isRight) _momentumLockedR = false; else _momentumLockedL = false;
                    }
                    else
                    {
                        target = anchor; // pin until a big kick happens
                    }
                }
            }

            if (useTurnSensitiveLag)
            {
                Vector3 prevT = isRight ? _prevTargetR : _prevTargetL;
                Vector3 prevD = isRight ? _prevDeltaR : _prevDeltaL;
                bool hasPrev = isRight ? _hasPrevTargetR : _hasPrevTargetL;
                Vector3 curDelta = hasPrev ? (target - prevT) : Vector3.zero;
                float deltaMag = curDelta.magnitude;
                float turnAngle = (hasPrev && prevD.sqrMagnitude > 1e-9f && deltaMag > 1e-9f) ? Vector3.Angle(prevD, curDelta) : 0f;

                float baseMaxSpeed = planeMinDim / Mathf.Max(0.05f, planeTraverseTime); // m/s
                float speed = (deltaMag > 0f && Time.deltaTime > 0f) ? (deltaMag / Time.deltaTime) : 0f;
                float speedNorm = Mathf.Clamp01(baseMaxSpeed > 1e-5f ? (speed / baseMaxSpeed) : 0f);
                float turnBoost = Mathf.InverseLerp(15f, 90f, turnAngle);
                float slowFactor = 1f - speedNorm;
                float boost = Mathf.Clamp01(turnBoost * slowFactor);

                float smoothTimeDyn = Mathf.Max(0.01f, lagSmoothTime + turnExtraSmoothTime * boost);
                float maxSpeedDyn = Mathf.Lerp(baseMaxSpeed, baseMaxSpeed * 0.35f, boost);
                lag = Vector3.SmoothDamp(lag, target, ref vel, smoothTimeDyn, maxSpeedDyn, Time.deltaTime);

                if (isRight) { _prevDeltaR = curDelta; _prevTargetR = target; _hasPrevTargetR = true; }
                else         { _prevDeltaL = curDelta; _prevTargetL = target; _hasPrevTargetL = true; }
            }
            else
            {
                lag = Vector3.SmoothDamp(lag, target, ref vel, lagSmoothTime);
            }

            if (isRight) { _lagPosR = lag; _lagVelR = vel; } else { _lagPosL = lag; _lagVelL = vel; }

            Vector3 laserStart = useControllerLaserOrigin ? con.position : origin;
            // Choose laser FX based on state
            Color fxColor = laserColor;
            float fxWidth = laserLineWidth;
            float pulse = 1f;
            HandState hs = isRight ? _stateR : _stateL;
            if (requireChargeToDraw)
            {
                if (hs == HandState.Charging)
                {
                    float s = Mathf.Sin(Time.time * laserPulseSpeed);
                    pulse = 1f + laserPulseIntensity * Mathf.Max(0f, s);
                    float p = (isRight ? _chargeTmrR : _chargeTmrL) / Mathf.Max(0.001f, chargeTime);
                    fxColor = Color.Lerp(laserChargingColor, laserDrawingColor, Mathf.Clamp01(p));
                    fxWidth = laserWidthCharging;
                }
                else if (hs == HandState.Drawing || hs == HandState.Charged)
                {
                    pulse = 1.0f;
                    fxColor = laserDrawingColor;
                    fxWidth = laserWidthDrawing;
                }
            }
            if (laser) laser.widthMultiplier = Mathf.Max(0.0008f, fxWidth * pulse);
            if (useCurvedLaser)
                UpdateLaserCurve(laser, laserStart, lag, con, fxColor, true);
            else
                UpdateHelperLine(laser, laserStart, lag, fxColor, true);
            // Halo line for cheap glow
            if (useHaloLaser)
            {
                var halo = isRight ? _laserHaloR : _laserHaloL;
                if (halo)
                {
                    halo.widthMultiplier = (laser ? laser.widthMultiplier : (laserLineWidth * haloWidthMultiplier));
                    if (useCurvedLaser)
                        UpdateLaserCurve(halo, laserStart, lag, con, haloColor, true);
                    else
                        UpdateHelperLine(halo, laserStart, lag, haloColor, true);
                }
            }

            // Update charging/drawing state machine (respect handedness for gating)
            bool handEnabled = thisHandIsDrawing;
            bool supportOk = !requireSupportHandHold || !handEnabled || _supportReady;
            UpdateChargeAndDrawState(isRight, handEnabled && extended, handEnabled && pointing, aimedAtBoard, supportOk);

            // Draw only when allowed (handedness + charged + pointing + aimed)
            bool allowDraw = handEnabled && supportOk && ((!requireChargeToDraw) ? fingerOk : ((isRight ? _stateR : _stateL) == HandState.Drawing));
            if (allowDraw)
            {
                // Robustly ensure the drawing loop is active while drawing
                EnsureDrawingLoop(isRight);
                // Stroke lifecycle
                if (isRight && !_isDrawingR)
                {
                    StartNewStroke(ref _activeLineR, _ptsR, "RightStroke");
                    _isDrawingR = true; _emitTimerR = 0f;
                    StartDrawingLoopOnBeam(true);
                    _drawStartPosR = _lagPosR; _dotStampedR = false;
                }
                if (!isRight && !_isDrawingL)
                {
                    StartNewStroke(ref _activeLineL, _ptsL, "LeftStroke");
                    _isDrawingL = true; _emitTimerL = 0f;
                    StartDrawingLoopOnBeam(false);
                    _drawStartPosL = _lagPosL; _dotStampedL = false;
                }

                // Emit points at fixed cadence (with spacing gate) lifted off the plane
                if (isRight)
                {
                    _emitTimerR += Time.deltaTime;
                    if (_emitTimerR >= emitInterval)
                    {
                        _emitTimerR = 0f;
                        if (!(useMomentumLag && _momentumLockedR)) { AddPointSmart(ref _activeLineR, _ptsR, "RightStroke", _lagPosR, continuousWhileAiming); _lastEmitTimeR = Time.time; }
                        _lastEmitTimeR = Time.time;
                        if (enableDotStamp && !_dotStampedR && Vector3.Distance(_lagPosR, _drawStartPosR) <= dotMovementThreshold)
                        {
                            StampDot(_lagPosR);
                            _dotStampedR = true;
                        }
                    }
                }
                else
                {
                    _emitTimerL += Time.deltaTime;
                    if (_emitTimerL >= emitInterval)
                    {
                        _emitTimerL = 0f;
                        if (!(useMomentumLag && _momentumLockedL)) { AddPointSmart(ref _activeLineL, _ptsL, "LeftStroke", _lagPosL, continuousWhileAiming); _lastEmitTimeL = Time.time; }
                        _lastEmitTimeL = Time.time;
                        if (enableDotStamp && !_dotStampedL && Vector3.Distance(_lagPosL, _drawStartPosL) <= dotMovementThreshold)
                        {
                            StampDot(_lagPosL);
                            _dotStampedL = true;
                        }
                    }
                }
                // Cooldown active line to black
                UpdateActiveLineCooldown(isRight);
                // Impact FX at contact (continuous while drawing)
                if (useImpactFX)
                {
                    var pos = isRight ? _lagPosR : _lagPosL;
                    HandleImpactFX(isRight, pos);
                }
            }
            else
            {
                if (isRight && _isDrawingR) { StopDrawingFully(true); }
                if (!isRight && _isDrawingL) { StopDrawingFully(false); }
                // Disable FX when not drawing
                DisableImpactFX(isRight);
            }
        }
        else
        {
            // Not aimed at board, hide laser line (and halo) but keep external beam state
            SetLineEnabled(laser, false);
            if (useHaloLaser)
            {
                var halo = isRight ? _laserHaloR : _laserHaloL;
                if (halo) SetLineEnabled(halo, false);
            }
            if (isRight && _isDrawingR) { StopDrawingFully(true); }
            if (!isRight && _isDrawingL) { StopDrawingFully(false); }
            // Ensure impact FX are disabled when aim is lost
            DisableImpactFX(isRight);
            if (isRight) { _hasPrevTargetR = false; _hasStableR = false; }
            else         { _hasPrevTargetL = false; _hasStableL = false; }

            // Allow charging only when aimed, so power-up never plays off-board
            bool handEnabled2 = thisHandIsDrawing;
            UpdateChargeAndDrawState(isRight, handEnabled2 && extended, handEnabled2 && pointing, aimedAtBoard:false, supportReady:false);
            // Cooldown even when not drawing
            UpdateActiveLineCooldown(isRight);
        }

        // Ensure external beam GOs stay visible during dictation if enabled (selected hand only)
        if (laserBeamsEnabled)
        {
            bool handEnabled3 = thisHandIsDrawing;
            var go = isRight ? rightLaserBeamGO : leftLaserBeamGO;
            if (go)
            {
                if (handEnabled3) { if (!go.activeSelf) go.SetActive(true); }
                else { if (go.activeSelf) go.SetActive(false); }
            }
        }

        // Place the external beam GO on the HMD->controller ray at arm-length distance
        if (laserBeamsEnabled)
        {
            bool handEnabled4 = thisHandIsDrawing;
            if (!handEnabled4) return;
            Vector3 dirNBeam = dirN.sqrMagnitude > 0f ? dirN : transform.forward;
            float targetDist = armLen;
            if (useArmLengthEstimator && armEstimator != null)
            {
                targetDist = Mathf.Max(0.05f, armEstimator.CurrentBestGuess);
                float hh = armEstimator.CurrentHmdHeight;
                if (hh > 0f) targetDist = Mathf.Min(targetDist, hh);
            }
            Vector3 beamPos = origin + dirNBeam * targetDist;
            var beamGO = isRight ? rightLaserBeamGO : leftLaserBeamGO;
            if (beamGO)
            {
                beamGO.transform.position = beamPos;
                beamGO.transform.rotation = Quaternion.LookRotation(dirNBeam, Vector3.up);
            }
        }
    }

    private float GetExtendThreshold()
    {
        if (useArmLengthEstimator && armEstimator != null)
        {
            float t = armEstimator.CurrentTriggerDistance;
            // As an extra safety net, cap by current HMD height above floor if available
            if (player && player.hmd)
            {
                // ArmLengthEstimator already clamps internally; this is redundant but safe
                float h = armEstimator.CurrentHmdHeight;
                if (h > 0f) t = Mathf.Min(t, h);
            }
            // Ensure a reasonable minimal threshold
            return Mathf.Max(0.05f, t);
        }
        return Mathf.Max(0.05f, extendDistanceThreshold);
    }

    /* =========================
       Helpers
       ========================= */

    private LineRenderer CreateHelperLine(string name, float width, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.textureMode = LineTextureMode.Stretch;
        lr.numCapVertices = 8; // More vertices for smoother caps
        lr.numCornerVertices = 8; // More vertices for smoother corners
        lr.widthMultiplier = width;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        lr.sharedMaterial = sUnlitLineMat;
        lr.startColor = lr.endColor = color;
        lr.positionCount = 0;
        return lr;
    }

    private void UpdateHelperLine(LineRenderer lr, Vector3 a, Vector3 b, Color color, bool enabled)
    {
        if (!lr) return;
        lr.startColor = lr.endColor = color;
        if (!enabled)
        {
            lr.positionCount = 0;
            return;
        }
        lr.positionCount = 2;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);
    }
    private void SetLineEnabled(LineRenderer lr, bool enabled)
    {
        if (!lr) return;
        if (!enabled) lr.positionCount = 0;
    }

private void UpdateChargeAndDrawState(bool isRight, bool extended, bool fingerOk, bool aimedAtBoard, bool supportReady)
    {
        if (!requireChargeToDraw) return;

        bool canCharge = extended && fingerOk && aimedAtBoard && supportReady;
        bool canDraw = extended && fingerOk && aimedAtBoard && supportReady;

        if (isRight)
        {
            switch (_stateR)
            {
            case HandState.Idle:
                if (canCharge)
                {
                    SetHandState(true, HandState.Charging);
                    _chargeTmrR = 0f;
                    SetHandTint(true, chargingHandColor);
                    SetBeamTint(true, beamChargingColor);
                    StartChargeUpOnBeam(true);
                }
                break;
            case HandState.Charging:
                if (!canCharge)
                {
                    SetHandState(true, HandState.Idle);
                    _chargeTmrR = 0f;
                    SetHandTint(true, null);
                    SetBeamTint(true, null);
                    StopChargeUpOnBeam(true);
                }
                else
                {
                    _chargeTmrR += Time.deltaTime;
                    SetHandTint(true, chargingHandColor);
                    SetBeamTint(true, beamChargingColor);
                    if (_chargeTmrR >= Mathf.Max(0.05f, chargeTime))
                    {
                        SetHandState(true, HandState.Drawing);
                        _snapLagR = true;
                        SetHandTint(true, null);
                        SetBeamTint(true, null);
                        StopChargeUpOnBeam(true);
                    }
                }
                break;
                case HandState.Drawing:
                    if (!aimedAtBoard || !extended)
                    {
                        StopDrawingFully(true);
                    }
                    else if (!fingerOk)
                    {
                        if (_isDrawingR) { 
                            _isDrawingR = false; 
                            StopDrawingLoopOnBeam(true);
                            // Clear stroke data to prevent line connection
                            _activeLineR = null;
                            _ptsR.Clear();
                            ResetStrokeSmoothing(true);
                        }
                        PlayCooldownSound(true);
                        SetHandState(true, HandState.Charged);
                        SetHandTint(true, null);
                        SetBeamTint(true, beamChargingColor);
                    }
                    else if (!supportReady)
                    {
                        if (_isDrawingR)
                        {
                            _isDrawingR = false;
                            StopDrawingLoopOnBeam(true);
                            _activeLineR = null;
                            _ptsR.Clear();
                            ResetStrokeSmoothing(true);
                        }
                        PlayCooldownSound(true);
                        SetHandState(true, HandState.Charged);
                        SetHandTint(true, null);
                        SetBeamTint(true, beamChargingColor);
                    }
                    break;
                case HandState.Charged:
                    if (!aimedAtBoard || !extended)
                    {
                        PlayCooldownSound(true);
                        SetHandState(true, HandState.Idle);
                        _chargeTmrR = 0f;
                        SetHandTint(true, null);
                        SetBeamTint(true, null);
                    }
                    else if (fingerOk && supportReady)
                    {
                        SetHandState(true, HandState.Drawing);
                        _snapLagR = true;
                        SetHandTint(true, null);
                        SetBeamTint(true, null);
                    }
                    break;
            }
        }
        else
        {
            switch (_stateL)
            {
            case HandState.Idle:
                if (canCharge)
                {
                    SetHandState(false, HandState.Charging);
                    _chargeTmrL = 0f;
                    SetHandTint(false, chargingHandColor);
                    SetBeamTint(false, beamChargingColor);
                    StartChargeUpOnBeam(false);
                }
                break;
            case HandState.Charging:
                if (!canCharge)
                {
                    SetHandState(false, HandState.Idle);
                    _chargeTmrL = 0f;
                    SetHandTint(false, null);
                    SetBeamTint(false, null);
                    StopChargeUpOnBeam(false);
                }
                else
                {
                    _chargeTmrL += Time.deltaTime;
                    SetHandTint(false, chargingHandColor);
                    SetBeamTint(false, beamChargingColor);
                    if (_chargeTmrL >= Mathf.Max(0.05f, chargeTime))
                    {
                        SetHandState(false, HandState.Drawing);
                        _snapLagL = true;
                        SetHandTint(false, null);
                        SetBeamTint(false, null);
                        StopChargeUpOnBeam(false);
                    }
                }
                break;
                case HandState.Drawing:
                    if (!aimedAtBoard || !extended)
                    {
                        StopDrawingFully(false);
                    }
                    else if (!fingerOk)
                    {
                        if (_isDrawingL) { 
                            _isDrawingL = false; 
                            StopDrawingLoopOnBeam(false);
                            // Clear stroke data to prevent line connection
                            _activeLineL = null;
                            _ptsL.Clear();
                            ResetStrokeSmoothing(false);
                        }
                        PlayCooldownSound(false);
                        SetHandState(false, HandState.Charged);
                        SetHandTint(false, null);
                        SetBeamTint(false, beamChargingColor);
                    }
                    else if (!supportReady)
                    {
                        if (_isDrawingL)
                        {
                            _isDrawingL = false;
                            StopDrawingLoopOnBeam(false);
                            _activeLineL = null;
                            _ptsL.Clear();
                            ResetStrokeSmoothing(false);
                        }
                        PlayCooldownSound(false);
                        SetHandState(false, HandState.Charged);
                        SetHandTint(false, null);
                        SetBeamTint(false, beamChargingColor);
                    }
                    break;
                case HandState.Charged:
                    if (!aimedAtBoard || !extended)
                    {
                        PlayCooldownSound(false);
                        SetHandState(false, HandState.Idle);
                        _chargeTmrL = 0f;
                        SetHandTint(false, null);
                        SetBeamTint(false, null);
                    }
                    else if (fingerOk && supportReady)
                    {
                        SetHandState(false, HandState.Drawing);
                        _snapLagL = true;
                        SetHandTint(false, null);
                        SetBeamTint(false, null);
                    }
                    break;
            }
        }
    }

    private void StopDrawingFully(bool isRight)
    {
        if (isRight)
        {
            if (_isDrawingR) { _isDrawingR = false; StopDrawingLoopOnBeam(true); }
            SetHandState(true, HandState.Idle);
            _chargeTmrR = 0f;
            ResetStrokeSmoothing(true);
        }
        else
        {
            if (_isDrawingL) { _isDrawingL = false; StopDrawingLoopOnBeam(false); }
            SetHandState(false, HandState.Idle);
            _chargeTmrL = 0f;
            ResetStrokeSmoothing(false);
        }
        PlayCooldownSound(isRight);
        SetHandTint(isRight, null);
        SetBeamTint(isRight, null);
        StopChargeUpOnBeam(isRight);
    }


    private void TransitionOnAimLost(ref HandState state, bool isRight, bool extended)
    {
        if (!requireChargeToDraw)
        {
            SetHandTint(isRight, null);
            SetExternalBeamActive(isRight, false);
            return;
        }
        if (!extended)
        {
            if (state == HandState.Charging || state == HandState.Charged || state == HandState.Drawing)
            {
                PlayCooldownSound(isRight);
            }
            SetHandState(isRight, HandState.Idle);
            SetHandTint(isRight, null);
            SetExternalBeamActive(isRight, false);
            StopDrawingLoopOnBeam(isRight);
            SetBeamTint(isRight, null);
        }
        else
        {
            // Still extended: keep charge; stop drawing if needed
            if (state == HandState.Drawing)
            {
                SetHandState(isRight, HandState.Charged);
                StopDrawingLoopOnBeam(isRight);
            }
            // keep beam visible if enabled, and normal mat
            SetExternalBeamActive(isRight, true);
            SetBeamTint(isRight, null);
            SetHandTint(isRight, null);
        }
    }

    private AudioSource GetBeamAudio(bool isRight)
    {
        var go = isRight ? rightLaserBeamGO : leftLaserBeamGO;
        if (!go) return null;
        var src = go.GetComponent<AudioSource>();
        if (!src) src = go.GetComponentInChildren<AudioSource>();
        return src;
    }

    private void PlayOneShotOnBeam(bool isRight, AudioClip clip)
    {
        if (!clip) { if (debugAudio) Debug.LogWarning("[PlaneSurfaceDrawer] Missing one-shot clip"); return; }
        var src = GetBeamAudio(isRight);
        if (!src) { if (debugAudio) Debug.LogWarning($"[PlaneSurfaceDrawer] Missing AudioSource on {(isRight?"Right":"Left")} beam"); return; }
        src.PlayOneShot(clip);
    }

    private void PlayCooldownSound(bool isRight)
    {
        if (!chargeDownClip) return;
        float now = Time.time;
        float last = isRight ? _lastCooldownSoundTimeR : _lastCooldownSoundTimeL;
        if (now - last < Mathf.Max(0f, cooldownSoundMinInterval))
            return;

        PlayOneShotOnBeam(isRight, chargeDownClip);
        if (isRight) _lastCooldownSoundTimeR = now;
        else _lastCooldownSoundTimeL = now;
    }

    private void StartDrawingLoopOnBeam(bool isRight)
    {
        var src = GetBeamAudio(isRight);
        if (!src || !drawingLoopClip) { if (debugAudio) Debug.LogWarning($"[PlaneSurfaceDrawer] Missing {(src==null?"AudioSource":"drawingLoopClip")} for {(isRight?"Right":"Left")} beam"); return; }
        if (src.isPlaying && !src.loop) src.Stop();
        if (src.clip != drawingLoopClip) src.clip = drawingLoopClip;
        src.loop = true;
        if (!src.isPlaying) src.Play();
    }

    private void StopDrawingLoopOnBeam(bool isRight)
    {
        var src = GetBeamAudio(isRight);
        if (!src) return;
        if (src.isPlaying && src.loop) src.Stop();
        src.loop = false;
        if (src.clip == drawingLoopClip) src.clip = null;
    }

    private void EnsureDrawingLoop(bool isRight)
    {
        var src = GetBeamAudio(isRight);
        if (!src || !drawingLoopClip) return;
        if (src.clip != drawingLoopClip) src.clip = drawingLoopClip;
        if (!src.loop) src.loop = true;
        if (!src.isPlaying) src.Play();
    }

    private void StartChargeUpOnBeam(bool isRight)
    {
        var src = GetBeamAudio(isRight);
        if (!src || !chargeUpClip) return;
        if (src.isPlaying && !src.loop) src.Stop();
        src.loop = false;
        src.clip = chargeUpClip;
        src.Play();
    }

    private void StopChargeUpOnBeam(bool isRight)
    {
        var src = GetBeamAudio(isRight);
        if (!src) return;
        if (!src.loop && src.clip == chargeUpClip && src.isPlaying)
        {
            src.Stop();
            src.clip = null;
        }
    }

    // Default impact FX builders (lightweight, Quest-friendly)
    private GameObject CreateDefaultSparkPrefab()
    {
        var go = new GameObject("__DefaultSpark");
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main; main.playOnAwake = false; ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        main.duration = 0.15f; main.loop = false; main.startLifetime = 0.15f; main.startSpeed = 0.4f; main.startSize = 0.01f; main.maxParticles = 64;
        var emission = ps.emission; emission.rateOverTime = 0f; var bursts = new ParticleSystem.Burst[1]; bursts[0] = new ParticleSystem.Burst(0f, 12, 18, 1, 0.01f); emission.SetBursts(bursts);
        var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 20f; shape.radius = 0.005f;
        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.renderMode = ParticleSystemRenderMode.Billboard;
        pr.sharedMaterial = sParticleAdditiveMat;
        go.SetActive(false);
        return go;
    }

    private GameObject CreateDefaultSmokePrefab()
    {
        var go = new GameObject("__DefaultSmoke");
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main; main.playOnAwake = false; ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        main.duration = 0.6f; main.loop = true; main.startLifetime = 0.4f; main.startSpeed = 0.02f; main.startSize = 0.015f; main.maxParticles = 64;
        var emission = ps.emission; emission.rateOverTime = 15f;
        var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.002f;
        var colorOverLifetime = ps.colorOverLifetime; colorOverLifetime.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(new GradientColorKey[] { new GradientColorKey(new Color(0.2f,0.2f,0.2f,1f), 0f), new GradientColorKey(new Color(0.1f,0.1f,0.1f,1f), 1f) },
                     new GradientAlphaKey[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.4f, 0.2f), new GradientAlphaKey(0.0f, 1f) });
        colorOverLifetime.color = grad;
        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.renderMode = ParticleSystemRenderMode.Billboard;
        pr.sharedMaterial = sParticleAlphaMat;
        go.SetActive(false);
        return go;
    }

    private void HandleImpactFX(bool isRight, Vector3 pos)
    {
        var spark = isRight ? _sparkR : _sparkL;
        var smoke = isRight ? _smokeR : _smokeL;
        // Ensure instances exist (prefabs may destroy themselves via Stop Action)
        if (!spark)
        {
            var prefab = sparkImpactPrefab != null ? sparkImpactPrefab : CreateDefaultSparkPrefab();
            if (prefab)
            {
                spark = Instantiate(prefab, transform);
                if (isRight) _sparkR = spark; else _sparkL = spark;
                spark.SetActive(false);
            }
        }
        if (!smoke)
        {
            var prefab2 = smokeImpactPrefab != null ? smokeImpactPrefab : CreateDefaultSmokePrefab();
            if (prefab2)
            {
                smoke = Instantiate(prefab2, transform);
                if (isRight) _smokeR = smoke; else _smokeL = smoke;
                smoke.SetActive(false);
            }
        }

        if (spark)
        {
            spark.transform.position = pos;
            if (!spark.activeSelf) spark.SetActive(true);
            var ps = spark.GetComponent<ParticleSystem>();
            if (ps)
            {
                if (isRight) _impactTmrR += Time.deltaTime; else _impactTmrL += Time.deltaTime;
                float t = isRight ? _impactTmrR : _impactTmrL;
                if (t >= Mathf.Max(0.01f, impactSparkInterval))
                {
                    if (isRight) _impactTmrR = 0f; else _impactTmrL = 0f;
                    ps.Emit(Mathf.Max(1, impactSparkCount));
                }
            }
        }
        if (smoke)
        {
            smoke.transform.position = pos;
            smoke.transform.up = _constraint ? _constraint.PlaneNormal : Vector3.up;
            if (!smoke.activeSelf) smoke.SetActive(true);
            var ps2 = smoke.GetComponent<ParticleSystem>();
            if (ps2 && !ps2.isPlaying) ps2.Play();
        }
    }

    private void DisableImpactFX(bool isRight)
    {
        var spark = isRight ? _sparkR : _sparkL;
        var smoke = isRight ? _smokeR : _smokeL;
        if (spark)
        {
            var ps = spark.GetComponent<ParticleSystem>();
            if (ps) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            spark.SetActive(false);
            if (isRight) _impactTmrR = 0f; else _impactTmrL = 0f;
        }
        if (smoke)
        {
            var ps2 = smoke.GetComponent<ParticleSystem>();
            if (ps2) ps2.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            smoke.SetActive(false);
        }
    }

    private void SetExternalBeamActive(bool isRight, bool active)
    {
        var go = isRight ? rightLaserBeamGO : leftLaserBeamGO;
        if (!go) return;
        if (laserBeamsEnabled)
        {
            if (!go.activeSelf) go.SetActive(true);
        }
        else
        {
            if (go.activeSelf != active) go.SetActive(active);
        }
    }

    public void SetLaserBeamsEnabled(bool enabled)
    {
        laserBeamsEnabled = enabled;
        if (!enabled)
        {
            if (rightLaserBeamGO) rightLaserBeamGO.SetActive(false);
            if (leftLaserBeamGO) leftLaserBeamGO.SetActive(false);
        }
        else
        {
            UpdateBeamHandVisibility();
        }
    }

    private void UpdateActiveLineCooldown(bool isRight)
    {
        float last = isRight ? _lastEmitTimeR : _lastEmitTimeL;
        var lr = isRight ? _activeLineR : _activeLineL;
        if (!lr) return;
        float t = (Time.time - last) / Mathf.Max(0.01f, strokeCooldownSeconds);
        t = Mathf.Clamp01(t);
        Color col = Color.Lerp(strokeHotColor, strokeColdColor, t);
        lr.startColor = lr.endColor = col;
    }

    private void StampDot(Vector3 worldPos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "Dot";
        go.transform.SetParent(_strokesRoot != null ? _strokesRoot : transform, true);
        go.transform.position = worldPos;
        // Orient to plane
        Vector3 n = _constraint ? _constraint.PlaneNormal : transform.forward;
        go.transform.up = n;
        float height = 0.002f;
        go.transform.localScale = new Vector3(dotRadius * 2f, height * 0.5f, dotRadius * 2f);
        var col = go.GetComponent<Collider>(); if (col) Destroy(col);
        var mr = go.GetComponent<MeshRenderer>();
        if (mr)
        {
            mr.sharedMaterial = sSpriteBlackMat;
        }
        if (strokeLayer >= 0) SetLayerRecursive(go, strokeLayer);
    }

    public void SetLeftHanded(bool left)
    {
        leftHanded = left;
        UpdateBeamHandVisibility();
    }

    private void UpdateBeamHandVisibility()
    {
        if (!laserBeamsEnabled)
        {
            if (rightLaserBeamGO) rightLaserBeamGO.SetActive(false);
            if (leftLaserBeamGO) leftLaserBeamGO.SetActive(false);
            return;
        }
        bool enableLeft = leftHanded;
        if (leftLaserBeamGO) leftLaserBeamGO.SetActive(enableLeft);
        if (rightLaserBeamGO) rightLaserBeamGO.SetActive(!enableLeft);
    }

    // Draw a gently curved laser between origin and end, bowing toward the controller
    private void UpdateLaserCurve(LineRenderer lr, Vector3 origin, Vector3 end, Transform controller, Color color, bool enabled)
    {
        if (!lr) return;
        lr.startColor = lr.endColor = color;
        if (!enabled)
        {
            lr.positionCount = 0;
            return;
        }

        int segs = Mathf.Max(3, laserSegments);
        lr.positionCount = segs + 1;
        Vector3 mid = (origin + end) * 0.5f;
        Vector3 toCon = controller ? (controller.position - mid) : (origin - mid);
        float d = Vector3.Distance(origin, end);
        Vector3 ctrl = mid + toCon.normalized * (laserBendAmount * d);

        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            float u = 1f - t;
            Vector3 p = (u*u) * origin + (2f*u*t) * ctrl + (t*t) * end;
            lr.SetPosition(i, p);
        }
    }

    

    private void CacheHandRenderers()
    {
        if (!_rightRenderersCached && rightHandModelRoot)
        {
            _rightHandRenderers.Clear();
            _rightHandRenderers.AddRange(rightHandModelRoot.GetComponentsInChildren<Renderer>(true));
            _rightRenderersCached = true;
        }
        if (!_leftRenderersCached && leftHandModelRoot)
        {
            _leftHandRenderers.Clear();
            _leftHandRenderers.AddRange(leftHandModelRoot.GetComponentsInChildren<Renderer>(true));
            _leftRenderersCached = true;
        }
    }

    // Cache sphere MeshRenderers under controller objects (by name/mesh containing "sphere")
    private readonly List<MeshRenderer> _conSphereR_R = new List<MeshRenderer>();
    private readonly List<MeshRenderer> _conSphereR_L = new List<MeshRenderer>();
    private bool _conSphereCachedR = false, _conSphereCachedL = false;
    private void CacheControllerSphereRenderers()
    {
        if (player && player.conR && !_conSphereCachedR)
        {
            _conSphereR_R.Clear();
            var rends = player.conR.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in rends)
            {
                if (!r) continue;
                var mf = r.GetComponent<MeshFilter>();
                string nm = (r.name + " " + (mf && mf.sharedMesh ? mf.sharedMesh.name : "")).ToLowerInvariant();
                if (nm.Contains("sphere")) _conSphereR_R.Add(r);
            }
            _conSphereCachedR = true;
        }
        if (player && player.conL && !_conSphereCachedL)
        {
            _conSphereR_L.Clear();
            var rends = player.conL.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in rends)
            {
                if (!r) continue;
                var mf = r.GetComponent<MeshFilter>();
                string nm = (r.name + " " + (mf && mf.sharedMesh ? mf.sharedMesh.name : "")).ToLowerInvariant();
                if (nm.Contains("sphere")) _conSphereR_L.Add(r);
            }
            _conSphereCachedL = true;
        }
    }

    public void SetControllerSphereVisible(bool visible)
    {
        CacheControllerSphereRenderers();
        foreach (var r in _conSphereR_R) if (r) r.enabled = visible;
        foreach (var r in _conSphereR_L) if (r) r.enabled = visible;
    }

    private void CacheBeamRenderers()
    {
        if (!_beamCachedR && rightLaserBeamGO)
        {
            _beamRenderersR.Clear();
            _beamRenderersR.AddRange(rightLaserBeamGO.GetComponentsInChildren<Renderer>(true));
            _beamCachedR = true;
        }
        if (!_beamCachedL && leftLaserBeamGO)
        {
            _beamRenderersL.Clear();
            _beamRenderersL.AddRange(leftLaserBeamGO.GetComponentsInChildren<Renderer>(true));
            _beamCachedL = true;
        }
    }

    private void SetHandTriggeredVisual(bool isRight, bool triggered)
    {
        CacheHandRenderers();
        var list = isRight ? _rightHandRenderers : _leftHandRenderers;
        if (list == null || list.Count == 0) return;
        if (triggered)
        {
            _mpb.Clear();
            _mpb.SetColor("_Color", triggeredHandColor);
            _mpb.SetColor("_BaseColor", triggeredHandColor);
            foreach (var r in list) if (r) r.SetPropertyBlock(_mpb);
        }
        else
        {
            foreach (var r in list) if (r) r.SetPropertyBlock(null);
        }
    }

    private void SetHandTint(bool isRight, Color? color)
    {
        CacheHandRenderers();
        var list = isRight ? _rightHandRenderers : _leftHandRenderers;
        if (list == null || list.Count == 0) return;
        if (color.HasValue)
        {
            _mpb.Clear();
            _mpb.SetColor("_Color", color.Value);
            _mpb.SetColor("_BaseColor", color.Value);
            foreach (var r in list) if (r) r.SetPropertyBlock(_mpb);
        }
        else
        {
            foreach (var r in list) if (r) r.SetPropertyBlock(null);
        }
    }

    private void SetBeamTint(bool isRight, Color? color)
    {
        CacheBeamRenderers();
        var list = isRight ? _beamRenderersR : _beamRenderersL;
        if (list == null || list.Count == 0) return;
        if (color.HasValue)
        {
            _beamMpb.Clear();
            _beamMpb.SetColor("_Color", color.Value);
            _beamMpb.SetColor("_BaseColor", color.Value);
            foreach (var r in list) if (r) r.SetPropertyBlock(_beamMpb);
        }
        else
        {
            foreach (var r in list) if (r) r.SetPropertyBlock(null);
        }
    }

    private bool IsAnyFingerPointingToward(Vector3 aimDir, bool isRight)
    {
        var hf = isRight ? rightFingers : leftFingers;
        // Prefer explicitly assigned model roots, then hf.handRoot, then player's hand spheres
        Transform preferredModelRoot = isRight ? rightHandModelRoot : leftHandModelRoot;
        Transform root = preferredModelRoot ? preferredModelRoot : (hf.handRoot ? hf.handRoot : (player ? (isRight ? player.righthand?.transform : player.lefthand?.transform) : null));
        if (root)
        {
            TryAutoBindIfNeeded(hf, root);
        }

        bool CheckFinger(FingerJoints fj)
        {
            if (fj == null) return false;
            Vector3 dir;
            if (fj.tip != null && fj.prev != null)
            {
                dir = (fj.tip.position - fj.prev.position);
            }
            else if (fj.tip != null)
            {
                dir = fj.tip.forward; // best-effort
            }
            else return false;
            if (dir.sqrMagnitude < 1e-6f) return false;
            dir.Normalize();
            return Vector3.Dot(dir, aimDir) >= fingerAlignDot;
        }

        bool anyAvailable = (hf.index != null && (hf.index.tip != null || hf.index.prev != null)) ||
                            (hf.middle != null && (hf.middle.tip != null || hf.middle.prev != null)) ||
                            (hf.ring != null && (hf.ring.tip != null || hf.ring.prev != null)) ||
                            (hf.pinky != null && (hf.pinky.tip != null || hf.pinky.prev != null));

        bool anyAligned = CheckFinger(hf.index) || CheckFinger(hf.middle) || CheckFinger(hf.ring) || CheckFinger(hf.pinky);
        // If we have no finger data, don't block drawing
        return anyAvailable ? anyAligned : true;
    }

    private void TryAutoBindIfNeeded(HandFingers hf, Transform root)
    {
        if (hf != null &&
            hf.index != null && hf.index.tip != null &&
            hf.middle != null && hf.middle.tip != null &&
            hf.ring != null && hf.ring.tip != null &&
            hf.pinky != null && hf.pinky.tip != null)
            return;
        var all = root.GetComponentsInChildren<Transform>(true);
        // Helper local functions for lookup by tokens
        Transform Find(params string[] tokens)
        {
            foreach (var t in all)
            {
                string n = t.name.ToLowerInvariant();
                bool ok = true;
                foreach (var tok in tokens)
                {
                    if (!n.Contains(tok)) { ok = false; break; }
                }
                if (ok) return t;
            }
            return null;
        }

        void BindFinger(FingerJoints fj, string fingerToken)
        {
            if (fj == null) return;
            if (!fj.tip)
            {
                fj.tip = Find(fingerToken, "tip") ?? Find(fingerToken, "distal") ?? Find(fingerToken, "end");
            }
            if (!fj.prev)
            {
                // Prefer intermediate, fallback to proximal
                fj.prev = Find(fingerToken, "intermediate") ?? Find(fingerToken, "middle") ?? Find(fingerToken, "proximal");
            }
        }

        BindFinger(hf.index, "index");
        BindFinger(hf.middle, "middle");
        BindFinger(hf.ring, "ring");
        BindFinger(hf.pinky, "pinky");
    }

    private void StartNewStroke(ref LineRenderer line, List<Vector3> pts, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_strokesRoot != null ? _strokesRoot : transform, false);
        if (strokeLayer >= 0) SetLayerRecursive(go, strokeLayer);

        line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = false; // LOCAL SPACE so strokes move with parent (original logic)
        line.textureMode = LineTextureMode.Stretch;
        line.numCapVertices = 8; // More vertices for smoother caps
        line.numCornerVertices = 8; // More vertices for smoother corners
        line.widthMultiplier = lineWidth;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        EnsureSharedMaterials();
        line.sharedMaterial = sUnlitLineMat;
        line.startColor = line.endColor = strokeHotColor;
        line.positionCount = 0;

        pts.Clear(); // new segment list (history kept via old GOs)
        bool isRight = name.Contains("Right");
        ResetStrokeSmoothing(isRight);
    }

    private void AddPointSmart(ref LineRenderer line, List<Vector3> pts, string name, Vector3 projectedOnPlaneWorld, bool continuousSession = false)
    {
        if (line == null)
            StartNewStroke(ref line, pts, name);

        // Apply additional smoothing for paintbrush feel
        bool isRight = name.Contains("Right");
        Vector3 smoothedWorld = ApplyStrokeSmoothing(projectedOnPlaneWorld, isRight);
        
        // world -> lifted world
        Vector3 liftedWorld = smoothedWorld + _constraint.PlaneNormal * surfaceLift;

        // If far jump from last point (world), start a new stroke (unless continuous)
        if (!continuousSession && pts.Count > 0)
        {
            Vector3 lastWorld = _strokesRoot.TransformPoint(pts[^1]); // convert last local -> world
            if ((liftedWorld - lastWorld).sqrMagnitude > startNewStrokeGap * startNewStrokeGap)
            {
                StartNewStroke(ref line, pts, name);
            }
        }

        // Calculate pressure-sensitive width
        float widthMultiplier = CalculatePressureWidth(smoothedWorld, isRight);
        
        AddPointProjected(pts, line, liftedWorld, forceFirst:false, inputIsWorld:true, widthMultiplier);
    }

    // projectedOnPlane param is in **world** coords; we store **local** for the LineRenderer
    private void AddPointProjected(List<Vector3> pts, LineRenderer lr, Vector3 projectedOnPlaneWorld, bool forceFirst, bool inputIsWorld = true, float widthMultiplier = 1f)
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
        int count = pts.Count;
        lr.positionCount = count;
        lr.SetPosition(count - 1, local);

        if (usePressureWidth)
        {
            lr.widthMultiplier = lineWidth * widthMultiplier;
        }
        else if (!Mathf.Approximately(lr.widthMultiplier, lineWidth))
        {
            lr.widthMultiplier = lineWidth;
        }
    }

    private void ResetStrokeSmoothing(bool isRight)
    {
        if (isRight)
        {
            _hasLastDrawPosR = false;
            _smoothedPosR = Vector3.zero;
            _lastDrawPosR = Vector3.zero;
            _lastDrawTimeR = 0f;
        }
        else
        {
            _hasLastDrawPosL = false;
            _smoothedPosL = Vector3.zero;
            _lastDrawPosL = Vector3.zero;
            _lastDrawTimeL = 0f;
        }
    }

    private Vector3 ApplyStrokeSmoothing(Vector3 worldPos, bool isRight)
    {
        Vector3 currentSmoothed = isRight ? _smoothedPosR : _smoothedPosL;
        bool hasSmoothed = isRight ? _hasLastDrawPosR : _hasLastDrawPosL;
        
        if (!hasSmoothed)
        {
            currentSmoothed = worldPos;
            if (isRight) { _smoothedPosR = currentSmoothed; _hasLastDrawPosR = true; }
            else { _smoothedPosL = currentSmoothed; _hasLastDrawPosL = true; }
            return currentSmoothed;
        }
        
        // Apply smoothing using Lerp for paintbrush feel
        float smoothFactor = Mathf.Clamp01(strokeSmoothing * Time.deltaTime * 60f); // Frame-rate independent
        currentSmoothed = Vector3.Lerp(currentSmoothed, worldPos, smoothFactor);
        
        if (isRight) { _smoothedPosR = currentSmoothed; }
        else { _smoothedPosL = currentSmoothed; }
        
        return currentSmoothed;
    }
    
    private float CalculatePressureWidth(Vector3 worldPos, bool isRight)
    {
        if (!usePressureWidth) return 1f;
        
        Vector3 lastPos = isRight ? _lastDrawPosR : _lastDrawPosL;
        float lastTime = isRight ? _lastDrawTimeR : _lastDrawTimeL;
        bool hasLast = isRight ? _hasLastDrawPosR : _hasLastDrawPosL;
        
        if (!hasLast)
        {
            if (isRight) { _lastDrawPosR = worldPos; _lastDrawTimeR = Time.time; _hasLastDrawPosR = true; }
            else { _lastDrawPosL = worldPos; _lastDrawTimeL = Time.time; _hasLastDrawPosL = true; }
            return 1f;
        }
        
        // Calculate drawing speed
        float deltaTime = Time.time - lastTime;
        if (deltaTime <= 0f) return 1f;
        
        float distance = Vector3.Distance(worldPos, lastPos);
        float speed = distance / deltaTime;
        
        // Map speed to width multiplier (slower = thicker, faster = thinner)
        float speedRatio = Mathf.Clamp01(speed / speedThreshold);
        float widthMultiplier = Mathf.Lerp(maxWidthMultiplier, minWidthMultiplier, speedRatio);
        
        // Update tracking
        if (isRight) { _lastDrawPosR = worldPos; _lastDrawTimeR = Time.time; }
        else { _lastDrawPosL = worldPos; _lastDrawTimeL = Time.time; }
        
        return widthMultiplier;
    }

    private static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        for (int i = 0; i < go.transform.childCount; i++)
            SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
    }

    /* Expose anchors for other scripts (e.g., DictationManager) */
    public Transform StrokesRoot => _strokesRoot;

    /// <summary>
    /// Public API: remove all strokes created under the internal __Strokes root.
    /// Safe to call from other scripts (e.g., UI buttons).
    /// </summary>
    public void ClearStrokes()
    {
        if (_strokesRoot == null) return;
        // Destroy child GameObjects (LineRenderer stroke GOs). Use DestroyImmediate in editor, Destroy at runtime.
        var children = new System.Collections.Generic.List<Transform>();
        for (int i = 0; i < _strokesRoot.childCount; ++i) children.Add(_strokesRoot.GetChild(i));
        foreach (var t in children)
        {
            if (Application.isPlaying) Destroy(t.gameObject);
            else DestroyImmediate(t.gameObject);
        }
    }

    // Compute the smaller in-plane dimension of the drawing surface in meters (world space)
    private static float DistancePointToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        if (a == b) return Vector3.Distance(point, a);
        Vector3 ab = b - a;
        float t = Vector3.Dot(point - a, ab) / Vector3.Dot(ab, ab);
        t = Mathf.Clamp01(t);
        Vector3 closest = a + ab * t;
        return Vector3.Distance(point, closest);
    }

    private float GetPlaneMinDimension()
    {
        if (!boardCollider) return 1f;
        Bounds b = boardCollider.bounds; // world AABB
        Vector3 ex = b.extents;
        Vector3 r = transform.right.normalized;
        Vector3 f = transform.forward.normalized;
        float halfU = Mathf.Abs(r.x) * ex.x + Mathf.Abs(r.y) * ex.y + Mathf.Abs(r.z) * ex.z;
        float halfV = Mathf.Abs(f.x) * ex.x + Mathf.Abs(f.y) * ex.y + Mathf.Abs(f.z) * ex.z;
        float minDim = 2f * Mathf.Min(halfU, halfV);
        return Mathf.Max(minDim, 0.001f);
    }
}




















