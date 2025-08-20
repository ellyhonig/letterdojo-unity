using UnityEngine;
using System.Collections;
using System;

public class ObjectOfInterestManager : MonoBehaviour
{
    public enum ObjectState
    {
        Locked,
        Unlocked,
        Held,
        Thrown,
        Collected
    }

    public GameObject objectOfInterest;
    public GameObject containerObject;
    public float maxDistance = 2f;
    public float proximityThreshold = 0.1f;
    public float throwThreshold = 5f;
    public float throwDuration = 0.4f;
    public float resetDelay = 3f;
    public float hmdProximityThreshold = 0.3f;
    public GameObject spherePrefab;
    public int sphereCount = 3;
    public float sphereEjectionForce = 5f;
    public float sphereEjectionOffset = 0.2f;
    public float containerProximityThreshold = 0.5f;
    public float initialSpinForce = 2f;
    public float positionSpringForce = 1000f;
    public float positionSpringDamper = 10f;

    public event Action<ObjectState> OnStateChanged;
    public event Action OnHMDProximity;
    public event Action OnObjectCollected;

    private LetterTracingSystem letterTracingSystem;
    private CanvasManager canvasManager;
    private SimpleRecorder recorder;
    private Vector3 initialPosition;
    private ObjectState currentState = ObjectState.Locked;
    private Rigidbody rb;
    private Vector3 handVelocity;
    private Vector3 lastHandPosition;
    private float handTrackTimer;

    private void Start()
    {
        canvasManager = GetComponent<CanvasManager>();
        letterTracingSystem = GetComponent<LetterTracingSystem>();
        recorder = GetComponent<SimpleRecorder>();

        if (letterTracingSystem == null || recorder == null)
        {
            Debug.LogError("Required components are missing!");
            return;
        }

        letterTracingSystem.OnTraceCompleted += UnlockObject;
        recorder.OnRecordingLoaded += SetInitialPosition;
        canvasManager.OnCanvasRepositioned += SetInitialPosition;

        if (objectOfInterest == null)
        {
            CreateDefaultObject();
        }

        if (spherePrefab == null)
        {
            CreateDefaultSpherePrefab();
        }

        if (containerObject == null)
        {
            CreateDefaultContainerObject();
        }

        SetInitialPosition();
        AddRigidbody();
        SetState(ObjectState.Locked);
        ApplyInitialSpin();
        letterTracingSystem.OnTraceCompleted += UnlockObject;
        recorder.OnRecordingLoaded += OnRecordingLoaded;
        canvasManager.OnCanvasRepositioned += SetInitialPosition;
    }
        private void CreateDefaultObject()
    {
        objectOfInterest = GameObject.CreatePrimitive(PrimitiveType.Cube);
        objectOfInterest.transform.localScale = Vector3.one * 0.1f;
        objectOfInterest.name = "ObjectOfInterest";
    }

    private void CreateDefaultSpherePrefab()
    {
        spherePrefab = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        spherePrefab.transform.localScale = Vector3.one * 0.05f;
        spherePrefab.name = "SpherePrefab";
        spherePrefab.SetActive(false);
    }

    private void CreateDefaultContainerObject()
    {
        containerObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        containerObject.transform.localScale = Vector3.one;
        containerObject.name = "ContainerObject";
        containerObject.transform.position = recorder.playerToRecord.hmd.transform.position + Vector3.forward;
    }
    public void StartObjectPlacing()
    {
        // Reset or initialize the object for placing
        ResetToInitialPosition();
        SetState(ObjectState.Unlocked);
    }
    private void SetInitialPosition()
{
    if (canvasManager == null)
    {
        Debug.LogError("CanvasManager is null!");
        return;
    }

    var spheres = canvasManager.activeSpheres;

    if (spheres != null && spheres.Count > 0)
    {
        // 👉 always grab the *actual* last sphere, not the last-letter index
        int lastIndex   = spheres.Count - 1;
        GameObject last = spheres[lastIndex];

        if (last != null)
        {
            initialPosition               = last.transform.position - Vector3.up * 0.1f;
            objectOfInterest.transform.position = initialPosition;
            Debug.Log($"Initial pos set from sphere {lastIndex}: {initialPosition}");
        }
        else
        {
            SetDefaultPosition();
        }
    }
    else
    {
        SetDefaultPosition();
    }
}


