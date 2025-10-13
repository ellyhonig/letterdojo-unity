using System;
using UnityEngine;

// Continuously estimates a robust arm-length for use as an extension trigger threshold.
// Designed for SteamVR/OpenXR projects that expose HMD and controller transforms via simplePlayer.
//
// Key behaviors:
// - Updates only when controller-to-origin distance is stable within ±10% for at least 1s
//   AND the controller moved at least a small amount during that window (to avoid "frozen" poses).
// - Clamps any candidate length to not exceed HMD height above floor.
// - Resets the current best guess when HMD height changes > 10% (likely a new user swapping in).
// - Exposes a trigger distance at 85% of the current best guess.
// - Limits sudden changes with a measured, configurable convergence rate.
//
// Attach this to a scene object and assign the simplePlayer reference.
// Consumers (e.g., PlaneSurfaceDrawer) can read CurrentTriggerDistance to gate extension.
public class ArmLengthEstimator : MonoBehaviour
{
    [Header("Player Refs")]
    [Tooltip("Provides HMD + controller transforms")] public simplePlayer player;

    [Header("Origin From HMD")]
    [Tooltip("Meters below HMD for arm origin (match PlaneSurfaceDrawer)")]
    [SerializeField] private float hmdDownOffset = 0.20f;

    [Header("Initial Guess + Trigger")]
    [Tooltip("Fallback arm-length guess in meters")] [SerializeField, Range(0.2f, 1.1f)]
    private float initialArmLengthGuess = 0.55f;
    [Tooltip("Trigger fraction of best guess")] [SerializeField, Range(0.5f, 1.0f)]
    private float triggerFraction = 0.50f;

    [Header("Stability Gate")] 
    [Tooltip("Seconds within band before accepting sample")] [SerializeField, Range(0.2f, 2.0f)]
    private float stabilitySeconds = 1.0f;
    [Tooltip("Accept if distance stays within ±this fraction")] [SerializeField, Range(0.05f, 0.25f)]
    private float stabilityBandFraction = 0.10f;
    [Tooltip("Meters of controller travel required during the window")] [SerializeField, Range(0.0f, 0.10f)]
    private float minMovementDuringWindow = 0.02f; // 2 cm

    [Header("Change Limits")] 
    [Tooltip("Max relative change per second toward a new accepted sample")] [SerializeField, Range(0.02f, 0.6f)]
    private float maxRisePerSecondFraction = 0.20f;
    [Tooltip("Max relative drop per second toward a smaller accepted sample")] [SerializeField, Range(0.02f, 0.6f)]
    private float maxFallPerSecondFraction = 0.20f;

    [Header("Floor Clamp")] 
    [Tooltip("If true, estimate floorY by raycasting down from HMD each second")] [SerializeField]
    private bool autoDetectFloor = true;
    [Tooltip("Layer(s) considered floor for raycast")] [SerializeField]
    private LayerMask floorMask = ~0; // default everything
    [Tooltip("Fallback floor height if raycast fails")] [SerializeField]
    private float fallbackFloorY = 0f;

    [Header("Safeguards")] 
    [Tooltip("Reset if HMD height changes by > this fraction")] [SerializeField, Range(0.05f, 0.5f)]
    private float hmdHeightResetFraction = 0.10f;
    [Tooltip("Optional hard min/max arm length (meters)")] [SerializeField]
    private Vector2 hardBounds = new Vector2(0.25f, 1.05f);

    // Public API
    public float CurrentBestGuess => _bestArmLength;
    public float CurrentTriggerDistance => Mathf.Max(0f, triggerFraction * _bestArmLength);
    public float CurrentHmdHeight => Mathf.Max(0f, _lastHmdHeight);
    public event Action<float> OnGuessChanged; // emits _bestArmLength

    // Internal state
    private float _bestArmLength;
    private float _lastHmdHeight; // above floor
    private float _baselineHmdHeight; // for swap/reset detection
    private float _floorY;
    private float _nextFloorSampleTime;

    // Per-hand window trackers
    private StableWindow _r = new StableWindow();
    private StableWindow _l = new StableWindow();

    private struct StableWindow
    {
        public bool active;
        public float baseline;
        public float min;
        public float max;
        public float startTime;
        public float avg; // running average of distance
        public int count;
        public Vector3 lastPos;
        public float moved;

        public void Reset(float dist, Vector3 pos, float now)
        {
            active = true;
            baseline = Mathf.Max(0f, dist);
            min = baseline;
            max = baseline;
            startTime = now;
            avg = baseline;
            count = 1;
            lastPos = pos;
            moved = 0f;
        }

        public void Step(float dist, Vector3 pos, float now, float bandFrac)
        {
            if (!active) { Reset(dist, pos, now); return; }
            float lo = baseline * (1f - bandFrac);
            float hi = baseline * (1f + bandFrac);

            if (dist < lo || dist > hi)
            {
                // restart window around the new reading
                Reset(dist, pos, now);
                return;
            }

            // within band
            if (dist < min) min = dist;
            if (dist > max) max = dist;
            count++;
            // running average
            avg += (dist - avg) / Mathf.Max(1, count);
            moved += (pos - lastPos).magnitude;
            lastPos = pos;
        }

        public float Elapsed(float now) => active ? (now - startTime) : 0f;
    }

    void Awake()
    {
        _bestArmLength = Mathf.Clamp(initialArmLengthGuess, hardBounds.x, hardBounds.y);
        _floorY = fallbackFloorY;
    }

