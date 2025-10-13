using UnityEngine;

public class DotsDebugButton : MonoBehaviour
{
    [SerializeField] private CanvasManager canvasManager;

    private void OnEnable()
    {
        if (!canvasManager) canvasManager = FindObjectOfType<CanvasManager>();
        var laserBtn = GetComponent<LaserUIButton>();
        if (laserBtn != null)
        {
            laserBtn.SetCustomAction(ForceBuild);
        }
    }

    public void ForceBuild()
    {
        if (!canvasManager)
        {
            Debug.LogWarning("[DotsDebugButton] No CanvasManager found.");
            return;
        }
        var rec = canvasManager.recorder;
        int frames = (rec != null && rec.currentRecord != null && rec.currentRecord.frames != null)
            ? rec.currentRecord.frames.Count : -1;
        Debug.Log($"[DotsDebugButton] Forcing rebuild. Frames={frames}");
        canvasManager.CreateVisualizationForAllPointsEvenInDictation();
        Debug.Log($"[DotsDebugButton] Active spheres after build: {canvasManager.activeSpheres?.Count ?? 0}");
    }
}
