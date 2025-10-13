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
    [SerializeField] private simplePlayer playerToCheck;

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

    // Overload: initialize with simplePlayer instead of SimpleRecorder
    public void Initialize(string name, Action action, simplePlayer player)
    {
        buttonName = name;
        buttonAction = action;
        playerToCheck = player;
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
        GameObject rightHand = null;
        GameObject leftHand = null;

        if (recorder != null && recorder.playerToRecord != null)
        {
            rightHand = recorder.playerToRecord.righthand;
            leftHand = recorder.playerToRecord.lefthand;
        }
        else if (playerToCheck != null)
        {
            rightHand = playerToCheck.righthand;
            leftHand = playerToCheck.lefthand;
        }
        else
        {
            // try to auto-find simplePlayer once
            var sp = FindObjectOfType<simplePlayer>();
            if (sp != null)
            {
                rightHand = sp.righthand;
                leftHand = sp.lefthand;
            }
        }

        if (rightHand == null || leftHand == null)
        {
            Debug.LogError("ProximityButton: no player hands found (need SimpleRecorder or simplePlayer)");
            return;
        }

        Vector3 rightHandPosition = rightHand.transform.position;
        Vector3 leftHandPosition = leftHand.transform.position;

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
