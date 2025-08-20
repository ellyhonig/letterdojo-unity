using UnityEngine;
using System.IO;

public class TraceCompleteListener : MonoBehaviour
{
    [SerializeField] private LetterTracingSystem tracingSystem;
    private string outputFilePath;

    void Start()
    {
        if (tracingSystem == null)
        {
            tracingSystem = GetComponent<LetterTracingSystem>();
            if (tracingSystem == null)
            {
                tracingSystem = FindObjectOfType<LetterTracingSystem>();
            }
        }

        if (tracingSystem != null)
        {
            tracingSystem.OnTraceCompleted += OnTraceCompleted;
        }

        // Ensure the StreamingAssets folder is accessible
        // StreamingAssets is read-only at runtime on most platforms. 
        // For testing in Editor on Windows, it can be written to.
        // If necessary, choose another writable folder (e.g., Application.persistentDataPath).
        outputFilePath = Path.Combine(Application.streamingAssetsPath, "trace_completed.txt");
    }

    private void OnTraceCompleted()
    {
        // Write "TRACE_COMPLETED" to the file
        File.WriteAllText(outputFilePath, "TRACE_COMPLETED");
        Debug.Log("Trace completed. Wrote TRACE_COMPLETED to file.");
    }

    void OnDestroy()
    {
        if (tracingSystem != null)
        {
            tracingSystem.OnTraceCompleted -= OnTraceCompleted;
        }
    }
}
