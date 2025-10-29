using UnityEngine;
using System;

public class HandPlaneConstraint : MonoBehaviour
{
    public enum Hand { Right, Left }

    [SerializeField] private simplePlayer player;

    // Start constradining only after this much penetration *through* the plane (meters)
    [SerializeField] private float penetrationEpsilon = 0.004f;  // ~4 mm
    // Release when depth is shallower than this (meters)
    [SerializeField] private float releaseEpsilon = 0.0015f;      // ~1.5 mm

    // Fired when a hand first penetrates past penetrationEpsilon (projected point provided)
    public event Action<Hand, Vector3> OnPenetrationEnter;
    // Fired when a hand exits (i.e., shallower than releaseEpsilon; projected point provided)
    public event Action<Hand, Vector3> OnPenetrationExit;

    private Plane _plane;

    // per-hand sticky state prevents jitter at the surface
    private bool _isConstrainedR;
    private bool _isConstrainedL;

    void Awake()
    {
        if (player == null) player = FindObjectOfType<simplePlayer>();
    }

    void Start()
    {
        _plane = new Plane(transform.up, transform.position);
        if (player != null) player.currentUpdate = ConstrainBothHands;
    }

    void OnEnable()
    {
        if (player != null) player.currentUpdate = ConstrainBothHands;
    }

    void OnDisable()
    {
        if (player != null) player.currentUpdate = player.regularUpdate;
    }

    void Update()
    {
        // keep plane in sync if it moves/rotates
        _plane.SetNormalAndPosition(transform.up, transform.position);
        if (player != null) player.currentUpdate = ConstrainBothHands;
    }

    public bool IsConstrained(Hand which)
        => which == Hand.Right ? _isConstrainedR : _isConstrainedL;

    public Vector3 ProjectToPlane(Vector3 worldPoint)
        => _plane.ClosestPointOnPlane(worldPoint);

    public Vector3 PlaneNormal => _plane.normal;

    private void ConstrainBothHands()
    {
        ProjectOrPass(Hand.Right, player.conR.transform.position, player.righthand.transform, ref _isConstrainedR);
        ProjectOrPass(Hand.Left,  player.conL.transform.position, player.lefthand.transform,  ref _isConstrainedL);
    }

    private void ProjectOrPass(Hand which, Vector3 ctrlPos, Transform hand, ref bool isConstrained)
    {
        // signed distance from plane (positive on plane.normal side)
        float dist = _plane.GetDistanceToPoint(ctrlPos);

        // Figure out which side the HMD is on (reference side)
        bool hmdSide = _plane.GetSide(player.hmd.transform.position);
        float sideSign = hmdSide ? 1f : -1f;

        // Depth > 0 means "past the plane away from the HMD" (i.e., truly penetrated through)
        float penetrationDepth = -sideSign * dist;

        bool wasConstrained = isConstrained;

        // State machine w/ hysteresis
        if (!isConstrained)
        {
            // Only enter constraint once we've *actually* gone through by penetrationEpsilon
            if (penetrationDepth >= penetrationEpsilon)
                isConstrained = true;
        }
        else
        {
            // Release when we've come back above the plane enough
            if (penetrationDepth <= releaseEpsilon)
                isConstrained = false;
        }

        Vector3 projected = _plane.ClosestPointOnPlane(ctrlPos);

        // Fire events on edge transitions
        if (!wasConstrained && isConstrained)
        {
            OnPenetrationEnter?.Invoke(which, projected);
        }
        else if (wasConstrained && !isConstrained)
        {
            OnPenetrationExit?.Invoke(which, projected);
        }

        // Apply
        if (isConstrained)
            hand.position = projected;
        else
            hand.position = ctrlPos;

        // Persist state back
        if (which == Hand.Right) _isConstrainedR = isConstrained;
        else _isConstrainedL = isConstrained;
    }
}
