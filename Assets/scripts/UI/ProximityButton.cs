using UnityEngine;
using System;

public class ProximityButton : MonoBehaviour
{
    public enum ButtonState
    {
        Idle,
        Pressed
    }

    public ButtonState CurrentState { get; private set; }

    public event Action OnButtonPressed;
    public event Action OnButtonReleased;
    public event Action OnButtonHeld;

    [SerializeField] private float proximityThreshold = 0.05f;
    [SerializeField] private float holdThreshold = 0.5f;
    [SerializeField] private SimpleRecorder recorder;

    private string buttonName;
    private Action buttonAction;
    private float holdTimer;

    private Renderer buttonRenderer;
    private Color idleColor = Color.white;
    private Color pressedColor = Color.green;

    public void Initialize(string name, Action action, SimpleRecorder rec)
    {
        buttonName = name;
        buttonAction = action;
        recorder = rec;
        CurrentState = ButtonState.Idle;
    }

    private void Start()
    {
        buttonRenderer = GetComponent<Renderer>();
        if (buttonRenderer != null)
        {
            buttonRenderer.material.color = idleColor;
        }
    }

    private void Update()
    {
        CheckProximity();
        UpdateVisuals();
    }

    private void CheckProximity()
    {
        if (recorder == null || recorder.playerToRecord == null)
        {
            Debug.LogError("Recorder or player not set!");
            return;
        }

        Vector3 rightHandPosition = recorder.playerToRecord.righthand.transform.position;
        Vector3 leftHandPosition = recorder.playerToRecord.lefthand.transform.position;

        float rightDistance = Vector3.Distance(transform.position, rightHandPosition);
        float leftDistance = Vector3.Distance(transform.position, leftHandPosition);

        float closestDistance = Mathf.Min(rightDistance, leftDistance);

        switch (CurrentState)
        {
            case ButtonState.Idle:
                if (closestDistance <= proximityThreshold)
                {
                    CurrentState = ButtonState.Pressed;
                    OnButtonPressed?.Invoke();
                    buttonAction?.Invoke();
                    holdTimer = 0f;
                }
                break;
            case ButtonState.Pressed:
                if (closestDistance > proximityThreshold)
                {
                    CurrentState = ButtonState.Idle;
                    OnButtonReleased?.Invoke();
                }
                else
                {
                    holdTimer += Time.deltaTime;
                    if (holdTimer >= holdThreshold)
                    {
                        OnButtonHeld?.Invoke();
                    }
                }
                break;
        }
    }

    private void UpdateVisuals()
    {
        if (buttonRenderer != null)
        {
            buttonRenderer.material.color = (CurrentState == ButtonState.Pressed) ? pressedColor : idleColor;
        }
    }

    /// <summary>
    /// Resets the button's color based on its current state (Idle or Pressed).
    /// </summary>
    public void ResetColor()
    {
        if (buttonRenderer != null)
        {
            buttonRenderer.material.color = (CurrentState == ButtonState.Pressed) ? pressedColor : idleColor;
        }
    }
}