    private void SetDefaultPosition()
    {
        Debug.LogWarning("No spheres found or last sphere is null. Using current position as default.");
        initialPosition = objectOfInterest.transform.position;
    }

    private void AddRigidbody()
    {
        if (rb == null)
            rb = objectOfInterest.AddComponent<Rigidbody>();
        rb.useGravity = false;
        rb.isKinematic = false;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.drag = 0.5f;
        rb.angularDrag = 0.5f;
    }

    private void ApplyInitialSpin()
    {
        Vector3 randomAxis = UnityEngine.Random.onUnitSphere;
        rb.AddTorque(randomAxis * initialSpinForce, ForceMode.VelocityChange);
    }
    private void OnRecordingLoaded()
    {
        Debug.Log("Recording loaded. Resetting object state.");
        SetInitialPosition();
        SetState(ObjectState.Locked);
    }
    private void SetState(ObjectState newState)
    {
        if (currentState != newState)
        {
            Debug.Log($"State changing from {currentState} to {newState}");
            currentState = newState;
            OnStateChanged?.Invoke(currentState);

            switch (currentState)
            {
                case ObjectState.Locked:
                    SetLocked();
                    break;
                case ObjectState.Unlocked:
                    SetUnlocked();
                    break;
                case ObjectState.Held:
                    SetHeld();
                    break;
                case ObjectState.Thrown:
                    SetThrown();
                    break;
                case ObjectState.Collected:
                    SetCollected();
                    break;
            }
        }
    }

    private void SetLocked()
    {
        rb.isKinematic = true;
        rb.constraints = RigidbodyConstraints.FreezeAll;
    }

    private void SetUnlocked()
    {
        rb.isKinematic = false;
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.None;
    }

    private void SetHeld()
    {
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.None;
    }

    private void SetThrown()
    {
        rb.useGravity = true;
        rb.constraints = RigidbodyConstraints.None;
        rb.velocity = handVelocity;
        StartCoroutine(ResetAfterDelay());
    }

    private void SetCollected()
    {
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.None;
        objectOfInterest.transform.position = containerObject.transform.position;
        objectOfInterest.transform.SetParent(containerObject.transform);
    }

    private void FixedUpdate()
    {
        if (currentState == ObjectState.Unlocked)
        {
            Vector3 positionError = initialPosition - rb.position;
            Vector3 velocityError = -rb.velocity;
            rb.AddForce(positionError * positionSpringForce + velocityError * positionSpringDamper);
        }
        else if (currentState == ObjectState.Held)
        {
            UpdateHandPosition();
            UpdateHandVelocity();
        }
    }

    private void UpdateHandPosition()
    {
        Vector3 rightHandPos = recorder.playerToRecord.righthand.transform.position;
        Vector3 leftHandPos = recorder.playerToRecord.lefthand.transform.position;
        Vector3 closestHand = Vector3.Distance(objectOfInterest.transform.position, rightHandPos) <= Vector3.Distance(objectOfInterest.transform.position, leftHandPos) ? rightHandPos : leftHandPos;

        rb.MovePosition(closestHand);
    }

    private void UpdateHandVelocity()
    {
        Vector3 rightHandPos = recorder.playerToRecord.righthand.transform.position;
        Vector3 leftHandPos = recorder.playerToRecord.lefthand.transform.position;
        Vector3 closestHand = Vector3.Distance(objectOfInterest.transform.position, rightHandPos) <= Vector3.Distance(objectOfInterest.transform.position, leftHandPos) ? rightHandPos : leftHandPos;

        handVelocity = (closestHand - lastHandPosition) / Time.fixedDeltaTime;
        lastHandPosition = closestHand;
    }

    private void Update()
    {
        switch (currentState)
        {
            case ObjectState.Unlocked:
                CheckProximity();
                break;
            case ObjectState.Held:
                CheckThrow();
                CheckContainerProximity();
                break;
        }

        CheckHMDProximity();
    }

