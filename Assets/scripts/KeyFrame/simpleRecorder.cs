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
        currentRecord = new SimpleRecord { isLocalSpace = true };

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
        currentRecord = new SimpleRecord { isLocalSpace = true };
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
        currentRecord.isLocalSpace = true;
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
            bool usedFallback = false;

            if (loadedRecord == null || loadedRecord.frames == null || loadedRecord.frames.Count == 0)
            {
                loadedRecord = LoadFallbackRecording(letter);
                if (loadedRecord != null && loadedRecord.frames != null && loadedRecord.frames.Count > 0)
                {
                    usedFallback = true;
                }
            }

            if (loadedRecord != null && loadedRecord.frames != null && loadedRecord.frames.Count > 0)
            {
                currentRecord = loadedRecord;
                currentRecord.isLocalSpace = true;
                if (currentRecord.canvasTransform == null)
                {
                    currentRecord.canvasTransform = new SerializableTransform(canvasManager.canvasPlane.transform);
                }
                if (usedFallback)
                {
                if (currentRecord.canvasTransform != null)
                {
                    ConvertFramesToSavedLocal(currentRecord.canvasTransform);
                    TransformPointsToCurrentCanvas(currentRecord.canvasTransform);
                }
                NormalizeLocalFrames();
                currentRecord.isLocalSpace = true;
                currentRecord.canvasTransform = new SerializableTransform(canvasManager.canvasPlane.transform);
                    Debug.Log($"[SimpleRecorder] Loaded fallback recording for letter {letter}. Frame count: {currentRecord.frames.Count}");
                }
                else
                {
                    ApplyCanvasTransform(loadedRecord.canvasTransform);
                    currentRecord.isLocalSpace = true;
                    Debug.Log($"Recording for letter {letter} loaded successfully. Frame count: {currentRecord.frames.Count}");
                }
                OnRecordingLoaded?.Invoke();
            }
            else
            {
                Debug.LogWarning($"Failed to load recording for letter {letter} or loaded record is empty. Creating a new empty record.");
                currentRecord = new SimpleRecord { isLocalSpace = true };
                OnRecordingLoaded?.Invoke();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error loading recording: {e.Message}\n{e.StackTrace}");
            currentRecord = new SimpleRecord { isLocalSpace = true };
            OnRecordingLoaded?.Invoke();
        }
    }

    public bool EnsureFallbackLoaded(string letter, bool invokeLoaded = true)
    {
        var fallback = LoadFallbackRecording(letter);
        if (fallback == null || fallback.frames == null || fallback.frames.Count == 0)
        {
            return false;
        }

        currentRecord = fallback;
        if (currentRecord.canvasTransform != null)
        {
            ConvertFramesToSavedLocal(currentRecord.canvasTransform);
            TransformPointsToCurrentCanvas(currentRecord.canvasTransform);
        }

        NormalizeLocalFrames();
        currentRecord.canvasTransform = new SerializableTransform(canvasManager.canvasPlane.transform);
        currentRecord.isLocalSpace = true;

        Debug.Log($"[SimpleRecorder] EnsureFallbackLoaded succeeded for letter {letter}. Frame count: {currentRecord.frames.Count}");
        if (invokeLoaded)
        {
            OnRecordingLoaded?.Invoke();
        }
        return true;
    }

    private SimpleRecord LoadFallbackRecording(string letter)
    {
        string normalized = string.IsNullOrWhiteSpace(letter) ? string.Empty : letter.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            Debug.LogWarning("[SimpleRecorder] Fallback requested with empty letter key.");
            return null;
        }

        string resourcePath = $"letter3Dpathdata/simple_recording_{normalized.ToUpperInvariant()}";
        TextAsset asset = Resources.Load<TextAsset>(resourcePath);
        if (asset == null || string.IsNullOrWhiteSpace(asset.text))
        {
            Debug.LogWarning($"[SimpleRecorder] Fallback simple recording not found at Resources/{resourcePath}.");
            return null;
        }

        try
        {
            SimpleRecordingDto dto = JsonUtility.FromJson<SimpleRecordingDto>(asset.text);
            if (dto == null || dto.frames == null || dto.frames.Length == 0)
            {
                Debug.LogWarning($"[SimpleRecorder] Fallback simple recording '{resourcePath}' is empty.");
                return null;
            }

            var record = new SimpleRecord();
            for (int i = 0; i < dto.frames.Length; i++)
            {
                record.frames.Add(new SimpleFrame(dto.frames[i].position));
            }

            if (dto.canvasTransform != null)
            {
                record.canvasTransform = dto.canvasTransform.ToSerializableTransform();
            }

            record.isLocalSpace = true;

            Debug.Log($"[SimpleRecorder] Loaded simple recording fallback '{resourcePath}' frames={record.frames.Count}");
            return record;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SimpleRecorder] Failed to parse fallback recording '{resourcePath}': {ex.Message}");
            return null;
        }
    }

    private void ConvertFramesToSavedLocal(SerializableTransform savedCanvasTransform)
    {
        if (currentRecord == null || currentRecord.frames == null || currentRecord.frames.Count == 0)
            return;

        Vector3 savedScale = savedCanvasTransform != null ? savedCanvasTransform.scale : Vector3.one;
        if (savedScale == Vector3.zero) savedScale = Vector3.one;

        Matrix4x4 worldToSavedLocal = Matrix4x4.TRS(
            savedCanvasTransform != null ? savedCanvasTransform.position : Vector3.zero,
            savedCanvasTransform != null ? savedCanvasTransform.rotation : Quaternion.identity,
            savedScale).inverse;

        for (int i = 0; i < currentRecord.frames.Count; i++)
        {
            currentRecord.frames[i].position = worldToSavedLocal.MultiplyPoint3x4(currentRecord.frames[i].position);
        }
    }

    private void TransformPointsToCurrentCanvas(SerializableTransform savedCanvasTransform)
    {
        if (savedCanvasTransform == null)
        {
            Debug.LogWarning("[SimpleRecorder] TransformPointsToCurrentCanvas called with null transform.");
            return;
        }

        // Since the positions are already in local space relative to the saved canvas,
        // and we want them in local space relative to the current canvas:
        Vector3 savedScale = savedCanvasTransform.scale == Vector3.zero ? Vector3.one : savedCanvasTransform.scale;

        Vector3 savedForward = savedCanvasTransform.rotation * Vector3.forward;
        Vector3 currForward = canvasManager.canvasPlane.transform.forward;
        bool flipZ = Vector3.Dot(savedForward, currForward) < 0f;

        Quaternion adjust = Quaternion.identity;
        if (flipZ)
        {
            adjust = Quaternion.AngleAxis(180f, Vector3.right);
        }

        Matrix4x4 savedLocalToWorld = Matrix4x4.TRS(
            savedCanvasTransform.position,
            savedCanvasTransform.rotation * adjust,
            savedScale);

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

    private void NormalizeLocalFrames()
    {
        if (currentRecord == null || currentRecord.frames == null || currentRecord.frames.Count == 0)
            return;

        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);

        for (int i = 0; i < currentRecord.frames.Count; i++)
        {
            var local = currentRecord.frames[i].position;
            float u = local.x;
            float v = local.z;
            if (u < min.x) min.x = u;
            if (v < min.y) min.y = v;
            if (u > max.x) max.x = u;
            if (v > max.y) max.y = v;
        }

        Vector2 center = new Vector2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f);
        float width = Mathf.Max(max.x - min.x, 1e-5f);
        float height = Mathf.Max(max.y - min.y, 1e-5f);

        float targetWidth = 1f;
        float targetHeight = 1f;
        if (canvasManager != null && canvasManager.canvasPlane != null)
        {
            Vector3 lossy = canvasManager.canvasPlane.transform.lossyScale;
            targetWidth = Mathf.Max(0.0001f, Mathf.Abs(lossy.x));
            targetHeight = Mathf.Max(0.0001f, Mathf.Abs(lossy.z));
        }

        float widthScale = (targetWidth * 0.9f) / width;
        float heightScale = (targetHeight * 0.9f) / height;
        float scale = Mathf.Min(1f, widthScale, heightScale);

        for (int i = 0; i < currentRecord.frames.Count; i++)
        {
            var local = currentRecord.frames[i].position;
            float centeredX = local.x - center.x;
            float centeredZ = local.z - center.y;

            centeredX = -centeredX; // mirror horizontally so fallback dots match renderer orientation

            float u = centeredX * scale;
            float v = centeredZ * scale;
            currentRecord.frames[i].position = new Vector3(u, 0f, v);
        }

        currentRecord.isLocalSpace = true;
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
    public bool isLocalSpace = true;
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

    public SerializableTransform()
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        scale = Vector3.one;
    }

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

[System.Serializable]
internal class SimpleRecordingDto
{
    public SimpleFrameDto[] frames;
    public SerializableTransformDto canvasTransform;
}

[System.Serializable]
internal class SimpleFrameDto
{
    public Vector3 position;
}

[System.Serializable]
internal class SerializableTransformDto
{
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;

    public SerializableTransform ToSerializableTransform()
    {
        var serializable = new SerializableTransform();
        serializable.position = position;
        serializable.rotation = rotation;
        serializable.scale = scale == Vector3.zero ? Vector3.one : scale;
        return serializable;
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












