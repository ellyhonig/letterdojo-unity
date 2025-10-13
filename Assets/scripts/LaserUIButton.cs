using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class LaserUIButton : MonoBehaviour
{
    public enum ButtonAction { Clear, NextLetter, PreviousLetter, Custom }

    [Header("Laser / Aim")]
    public float hmdDownOffset = 0.20f;
    public float raycastMaxDistance = 6f;
    [Tooltip("If the ray barely misses, still count it if the ray->collider distance <= this")]
    public float lockDistance = 0.06f;

    [Header("Gate (match PlaneSurfaceDrawer)")]
    [Tooltip("Arm length needed to count as extended")]
    public float extendDistanceThreshold = 0.18f;  // set this to Drawer’s value
    [Tooltip("Require a finger to be aligned toward the aim dir")]
    public bool requireExtendedFinger = true;
    [Range(0f,1f)] public float fingerAlignDot = 0.65f;

    [Header("Sticky / Hysteresis")]
    [Tooltip("Keep aim ‘latched’ after ray leaves the button")]
    public float aimStickyTime = 0.6f;
    [Tooltip("Frames of pointing to arm")]
    public int armFrames = 2;
    [Tooltip("Frames of NOT pointing (or not extended) to trigger while armed")]
    public int releaseFrames = 3;

    [Header("FX")]
    public Color laserColor = Color.red;
    public float laserWidth = 0.004f;
    public float selectedScale = 1.18f;
    public float triggerScale = 1.6f;
    public float scaleLerpSpeed = 12f;
    public float triggerPulseTime = 0.22f;

    [Header("Laser Curve")]
    [Tooltip("Render the UI laser as a smooth curve instead of three points")]
    public bool useCurvedLaser = true;
    [Tooltip("Number of segments for the curved laser (if enabled)")]
    [Range(3, 24)] public int laserSegments = 12;
    [Tooltip("How strongly the curve bows toward the controller (0 = straight)")]
    [Range(0f, 0.6f)] public float laserBendAmount = 0.22f;

    [Header("Refs")]
    public PlaneSurfaceDrawer surfaceDrawer;
    public simplePlayer player;
    [SerializeField] private LevelManager levelManager;

    [Header("Actions")]
    [SerializeField] private ButtonAction buttonAction = ButtonAction.Clear;

    // finger model (auto-binds if not set)
    [Serializable] public class FingerJoints { public Transform tip; public Transform prev; }
    [Serializable] public class HandFingers
    {
        public Transform handRoot;
        public FingerJoints index = new FingerJoints();
        public FingerJoints middle = new FingerJoints();
        public FingerJoints ring = new FingerJoints();
        public FingerJoints pinky = new FingerJoints();
    }
    public HandFingers rightFingers = new HandFingers();
    public HandFingers leftFingers  = new HandFingers();

    private LineRenderer _laser;
    private Material _laserMat;
    private Collider _col;
    private Vector3 _baseScale;
    private bool _selected;
    private bool? _lockedHand; // true=right, false=left
    private float _triggerTimer;
    private bool _hasLaserLock;

    // sticky aim
    private float _lastAimTime = -999f;
    private bool _lastAimRight = true;
    private Vector3 _lastHitPoint;

    // hysteresis
    private int _alignFrames, _misalignFrames;

    // actions
    private readonly Dictionary<ButtonAction, Action> _actionMap = new Dictionary<ButtonAction, Action>();
    private Action _customAction;

    public event Action OnTriggered;

    public ButtonAction CurrentAction => buttonAction;

    private static class LaserMutex
    {
        private const float ScoreEpsilon = 1e-4f;
        private static int _frameRight = -1;
        private static float _bestScoreRight = float.NegativeInfinity;
        private static LaserUIButton _winnerRight;

        private static int _frameLeft = -1;
        private static float _bestScoreLeft = float.NegativeInfinity;
        private static LaserUIButton _winnerLeft;

        public static bool TryClaim(LaserUIButton button, bool isRight, float score)
        {
            int frame = Time.frameCount;
            if (isRight)
            {
                if (_frameRight != frame)
                {
                    _frameRight = frame;
                    _bestScoreRight = float.NegativeInfinity;
                    _winnerRight = null;
                }

                if (score > _bestScoreRight + ScoreEpsilon ||
                    (_winnerRight == button && Mathf.Abs(score - _bestScoreRight) <= ScoreEpsilon))
                {
                    _bestScoreRight = score;
                    _winnerRight = button;
                }

                return _winnerRight == button;
            }
            else
            {
                if (_frameLeft != frame)
                {
                    _frameLeft = frame;
                    _bestScoreLeft = float.NegativeInfinity;
                    _winnerLeft = null;
                }

                if (score > _bestScoreLeft + ScoreEpsilon ||
                    (_winnerLeft == button && Mathf.Abs(score - _bestScoreLeft) <= ScoreEpsilon))
                {
                    _bestScoreLeft = score;
                    _winnerLeft = button;
                }

                return _winnerLeft == button;
            }
        }

        public static void Release(LaserUIButton button)
        {
            if (_winnerRight == button)
            {
                _winnerRight = null;
                _bestScoreRight = float.NegativeInfinity;
            }
            if (_winnerLeft == button)
            {
                _winnerLeft = null;
                _bestScoreLeft = float.NegativeInfinity;
            }
        }
    }

    public void RegisterAction(ButtonAction action, Action handler)
    {
        if (handler == null)
        {
            _actionMap.Remove(action);
            return;
        }

        _actionMap[action] = handler;
    }

    public void SetButtonAction(ButtonAction action)
    {
        buttonAction = action;
    }

    public void SetCustomAction(Action handler)
    {
        buttonAction = ButtonAction.Custom;
        _customAction = handler;
    }

    void Awake()
    {
        _col = GetComponent<Collider>();
        _baseScale = transform.localScale;
        if (!surfaceDrawer) surfaceDrawer = FindObjectOfType<PlaneSurfaceDrawer>();
        if (!player) player = FindObjectOfType<simplePlayer>();
        if (!levelManager) levelManager = FindObjectOfType<LevelManager>();

        _actionMap[ButtonAction.Clear] = () => surfaceDrawer?.ClearStrokes();
        _actionMap[ButtonAction.NextLetter] = () => levelManager?.SkipToNextLetterManual();
        _actionMap[ButtonAction.PreviousLetter] = () => levelManager?.ReturnToPreviousLetterManual();

        var go = new GameObject("UIButtonLaser");
        go.transform.SetParent(transform, false);
        _laser = go.AddComponent<LineRenderer>();
        _laser.useWorldSpace = true;
        _laser.textureMode = LineTextureMode.Stretch;
        _laser.numCapVertices = 4;
        _laser.numCornerVertices = 4;
        _laser.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _laser.receiveShadows = false;
        _laser.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        _laser.positionCount = 0;
        var sh = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive") ?? Shader.Find("Sprites/Default");
        _laserMat = new Material(sh) { color = laserColor };
        _laser.sharedMaterial = _laserMat;
        _laser.widthMultiplier = laserWidth;
    }

    void OnDestroy()
    {
        LaserMutex.Release(this);
        if (_laserMat) Destroy(_laserMat);
    }

    void OnDisable()
    {
        LaserMutex.Release(this);
        if (_laser) _laser.positionCount = 0;
    }

    void Update()
    {
        if (!levelManager)
            levelManager = FindObjectOfType<LevelManager>();

        if (player == null || player.hmd == null) { SoftReset(clearLaser:true); return; }

        // 1) Try fresh aim; else we’ll use sticky
        bool gotAim = TryAim(out bool aimRight, out Vector3 hit);
        if (gotAim)
        {
            _lastAimTime = Time.time;
            _lastAimRight = aimRight;
            _lastHitPoint = hit;
            if (!_lockedHand.HasValue) _lockedHand = aimRight;
        }

        bool aiming = (Time.time - _lastAimTime) <= aimStickyTime;
        if (!aiming || !_hasLaserLock) { SoftReset(clearLaser:true); return; }

        // 2) Show laser (even if finger not aligned yet)
        UpdateLaser(_lastHitPoint, _lastAimRight);

        // 3) Gate EXACT like PlaneSurfaceDrawer:
        //    extended = arm length >= threshold
        //    pointing = any finger aligned with aimDir (origin->controller)
        bool isRight = _lockedHand.GetValueOrDefault(true);
        Transform con = isRight ? player.conR?.transform : player.conL?.transform;
        if (con == null) { SoftReset(clearLaser:false); return; }

        Vector3 hmdPos = player.hmd.transform.position;
        Vector3 origin = hmdPos + Vector3.down * hmdDownOffset;
        Vector3 arm = (con.position - origin);
        float armLen = arm.magnitude;
        bool extended = armLen >= Mathf.Max(0.05f, extendDistanceThreshold);
        Vector3 aimDir = armLen > 1e-5f ? (arm / armLen) : Vector3.forward;

        var hf = isRight ? rightFingers : leftFingers;
        AutoBindIfNeeded(hf, isRight);
        Vector3? aimPoint = aiming ? _lastHitPoint : (Vector3?)null;
        bool pointing = IsAnyFingerPointingToward(hf, aimDir, aimPoint, out _);

        bool fingerOk = !requireExtendedFinger || pointing;
        bool gateTriggered = extended && fingerOk;

        if (!_selected)
        {
            if (gateTriggered) _alignFrames++;
            else _alignFrames = 0;
            _misalignFrames = 0;

            if (_alignFrames >= armFrames)
            {
                _selected = true;
                _misalignFrames = 0;
            }
        }
        else
        {
            if (!gateTriggered) _misalignFrames++;
            else _misalignFrames = 0;

            if (_misalignFrames >= releaseFrames)
                Trigger();
        }

        // 5) Scale FX
        Vector3 target = _baseScale;
        if (_triggerTimer > 0f) { target = _baseScale * triggerScale; _triggerTimer -= Time.deltaTime; }
        else if (_selected) target = _baseScale * selectedScale;
        transform.localScale = Vector3.Lerp(transform.localScale, target, Mathf.Clamp01(Time.deltaTime * scaleLerpSpeed));
    }

    bool TryAim(out bool aimRight, out Vector3 hitPoint)
    {
        aimRight = true; hitPoint = Vector3.zero;
        _hasLaserLock = false;
        if (player == null || player.hmd == null) return false;

        bool gotR = TryAimHand(true, out Vector3 hitR, out float scoreR);
        bool gotL = TryAimHand(false, out Vector3 hitL, out float scoreL);

        if (!gotR && !gotL) return false;
        if (gotR && !gotL)
        {
            bool allowed = LaserMutex.TryClaim(this, true, scoreR);
            _hasLaserLock = allowed;
            if (!allowed) return false;
            aimRight = true;
            hitPoint = hitR;
            return true;
        }
        if (gotL && !gotR)
        {
            bool allowed = LaserMutex.TryClaim(this, false, scoreL);
            _hasLaserLock = allowed;
            if (!allowed) return false;
            aimRight = false;
            hitPoint = hitL;
            return true;
        }

        // both hit: choose closer to ray
        Vector3 originR = player.hmd.transform.position + Vector3.down * hmdDownOffset;
        Vector3 dirR = (player.conR.transform.position - originR).normalized;
        float dR = DistancePointToRay(_col.ClosestPoint(hitR), originR, dirR);

        Vector3 originL = player.hmd.transform.position + Vector3.down * hmdDownOffset;
        Vector3 dirL = (player.conL.transform.position - originL).normalized;
        float dL = DistancePointToRay(_col.ClosestPoint(hitL), originL, dirL);

        if (dR <= dL)
        {
            bool allowed = LaserMutex.TryClaim(this, true, scoreR);
            _hasLaserLock = allowed;
            if (!allowed) return false;
            aimRight = true;
            hitPoint = hitR;
        }
        else
        {
            bool allowed = LaserMutex.TryClaim(this, false, scoreL);
            _hasLaserLock = allowed;
            if (!allowed) return false;
            aimRight = false;
            hitPoint = hitL;
        }
        return true;
    }

    bool TryAimHand(bool right, out Vector3 hitPoint, out float score)
    {
        hitPoint = Vector3.zero;
        score = -1f;
        Transform con = right ? player.conR?.transform : player.conL?.transform;
        if (con == null) return false;

        Vector3 origin = player.hmd.transform.position + Vector3.down * hmdDownOffset;
        Vector3 dir = (con.position - origin).normalized;
        Ray ray = new Ray(origin, dir);

        // 1) Exact collider hit
        if (_col.Raycast(ray, out RaycastHit hit, raycastMaxDistance))
        {
            hitPoint = hit.point;
            score = ComputeAlignmentScore(origin, dir, hitPoint);
            return true;
        }

        // 2) Near-lock: if ray passes within lockDistance of collider, use closest point on collider
        Vector3 closest = _col.ClosestPoint(origin + dir * raycastMaxDistance);
        float d = DistancePointToRay(closest, origin, dir);
        if (d <= lockDistance)
        {
            hitPoint = closest;
            score = ComputeAlignmentScore(origin, dir, hitPoint);
            return true;
        }

        return false;
    }

    static float DistancePointToRay(Vector3 point, Vector3 rayOrigin, Vector3 rayDirNorm)
    {
        Vector3 toPoint = point - rayOrigin;
        float t = Mathf.Clamp(Vector3.Dot(toPoint, rayDirNorm), 0f, 9999f);
        Vector3 proj = rayOrigin + rayDirNorm * t;
        return Vector3.Distance(point, proj);
    }

    float ComputeAlignmentScore(Vector3 origin, Vector3 armDirNorm, Vector3 targetPoint)
    {
        Vector3 arm = armDirNorm;
        if (arm.sqrMagnitude < 1e-6f)
            return -1f;
        arm.Normalize();
        Vector3 toTarget = targetPoint - origin;
        if (toTarget.sqrMagnitude < 1e-6f)
            return -1f;
        toTarget.Normalize();
        return Vector3.Dot(arm, toTarget);
    }

    void UpdateLaser(Vector3 hitPoint, bool isRight)
    {
        if (_laser == null) return;

        Vector3 origin = player.hmd.transform.position + Vector3.down * hmdDownOffset;
        Transform controller = isRight ? player.conR?.transform : player.conL?.transform;

        if (useCurvedLaser)
            UpdateCurvedLaser(origin, hitPoint, controller);
        else
            UpdateStraightLaser(origin, hitPoint);

        _laser.startColor = _laser.endColor = laserColor;
        _laser.widthMultiplier = laserWidth;
    }

    void UpdateStraightLaser(Vector3 start, Vector3 end)
    {
        _laser.positionCount = 2;
        _laser.SetPosition(0, start);
        _laser.SetPosition(1, end);
    }

    void UpdateCurvedLaser(Vector3 start, Vector3 end, Transform controller)
    {
        int segs = Mathf.Max(3, laserSegments);
        _laser.positionCount = segs + 1;

        Vector3 mid = (start + end) * 0.5f;
        Vector3 toController = controller ? (controller.position - mid) : (start - mid);
        if (toController.sqrMagnitude < 1e-6f)
            toController = (end - start);
        if (toController.sqrMagnitude < 1e-6f)
            toController = Vector3.up;
        toController.Normalize();

        float dist = Vector3.Distance(start, end);
        Vector3 control = mid + toController * (Mathf.Clamp01(laserBendAmount) * dist);

        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            float u = 1f - t;
            Vector3 p = (u * u) * start + (2f * u * t) * control + (t * t) * end;
            _laser.SetPosition(i, p);
        }
    }

    void Trigger()
    {
        if (surfaceDrawer == null)
            surfaceDrawer = FindObjectOfType<PlaneSurfaceDrawer>();

        Action act = null;
        if (buttonAction == ButtonAction.Custom)
            act = _customAction;
        else
            _actionMap.TryGetValue(buttonAction, out act);

        act?.Invoke();
        OnTriggered?.Invoke();

        _triggerTimer = triggerPulseTime;
        _selected = false;
        _lockedHand = null;
        _alignFrames = _misalignFrames = 0;
        if (_hasLaserLock) LaserMutex.Release(this);
        _hasLaserLock = false;
        // keep laser until sticky window expires
    }

    void SoftReset(bool clearLaser)
    {
        _selected = false;
        _lockedHand = null;
        _alignFrames = _misalignFrames = 0;
        if (_hasLaserLock) LaserMutex.Release(this);
        _hasLaserLock = false;
        if (clearLaser && _laser) _laser.positionCount = 0;
        transform.localScale = Vector3.Lerp(transform.localScale, _baseScale, Mathf.Clamp01(Time.deltaTime * scaleLerpSpeed));
    }

    /* ===== Finger utils (mirror Drawer) ===== */

    void AutoBindIfNeeded(HandFingers hf, bool right)
    {
        if (hf == null) return;

        bool allTips = hf.index?.tip && hf.middle?.tip && hf.ring?.tip && hf.pinky?.tip;
        bool allPrev = hf.index?.prev && hf.middle?.prev && hf.ring?.prev && hf.pinky?.prev;
        if (allTips && allPrev) return;

        Transform root = hf.handRoot;
        if (root == null && player != null)
            root = right ? (player.righthand ? player.righthand.transform : null)
                         : (player.lefthand ? player.lefthand.transform : null);
        if (root == null) return;

        var all = root.GetComponentsInChildren<Transform>(true);

        Transform Find(params string[] tokens)
        {
            foreach (var t in all)
            {
                string n = t.name.ToLowerInvariant();
                bool ok = true;
                foreach (var tok in tokens) if (!n.Contains(tok)) { ok = false; break; }
                if (ok) return t;
            }
            return null;
        }

        void Bind(FingerJoints fj, string finger)
        {
            if (fj == null) return;
            if (!fj.tip)  fj.tip  = Find(finger, "tip") ?? Find(finger, "distal") ?? Find(finger, "end");
            if (!fj.prev) fj.prev = Find(finger, "intermediate") ?? Find(finger, "middle") ?? Find(finger, "proximal");
        }

        Bind(hf.index, "index");
        Bind(hf.middle, "middle");
        Bind(hf.ring, "ring");
        Bind(hf.pinky, "pinky");
    }

    bool IsAnyFingerPointingToward(HandFingers hf, Vector3 aimDir, Vector3? aimPoint, out bool pointingTowardPoint)
    {
        pointingTowardPoint = !aimPoint.HasValue;
        if (hf == null) return true;

        bool anyAvailable = false;
        bool anyAlignedDir = false;
        bool anyAlignedPoint = false;

        void Evaluate(FingerJoints fj)
        {
            if (fj == null) return;
            bool hasTip = fj.tip;
            bool hasPrev = fj.prev;
            if (!hasTip && !hasPrev) return;

            anyAvailable = true;

            Vector3 dir;
            if (hasTip && hasPrev) dir = fj.tip.position - fj.prev.position;
            else if (hasTip) dir = fj.tip.forward;
            else return;
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();

            if (Vector3.Dot(dir, aimDir) >= fingerAlignDot)
                anyAlignedDir = true;

            if (aimPoint.HasValue)
            {
                Vector3 basePoint = hasPrev ? fj.prev.position : fj.tip.position;
                Vector3 toTarget = aimPoint.Value - basePoint;
                if (toTarget.sqrMagnitude >= 1e-6f)
                {
                    toTarget.Normalize();
                    if (Vector3.Dot(dir, toTarget) >= fingerAlignDot)
                        anyAlignedPoint = true;
                }
            }
        }

        Evaluate(hf.index);
        Evaluate(hf.middle);
        Evaluate(hf.ring);
        Evaluate(hf.pinky);

        if (!aimPoint.HasValue)
            pointingTowardPoint = anyAlignedDir;
        else
            pointingTowardPoint = anyAvailable ? anyAlignedPoint : true;

        return anyAvailable ? anyAlignedDir : true;
    }
}