    private void CheckProximity()
    {
        if (currentState != ObjectState.Unlocked) return;

        Vector3 rightHandPos = recorder.playerToRecord.righthand.transform.position;
        Vector3 leftHandPos = recorder.playerToRecord.lefthand.transform.position;

        if (Vector3.Distance(objectOfInterest.transform.position, rightHandPos) <= proximityThreshold ||
            Vector3.Distance(objectOfInterest.transform.position, leftHandPos) <= proximityThreshold)
        {
            SetState(ObjectState.Held);
            lastHandPosition = Vector3.Distance(objectOfInterest.transform.position, rightHandPos) <= proximityThreshold ? rightHandPos : leftHandPos;
        }
    }

    private void CheckThrow()
    {
        if (handVelocity.magnitude >= throwThreshold)
        {
            handTrackTimer += Time.deltaTime;
            if (handTrackTimer >= throwDuration)
            {
                SetState(ObjectState.Thrown);
                handTrackTimer = 0f;
            }
        }
        else
        {
            handTrackTimer = 0f;
        }
    }

    private void CheckContainerProximity()
    {
        if (Vector3.Distance(objectOfInterest.transform.position, containerObject.transform.position) <= containerProximityThreshold)
        {
            CollectObject();
        }
    }

    private void CollectObject()
    {
        SetState(ObjectState.Collected);
        OnObjectCollected?.Invoke();
    }

    private void CheckHMDProximity()
    {
        Vector3 hmdPosition = recorder.playerToRecord.hmd.transform.position;
        if (Vector3.Distance(objectOfInterest.transform.position, hmdPosition) <= hmdProximityThreshold)
        {
            OnHMDProximity?.Invoke();
            EjectSpheres();
            ResetToInitialPosition();
            CollectObject();
        }
    }

    private void ResetToInitialPosition()
    {
        objectOfInterest.transform.position = initialPosition;
        objectOfInterest.transform.SetParent(null);
        SetState(ObjectState.Unlocked);
    }

    private void EjectSpheres()
    {
        Vector3 hmdForward = recorder.playerToRecord.hmd.transform.forward;
        Vector3 hmdPosition = recorder.playerToRecord.hmd.transform.position;
        Vector3 hmdUp = recorder.playerToRecord.hmd.transform.up;

        Vector3 ejectionStartPosition = hmdPosition + hmdForward * 0.2f - hmdUp * sphereEjectionOffset;

        for (int i = 0; i < sphereCount; i++)
        {
            GameObject sphere = Instantiate(spherePrefab, ejectionStartPosition, Quaternion.identity);
            sphere.SetActive(true);

            Rigidbody sphereRb = sphere.AddComponent<Rigidbody>();
            
            Vector3 ejectionDirection = Quaternion.Euler(
                UnityEngine.Random.Range(-15f, -45f),
                UnityEngine.Random.Range(-30f, 30f),
                0
            ) * hmdForward;

            sphereRb.AddForce(ejectionDirection * sphereEjectionForce, ForceMode.Impulse);

            StartCoroutine(DestroySphereAfterDelay(sphere, 2f));
        }
    }

    private IEnumerator DestroySphereAfterDelay(GameObject sphere, float delay)
    {
        yield return new WaitForSeconds(delay);
        Destroy(sphere);
    }

    private IEnumerator ResetAfterDelay()
    {
        yield return new WaitForSeconds(resetDelay);
        ResetToInitialPosition();
    }

    private void UnlockObject()
    {
        if (currentState == ObjectState.Locked)
        {
            SetState(ObjectState.Unlocked);
        }
    }

    private void OnDestroy()
    {
        if (letterTracingSystem != null)
        {
            letterTracingSystem.OnTraceCompleted -= UnlockObject;
        }
        if (recorder != null)
        {
            recorder.OnRecordingLoaded -= OnRecordingLoaded;
        }
        if (canvasManager != null)
        {
            canvasManager.OnCanvasRepositioned -= SetInitialPosition;
        }
    }
}