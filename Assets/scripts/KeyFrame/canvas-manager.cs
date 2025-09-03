// CanvasManager.cs
using UnityEngine;
using System.Collections.Generic;
using System;

public class CanvasManager : MonoBehaviour
{
    [SerializeField] public GameObject canvasPlane;
    [SerializeField] public SimpleRecorder recorder;
    [SerializeField] private LevelManager levelManager; // gate visualization by mode
    [SerializeField] public float proximityThreshold = 0.1f;
    [SerializeField] private Color activeColor = Color.green;
    [SerializeField] private Color inactiveColor = Color.white;

    [Header("Visualization")]
    [SerializeField] public float sphereRadius = 0.01f;
    [SerializeField] private Color sphereColor = Color.red;
    [SerializeField, Range(0, 31)] private int visualizationLayer = 0;
    [SerializeField] private int initialPoolSize = 2000;
    [SerializeField] private GameObject spherePrefab; // kept; not used to avoid asset dependency
    [SerializeField] private float enterThreshold = 0.01f;


    [Header("Motion Smoothener")]
    [SerializeField, Range(0, 180)] private float angleThreshold = 30f;

    [Header("reset canvas")]
    [SerializeField] private float resetDistance = 0.5f;
    [SerializeField] private float resetAngle = -0.5f;

    [Header("Smoothing")]
    [Tooltip("Time constant for SmoothDamp position (smaller = snappier).")]
    [SerializeField] private float positionSmoothTime = 0.15f;
    [Tooltip("Degrees per second-ish for rotation slerp (higher = faster).")]
    [SerializeField] private float rotationLerpSpeed = 12f;
    [Tooltip("Stop moving when within this distance to target.")]
    [SerializeField] private float stopDistance = 0.002f;
    [Tooltip("Stop rotating when within this many degrees to target.")]
    [SerializeField] private float stopAngle = 0.25f;

    [Header("Retarget gating (prevents jitter)")]
    [SerializeField] private float retargetCooldown = 0.25f;   // s
    [SerializeField] private float minRetargetMove = 0.05f;    // m
    [SerializeField] private float minRetargetAngle = 6f;      // deg

    public Queue<GameObject> spherePool = new Queue<GameObject>();
    public List<GameObject> activeSpheres = new List<GameObject>();
    private Renderer canvasRenderer;
    private Plane canvasPlaneGeometry;
    private bool isInitialized = false;
    private bool isRecording = false;
    private bool isHandConstrained = false;
    private int hmdSide = 1; // 1 for positive side, -1 for negative side
    private GameObject visualizationParent;
    private GameObject canvasParent;

    public float pointDistance = 0.1f;
    public float distanceFromHMD = .5f;
    public Vector3 offsetFromHMD = new Vector3(0f, -0.2f, 0f);
    public float additionalRotationAngle = 116; // kept
    public event Action OnCanvasRepositioned;
    public delegate void UpdateDelegate();
    public UpdateDelegate currentUpdate;

    // --- Smoothing state ---
    private bool isRepositioning = false;
    private Vector3 targetPosition;
    private Quaternion targetRotation;
    private Vector3 positionVelocity = Vector3.zero;

    // --- Retarget gating state ---
    private float lastRetargetAt = -999f;
    private Vector3 lastTargetPos;
    private Quaternion lastTargetRot = Quaternion.identity;

    // --- Stroke smoothing state (kept) ---
    private Vector3? lastSpherePosition = null;
    private Vector3? referenceDirection = null;

    // write-state (kept)
    public float writingDistance = 0.03f;
    [SerializeField] private int smoothBatchSize = 5;
    [SerializeField] private float lineThreshold = 0.001f;
    private Vector3? lastSpherePos;
    private List<Vector3> rawStroke = new List<Vector3>();
    private List<GameObject> strokeSpheres = new List<GameObject>();

    private bool lastConstrainedVisual = false; // to avoid per-frame material writes

    private void Start()
    {
        Initialize();
    }

