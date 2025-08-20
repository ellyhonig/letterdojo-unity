using UnityEngine;
using System;
using System.Collections.Generic;

public class SimpleRecorder : MonoBehaviour
{
    public simplePlayer playerToRecord;
    public SimpleRecord currentRecord;
    [SerializeField] private CanvasManager canvasManager;
    private LevelManager levelManager;

    public event Action OnRecordingStarted;
    public event Action OnRecordingStopped;
    public event Action OnRecordingLoaded;
    [SerializeField] private bool _isRecording = false;
    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            _isRecording = value;
            if (_isRecording)
            {
                StartRecording();
            }
            else
            {
                StopRecording();
            }
        }
    }

    [SerializeField] private GameObject pointPrefab;
    [SerializeField] private Transform pointParent;

    private float timer = 0f;
    private SaveManager saveManager;

    private float updateRate = 0.1f; // Run 10x per second
    private float nextUpdateTime = 0f;

    void Start()
    {
       

    }

    void Awake()
    {
         playerToRecord = GetComponent<simplePlayer>();
        Debug.Log("SimpleRecorder started.");
        canvasManager = GetComponent<CanvasManager>();
        levelManager = GetComponent<LevelManager>();
        InitializeSaveManager();
        currentRecord = new SimpleRecord();

    }

    private void InitializeSaveManager()
    {
        saveManager = GetComponent<SaveManager>();
        if (saveManager == null)
        {
            Debug.Log("SaveManager not found. Adding a new one.");
            saveManager = gameObject.AddComponent<SaveManager>();
        }
    }

    private void Update()
    {
        if (_isRecording && Time.time >= nextUpdateTime)
        {
            RecordFrame();
            nextUpdateTime = Time.time + updateRate;
        }
    }

    private void StartRecording()
    {
        currentRecord = new SimpleRecord();
        OnRecordingStarted?.Invoke();
        Debug.Log("Recording started");
    }

    private void StopRecording()
    {
        Debug.Log("StopRecording called. Subscriber count: " + (OnRecordingStopped?.GetInvocationList().Length ?? 0));
        OnRecordingStopped?.Invoke();
        Debug.Log("Recording stopped");
    }

    private void RecordFrame()
    {
         // only record when hand is close to the canvas
        var handPos = playerToRecord.righthand.transform.position;
        var plane   = new Plane(
                          canvasManager.canvasPlane.transform.up,
                          canvasManager.canvasPlane.transform.position);
        float dist  = plane.GetDistanceToPoint(handPos);
        if (Mathf.Abs(dist) > canvasManager.proximityThreshold) 
            return;  // skip if not “on” the canvas

        // now log it
        Vector3 localPosition = canvasManager.canvasPlane
                                    .transform
                                    .InverseTransformPoint(handPos);
        currentRecord.frames.Add(new SimpleFrame(localPosition));
    }

    public void SaveRecording()
    {
        SaveRecording(levelManager.currentLetter);
    }

    public void SaveRecording(string letter)
    {
        currentRecord.canvasTransform = new SerializableTransform(canvasManager.canvasPlane.transform);
        saveManager.SaveRecording(currentRecord, letter);
        Debug.Log($"Recording saved for letter {letter}. Frame count: {currentRecord.frames.Count}");
    }

    public void LoadRecording()
    {
        LoadRecording(levelManager.currentLetter);
    }

    public void LoadRecording(string letter)
    {
        if (saveManager == null)
        {
            Debug.LogError("SaveManager is null. Initializing SaveManager.");
            InitializeSaveManager();
        }

        try
        {
            SimpleRecord loadedRecord = saveManager.LoadRecording(letter);
            if (loadedRecord != null && loadedRecord.frames != null && loadedRecord.frames.Count > 0)
            {
                currentRecord = loadedRecord;
                ApplyCanvasTransform(loadedRecord.canvasTransform);
                OnRecordingLoaded?.Invoke();
                Debug.Log($"Recording for letter {letter} loaded successfully. Frame count: {currentRecord.frames.Count}");
            }
            else
            {
                Debug.LogWarning($"Failed to load recording for letter {letter} or loaded record is empty. Creating a new empty record.");
                currentRecord = new SimpleRecord();
                OnRecordingLoaded?.Invoke();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error loading recording: {e.Message}\n{e.StackTrace}");
            currentRecord = new SimpleRecord();
            OnRecordingLoaded?.Invoke();
        }
    }

    private void TransformPointsToCurrentCanvas(SerializableTransform savedCanvasTransform)
    {
        // Since the positions are already in local space relative to the saved canvas,
        // and we want them in local space relative to the current canvas:
        Matrix4x4 savedLocalToWorld = Matrix4x4.TRS(
            savedCanvasTransform.position,
            savedCanvasTransform.rotation,
            savedCanvasTransform.scale);

        Matrix4x4 currentWorldToLocal = canvasManager.canvasPlane.transform.worldToLocalMatrix;

        // Transform each point from:
        // saved-local -> world -> current-local
        for (int i = 0; i < currentRecord.frames.Count; i++)
        {
            // Convert from saved canvas local space to world space
            Vector3 worldPos = savedLocalToWorld.MultiplyPoint3x4(currentRecord.frames[i].position);
            
            // Convert from world space to current canvas local space
            currentRecord.frames[i].position = currentWorldToLocal.MultiplyPoint3x4(worldPos);
        }
    }

    private void ApplyCanvasTransform(SerializableTransform canvasTransform)
    {
        if (canvasManager != null && canvasManager.canvasPlane != null)
        {
            canvasTransform.ApplyTo(canvasManager.canvasPlane.transform);
            canvasManager.UpdateCanvasPlaneGeometry();
            Debug.Log("Canvas transform applied and geometry updated.");
        }
        else
        {
            Debug.LogError("CanvasManager or canvasPlane is null. Cannot apply canvas transform.");
        }
    }
}

[System.Serializable]
public class SimpleRecord
{
    public List<SimpleFrame> frames = new List<SimpleFrame>();
    public SerializableTransform canvasTransform;
}
[System.Serializable]
public class SimpleFrame
{
    public Vector3 position; // Changed from position to localPosition

    public SimpleFrame(Vector3 pos)
    {
        position = pos;
    }
}


[System.Serializable]
public class SerializableTransform
{
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;

    public SerializableTransform(Transform transform)
    {
        position = transform.position;
        rotation = transform.rotation;
        scale = transform.localScale;
    }

    public void ApplyTo(Transform transform)
    {
        transform.position = position;
        transform.rotation = rotation;
        transform.localScale = scale;
    }
}
public static class SaveManagerExtensions
{
    /// <summary>
    /// LoadRecording that swallows exceptions and returns null on failure.
    /// </summary>
    public static SimpleRecord LoadRecordingSilent(this SaveManager sm, string letter)
    {
        try { return sm.LoadRecording(letter); }
        catch { return null; }
    }
}