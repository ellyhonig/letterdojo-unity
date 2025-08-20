using UnityEngine;
using System.Collections.Generic;
using TMPro;

public class ButtonManager : MonoBehaviour
{
    [SerializeField] private GameObject buttonPrefab;
    [SerializeField] private Vector2 buttonSize = new Vector2(0.05f, 0.05f);
    [SerializeField] private Vector2 gridSize = new Vector2(3, 3);
    [SerializeField] private Vector2 spacing = new Vector2(0.01f, 0.01f);

    // Automatically get these components
    private SimpleRecorder recorder;
    private CanvasManager canvasManager;
    private LetterTracingSystem tracingSystem;
    private DictationManager dictationManager;
    private LevelManager levelManager;

    [SerializeField] private float distanceFromHMD = 0.5f;
    [SerializeField] private Vector3 offsetFromHMD = new Vector3(1f, -0.2f, 1f); // Offset downwards slightly
    private bool loaded = false;
    private Dictionary<string, System.Action> buttonActions;
    private GameObject buttonParent;
    public bool hideDevButtons = false;

    void Start()
    {
        // Automatically get the components attached to the same GameObject
        canvasManager = GetComponent<CanvasManager>();
        levelManager = GetComponent<LevelManager>();
        recorder = GetComponent<SimpleRecorder>();
        tracingSystem = GetComponent<LetterTracingSystem>();
        dictationManager = GetComponent<DictationManager>();

        InitializeButtonActions();
        CreateButtonGrid();
        UpdateButtonParentPosition();
    }

    private void InitializeButtonActions()
    {
        if (!hideDevButtons)
    {
        buttonActions = new Dictionary<string, System.Action>
        {
            {"Start Recording", () => recorder.IsRecording = true},
            {"Stop Recording", () => recorder.IsRecording = false},
            {"Save Recording", () => recorder.SaveRecording()},
            {"Reposition Canvas", canvasManager.UpdateCanvas},
            {"Load Recording", () => recorder.LoadRecording()},
            {"Start Tracing", tracingSystem.StartTracing},
            {"Next Level", () => levelManager.SetLevel(levelManager.currentLevel + 1)},
            {"Start All", levelManager.Restart},
            {"Previous Level", () => { if (levelManager.currentLevel > 0) levelManager.SetLevel(levelManager.currentLevel - 1); }},
            {"Clear", canvasManager.ClearVisualization},
            {"Start Dictation", dictationManager.StartDictation}
        };
    }
        else
        {
            buttonActions = new Dictionary<string, System.Action>
            {
                

            };
        }
    }

    private void CreateButtonGrid()
    {
        buttonParent = new GameObject("ButtonParent");

        float startX = -(gridSize.x - 1) * (buttonSize.x + spacing.x) / 2;
        float startY = (gridSize.y - 1) * (buttonSize.y + spacing.y) / 2;

        int buttonIndex = 0;
        foreach (var buttonAction in buttonActions)
        {
            if (buttonIndex >= gridSize.x * gridSize.y) break;

            int row = buttonIndex / (int)gridSize.x;
            int col = buttonIndex % (int)gridSize.x;

            Vector3 localPosition = new Vector3(
                startX + col * (buttonSize.x + spacing.x),
                startY - row * (buttonSize.y + spacing.y),
                0
            );

            CreateButton(buttonAction.Key, localPosition, buttonParent.transform);
            buttonIndex++;
        }
    }

    private void CreateButton(string buttonName, Vector3 localPosition, Transform parent)
    {
        GameObject buttonObject = Instantiate(buttonPrefab, parent);
        buttonObject.name = buttonName;
        buttonObject.transform.localPosition = localPosition;
        buttonObject.transform.localScale = new Vector3(buttonSize.x, buttonSize.y, buttonSize.x);

        ProximityButton proximityButton = buttonObject.AddComponent<ProximityButton>();
        proximityButton.Initialize(buttonName, buttonActions[buttonName], recorder);

        TextMeshPro textMesh = buttonObject.GetComponentInChildren<TextMeshPro>();
        if (textMesh != null)
        {
            textMesh.text = buttonName;
            textMesh.color = Color.black;
            textMesh.fontSize = 0.2f;
            textMesh.alignment = TextAlignmentOptions.Center;

            // Move the text slightly forward
            textMesh.transform.localPosition = new Vector3(0, 0, 0.9f);  // Adjust this value as needed
            textMesh.transform.localScale = Vector3.one * (1f / buttonSize.x);
            textMesh.transform.localRotation = Quaternion.Euler(0, 180, 0);

            Debug.Log($"TextMeshPro for button {buttonName} - Text: {textMesh.text}, Color: {textMesh.color}, FontSize: {textMesh.fontSize}, Position: {textMesh.transform.localPosition}, Scale: {textMesh.transform.localScale}");
        }
        else
        {
            Debug.LogError($"TextMeshPro component not found on button: {buttonName}");
        }

        Debug.Log($"Button created: {buttonName} at position {buttonObject.transform.position}");
    }

    public bool isLookingUp()
    {
        return Vector3.Dot(recorder.playerToRecord.hmd.transform.forward, Vector3.up) > .8f;
    }
    public bool isHmdFarAway()
    {
        return Vector3.Distance(recorder.playerToRecord.hmd.transform.position, buttonParent.transform.position) > 1.5f;
    }

    private void Update()
    {
        // Handle keyboard input for Next Level
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (buttonActions.ContainsKey("Next Level"))
            {
                buttonActions["Next Level"]();
            }
        }

        if (isLookingUp())
        {
          
        }
        if (isHmdFarAway())
        {
                }
    }

    private void UpdateButtonParentPosition()
    {
        if (recorder.playerToRecord != null && recorder.playerToRecord.hmd != null)
        {
            Transform hmdTransform = recorder.playerToRecord.hmd.transform;

            // Get the HMD's forward vector
            Vector3 hmdForward = hmdTransform.forward;

            // Project the forward vector onto the XZ plane
            Vector3 forwardProjected = Vector3.ProjectOnPlane(hmdForward, Vector3.up).normalized;

            // Calculate the new position
            Vector3 newPosition = hmdTransform.position + forwardProjected * distanceFromHMD;

            // Keep the Y position the same as the HMD
            newPosition.y = hmdTransform.position.y;

            // Apply the offset
            newPosition += offsetFromHMD;

            // Set the position and rotation of the button parent
            buttonParent.transform.position = newPosition;
            buttonParent.transform.rotation = Quaternion.LookRotation(-forwardProjected, Vector3.up);
        }
    }

    private void ClearRecording()
    {
        recorder.currentRecord = new SimpleRecord();
        Debug.Log("Recording cleared");
    }
}
