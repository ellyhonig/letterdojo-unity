using System;
using UnityEngine;

[DisallowMultipleComponent]
public class LetterTracingSystem : MonoBehaviour
{
    public enum TracingState
    {
        Idle,
        Tracing,
        Completed
    }

    [Header("Trace Components")]
    [SerializeField] private LetterPathRenderer pathRenderer;
    [SerializeField] private LetterTraceAimSequence aimSequence;
    [SerializeField] private string fallbackLetter = "a";

    public TracingState CurrentState { get; private set; } = TracingState.Idle;
    public int CurrentKeyframeIndex { get; private set; }
    public string CurrentLetter => currentLetter;

    public event Action OnTraceStarted;
    public event Action OnKeyframeReached;
    public event Action OnTraceCompleted;

    private string currentLetter = string.Empty;
    private bool callbacksAttached;

    private void Awake()
    {
        ResolveDependencies();
    }

    private void OnEnable()
    {
        AttachCallbacks();
    }

    private void OnDisable()
    {
        DetachCallbacks();
    }

    private void OnDestroy()
    {
        DetachCallbacks();
    }

    private void ResolveDependencies()
    {
        if (!pathRenderer)
        {
            pathRenderer = GetComponent<LetterPathRenderer>() ?? GetComponentInChildren<LetterPathRenderer>(true);
        }

        if (!aimSequence)
        {
            aimSequence = GetComponent<LetterTraceAimSequence>() ?? GetComponentInChildren<LetterTraceAimSequence>(true);
        }

        if (aimSequence && !pathRenderer)
        {
            pathRenderer = aimSequence.GetComponent<LetterPathRenderer>();
        }

        if (!aimSequence && pathRenderer)
        {
            aimSequence = pathRenderer.GetComponent<LetterTraceAimSequence>();
        }

        if (!aimSequence || !pathRenderer)
        {
            Debug.LogWarning("[LetterTracingSystem] Missing LetterPathRenderer or LetterTraceAimSequence components.", this);
        }
    }

    private void AttachCallbacks()
    {
        ResolveDependencies();
        if (aimSequence == null || callbacksAttached)
        {
            return;
        }

        aimSequence.PointCompleted += HandlePointCompleted;
        aimSequence.TraceCompleted += HandleTraceCompleted;
        callbacksAttached = true;
    }

    private void DetachCallbacks()
    {
        if (aimSequence == null || !callbacksAttached)
        {
            return;
        }

        aimSequence.PointCompleted -= HandlePointCompleted;
        aimSequence.TraceCompleted -= HandleTraceCompleted;
        callbacksAttached = false;
    }

    public void SetLetter(string letter)
    {
        ResolveDependencies();

        currentLetter = string.IsNullOrWhiteSpace(letter) ? fallbackLetter : letter.Trim();
        CurrentKeyframeIndex = 0;
        CurrentState = TracingState.Idle;

        if (pathRenderer != null)
        {
            pathRenderer.RenderLetter(currentLetter);
        }

        aimSequence?.PrepareSequence();
    }

    public void StartTracing()
    {
        ResolveDependencies();
        AttachCallbacks();

        if (aimSequence == null)
        {
            Debug.LogWarning("[LetterTracingSystem] StartTracing aborted - no LetterTraceAimSequence available.", this);
            CurrentState = TracingState.Idle;
            return;
        }

        if (!aimSequence.StartRun())
        {
            Debug.LogWarning($"[LetterTracingSystem] StartTracing aborted - no trace points available for '{currentLetter}'.", this);
            CurrentState = TracingState.Completed;
            CurrentKeyframeIndex = 0;
            OnTraceCompleted?.Invoke();
            return;
        }

        CurrentState = TracingState.Tracing;
        CurrentKeyframeIndex = 0;
        OnTraceStarted?.Invoke();
    }

    private void HandlePointCompleted(int index)
    {
        if (CurrentState != TracingState.Tracing)
        {
            return;
        }

        CurrentKeyframeIndex = index;
        OnKeyframeReached?.Invoke();
        CurrentKeyframeIndex = Mathf.Max(CurrentKeyframeIndex, index + 1);
    }

    private void HandleTraceCompleted()
    {
        CurrentState = TracingState.Completed;
        if (aimSequence != null)
        {
            CurrentKeyframeIndex = Mathf.Max(CurrentKeyframeIndex, aimSequence.CompletedCount);
        }
        OnTraceCompleted?.Invoke();
    }

    public void SetVisualizationActive(bool active)
    {
        ResolveDependencies();
        if (pathRenderer != null)
            pathRenderer.SetVisualizationVisible(active);
        if (aimSequence != null)
            aimSequence.SetVisualizationVisible(active);

        if (!active)
            CurrentState = TracingState.Idle;
    }

    public bool TryTriggerAtWorldPoint(Vector3 worldPoint)
    {
        // Aim handling is managed internally by LetterTraceAimSequence.
        return false;
    }
}
