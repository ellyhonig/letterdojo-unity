using System.Collections;
using UnityEngine;

[DisallowMultipleComponent]
public class ProximityButtonBounce : MonoBehaviour
{
    [Header("Source Button")]
    [SerializeField] private ProximityButton proximityButton;

    [Header("Target To Scale")]
    [SerializeField] private Transform target; // Defaults to a best visual child or this.transform
    [SerializeField] private bool autoPickVisualChild = true;

    [Header("Bounce Settings")] 
    [SerializeField, Range(0.5f, 1f)] private float downScale = 0.9f; // scale down factor
    [SerializeField, Range(1f, 1.5f)] private float overshootScale = 1.08f; // overshoot factor
    [SerializeField, Min(0f)] private float downDuration = 0.06f; // quick press in
    [SerializeField, Min(0f)] private float upDuration = 0.10f;   // up to overshoot
    [SerializeField, Min(0f)] private float settleDuration = 0.10f; // settle back to 1
    [SerializeField] private bool useUnscaledTime = false;

    private Vector3 baseScale;
    private Coroutine bounceRoutine;

    private void Reset()
    {
        target = null; // allow Awake to auto-pick best visual
        proximityButton = GetComponent<ProximityButton>();
    }

    private void Awake()
    {
        if (target == null)
        {
            target = autoPickVisualChild ? PickBestVisualTarget() : transform;
        }
        baseScale = target.localScale;
    }

    private void OnEnable()
    {
        // Auto-wire if not assigned
        if (proximityButton == null)
            proximityButton = GetComponent<ProximityButton>();

        if (proximityButton != null)
            proximityButton.OnButtonPressed += HandlePressed;
    }

    private Transform PickBestVisualTarget()
    {
        // Strategy:
        // - Prefer a child with a Renderer over the root transparent box, by choosing the smallest renderer bounds.
        // - If no children renderers exist, fall back to self.
        var renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers == null || renderers.Length == 0)
            return transform;

        Renderer best = null;
        float bestVol = float.PositiveInfinity;
        foreach (var r in renderers)
        {
            if (!r || !r.enabled) continue;
            var size = r.bounds.size;
            float vol = Mathf.Max(0.000001f, size.x * size.y * size.z);
            // Prefer children over the root when volumes are similar
            bool isChild = r.transform != transform;
            float bias = isChild ? 0.5f : 1.0f; // child gets lower effective volume
            float score = vol * bias;
            if (score < bestVol)
            {
                bestVol = score;
                best = r;
            }
        }
        return best ? best.transform : transform;
    }

    private void OnDisable()
    {
        if (proximityButton != null)
            proximityButton.OnButtonPressed -= HandlePressed;
    }

    public void SetButton(ProximityButton button)
    {
        if (proximityButton == button) return;
        if (proximityButton != null)
            proximityButton.OnButtonPressed -= HandlePressed;
        proximityButton = button;
        if (isActiveAndEnabled && proximityButton != null)
            proximityButton.OnButtonPressed += HandlePressed;
    }

    public void Trigger()
    {
        HandlePressed();
    }

    private void HandlePressed()
    {
        // Restart the bounce, using the current scale as the start point for responsiveness
        if (bounceRoutine != null)
        {
            StopCoroutine(bounceRoutine);
            bounceRoutine = null;
        }
        bounceRoutine = StartCoroutine(BounceSequence());
    }

    private IEnumerator BounceSequence()
    {
        // Use the current scale as the "normal" baseline so the bounce adapts
        // to any runtime changes in scale from other systems.
        Vector3 baseline = target.localScale;
        Vector3 start = baseline;
        Vector3 down = baseline * downScale;
        Vector3 over = baseline * overshootScale;

        // Phase 1: scale down quickly
        yield return TweenScale(start, down, downDuration, EaseOutCubic);

        // Phase 2: scale up past base (overshoot)
        start = target.localScale;
        yield return TweenScale(start, over, upDuration, EaseOutCubic);

        // Phase 3: settle back to baseline
        start = target.localScale;
        yield return TweenScale(start, baseline, settleDuration, EaseInOutCubic);

        target.localScale = baseline;
        bounceRoutine = null;
    }

    private IEnumerator TweenScale(Vector3 from, Vector3 to, float duration, System.Func<float, float> ease)
    {
        if (duration <= 0f)
        {
            target.localScale = to;
            yield break;
        }

        float t = 0f;
        while (t < 1f)
        {
            float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            t += dt / duration;
            float k = Mathf.Clamp01(t);
            float e = ease != null ? ease(k) : k;
            target.localScale = Vector3.LerpUnclamped(from, to, e);
            yield return null;
        }
        target.localScale = to;
    }

    // Easing helpers
    private static float EaseOutCubic(float x)
    {
        // decelerating to zero velocity
        return 1f - Mathf.Pow(1f - x, 3f);
    }

    private static float EaseInOutCubic(float x)
    {
        return x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
    }
}