    private void Initialize()
    {
        if (!levelManager) levelManager = GetComponent<LevelManager>();
        if (canvasPlane == null || recorder == null || recorder.playerToRecord == null)
        {
            Debug.LogError("CanvasManager: Missing required references!");
            return;
        }

        canvasRenderer = canvasPlane.GetComponent<Renderer>();
        UpdateCanvasPlaneGeometry();

        if (recorder != null)
        {
            recorder.OnRecordingStarted += StartProcessing;
            recorder.OnRecordingStopped += StopProcessing;
            recorder.OnRecordingStopped += currentRecordProcessor;
            recorder.OnRecordingLoaded += CreateVisualizationForAllPoints;
            recorder.OnRecordingLoaded += UpdateCanvas; // keep behavior
        }
        else
        {
            Debug.LogError("SimpleRecorder reference is missing in CanvasManager.");
        }

        CreateVisualizationParent();
        InitializeSpherePool();
        canvasParent = new GameObject("CanvasParent");

        // Initial snap (no visible tween)
        InstantSnapCanvas();
        UpdateCanvasPlaneGeometry();
        lastTargetPos = canvasPlane.transform.position;
        lastTargetRot = canvasPlane.transform.rotation;

        isInitialized = true;
    }

    public void currentRecordProcessor()
    {
        if (recorder == null || recorder.currentRecord == null)
        {
            Debug.LogError("Recorder or currentRecord is null");
            return;
        }

        List<GameObject> spheresToKeep = new List<GameObject>();
        List<GameObject> spheresToRemove = new List<GameObject>();

        if (activeSpheres.Count > 0) spheresToKeep.Add(activeSpheres[0]);

        for (int i = 1; i < activeSpheres.Count; i++)
        {
            Vector3 lastKeptPosition = spheresToKeep[spheresToKeep.Count - 1].transform.localPosition;
            Vector3 currentPosition = activeSpheres[i].transform.localPosition;

            if (Vector3.Distance(lastKeptPosition, currentPosition) >= pointDistance)
                spheresToKeep.Add(activeSpheres[i]);
            else
                spheresToRemove.Add(activeSpheres[i]);
        }

        // RETURN to pool instead of Destroy (perf-safe)
        foreach (GameObject sphere in spheresToRemove)
        {
            activeSpheres.Remove(sphere);
            sphere.SetActive(false);
            spherePool.Enqueue(sphere);
        }

        recorder.currentRecord.frames.Clear();

        // Keep world conversion identical to your current behavior
        foreach (GameObject sphere in spheresToKeep)
        {
            Vector3 worldPosition = canvasPlane.transform.TransformPoint(sphere.transform.localPosition);
            SimpleFrame newFrame = new SimpleFrame(worldPosition);
            recorder.currentRecord.frames.Add(newFrame);
        }

        Debug.Log($"CurrentRecord processed. New frame count: {recorder.currentRecord.frames.Count}");
        Debug.Log($"Kept spheres: {spheresToKeep.Count}, Removed spheres: {spheresToRemove.Count}");
    }

    private void CreateVisualizationParent()
    {
        visualizationParent = canvasPlane; // kept
    }

    private void InitializeSpherePool()
    {
        for (int i = 0; i < initialPoolSize; i++)
        {
            GameObject sphere = CreateSphere(i);
            sphere.SetActive(false);
            spherePool.Enqueue(sphere);
        }
    }

    private GameObject CreateSphere(int index)
    {
        // Keep CreatePrimitive to avoid prefab dependency
        GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = $"keypoint_{index}";
        sphere.transform.localScale = Vector3.one * (sphereRadius * 5);
        var r = sphere.GetComponent<Renderer>();
        if (r != null) r.material.color = sphereColor; // unchanged
        sphere.layer = visualizationLayer;
        Destroy(sphere.GetComponent<Collider>());
        sphere.transform.SetParent(visualizationParent.transform);
        return sphere;
    }

    private void Update()
    {
        if (!isInitialized)
            return;

        // Only set a new target when you're looking away enough, with hysteresis/cooldown
        if (isNotLookingAtCanvas(resetAngle))
        {
            MaybeUpdateCanvasTarget();
        }

        if (!isRecording)
        {
            // still allow smoothing via LateUpdate
        }
        else
        {
            UpdateHmdSide();
            CheckHandProximity();
            UpdateCanvasColor();
        }
    }

