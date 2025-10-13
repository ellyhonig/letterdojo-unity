using UnityEngine;

[RequireComponent(typeof(CanvasManager))]
public sealed class CanvasMemoryAdapter : MonoBehaviour, IMemoryBudgetConsumer
{
    [SerializeField] private CanvasManager canvasManager;
    [SerializeField] private SimpleRecorder recorder;
    [SerializeField, Tooltip("Also clear recorder frames when releasing memory.")]
    private bool clearRecorderFrames = false;

    private void Awake()
    {
        if (!canvasManager) canvasManager = GetComponent<CanvasManager>();
        if (!recorder) recorder = GetComponent<SimpleRecorder>();
    }

    public void ReleaseMemory()
    {
        if (canvasManager == null) return;
        canvasManager.ClearVisualization();
        if (!clearRecorderFrames) return;
        if (recorder != null && recorder.currentRecord != null && recorder.currentRecord.frames != null)
            recorder.currentRecord.frames.Clear();
    }
}
