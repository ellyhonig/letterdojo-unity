using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[RequireComponent(typeof(HandPlaneConstraint))]
public class HandPlanePointRecorder : MonoBehaviour
{
    [Serializable]
    private struct ParamPoint
    {
        public float u;
        public float v;
        public float time;
    }

    [Serializable]
    private class RecordingData
    {
        public float planeSize;
        public List<ParamPoint> points = new List<ParamPoint>();
    }

    [Header("References")]
    [SerializeField] private HandPlaneConstraint planeConstraint;
    [SerializeField] private simplePlayer player;
    [Tooltip("Optional override. Falls back to collider on the constraint GameObject if left empty.")]
    [SerializeField] private Collider planeCollider;
    [Tooltip("Optional override used when no collider is available.")]
    [SerializeField] private Renderer planeRenderer;

    [Header("Recording")]
    [Tooltip("Minimum parameter-space distance between stored points (0..2).")]
    [SerializeField, Range(0.001f, 2f)] private float minParamDelta = 0.03f;
    [Tooltip("File name saved to Application.persistentDataPath.")]
    [SerializeField] private string fileName = "hand_plane_points.json";
    [Tooltip("Fallback plane size (meters) when no collider or renderer is present.")]
    [SerializeField] private float manualPlaneSize = 1f;

    private readonly List<ParamPoint> _recordedPoints = new List<ParamPoint>();
    private bool _isRecording;
    private Vector2 _lastRecorded;
    private bool _hasLastRecorded;
    private float _halfWidth;
    private float _halfHeight;

    public bool IsRecording => _isRecording;

    private void Awake()
    {
        if (!planeConstraint) planeConstraint = GetComponent<HandPlaneConstraint>();
        if (!player) player = FindRelevantPlayer();
        if (!planeCollider) planeCollider = GetComponent<Collider>();
        if (!planeRenderer) planeRenderer = GetComponent<Renderer>();
        RecalculatePlaneExtents();
    }

    private void OnValidate()
    {
        if (!planeConstraint) planeConstraint = GetComponent<HandPlaneConstraint>();
        if (!player) player = FindRelevantPlayer();
        if (!planeCollider) planeCollider = GetComponent<Collider>();
        if (!planeRenderer) planeRenderer = GetComponent<Renderer>();
        RecalculatePlaneExtents();
    }

    private void Update()
    {
        if (!_isRecording || planeConstraint == null) return;
        Transform controller = GetRightControllerTransform();
        if (!controller) return;
        if (!planeConstraint.IsConstrained(HandPlaneConstraint.Hand.Right)) return;

        Vector3 projected = planeConstraint.ProjectToPlane(controller.position);
        Vector2 param = WorldToParam(projected);
        if (!_hasLastRecorded || Vector2.Distance(param, _lastRecorded) >= minParamDelta)
        {
            var point = new ParamPoint { u = param.x, v = param.y, time = Time.time };
            _recordedPoints.Add(point);
            _lastRecorded = param;
            _hasLastRecorded = true;
            Debug.Log($"HandPlanePointRecorder: recorded ({point.u:F3}, {point.v:F3}) at t={point.time:F2}");
        }
    }

    public void ToggleRecording()
    {
        if (_isRecording)
        {
            StopRecordingInternal();
        }
        else
        {
            StartRecordingInternal();
        }
    }