    private void LateUpdate()
    {
        // Do smoothing here so it doesn't fight XR rig updates
        if (isRepositioning)
            TickSmoothMove();
    }

    private void MaybeUpdateCanvasTarget()
    {
        if (Time.time - lastRetargetAt < retargetCooldown) return;

        // Compute pose using the SAME math you had in UpdateCanvas()
        Transform hmdTransform = recorder.playerToRecord.hmd.transform;
        Vector3 hmdForward = hmdTransform.forward;
        Vector3 forwardProjected = Vector3.ProjectOnPlane(hmdForward, Vector3.up);
        if (forwardProjected.sqrMagnitude < 1e-6f)
            forwardProjected = new Vector3(hmdForward.x, 0f, hmdForward.z);
        forwardProjected.Normalize();

        Vector3 newPosition = hmdTransform.position + forwardProjected * distanceFromHMD;
        newPosition.y = hmdTransform.position.y;
        newPosition += offsetFromHMD;

        Vector3 right = Vector3.Cross(Vector3.up, forwardProjected).normalized;
        Vector3 up = Vector3.Cross(forwardProjected, right).normalized;

        Quaternion targetRot = Quaternion.LookRotation(forwardProjected, up);
        targetRot *= Quaternion.Euler(-90, 0, 0);

        float move = Vector3.Distance(lastTargetPos, newPosition);
        float ang = Quaternion.Angle(lastTargetRot, targetRot);
        if (move < minRetargetMove && ang < minRetargetAngle) return;

        targetPosition = newPosition;
        targetRotation = targetRot;
        positionVelocity = Vector3.zero;
        isRepositioning = true;

        lastTargetPos = newPosition;
        lastTargetRot = targetRot;
        lastRetargetAt = Time.time;
    }

    private void TickSmoothMove()
    {
        // Smooth position
        canvasPlane.transform.position = Vector3.SmoothDamp(
            canvasPlane.transform.position,
            targetPosition,
            ref positionVelocity,
            positionSmoothTime
        );

        // Smooth rotation
        canvasPlane.transform.rotation = Quaternion.Slerp(
            canvasPlane.transform.rotation,
            targetRotation,
            rotationLerpSpeed * Time.deltaTime
        );

        // Keep plane geometry in sync while moving
        UpdateCanvasPlaneGeometry();

        bool posDone = (canvasPlane.transform.position - targetPosition).sqrMagnitude <= (stopDistance * stopDistance);
        bool rotDone = Quaternion.Angle(canvasPlane.transform.rotation, targetRotation) <= stopAngle;

        if (posDone && rotDone)
        {
            canvasPlane.transform.position = targetPosition;
            canvasPlane.transform.rotation = targetRotation;
            UpdateCanvasPlaneGeometry();
            isRepositioning = false;
            OnCanvasRepositioned?.Invoke();
        }
    }

    public bool isNotLookingAtCanvas(float resetAngle)
    {
        return Vector3.Dot(recorder.playerToRecord.hmd.transform.forward, canvasPlane.transform.up * -1f) < resetAngle;
    }

    public bool isHmdFarAway(float distance)
    {
        // bugfix: compare to plane POSITION, not its UP vector
        return Vector3.Distance(recorder.playerToRecord.hmd.transform.position,
                                canvasPlane.transform.position) > distance;
    }

    private void UpdateHmdSide()
    {
        hmdSide = canvasPlaneGeometry.GetSide(recorder.playerToRecord.hmd.transform.position) ? 1 : -1;
    }

    private void ResetStrokeSmoothing()
    {
        lastSpherePosition = null;
        referenceDirection = null;
    }

    private void CheckHandProximity()
    {
        Vector3 handPos = recorder.playerToRecord.conR.transform.position;
        float d = canvasPlaneGeometry.GetDistanceToPoint(handPos);
        bool pastPlane = Mathf.Abs(d) > proximityThreshold
                         && canvasPlaneGeometry.GetSide(handPos) != (hmdSide == 1);

        if (pastPlane)
        {
            if (!isHandConstrained)
            {
                isHandConstrained = true;
                recorder.playerToRecord.currentUpdate = ConstrainedHandUpdate;
                ResetStrokeSmoothing();
            }
        }
        else if (isHandConstrained)
        {
            isHandConstrained = false;
            recorder.playerToRecord.currentUpdate = recorder.playerToRecord.regularUpdate;
        }

        UpdateCanvasColor();
    }

