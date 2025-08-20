using UnityEngine;
using TMPro;

public class LoadAndTraceButton : MonoBehaviour
{
    public GameObject buttonPrefab;
    public SimpleRecorder recorder;
    public LetterTracingSystem tracingSystem;
    public float distanceFromHMD = 0.5f;
    public Vector3 offsetFromHMD = new Vector3(0f, -0.2f, 0f);

    private GameObject buttonObject;
    private ProximityButton proximityButton;

    void Start()
    {
        CreateButton();
    }

    private void CreateButton()
    {
        buttonObject = Instantiate(buttonPrefab, transform);
        buttonObject.name = "LoadAndTraceButton";
        
        proximityButton = buttonObject.AddComponent<ProximityButton>();
        proximityButton.Initialize("Load and Trace", LoadAndStartTrace, recorder);

        TextMeshPro textMesh = buttonObject.GetComponentInChildren<TextMeshPro>();
        if (textMesh != null)
        {
            textMesh.text = "Load and Trace";
            textMesh.color = Color.black;
            textMesh.fontSize = 0.02f;
            textMesh.alignment = TextAlignmentOptions.Center;
            textMesh.transform.localPosition = new Vector3(0, 0, -0.001f);
            textMesh.transform.localRotation = Quaternion.Euler(0, 180, 0);
        }
    }

    void Update()
    {
        UpdateButtonPosition();
    }

    private void UpdateButtonPosition()
    {
        if (recorder.playerToRecord != null && recorder.playerToRecord.hmd != null)
        {
            Transform hmdTransform = recorder.playerToRecord.hmd.transform;
            Vector3 hmdForward = hmdTransform.forward;
            Vector3 forwardProjected = Vector3.ProjectOnPlane(hmdForward, Vector3.up).normalized;
            
            Vector3 newPosition = hmdTransform.position + forwardProjected * distanceFromHMD;
            newPosition.y = hmdTransform.position.y;
            newPosition += offsetFromHMD;
            
            buttonObject.transform.position = newPosition;
            buttonObject.transform.rotation = Quaternion.LookRotation(-forwardProjected, Vector3.up);
        }
    }

    private void LoadAndStartTrace()
    {
        Debug.Log("Load and Start Trace button pressed");
        recorder.LoadRecording();
        tracingSystem.StartTracing();
    }
}