    public void SaveRecording()
    {
        if (_recordedPoints.Count == 0)
        {
            Debug.LogWarning("HandPlanePointRecorder: no points to save.");
            return;
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);
        var data = new RecordingData
        {
            planeSize = (_halfWidth + _halfHeight) * 0.5f * 2f,
            points = new List<ParamPoint>(_recordedPoints)
        };
        string json = JsonUtility.ToJson(data, true);
        try
        {
            File.WriteAllText(path, json);
            Debug.Log($"HandPlanePointRecorder: saved {_recordedPoints.Count} points to {path}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"HandPlanePointRecorder: failed to save recording. {ex.Message}");
        }
    }

    public void LoadRecording()
    {
        string path = Path.Combine(Application.persistentDataPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"HandPlanePointRecorder: recording file not found at {path}");
            return;
        }

        try
        {
            string json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<RecordingData>(json);
            _recordedPoints.Clear();
            if (data?.points != null)
            {
                _recordedPoints.AddRange(data.points);
            }

            foreach (var point in _recordedPoints)
            {
                Debug.Log($"HandPlanePointRecorder: loaded point ({point.u:F3}, {point.v:F3})");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"HandPlanePointRecorder: failed to load recording. {ex.Message}");
        }
    }

    private void StartRecordingInternal()
    {
        Transform controller = GetRightControllerTransform();
        if (planeConstraint == null || controller == null)
        {
            Debug.LogWarning("HandPlanePointRecorder: missing constraint or right-hand controller reference.");
            return;
        }

        RecalculatePlaneExtents();
        _isRecording = true;
        _recordedPoints.Clear();
        _hasLastRecorded = false;
        Debug.Log("HandPlanePointRecorder: started recording.");
    }

    private void StopRecordingInternal()
    {
        _isRecording = false;
        Debug.Log($"HandPlanePointRecorder: stopped recording with {_recordedPoints.Count} points.");
        foreach (var point in _recordedPoints)
        {
            Debug.Log($"HandPlanePointRecorder: point ({point.u:F3}, {point.v:F3})");
        }
    }

    private Vector2 WorldToParam(Vector3 worldPoint)
    {
        if (_halfWidth <= Mathf.Epsilon || _halfHeight <= Mathf.Epsilon)
            return Vector2.zero;

        Transform planeTransform = planeConstraint.transform;
        Vector3 toPoint = worldPoint - planeTransform.position;
        float u = Vector3.Dot(toPoint, planeTransform.right) / _halfWidth;
        float v = Vector3.Dot(toPoint, planeTransform.forward) / _halfHeight;
        return new Vector2(u, v);
    }

    private void RecalculatePlaneExtents()
    {
        Transform planeTransform = planeConstraint != null ? planeConstraint.transform : transform;
        Vector3 right = planeTransform.right.normalized;
        Vector3 forward = planeTransform.forward.normalized;

        bool updated = false;
        if (planeCollider != null)
        {
            Bounds bounds = planeCollider.bounds;
            Vector3 ex = bounds.extents;
            _halfWidth = ProjectionExtent(ex, right);
            _halfHeight = ProjectionExtent(ex, forward);
            updated = true;
        }
        else if (planeRenderer != null)
        {
            Bounds bounds = planeRenderer.bounds;
            Vector3 ex = bounds.extents;
            _halfWidth = ProjectionExtent(ex, right);
            _halfHeight = ProjectionExtent(ex, forward);
            updated = true;
        }

        if (!updated)
        {
            float half = Mathf.Max(0.0001f, manualPlaneSize * 0.5f);
            _halfWidth = half;
            _halfHeight = half;
        }
    }

    private static float ProjectionExtent(Vector3 extents, Vector3 direction)
    {
        direction = direction.normalized;
        return Mathf.Abs(direction.x) * extents.x +
               Mathf.Abs(direction.y) * extents.y +
               Mathf.Abs(direction.z) * extents.z;
    }

    private simplePlayer FindRelevantPlayer()
    {
        if (planeConstraint != null)
        {
            var fromConstraint = planeConstraint.GetComponent<simplePlayer>();
            if (fromConstraint) return fromConstraint;
            var parent = planeConstraint.GetComponentInParent<simplePlayer>();
            if (parent) return parent;
        }

        var local = GetComponent<simplePlayer>();
        if (local) return local;

        return FindObjectOfType<simplePlayer>();
    }

    private Transform GetRightControllerTransform()
    {
        if (player == null)
            player = FindRelevantPlayer();

        return player != null && player.conR != null ? player.conR.transform : null;
    }
}