    private void ConstrainedHandUpdate()
    {
        Vector3 controllerPosition = recorder.playerToRecord.conR.transform.position;
        Vector3 projectedPosition = canvasPlaneGeometry.ClosestPointOnPlane(controllerPosition);

        // … existing shoulder/hand clamp …

        if (canvasPlaneGeometry.GetSide(controllerPosition) == (hmdSide == 1))
        {
            isHandConstrained = false;
            recorder.playerToRecord.currentUpdate = recorder.playerToRecord.regularUpdate;
            return;
        }

        if (!recorder.IsRecording) return;

        Vector3 proj = canvasPlaneGeometry.ClosestPointOnPlane(
            recorder.playerToRecord.conR.transform.position
        );

        if (!lastSpherePos.HasValue ||
            Vector3.Distance(lastSpherePos.Value, proj) > writingDistance)
        {
            // spawn & record
            rawStroke.Add(proj);
            var sph = CreateVisualizationSphere(proj); // *** DO NOT TOUCH POSITION LOGIC ***
            strokeSpheres.Add(sph);
            lastSpherePos = proj;

            // every batch of N, smooth it
            if (rawStroke.Count >= smoothBatchSize)
            {
                SmoothBatch(rawStroke.GetRange(0, smoothBatchSize),
                            strokeSpheres.GetRange(0, smoothBatchSize));
                rawStroke.RemoveRange(0, smoothBatchSize);
                strokeSpheres.RemoveRange(0, smoothBatchSize);
            }
        }
    }

    private void SmoothBatch(List<Vector3> pts, List<GameObject> sphs)
    {
        Vector3 p0 = pts[0], pN = pts[pts.Count - 1];
        float maxDev = 0f;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            float dev = Vector3.Magnitude(
                Vector3.Cross(pN - p0, pts[i] - p0) / (pN - p0).magnitude
            );
            maxDev = Mathf.Max(maxDev, dev);
        }

        bool isCurve = maxDev > lineThreshold;
        Vector3 cp = isCurve ? pts[pts.Count / 2] : Vector3.zero;