    void OnEnable()
    {
        SampleFloor(force:true);
        CacheHmdHeightAsBaseline();
        _r = new StableWindow();
        _l = new StableWindow();
    }

    void Update()
    {
        if (!player || !player.hmd || !player.conR || !player.conL) return;

        // Maintain a floor estimate
        SampleFloor(force:false);

        Transform hmd = player.hmd.transform;
        Transform conR = player.conR.transform;
        Transform conL = player.conL.transform;
        Vector3 origin = hmd.position + Vector3.down * hmdDownOffset;

        // Current height clamp
        float hmdHeight = Mathf.Max(0f, hmd.position.y - _floorY);
        _lastHmdHeight = hmdHeight;

        // Detect user swap via height change
        if (_baselineHmdHeight > 0.001f)
        {
            float diffFrac = Mathf.Abs(hmdHeight - _baselineHmdHeight) / Mathf.Max(0.001f, _baselineHmdHeight);
            if (diffFrac > hmdHeightResetFraction)
            {
                HardResetToInitial("HMD height changed");
            }
        }

        float now = Time.time;

        // Distances: origin -> controller
        float dR = Vector3.Distance(origin, conR.position);
        float dL = Vector3.Distance(origin, conL.position);

        // Step windows with band guard and motion accumulation
        _r.Step(dR, conR.position, now, stabilityBandFraction);
        _l.Step(dL, conL.position, now, stabilityBandFraction);

        // Check stability completion for either hand
        bool rReady = _r.active && _r.Elapsed(now) >= stabilitySeconds && _r.moved >= minMovementDuringWindow;
        bool lReady = _l.active && _l.Elapsed(now) >= stabilitySeconds && _l.moved >= minMovementDuringWindow;

        float? candidate = null;
        if (rReady && lReady)
        {
            // Conservative: prefer the smaller of the two to avoid overestimation
            candidate = Mathf.Min(_r.avg, _l.avg);
        }
        else if (rReady)
        {
            candidate = _r.avg;
        }
        else if (lReady)
        {
            candidate = _l.avg;
        }

        if (candidate.HasValue)
        {
            // Clamp to height and hard bounds
            float capped = Mathf.Min(candidate.Value, hmdHeight);
            capped = Mathf.Clamp(capped, hardBounds.x, Mathf.Min(hardBounds.y, Mathf.Max(hardBounds.x, hmdHeight)));

            // Converge toward the new sample within configured rates
            float maxRisePerSec = Mathf.Max(0.0f, maxRisePerSecondFraction);
            float maxFallPerSec = Mathf.Max(0.0f, maxFallPerSecondFraction);
            float stepUp = Mathf.Max(0.001f, _bestArmLength * maxRisePerSec) * Time.deltaTime;
            float stepDn = Mathf.Max(0.001f, _bestArmLength * maxFallPerSec) * Time.deltaTime;
            float stepped = _bestArmLength;
            if (capped > _bestArmLength) stepped = Mathf.Min(capped, _bestArmLength + stepUp);
            else if (capped < _bestArmLength) stepped = Mathf.Max(capped, _bestArmLength - stepDn);

            if (!Mathf.Approximately(stepped, _bestArmLength))
            {
                _bestArmLength = stepped;
                OnGuessChanged?.Invoke(_bestArmLength);
            }

            // After accepting, restart windows to avoid reusing stale samples
            _r.Reset(dR, conR.position, now);
            _l.Reset(dL, conL.position, now);
        }
    }

    private void SampleFloor(bool force)
    {
        if (!autoDetectFloor) { _floorY = fallbackFloorY; return; }
        if (!player || !player.hmd) return;

        if (!force && Time.time < _nextFloorSampleTime) return;
        _nextFloorSampleTime = Time.time + 1.0f; // sample once per second

        Vector3 start = player.hmd.transform.position + Vector3.up * 0.05f; // avoid starting inside geometry
        if (Physics.Raycast(start, Vector3.down, out RaycastHit hit, 5f, floorMask, QueryTriggerInteraction.Ignore))
        {
            _floorY = hit.point.y;
        }
        else
        {
            _floorY = fallbackFloorY; // fallback
        }
    }

    private void CacheHmdHeightAsBaseline()
    {
        if (!player || !player.hmd) return;
        _baselineHmdHeight = Mathf.Max(0f, player.hmd.transform.position.y - _floorY);
        if (_baselineHmdHeight < hardBounds.x) _baselineHmdHeight = hardBounds.x; // avoid divide-by-zero sensitivity
    }

    public void HardResetToInitial(string reason = null)
    {
        _bestArmLength = Mathf.Clamp(initialArmLengthGuess, hardBounds.x, hardBounds.y);
        CacheHmdHeightAsBaseline();
        float now = Time.time;
        if (player && player.conR) _r.Reset(Vector3.Distance(player.hmd.transform.position + Vector3.down * hmdDownOffset, player.conR.transform.position), player.conR.transform.position, now);
        if (player && player.conL) _l.Reset(Vector3.Distance(player.hmd.transform.position + Vector3.down * hmdDownOffset, player.conL.transform.position), player.conL.transform.position, now);
        OnGuessChanged?.Invoke(_bestArmLength);
        if (!string.IsNullOrEmpty(reason))
        {
            // Optional: log once per reset
          //  Debug.Log($"[ArmLengthEstimator] Reset to initial ({_bestArmLength:F2}m). Reason: {reason}");
        }
    }
}

