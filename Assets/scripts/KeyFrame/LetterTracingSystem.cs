using UnityEngine;
using System;
using System.Collections.Generic;

public class LetterTracingSystem : MonoBehaviour
{
    public enum TracingState
    {
        Idle,
        Tracing,
        Completed
    }

    public TracingState CurrentState { get; private set; }

    public event Action OnTraceStarted;
    public event Action OnKeyframeReached;
    public event Action OnTraceCompleted;

    [SerializeField] private CanvasManager canvasManager;
    [SerializeField] private float proximityThreshold = 0.1f;
    [SerializeField] private float pointDistance = 0.5f; // Distance to determine end of first letter
    [SerializeField] private bool firstLetter = true; // Whether to trace only the first letter

    private int currentKeyframeIndex;
    public int lastLetterKeyframeIndex;

    public int CurrentKeyframeIndex => currentKeyframeIndex;

    private void Start()
    {
        if (canvasManager == null)
        {
            canvasManager = GetComponent<CanvasManager>();
        }
        
        CurrentState = TracingState.Idle;
    }

    private void Update()
    {
        if (CurrentState == TracingState.Tracing)
        {
            CheckKeyframeProximity();
        }
    }

    private void CheckKeyframeProximity()
    {
        if (currentKeyframeIndex >= canvasManager.activeSpheres.Count || 
            (firstLetter && currentKeyframeIndex > lastLetterKeyframeIndex))
        {
            return;
        }

        Vector3 currentKeyframePosition = canvasManager.activeSpheres[currentKeyframeIndex].transform.position;

        // Get the positions of the right and left hands from the CanvasManager's recorder player
        Vector3 rightHandPosition = canvasManager.recorder.playerToRecord.righthand.transform.position;
        Vector3 leftHandPosition = canvasManager.recorder.playerToRecord.lefthand.transform.position;

        // Check the distance to both hands and trigger if either hand is close enough
        if (Vector3.Distance(rightHandPosition, currentKeyframePosition) < proximityThreshold ||
            Vector3.Distance(leftHandPosition, currentKeyframePosition) < proximityThreshold)
        {
            OnKeyframeReached?.Invoke();
            currentKeyframeIndex++;
            Debug.Log("Hit a point");

            if (currentKeyframeIndex >= canvasManager.activeSpheres.Count || 
                (firstLetter && currentKeyframeIndex > lastLetterKeyframeIndex))
            {
                CurrentState = TracingState.Completed;
                OnTraceCompleted?.Invoke();
            }
        }
    }

    public void StartTracing()
    {

    if (canvasManager.activeSpheres.Count == 0)
    {
        Debug.LogWarning("StartTracing() aborted – no valid spheres.");
        return;
    }

        CurrentState = TracingState.Tracing;
        currentKeyframeIndex = 0;
        
        if (firstLetter)
        {
            DetermineLastLetterKeyframe();
        }
        else
        {
            lastLetterKeyframeIndex = canvasManager.activeSpheres.Count - 1;
        }

        OnTraceStarted?.Invoke();
    }

    private void DetermineLastLetterKeyframe()
    {
        lastLetterKeyframeIndex = 0;
        for (int i = 0; i < canvasManager.activeSpheres.Count - 1; i++)
        {
            if (Vector3.Distance(canvasManager.activeSpheres[i].transform.position, 
                                 canvasManager.activeSpheres[i + 1].transform.position) > pointDistance)
            {
                lastLetterKeyframeIndex = i;
                break;
            }
        }
        
        // If no break is found, set to the last sphere
        if (lastLetterKeyframeIndex == 0)
        {
            lastLetterKeyframeIndex = canvasManager.activeSpheres.Count - 1;
        }

        Debug.Log($"Last keyframe of first letter: {lastLetterKeyframeIndex}");
    }
}