        for (int i = 0; i < pts.Count; i++)
        {
            float t = i / (float)(pts.Count - 1);
            Vector3 newPos = isCurve
                ? BezierPoint(p0, cp, pN, t)
                : Vector3.Lerp(p0, pN, t);
            sphs[i].transform.position = newPos;
        }
    }

    private Vector3 BezierPoint(Vector3 a, Vector3 b, Vector3 c, float t)
    {
        float u = 1 - t;
        return u * u * a + 2 * u * t * b + t * t * c;
    }

    private void UpdateCanvasColor()
    {
        if (canvasRenderer == null) return;

        // only change on state flip (no per-frame material churn)
        if (lastConstrainedVisual == isHandConstrained) return;
        lastConstrainedVisual = isHandConstrained;

        // keep your original material-color approach (simple/safe)
        canvasRenderer.material.color = isHandConstrained ? activeColor : inactiveColor;
    }

    private void StartProcessing()
    {
        isRecording = true;
        ClearVisualization();
    }

    private void StopProcessing()
    {
        isRecording = false;
        isHandConstrained = false;
        recorder.playerToRecord.currentUpdate = recorder.playerToRecord.regularUpdate;
        UpdateCanvasColor();
    }

    public void CreateVisualizationForAllPoints()
    {
        // Skip auto-creating reference spheres while in Dictation mode
        if (levelManager != null && levelManager.currentMode == LevelManager.GameMode.Dictation)
            return;
        ClearVisualization();

        if (recorder.currentRecord != null && recorder.currentRecord.frames != null)
        {
            foreach (var frame in recorder.currentRecord.frames)
            {
                CreateVisualizationSphere(frame.position); // *** DO NOT TOUCH POSITION LOGIC ***
            }
        }
    }

    // *** DO NOT TOUCH POSITION LOGIC ***
    public GameObject CreateVisualizationSphere(Vector3 worldPosition)
    {
        GameObject sphere;
        if (spherePool.Count > 0)
        {
            sphere = spherePool.Dequeue();
        }
        else
        {
            sphere = CreateSphere(activeSpheres.Count);
        }

        Vector3 localPosition = canvasPlane.transform.InverseTransformPoint(worldPosition);
        sphere.transform.localPosition = localPosition;
        sphere.SetActive(true);
        activeSpheres.Add(sphere);

        return sphere;
    }

    public void ClearVisualization()
    {
        foreach (var sphere in activeSpheres)
        {
            sphere.SetActive(false);
            spherePool.Enqueue(sphere);
        }
        activeSpheres.Clear();
    }

    public void UpdateCanvas()
    {
        if (recorder.playerToRecord == null || recorder.playerToRecord.hmd == null)
        {
            Debug.LogError("Player or HMD reference is missing!");
            return;
        }

        Transform hmdTransform = recorder.playerToRecord.hmd.transform;

        Vector3 hmdForward = hmdTransform.forward;
        Vector3 forwardProjected = Vector3.ProjectOnPlane(hmdForward, Vector3.up);
        if (forwardProjected.sqrMagnitude < 1e-6f)
            forwardProjected = new Vector3(hmdForward.x, 0f, hmdForward.z);
        forwardProjected.Normalize();

        Vector3 newPosition = hmdTransform.position + forwardProjected * distanceFromHMD;
        newPosition.y = hmdTransform.position.y;
        newPosition += offsetFromHMD;

        Vector3 right = Vector3.Cross(Vector3.up, forwardProjected).normalized;
        Vector3 up = Vector3.Cross(forwardProjected, right).normalized;

        Quaternion targetRot = Quaternion.LookRotation(forwardProjected, up);
        targetRot *= Quaternion.Euler(-90, 0, 0);

        targetPosition = newPosition;
        targetRotation = targetRot;
        positionVelocity = Vector3.zero;
        isRepositioning = true;

        lastTargetPos = newPosition;
        lastTargetRot = targetRot;
        lastRetargetAt = Time.time;
    }

    private void InstantSnapCanvas()
    {
        if (recorder == null || recorder.playerToRecord == null || recorder.playerToRecord.hmd == null) return;

        Transform hmdTransform = recorder.playerToRecord.hmd.transform;

        Vector3 hmdForward = hmdTransform.forward;
        Vector3 forwardProjected = Vector3.ProjectOnPlane(hmdForward, Vector3.up);
        if (forwardProjected.sqrMagnitude < 1e-6f)
            forwardProjected = new Vector3(hmdForward.x, 0f, hmdForward.z);
        forwardProjected.Normalize();

        Vector3 newPosition = hmdTransform.position + forwardProjected * distanceFromHMD;
        newPosition.y = hmdTransform.position.y;
        newPosition += offsetFromHMD;

        Vector3 right = Vector3.Cross(Vector3.up, forwardProjected).normalized;
        Vector3 up = Vector3.Cross(forwardProjected, right).normalized;

        Quaternion rot = Quaternion.LookRotation(forwardProjected, up);
        rot *= Quaternion.Euler(-90, 0, 0);

        canvasPlane.transform.position = newPosition;
        canvasPlane.transform.rotation = rot;
        UpdateCanvasPlaneGeometry();

        targetPosition = newPosition;
        targetRotation = rot;
        isRepositioning = false;
        positionVelocity = Vector3.zero;

        lastTargetPos = newPosition;
        lastTargetRot = rot;
    }

    public void UpdateCanvasPlaneGeometry()
    {
        if (canvasPlane != null)
        {
            canvasPlaneGeometry = new Plane(canvasPlane.transform.up, canvasPlane.transform.position);
        }
    }

    private void OnDestroy()
    {
        // Do NOT destroy canvasPlane (visualizationParent == canvasPlane).
        // If you need cleanup, disable pooled children instead (already handled).
    }
}
